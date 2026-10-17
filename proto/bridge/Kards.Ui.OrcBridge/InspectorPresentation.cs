using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kards.Ui.OrcBridge;

/// <summary>Presentation metadata for arbitrary source declarations. No gameplay decisions or
/// card-specific recipes: controls follow the complete authoring schema.</summary>
public static class InspectorPresentation
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,JsonElement> InferredFields=new();
    public static JsonElement Infer(string key)=>InferredFields.GetOrAdd(key,k=>DesktopOrc.Request(new {method="infer",name=k}));
    private static readonly Dictionary<string,string> Names=Pairs("""
script=自定义脚本|log=记录提示信息|hqBelow=总部生命低于指定值|hqAbove=总部生命高于指定值|frontlineControl=前线控制情况|frontlineEnemyOrFull=前线被敌方占领或已满|hasRoom=指定区域还有空位|unitCountLess=单位数量不足|targetDestroyed=目标已被消灭|targetIsHQ=目标是总部|handHasCard=手牌中有指定卡牌|cardInDeck=卡组中有指定卡牌|discoveredCardNotInDeck=发现的牌不在卡组中|revealedInEnemyHand=敌方手牌中有已公开的牌|eventUnitIs=引发事件的是指定单位|eventCardIs=引发事件的是指定卡牌|lastTurnKilledCompare=比较上回合消灭数量|damageFromType=伤害来自指定兵种|deadUnitCostAtLeast=被消灭单位费用达到要求|opCost=比较行动费用|defenderIsType=被攻击者是指定兵种|playedUnitThisTurn=本回合使用过单位|firstUnitThisTurn=本回合第一个单位|random=按概率成立|predicate=组合判断条件|damageAll=群体伤害|healAll=群体治疗|healHQ=治疗总部|destroyAll=群体消灭|buffAll=群体属性增减|debuff=降低属性|condOpCost=按条件修改行动费用|debuffTemp=临时降低属性|setDefense=设置防御力|copyLastDiscarded=复制刚弃掉的牌|grantAll=群体获得词条|removeUnit=移除单位|returnToHand=返回手牌|_returnToHandOld=返回手牌（旧格式）|intel=获得情报|armorBonus=增加重甲|activate=执行子效果|randomPick=随机执行一个分支|salvage=打捞|fury=获得狂怒|shuffleInUntil=补充卡组至指定数量|removeDeckTop=移除卡组顶的牌|deckToField=从卡组部署|enemyDeckToHand=获取敌方卡组的牌|drawUntil=补充手牌至指定数量|noDrawNextTurn=下回合停止抽牌|noKreditSlotNextTurn=下回合不增加指挥点槽|setOpCost=设置行动费用|globalOpCostMod=修改全局行动费用|setKreditSlots=设置指挥点槽|loseKreditSlots=失去指挥点槽|loseKredits=失去指挥点|nextTurnKredits=下回合获得指挥点|nextTurnSlots=下回合获得指挥点槽|nextTurnDrawPending=下回合额外抽牌|nextTurnStart=下回合开始时执行|suppressHQ=抑制总部|orderDamageMod=修改指令伤害|deckToTop=将牌移至卡组顶|rememberTarget=记住所选对象|triggerMountain=触发山地效果|shuffleHandIntoDeck=将手牌洗回卡组|fightRepeatedly=连续交战|removeFromEnemyDeck=移除敌方卡组中的牌|deckCostModAll=修改卡组卡牌费用|noKreditSlotForTurns=暂时停止增加指挥点槽|refreshAction=恢复行动次数|modMovedCost=修改已移动单位的费用|grantEffectToHand=给手牌附加效果|scrapAirDraw=回收飞机并抽牌|forecast=预知卡组顶|countered=反制当前指令|hqKeyword=给总部附加词条|delayNextTurn=延迟到下回合执行|hqArmor=总部获得重甲|handToField=从手牌部署|setHandCost=设置手牌费用|summonFromHand=召唤手牌单位|setLastAddedCost=修改刚加入手牌的费用|handUnitToFrontline=从手牌部署到前线|shuffleRandomSet=洗入指定系列的随机牌|grantRandomCombatKw=获得随机对战词条|grantRandomKwToPiles=给牌堆附加随机词条|opCostModAll=群体修改行动费用|handOpCostMod=修改手牌行动费用|copyHandCard=复制手牌|discardChosenHand=弃掉选中的手牌|chooseHandCard=选择手牌|buffCardsInPiles=加强牌堆中的卡牌|buffDeckAndHand=加强手牌和卡组|chooseFromHand=选择手牌并处理|canMoveAndAttack=允许移动后攻击|transformHandCard=变换手牌|copyToDeck=复制到卡组|orderDoubleTurn=本回合指令效果翻倍|discardAll=弃掉所有符合条件的手牌|discardFromDeck=弃掉卡组中符合条件的牌|damageSplit=分配伤害|conditionalRandomSplit=按条件随机分配伤害
""");
    private static readonly Dictionary<string,string> Fields=Pairs("""
trigger=什么时候生效|actions=接下来做什么|targets=让玩家选择的目标|condition=只有满足条件才生效|target=对谁生效|amount=数值|count=数量|times=重复几次|attack=攻击力变化|defense=防御力变化|side=哪一方|zone=在哪个区域|keyword=哪个词条|keywords=附加词条|mod=特殊规则|duration=持续多久|prompt=给玩家的提示|label=显示名称|name=记录名称|filter=筛选条件|value=取值|cmp=比较方式|sel=选择方式|ref=使用哪个已记录对象|options=可选方案|id=目标名称|cardType=卡牌类别|kind=对象种类|spec=对象范围|opCost=行动费用|aura=持续影响|vars=预先记录的值|oncePerTurn=每回合只触发一次|once=只触发一次|firstEvent=只响应第一次事件|else=不满足时|then=满足时|items=组成项目|item=判断内容|text=文字说明|excludeSelf=排除本单位|key=记忆名称|every=每次达成后重新计数|reset=达成后清零|on=记录在哪|as=把结果记作|scope=记忆保存在哪|property=修改哪个属性|mode=怎样修改|enabled=启用这个状态|turns=持续回合数|cardId=选择卡牌|to=放到哪里|position=放置位置|pool=从哪些牌中挑选|asCopies=生成副本|data=附带的信息|test=判断什么|effect=获得的新效果|effects=附加效果|from=来源|cost=卡牌费用|costMod=费用变化|costTo=费用改为|total=总数量|deckSize=卡组目标张数|handSize=手牌目标张数|maxCost=费用上限|minCost=费用下限|max=最大值|min=最小值|unitType=兵种|set=国家或系列|random=随机选择|adjacent=只影响相邻单位|byCost=按费用计算|thenSummon=随后召唤|allowToken=允许衍生牌|killAtTurnEnd=回合结束时消灭|choose=由玩家选择|oddOnly=仅限奇数费用|loseBlitz=失去闪击|trueCopy=保留当前状态复制|halve=减半|double=翻倍|negate=取反|untilTurnEnd=仅本回合|optional=允许不选择|piles=哪些牌堆|cond=生效条件|stat=读取哪个属性|of=读取谁的属性|holder=效果携带者|pick=如何挑选|range=作用范围|effectTrigger=新效果的生效时机|forEach=对每个对象执行|with=与谁交战|srcRef=伤害来源|reveal=向双方公开|buff=属性增益|damage=伤害修正|takeDamage=受到的伤害修正|hasKw=需要的词条|evenTo=均分给谁|removeKeywords=移除哪些词条|code=脚本内容|script=脚本内容|eventSide=响应哪一方的行为|selfOnly=只响应本单位的行为|listenZone=携带者在哪时生效|expr=怎样取得这个值|left=比较左边的值|right=比较右边的值|operator=如何计算|query=统计哪些对象|where=只保留符合条件的对象|queries=合并哪些对象范围|path=读取哪个属性|includeAnchor=包含作为中心的单位|cardIds=候选卡牌|kwMap=词条|kwValues=词条的数值
""");
    private static readonly Dictionary<string,string> Values=Pairs("""
self=我方|enemy=敌方|friendly=友方|opponent=对方|both=双方|any=不限|field=战场|board=战场|support=支援阵线|frontline=前线|hand=手牌|deck=卡组|discard=弃牌区|hq=总部|pool=完整卡池|all=全部符合者|random=随机选取|first=从最左侧或牌堆顶选|last=从最右侧或牌堆底选|ref=已记录对象中的第一个|refAll=一组已记录对象|adjacent=相邻单位|union=合并多组对象|unit=单位|card=卡牌|order=指令|counter=反制|infantry=步兵|tank=坦克|artillery=炮兵|fighter=战斗机|bomber=轰炸机|permanent=永久|turn=本回合|nextTurn=直到下回合|unitScope=本单位|player=我方玩家|match=整场对局|global=整场对局|add=增加或减少|set=直接设为|attack=攻击力|defense=防御力|cost=卡牌费用|opCost=行动费用|maxDefense=防御力上限|missingDefense=损失的防御力|unitType=兵种|cardType=卡牌类别|system=体系|rarity=稀有度|keywords=当前词条|owner=所属玩家|zone=所在区域|alive=存活状态|damaged=是否受伤|pinned=是否被压制|canAct=能否行动|actionsLeft=剩余行动次数|revealed=是否已公开|silenced=是否被抑制|chargeIn=充能剩余回合|printed.attack=卡面攻击力|printed.defense=卡面防御力|printed.keywords=卡面词条|mods.immuneOrders=是否无视指令|hqMax=总部生命上限|kredits=当前指挥点|maxKredits=指挥点槽|before=发生之前|during=发生之时|after=发生之后|game=对局行为|op=效果动作|source=效果来源|eventUnit=引发事件的单位|event=当前事件|dead=刚被消灭的单位|eventVictim=事件中的受影响者|victim=受影响者|defender=被攻击者|attackTarget=攻击目标|attacker=攻击者|summoned=刚召唤的单位|lastSummoned=最近召唤的单位|target=选中的目标|eventCard=引发事件的卡牌|remembered=已记录的对象|candidate=正在筛选的对象|current=当前对象|friendlyPlayer=我方玩家|enemyPlayer=敌方玩家|state=当前对局|legacy=原有取值方式|auto=自动判断|literal=直接填写|read=读取一个属性|variable=使用记住的值|count=统计对象数量|query=取得一组对象|size=统计列表长度|math=计算一个数值|intersection=取多组共有的对象|memory=读取跨回合记忆|multiply=相乘|subtract=相减|divide=相除|min=取最小值|max=取最大值|floor=向下取整|ceil=向上取整|clear=晴朗|mist=薄雾|gale=狂风|snow=落雪|fixed=指定候选牌|shuffle=随机洗入|top=卡组顶|bottom=卡组底|left=手牌最左侧|right=手牌最右侧
""");
    private static Dictionary<string,string> Pairs(string text)=>text.Trim().Split('|').Select(p=>p.Trim().Split('=',2)).ToDictionary(p=>p[0],p=>p[1]);
    public static string Field(string key)=>Fields.GetValueOrDefault(key)??Chinese(InspectorVocabulary.Label("field",key),"附加设置");
    public static string Value(string key)=>key switch
    {
        "=="=>"等于","!="=>"不等于",">"=>"大于",">="=>"不小于","<"=>"小于","<="=>"不大于","contains"=>"包含","in"=>"属于列表", ""=>"默认",
        _=>Values.GetValueOrDefault(key)??Chinese(InspectorVocabulary.Label("value",key),key)
    };
    public static string Operation(string op)=>Names.GetValueOrDefault(op)??Chinese(InspectorVocabulary.Label("op",op),"扩展操作");
    public static string Condition(string op)=>Chinese(InspectorVocabulary.Label("condition",op),Operation(op));
    private static string Chinese(string text,string fallback)=>text.Any(c=>c is >= '\u4e00' and <= '\u9fff')?text:fallback;
    public static JsonElement Definition(string op,string table="ops")=>InspectorVocabulary.Catalog.GetProperty("vocabulary").GetProperty(table).GetProperty(op);
    public static IReadOnlyList<JsonElement> Parameters(string op,string table="ops")=>Definition(op,table).GetProperty("fields").EnumerateArray().Select(f=>f.Clone()).ToArray();
    public static string Group(string op)
    {
        var group=Definition(op).GetProperty("group").GetString()??"其他操作";
        if(group!="全部 DSL 动作")return group;
        var name=Operation(op);
        if(new[]{"牌","卡组","手牌","召唤","部署","开发","打捞"}.Any(name.Contains))return "卡牌";
        if(new[]{"指挥","天气","回合"}.Any(name.Contains))return "资源与环境";
        if(new[]{"重复","分支","随机执行","条件","逐个"}.Any(name.Contains))return "流程";
        if(new[]{"记录","记住","变量","触发","效果"}.Any(name.Contains))return "记录";
        return "单位与总部";
    }
    public static string ParameterType(JsonObject parent,string key)
    {
        if(parent["op"] is {} op)
            foreach(var table in new[]{"ops","conditions"})
                if(InspectorVocabulary.Catalog.GetProperty("vocabulary").GetProperty(table).TryGetProperty(op.ToString(),out var def))
                    foreach(var f in def.GetProperty("fields").EnumerateArray())if(f.GetProperty("name").GetString()==key)return f.GetProperty("type").GetString()!;
        return key switch
        {
            "actions" or "then" or "else"=>"actions","effect"=>"effect","effects"=>"effectList","condition"=>"condition","test" or "where"=>"predicate",
            "target" or "query" or "spec" or "with"=>"query","filter"=>"filter","value" or "left" or "right" or "amount" or "count" or "times" or "min" or "max"=>"value",
            "keyword"=>"keyword","side" or "eventSide"=>"legacySide","trigger"=>"trigger","cardId"=>"card",_=>Infer(key).GetProperty("type").GetString()!
        };
    }
    public static string Brief(JsonNode? node,int depth=0)
    {
        if(node is null)return "尚未填写";
        if(depth>6)return "后续步骤";
        if(node is JsonArray a)return a.Count==0?"还没有步骤":string.Join("，然后",a.Take(3).Select(n=>Brief(n,depth+1)))+(a.Count>3?"等 "+a.Count+" 步":"");
        if(node is JsonObject o)
        {
            if(o["trigger"] is {} trigger)return Timing(trigger.ToString())+"："+Brief(o["actions"],depth+1);
            if(o["op"] is {} op)
            {
                var name=Operation(op.ToString());
                if(op.ToString() is "loop" or "repeat")return "重复 "+Brief(o["times"],depth+1)+" 次（"+Brief(o["actions"],depth+1)+"）";
                var value=o["amount"]??o["count"]??o["value"];
                return name+(value is JsonValue?" "+value:"");
            }
            if(o["expr"] is {} expr)return Value(expr.ToString())+(o["name"] is {} n?"「"+n+"」":"");
            if(o["sel"]?.ToString()=="self")return "本单位";
            if(o["sel"] is not null)return Value(o["side"]?.ToString()??"any")+"的"+Value(o["zone"]?.ToString()??"field")+"对象";
            if(o["test"] is {} test)return test.ToString() switch{"all"=>"全部条件都满足","any"=>"任一条件满足","not"=>"不满足下面的条件",_=>"比较两个值"};
            return "自定义组合";
        }
        if(node.GetValueKind() is JsonValueKind.True or JsonValueKind.False)return node.ToString()=="true"?"是":"否";
        return node.GetValueKind()==JsonValueKind.String?Value(node.ToString()):node.ToString();
    }
    public static string Timing(string trigger)
    {
        if(!trigger.StartsWith("behavior:",StringComparison.Ordinal))return InspectorVocabulary.Label("trigger",trigger);
        var pieces=trigger[9..].Split(['/', ':'],StringSplitOptions.RemoveEmptyEntries);
        if(pieces.Length!=3)return "自定义时机";
        var action=pieces[0]=="game"&&InspectorVocabulary.Catalog.GetProperty("behaviorActions").TryGetProperty(pieces[1],out var name)?name.GetString()!:Operation(pieces[1]);
        return action+"，"+Value(pieces[2]);
    }
}
