using System.IO.Compression;
using System.Text.Json;
using OrC.Update;

try
{
    switch(args[0])
    {
        case "combine":
        {
            var plans=PatchBuilder.Combine(args.Skip(2),args[1]);
            Console.WriteLine($"PATCH_COMBINE_OK sources={plans.Length} target={plans[0].Target.BuildId} bytes={new FileInfo(args[1]).Length}");break;
        }
        case "inspect-all":Console.WriteLine(JsonSerializer.Serialize(PatchBuilder.InspectAll(args[1])));break;
        case "build":
        {
            var source=args[1];string? temporary=null;
            try
            {
                if(File.Exists(source)){temporary=Path.Combine(Path.GetTempPath(),"orcksd-base-"+Guid.NewGuid().ToString("N"));ZipFile.ExtractToDirectory(source,temporary);source=temporary;}
                var p=PatchBuilder.Create(source,args[2],args[3],args[4],args[5]);
                Console.WriteLine($"PATCH_BUILD_OK source={p.Source.BuildId} target={p.Target.BuildId} changes={p.Changes.Length} bytes={new FileInfo(args[3]).Length}");
            }
            finally
            {
                if(temporary is not null)
                {
                    var full=Path.GetFullPath(temporary);var prefix=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
                    if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(full).StartsWith("orcksd-base-",StringComparison.Ordinal))throw new IOException("Refusing cleanup outside the update baseline workspace.");
                    Directory.Delete(full,true);
                }
            }break;
        }
        case "stamp":Builds.Write(args[1],Builds.Scan(args[1],args[2]));break;
        case "inspect":Console.WriteLine(JsonSerializer.Serialize(PatchBuilder.Inspect(args[1])));break;
        case "apply":
            await Installer.ApplyAsync(args[1],args[2],new UpdateOptions{Checkpoint=point=>{if(args.Length>3&&point==args[3])Environment.Exit(86);}});
            Console.WriteLine("PATCH_APPLY_OK");break;
        case "recover":Installer.Recover(args[1]);Console.WriteLine("PATCH_RECOVERY_OK");break;
        case "rollback":Installer.Rollback(args[1]);Console.WriteLine("PATCH_ROLLBACK_OK");break;
        case "verify":
        {
            var expected=Builds.Scan(args[2],"verify");var actual=Builds.Scan(args[1],"verify");
            if(expected.BuildId!=actual.BuildId)throw new InvalidDataException("更新结果与完整包不同。");Console.WriteLine("PATCH_EXACT_FILES_OK "+actual.BuildId);break;
        }
        default:throw new ArgumentException("命令：build <旧ZIP或目录> <新目录> <补丁> <旧版本> <新版本> | apply <游戏目录> <补丁> | recover | rollback | verify <安装目录> <新目录>");
    }
}
catch(Exception e){Console.Error.WriteLine(e.ToString());Environment.ExitCode=1;}
