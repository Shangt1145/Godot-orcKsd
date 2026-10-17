using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Orc.Cards;
using Orc.Game.Cards;

namespace Kards.Ui.OrcBridge;

public static class WorkshopAuthoring
{
    private static IReadOnlyList<DiyPackDraft> _packs=[];
    private static IReadOnlyDictionary<string,string> _keywords=new Dictionary<string,string>();
    public static IReadOnlyDictionary<string,string> Errors {get;private set;}=new Dictionary<string,string>();
    public static IReadOnlyList<DiyNationDraft> Nations=>_packs.SelectMany(p=>p.Nations).ToArray();
    public static IReadOnlyList<UiDiyChoice> KeywordChoices=>_packs.SelectMany(p=>p.Keywords).Select(k=>new UiDiyChoice(k.Id,k.Name)).ToArray();
    public static IReadOnlyDictionary<string,string> PackNames=>_packs.ToDictionary(p=>p.Id,p=>p.Name);
    /// <summary>工坊包清单（收藏页磁贴/来源筛选消费；Id 保留 pack/ 前缀，CardCount 按当前 DIY 卡归属统计）。</summary>
    public static IReadOnlyList<PackDescriptor> AuthoredPacks {get;private set;}=[];
    public static bool TryKeyword(string id,out string engine)=>_keywords.TryGetValue(id,out engine!);
    public static string KeywordLabel(string id)=>_packs.SelectMany(p=>p.Keywords).FirstOrDefault(k=>k.Id==id||_keywords.GetValueOrDefault(k.Id)==id)?.Name??id;
    public static string NationLabel(string id)=>Nations.FirstOrDefault(n=>n.Id==id)?.Name??id;
    public static DiyKeywordDraft? Keyword(string id)=>_packs.SelectMany(p=>p.Keywords).FirstOrDefault(k=>k.Id==id);
    public static int ParameterDefault(string id)
    {try{return Keyword(id)?.ProgramJson is { } json?KeywordProgram.Parse(json).DefaultValue:1;}catch(InvalidDataException){return 1;}}
    public static string? ValidateKeyword(DiyKeywordDraft keyword)
    {
        try{if(keyword.ProgramJson is { } program)KeywordProgram.Parse(program);else CompileKeyword(keyword);return null;}catch(Exception e)when(e is InvalidDataException or InvalidOperationException or ArgumentException or JsonException){return e.Message;}
    }
    private static EffectSnapshot[] CompileKeyword(DiyKeywordDraft keyword)
    {
        var effects=keyword.Effects;
        if(effects.Length==0)
        {
            var parsed=OrcInspector.CompileText(keyword.Id,keyword.EffectText,"unit");
            if(!parsed.ParseComplete||!parsed.AllCompiled)throw new InvalidDataException(string.Join("；",parsed.Problems));
            effects=parsed.Parse.Effects.Select(OrcEffectEditing.ToDraft).ToArray();
        }
        var compiled=OrcEffectEditing.Compile(keyword.Id,effects);
        if(compiled.Any(e=>e.Error is not null))throw new InvalidDataException(string.Join("；",compiled.Where(e=>e.Error is not null).Select(e=>e.Error)));
        var snapshots=compiled.SelectMany(e=>e.Snapshots).ToArray();if(snapshots.Length==0)throw new InvalidDataException("请为词条填写可执行的效果");return snapshots;
    }
    public static void Configure(DiyWorkshopLibrary library,IReadOnlyList<DiyCardDraft> cards)
    {
        var packs=library.Packs;var keywords=new Dictionary<string,string>();var errors=new Dictionary<string,string>();
        var ids=packs.SelectMany(p=>p.Keywords).ToDictionary(k=>k.Id,k=>k.Id+"."+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(k))))[..16].ToLowerInvariant());
        foreach(var n in packs.SelectMany(p=>p.Nations))Faction.RegisterCustom(n.Id);
        foreach(var k in packs.SelectMany(p=>p.Keywords))
        {
            try{
            var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(k))))[..16].ToLowerInvariant();
            var engineId=k.Id+"."+hash;
            if(k.ProgramJson is { } program)
            {
                var definition=KeywordProgram.Parse(program);
                string BindKeyword(string key)=>key.Length==0?"":ids.TryGetValue(key,out var authored)?authored:KeywordRegistry.IsDefined(key)?key:DiyCardCompiler.EngineKeywordId(key);
                KeywordProgramAction[] Bind(KeywordProgramAction[] actions)=>actions.Select(a=>a with{Key=a.Op is "keyword.grant" or "keyword.remove"?BindKeyword(a.Key):a.Key,Then=Bind(a.Then),Else=Bind(a.Else)}).ToArray();
                definition=definition with{Handlers=definition.Handlers.Select(h=>h with{Actions=Bind(h.Actions)}).ToArray(),Skills=definition.Skills.Select(s=>s with{Actions=Bind(s.Actions)}).ToArray()};
                ProgramKeywordComponent.Register(engineId,definition,k.Battle);
            }
            else DeclarativeKeywordComponent.Register(engineId,CompileKeyword(k),k.Battle);
            keywords[k.Id]=engineId;
            }catch(Exception e)when(e is InvalidDataException or InvalidOperationException or ArgumentException or JsonException){errors[k.Id]=e.Message;}
        }
        _packs=packs;_keywords=keywords;Errors=errors;
        AuthoredPacks=packs.Select(p=>new PackDescriptor(p.Id,p.Name,p.IsExpansion?p.BasePackId:null,cards.Count(c=>c.PackId==p.Id))).ToArray();
        DeckCardPacks.ConfigureAuthored(cards.Where(c=>c.PackId.Length>0).ToDictionary(c=>c.Id,c=>c.PackId),packs.Where(p=>p.IsExpansion).ToDictionary(p=>p.Id,p=>p.BasePackId!));
        ConstructionRules.ConfigureCustomNations(Nations.Select(n=>n.Id));
    }
    public static string? CheckScope(DiyCardDraft draft)
    {
        if(draft.PackId.Length==0)return null;
        var pack=_packs.FirstOrDefault(p=>p.Id==draft.PackId);if(pack is null)return "卡牌所属卡包不存在";
        var vocabulary=_packs.Where(p=>p.Id==pack.Id||p.Id==pack.BasePackId).ToArray();
        if(draft.Nation.StartsWith("nation-",StringComparison.Ordinal)&&!vocabulary.SelectMany(p=>p.Nations).Any(n=>n.Id==draft.Nation))return "此国家不属于当前卡包或基础卡包";
        if(draft.Keywords.Any(k=>k.StartsWith("kw-",StringComparison.Ordinal)&&!vocabulary.SelectMany(p=>p.Keywords).Any(v=>v.Id==k)))return "词条不属于当前卡包或基础卡包";
        return null;
    }
    public static IReadOnlyList<UiDiyChoice> NationsFor(string packId)=>DiyCardCompiler.BuiltInNations.Concat(_packs.Where(p=>p.Id==packId||_packs.Any(e=>e.Id==packId&&e.BasePackId==p.Id)).SelectMany(p=>p.Nations).Select(n=>new UiDiyChoice(n.Id,n.Name))).ToArray();
    public static IReadOnlyList<UiDiyChoice> KeywordsFor(string packId)=>_packs.Where(p=>p.Id==packId||_packs.Any(e=>e.Id==packId&&e.BasePackId==p.Id)).SelectMany(p=>p.Keywords).Select(k=>new UiDiyChoice(k.Id,k.Name)).ToArray();
    public static string[] KeywordGraphs(DiyCardDraft draft)=>_packs.SelectMany(p=>p.Keywords)
        // A native program is authoritative. Its description and retained legacy editor fields
        // must never register another pre-play selector alongside that program.
        .Where(k=>draft.Keywords.Contains(k.Id)&&k.ProgramJson is null).SelectMany(k=>k.Effects.Length>0
        ?k.Effects.Where(e=>e.DesktopJson is not null).Select(e=>e.DesktopJson!)
        :OrcInspector.CompileText(k.Id,k.EffectText,"unit").Parse.Effects.Where(e=>e.DesktopJson is not null).Select(e=>e.DesktopJson!)).ToArray();
}
