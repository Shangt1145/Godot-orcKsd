using System.IO.Compression;
using System.Text.Json;
using OrC.Update;
using Xunit;

namespace Kards.Ui.Tests;

public sealed class OfflineUpdateTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"OrC 离线 更新 "+Guid.NewGuid().ToString("N"));
    private string Old=>Path.Combine(root,"旧版 游戏");
    private string New=>Path.Combine(root,"新版 游戏");
    private string Patch=>Path.Combine(root,"升级.orcupdate");
    public OfflineUpdateTests()
    {
        Directory.CreateDirectory(Old);Directory.CreateDirectory(New);
        var bytes=new byte[3*1024*1024];new Random(123).NextBytes(bytes);
        Write(Old,"KardsUi.exe",[1,2,3]);Write(New,"KardsUi.exe",[1,2,3,4]);
        Write(Old,"KardsUi.pck",bytes);Write(New,"KardsUi.pck",bytes[..800000].Concat(new byte[]{17,18,19,20,21}).Concat(bytes[800000..]).ToArray());
        Write(Old,"data_Kards.Ui_windows_x86_64/删除.dll",[2,4,6]);
        Write(New,"data_Kards.Ui_windows_x86_64/新卡.dll",[9,8,7]);
    }
    private static void Write(string root,string name,byte[] bytes){var f=Path.Combine(root,name);Directory.CreateDirectory(Path.GetDirectoryName(f)!);File.WriteAllBytes(f,bytes);}
    private PatchManifest Build()=>PatchBuilder.Create(Old,New,Patch,"0.0.0-beta.1","0.0.0-beta.1");
    private void Exact(string expected)=>Assert.Equal(Builds.Scan(expected,"v").BuildId,Builds.Scan(Old,"v").BuildId);
    [Fact]
    public async Task IncrementalPatchReusesShiftedPckAndRollsBackWithPlayerFilesIntact()
    {
        var p=Build();Assert.NotEqual(p.Source.BuildId,p.Target.BuildId);
        Assert.True(new FileInfo(Patch).Length<350000,"PCK shifted by five bytes should reuse its other content.");
        Assert.True(p.Changes.Single(c=>c.Path=="KardsUi.pck").Parts.Where(p=>p.Entry is null).Sum(p=>(long)p.Length)>2500000);
        Write(Old,"自己的说明.txt",[12,34]);Write(Old,"玩家备份/卡组.json",[1,3,5]);
        await Installer.ApplyAsync(Old,Patch);Exact(New);Assert.False(File.Exists(Path.Combine(Old,"data_Kards.Ui_windows_x86_64/删除.dll")));
        Assert.Equal(new byte[]{1,3,5},File.ReadAllBytes(Path.Combine(Old,"玩家备份/卡组.json")));Assert.True(Installer.CanRollback(Old));
        Installer.Rollback(Old);Assert.Equal(p.Source.BuildId,Builds.Scan(Old,"v").BuildId);Assert.True(File.Exists(Path.Combine(Old,"自己的说明.txt")));
        Assert.False(File.Exists(Path.Combine(Old,Builds.ManifestName)));
    }
    [Fact]
    public async Task LockedGameFileStopsBeforeReplacement()
    {
        var p=Build();using(var locked=new FileStream(Path.Combine(Old,"KardsUi.exe"),FileMode.Open,FileAccess.Read,FileShare.None))
            await Assert.ThrowsAsync<IOException>(()=>Installer.ApplyAsync(Old,Patch));
        Assert.Equal(p.Source.BuildId,Builds.Scan(Old,"v").BuildId);
    }
    [Theory]
    [InlineData("stage:1")]
    [InlineData("backup:1")]
    [InlineData("commit:1")]
    [InlineData("commit:3")]
    [InlineData("committed")]
    public async Task AbruptProcessExitRecoversFromThePersistedJournal(string phase)
    {
        var p=Build();var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"project.godot")))directory=directory.Parent;
        var configuration=typeof(OfflineUpdateTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute),false)
            .Cast<System.Reflection.AssemblyConfigurationAttribute>().Single().Configuration;
        var dll=Path.Combine(directory!.FullName,$"tools/OfflineUpdate/OrC.Update.Cli/bin/{configuration}/net9.0/OrC.Update.Cli.dll");
        var start=new System.Diagnostics.ProcessStartInfo("dotnet"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{dll,"apply",Old,Patch,phase})start.ArgumentList.Add(arg);
        using var child=System.Diagnostics.Process.Start(start)!;var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));await Task.WhenAll(stdout,stderr);Assert.Equal(86,child.ExitCode);
        Assert.True(Installer.HasRecovery(Old));Installer.Recover(Old);
        Assert.Equal(phase=="committed"?p.Target.BuildId:p.Source.BuildId,Builds.Scan(Old,"v").BuildId);
        if(phase=="committed"){Installer.Rollback(Old);Assert.Equal(p.Source.BuildId,Builds.Scan(Old,"v").BuildId);}
    }
    [Fact]
    public async Task UserDataOutsideTheGameAndExistingManifestSurviveUpdateAndRollback()
    {
        var saved=Path.Combine(root,"user-data");Write(saved,"decks.json",[1,2]);Write(saved,"diy-cards.json",[3,4]);Write(saved,"orc-imports.json",[5]);Write(saved,"art/卡图.png",[8,9]);
        var expected=Directory.GetFiles(saved,"*",SearchOption.AllDirectories).ToDictionary(p=>p,Builds.Hash);
        var original=Builds.Scan(Old,"old");Builds.Write(Old,original);var metadata=File.ReadAllBytes(Path.Combine(Old,Builds.ManifestName));
        Build();await Installer.ApplyAsync(Old,Patch);Installer.Rollback(Old);
        Assert.Equal(metadata,File.ReadAllBytes(Path.Combine(Old,Builds.ManifestName)));Assert.All(expected,p=>Assert.Equal(p.Value,Builds.Hash(p.Key)));
    }
    [Fact]
    public void BootstrapInstallsTheNativeUpdaterWithoutTouchingTheGame()
    {
        var bootstrap=Path.Combine(root,"首次更新器");Write(bootstrap,"更新游戏.exe",[1]);Write(bootstrap,"更新游戏.pck",[2]);Write(bootstrap,"data_OrC.Updater_windows_x86_64/OrC.Updater.dll",[3]);
        var previous=Builds.Scan(Old,"v").BuildId;Installer.InstallUpdater(bootstrap,Old);Installer.InstallUpdater(bootstrap,Old);
        Assert.Equal(previous,Builds.Scan(Old,"v").BuildId);Assert.True(File.Exists(Path.Combine(Old,"更新游戏.exe")));
    }
    [Fact]
    public async Task WrongOrModifiedBaseDoesNotReplaceAnyOtherFile()
    {
        Build();Write(Old,"KardsUi.exe",[8,8]);var previous=Builds.Scan(Old,"v").BuildId;
        var error=await Assert.ThrowsAsync<InvalidDataException>(()=>Installer.ApplyAsync(Old,Patch));Assert.Contains("此补丁需要",error.Message);
        Assert.Equal(previous,Builds.Scan(Old,"v").BuildId);
    }
    [Fact]
    public async Task CorruptDataDoesNotTouchTheInstalledBuild()
    {
        var p=Build();using(var zip=ZipFile.Open(Patch,ZipArchiveMode.Update))
        {var block=zip.Entries.First(e=>e.FullName.StartsWith("blocks/"));var name=block.FullName;block.Delete();using var raw=zip.CreateEntry(name).Open();raw.Write([0,0,0]);}
        await Assert.ThrowsAnyAsync<Exception>(()=>Installer.ApplyAsync(Old,Patch));Assert.Equal(p.Source.BuildId,Builds.Scan(Old,"v").BuildId);Assert.False(Installer.HasRecovery(Old));
    }
    [Fact]
    public async Task InsufficientSpaceStopsBeforeReplacement()
    {
        var p=Build();await Assert.ThrowsAsync<IOException>(()=>Installer.ApplyAsync(Old,Patch,new(){AvailableBytes=()=>0}));
        Assert.Equal(p.Source.BuildId,Builds.Scan(Old,"v").BuildId);
    }
    [Theory]
    [InlineData("stage:1")]
    [InlineData("before-commit")]
    [InlineData("backup:1")]
    [InlineData("commit:1")]
    [InlineData("commit:2")]
    public async Task InterruptedIoAtEveryPhaseRecoversTheOldVersion(string phase)
    {
        var p=Build();await Assert.ThrowsAsync<IOException>(()=>Installer.ApplyAsync(Old,Patch,new(){Checkpoint=s=>{if(s==phase)throw new IOException("injected interruption");}}));
        Assert.Equal(p.Source.BuildId,Builds.Scan(Old,"v").BuildId);Installer.Recover(Old);Assert.False(Installer.HasRecovery(Old));
    }
    [Fact]
    public async Task RunningGameWaitsAndCanCancelWithoutChangingFiles()
    {
        var p=Build();using var cancel=new CancellationTokenSource(150);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Installer.ApplyAsync(Old,Patch,new(){GameRunning=()=>true},ct:cancel.Token));
        Assert.Equal(p.Source.BuildId,Builds.Scan(Old,"v").BuildId);
    }
    [Fact]
    public async Task SecondUpdaterCannotEnterTheSameInstallation()
    {
        Build();using var cancel=new CancellationTokenSource();var waiting=new TaskCompletionSource();
        var first=Installer.ApplyAsync(Old,Patch,new(){GameRunning=()=>{waiting.TrySetResult();return true;}},ct:cancel.Token);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<IOException>(()=>Installer.ApplyAsync(Old,Patch));cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>first);
    }
    [Fact]
    public async Task NewlyIntroducedFilesNeverOverwritePlayerFiles()
    {
        var p=Build();Write(Old,"data_Kards.Ui_windows_x86_64/新卡.dll",[77]);
        await Assert.ThrowsAsync<IOException>(()=>Installer.ApplyAsync(Old,Patch));Assert.Equal(new byte[]{77},File.ReadAllBytes(Path.Combine(Old,"data_Kards.Ui_windows_x86_64/新卡.dll")));
        Assert.Equal(p.Source.Files.Single(f=>f.Path=="KardsUi.exe").Sha256,Builds.Hash(Path.Combine(Old,"KardsUi.exe")));
    }
    [Fact]
    public async Task ZeroLengthNewFilesAndUnchangedFilesWork()
    {
        Write(New,"data_Kards.Ui_windows_x86_64/empty.dll",[]);Write(New,"data_Kards.Ui_windows_x86_64/same.dll",[1]);Write(Old,"data_Kards.Ui_windows_x86_64/same.dll",[1]);
        Build();await Installer.ApplyAsync(Old,Patch);Exact(New);
    }
    [Fact]
    public async Task MissingLiteralAndDuplicateOrTraversalEntriesAreRejected()
    {
        Build();using(var zip=ZipFile.Open(Patch,ZipArchiveMode.Update)){zip.Entries.First(e=>e.FullName.StartsWith("blocks/")).Delete();}
        Assert.Throws<InvalidDataException>(()=>PatchBuilder.Inspect(Patch));
        Build();using(var zip=ZipFile.Open(Patch,ZipArchiveMode.Update)){using var extra=zip.CreateEntry("patch.json").Open();extra.Write([1]);}
        Assert.Throws<InvalidDataException>(()=>PatchBuilder.Inspect(Patch));
        Build();using(var zip=ZipFile.Open(Patch,ZipArchiveMode.Update))
        {
            var e=zip.GetEntry("patch.json")!;PatchManifest p;using(var input=e.Open())p=JsonSerializer.Deserialize<PatchManifest>(input)!;e.Delete();
            p=p with{Target=p.Target with{Files=[new("../玩家文件",0,new string('A',64))]}};using var output=zip.CreateEntry("patch.json").Open();JsonSerializer.Serialize(output,p);
        }
        Assert.Throws<InvalidDataException>(()=>PatchBuilder.Inspect(Patch));await Task.CompletedTask;
    }
    [Fact]
    public async Task TwoSuccessfulUpdatesRetainOnlyTheMostRecentRollback()
    {
        Build();await Installer.ApplyAsync(Old,Patch);var middle=Builds.Scan(New,"v").BuildId;
        var third=Path.Combine(root,"第三版");
        foreach(var file in Directory.GetFiles(New,"*",SearchOption.AllDirectories))Write(third,Path.GetRelativePath(New,file),File.ReadAllBytes(file));
        Write(third,"KardsUi.exe",[4,5,6,7]);var second=Path.Combine(root,"再次更新.orcupdate");PatchBuilder.Create(New,third,second,"middle","third");
        await Installer.ApplyAsync(Old,second);Exact(third);Installer.Rollback(Old);Assert.Equal(middle,Builds.Scan(Old,"v").BuildId);Assert.False(Installer.CanRollback(Old));
    }
    [Fact]
    public async Task FailedSecondUpdatePreservesTheFirstRollback()
    {
        var p=Build();await Installer.ApplyAsync(Old,Patch);
        await Assert.ThrowsAsync<InvalidDataException>(()=>Installer.ApplyAsync(Old,Patch));Assert.True(Installer.CanRollback(Old));
        Installer.Rollback(Old);Assert.Equal(p.Source.BuildId,Builds.Scan(Old,"v").BuildId);
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
