using System.Text.Json;
using System.Text.Json.Nodes;
using Kards.Ui.OrcBridge;
using Kards.Ui.Core;

var root = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "../../../../.."));
if(args.Contains("--parser-deltas"))
{
    using var source=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"tests/fixtures/official-raw.json")));
    using var reference=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"tests/fixtures/desktop-orc-reference.json")));
    var rows=source.RootElement.GetProperty("cards").EnumerateArray().Select(c=>c.GetProperty("json"))
        .Where(c=>c.TryGetProperty("text",out var text)&&text.TryGetProperty("zh-Hans",out _)).Select(c=>new
        {id=c.GetProperty("id").GetString(),text=c.GetProperty("text").GetProperty("zh-Hans").GetString(),cardType=c.GetProperty("type").GetString() switch{"order"=>"order","countermeasure"=>"counter",_=>"unit"}}).ToArray();
    var full=DesktopOrc.Request(new {method="parityCorpus",rows});var deltas=new List<object>();var complete=0;
    for(var i=0;i<rows.Length;i++)
    {
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(full[i].GetRawText())));
        if(full[i].GetProperty("complete").GetBoolean())complete++;
        if(hash!=reference.RootElement[i].GetProperty("sha256").GetString())deltas.Add(new {rows[i].id,sha256=hash,rows[i].text,result=full[i]});
    }
    File.WriteAllText(Path.Combine(root,"artifacts/desktop-parser-delta-review.json"),JsonSerializer.Serialize(deltas,new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine($"PARSER_DELTA_REVIEW rows={rows.Length} changed={deltas.Count} complete={complete}");return;
}
if(args.Contains("--verify"))
{
    var catalog=new CardCatalog(); var source=Path.Combine(root,"proto/data/nations"); catalog.Load(source);
    var (cards, failures, descriptors)=OrcEngineHost.LoadPackCards(Path.Combine(root,"proto/data/kards"));
    if(failures.Count>0)throw new InvalidDataException(string.Join("；",failures));
    var count=await PackPlayabilityVerification.VerifyAsync(catalog.Cards,source);
    if(count!=cards.Count)throw new InvalidDataException("卡包装载和实战定义数不一致");
    var service=new DeckConstructionService(catalog.Cards,source);service.UpdatePacks(cards);
    Console.WriteLine($"POOL_COUNTS playable={service.PlayableCards.Count} verified={service.Report.Verified} packs={cards.Count} hidden={string.Join(',',service.Cards.Where(c=>service.PlayableIds.Contains(c.CardId)&&!service.PlayableCards.Contains(c)).Select(c=>c.CardId+":"+c.Rarity))}");
    if(service.Packs.Any(p=>descriptors.Any(d=>"pack/"+d.Id==p.Id&&d.BasePackId is not null)))throw new InvalidDataException("扩展包被错误暴露为可选卡包");
    Console.WriteLine($"OFFICIAL_NATIVE_VERIFY_OK cards={count} source-cards={service.Report.Verified} selectable-packs={service.Packs.Count} engine-effect-mounts exact-deck");
    return;
}
var write = args.Contains("--write");
var sets = new Dictionary<string,(string Id,string Name)>
{
    ["Base"]=("builtin","内置卡包"), ["Allegiance"]=("exp-loyalty","忠诚"), ["TheatersOfWar"]=("exp-warzone","战区"),
    ["Breakthrough"]=("exp-breakthrough","突破"), ["WorldAtWar"]=("exp-worldatwar","世界大战"), ["Legions"]=("exp-legions","军团"),
    ["BrothersInArms"]=("exp-comrades","战友"), ["WinterWar"]=("exp-winterwar","冬季战争"), ["CovertOps"]=("exp-covertops","秘密行动"),
    ["BloodAndIron"]=("exp-bloodandiron","血与铁"), ["NavalWarfare"]=("exp-navalwarfare","海战"), ["Homefront"]=("exp-homefront","国土阵线"),
    ["OceaniaStorm"]=("exp-oceaniastorm","澳新风暴")
};
var packs = Path.Combine(root,"proto/data/kards/packs");
var existing = Directory.GetFiles(packs,"*.card.json",SearchOption.AllDirectories)
    .Select(f=>Path.GetFileName(f)[..^10]).ToHashSet(StringComparer.Ordinal);
var managed = new List<(string Directory,string Id)>();
if(args.Contains("--refresh"))
    foreach(var file in Directory.GetFiles(packs,"desktop-effects.json",SearchOption.AllDirectories))
    {
        using var document=JsonDocument.Parse(File.ReadAllText(file));
        foreach(var entry in document.RootElement.EnumerateObject())
        {
            if(entry.Name.Length==0||entry.Name.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not '_' and not '-'))throw new InvalidDataException("生成卡编号无效");
            managed.Add((Path.GetDirectoryName(file)!,entry.Name));existing.Remove(entry.Name);
        }
    }
