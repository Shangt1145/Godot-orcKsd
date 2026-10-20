using System.Text.Json;
using System.Text.Json.Nodes;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Orc.Cards;
using Orc.Core;
using Orc.Game.Actions;
using Orc.Game.EffectParsing.Dsl;
using Orc.Output;
using Xunit;

namespace Kards.Ui.Tests;

[Collection("Workshop")]
public sealed class DiyEditorUpdateTests
{
    private const string Source=@"H:\Working Folder\OrC-KSD.Godot\proto\data\nations";
    private static CardCatalog Catalog()
    {
        OrcEngineHost.WarmUp(Path.Combine(Path.GetDirectoryName(Source)!,"kards"));
        var catalog=new CardCatalog();catalog.Load(Source);return catalog;
    }
    [Fact]
    public void CapabilityIsPerEffectAndIncludesLegacyAndCardLevelReasons()
    {
        var card=new DiyCardDraft{OrcEffects=[new(){Ops=[new(){Op="draw",Count=1}]},new(){Ops=[new(){Op="unknown"}]}],Effects=[new(){Op="bad"}]};
        var probe=DiyCapability.Probe(card,Catalog().Cards);
        Assert.Equal(DiyPlayability.Playable,probe.Single(r=>r.Source=="structured"&&r.EffectIndex==0).State);
        Assert.Contains("unknown",Assert.Single(probe.Single(r=>r.Source=="structured"&&r.EffectIndex==1).Reasons));
        Assert.Equal(DiyPlayability.DraftOnly,probe.Single(r=>r.Source=="legacy").State);
        Assert.DoesNotContain(probe,r=>r.Source=="card");
        Assert.Contains(DiyCapability.Probe(card with{Cost=-1},Catalog().Cards),r=>r.Source=="card"&&r.Reasons.Count>0);
    }
    [Fact]
    public void OwnerControlsConditionSurvivesNativeDslStorageExportAndDeepCopy()
    {
        var effect=new OrcEffectDraft{Ops=[new(){Op="buff",Sel="self",Attack=1,Defense=1,Condition=OrcEffectEditing.OwnerControlsType,CondUnitTypes=["infantry","tank"],CondComparison=">=",CondValue=2}]};
        var parsed=OrcEffectEditing.ToParsed(effect);
        var json=DslJson.Serialize(new DslEffectInstance(parsed.TemplateId,new Dictionary<string,DslSlotFill>{[parsed.Slot]=new(parsed.Ops)}));
        using var doc=JsonDocument.Parse(json);var condition=doc.RootElement.GetProperty("fills").GetProperty(parsed.Slot).GetProperty("ops")[0].GetProperty("condition");
        Assert.Equal(2,condition.GetProperty("unitTypes").GetArrayLength());Assert.Equal(">=",condition.GetProperty("comparison").GetString());Assert.Equal(2,condition.GetProperty("value").GetInt32());
        var round=OrcEffectEditing.ToDraft(parsed);Assert.Equal(effect.Ops[0].CondUnitTypes,round.Ops[0].CondUnitTypes);
        var copy=effect.Snapshot();copy.Ops[0].CondUnitTypes![0]="bomber";Assert.Equal("infantry",effect.Ops[0].CondUnitTypes![0]);
        var imported=DiyCardLibrary.Import(DiyCardLibrary.Export(new(){OrcEffects=[effect]}));Assert.Equal(2,imported.OrcEffects[0].Ops[0].CondValue);
        var compiled=Assert.Single(OrcEffectEditing.Compile("condition",[effect]));Assert.Null(compiled.Error);
        Assert.Contains("OwnerUnits",compiled.JsonText);Assert.Contains("ReferenceEquals",compiled.JsonText);
    }
    [Fact]
    public void CounterNamesAreTypedBoundedLiteralSafeAndRenameOnlyReferences()
    {
        Assert.Equal("diy.counter.阵亡数",OrcCounterCodegen.Key(" 阵亡数 "));
        Assert.Throws<InvalidDataException>(()=>OrcCounterCodegen.Key("x\";throw"));
        var effects=new[]{new OrcEffectDraft{Ops=[new(){Op="counter.add",Counter="阵亡数",Amount=1},new(){Op="damage",Counter="阵亡数",AmountFromCounter=true}]}};
        var renamed=OrcCounterCodegen.Rename(effects,"阵亡数","新计数");Assert.Equal(new[]{"新计数"},OrcCounterCodegen.CountersIn(renamed));
        Assert.Equal("阵亡数",effects[0].Ops[0].Counter);
        var result=DiyCardCompiler.Validate(new(){OrcEffects=renamed},Catalog().Cards);Assert.True(result.Success,string.Join(';',result.Errors));
    }
    [Fact]
    public void VariablesAreExcludedFromSnapshotsAndFreshEngineStartsAtZero()
    {
        var engine=new LogicEngine();engine.Variables.Set(OrcCounterCodegen.Key("计数"),5);
        var json=SnapshotJson.SerializeAll(engine);Assert.DoesNotContain("diy.counter",json);
        using var snapshot=JsonDocument.Parse(json);Assert.NotNull(snapshot);
        var fresh=new LogicEngine();Assert.False(fresh.Variables.TryGet<int>(OrcCounterCodegen.Key("计数"),out var value));Assert.Equal(0,value);
        // A snapshot of the same engine is not a rollback: existing variables remain intact.
        Assert.Equal(5,engine.Variables.Get<int>(OrcCounterCodegen.Key("计数")));
    }
    [Fact]
    public void BandCatalogMatchesActualEnumsAndNonemptyBandsAreHonestlyBlocked()
    {
        Assert.Equal(Enum.GetNames<OrderFlowBands>(),DiyInjectionCatalog.Schemes["OrderFlowBands"]);
        // 引擎 S2：AttackFlowBands / DamageFlowBands 已并入底层动作的触发端/处理端两套 band。
        Assert.Equal(Enum.GetNames<ActionTriggerBands>(),DiyInjectionCatalog.Schemes["ActionTriggerBands"]);
        Assert.Equal(Enum.GetNames<ActionHandleBands>(),DiyInjectionCatalog.Schemes["ActionHandleBands"]);
        Assert.Equal(Enum.GetNames<DefaultBands>(),DiyInjectionCatalog.Schemes["DefaultBands"]);
        var compiled=OrcEffectEditing.Compile("prefab",[new(){Ops=[new(){Op="draw",Count=1}]}]).Single();Assert.Null(compiled.Error);
        var raw=JsonNode.Parse(compiled.JsonText!)!.AsArray()[0]!.DeepClone().AsObject();
        raw["root"]!["injects"]=new JsonArray(new JsonObject{["target"]="部署触发器",["event"]="e1",["band"]="TypoBand"});
        var draft=new OrcEffectDraft{Template="native-prefab",PrefabJson=raw.ToJsonString()};
        Assert.Contains("未知 band",DiyInjectionCatalog.Read(draft).Single().Error);
        var check=DiyCapability.Probe(new(){OrcEffects=[draft]},Catalog().Cards);Assert.Equal(DiyPlayability.DraftOnly,check.Single(r=>r.Source=="structured").State);
        raw["root"]!["injects"]![0]!["band"]="Counter";
        Assert.Contains("只支持",DiyInjectionCatalog.Read(raw.ToJsonString()).Single().Error);
        raw["root"]!["injects"]![0]!.AsObject().Remove("band");
        var valid=OrcEffectEditing.Compile("native",[draft with{PrefabJson=raw.ToJsonString()}]).Single();Assert.Null(valid.Error);
        Assert.Contains("部署触发器",DiyInjectionCatalog.Read(valid.JsonText).Single().Summary);
    }
    [Fact]
    public async Task CounterIncrementBuffAndDamageExecuteInOneNativeMatchAcrossCards()
    {
        var unit=new DiyCardDraft{Id="diy/counter-unit",Cost=0,Attack=2,Defense=10,OrcEffects=[new(){Ops=[
            new(){Op="counter.add",Counter="累计",Amount=2},new(){Op="buff",Sel="self",Counter="累计",AmountFromCounter=true,Attack=1,Defense=2}]}]};
        var order=new DiyCardDraft{Id="diy/counter-order",CardType="order",Cost=0,OrcEffects=[new(){Template="played_basic",Hook="card.played",Guard="SelfCard",Ops=[
            new(){Op="counter.add",Counter="累计",Amount=1},new(){Op="damage",Sel="all",Side="friendly",Counter="累计",AmountFromCounter=true}]}]};
        using var session=await OrcMatchSession.CreateFromCatalogAsync(Catalog().Cards,31,"counter-proof","unused",sourceDirectory:Source,openingHand:[unit.Id,order.Id],customCards:[unit,order],firstPlayerIndex:0);
        session.DriveOpponent=false;await session.SettleMulliganAsync(false);
        var actor=session.View.SelfHand.Single(c=>c.CardId==unit.Id);Assert.Equal(UiSubmitOutcome.Applied,await session.SubmitAsync(new PlayCard(actor.Uid)));session.SettleProjection();
        var before=session.View.SelfLine.Single(c=>c.Uid==actor.Uid);Assert.Equal(4,before.EffectiveAttack);Assert.Equal(14,before.EffectiveDefense);
        var command=session.View.SelfHand.Single(c=>c.CardId==order.Id);Assert.Equal(UiSubmitOutcome.Applied,await session.SubmitAsync(new PlayCard(command.Uid)));session.SettleProjection();
        Assert.Equal(11,session.View.SelfLine.Single(c=>c.Uid==actor.Uid).EffectiveDefense);
    }
    [Fact]
    public async Task ConditionIsCombinedWithSelfGuardAndDoesNotTriggerOnAnotherUnit()
    {
        var first=new DiyCardDraft{Id="diy/conditional-unit",Cost=0,OrcEffects=[new(){Ops=[new(){Op="buff",Sel="self",Attack=5,Defense=0,
            Condition=OrcEffectEditing.OwnerControlsType,CondUnitTypes=["infantry"],CondComparison=">=",CondValue=2}]}]};
        var second=new DiyCardDraft{Id="diy/other-unit",Cost=0};
        using var session=await OrcMatchSession.CreateFromCatalogAsync(Catalog().Cards,31,"condition-proof","unused",sourceDirectory:Source,openingHand:[first.Id,second.Id],customCards:[first,second],firstPlayerIndex:0);
        session.DriveOpponent=false;await session.SettleMulliganAsync(false);
        var a=session.View.SelfHand.Single(c=>c.CardId==first.Id);await session.SubmitAsync(new PlayCard(a.Uid));session.SettleProjection();
        Assert.Equal(2,session.View.SelfLine.Single(c=>c.Uid==a.Uid).EffectiveAttack);
        var b=session.View.SelfHand.Single(c=>c.CardId==second.Id);await session.SubmitAsync(new PlayCard(b.Uid));session.SettleProjection();
        Assert.Equal(2,session.View.SelfLine.Single(c=>c.Uid==a.Uid).EffectiveAttack);
    }
}
