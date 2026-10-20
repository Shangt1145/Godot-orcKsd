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
    {using var zip=ZipFile.OpenRead(patch);return ReadAll(zip)[0];}
    public static PatchManifest[] InspectAll(string patch)
    {using var zip=ZipFile.OpenRead(patch);return ReadAll(zip);}
    internal static PatchManifest[] ReadAll(ZipArchive zip)
    {
        if(zip.Entries.GroupBy(e=>e.FullName,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1))throw new InvalidDataException("补丁包含重复数据。");
        var entry=zip.GetEntry("patch.json")??throw new InvalidDataException("这不是 OrC-KSD 离线补丁。");
        if(entry.Length>32*1024*1024)throw new InvalidDataException("补丁清单过大。");
        using var stream=entry.Open();using var document=JsonDocument.Parse(stream);
        var format=document.RootElement.GetProperty("Format").GetInt32();
        PatchManifest[] variants=format switch
        {
            1=>[document.RootElement.Deserialize<PatchManifest>()??throw new InvalidDataException("补丁清单为空。")],
            2=>document.RootElement.Deserialize<PatchBundle>()?.Variants??throw new InvalidDataException("兼容补丁清单为空。"),
            _=>throw new InvalidDataException("请使用新版更新器打开此补丁。")
        };
        if(variants.Length is <1 or >64||variants.Any(p=>p is null)||variants.Select(p=>p.Source.BuildId).Distinct().Count()!=variants.Length)
            throw new InvalidDataException("兼容补丁来源数量或编号无效。");
        foreach(var p in variants)ValidatePlan(zip,p);
        if(variants.Any(p=>p.Target.BuildId!=variants[0].Target.BuildId||p.Target.Version!=variants[0].Target.Version))
            throw new InvalidDataException("兼容补丁必须升级到相同目标构建。");
        return variants;
    }
    private static void ValidatePlan(ZipArchive zip,PatchManifest p)
    {
        if(p.Format!=1)throw new InvalidDataException("差分格式无效。");
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
    }
    internal static PatchManifest Select(ZipArchive archive,string root)
    {
        var plans=ReadAll(archive);
        if(plans.Length==1){Builds.Verify(root,plans[0].Source);return plans[0];}
        // Verify bytes rather than trusting the display version or a stale local manifest.
        foreach(var plan in plans)
        {
            try{Builds.Verify(root,plan.Source);return plan;}
            catch(InvalidDataException) { }
        }
        throw new InvalidDataException("当前游戏文件不匹配本兼容补丁支持的任何构建（"+
            string.Join("、",plans.Select(p=>p.Source.BuildId[..12]))+
            "）。可能是未收录构建、混装或文件已损坏；请使用对应原始完整包恢复，不能跳过文件校验。");
    }

    public static PatchManifest[] Combine(IEnumerable<string> patches,string output)
    {
        var variants=new List<PatchManifest>();
        var temporary=output+"."+Guid.NewGuid().ToString("N")+".building";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        try
        {
            using(var target=ZipFile.Open(temporary,ZipArchiveMode.Create))
            {
                var blocks=new HashSet<string>(StringComparer.Ordinal);
                foreach(var path in patches)
                {
                    using var source=ZipFile.OpenRead(path);
                    foreach(var plan in ReadAll(source))
                    {
                        if(variants.Count>=64||variants.Any(p=>p.Source.BuildId==plan.Source.BuildId))throw new InvalidDataException("来源构建重复或过多。");
                        if(variants.Count>0&&(plan.Target.BuildId!=variants[0].Target.BuildId||plan.Target.Version!=variants[0].Target.Version))
                            throw new InvalidDataException("只能合并相同目标构建的补丁。");
                        var changes=new List<FileDelta>();
                        foreach(var change in plan.Changes)
                        {
                            var parts=new List<DeltaPart>();
                            foreach(var part in change.Parts)
                            {
                                if(part.Entry is null){parts.Add(part);continue;}
                                using var raw=source.GetEntry(part.Entry)!.Open();using var data=new MemoryStream();raw.CopyTo(data);
                                var bytes=data.ToArray();var name="blocks/"+Convert.ToHexString(SHA256.HashData(bytes))+".br";
                                if(blocks.Add(name)){using var dest=target.CreateEntry(name,CompressionLevel.NoCompression).Open();dest.Write(bytes);}
                                parts.Add(part with{Entry=name});
                            }
                            changes.Add(change with{Parts=parts.ToArray()});
                        }
                        variants.Add(plan with{Changes=changes.ToArray()});
                    }
                }
                if(variants.Count==0)throw new InvalidDataException("请选择至少一份来源补丁。");
                using var manifest=target.CreateEntry("patch.json",CompressionLevel.Optimal).Open();
                JsonSerializer.Serialize(manifest,new PatchBundle(2,variants.ToArray()),Builds.Json);
            }
            InspectAll(temporary);File.Move(temporary,output,true);return variants.ToArray();
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
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
