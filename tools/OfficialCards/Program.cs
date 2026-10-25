using System.Text.Json;
using System.Text.Json.Nodes;
using Kards.Ui.OrcBridge;
using Kards.Ui.Core;

// 仓库根：显式传入的现存目录优先，否则按可执行文件位置回退五级。
// 注意别把开关（`--probe`）当路径——那样会在读语料时抛 FileNotFound，看着像资源缺失。
var root = Path.GetFullPath(args.FirstOrDefault(a => Directory.Exists(a) && !a.StartsWith('-'))
    ?? Path.Combine(AppContext.BaseDirectory, "../../../../.."));
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
if(args.Contains("--probe"))
{
    // 单卡文本编译探针：拿原始语料里的中文规则文本走**当前**解析器，打印声明图与 csx，
    // 用来判定卡包里的既有 csx 是不是旧解析器产物（只读，不写卡包）。
    // --text "<规则文本>" 可直接编译任意句子，--type order|unit 指定卡型（默认 order）。
    var list=args.ToList();var index=list.IndexOf("--probe");
    var ids=list.Skip(index+1).Where(a=>!a.StartsWith("--")&&!a.StartsWith("\"")).ToArray();
    var textIndex=list.IndexOf("--text");
    var probeText=textIndex>=0&&textIndex+1<list.Count?list[textIndex+1]:null;
    var typeIndex=list.IndexOf("--type");
    var probeType=typeIndex>=0&&typeIndex+1<list.Count?list[typeIndex+1]:"order";
    using var raw=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"tests/fixtures/official-raw.json")));
    var byId=raw.RootElement.GetProperty("cards").EnumerateArray()
        .Where(c=>c.GetProperty("json").TryGetProperty("id",out var i)&&ids.Contains(i.GetString()))
        .Select(c=>c.GetProperty("json")).ToDictionary(c=>c.GetProperty("id").GetString()!);
    var report=new JsonObject();
    void Emit(string id,JsonElement card)
    {
        var type=card.GetProperty("type").GetString()!;
        var compiled=OfficialCardCompiler.Compile(card,"probe");
        var effects=new JsonArray();
        using var engine=JsonDocument.Parse(compiled.EngineJson);
        foreach(var component in engine.RootElement.GetProperty("components").EnumerateArray())
        {
            if(component.GetProperty("component").GetString()!="effects")continue;
            foreach(var inline in component.GetProperty("inline").EnumerateArray())
            {
                var trigger=inline.GetProperty("root").GetProperty("mainTrigger");
                var scripts=new JsonArray();
                foreach(var e in trigger.GetProperty("events").EnumerateArray())
                    scripts.Add(e.TryGetProperty("csx",out var csx)?csx.GetString():null);
                effects.Add(new JsonObject
                {
                    ["stableKey"]=trigger.GetProperty("stableKey").GetString(),
                    ["csx"]=scripts
                });
            }
        }
        report[id]=new JsonObject
        {
            ["text"]=compiled.Text,
            ["type"]=type,
            ["graphs"]=JsonNode.Parse(JsonSerializer.Serialize(compiled.Graphs)),
            ["csx"]=effects
        };
        Console.WriteLine($"PROBE_OK id={id} graphs={compiled.Graphs.Length} effects={effects.Count}");
    }
    foreach(var id in ids)
    {
        if(!byId.TryGetValue(id,out var card)){Console.WriteLine($"PROBE_MISS id={id}");continue;}
        Emit(id,card);
    }
    if(probeText is not null)
    {
        var synthetic=JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string,object?>
        {
            ["id"]="probe_text",["set"]="Base",["type"]=probeType,["faction"]="soviet",["kredits"]=1,
            ["rarity"]="standard",["title"]=new Dictionary<string,string>{["zh-Hans"]="探针"},
            ["text"]=new Dictionary<string,string>{["zh-Hans"]=probeText},
            ["attack"]=probeType=="order"?0:1,["defense"]=probeType=="order"?0:1,["operationCost"]=0,["attributes"]=Array.Empty<string>()
        }));
        Emit("__text__",synthetic.RootElement);
    }
    Directory.CreateDirectory(Path.Combine(root,"artifacts"));
    File.WriteAllText(Path.Combine(root,"artifacts/official-card-probe.json"),report.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine("PROBE_REPORT artifacts/official-card-probe.json");return;
}
var write = args.Contains("--write");
// --only a,b,c：定向重生成（配合 --refresh）。全量 --refresh 会顺带把语料里另外 200+ 张新卡塞进卡池，
// 那是产品级改动；修单张卡的行为时只该动那几张。
var onlyArgument=args.SkipWhile(a=>a!="--only").Skip(1).FirstOrDefault();
var only=onlyArgument is null?new HashSet<string>(StringComparer.Ordinal)
    :onlyArgument.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
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
            if(only.Count>0&&!only.Contains(entry.Name))continue;
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
    // --only 是显式白名单：即便这张卡已经在卡池里也要重编。
    // 卡池里有大量「有卡面文本、card.json 却连 effects 组件都没有」的白板卡（历史产物缺口），
    // 它们不在 desktop-effects.json 的受管表里，走 --refresh 永远轮不到——只能靠白名单强刷。
    if(only.Count>0){ if(!only.Contains(id))continue; }
    else if(existing.Contains(id))continue;
    if(!sets.TryGetValue(set,out var pack))continue;
    try { admitted.Add((set,OfficialCardCompiler.Compile(card,pack.Id))); }
    catch(Exception e) when(e is InvalidDataException or ArgumentException or InvalidOperationException or FormatException)
    { pending.Add(new {id,set,reason=e.Message}); }
}
var jsonOptions = new JsonSerializerOptions {WriteIndented=true};
if(write)
    foreach(var group in managed.GroupBy(m=>m.Directory))
    {
        // texts.json＝「按文本编译效果」的声明表。同一张卡一旦有了已编译效果（desktop-effects），
        // 两处同名会让 PackPresentationData 直接抛「同时声明了文本编译与已编译效果」。
        foreach(var name in new[]{"display-texts.json","desktop-effects.json","texts.json"})
        {
            var file=Path.Combine(group.Key,name);if(!File.Exists(file))continue;
            var data=JsonNode.Parse(File.ReadAllText(file))!.AsObject();
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
    var textCompilePath=Path.Combine(directory,"texts.json");
    var display=Read(Path.Combine(directory,"display-texts.json")); var graphs=Read(Path.Combine(directory,"desktop-effects.json"));
    var textCompile=Read(textCompilePath);
    foreach(var (_,card) in group)
    {
        File.WriteAllText(Path.Combine(directory,"cards",card.Id+".card.json"),card.EngineJson);
        display[card.Id]=card.Text;
        // 已编译效果进 desktop-effects，就必须撤掉同名的文本编译声明（否则装配期直接抛冲突）。
        textCompile.Remove(card.Id);
        // Preserve the exact graph bytes: native pre-play capture keys include a graph hash.
        graphs[card.Id]=JsonSerializer.SerializeToNode(card.Graphs);
    }
    File.WriteAllText(Path.Combine(directory,"display-texts.json"),display.ToJsonString(jsonOptions));
    File.WriteAllText(Path.Combine(directory,"desktop-effects.json"),graphs.ToJsonString(jsonOptions));
    if(File.Exists(textCompilePath))File.WriteAllText(textCompilePath,textCompile.ToJsonString(jsonOptions));
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
