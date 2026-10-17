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
        cards.Save(draft);var reloaded=new DiyCardLibrary(Path.Combine(root,"cards.json")).Cards.Single();Assert.Equivalent(draft,reloaded);
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
    public async Task NativeRuleProgramAndCardParameterSurviveDiskAndNativeJsonThenDeploy()
    {
        var program=new KeywordProgram{DefaultValue=3,Handlers=[new(){Actions=[new(){Op="move.set",Value="1"},new(){Op="attack.set",Value="0"}]}]};
        var keyword=new DiyKeywordDraft{Name="突进",ProgramJson=program.ToJson()};var pack=new DiyPackDraft{Keywords=[keyword]};store.Save(pack);
        var card=new DiyCardDraft{PackId=pack.Id,Cost=0,Keywords=[keyword.Id],KeywordValues=new(){{keyword.Id,7}}};cards.Save(card);WorkshopAuthoring.Configure(store,cards.Cards);
        var loaded=new DiyWorkshopLibrary(Path.Combine(root,"packs.json"));Assert.Equal(program.ToJson(),loaded.Packs.Single().Keywords.Single().ProgramJson);
        var result=DiyCardCompiler.Validate(card,catalog.Cards);Assert.True(result.Success,string.Join(';',result.Errors));
        Assert.Contains(":7",result.EngineCardJson);Assert.True(WorkshopAuthoring.TryKeyword(keyword.Id,out var engineId));
        using var session=await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards,31,"program-bridge","unused",sourceDirectory:Source,openingHand:[card.Id],customCards:[card]);
        session.DriveOpponent=false;await session.SettleMulliganAsync(false);var actor=session.View.SelfHand.Single(c=>c.CardId==card.Id);
        Assert.Equal(UiSubmitOutcome.Applied,await session.SubmitAsync(new PlayCard(actor.Uid)));session.SettleProjection();
        var unit=(CardBase)session.Host.Index[actor.Uid];Assert.Equal(7,unit.Keywords.GetValue(engineId));Assert.True(unit.GetData<CommandData>().CanMove);Assert.False(unit.GetData<CommandData>().CanAttack);
    }
    [Fact]
    public async Task BridgeEnumeratesSkillsAndUseKeywordSkillAppliesThenRejectsSpentOpportunity()
    {
        var program=new KeywordProgram{Handlers=[new(){Actions=[new(){Op="attack.set",Value="1"}]}],Skills=[new(){Id="strike",Name="重击",Cost="0",SpendAttack=true,SpendMove=false,Actions=[new(){Op="buff",Value="2"}]}]};
        var keyword=new DiyKeywordDraft{Name="突击",ProgramJson=program.ToJson()};var pack=new DiyPackDraft{Keywords=[keyword]};store.Save(pack);
        var card=new DiyCardDraft{PackId=pack.Id,Cost=0,Keywords=[keyword.Id]};cards.Save(card);WorkshopAuthoring.Configure(store,cards.Cards);
        using var session=await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards,31,"skill-bridge","unused",sourceDirectory:Source,openingHand:[card.Id],customCards:[card]);
        session.DriveOpponent=false;await session.SettleMulliganAsync(false);var actor=session.View.SelfHand.Single(c=>c.CardId==card.Id);
        Assert.Equal(UiSubmitOutcome.Applied,await session.SubmitAsync(new PlayCard(actor.Uid)));session.SettleProjection();
        var skill=Assert.Single(session.KeywordSkills(actor.Uid));
        Assert.True(WorkshopAuthoring.TryKeyword(keyword.Id,out var engineId));Assert.Equal(engineId,skill.KeywordId);Assert.Equal("重击",skill.Name);Assert.True(skill.CanUse,$"技能应可用：{skill.State}");Assert.Equal(0,skill.Cost);
        Assert.Equal(UiSubmitOutcome.Applied,await session.SubmitAsync(new UseKeywordSkill(actor.Uid,engineId,"strike")));session.SettleProjection();
        Assert.Equal(card.Attack+2,session.View.SelfLine.Single(c=>c.Uid==actor.Uid).EffectiveAttack);
        Assert.Equal(UiSubmitOutcome.Rejected,await session.SubmitAsync(new UseKeywordSkill(actor.Uid,engineId,"strike")));
        Assert.Equal(card.Attack+2,session.View.SelfLine.Single(c=>c.Uid==actor.Uid).EffectiveAttack);
    }
    [Fact]
    public async Task UseKeywordSkillRejectsUnknownCardAndUnaffordableSkillWithoutSideEffects()
    {
        var program=new KeywordProgram{Skills=[new(){Id="grand",Name="大招",Cost="10000",SpendAttack=false,SpendMove=false,Actions=[new(){Op="buff",Value="2"}]}]};
        var keyword=new DiyKeywordDraft{Name="豪掷",ProgramJson=program.ToJson()};var pack=new DiyPackDraft{Keywords=[keyword]};store.Save(pack);
        var card=new DiyCardDraft{PackId=pack.Id,Cost=0,Keywords=[keyword.Id]};cards.Save(card);WorkshopAuthoring.Configure(store,cards.Cards);
        using var session=await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards,31,"skill-guard","unused",sourceDirectory:Source,openingHand:[card.Id],customCards:[card]);
        session.DriveOpponent=false;await session.SettleMulliganAsync(false);var actor=session.View.SelfHand.Single(c=>c.CardId==card.Id);
        Assert.Equal(UiSubmitOutcome.Applied,await session.SubmitAsync(new PlayCard(actor.Uid)));session.SettleProjection();
        Assert.Equal(UiSubmitOutcome.UnknownCard,await session.SubmitAsync(new UseKeywordSkill("uid/nothing",keyword.Id,"grand")));
        var listed=Assert.Single(session.KeywordSkills(actor.Uid));Assert.False(listed.CanUse);Assert.Equal(10000,listed.Cost);
        var points=session.Host.Match.Players[0].Points;
        Assert.True(WorkshopAuthoring.TryKeyword(keyword.Id,out var engineId));
        Assert.Equal(UiSubmitOutcome.Rejected,await session.SubmitAsync(new UseKeywordSkill(actor.Uid,engineId,"grand")));
        Assert.Equal(points,session.Host.Match.Players[0].Points);
        Assert.Equal(card.Attack,session.View.SelfLine.Single(c=>c.Uid==actor.Uid).EffectiveAttack);
    }
    [Fact]
    public async Task EffectAttackIsRealCombatWithTriggersAndProgramReplaceSwapsTheUnitInPlace()
    {
        var pack=new DiyPackDraft();store.Save(pack);
        var body=new DiyCardDraft{PackId=pack.Id,Cost=0,Name="替换体",Attack=1,Defense=1};cards.Save(body);
        var program=new KeywordProgram{State=new(){{"hits",0}},Handlers=[
            new(){Actions=[new(){Op="attack.set",Value="1"}]},
            new(){Event="unit.attack.after",Actions=[new(){Op="state.add",Value="1",Key="hits"}]}],Skills=[
            new(){Id="strike-hq",Name="直击总部",Cost="0",SpendAttack=true,SpendMove=false,Actions=[new(){Op="attack",Target="enemy.hq",BypassRange=true}]},
            new(){Id="morph",Name="变形",Cost="0",SpendAttack=false,SpendMove=false,Actions=[new(){Op="replace",Target="self",Key=body.Id}]}]};
        var keyword=new DiyKeywordDraft{Name="突袭形态",ProgramJson=program.ToJson()};var pack2=pack with{Keywords=[keyword]};store.Save(pack2);
        var card=new DiyCardDraft{PackId=pack.Id,Cost=0,Keywords=[keyword.Id]};cards.Save(card);WorkshopAuthoring.Configure(store,cards.Cards);
        using var session=await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards,31,"combat-bridge","unused",sourceDirectory:Source,openingHand:[card.Id],customCards:[card,body]);
        session.DriveOpponent=false;await session.SettleMulliganAsync(false);var actor=session.View.SelfHand.Single(c=>c.CardId==card.Id);
        Assert.Equal(UiSubmitOutcome.Applied,await session.SubmitAsync(new PlayCard(actor.Uid)));session.SettleProjection();
        Assert.True(WorkshopAuthoring.TryKeyword(keyword.Id,out var engineId));
        var before=session.Host.Match.Players[1].Hq.Health;
        Assert.Equal(UiSubmitOutcome.Applied,await session.SubmitAsync(new UseKeywordSkill(actor.Uid,engineId,"strike-hq")));session.SettleProjection();
        Assert.Equal(before-card.Attack,session.Host.Match.Players[1].Hq.Health);
        var state=session.KeywordSkills(actor.Uid).Single(s=>s.SkillId=="morph").State;
        Assert.Contains("hits 1",state);
        Assert.Equal(UiSubmitOutcome.Applied,await session.SubmitAsync(new UseKeywordSkill(actor.Uid,engineId,"morph")));session.SettleProjection();
        Assert.DoesNotContain(session.View.SelfLine,c=>c.Uid==actor.Uid);
        var morphed=session.View.SelfLine.Single(c=>c.CardId==body.Id);
        Assert.Equal(body.Defense,morphed.EffectiveDefense);
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
