using System.Text.Json;
using System.Text.Json.Serialization;
using Kards.Ui.Contracts;

namespace Kards.Ui.OrcBridge;

public sealed record PackEntry(string Id, string Name, string Kind, bool Enabled, string Directory, string? BasePackId, string? LoadError);

/// <summary>
/// Local pack storage under one root: each imported pack lives in <c>&lt;root&gt;/&lt;packId&gt;/</c>,
/// the registry in <c>packs.json</c>. Import only validates the manifest; enabling runs the full
/// PackSystem pipeline (precheck, dependency resolution, keyword registration) and reports per-pack
/// load errors back to the UI instead of throwing past it.
/// </summary>
public sealed class PackLibrary
{
    private sealed record FileData(int Schema, List<PackRecord> Packs);
    private sealed record PackRecord(string Id, string Name, string Kind, bool Enabled, string? BasePackId);
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly string _root;
    private readonly string _path;
    private List<PackRecord> _records = [];
    private readonly Dictionary<string, string> _loadErrors = new(StringComparer.Ordinal);
    public string? LoadError { get; private set; }

    public PackLibrary(string root)
    {
        _root = root;
        _path = Path.Combine(root, "packs.json");
        Directory.CreateDirectory(root);
        if (!File.Exists(_path)) return;
        try
        {
            if (new FileInfo(_path).Length > 200_000) throw new InvalidDataException("卡包登记文件过大");
            var data = JsonSerializer.Deserialize<FileData>(File.ReadAllText(_path), Json);
            if (data is null || data.Schema != 1 || data.Packs is null || data.Packs.Count > 64)
                throw new InvalidDataException("卡包登记版本或内容无效");
            if (data.Packs.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != data.Packs.Count)
                throw new InvalidDataException("卡包编号重复");
            _records = data.Packs;
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        { LoadError = $"卡包登记读取失败，原文件保留：{e.Message}"; }
    }

    public IReadOnlyList<PackEntry> Entries =>
        [.. _records.Select(r => new PackEntry(r.Id, r.Name, r.Kind, r.Enabled,
            Path.Combine(_root, r.Id), r.BasePackId, _loadErrors.GetValueOrDefault(r.Id)))];

    /// <summary>Imports a pack directory (manifest-validated) into the root. The pack starts disabled.</summary>
    public PackEntry Import(string sourceDirectory)
    {
        var manifest = PackSystem.ReadManifest(sourceDirectory);
        if (_records.Any(r => r.Id == manifest.Id)) throw new InvalidDataException($"卡包 '{manifest.Id}' 已导入过");
        var target = Path.Combine(_root, manifest.Id);
        if (Directory.Exists(target)) throw new InvalidDataException($"目录已被占用：{target}");
        CopyDirectory(sourceDirectory, target);
        _records.Add(new PackRecord(manifest.Id, manifest.Name, manifest.Kind, Enabled: false,
            BasePackId: manifest.Kind == "expansion" ? manifest.DependsOn[0] : null));
        Mutate();
        return Entries.First(e => e.Id == manifest.Id);
    }

    public void Remove(string id)
    {
        var record = _records.FirstOrDefault(r => r.Id == id) ?? throw new InvalidDataException($"未知卡包：{id}");
        if (record.Enabled) throw new InvalidDataException("请先停用卡包再删除");
        var target = Path.Combine(_root, id);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        _records.Remove(record);
        Mutate();
    }

    /// <summary>Flips a pack's enabled flag and re-runs the whole enabled pipeline. Callers should
    /// read <see cref="Entries"/> and refresh the catalog afterwards (see Reload).</summary>
    public void SetEnabled(string id, bool enabled)
    {
        var index = _records.FindIndex(r => r.Id == id);
        if (index < 0) throw new InvalidDataException($"未知卡包：{id}");
        _records[index] = _records[index] with { Enabled = enabled };
        Reload();
    }

    /// <summary>
    /// Recomputes the enabled set from the records (base packs load before their expansions), runs
    /// the full PackSystem pipeline per pack and returns the winning projections for the catalog.
    /// A pack that fails to load is disabled with its error recorded — never half-enabled.
    /// </summary>
    public IReadOnlyList<UiCardDefinition> Reload()
    {
        _loadErrors.Clear();
        var enabledPacks = new List<PackContent>();
        foreach (var pass in new[] { "pack", "expansion" })
            foreach (var original in _records.Where(r => r.Enabled && r.Kind == pass).ToArray())
            {
                var record = original;
                try
                {
                    enabledPacks.Add(PackSystem.Load(Path.Combine(_root, record.Id), enabledPacks));
                }
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException
                    or ArgumentException or InvalidOperationException or JsonException)
                {
                    record = record with { Enabled = false };
                    var index = _records.FindIndex(r => r.Id == record.Id);
                    _records[index] = record;
                    _loadErrors[record.Id] = e.Message;
                }
            }
        Mutate();
        DiyCardCompiler.SetPackKeywords(enabledPacks.SelectMany(p => p.Manifest.Keywords)
            .ToDictionary(k => k.Id, k => k.Id, StringComparer.Ordinal));
        return enabledPacks.SelectMany(p => p.Projections).ToArray();
    }

    private void Mutate()
    {
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new FileData(1, _records), Json), new System.Text.UTF8Encoding(false));
        File.Move(temporary, _path, overwrite: true);
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(directory);
            if (name is "bin" or "obj" or ".git") continue;
            Directory.CreateDirectory(directory.Replace(source, target, StringComparison.Ordinal));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(p => p is "bin" or "obj" or ".git")) continue;
            File.Copy(file, file.Replace(source, target, StringComparison.Ordinal), overwrite: true);
        }
    }
}
