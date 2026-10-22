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
    public WorkshopTests()
    {
        Directory.CreateDirectory(root);store=new(Path.Combine(root,"packs.json"));cards=new(Path.Combine(root,"cards.json"));catalog.Load(Source);
        // 内置词条注册是进程级的；前一用例已注册的词条必须仍能在本局快照中找到效果库。
        OrcEngineHost.Reset();OrcEngineHost.WarmUp(Path.Combine(Path.GetDirectoryName(Source)!,"kards"));
    }
    [Fact]
    public void DiyNationChoicesCoverTheNativeFactionCatalogAndCompile()
    {
        foreach(var faction in Faction.All)
        {
            Assert.Contains(DiyCardCompiler.BuiltInNations,n=>n.Id==faction);
            var result=DiyCardCompiler.Validate(new(){Nation=faction},catalog.Cards);
            Assert.True(result.Success,faction+": "+string.Join(';',result.Errors));
            Assert.Contains(faction,result.EngineCardJson);
        }
        foreach(var legacy in new[]{"av76","deran","USG","UN"})
            Assert.True(DiyCardCompiler.Validate(new(){Nation=legacy},catalog.Cards).Success);
    }
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
    public void CollectionTilesSurfaceWorkshopPacksBySourceMapping()
    {
        var (pack,_,_,card)=Authored();var expansion=new DiyPackDraft{BasePackId=pack.Id};store.Save(expansion);
        var child=card with{Id="diy/"+Guid.NewGuid().ToString("N"),PackId=expansion.Id};cards.Save(child);WorkshopAuthoring.Configure(store,cards.Cards);
        var authored=WorkshopAuthoring.AuthoredPacks;
        Assert.Equal(pack.Name,authored.Single(p=>p.Id==pack.Id).Name);
        Assert.Equal(1,authored.Single(p=>p.Id==pack.Id).CardCount);Assert.Equal(1,authored.Single(p=>p.Id==expansion.Id).CardCount);
        Assert.Equal(expansion.Id,DeckCardPacks.RawOf(child.Id));Assert.Equal(pack.Id,DeckCardPacks.Family(DeckCardPacks.RawOf(child.Id)));
    }
    [Fact]
    public async Task AuthoredKeywordActuallyExecutesOnDeploymentWithTheCustomNation()
    {
        var (_,_,n,card)=Authored();using var session=await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards,31,"workshop-test","unused",sourceDirectory:Source,openingHand:[card.Id],customCards:[card], firstPlayerIndex: 0);
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
        using var session=await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards,31,"program-bridge","unused",sourceDirectory:Source,openingHand:[card.Id],customCards:[card], firstPlayerIndex: 0);
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
        using var session=await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards,31,"skill-bridge","unused",sourceDirectory:Source,openingHand:[card.Id],customCards:[card], firstPlayerIndex: 0);
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
        using var session=await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards,31,"skill-guard","unused",sourceDirectory:Source,openingHand:[card.Id],customCards:[card], firstPlayerIndex: 0);
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
        using var session=await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards,31,"combat-bridge","unused",sourceDirectory:Source,openingHand:[card.Id],customCards:[card,body], firstPlayerIndex: 0);
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
    public async Task TargetedDeploymentUsesChosenGapBeforeAskingForEffectTargetAndCanRetry()
    {
        var keyword = new DiyKeywordDraft { Name = "支援部署", ProgramJson = new KeywordProgram
        { Handlers = [new() { Actions = [new() { Op = "buff", Target = "choose.friendly.unit", Value = "2" }] }] }.ToJson() };
        var pack = new DiyPackDraft { Keywords = [keyword] }; store.Save(pack);
        var target = new DiyCardDraft { PackId = pack.Id, Cost = 0 };
        var source = new DiyCardDraft { PackId = pack.Id, Cost = 1, Keywords = [keyword.Id] };
        cards.Save(target); cards.Save(source); WorkshopAuthoring.Configure(store, cards.Cards);
        using var session = await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards, 31, "target-deployment", "unused",
            sourceDirectory: Source, openingHand: [target.Id, source.Id], customCards: [target, source], firstPlayerIndex: 0);
        session.DriveOpponent = false; await session.SettleMulliganAsync(false);
        var victim = session.View.SelfHand.Single(c => c.CardId == target.Id);
        await session.SubmitAsync(new PlayCard(victim.Uid)); session.SettleProjection();
        var actor = session.View.SelfHand.Single(c => c.CardId == source.Id);
        var gap = session.Actions.DeploymentGaps.Last(); var credits = session.View.SelfKredits;
        var cancel = true; var prompts = 0;
        session.TargetRequested += (request, responder) =>
        {
            prompts++; Assert.Equal(actor.Uid, request.SourceCard?.Uid);
            Assert.True(request.Slots.Single().BoardTargets);
            Assert.Contains(request.Slots.Single().Allowed, c => c.Id == victim.Uid);
            if (cancel) Assert.True(responder.Cancel(request.RequestId));
            else Assert.True(responder.Complete(request.RequestId,
                new Dictionary<string, IReadOnlyList<string>> { [request.Slots.Single().Name] = [victim.Uid] }));
        };
        Assert.Equal(UiSubmitOutcome.Rejected, await session.SubmitAsync(new PlayCard(actor.Uid, GapId: gap.GapId)).WaitAsync(TimeSpan.FromSeconds(5)));
        session.SettleProjection(); Assert.Equal(credits, session.View.SelfKredits);
        Assert.Contains(session.View.SelfHand, c => c.Uid == actor.Uid);
        cancel = false;
        Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new PlayCard(actor.Uid, GapId: gap.GapId)).WaitAsync(TimeSpan.FromSeconds(5)));
        session.SettleProjection(); Assert.Equal(2, prompts);
        Assert.Equal(credits - 1, session.View.SelfKredits);
        Assert.Contains(session.View.SelfLine, c => c.Uid == actor.Uid);
        Assert.Equal(target.Attack + 2, session.View.SelfLine.Single(c => c.Uid == victim.Uid).EffectiveAttack);
    }
    [Fact]
    public async Task TargetedKeywordSkillExposesItsBattlefieldSourceAndUsesTheNativeSelector()
    {
        var keyword = new DiyKeywordDraft { Name = "支援技能", ProgramJson = new KeywordProgram
        { Skills = [new() { Id = "support", Name = "支援", Cost = "0", SpendAttack = false, SpendMove = false,
            Actions = [new() { Op = "buff", Target = "choose.friendly.unit", Value = "2" }] }] }.ToJson() };
        var pack = new DiyPackDraft { Keywords = [keyword] }; store.Save(pack);
        var source = new DiyCardDraft { PackId = pack.Id, Cost = 0, Keywords = [keyword.Id] };
        cards.Save(source); WorkshopAuthoring.Configure(store, cards.Cards);
        using var session = await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards, 31, "skill-target-source", "unused",
            sourceDirectory: Source, openingHand: [source.Id], customCards: [source], firstPlayerIndex: 0);
        session.DriveOpponent = false; await session.SettleMulliganAsync(false);
        var actor = session.View.SelfHand.Single(c => c.CardId == source.Id);
        await session.SubmitAsync(new PlayCard(actor.Uid)); session.SettleProjection();
        var calls = 0;
        session.TargetRequested += (request, responder) =>
        {
            calls++; Assert.Equal(actor.Uid, request.SourceCard?.Uid);
            Assert.Equal("support", request.SourceCard?.Zone);
            Assert.True(responder.Complete(request.RequestId,
                new Dictionary<string, IReadOnlyList<string>> { [request.Slots.Single().Name] = [actor.Uid] }));
        };
        var skill = Assert.Single(session.KeywordSkills(actor.Uid));
        Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new UseKeywordSkill(actor.Uid, skill.KeywordId, skill.SkillId)).WaitAsync(TimeSpan.FromSeconds(5)));
        session.SettleProjection(); Assert.Equal(1, calls);
        Assert.Equal(source.Attack + 2, session.View.SelfLine.Single(c => c.Uid == actor.Uid).EffectiveAttack);
    }
    [Fact]
    public async Task KeywordOrderPublishesItsFirstTargetBeforePlayAndConsumesTheDirectDropWithoutAnotherPrompt()
    {
        var keyword = new DiyKeywordDraft { Name = "指向指令", ProgramJson = new KeywordProgram
        {
            State = new() { ["enabled"] = 1 },
            Handlers = [new() { Event = "card.played", Actions = [new() { Op = "if",
                When = [new() { Left = "state.enabled", Op = "eq", Right = "1" }],
                Then = [new() { Op = "damage", Target = "choose.friendly.unit", Value = "1" }],
                Else = [new() { Op = "draw", Value = "1" }] }] }]
        }.ToJson() };
        var pack = new DiyPackDraft { Keywords = [keyword] }; store.Save(pack);
        var target = new DiyCardDraft { PackId = pack.Id, Cost = 0 };
        var order = new DiyCardDraft { PackId = pack.Id, CardType = "order", Cost = 0, Keywords = [keyword.Id] };
        cards.Save(target); cards.Save(order); WorkshopAuthoring.Configure(store, cards.Cards);
        using var session = await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards, 31, "direct-keyword-order", "unused",
            sourceDirectory: Source, openingHand: [target.Id, order.Id], customCards: [target, order], firstPlayerIndex: 0);
        session.DriveOpponent = false; await session.SettleMulliganAsync(false);
        var victim = session.View.SelfHand.Single(c => c.CardId == target.Id);
        var source = session.View.SelfHand.Single(c => c.CardId == order.Id);
        Assert.Empty(session.Actions.OrderTargets.Single(t => t.Uid == source.Uid).TargetUids);
        await session.SubmitAsync(new PlayCard(victim.Uid)); session.SettleProjection();
        var before = session.View.SelfLine.Single(c => c.Uid == victim.Uid).EffectiveDefense;
        var published = session.Actions.OrderTargets.Single(t => t.Uid == source.Uid);
        Assert.Equal(new[] { victim.Uid }, published.TargetUids);
        Assert.Contains(session.View.SelfHand, c => c.Uid == source.Uid);
        var prompts = 0; session.TargetRequested += (_, _) => prompts++;
        Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new PlayCard(source.Uid, TargetUid: victim.Uid)).WaitAsync(TimeSpan.FromSeconds(5)));
        session.SettleProjection(); Assert.Equal(0, prompts);
        Assert.DoesNotContain(session.View.SelfHand, c => c.Uid == source.Uid);
        Assert.Equal(before - 1, session.View.SelfLine.Single(c => c.Uid == victim.Uid).EffectiveDefense);
    }
    [Fact]
    public void ProgramKeywordMustNotCompileItsDescriptionOrOldEffectsIntoExtraSelectionGraphs()
    {
        var legacy = OrcInspector.CompileText("legacy-selector", "溃军可撤退到支援线。", "unit");
        var effects = legacy.Parse.Effects.Select(OrcEffectEditing.ToDraft).ToArray();
        Assert.NotEmpty(effects);
        var keyword = new DiyKeywordDraft { Name = "溃退", EffectText = "溃军可撤退到支援线。", Effects = effects,
            ProgramJson = new KeywordProgram { Skills = [new() { Id = "fallback", Name = "撤退", SpendMove = false,
                SpendAttack = false, Actions = [new() { Op = "retreat", Target = "self" }] }] }.ToJson() };
        var pack = new DiyPackDraft { Keywords = [keyword] }; store.Save(pack);
        var draft = new DiyCardDraft { PackId = pack.Id, Keywords = [keyword.Id] };
        WorkshopAuthoring.Configure(store, [draft]);
        Assert.Empty(WorkshopAuthoring.KeywordGraphs(draft));
        store.Save(pack with { Keywords = [keyword with { Effects = [] }] });
        WorkshopAuthoring.Configure(store, [draft]);
        Assert.Empty(WorkshopAuthoring.KeywordGraphs(draft));
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
        OrcEngineHost.Reset();
        var full=Path.GetFullPath(root);if(!full.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))throw new IOException("Unexpected test workspace");Directory.Delete(full,true);
    }
}
