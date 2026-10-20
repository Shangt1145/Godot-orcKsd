using System.Text.Json;
using System.Text.RegularExpressions;
using Kards.Ui.Contracts;

namespace Kards.Ui.Core;

public sealed class DiyWorkshopLibrary
{
    private sealed record Storage(int Schema,DiyPackDraft[] Packs);
    private readonly string _path;
    private List<DiyPackDraft> _packs=[];
    public string? LoadError {get;private set;}
    public IReadOnlyList<DiyPackDraft> Packs=>_packs.Select(p=>p.Snapshot()).ToArray();
    public DiyWorkshopLibrary(string path)
    {
        _path=path;if(!File.Exists(path))return;
        try
        {
            if(new FileInfo(path).Length>16_000_000)throw new InvalidDataException("工坊文件过大");
            var data=JsonSerializer.Deserialize<Storage>(File.ReadAllText(path))??throw new InvalidDataException("工坊文件为空");
            if(data.Schema!=1)throw new InvalidDataException("工坊版本无效");Validate(data.Packs);_packs=data.Packs.Select(p=>p.Snapshot()).ToList();
        }
        catch(Exception e)when(e is IOException or JsonException or InvalidDataException){LoadError="工坊读取失败，原文件保留："+e.Message;}
    }
    private static bool Id(string s,string prefix)=>s is not null&&s.StartsWith(prefix,StringComparison.Ordinal)&&Regex.IsMatch(s[prefix.Length..],"^[a-zA-Z0-9_-]{1,64}$");
    private static void Validate(DiyPackDraft[] packs)
    {
        if(packs is null||packs.Length>128||packs.Any(p=>p is null)||packs.Select(p=>p.Id).Distinct().Count()!=packs.Length)throw new InvalidDataException("卡包数量或编号无效");
        var ids=new HashSet<string>(StringComparer.Ordinal);
        foreach(var p in packs)
        {
            if(!ValidPackId(p.Id)||string.IsNullOrWhiteSpace(p.Name)||p.Name.Length>60||p.Nations is null||p.Keywords is null||p.Nations.Length>64||p.Keywords.Length>128)throw new InvalidDataException("卡包内容无效");
            if(p.BasePackId is { } parent)
            {
                if(!DeckCardPacks.ValidSelection([parent])||parent==p.Id||packs.Any(q=>q.Id==parent&&q.IsExpansion))throw new InvalidDataException("扩展包必须基于一个基础卡包");
                if(p.Keywords.Length>0)throw new InvalidDataException("词条定义属于基础卡包，请到基础卡包编辑");
            }
            foreach(var n in p.Nations)
                if(n is null||!Id(n.Id,"nation-")||!ids.Add(n.Id)||string.IsNullOrWhiteSpace(n.Name)||n.Name.Length>40||n.Color is null||n.IconFile is null||!Regex.IsMatch(n.Color,"^[0-9a-fA-F]{6}$")
                    ||(n.IconFile.Length>0&&n.IconFile!=n.Id+".png"))throw new InvalidDataException("自定义国家字段无效");
            foreach(var k in p.Keywords)
                if(k is null||!Id(k.Id,"kw-")||!ids.Add(k.Id)||string.IsNullOrWhiteSpace(k.Name)||k.Name.Length>30||k.Description is null||k.EffectText is null||k.Description.Length>1000||k.EffectText.Length>2000||k.Effects is null||k.Effects.Length>128)throw new InvalidDataException("词条字段无效");
        }
        foreach(var p in packs.Where(p=>p.IsExpansion&&p.BasePackId!.StartsWith("pack/diy-",StringComparison.Ordinal)))
            if(!packs.Any(b=>b.Id==p.BasePackId&&!b.IsExpansion))throw new InvalidDataException("扩展包的基础卡包不存在");
    }
    public static bool ValidPackId(string? id)=>id is not null && (id=="source" || (id.StartsWith("pack/diy-",StringComparison.Ordinal)
        ? Id(id,"pack/diy-") : id.StartsWith("pack/",StringComparison.Ordinal)&&DeckCardPacks.ValidSelection([id])));
    public void Save(DiyPackDraft pack)
    {
        if(LoadError is not null)throw new InvalidDataException(LoadError);
        var next=_packs.Where(p=>p.Id!=pack.Id).Append(pack.Snapshot()).ToArray();Validate(next);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);var temp=_path+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(new Storage(1,next),new JsonSerializerOptions{WriteIndented=true}));File.Move(temp,_path,true);_packs=next.ToList();
    }
    public IReadOnlyList<DiyPackDraft> VocabularyPacks(string id)
    {
        var pack=_packs.Single(p=>p.Id==id);var parent=pack.BasePackId is { } b?_packs.FirstOrDefault(p=>p.Id==b):null;
        return parent is null?[pack.Snapshot()]:[parent.Snapshot(),pack.Snapshot()];
    }
    public string SaveNationIcon(DiyNationDraft nation,byte[] png)
    {
        if(!Id(nation.Id,"nation-")||png.Length is 0 or >4_000_000)throw new InvalidDataException("国家图标无效或过大");
        var dir=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_path))!,"art");Directory.CreateDirectory(dir);
        var file=nation.Id+".png";var path=Path.Combine(dir,file);File.WriteAllBytes(path+".tmp",png);File.Move(path+".tmp",path,true);return file;
    }
}
