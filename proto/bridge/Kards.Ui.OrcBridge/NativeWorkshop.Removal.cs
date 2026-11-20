using Orc.Packages;

namespace Kards.Ui.OrcBridge;

public sealed partial class NativeWorkshop
{
    public static bool CardPacksLocked => OrcEngineHost.Current?.CardPacksLocked == true;

    public string? BasePackOf(string root, string id)
    {
        try { return WorkshopPackagePresentation.Parent(root, id); }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException) { return null; }
    }

    public string RemovePackage(string id)
    {
        Idle();
        if (HasSession) Suspend();
        if (PackageDiscovery.Discover(_shipped).Packages.Any(p => p.Manifest.Id == id))
            throw new InvalidOperationException("自带卡包不能删除。");

        var packages = Packages();
        var drafts = InterruptedDrafts();
        var installed = packages.SingleOrDefault(p => p.Id == id && p.Installed);
        var draft = drafts.SingleOrDefault(p => p.Id == id);
        if (installed is null && draft is null) throw new InvalidDataException("找不到要删除的卡包。");
        var dependents = packages.Select(p => (p.Id, p.Name, p.Root))
            .Concat(drafts.Select(p => (p.Id, p.Name, p.Root)))
            .Where(p => p.Id != id && DependsOn(p.Root, p.Id, id))
            .Select(p => p.Name).Distinct().ToArray();
        if (dependents.Length > 0)
            throw new InvalidOperationException("以下卡包或草稿仍依赖此包，请先处理它们：" + string.Join("、", dependents));

        var paths = new[] { installed?.Root, draft?.Root ?? (installed?.HasDraft == true ? installed.Root + ".tmp" : null) }
            .OfType<string>().ToArray();
        if (paths.Any(path => !string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), InstalledRoot, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("卡包不在工坊安装目录。");

        var backup = Path.Combine(_root, "backups", $"{WorkshopPackageArchive.Key(id)}-removed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(backup);
        var moved = new List<(string Source, string Archived)>();
        var hadSetting = _enabled.TryGetValue(id, out var enabled);
        try
        {
            foreach (var path in paths)
            {
                var archived = Path.Combine(backup, Path.GetFileName(path));
                Directory.Move(path, archived);
                moved.Add((path, archived));
            }
            _enabled.Remove(id);
            ValidateSet(EnabledRoots());
            Activate();
            SaveCatalog();
        }
        catch
        {
            foreach (var (source, archived) in moved.AsEnumerable().Reverse())
                Directory.Move(archived, source);
            if (hadSetting) _enabled[id] = enabled;
            Activate();
            throw;
        }
        return backup;
    }

    public bool CanRemoveInstalledPack(NativeWorkshopPack pack) => pack.Installed
        && !PackageDiscovery.Discover(_shipped).Packages.Any(p => p.Manifest.Id == pack.Id);

    private static bool DependsOn(string root, string candidateId, string targetId)
    {
        var manifest = Manifest(root);
        return WorkshopPackagePresentation.Parent(root, candidateId) == targetId
            || manifest.RequiredPackages.Contains(targetId, StringComparer.Ordinal)
            || manifest.Dependencies.Any(p => p.Id == targetId);
    }
}
