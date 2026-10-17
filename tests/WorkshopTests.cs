using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Orc.Game.Cards;
using Xunit;

namespace Kards.Ui.Tests;

[CollectionDefinition("Workshop",DisableParallelization=true)]
public class WorkshopCollection { }
[Collection("Workshop")]
public sealed class WorkshopTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"OrC Workshop "+Guid.NewGuid().ToString("N"));
    private const string Source=@"H:\Working Folder\OrC-KSD.Godot\proto\data\nations";
    private readonly DiyWorkshopLibrary store;
    private readonly DiyCardLibrary cards;
    private readonly CardCatalog catalog=new();
    public WorkshopTests(){Directory.CreateDirectory(root);store=new(Path.Combine(root,"packs.json"));cards=new(Path.Combine(root,"cards.json"));catalog.Load(Source);}
    [Fact]
    public void PackExpansionAndVocabularyPersistWithoutAliasing()
    {
        var nation=new DiyNationDraft{Name="测试国家"};var keyword=new DiyKeywordDraft{Name="成长",EffectText="友方回合开始时，获得 +1+1。"};
        var pack=new DiyPackDraft{Name="基础包",Nations=[nation],Keywords=[keyword]};store.Save(pack);
        var expansion=new DiyPackDraft{Name="扩展",BasePackId=pack.Id};store.Save(expansion);
        var loaded=new DiyWorkshopLibrary(Path.Combine(root,"packs.json"));Assert.Null(loaded.LoadError);Assert.Equal(2,loaded.Packs.Count);
        Assert.Equal(keyword.Id,loaded.VocabularyPacks(expansion.Id).First().Keywords.Single().Id);
        pack.Keywords[0]=new(){Name="不会写回"};Assert.Equal("成长",loaded.Packs.First().Keywords.Single().Name);
    }
    [Fact]
    public void ExpansionCannotDependOnItselfAnotherExpansionOrMissingCustomBase()
    {
        var pack=new DiyPackDraft{Name="基础包"};store.Save(pack);var expansion=new DiyPackDraft{BasePackId=pack.Id};store.Save(expansion);
        Assert.Throws<InvalidDataException>(()=>store.Save(new(){BasePackId=expansion.Id}));
        Assert.Throws<InvalidDataException>(()=>store.Save(pack with{BasePackId=pack.Id}));
        Assert.Throws<InvalidDataException>(()=>store.Save(new(){BasePackId="pack/diy-missing"}));
        Assert.Throws<InvalidDataException>(()=>store.Save(expansion with{Keywords=[new(){EffectText="部署：抽1张牌。"}]}));
    }
    [Fact]
    public void MalformedWorkshopFileIsPreservedAndNotOverwritten()
    {
        var path=Path.Combine(root,"bad.json");File.WriteAllText(path,"{\"Schema\":1,\"Packs\":[null]}");var invalid=new DiyWorkshopLibrary(path);
        Assert.NotNull(invalid.LoadError);Assert.Throws<InvalidDataException>(()=>invalid.Save(new()));Assert.Contains("null",File.ReadAllText(path));
    }
    [Fact]
    public void CardCropAndOriginalSourceRoundTripWithPackMembership()
    {
        var pack=new DiyPackDraft();store.Save(pack);var draft=new DiyCardDraft{PackId=pack.Id,ArtSourceFile="sample-source.png",ArtImportMode="card",ArtRegion=new(.03f,.14f,.94f,.49f)};
        cards.Save(draft);var reloaded=new DiyCardLibrary(Path.Combine(root,"cards.json")).Cards.Single();Assert.Equal(draft,reloaded);
        var imported=DiyCardLibrary.Import(DiyCardLibrary.Export(draft));Assert.Equal(draft.PackId,imported.PackId);Assert.Equal(draft.ArtRegion,imported.ArtRegion);Assert.NotEqual(draft.Id,imported.Id);
        Assert.Throws<InvalidDataException>(()=>cards.Save(draft with{ArtRegion=new(-1,0,1,1)}));
    }
    private (DiyPackDraft Pack,DiyKeywordDraft Keyword,DiyNationDraft Nation,DiyCardDraft Card) Authored()
    {
        var k=new DiyKeywordDraft{Name="部署强化",Description="部署时获得攻防",EffectText="部署：获得 +1+2。"};var n=new DiyNationDraft{Name="新联邦"};
        var p=new DiyPackDraft{Name="联邦卡包",Keywords=[k],Nations=[n]};store.Save(p);
        var card=new DiyCardDraft{PackId=p.Id,Name="测试步兵",Nation=n.Id,Keywords=[k.Id]};cards.Save(card);WorkshopAuthoring.Configure(store,cards.Cards);return(p,k,n,card);
    }
    [Fact]
    public void CustomNationAndKeywordCompileIntoActualNativeDefinitions()
    {
        var (_,k,n,card)=Authored();var result=DiyCardCompiler.Validate(card,catalog.Cards);Assert.True(result.Success,string.Join(';',result.Errors));
        Assert.True(Faction.IsDefined(n.Id));Assert.True(FactionRules.IsMajorFaction(n.Id));Assert.True(WorkshopAuthoring.TryKeyword(k.Id,out var engineId));Assert.Contains(engineId,result.EngineCardJson);
        Assert.Contains(n.Id,result.EngineCardJson);Assert.Equal(n.Id,result.Card!.Set);
        var record=InspectorCardRecord.FromDraft(card);using var json=System.Text.Json.JsonDocument.Parse(record.ToJsonString());
        var inspector=InspectorCardRecord.ApplyRecord(card,json.RootElement);Assert.True(DiyCardCompiler.Validate(inspector,catalog.Cards).Success);
    }
    [Fact]
    public void ExpansionInheritsVocabularyButOtherPacksCannotUseIt()
    {
        var (p,k,n,card)=Authored();var expansion=new DiyPackDraft{BasePackId=p.Id};store.Save(expansion);var another=new DiyPackDraft();store.Save(another);WorkshopAuthoring.Configure(store,cards.Cards);
        Assert.True(DiyCardCompiler.Validate(card with{PackId=expansion.Id},catalog.Cards).Success);
        Assert.False(DiyCardCompiler.Validate(card with{PackId=another.Id},catalog.Cards).Success);
        Assert.Contains(WorkshopAuthoring.NationsFor(expansion.Id),v=>v.Id==n.Id);Assert.Contains(WorkshopAuthoring.KeywordsFor(expansion.Id),v=>v.Id==k.Id);
    }
    [Fact]
    public void ParentDeckSelectionIncludesItsExpansionCardsAndPreservesIds()
    {
        var (pack,_,_,card)=Authored();var expansion=new DiyPackDraft{BasePackId=pack.Id};store.Save(expansion);var child=card with{Id="diy/"+Guid.NewGuid().ToString("N"),PackId=expansion.Id};cards.Save(child);WorkshopAuthoring.Configure(store,cards.Cards);
        var service=new DeckConstructionService(catalog.Cards,Source);service.UpdateCustom(cards.Cards);var deck=new ConstructedDeck{PackIds=[pack.Id]};
        Assert.Equal(2,service.Pool(deck).Count);Assert.Single(service.Packs.Where(p=>p.Id==pack.Id));Assert.DoesNotContain(service.Packs,p=>p.Id==expansion.Id);
        Assert.Equal(pack.Id,DeckCardPacks.Of(child.Id));Assert.Equal(card.Id,cards.Cards.First().Id);
    }
    [Fact]
    public async Task AuthoredKeywordActuallyExecutesOnDeploymentWithTheCustomNation()
    {
        var (_,_,n,card)=Authored();using var session=await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards,31,"workshop-test","unused",sourceDirectory:Source,openingHand:[card.Id],customCards:[card]);
        session.DriveOpponent=false;await session.SettleMulliganAsync(false);
        var actor=session.View.SelfHand.Single(c=>c.CardId==card.Id);Assert.Equal(card.Attack,actor.EffectiveAttack);
        Assert.Equal(UiSubmitOutcome.Applied,await session.SubmitAsync(new PlayCard(actor.Uid)));session.SettleProjection();
        var unit=session.View.SelfLine.Single(c=>c.Uid==actor.Uid);Assert.Equal(card.Attack+1,unit.EffectiveAttack);Assert.Equal(card.Defense+2,unit.EffectiveDefense);
        Assert.Equal(n.Id,((CardBase)session.Host.Index[actor.Uid]).Definition.Faction);
    }
    [Fact]
    public void InvalidKeywordIsDiagnosedWithoutBreakingOtherPacks()
    {
        store.Save(new(){Keywords=[new(){EffectText="未定义效果 这不是可执行规则"}]});WorkshopAuthoring.Configure(store,[]);Assert.Single(WorkshopAuthoring.Errors);
        Assert.NotNull(WorkshopAuthoring.ValidateKeyword(new()));
    }
    [Fact]
    public void LegacyDeckAndCodeSelectionStillAllowsMigratedCardsOnly()
    {
        var legacy=new DiyPackDraft{Id="pack/diy-legacy"};var other=new DiyPackDraft();store.Save(legacy);store.Save(other);
        var oldCard=new DiyCardDraft{PackId=legacy.Id};var newCard=new DiyCardDraft{PackId=other.Id};cards.Save(oldCard);cards.Save(newCard);
        WorkshopAuthoring.Configure(store,cards.Cards);
        var deck=new ConstructedDeck{CardIds=[oldCard.Id],PackIds=["diy"]};
        Assert.True(DeckCardPacks.Allows(deck,oldCard.Id));Assert.False(DeckCardPacks.Allows(deck,newCard.Id));
        Assert.Equal(new[]{legacy.Id},DeckCardPacks.Selected(deck));
    }
    public void Dispose()
    {
        WorkshopAuthoring.Configure(new DiyWorkshopLibrary(Path.Combine(root,"empty.json")),[]);
        var full=Path.GetFullPath(root);if(!full.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))throw new IOException("Unexpected test workspace");Directory.Delete(full,true);
    }
}
