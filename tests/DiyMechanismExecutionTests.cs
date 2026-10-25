using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Xunit;

namespace Kards.Ui.Tests;

[Collection("Workshop")]
public class DiyMechanismExecutionTests
{
    private static string Root
    {
        get { var path=AppContext.BaseDirectory;while(!File.Exists(Path.Combine(path,"Kards.Ui.csproj")))path=Path.GetDirectoryName(path)!;return path; }
    }
    private static CardCatalog Catalog(){var catalog=new CardCatalog();catalog.Load(Path.Combine(Root,"proto/data/nations"));return catalog;}
    [Theory]
    [InlineData("部署：使本单位攻击力设为5；使本单位获得+2防御力；抽1张牌。",5,3,0,0)]
    [InlineData("部署：对敌方总部造成3点伤害。",1,1,-1,-3)]
    [InlineData("部署：敌方抽2张牌。",1,1,-1,0)]
    [InlineData("部署：使所有友方单位获得+1+1。",2,2,-1,0)]
    public async Task ParsedEffectsExecuteThroughTheSameNativeMatch(string text,int attack,int defense,int handDelta,int hqDelta)
    {
        var catalog=Catalog();var draft=new DiyCardDraft{Name="机制验证",Cost=0,OpCost=0,Attack=1,Defense=1,EffectText=text};
        var result=DiyTextAuthoring.Parse(draft,text,catalog.Cards);
        Assert.True(result.Success,string.Join("；",result.Problems));draft=result.Draft!;
        using var session=await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards,725,"diy-mechanisms","probe",sourceDirectory:Path.Combine(Root,"proto/data/nations"),customCards:[draft],openingHand:[draft.Id],firstPlayerIndex:0);
        session.DriveOpponent=false;await session.SettleMulliganAsync(false);session.SettleProjection();
        var before=session.View.SelfHand.Count;var enemy=session.View.EnemyHandCount;
        var card=session.View.SelfHand.Single(c=>c.CardId==draft.Id);
        Assert.Equal(UiSubmitOutcome.Applied,await session.SubmitAsync(new PlayCard(card.Uid,0)));session.SettleProjection();
        var unit=Assert.Single(session.View.SelfLine);Assert.Equal(attack,unit.EffectiveAttack);Assert.Equal(defense,unit.EffectiveDefense);
        Assert.Equal(before+handDelta,session.View.SelfHand.Count);Assert.Equal(20+hqDelta,session.View.EnemyHq!.Health);
        if(text.Contains("敌方抽"))Assert.Equal(enemy+2,session.View.EnemyHandCount);
    }
    [Fact]
    public void AppendingTextKeepsBothCompleteProgramsAndTheirTargetScopes()
    {
        var catalog=Catalog();var draft=new DiyCardDraft{Name="追加机制",Cost=0};
        var first=DiyTextAuthoring.Parse(draft,"部署：抽1张牌。",catalog.Cards);Assert.True(first.Success,string.Join("；",first.Problems));
        var second=DiyTextAuthoring.Parse(first.Draft!,"亡计：对敌方总部造成2点伤害。",catalog.Cards,append:true);
        Assert.True(second.Success,string.Join("；",second.Problems));Assert.Equal(2,second.Draft!.OrcEffects.Length);
        Assert.Contains("drawOne",second.Draft.OrcEffects[0].DesktopJson);Assert.Contains("dealDamage",second.Draft.OrcEffects[1].DesktopJson);
    }
    [Fact]
    public async Task NativeAuraTracksNewUnitsAndDisappearsWhenItsSourceLeaves()
    {
        var catalog=Catalog();
        const string graph="""{"trigger":"passive","actions":[{"op":"changeAttribute","target":{"sel":"all","side":"friendly","zone":"field","excludeSelf":true},"property":"attack","mode":"add","value":2}]}""";
        var aura=new DiyCardDraft{Name="持续指挥官",Cost=0,Attack=1,Defense=3,OrcEffects=[new(){DesktopJson=graph}]};
        var ally=new DiyCardDraft{Name="受益步兵",Cost=0,Attack=1,Defense=2};
        using var session=await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards,729,"aura-native","probe",sourceDirectory:Path.Combine(Root,"proto/data/nations"),customCards:[aura,ally],openingHand:[aura.Id,ally.Id],firstPlayerIndex:0);
        session.DriveOpponent=false;await session.SettleMulliganAsync(false);session.SettleProjection();
        foreach(var draft in new[]{aura,ally})
        {var card=session.View.SelfHand.Single(c=>c.CardId==draft.Id);Assert.Equal(UiSubmitOutcome.Applied,await session.SubmitAsync(new PlayCard(card.Uid,0)));session.SettleProjection();}
        Assert.True(session.Match.Environment.Auras.All.Count>0,"Native aura was not registered on entry.");
        Assert.Equal(3,session.View.SelfLine.Single(c=>c.CardId==ally.Id).EffectiveAttack);
        var native=session.Match.Players[0];var host=session.Match.Battlefield.GetSupportLine(native).Members.Single(c=>c.Name==aura.Name);
        await session.Match.EffectRuntime.KillAsync(host,ct:default);session.SettleProjection();
        Assert.Equal(1,session.View.SelfLine.Single(c=>c.CardId==ally.Id).EffectiveAttack);
    }
    [Fact]
    public async Task PlayerChoiceExecutesOnlyTheChosenNativeBranch()
    {
        var catalog=Catalog();
        const string graph="""{"trigger":"deploy","actions":[{"op":"choice","prompt":"选择方案","options":[{"label":"抽牌","actions":[{"op":"drawOne","side":"self"}]},{"label":"炮击","actions":[{"op":"dealDamage","target":{"sel":"all","side":"enemy","zone":"hq"},"amount":3}]}]}]}""";
        var draft=new DiyCardDraft{Name="抉择步兵",Cost=0,OrcEffects=[new(){DesktopJson=graph}]};
        using var session=await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards,735,"choice-native","probe",sourceDirectory:Path.Combine(Root,"proto/data/nations"),customCards:[draft],openingHand:[draft.Id],firstPlayerIndex:0);
        session.DriveOpponent=false;await session.SettleMulliganAsync(false);session.SettleProjection();var hand=session.View.SelfHand.Count;var prompts=0;
        session.TargetRequested+=(request,responder)=>{prompts++;Assert.True(responder.Complete(request.RequestId,new Dictionary<string,IReadOnlyList<string>>{{request.Slots[0].Name,[request.Slots[0].Allowed.Last().Id]}}));};
        var card=session.View.SelfHand.Single(c=>c.CardId==draft.Id);Assert.Equal(UiSubmitOutcome.Applied,await session.SubmitAsync(new PlayCard(card.Uid,0)));session.SettleProjection();
        Assert.Equal(1,prompts);Assert.Equal(hand-1,session.View.SelfHand.Count);Assert.Equal(17,session.View.EnemyHq!.Health);
    }
}
