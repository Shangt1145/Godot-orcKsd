using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace OrC.Update;

public static class PatchBuilder
{
    // Content-defined boundaries keep later chunks reusable when PCK contents shift.
    private const int Minimum=16384,Maximum=262144;
    private static readonly ulong[] Gear=Enumerable.Range(0,256).Select(i=>
    {var b=SHA256.HashData(BitConverter.GetBytes(i));return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(b);}).ToArray();
    private static IEnumerable<(long Offset,byte[] Bytes)> Chunks(string file)
    {
        using var stream=new BufferedStream(File.OpenRead(file),1024*1024);var buffer=new byte[Maximum];var count=0;long offset=0;ulong hash=0;
        int b;while((b=stream.ReadByte())>=0)
        {
            buffer[count++]=(byte)b;hash=unchecked((hash<<1)+Gear[b]);
            if(count>=Maximum||(count>=Minimum&&(hash&65535)==0))
            {yield return(offset,buffer[..count]);offset+=count;count=0;hash=0;}
        }
        if(count>0)yield return(offset,buffer[..count]);
    }
    public static PatchManifest Create(string sourceRoot,string targetRoot,string output,string sourceVersion,string targetVersion,IProgress<UpdateProgress>? progress=null)
    {
        var source=Builds.Scan(sourceRoot,sourceVersion);var target=Builds.Scan(targetRoot,targetVersion);
        var before=source.Files.ToDictionary(f=>f.Path,StringComparer.OrdinalIgnoreCase);var changes=new List<FileDelta>();var serial=0;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);var temporary=output+"."+Guid.NewGuid().ToString("N")+".building";
        try
        {
            using(var zip=ZipFile.Open(temporary,ZipArchiveMode.Create))
            {
                var changed=target.Files.Where(f=>!before.TryGetValue(f.Path,out var old)||old.Sha256!=f.Sha256).ToArray();
                foreach(var f in changed)
                {
                    progress?.Report(new("生成差分："+f.Path,(double)changes.Count/Math.Max(1,changed.Length)));
                    var index=new Dictionary<string,(long Offset,int Length)>(StringComparer.Ordinal);
                    if(before.ContainsKey(f.Path))foreach(var chunk in Chunks(Builds.Safe(sourceRoot,f.Path)))
                        index.TryAdd(Convert.ToHexString(SHA256.HashData(chunk.Bytes)),(chunk.Offset,chunk.Bytes.Length));
                    var parts=new List<DeltaPart>();
                    foreach(var chunk in Chunks(Builds.Safe(targetRoot,f.Path)))
                    {
                        var hash=Convert.ToHexString(SHA256.HashData(chunk.Bytes));
                        if(index.TryGetValue(hash,out var old)&&old.Length==chunk.Bytes.Length)parts.Add(new(old.Offset,old.Length));
                        else
                        {
                            var name=$"blocks/{serial++:D8}.br";var entry=zip.CreateEntry(name,CompressionLevel.NoCompression);
                            using(var raw=entry.Open())using(var compressed=new BrotliStream(raw,CompressionLevel.Optimal))compressed.Write(chunk.Bytes);
                            parts.Add(new(0,chunk.Bytes.Length,name));
                        }
                    }
                    changes.Add(new(f.Path,parts.ToArray()));
                }
                var manifest=new PatchManifest(1,source,target,changes.ToArray());
                using var data=zip.CreateEntry("patch.json",CompressionLevel.Optimal).Open();JsonSerializer.Serialize(data,manifest,Builds.Json);
            }
            File.Move(temporary,output,true);Builds.Write(targetRoot,target);
            return new(1,source,target,changes.ToArray());
        }
        finally {if(File.Exists(temporary))File.Delete(temporary);}
    }
    public static PatchManifest Inspect(string patch)
    {using var zip=ZipFile.OpenRead(patch);return Read(zip);}
    internal static PatchManifest Read(ZipArchive zip)
    {
        if(zip.Entries.GroupBy(e=>e.FullName,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1))throw new InvalidDataException("补丁包含重复数据。");
        var entry=zip.GetEntry("patch.json")??throw new InvalidDataException("这不是 OrC-KSD 离线补丁。");
        if(entry.Length>32*1024*1024)throw new InvalidDataException("补丁清单过大。");
        using var stream=entry.Open();var p=JsonSerializer.Deserialize<PatchManifest>(stream)??throw new InvalidDataException("补丁清单为空。");
        if(p.Format!=1)throw new InvalidDataException("请使用新版更新器打开此补丁。");
        Builds.Validate(p.Source);Builds.Validate(p.Target);
        var before=p.Source.Files.ToDictionary(f=>f.Path,StringComparer.OrdinalIgnoreCase);var after=p.Target.Files.ToDictionary(f=>f.Path,StringComparer.OrdinalIgnoreCase);
        var expected=after.Values.Where(f=>!before.TryGetValue(f.Path,out var b)||f.Sha256!=b.Sha256).Select(f=>f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(p.Changes is null||p.Changes.Select(c=>c.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=p.Changes.Length
            ||!expected.SetEquals(p.Changes.Select(c=>c.Path)))throw new InvalidDataException("补丁变更清单不完整。");
        foreach(var c in p.Changes)
        {
            long total=0;foreach(var part in c.Parts)
            {
                if(part.Length<=0||part.Length>Maximum||part.Offset<0)throw new InvalidDataException("差分片段无效。");
                total=checked(total+part.Length);
                if(part.Entry is null)
                {if(!before.TryGetValue(c.Path,out var old)||part.Offset>old.Size-part.Length)throw new InvalidDataException("差分引用超出旧文件。");}
                else if(!part.Entry.StartsWith("blocks/",StringComparison.Ordinal)||zip.GetEntry(part.Entry) is not { } block||block.Length>Maximum+65536)
                    throw new InvalidDataException("补丁缺少新增数据。");
            }
            if(total!=after[c.Path].Size)throw new InvalidDataException("差分文件大小不匹配。");
        }
        return p;
    }
    internal static void Rebuild(ZipArchive zip,FileDelta change,string? oldFile,string output,CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);using var target=File.Create(output);
        using var old=oldFile is null?null:File.OpenRead(oldFile);var buffer=new byte[Maximum];
        foreach(var part in change.Parts)
        {
            ct.ThrowIfCancellationRequested();
            if(part.Entry is null){old!.Position=part.Offset;old.ReadExactly(buffer.AsSpan(0,part.Length));}
            else
            {
                using var raw=zip.GetEntry(part.Entry)!.Open();using var data=new BrotliStream(raw,CompressionMode.Decompress);
                data.ReadExactly(buffer.AsSpan(0,part.Length));if(data.ReadByte()!=-1)throw new InvalidDataException("补丁数据超出声明大小。");
            }
            target.Write(buffer,0,part.Length);
        }
        target.Flush(true);
    }
}
