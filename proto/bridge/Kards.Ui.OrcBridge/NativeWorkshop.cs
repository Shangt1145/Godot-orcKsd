using System.Text.Json;
using System.Text.Json.Nodes;
using Orc.Core;
using Orc.Packages;
using Orc.Packages.Editing;
using Orc.Packages.Fs;
using Orc.UI;

namespace Kards.Ui.OrcBridge;

public sealed record NativeWorkshopPack(string Id, string Name, string Root, string Version, bool Installed, bool Enabled, bool HasDraft, int Cards, IReadOnlyList<string> Diagnostics);
public sealed record NativeWorkshopAsset(string Kind, string Id, string AppId, string Version, string Path);
public sealed record NativeWorkshopImport(NativeWorkshopPack Pack, bool Unchanged);
public sealed class WorkshopPackageConflictException(string message) : InvalidOperationException(message);

/// <summary>Persistent transport and native editor facade; never compiles or decides game rules.</summary>
public sealed partial class NativeWorkshop
{
    private readonly string _root;
    private readonly string _shipped;
    private readonly Action<IReadOnlyList<string>>? _activate;
    private AssetEditor? _editor;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    private Dictionary<string, bool> _enabled = new(StringComparer.Ordinal);
    private string? _catalogError;
    public string InstalledRoot => Path.Combine(_root, "installed");
    public bool HasSession => _editor?.HasActiveSession == true;
    public string? SessionPackage => _editor?.Session?.PackageId;
    public string? TemporaryRoot => _editor?.Session?.TemporaryRoot;
    public NativeWorkshopPack WorkingPackage
    {
        get
        {
            var session = Editor.Session!; var manifest = Manifest(session.TemporaryRoot);
            return new(manifest.Id, manifest.Name ?? manifest.Id, session.PackageRoot, manifest.Version.ToString(), true, false, true, Assets().Count(a => a.Kind == "Card"), []);
        }
    }
    public IReadOnlyList<string> StartupDiagnostics { get; private set; } = [];

    public NativeWorkshop(string root, string shippedBase, Action<IReadOnlyList<string>>? activate = null)
    {
        _root = Path.GetFullPath(root); _shipped = Path.GetFullPath(shippedBase); _activate = activate;
        Directory.CreateDirectory(InstalledRoot);
        foreach (var name in new[] { "staging", "backups" }) Directory.CreateDirectory(Path.Combine(_root, name));
        var catalog = Path.Combine(_root, "catalog.json");
        if (File.Exists(catalog))
        {
            try { _enabled = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(catalog)) ?? throw new InvalidDataException("工坊安装目录索引为空。"); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            { _catalogError = "工坊安装目录索引损坏，已保留原文件并暂停写入：" + catalog; }
        }
    }

    private static PackageManifest Manifest(string root) => PackageManifest.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
    private string Destination(string id) => Path.Combine(InstalledRoot, WorkshopPackageArchive.Key(id));
    private static string[] Inspect(string root, out int cards)
    {
        RegisterNations(root);
        var manifest = Manifest(root);
        if (manifest.Kind != PackageKind.CardPack) throw new InvalidDataException("只能导入 cardpack 卡包，插件须由程序发行版安装。");
        // Scripts/resources are retained verbatim. Native scanner/registrar owns all source and sidecar checks.
        _ = WorkshopPackageArchive.Files(root).Count();
        var registered = PackageRegistrar.Register(root, manifest, new AssetRegistry());
        cards = registered.Content.Definitions.Count;
        return registered.Diagnostics.Select(d => $"{d.File}: {d.Message}")
            .Concat(registered.Assets.Where(a => a.Metadata.Kind == AssetKind.Card && a.Card is null).Select(a => a.AppId + "：未通过原生卡牌准入"))
            .Distinct().ToArray();
    }