var archive=Path.Combine(root,"artifacts/kards-api-all-cards-20261012.json");
using var input = JsonDocument.Parse(File.ReadAllText(File.Exists(archive)?archive:Path.Combine(root,"tests/fixtures/official-raw.json")));
var records=input.RootElement.ValueKind==JsonValueKind.Array?input.RootElement:input.RootElement.GetProperty("cards");
var admitted = new List<(string Set, OfficialCompiledCard Card)>(); var pending = new List<object>();
foreach (var item in records.EnumerateArray())
{
    var card = item.GetProperty("json"); var id=card.GetProperty("id").GetString()!; var set=card.GetProperty("set").GetString()!;
    if(existing.Contains(id))continue;
    if(!sets.TryGetValue(set,out var pack))continue;
    try { admitted.Add((set,OfficialCardCompiler.Compile(card,pack.Id))); }
    catch(Exception e) when(e is InvalidDataException or ArgumentException or InvalidOperationException or FormatException)
    { pending.Add(new {id,set,reason=e.Message}); }
}
var jsonOptions = new JsonSerializerOptions {WriteIndented=true};
if(write)
    foreach(var group in managed.GroupBy(m=>m.Directory))
    {
        foreach(var name in new[]{"display-texts.json","desktop-effects.json"})
        {
            var file=Path.Combine(group.Key,name);var data=JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            foreach(var card in group)data.Remove(card.Id);
            File.WriteAllText(file,data.ToJsonString(jsonOptions));
        }
        foreach(var card in group)File.Delete(Path.Combine(group.Key,"cards",card.Id+".card.json"));
    }
foreach (var group in admitted.GroupBy(a=>a.Set))
{
    var pack=sets[group.Key]; var directory=Path.Combine(packs,pack.Id);
    Console.WriteLine($"{pack.Name}: +{group.Count()}");
    if(!write)continue;
    Directory.CreateDirectory(Path.Combine(directory,"cards"));
    var manifest=Path.Combine(directory,"manifest.json");
    if(!File.Exists(manifest))
    {
        var bytes=System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("OrC-KSD.official."+pack.Id));
        File.WriteAllText(manifest,JsonSerializer.Serialize(new {kind="cardpack",id=pack.Id,name=pack.Name,guid=new Guid(bytes[..16]).ToString(),version="1.0.0",
            dependsOn= new[]{new {id="builtin",version="1.*.*"}},assets=Array.Empty<object>()},jsonOptions));
    }
    JsonObject Read(string file)=>File.Exists(file)?JsonNode.Parse(File.ReadAllText(file))!.AsObject():new();
    var display=Read(Path.Combine(directory,"display-texts.json")); var graphs=Read(Path.Combine(directory,"desktop-effects.json"));
    foreach(var (_,card) in group)
    {
        File.WriteAllText(Path.Combine(directory,"cards",card.Id+".card.json"),card.EngineJson);
        display[card.Id]=card.Text;
        // Preserve the exact graph bytes: native pre-play capture keys include a graph hash.
        graphs[card.Id]=JsonSerializer.SerializeToNode(card.Graphs);
    }
    File.WriteAllText(Path.Combine(directory,"display-texts.json"),display.ToJsonString(jsonOptions));
    File.WriteAllText(Path.Combine(directory,"desktop-effects.json"),graphs.ToJsonString(jsonOptions));
}
Directory.CreateDirectory(Path.Combine(root,"artifacts"));
File.WriteAllText(Path.Combine(root,"artifacts/official-card-expansion.json"),JsonSerializer.Serialize(new {
    previous=existing.Count,added=admitted.Count,total=existing.Count+admitted.Count, written=write,
    cards=admitted.Select(a=>new {set=a.Set,a.Card.Id,a.Card.Name,a.Card.Text}),pending},jsonOptions));
Console.WriteLine($"OFFICIAL_EXPANSION_OK previous={existing.Count} added={admitted.Count} total={existing.Count+admitted.Count} written={write}");
if(write)
    File.WriteAllText(Path.Combine(root,"proto/data/kards/_report.json"),JsonSerializer.Serialize(new {
        shippedOfficialCards=Directory.GetFiles(packs,"*.card.json",SearchOption.AllDirectories).Length,
        mechanicImplementationRequired=pending.Count,
        note="Missing mechanics remain implementation work; omission from the playable pool is not completion.",
        packs=Directory.GetDirectories(packs).Where(d=>File.Exists(Path.Combine(d,"manifest.json")))
            .Select(d=>new {id=Path.GetFileName(d),cards=Directory.GetFiles(Path.Combine(d,"cards"),"*.card.json").Length})
    },jsonOptions));
