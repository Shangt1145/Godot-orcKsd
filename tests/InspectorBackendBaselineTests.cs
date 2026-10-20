using System.Text.Json;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Orc.Game.EffectParsing;
using Xunit;
using Xunit.Abstractions;

namespace Kards.Ui.Tests;

public class InspectorBackendBaselineTests(ITestOutputHelper output)
{
    private static string Root
    {
        get
        {
            for(var directory=AppContext.BaseDirectory;directory is not null;directory=Path.GetDirectoryName(directory))
                if(File.Exists(Path.Combine(directory,"Kards.Ui.csproj")))return directory;
            throw new DirectoryNotFoundException("UI workspace not found");
        }
    }
    [Fact]
    public async Task RecordActualBackendCapabilitiesWithoutChangingParserOrAdmission()
    {
        var spaced=CardTextEffectCompiler.Parse("部署：对1 个敌方单位造成2点伤害。","unit");
        var compact=CardTextEffectCompiler.Parse("部署：对1个敌方单位造成2点伤害。","unit");
        Assert.NotNull(spaced);Assert.NotNull(compact);
        Assert.Null(CardTextEffectCompiler.Parse("随便理解这句话。","unit"));
        object Probe(string text,string type)
        {
            try
            {
                var parsed=CardTextEffectCompiler.Parse(text,type);
                if(parsed is null)return new{state="not-parsed"};
                _=CardTextEffectCompiler.Compile(type,parsed,"inspector/backend-baseline");
                return new{state="compiled",count=parsed.Count};
            }
            catch(Exception error){return new{state="gap",message=error.Message};}
        }
        async Task<object> ExecuteRaw(string text,int draws)
        {
            try
            {
                var id="diy/raw-backend-probe-"+draws;
                var snapshot=CardTextEffectCompiler.Compile("unit",CardTextEffectCompiler.Parse(text,"unit")!,id);
                var card=new DiyCardDraft{Id=id,Name="后端直接产物验证",Cost=0,OrcEffects=[new(){Template="native-prefab",PrefabJson=Orc.Cards.PrefabJson.SerializeForDisk(snapshot)}]};
                var catalog=new CardCatalog();var source=Path.Combine(Root,"proto","data","nations");catalog.Load(source);
                using var session=await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards,20261020,"raw-backend-"+draws,"probe",openingHand:[id],sourceDirectory:source,customCards:[card],firstPlayerIndex:0);
                await session.SettleMulliganAsync(false);session.SettleProjection();
                var actor=session.View.SelfHand.Single(c=>c.CardId==id);var before=session.View.SelfHand.Count;
                var outcome=await session.SubmitAsync(new PlayCard(actor.Uid));session.SettleProjection();
                var after=session.View.SelfHand.Count;
                return new{state=outcome==UiSubmitOutcome.Applied&&after==before-1+draws?"executed":"gap",outcome=outcome.ToString(),before,after,expected=before-1+draws};
            }
            catch(Exception error){return new{state="gap",message=error.Message};}
        }
        var report=new
        {
            engine="6d72eac",phase="compile probes and separate raw native-prefab execution probes",
            spacedTarget=spaced[0].Target,compactTarget=compact[0].Target,
            spacingParity=spaced[0].Target==compact[0].Target,
            single=Probe("部署：抽1张牌。","unit"),multiple=Probe("部署：抽1张牌；抽1张牌。","unit"),
            emptyUnit=Probe("","unit"),emptyOrder=Probe("","order"),counter=Probe("抽1张牌。","counter"),
            singleExecution=await ExecuteRaw("部署：抽1张牌。",1),multipleExecution=await ExecuteRaw("部署：抽1张牌；抽1张牌。",2)
        };
        var json=JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true});output.WriteLine(json);
        var folder=Path.Combine(Root,"artifacts","inspector-refactor-20261020");Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder,"backend-capabilities.json"),json);
    }
    [Fact]
    public async Task ComposedQueryCountAndCandidateComparisonExecuteInNativeMatch()
    {
        const string graph="""{"trigger":"deploy","targets":[],"actions":[{"op":"branch","test":{"test":"compare","left":{"expr":"count","query":{"sel":"all","side":"friendly","zone":"hand","where":{"test":"compare","left":{"expr":"read","of":"candidate","path":"cost"},"cmp":"<","right":4}}},"cmp":">=","right":0},"then":[{"op":"drawOne","side":"self"}],"else":[]}]}""";
        var card=new DiyCardDraft{Id="diy/composition-native",Name="通用组合执行验证",Cost=0,OrcEffects=[new(){DesktopJson=graph}]};
        var catalog=new CardCatalog();var source=Path.Combine(Root,"proto","data","nations");catalog.Load(source);
        var validation=DiyCardCompiler.Validate(card,catalog.Cards);Assert.True(validation.Success,string.Join("；",validation.Errors));
        using var session=await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards,20261020,"composition-native","probe",openingHand:[card.Id],sourceDirectory:source,customCards:[card],firstPlayerIndex:0);
        await session.SettleMulliganAsync(false);session.SettleProjection();
        var actor=session.View.SelfHand.Single(c=>c.CardId==card.Id);var before=session.View.SelfHand.Count;
        Assert.Equal(UiSubmitOutcome.Applied,await session.SubmitAsync(new PlayCard(actor.Uid)));session.SettleProjection();
        Assert.DoesNotContain(session.View.SelfHand,c=>c.Uid==actor.Uid);Assert.Equal(before,session.View.SelfHand.Count);
        output.WriteLine("Native deployment: query + candidate cost filter + count comparison + branch + draw executed.");
    }
}