    public IReadOnlyList<NativeWorkshopPack> Packages()
    {
        var found = new Dictionary<string, NativeWorkshopPack>(StringComparer.Ordinal);
        var errors = new List<string>();
        var shipped = PackageDiscovery.Discover(_shipped).Packages.Where(p => p.Manifest.Kind == PackageKind.CardPack).Select(p => p.RootDirectory);
        var installed = Directory.GetDirectories(InstalledRoot).Where(p => !p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        foreach (var root in shipped.Concat(installed))
        {
            try
            {
                var manifest = Manifest(root);
                var diagnostics = Inspect(root, out var count);
                var local = root.StartsWith(InstalledRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                found[manifest.Id] = new(manifest.Id, manifest.Name ?? manifest.Id, root, manifest.Version.ToString(), local,
                    !local || (_catalogError is null && _enabled.GetValueOrDefault(manifest.Id, true)), Directory.Exists(root + ".tmp"), count, diagnostics);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add(root + "：" + ex.Message); }
        }
        StartupDiagnostics = _catalogError is null ? errors : errors.Prepend(_catalogError).ToArray();
        return found.Values.OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<string> EnabledRoots() => Packages().Where(p => p.Enabled).Select(p => p.Root).ToArray();
    public void Activate() => _activate?.Invoke(EnabledRoots());
    public string ArtFor(string appId)
    {
        var package = OrcEngineHost.Snapshot().CardPacks.FirstOrDefault(p => appId.StartsWith("pack/" + p.Manifest.Id + "/", StringComparison.Ordinal));
        if (package is null) return "";
        return ArtAt(package.RootDirectory, package.Manifest.Id, appId);
    }
    public string PreviewArtFor(string appId) => ArtAt(Editor.Session!.TemporaryRoot, Editor.Session.PackageId, appId);
    private static string ArtAt(string root, string packageId, string appId)
    {
        var path = Path.Combine(root, "workshop.presentation.json");
        if (!File.Exists(path)) return "";
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var localId = appId[(packageId.Length + 6)..];
        if (!doc.RootElement.TryGetProperty("cards", out var cards) || !cards.TryGetProperty(localId, out var entry)
            || !entry.TryGetProperty("art", out var art) || art.GetString() is not { Length: > 0 } relative) return "";
        var full = WorkshopPackageArchive.Child(root, relative);
        if (!File.Exists(full)) return "";
        return (entry.TryGetProperty("artKind", out var kind) && kind.GetString() == "full" ? "packfull://" : "packart://") + full;
    }
    public void SetArtwork(string localId, byte[] png, bool fullCard)
    {
        if (png.Length > 32 * 1024 * 1024) throw new InvalidDataException("卡图最大 32 MiB。");
        var session = Editor.Session!;
        _ = Editor.GetAsset(AssetKind.Card, localId);
        var relative = "art/" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png)).ToLowerInvariant() + ".png";
        var file = WorkshopPackageArchive.Child(session.TemporaryRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllBytes(file, png);
        var presentationFile = Path.Combine(session.TemporaryRoot, "workshop.presentation.json");
        var presentation = File.Exists(presentationFile) ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(presentationFile))!.AsObject() : new System.Text.Json.Nodes.JsonObject { ["schemaVersion"] = 1 };
        var cards = presentation["cards"] as System.Text.Json.Nodes.JsonObject ?? new(); presentation["cards"] = cards;
        if (cards[localId] is not JsonObject card) { card = new(); cards[localId] = card; }
        card["art"] = relative; card["artKind"] = fullCard ? "full" : "illustration";
        SavePresentation(presentation);
    }
    private void Idle()
    {
        if (_catalogError is not null) throw new InvalidDataException(_catalogError);
        if (OrcEngineHost.Current?.CardPacksLocked == true) throw new InvalidOperationException("请先结束当前对局，再更改卡包。");
    }
    private void SaveCatalog()
    {
        var path = Path.Combine(_root, "catalog.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(_enabled, _json)); File.Move(path + ".tmp", path, true);
    }

    public NativeWorkshopImport Import(string archive, bool replace = false)
    {
        Idle();
        if (HasSession) throw new InvalidOperationException("请先转正或放弃当前编辑会话。");
        var staging = Path.Combine(_root, "staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var source = WorkshopPackageArchive.Extract(archive, staging);
            var manifest = Manifest(source);
            _ = Inspect(source, out _);
            var existing = Packages().FirstOrDefault(p => p.Id == manifest.Id);
            if (existing is not null)
            {
                if (Manifest(existing.Root).Guid != manifest.Guid) throw new InvalidDataException("相同卡包 ID 的稳定 GUID 不同，不能覆盖：" + manifest.Id);
                if (WorkshopPackageArchive.Fingerprint(existing.Root) == WorkshopPackageArchive.Fingerprint(source)) return new(existing, true);
                if (!replace) throw new WorkshopPackageConflictException($"已有卡包「{existing.Name}」（{existing.Version}）。导入版本 {manifest.Version} 将替换本机内容，旧内容会保留在 backups。");
                if (existing.HasDraft) throw new InvalidOperationException("此卡包有未转正草稿，请先处理草稿。");
            }
            var destination = existing?.Installed == true ? existing.Root : Destination(manifest.Id);
            var backup = Path.Combine(_root, "backups", Guid.NewGuid().ToString("N"));
            var previous = _enabled.GetValueOrDefault(manifest.Id, true);
            var hadSetting = _enabled.ContainsKey(manifest.Id);
            if (Directory.Exists(destination)) Directory.Move(destination, backup);
            try
            {
                Directory.Move(source, destination);
                _enabled[manifest.Id] = true;
                ValidateSet(EnabledRoots());
                Activate(); SaveCatalog();
            }
            catch
            {
                Directory.CreateDirectory(staging);
                if (Directory.Exists(destination)) Directory.Move(destination, Path.Combine(staging, "failed"));
                if (Directory.Exists(backup)) Directory.Move(backup, destination);
                if (hadSetting) _enabled[manifest.Id] = previous; else _enabled.Remove(manifest.Id);
                Activate(); throw;
            }
            return new(Packages().Single(p => p.Id == manifest.Id), false);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    // Registry and lock planner are rebuilt from durable sources, so no deleted .tmp paths survive promotion.
    private static AssetRegistry ValidateSet(IEnumerable<string> roots)
    {
        var registry = new AssetRegistry(); var locks = new List<AssetLock>();
        foreach (var root in roots)
        {
            var manifest = Manifest(root);
            PackageRegistrar.Register(root, manifest, registry);
            if (AssetLockJson.ReadPackage(root, AssetDomain.Formal) is { } formal) locks.Add(formal);
        }
        if (locks.Count > 0)
        {
            var plan = LoadListPlanner.Plan(registry, locks);
            if (!plan.IsValid) throw new InvalidDataException("原生资产 lock 存在冲突或循环依赖。");
        }
        return registry;
    }

    public void SetEnabled(string id, bool enabled)
    {
        Idle();
        if (HasSession) throw new InvalidOperationException("请先结束编辑会话。");
        var pack = Packages().Single(p => p.Id == id);
        if (!pack.Installed) throw new InvalidOperationException("自带卡包不能从安装管理中停用。");
        var before = _enabled.GetValueOrDefault(id, true); _enabled[id] = enabled;
        try { ValidateSet(EnabledRoots()); Activate(); SaveCatalog(); }
        catch { _enabled[id] = before; Activate(); throw; }
    }

    public void Begin(string id)
    {
        Idle();
        if (SessionPackage == id) return;
        if (HasSession) Suspend();
        if (InterruptedDrafts().FirstOrDefault(d => d.Id == id) is { } interrupted) { Resume(interrupted); return; }
        RecoveredDraft = false;
        var pack = Packages().Single(p => p.Id == id);
        var destination = pack.Installed ? pack.Root : Destination(id);
        if (Directory.Exists(destination + ".tmp"))
            throw new InvalidDataException("上次的草稿无法读取，原文件已保留。可在卡包管理中选择放弃这份草稿，再从已应用版本开始。");
        if (!pack.Installed)
        {
            if (!Directory.Exists(destination)) WorkshopPackageArchive.Copy(pack.Root, destination);
            _enabled[id] = true; SaveCatalog();
        }
        var registry = ValidateSet(EnabledRoots());
        _editor = CreateEditor(registry);
        _editor.BeginSession(EditorSessionRequest.Edit(destination));
    }

    public void Create(string id, string name, string dependencyId)
    {
        Idle();
        if (HasSession) throw new InvalidOperationException("请先结束当前编辑会话。");
        if (Packages().Any(p => p.Id == id)) throw new InvalidDataException("卡包 ID 已存在。");
        // Backend owns the package identity and numeric asset versions.
        PrepareAuthoringReference(dependencyId);
        _editor = CreateEditor(ValidateSet(EnabledRoots()));
        _editor.BeginSession(EditorSessionRequest.CreateNew(id, InstalledRoot, [dependencyId], name));
    }
    public NativeWorkshopAsset CreateCard(string id)
    {
        var asset = Editor.CreateAsset(AssetKind.Card, id);
        CheckpointRecovery();
        return new(asset.Kind.ToString(), asset.LocalId, asset.AppId, asset.Version.ToString(), asset.SourcePath);
    }
    public void DeleteAsset(NativeWorkshopAsset asset)
    {
        Editor.DeleteAsset(Enum.Parse<AssetKind>(asset.Kind), asset.Id);
        if (asset.Kind != "Card") { CheckpointRecovery(); return; }
        var presentation = Presentation(); (presentation["cards"] as JsonObject)?.Remove(asset.Id); SavePresentation(presentation);
        var path = Path.Combine(Editor.Session!.TemporaryRoot, "display-texts.json");
        if (File.Exists(path)) { var texts = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); texts.Remove(asset.Id); File.WriteAllText(path, texts.ToJsonString(_json)); }
        path = Path.Combine(Editor.Session.TemporaryRoot, "desktop-effects.json");
        if (File.Exists(path)) { var plans = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); plans.Remove(asset.Id); File.WriteAllText(path, plans.ToJsonString(_json)); }
        CheckpointRecovery();
    }

    public IReadOnlyList<NativeWorkshopAsset> Assets() => Editor.ListAssets().Select(a => new NativeWorkshopAsset(a.Kind.ToString(), a.LocalId, a.AppId, a.Version.ToString(), a.SourcePath)).ToArray();
    public IReadOnlyList<Orc.UI.Contracts.UiCardDefinition> PreviewCards()
    {
        var root = Editor.Session!.TemporaryRoot;
        var content = PackageRegistrar.Register(root, Manifest(root), new AssetRegistry()).Content;
        var texts = PackPresentationData.Texts(content);
        return content.Definitions.Select(entry => StaticCardProjection.Project(entry.Id, entry.Definition, id => ArtAt(root, Editor.Session!.PackageId, id),
              text: texts.GetValueOrDefault(entry.Id.StartsWith(content.Prefix, StringComparison.Ordinal) ? entry.Id[content.Prefix.Length..] : entry.Id)) with { Flavor = PackPresentationData.Flavor(root, entry.Id[content.Prefix.Length..]) }).ToArray();
    }
    private AssetEditor Editor => HasSession ? _editor! : throw new InvalidOperationException("请先进入原生卡包编辑。");
    public string Read(string sourcePath) => File.ReadAllText(WorkshopPackageArchive.Child(Editor.Session!.TemporaryRoot, sourcePath));
    public NativeWorkshopAsset Commit(string sourcePath, string json)
    {
        // Commit writes before parsing in the engine. Restore the previous file if parsing fails.
        var before = Read(sourcePath);
        try
        {
            var asset = Editor.Commit(sourcePath, json).Asset;
            CheckpointRecovery();
            return new(asset.Kind.ToString(), asset.LocalId, asset.AppId, asset.Version.ToString(), asset.SourcePath);
        }
        catch { File.WriteAllText(WorkshopPackageArchive.Child(Editor.Session!.TemporaryRoot, sourcePath), before); throw; }
    }

    public string PreviewLocks() => JsonSerializer.Serialize(Editor.PreviewLocks(), _json);
    public void Promote()
    {
        Idle();
        var session = Editor.Session!;
        _ = Inspect(session.TemporaryRoot, out _);
        var destination = _recoveryDestination ?? session.PackageRoot;
        var backup = Path.Combine(_root, "backups", Guid.NewGuid().ToString("N"));
        if (Directory.Exists(destination)) WorkshopPackageArchive.Copy(destination, backup);
        Editor.PreviewLocks();
        Editor.Promote();
        try
        {
            if (_recoveryDestination is not null)
            {
                var staged = Path.Combine(_root, "staging", Guid.NewGuid().ToString("N"));
                WorkshopPackageArchive.Copy(session.PackageRoot, staged);
                if (Directory.Exists(destination)) Directory.Move(destination, Path.Combine(_root, "backups", Guid.NewGuid().ToString("N") + "-formal"));
                Directory.Move(staged, destination);
            }
            // New packages use the engine-selected directory name, so discovery never assumes our transport key.
            ValidateSet(EnabledRoots()); Activate(); SaveCatalog();
            ArchiveRecovery();
        }
        catch
        {
            // Preserve failed promoted content for diagnosis, then restore the last formal package.
            if (Directory.Exists(destination)) Directory.Move(destination, Path.Combine(_root, "backups", Guid.NewGuid().ToString("N") + "-failed"));
            if (Directory.Exists(backup)) WorkshopPackageArchive.Copy(backup, destination); Activate(); throw;
        }
    }
    public void Discard() { Editor.EndSession(EditorSessionEndMode.Discard); ArchiveRecovery(); }
    public void Export(string id, string destination, bool draft = false)
    {
        var root = draft && SessionPackage == id ? Editor.Session!.TemporaryRoot : Packages().Single(p => p.Id == id).Root;
        WorkshopPackageArchive.Export(root, destination);
    }
    public string BackupInterruptedDraft(string id)
    {
        if (HasSession) throw new InvalidOperationException("当前会话仍在编辑，请使用保存草稿或放弃。");
        var pack = Packages().Single(p => p.Id == id);
        if (!pack.HasDraft) throw new InvalidOperationException("没有残留草稿。");
        var backup = Path.Combine(_root, "backups", Guid.NewGuid().ToString("N") + "-draft");
        Directory.Move(pack.Root + ".tmp", backup);
        return backup;
    }
}
