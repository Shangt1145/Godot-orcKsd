using System.Diagnostics;
using System.IO.Compression;

namespace OrC.Update;

public static class Installer
{
    private sealed record Journal(PatchManifest Patch,string Phase,string? PreviousManifest);
    private const string StateName=".orcksd-update";
    private static string State(string root)=>Builds.Safe(root,StateName);
    private static FileStream Lock(string root)
    {
        if(!Directory.Exists(root))throw new DirectoryNotFoundException("请选择游戏所在文件夹。");
        var state=State(root);var owner=Builds.Safe(root,StateName+"/owner.txt");
        if(Directory.Exists(state)&&!File.Exists(owner)&&Directory.EnumerateFileSystemEntries(state).Any())throw new IOException("更新工作目录被其他文件占用。");
        Directory.CreateDirectory(state);
        if(File.Exists(owner)&&File.ReadAllText(owner)!="OrC-KSD Offline Update 1")throw new IOException("更新工作目录不匹配。");
        File.WriteAllText(owner,"OrC-KSD Offline Update 1");
        try{return new FileStream(Builds.Safe(root,StateName+"/lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
        catch(IOException){throw new IOException("已有一个更新器正在操作此游戏，请先关闭另一个更新器。");}
    }
    private static void RemoveTree(string root,string directory)
    {
        var relative=Path.GetRelativePath(root,directory).Replace('\\','/');Builds.Safe(root,relative);
        if(!relative.StartsWith(StateName+"/",StringComparison.Ordinal))throw new IOException("拒绝清理非更新工作目录。");
        if(Directory.Exists(directory))
        {
            void CheckTree(string dir)
            {
                foreach(var path in Directory.GetFileSystemEntries(dir))
                {Builds.Safe(root,Path.GetRelativePath(root,path).Replace('\\','/'));if(Directory.Exists(path))CheckTree(path);}
            }
            CheckTree(directory);
            Directory.Delete(directory,true);
        }
    }
    public static bool GameIsRunning(string root)
    {
        foreach(var process in Process.GetProcessesByName("KardsUi"))using(process)
        {
            try{if(string.Equals(process.MainModule?.FileName,Path.Combine(Path.GetFullPath(root),"KardsUi.exe"),StringComparison.OrdinalIgnoreCase))return true;}
            catch(System.ComponentModel.Win32Exception){return true;}
            catch(InvalidOperationException){}
        }
        return false;
    }
    public static void InstallUpdater(string sourceRoot,string gameRoot)=>InstallUpdater(sourceRoot,gameRoot,false);
    public static void InstallUpdater(string sourceRoot,string gameRoot,bool keepExistingDifferent)
    {
        sourceRoot=Path.GetFullPath(sourceRoot);gameRoot=Path.GetFullPath(gameRoot);
        if(sourceRoot.Equals(gameRoot,StringComparison.OrdinalIgnoreCase))return;
        if(!File.Exists(Path.Combine(gameRoot,"KardsUi.exe")))throw new IOException("请选择已有游戏所在文件夹。");
        using var lease=Lock(gameRoot);
        var files=new[]{"更新游戏.exe","更新游戏.pck"}.Concat(Directory.GetFiles(Path.Combine(sourceRoot,"data_OrC.Updater_windows_x86_64"),"*",SearchOption.AllDirectories)
            .Select(p=>Path.GetRelativePath(sourceRoot,p).Replace('\\','/'))).ToArray();
        // A newer external updater can operate without replacing a different installed updater.
        // Preflight the entire set before copying, so no mixed updater is installed.
        if(keepExistingDifferent&&files.Any(name=>File.Exists(Builds.Safe(gameRoot,name))
            &&Builds.Hash(Builds.Safe(sourceRoot,name))!=Builds.Hash(Builds.Safe(gameRoot,name))))return;
        foreach(var name in files)
        {
            var source=Builds.Safe(sourceRoot,name);var dest=Builds.Safe(gameRoot,name);
            if(File.Exists(dest))
            {
                if(Builds.Hash(source)==Builds.Hash(dest))continue;
                throw new IOException("游戏目录已有不同的更新器文件，未覆盖："+name);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);var temporary=dest+"."+Guid.NewGuid().ToString("N")+".installing";
            File.Copy(source,temporary,true);if(Builds.Hash(source)!=Builds.Hash(temporary))throw new IOException("更新器安装校验失败。");File.Move(temporary,dest);
        }
    }
    private static async Task WaitForGame(string root,UpdateOptions options,IProgress<UpdateProgress>? progress,CancellationToken ct)
    {
        while((options.GameRunning??(()=>GameIsRunning(root)))())
        {progress?.Report(new("请关闭游戏窗口，关闭后自动继续；也可以取消更新。",0));await Task.Delay(500,ct);}
    }
    public static async Task<BuildManifest> ApplyAsync(string root,string patch,UpdateOptions? options=null,IProgress<UpdateProgress>? progress=null,CancellationToken ct=default)
    {
        root=Path.GetFullPath(root);options??=new();using var lease=Lock(root);
        await WaitForGame(root,options,progress,ct);RecoverLocked(root);
        progress?.Report(new("正在识别本地构建并核对程序文件…",.02));
        using var archive=ZipFile.OpenRead(patch);var plan=PatchBuilder.Select(archive,root);
        progress?.Report(new("已识别构建 "+plan.Source.BuildId[..12]+"，正在准备更新…",.05));
        var before=plan.Source.Files.ToDictionary(f=>f.Path,StringComparer.OrdinalIgnoreCase);var after=plan.Target.Files.ToDictionary(f=>f.Path,StringComparer.OrdinalIgnoreCase);
        var paths=ChangedPaths(plan);
        foreach(var name in paths)
        {
            var path=Builds.Safe(root,name);
            if(!before.ContainsKey(name)&&(File.Exists(path)||Directory.Exists(path)))throw new IOException("新版本文件与玩家自己的文件重名，未覆盖："+name);
        }
        var required=checked(plan.Changes.Sum(c=>after[c.Path].Size)+16*1024*1024);
        var available=options.AvailableBytes?.Invoke()??new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace;
        if(available<required)throw new IOException($"磁盘空间不足，更新至少需要 {Math.Ceiling(required/1048576d)} MB 临时空间。");
        var pending=Path.Combine(State(root),"pending");Directory.CreateDirectory(pending);
        var manifest=Path.Combine(root,Builds.ManifestName);
        var journal=new Journal(plan,"Staging",File.Exists(manifest)?Convert.ToBase64String(File.ReadAllBytes(manifest)):null);
        Builds.AtomicJson(Path.Combine(pending,"journal.json"),journal);
        try
        {
            for(var i=0;i<plan.Changes.Length;i++)
            {
                var change=plan.Changes[i];ct.ThrowIfCancellationRequested();progress?.Report(new("准备更新："+change.Path,.1+.65*i/Math.Max(1,plan.Changes.Length)));
                var output=Builds.Safe(pending,"stage/"+change.Path);
                PatchBuilder.Rebuild(archive,change,before.ContainsKey(change.Path)?Builds.Safe(root,change.Path):null,output,ct);
                if(new FileInfo(output).Length!=after[change.Path].Size||Builds.Hash(output)!=after[change.Path].Sha256)throw new InvalidDataException("补丁校验失败，原游戏未替换："+change.Path);
                options.Checkpoint?.Invoke("stage:"+(i+1));
            }
            await WaitForGame(root,options,progress,ct);ct.ThrowIfCancellationRequested();Builds.Verify(root,plan.Source);
            journal=journal with{Phase="Applying"};Builds.AtomicJson(Path.Combine(pending,"journal.json"),journal);
            options.Checkpoint?.Invoke("before-commit");
            for(var i=0;i<paths.Length;i++)
            {
                var name=paths[i];var dest=Builds.Safe(root,name);progress?.Report(new("正在安装，请勿关闭更新器…",.8+.15*i/Math.Max(1,paths.Length)));
                if(before.ContainsKey(name))
                {
                    var backup=Builds.Safe(pending,"backup/"+name);Directory.CreateDirectory(Path.GetDirectoryName(backup)!);File.Move(dest,backup);
                }
                options.Checkpoint?.Invoke("backup:"+(i+1));
                if(after.ContainsKey(name)){Directory.CreateDirectory(Path.GetDirectoryName(dest)!);File.Move(Builds.Safe(pending,"stage/"+name),dest);}
                options.Checkpoint?.Invoke("commit:"+(i+1));
            }
            Builds.Verify(root,plan.Target);Builds.Write(root,plan.Target);
            Builds.AtomicJson(Path.Combine(pending,"journal.json"),journal with{Phase="Committed"});
            options.Checkpoint?.Invoke("committed");Promote(root,pending);
            progress?.Report(new("更新完成，可以启动游戏。",1));return plan.Target;
        }
        catch
        {
            RecoverLocked(root);throw;
        }
    }
    private static string[] ChangedPaths(PatchManifest p)=>p.Changes.Select(c=>c.Path).Concat(p.Source.Files.Where(f=>!p.Target.Files.Any(t=>t.Path.Equals(f.Path,StringComparison.OrdinalIgnoreCase))).Select(f=>f.Path)).Order(StringComparer.Ordinal).ToArray();
    private static void Promote(string root,string pending)
    {
        var rollback=Path.Combine(State(root),"rollback");RemoveTree(root,rollback);
        var stage=Path.Combine(pending,"stage");RemoveTree(root,stage);Directory.Move(pending,rollback);
    }
    public static void Recover(string root)
    {root=Path.GetFullPath(root);using var lease=Lock(root);if(GameIsRunning(root))throw new IOException("请先关闭游戏，再恢复更新。");RecoverLocked(root);}
    private static void RecoverLocked(string root)
    {
        var pending=Path.Combine(State(root),"pending");if(!Directory.Exists(pending))return;
        var file=Path.Combine(pending,"journal.json");
        if(!File.Exists(file)){RemoveTree(root,pending);return;}
        var journal=Builds.Read<Journal>(file);Builds.Validate(journal.Patch.Source);Builds.Validate(journal.Patch.Target);
        switch(journal.Phase)
        {
            case "Staging":RemoveTree(root,pending);break;
            case "Applying":Restore(root,pending,journal);RemoveTree(root,pending);break;
            case "Committed":Builds.Verify(root,journal.Patch.Target);Promote(root,pending);break;
            default:throw new InvalidDataException("更新进度记录损坏，请保留更新工作目录。");
        }
    }
    public static bool HasRecovery(string root)=>Directory.Exists(Path.Combine(State(root),"pending"));
    public static bool CanRollback(string root)=>File.Exists(Path.Combine(State(root),"rollback/journal.json"));
    public static BuildManifest Rollback(string root)
    {
        root=Path.GetFullPath(root);using var lease=Lock(root);if(GameIsRunning(root))throw new IOException("请先关闭游戏，再回退版本。");RecoverLocked(root);
        var backup=Path.Combine(State(root),"rollback");var journal=Builds.Read<Journal>(Path.Combine(backup,"journal.json"));
        Builds.Verify(root,journal.Patch.Target);
        // Mark the rollback as recoverable before moving any backup. Restore is idempotent.
        var pending=Path.Combine(State(root),"pending");Directory.Move(backup,pending);
        Builds.AtomicJson(Path.Combine(pending,"journal.json"),journal with{Phase="Applying"});
        Restore(root,pending,journal);RemoveTree(root,pending);return journal.Patch.Source;
    }
    private static void Restore(string root,string directory,Journal journal)
    {
        Builds.Validate(journal.Patch.Source);Builds.Validate(journal.Patch.Target);
        var before=journal.Patch.Source.Files.ToDictionary(f=>f.Path,StringComparer.OrdinalIgnoreCase);var after=journal.Patch.Target.Files.ToDictionary(f=>f.Path,StringComparer.OrdinalIgnoreCase);
        var paths=ChangedPaths(journal.Patch);
        // Check every recovery input before changing anything; preserve unexpected player edits.
        foreach(var name in paths)
        {
            var dest=Builds.Safe(root,name);var backup=Builds.Safe(directory,"backup/"+name);
            if(before.TryGetValue(name,out var old))
            {
                var candidate=File.Exists(backup)?backup:dest;
                if(!File.Exists(candidate)||new FileInfo(candidate).Length!=old.Size||Builds.Hash(candidate)!=old.Sha256)throw new IOException("无法自动恢复，旧文件已缺失或改变："+name);
            }
            if(File.Exists(dest))
            {
                var hash=Builds.Hash(dest);
                if(hash!=before.GetValueOrDefault(name)?.Sha256&&hash!=after.GetValueOrDefault(name)?.Sha256)throw new IOException("恢复时发现额外修改，已停止："+name);
            }
        }
        foreach(var name in paths)
        {
            var dest=Builds.Safe(root,name);var backup=Builds.Safe(directory,"backup/"+name);
            if(File.Exists(backup)){Directory.CreateDirectory(Path.GetDirectoryName(dest)!);File.Move(backup,dest,true);}
            else if(!before.ContainsKey(name)&&File.Exists(dest))File.Delete(dest);
        }
        var manifest=Path.Combine(root,Builds.ManifestName);
        if(journal.PreviousManifest is null){if(File.Exists(manifest))File.Delete(manifest);}
        else {var temp=manifest+".tmp";File.WriteAllBytes(temp,Convert.FromBase64String(journal.PreviousManifest));File.Move(temp,manifest,true);}
        Builds.Verify(root,journal.Patch.Source);
    }
}
