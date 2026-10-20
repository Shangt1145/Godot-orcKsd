using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OrC.Update;

public sealed record FileStamp(string Path, long Size, string Sha256);
public sealed record BuildManifest(string Version, string BuildId, FileStamp[] Files);
public sealed record DeltaPart(long Offset, int Length, string? Entry = null);
public sealed record FileDelta(string Path, DeltaPart[] Parts);
public sealed record PatchManifest(int Format, BuildManifest Source, BuildManifest Target, FileDelta[] Changes);
public sealed record PatchBundle(int Format, PatchManifest[] Variants);
public sealed record UpdateProgress(string Message, double Fraction);
public sealed class UpdateOptions
{
    public Func<bool>? GameRunning { get; init; }
    public Func<long>? AvailableBytes { get; init; }
    // Diagnostic injection also exercises process interruption and recovery in automated tests.
    public Action<string>? Checkpoint { get; init; }
}

public static class Builds
{
    public const string ManifestName = ".orcksd-build.json";
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string Hash(string file) { using var stream=File.OpenRead(file);return Convert.ToHexString(SHA256.HashData(stream)); }
    public static BuildManifest Scan(string root, string version)
    {
        var files=Directory.GetFiles(root,"*",SearchOption.AllDirectories)
            .Select(f=>(File:f,Relative:Path.GetRelativePath(root,f).Replace('\\','/')))
            .Where(f=>f.Relative is "KardsUi.exe" or "KardsUi.pck" || f.Relative.StartsWith("data_Kards.Ui_windows_x86_64/",StringComparison.Ordinal))
            .OrderBy(f=>f.Relative,StringComparer.Ordinal)
            .Select(f=>new FileStamp(f.Relative,new FileInfo(f.File).Length,Hash(f.File))).ToArray();
        if(!files.Any(f=>f.Path=="KardsUi.exe")||!files.Any(f=>f.Path=="KardsUi.pck"))throw new InvalidDataException("选择的目录不是完整的 OrC-KSD Windows 游戏。");
        return new(version,Id(files),files);
    }
    internal static string Id(IEnumerable<FileStamp> files)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\n',files.OrderBy(f=>f.Path,StringComparer.Ordinal).Select(f=>$"{f.Path}\0{f.Size}\0{f.Sha256}")))));
    public static void Write(string root,BuildManifest build)=>AtomicJson(Path.Combine(root,ManifestName),build);
    internal static void AtomicJson<T>(string path,T value)
    {
        var temp=path+".tmp";using(var stream=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None))
        {JsonSerializer.Serialize(stream,value,Json);stream.Flush(true);}File.Move(temp,path,true);
    }
    internal static T Read<T>(string path)=>JsonSerializer.Deserialize<T>(File.ReadAllText(path))??throw new InvalidDataException("更新记录为空。");
    internal static string Safe(string root,string relative)
    {
        if(string.IsNullOrEmpty(relative)||relative.Contains('\\')||relative.Contains(':')||Path.IsPathRooted(relative)
            ||relative.Split('/').Any(s=>s is "" or "." or ".."||s.EndsWith('.')||s.EndsWith(' ')||s.Any(c=>c<32)))throw new InvalidDataException("补丁含非法路径。");
        var full=Path.GetFullPath(Path.Combine(root,relative));var prefix=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("补丁路径超出游戏目录。");
        for(var path=full;path is not null;path=Path.GetDirectoryName(path))
        {if((File.Exists(path)||Directory.Exists(path))&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("更新目录不能包含链接或目录联接。");}
        return full;
    }
    internal static void Validate(BuildManifest build)
    {
        if(build.Files is null||build.Files.Length>100000||string.IsNullOrWhiteSpace(build.Version))throw new InvalidDataException("版本清单无效。");
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var f in build.Files)
        {
            Safe(Path.GetTempPath(),f.Path);
            if(!(f.Path is "KardsUi.exe" or "KardsUi.pck"||f.Path.StartsWith("data_Kards.Ui_windows_x86_64/",StringComparison.Ordinal))
                ||!names.Add(f.Path)||f.Size<0||f.Sha256.Length!=64||!f.Sha256.All(Uri.IsHexDigit))throw new InvalidDataException("补丁的文件清单无效。");
        }
        if(!build.Files.Any(f=>f.Path=="KardsUi.exe")||!build.Files.Any(f=>f.Path=="KardsUi.pck")||Id(build.Files)!=build.BuildId)throw new InvalidDataException("补丁的构建编号不匹配。");
    }
    internal static void Verify(string root,BuildManifest build)
    {
        Validate(build);
        foreach(var f in build.Files)
        {
            var path=Safe(root,f.Path);
            if(!File.Exists(path)||new FileInfo(path).Length!=f.Size||Hash(path)!=f.Sha256)
                throw new InvalidDataException($"此补丁需要 {build.Version}（构建 {build.BuildId[..12]}）。文件缺失或已修改：{f.Path}");
        }
    }
}
