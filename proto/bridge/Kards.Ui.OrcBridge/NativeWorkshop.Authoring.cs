using Orc.Packages;
using Orc.Packages.Editing;
using Orc.Packages.Fs;

namespace Kards.Ui.OrcBridge;

public sealed record NativeInterruptedDraft(string Id, string Name, string Root, bool IsNew);

public sealed partial class NativeWorkshop
{
    private string AuthoringReferencesRoot => Path.Combine(_root, "authoring-references");
    private string RecoveryRoot => Path.Combine(_root, "recovery");
    private string? _recoveryDestination, _recoveryCanonical;
    public bool RecoveredDraft { get; private set; }
    public IReadOnlyList<string> SessionWarnings => _editor?.Session?.Warnings ?? [];

    private AssetEditor CreateEditor(AssetRegistry registry)
    {
        Directory.CreateDirectory(AuthoringReferencesRoot);
        // All writable/readable roots are user-owned copies. Never grant editor policy access to shipped files.
        Directory.CreateDirectory(RecoveryRoot);
        return new AssetEditor(registry, new EditableRootPolicy([InstalledRoot, AuthoringReferencesRoot, RecoveryRoot]),
            new AssetEditorOptions { PackageRoots = [InstalledRoot, AuthoringReferencesRoot] });
    }

    public void Suspend()
    {
        if (!HasSession) return;
        CheckpointRecovery(); _editor = null; _recoveryDestination = _recoveryCanonical = null; RecoveredDraft = false;
    }

    private void Resume(NativeInterruptedDraft draft)
    {
        // Open a copy through the engine's public edit API. The interrupted directory and
        // formal package are never used as writable recovery baselines.
        var backup = Path.Combine(_root, "backups", Guid.NewGuid().ToString("N") + "-interrupted");
        WorkshopPackageArchive.Copy(draft.Root, backup);
        try
        {
            _ = Inspect(backup, out _);
            var baseline = Path.Combine(RecoveryRoot, Guid.NewGuid().ToString("N"));
            WorkshopPackageArchive.Copy(backup, baseline);
            _editor = CreateEditor(ValidateSet(EnabledRoots()));
            _editor.BeginSession(EditorSessionRequest.Edit(baseline));
            _recoveryDestination = draft.Root[..^4]; _recoveryCanonical = draft.Root; RecoveredDraft = true;
        }
        catch
        {
            _editor = null;
            throw new InvalidDataException("上次的草稿暂时无法恢复，原稿和完整备份均已保留。可在卡包管理中导出草稿进行修复。");
        }
    }

    private void CheckpointRecovery()
    {
        if (_recoveryCanonical is null || !HasSession) return;
        var staged = Path.Combine(_root, "staging", Guid.NewGuid().ToString("N"));
        WorkshopPackageArchive.Copy(Editor.Session!.TemporaryRoot, staged);
        var previous = Path.Combine(_root, "backups", Guid.NewGuid().ToString("N") + "-checkpoint");
        if (Directory.Exists(_recoveryCanonical)) Directory.Move(_recoveryCanonical, previous);
        try { Directory.Move(staged, _recoveryCanonical); }
        catch { if (Directory.Exists(previous)) Directory.Move(previous, _recoveryCanonical); throw; }
    }

    private void ArchiveRecovery()
    {
        if (_recoveryCanonical is not null && Directory.Exists(_recoveryCanonical))
            Directory.Move(_recoveryCanonical, Path.Combine(_root, "backups", Guid.NewGuid().ToString("N") + "-recovered"));
        _recoveryDestination = _recoveryCanonical = null; RecoveredDraft = false;
    }

    private void PrepareAuthoringReference(string id)
    {
        var source = Packages().SingleOrDefault(p => p.Id == id && p.Enabled)
            ?? throw new InvalidDataException("期望引用的卡包未启用：" + id);
        if (source.Installed) return;
        Directory.CreateDirectory(AuthoringReferencesRoot);
        var reference = Path.Combine(AuthoringReferencesRoot, WorkshopPackageArchive.Key(id));
        if (Directory.Exists(reference) && WorkshopPackageArchive.Fingerprint(reference) == WorkshopPackageArchive.Fingerprint(source.Root)) return;
        var staging = Path.Combine(_root, "staging", Guid.NewGuid().ToString("N"));
        WorkshopPackageArchive.Copy(source.Root, staging);
        if (Directory.Exists(reference)) Directory.Move(reference, Path.Combine(_root, "backups", Guid.NewGuid().ToString("N") + "-reference"));
        Directory.Move(staging, reference);
    }

    public IReadOnlyList<NativeWorkshopAsset> DeleteImpact(NativeWorkshopAsset asset)
    {
        var kind = Enum.Parse<Orc.Core.AssetKind>(asset.Kind);
        var info = Editor.GetAsset(kind, asset.Id);
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(WorkshopPackageArchive.Child(Editor.Session!.TemporaryRoot, info.SidecarPath)));
        var metadata = document.RootElement.GetProperty("assets").EnumerateArray().Single(a =>
            a.GetProperty("id").GetString() == asset.Id && string.Equals(a.GetProperty("kind").GetString(), asset.Kind, StringComparison.OrdinalIgnoreCase));
        var isEmbedded = metadata.TryGetProperty("anchor", out var anchor) && anchor.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(anchor.GetString());
        return isEmbedded ? [asset] : Assets().Where(a => a.Path == info.SourcePath).ToArray();
    }

    public IReadOnlyList<NativeInterruptedDraft> InterruptedDrafts()
    {
        var found = new List<NativeInterruptedDraft>();
        foreach (var root in Directory.GetDirectories(InstalledRoot, "*.tmp"))
        {
            if (string.Equals(root, TemporaryRoot, StringComparison.OrdinalIgnoreCase) || string.Equals(root, _recoveryCanonical, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var manifest = Manifest(root);
                found.Add(new(manifest.Id, manifest.Name ?? manifest.Id, root, !Directory.Exists(root[..^4])));
            }
            catch (Exception ex) when (ex is IOException or FormatException or System.Text.Json.JsonException) { /* Incomplete folders are retained; never auto-delete. */ }
        }
        return found;
    }

    public void ExportInterruptedDraft(string root, string destination)
    {
        var draft = InterruptedDrafts().SingleOrDefault(d => d.Root == root) ?? throw new InvalidDataException("此中断草稿不在工坊目录中。");
        WorkshopPackageArchive.Export(draft.Root, destination);
    }

    public void DiscardStoredDraft(string id)
    {
        Idle(); if (HasSession) Suspend();
        var root = InterruptedDrafts().FirstOrDefault(d => d.Id == id)?.Root;
        if (root is null)
        {
            var pack = Packages().Single(p => p.Id == id);
            root = (pack.Installed ? pack.Root : Destination(id)) + ".tmp";
        }
        if (!Directory.Exists(root) || !Path.GetFullPath(root).StartsWith(InstalledRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("找不到这份工坊草稿。");
        Directory.Move(root, Path.Combine(_root, "backups", Guid.NewGuid().ToString("N") + "-discarded"));
    }
}
