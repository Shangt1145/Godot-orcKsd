using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Orc.Game.Cards;
using Xunit;

namespace Kards.Ui.Tests;

/// <summary>
/// 机制示范包种子工具：往真实工坊库 upsert「机制示范包」——每个可由规则程序忠实落地的词条
/// 配一个示范卡。设 SEED_DEMO_PACK=1 才执行（常规测试不触碰真实数据）；目录可用 SEED_DEMO_DIR 覆盖。
/// 引擎缺口词条（制空/炎热/寒冷/地域/船/阻击/掩护/标记/伺掠）不在包内——口径见 docs/CUSTOM_KEYWORD_SYSTEM.md。
/// </summary>
public sealed class SeedDemoPackTool
{
    private static KeywordProgramAction A(string op,string value="0",string key="",string target="self")=>new(){Op=op,Value=value,Key=key,Target=target};

    [Fact]
    public void Seed()
    {
        if(Environment.GetEnvironmentVariable("SEED_DEMO_PACK")!="1")return;
        var dir=Environment.GetEnvironmentVariable("SEED_DEMO_DIR")??Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Godot","app_userdata","OrC-KSD · 卡牌指挥部");
        var store=new DiyWorkshopLibrary(Path.Combine(dir,"diy-packs.json"));
        var cards=new DiyCardLibrary(Path.Combine(dir,"diy-cards.json"));
        var catalog=new CardCatalog();catalog.Load(@"H:\Working Folder\OrC-KSD.Godot\proto\data\nations");

        const string routedId="diy/demo-kuijun";
        const string launchId="diy/demo-fashe";
        string P(string name,KeywordProgram program)
        {
            program.Validate();return program.ToJson();
        }
        var keywords=new List<DiyKeywordDraft>
        {
            new(){Id="kw-demo-youji",Name="游击",Battle=true,EffectText="使用此牌时，将 1 张不具有游击的复制加入手牌，回合结束从手中移除复制。",
                ProgramJson=P("游击",new(){Handlers=[new(){Event="card.played",Subject="self",Actions=[new(){Op="copy.hand",Target="self",Temporary=true}]}]})},
            new(){Id="kw-demo-zengyuan",Name="增援",Battle=true,EffectText="第一次进入战场不触发；再次进入战场时获得 +X+X（X＝词条参数）。也算部署效果。",
                ProgramJson=P("增援",new(){Handlers=[new(){Event="unit.deployed",Subject="self",DeploymentEffect=true,When=[new("entries","gte","value")],Actions=[new(){Op="buff",Target="self",Value="value",Second="value"}]}]})},
            new(){Id="kw-demo-jidong",Name="机动",Battle=true,EffectText="本单位可以攻击两次，完成第二次攻击后失去机动。",
                ProgramJson=P("机动",new(){Rules=[new(){Rule="attack.limit",Value="2"}],Handlers=[new(){Event="unit.attack.after",Subject="self",
                    When=[new("attacks","gte","2")],Actions=[new(){Op="keyword.remove",Target="self",Key=""}]}]})},
            new(){Id="kw-demo-buqu",Name="不屈",Battle=true,EffectText="第一次将被消灭时取消并保留 1 点防御；己方回合开始时被消灭。",
                ProgramJson=P("不屈",new(){State=new(){{"owed",0}},Handlers=[
                    new(){Event="unit.destroy.before",Subject="self",When=[new("state.owed","eq","0")],Actions=[A("state.set","1","owed"),new(){Op="death.cancel",Target="self"},new(){Op="health.set",Target="self",Value="1"}]},
                    new(){Event="turn.start",Subject="owner",When=[new("state.owed","eq","1")],Actions=[new(){Op="kill",Target="self"}]}]})},
            new(){Id="kw-demo-zhanlve",Name="战略",Battle=true,EffectText="将被消灭时转化为溃军（口径：以消灭介入完成转化，本体不实际死亡）。",
                ProgramJson=P("战略",new(){Handlers=[new(){Event="unit.destroy.before",Subject="self",Actions=[
                    new(){Op="death.cancel",Target="self"},new(){Op="replace",Target="self",Key=routedId}]}]})},
            new(){Id="kw-demo-hushi",Name="虎视",Battle=true,EffectText="本单位攻击后，仍存活的被攻击敌方单位转换为溃军。",
                ProgramJson=P("虎视",new(){Handlers=[new(){Event="unit.attack.after",Subject="self",Actions=[
                    new(){Op="replace",Target="attack.target",Key=routedId}]}]})},
            new(){Id="kw-demo-fuyuan",Name="复员",Battle=true,EffectText="非消灭离场时，将本单位的复制加入手牌且费用 +X（X＝词条参数）；复制品无复员，本体失去复员。口径：被消灭不算复员。",
                ProgramJson=P("复员",new(){Handlers=[new(){Event="card.leaving",Subject="self",Actions=[
                    new(){Op="copy.hand",Target="self",Value="value"},new(){Op="keyword.remove",Target="self",Key=""}]}]})},
            new(){Id="kw-demo-chandou",Name="缠斗",Battle=true,EffectText="部署时指定 1 个敌军，双方无法移动或攻击；缠斗方回合开始时双方进行 1 次战斗，直到一方离开战场。",
                ProgramJson=P("缠斗",new(){Handlers=[
                    new(){Event="unit.deployed",Subject="self",Actions=[new(){Op="link",Target="choose.enemy.unit"}]},
                    new(){Event="turn.start",Subject="owner",Actions=[new(){Op="attack",Target="linked"}]}],Rules=[
                    new(){Rule="move.allowed",Scope="pair",Value="0",Priority=1},
                    new(){Rule="attack.allowed",Scope="pair",Value="0",Priority=1}]})},
            new(){Id="kw-demo-kuijun",Name="溃退",Battle=true,EffectText="本单位可以消耗正常移动机会和行动花费，从前线移动回己方支援线。",
                ProgramJson=P("溃退",new(){Rules=[new(){Rule="move.retreat",Value="1"}]})},
            new(){Id="kw-demo-zhuangtian",Name="装填",Battle=true,EffectText="打出时记录 1 个单位的攻击力，将 1 张「发射」加入手牌。",
                ProgramJson=P("装填",new(){Handlers=[new(){Event="card.played",Subject="self",Actions=[
                    new(){Op="capture",Target="choose.unit",Value="target.attack",Key="loaded"},
                    new(){Op="create.hand",Target="self",Key=launchId,Bindings=new(){{"damage","data.loaded"}}}]}]})},
            new(){Id="kw-demo-fashe",Name="发射",Battle=true,EffectText="打出时对 1 个单位造成等同于记录值的伤害。",
                ProgramJson=P("发射",new(){Handlers=[new(){Event="card.played",Subject="self",Actions=[
                    new(){Op="damage",Target="choose.unit",Value="data.damage"}]}]})},
        };
        var pack=new DiyPackDraft{Id="pack/diy-demo-mechanics",Name="机制示范包",Keywords=keywords.ToArray()};

        var drafts=new List<DiyCardDraft>
        {
            new(){Id="diy/demo-mijia",PackId=pack.Id,Name="米佳游击队",Cost=1,OpCost=1,Attack=2,Defense=2,UnitType="infantry",
                Keywords=["kw-demo-zengyuan","kw-demo-youji"],KeywordValues=new(){["kw-demo-zengyuan"]=2},Flavor="增援：获得 +2+2。"},
            new(){Id="diy/demo-youji",PackId=pack.Id,Name="游击侦察兵",Cost=1,OpCost=1,Attack=2,Defense=2,Keywords=["kw-demo-youji"],Flavor="打了就跑。"},
            new(){Id="diy/demo-jidong",PackId=pack.Id,Name="机动骑兵",Cost=3,OpCost=1,Attack=3,Defense=3,Keywords=["kw-demo-jidong"],Flavor="再来一次冲锋。"},
            new(){Id="diy/demo-buqu",PackId=pack.Id,Name="不屈老兵",Cost=4,OpCost=2,Attack=2,Defense=5,Keywords=["kw-demo-buqu"],Flavor="还不到时候。"},
            new(){Id="diy/demo-zhanlve",PackId=pack.Id,Name="战略军团",Cost=5,OpCost=2,Attack=4,Defense=4,Keywords=["kw-demo-zhanlve"],Flavor="转化＝不实际死亡，原地换为溃军。"},
            new(){Id="diy/demo-hushi",PackId=pack.Id,Name="虎视猎手",Cost=4,OpCost=2,Attack=3,Defense=3,Keywords=["kw-demo-hushi"],Flavor="被打剩的都归溃军。"},
            new(){Id="diy/demo-fuyuan",PackId=pack.Id,Name="复员卫戍队",Cost=3,OpCost=1,Attack=3,Defense=3,Keywords=["kw-demo-fuyuan"],KeywordValues=new(){["kw-demo-fuyuan"]=1},Flavor="回家，但更贵了。"},
            new(){Id="diy/demo-chandou",PackId=pack.Id,Name="缠斗步兵",Cost=2,OpCost=1,Attack=2,Defense=3,Keywords=["kw-demo-chandou"],Flavor="死磕到底。"},
            new(){Id=routedId,PackId=pack.Id,Name="溃军",Cost=0,OpCost=1,Attack=1,Defense=1,Keywords=["kw-demo-kuijun"],Flavor="士气崩溃的残部。"},
            new(){Id="diy/demo-zhuangtian",PackId=pack.Id,Name="装填",CardType="order",Cost=2,Keywords=["kw-demo-zhuangtian"],Flavor="记录 1 个单位的攻击力，将 1 张「发射」加入手牌。"},
            new(){Id=launchId,PackId=pack.Id,Name="发射",CardType="order",Cost=2,Keywords=["kw-demo-fashe"],Flavor="对 1 个单位造成等同于记录值的伤害。"},
        };

        foreach(var draft in drafts)cards.Save(draft);
        store.Save(pack);
        WorkshopAuthoring.Configure(store,cards.Cards);
        var failures=new List<string>();
        foreach(var draft in drafts)
        {
            var result=DiyCardCompiler.Validate(draft,catalog.Cards);
            if(!result.Success)failures.Add($"{draft.Name}: {string.Join("；",result.Errors)}");
        }
        Assert.True(failures.Count==0,"示范卡编译失败："+string.Join(" | ",failures));
        var keywordErrors=WorkshopAuthoring.Errors;
        Assert.True(keywordErrors.Count==0,"词条程序注册失败："+string.Join(" | ",keywordErrors.Select(p=>p.Key+":"+p.Value)));
    }
}
