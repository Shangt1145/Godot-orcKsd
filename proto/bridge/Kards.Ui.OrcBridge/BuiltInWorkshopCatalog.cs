using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Orc.Packages;

namespace Kards.Ui.OrcBridge;

/// <summary>Editable local overlays over shipped declarations; original assets remain the baseline.</summary>
public static class BuiltInWorkshopCatalog
{
    public static IReadOnlyList<DiyPackDraft> Packs(DiyWorkshopLibrary library)
    {
        var packs = new List<DiyPackDraft> { new() { Id="source", Name="太空战争" } };
        packs.AddRange(OrcEngineHost.PackDescriptors.Select(p => new DiyPackDraft
        { Id="pack/"+p.Id, Name=p.Name, BasePackId=p.BasePackId is { } parent ? "pack/"+parent : null }));
        return packs.Concat(library.Packs).GroupBy(p=>p.Id,StringComparer.Ordinal).Select(g=>g.Last().Snapshot()).ToArray();
    }
    public static DiyCardDraft Edit(UiCardDefinition card, string packId, string sourceDirectory)
    {
        if (DeckCardPacks.RawOf(card.CardId)!=packId || card.CardId.StartsWith("diy/",StringComparison.Ordinal))
            throw new InvalidDataException("卡牌不属于此内置卡包");
        var graphs=Array.Empty<string>();var nativePrefabs=Array.Empty<string>(); var structured=true; JsonObject? extra=null;
        string Token(string id)
        {
            var words=DesktopOrc.Catalog.GetProperty("keywords");
            return DiyCardCompiler.KeywordToken(words.TryGetProperty(id,out var word)&&word.TryGetProperty("cn",out var label)?label.GetString()!:id);
        }
        var keywords=card.Keywords.Select(Token).ToList();
        var values=card.KeywordValues.ToDictionary(p=>Token(p.Key),p=>p.Value);
        if (packId=="source")
        {
            JsonElement? record=null;
            foreach(var file in Directory.EnumerateFiles(sourceDirectory,"*.json"))
            {
                using var doc=JsonDocument.Parse(File.ReadAllText(file));
                if(doc.RootElement.TryGetProperty("cards",out var entries)&&entries.TryGetProperty(card.CardId,out var found))
                {record=found.Clone();break;}
            }
            if(record is not { } raw) throw new InvalidDataException("找不到随附卡牌的原始声明");
            if(raw.TryGetProperty("effects",out var effects)) graphs=effects.EnumerateArray().Select(e=>e.GetRawText()).ToArray();
            if(raw.TryGetProperty("kwMap",out var map))
                foreach(var keyword in map.EnumerateObject().Where(p=>p.Value.ValueKind==JsonValueKind.True))keywords.Add(Token(keyword.Name));
            if(raw.TryGetProperty("kwValues",out var parameters))
                foreach(var value in parameters.EnumerateObject())
                    if(value.Value.TryGetInt32(out var number))values[Token(value.Name)]=number;
            // Retain semantic fields outside the editable surface; admission rejects unsupported
            // fields explicitly rather than silently dropping them during an ordinary stat edit.
            var metadata=new HashSet<string>(StringComparer.Ordinal) { "id","name","nameEn","set","nation","sub","cardType","unitType","cost","attack","defense","opCost","keywords","kwMap","kwValues","keywordsRaw","rarity","text","flavor","effects","art","src","file","custom","adopted","adoptedAt","compiled","overlayFields","confidence","notes","effectStatus","compileWarnings","token","variableStats" };
            foreach(var field in raw.EnumerateObject().Where(p=>!metadata.Contains(p.Name)))
            {extra??=new();extra[field.Name]=JsonNode.Parse(field.Value.GetRawText());}
        }
        else
        {
            var package=OrcEngineHost.Snapshot().CardPacks.Single(p=>"pack/"+p.Manifest.Id==packId);
            var content=OrcEngineHost.LoadPackContent(package.RootDirectory,package.Manifest);
            graphs=PackPresentationData.Plans(content).GetValueOrDefault(card.CardId)??[];
            if(graphs.Length==0&&content.Declarations.Any(d=>d.CardId==card.CardId&&d.PrefabIds.Count>0))
            {
                var prefabIds=content.Declarations.Where(d=>d.CardId==card.CardId).SelectMany(d=>d.PrefabIds).ToHashSet(StringComparer.Ordinal);
                nativePrefabs=content.InlinePrefabs.Where(p=>prefabIds.Contains(p.Root.Id)).Select(Orc.Cards.PrefabJson.SerializeForDisk).ToArray();
                if(nativePrefabs.Length!=prefabIds.Count)
                {
                    if(card.Text.Length==0){extra??=new();extra["unavailableNativeEffects"]=true;}
                    else structured=false;
                }
            }
        }
        if(card.IsToken) {extra??=new();extra["token"]=true;}
        if(card.HasVariableStats) {extra??=new();extra["variableStats"]=true;}
        return new DiyCardDraft
        {
            Id="diy/edit-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(card.CardId)))[..32].ToLowerInvariant(),
            SourceCardId=card.CardId, PackId=packId, Name=card.Name, Nation=card.Set,
            Rarity=card.Rarity switch { "standard" or "common"=>"iron", "limited"=>"bronze", "special"=>"silver", "elite"=>"gold", var rarity=>rarity },
            CardType=card.CardType, UnitType=card.UnitType.Length>0?card.UnitType:"infantry", Cost=card.Cost??0,
            OpCost=card.BaseOpCost??0, Attack=card.BaseAttack??0, Defense=Math.Max(1,card.BaseDefense??1),
            Keywords=keywords.Distinct(StringComparer.Ordinal).ToArray(),
            KeywordValues=values.Where(p=>keywords.Contains(p.Key)).ToDictionary(p=>p.Key,p=>p.Value),
            Armor=card.KeywordValues.GetValueOrDefault("armor",card.KeywordValues.GetValueOrDefault("重甲",1)),
            ArtCardId=card.CardId, EffectText=card.Text, Flavor=card.Flavor, UseStructuredEffects=structured,
            OrcEffects=graphs.Select(g=>new OrcEffectDraft {Template="original",DesktopJson=g}).Concat(nativePrefabs.Select(p=>new OrcEffectDraft{Template="native-prefab",PrefabJson=p})).ToArray(), DesktopCardJson=extra?.ToJsonString()
        };
    }
}
