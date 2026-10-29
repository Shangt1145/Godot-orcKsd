using System.Runtime.CompilerServices;
using System.Text.Json;
using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Effects;
using Orc.Game.Players;
using Orc.Game.Targeting;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// DIY/导入卡效果在运行期可用的**宿主侧门面**。
/// <para>
/// **为什么需要它**：本仓库的 DIY 管线是"结构化图 → 生成 csx → 引擎编译执行"。
/// 生成的 csx 里那 25 个辅助调用原来全部指向引擎的 <c>Orc.Game.Effects.AuthoringRuntime</c>，
/// v4 把这个类型连同 <c>AuthoringTargetContext</c>／<c>AuthoringAuraRuntime</c> 一起删了
/// （"效果内选靶从不产生 targeter"的老问题由新模型从根上改掉）。
/// 引擎没有替代入口，所以这些适配必须由宿主自己提供——**逐字移植**，不重新发明语义。
/// </para>
/// <para>
/// **三条与旧实现的实质差异（不是等价，写清楚）**：
/// </para>
/// <list type="number">
/// <item><b>视图类型换了</b>：v4 的 handler 视图是 <see cref="ViewCarrier"/>（按字段名投影），
///   不是强类型 <c>CardEventView</c>。所以 <see cref="EventCard"/>／<see cref="EventTarget"/> 改成
///   "按字段名取，取不到再退回反射"，两条路径都覆盖。</item>
/// <item><b>没有 <c>EffectRuntime.MatchFor</c></b>：v4 只提供按服务各自 <c>ResolveFor(card)</c>，
///   拿不到 <c>Match</c>。本门面自己登记：<see cref="Bind"/> 在建对局时调用一次，
///   之后按 <c>card.Engine</c> 反查。</item>
/// <item><b>持续效果（光环）没有移植</b>：v4 的 <c>DeclareAuraAsync</c> 只吃常量增量＋描述式受益谓词，
///   承载不了旧光环的逐候选求值（会丢 <c>where</c> 与 <c>set</c> 模式）。作者期就拒绝，见
///   <c>DesktopNativeCodegen</c> 的 passive 分支。</item>
/// </list>
/// </summary>
public static class DesktopNativeRuntime
{
    /// <summary>对局句柄登记表（<c>card.Engine</c> → <c>Match</c>）。</summary>
    private static readonly ConditionalWeakTable<LogicEngine, Match> MatchHandles = new();

    /// <summary>「每回合一次」「只触发一次」的用表（宿主 → 图指纹 → 上次触发的回合）。</summary>
    private static readonly ConditionalWeakTable<object, Dictionary<string, int>> Uses = new();

    /// <summary>登记本局的 <c>Match</c> 句柄（<c>OrcMatchSession</c> 建对局时调用一次；幂等）。</summary>
    public static void Bind(Match match)
    {
        ArgumentNullException.ThrowIfNull(match);
        MatchHandles.AddOrUpdate(match.Engine, match);
    }

    /// <summary>按卡反查本局（未登记＝明确失败，不静默返回 null）。</summary>
    public static Match MatchOf(Card source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return MatchHandles.TryGetValue(source.Engine, out var match)
            ? match
            : throw new InvalidOperationException("效果没有连接到对局：本局未登记 Match 句柄（DesktopNativeRuntime.Bind 未被调用）。");
    }

    // ─────────────────────────── 视图取值 ───────────────────────────

    /// <summary>按字段名读视图（v4 <see cref="ViewCarrier"/> 优先；非承载视图退回反射，兼容任何遗留强类型视图）。</summary>
    private static object? Field(object? view, string name)
    {
        if (view is null) return null;
        if (view is ViewCarrier carrier) return carrier.TryGet(name, out var value) ? value : null;
        return view.GetType().GetProperty(name)?.GetValue(view);
    }

    /// <summary>事件里的"施动卡"：单位事件读 <c>Unit</c>，通用事件读 <c>Card</c>，攻击事件读 <c>Attacker</c>。</summary>
    public static Card? EventCard(object? view)
        => Field(view, "Unit") as Card
           ?? Field(view, "Card") as Card
           ?? Deref(Field(view, "Attacker"));

    /// <summary>事件的"承受方"：优先 <c>Target</c>；没有就退回施动卡（旧实现同口径）。</summary>
    public static Card? EventTarget(object? view)
        => Deref(Field(view, "Target")) ?? EventCard(view);

    /// <summary>
    /// 按字段名读视图载荷（**手写 csx 用**：生成器只覆盖得了它自己编出来的那几种取值方式，
    /// 手写脚本需要点名读某个字段时走这里，而不是在 csx 里对 <c>ViewCarrier</c> 反射）。
    /// <para>
    /// 典型用例：<c>unit.damage.dealt</c> 的载荷里 <c>Unit</c> 是施动方、<c>Card</c> 是受方，
    /// <see cref="EventCard"/> 只会给出施动方，取受方必须点名读 <c>"Card"</c>（见 exp-winterwar/h39_swg）。
    /// </para>
    /// </summary>
    public static object? Payload(object? view, string field) => Field(view, field);

    /// <summary>
    /// **施动卡**（旧 csx 模板里的 <c>self</c>）：视图载荷的 <c>Host</c> 字段——效果宿主卡。
    /// <para>
    /// 与 <see cref="EventCard"/> 的区别必须分清：<c>EventCard</c> 是"这次事件里动了谁"，
    /// 而 <c>Host</c> 是"这段效果长在哪张卡上"。单位部署时两者通常同一张，但 <c>card.died</c>／
    /// 回合开始这类信号里只有 <c>Host</c> 能给出效果承载者。
    /// </para>
    /// <para>
    /// v4 的 handler 视图是 <c>ViewCarrier</c>（动态载体），**视图对象本身不是卡**——
    /// 所以生成的代码必须经这里取卡，不能写 <c>view as Card</c>（那样恒为 null，效果会静默不执行）。
    /// </para>
    /// </summary>
    public static Card? Host(object? view)
    {
        // **必须先读 `ViewCarrier.Host` 这个 CLR 属性，不能只走 `TryGet("Host")`**。
        // `ViewCarrier` 的 `Host` 是框架回填的（"取自 owner 的宿主引用，**不来自数据面**"），
        // 而 `TryGet`／`Get` 只查视图声明的数据面 ⇒ 组件成员执行时数据面里没有 Host，
        // `TryGet` 给出 null。后果不是"某个效果弱一点"：生成的 csx 守卫
        // `if (orcRuntime && orcSelf != null && ShouldRun(...))` 直接整段跳过，
        // **没有异常、没有日志、盘面不动**——2026-10-28 实测就是这么丢掉 33 条单测的
        // （`[fx-trace] skip reason=host-null view=ViewCarrier`）。
        // 数据面读位仍保留在后面，作为非 ViewCarrier 宿主（旧强类型视图）的兜底。
        var card = view is ViewCarrier carrier ? carrier.Host as Card : null;
        card ??= Field(view, "Host") as Card;
        if (card is null)
        {
            Trace("host-null", view);
        }

        return card;
    }

    /// <summary>
    /// 这张卡此刻**在不在场**（＝已单位化、未摧毁、有位置）。
    /// <para>
    /// 为什么必须有这个判据：「回合开始/结束」是**全局信号**，引擎会发给所有挂了这个成员的实体，
    /// 包括还躺在手牌／卡组里的卡。手牌里的卡没有 <c>UnitStateData</c>，此时对它
    /// <c>BuffAsync</c>／读有效攻防都会撞引擎的硬错误
    /// 「字段 'Attack' 的基准来源未就绪（缺必要组件；禁止静默返回错值）」，
    /// 而这条异常会顺着信号桥一路抛到 <c>TurnManager.BeginTurnAsync</c> ⇒ **整个回合开不起来**。
    /// </para>
    /// <para>
    /// 引擎自己的内建词条就是这么写的（<c>Cards/KeywordPrefabs/keyword.mobilize.accrual.component.json</c>：
    /// <c>if (!unit.TryGetData&lt;UnitStateData&gt;(out var state) || state.IsDestroyed || state.Line is null) return;</c>），
    /// 这里只是把同一条不变量提到宿主守卫里，免得每张卡的脚本各写一遍、漏一张就炸一局。
    /// </para>
    /// </summary>
    private static bool OnBoard(Card card)
        => card is UnitCard unit && unit.TryGetData<UnitStateData>(out var state)
           && !state.IsDestroyed && state.Line is not null;

    // ─────────────────────── 守卫留痕（诊断面，默认关） ───────────────────────
    // 生成的 csx 守卫是 `if (orcRuntime is not null && orcSelf is not null && ShouldRun(...))`，
    // **任一子条件为假就整段跳过且不留任何日志**——"效果没反应"最难查的就是这一类：
    // 没有异常、没有隔离日志、盘面就是不动。所以这里给每个子条件留一条可开关的痕迹。
    // 开关：环境变量 KARDS_EFFECT_TRACE=1（默认关，不污染对局日志）。同一 (原因,视图类型) 只打一次。
    private static readonly bool TraceEnabled =
        Environment.GetEnvironmentVariable("KARDS_EFFECT_TRACE") == "1";

    private static readonly HashSet<string> TraceSeen = new(StringComparer.Ordinal);

    internal static void Trace(string reason, object? view)
    {
        if (!TraceEnabled)
        {
            return;
        }

        var viewName = view?.GetType().Name ?? "null";
        lock (TraceSeen)
        {
            if (!TraceSeen.Add(reason + "/" + viewName))
            {
                return;
            }
        }

        Console.WriteLine($"[fx-trace] skip reason={reason} view={viewName}");
    }

    private static Card? Deref(object? value) => value switch
    {
        Card card => card,
        Ref<Entity> reference => reference.IsAlive ? reference.Value as Card : null,
        _ => null,
    };

    // ─────────────────────────── 触发门禁 ───────────────────────────

    /// <summary>
    /// 判断这次触发是否该跑这张图的动作（阵营、selfOnly、filter、orderPlayed、once／oncePerTurn）。
    /// 语义逐条对齐旧实现；<c>Uses</c> 的键从"效果对象"改成"宿主卡"（v4 没有效果对象）。
    /// </summary>
    public static bool ShouldRun(Card host, object? view, string graph)
    {
        using var document = JsonDocument.Parse(graph);
        var effect = document.RootElement;
        var trigger = effect.GetProperty("trigger").GetString();

        // 「回合开始/结束」只对**在场单位**成立：全局信号会发到每一张挂了该成员的卡上，
        // 包括手牌/卡组里的卡。不拦这一下，手牌里的单位会被 BuffAsync 撞上
        // 「基准来源未就绪」硬错误，整回合开不起来（详见 OnBoard 的注释）。
        if (trigger is "turnStart" or "turnEnd" && !OnBoard(host))
        {
            Trace($"off-board:{trigger}", view);
            return false;
        }

        var actor = EventCard(view);
        var owner = Field(view, "Player") as Player ?? (actor is null ? null : Owner(actor));
        var side = effect.TryGetProperty("eventSide", out var eventSide) ? eventSide.GetString()
            : effect.TryGetProperty("watchOpponent", out var opponent) && opponent.GetBoolean() ? "enemy"
            : trigger is "turnStart" or "turnEnd" or "unitDeployed" or "unitUpgraded" or "orderPlayed" or "friendlyDeath" or "friendlyDamaged" ? "friendly"
            : "any";

        if (side is "self" or "friendly" && !ReferenceEquals(owner, Owner(host))
            || side is "enemy" or "foe" && ReferenceEquals(owner, Owner(host)))
        {
            Trace($"side:{side}", view);
            return false;
        }

        if (effect.TryGetProperty("selfOnly", out var selfOnly) && selfOnly.GetBoolean() && !ReferenceEquals(actor, host))
        {
            Trace("self-only", view);
            return false;
        }

        if (effect.TryGetProperty("filter", out var filter) && (actor is null || !Matches(actor, filter.GetRawText())))
        {
            Trace(actor is null ? "filter:no-actor" : "filter:mismatch", view);
            return false;
        }

        if (trigger == "orderPlayed" && actor is not OrderCard)
        {
            Trace("order-played:not-order", view);
            return false;
        }

        if (effect.TryGetProperty("listenZone", out var zone) && zone.GetString() is { Length: > 0 } selected && selected is not ("any" or "board" or "field"))
            throw new InvalidDataException("监听区域尚未接入：" + selected);

        if (effect.TryGetProperty("once", out var once) && once.GetBoolean()
            || effect.TryGetProperty("oncePerTurn", out var perTurn) && perTurn.GetBoolean())
        {
            var uses = Uses.GetOrCreateValue(host);
            var turn = MatchOf(host).TurnNumber;
            if (uses.TryGetValue(graph, out var previous) && (once.ValueKind == JsonValueKind.True || previous == turn))
            {
                Trace("once-already-used", view);
                return false;
            }

            uses[graph] = turn;
        }

        return true;
    }

    // ─────────────────────────── 归属 / 视角 / 牌堆 ───────────────────────────

    /// <summary>归属玩家（单位/指令读 <c>CardBase.Owner</c>，HQ 读 <c>Hq.Owner</c>）。</summary>
    public static Player Owner(Card source) => source switch
    {
        CardBase card when card.Owner is not null => card.Owner,
        Hq hq when hq.Owner is not null => hq.Owner,
        _ => throw new InvalidOperationException("效果没有归属玩家。"),
    };

    /// <summary>视角卡（<c>enemy</c>＝对手 HQ，其余＝自己 HQ）。</summary>
    public static Card Viewer(Card source, string side)
        => side is "enemy" or "foe"
            ? MatchOf(source).Players.Single(player => !ReferenceEquals(player, Owner(source))).Hq
            : Owner(source).Hq;

    /// <summary>某个阵营的手牌／牌库（<c>both</c>／<c>any</c>＝双方）。</summary>
    public static Card[] Pile(Card source, string side, string zone)
    {
        var players = side is "both" or "any" ? MatchOf(source).Players : [Owner(Viewer(source, side))];
        return players.SelectMany(player => zone == "hand"
                ? player.Hand.Cast<Card>()
                : Enumerable.Range(0, player.Deck.Count).Select(index => (Card)player.Deck.PeekInstance(index)))
            .ToArray();
    }

    /// <summary>引擎随机源（**不是** <c>System.Random</c>：同一颗对局种子必须整体可复现）。</summary>
    public static int Random(Card source, int min, int max)
    {
        if (max < min || (long)max - min >= int.MaxValue) throw new InvalidDataException("随机数范围无效。");
        return min + MatchOf(source).RandomService.Next(max - min + 1);
    }

    /// <summary>按 <c>random</c>／<c>first</c>／<c>last</c> 取子集（其它值＝原样返回，与旧实现一致）。</summary>
    public static Card[] Pick(Card source, Card[] candidates, string selection, int count)
    {
        count = Math.Clamp(count, 0, candidates.Length);
        return selection switch
        {
            "random" => MatchOf(source).RandomService.PickN(candidates, count).ToArray(),
            "first" => candidates.Take(count).ToArray(),
            "last" => candidates.Reverse().Take(count).ToArray(),
            _ => candidates,
        };
    }

    /// <summary>相邻单位（按所在阵线的前后位；<paramref name="includeAnchor"/>＝把锚点自己也算进去）。</summary>
    public static Card[] Adjacent(Card[] anchors, bool includeAnchor)
        => anchors.OfType<UnitCard>().SelectMany(unit =>
        {
            var line = unit.GetData<UnitStateData>().Line;
            if (line is null) return Array.Empty<Card>();
            var all = line.Members.ToArray();
            var index = Array.IndexOf(all, unit);
            return all.Where((_, i) => Math.Abs(i - index) == 1 || includeAnchor && i == index).Cast<Card>();
        }).Distinct().ToArray();

    // ─────────────────────────── 数值 / 文本 / 状态 ───────────────────────────

    /// <summary>读数值属性（攻击/防御/上限/缺口/费用/资源/归属序号/印刷值）。</summary>
    public static int Int(Card? card, string property)
    {
        if (card is Hq hq && property is "defense" or "hq" or "hqMax" or "maxDefense") return hq.Health;
        if (card is not CardBase typed) return 0;
        return property switch
        {
            "attack" => typed.Modifiers.GetEffectiveValue(CardStatFields.Attack),
            "defense" => typed.Modifiers.GetEffectiveValue(CardStatFields.Defense),
            "maxDefense" => typed.Modifiers.GetEffectiveCapValue(CardStatFields.Defense),
            "missingDefense" => typed.Modifiers.GetEffectiveCapValue(CardStatFields.Defense) - typed.Modifiers.GetEffectiveValue(CardStatFields.Defense),
            "cost" => typed.Modifiers.GetEffectiveValue(CardStatFields.DeployCost),
            "opCost" => typed.Modifiers.GetEffectiveValue(CardStatFields.OperateCost),
            "kredits" => typed.Owner?.Points ?? 0,
            "maxKredits" => typed.Owner?.PointSlots ?? 0,
            "owner" => typed.Owner?.Index ?? -1,
            "printed.attack" => typed.Definition.Attack,
            "printed.defense" => typed.Definition.Defense,
            _ => throw new InvalidDataException("尚未接入数值属性：" + property),
        };
    }

    /// <summary>读文本属性（名字／id／阵营／卡类／单位类型／区域）。</summary>
    public static string Text(Card? card, string property) => property switch
    {
        "name" => card?.Name ?? "",
        "uid" => card is null ? "" : card.Id.ToString("N"),
        "set" => card is CardBase typed ? typed.GetData<FactionCostData>().Faction : "",
        "cardType" => card is UnitCard ? "unit" : card is OrderCard ? "order" : "hq",
        "unitType" => (card as CardBase)?.Definition.UnitTypes.FirstOrDefault() ?? "",
        "zone" => card is UnitCard unit && unit.TryGetData<UnitStateData>(out var state) && state.Line is not null ? "field"
            : card is Hq ? "hq" : "hand",
        _ => throw new InvalidDataException("尚未接入文本属性：" + property),
    };

    /// <summary>读布尔状态（存活／受伤／被压制）。</summary>
    public static bool Flag(Card? card, string property) => property switch
    {
        "alive" => card is not null && !(card.TryGetData<UnitStateData>(out var state) && state.IsDestroyed),
        "damaged" => Int(card, "missingDefense") > 0,
        "pinned" => card is CardBase typed && typed.Keywords.Has(KeywordIds.Suppressed),
        _ => throw new InvalidDataException("尚未接入状态属性：" + property),
    };

    /// <summary>词条 token → 引擎词条 id（先查工坊自定义词条，再查本仓库的引擎词条目录）。</summary>
    /// <remarks>
    /// 旧实现用的是引擎的 <c>Orc.Game.Cards.Data.CardAttributeMap</c>，v4 把它降成 internal——
    /// 宿主侧改为走**本仓库已有的同一份映射**（<see cref="DesktopEffectRuntime.Keyword"/> 读嵌入的引擎词条目录，
    /// 与代码生成器 <c>NativeKeyword</c> 的解析同源），不是另建一套词条表。
    /// </remarks>
    public static string Keyword(string key)
    {
        if (key == "armor") return KeywordIds.Armor;
        if (key == "intel") return KeywordIds.Intelligence;
        var mapped = DesktopEffectRuntime.Keyword(key);
        return KeywordIds.All.Contains(mapped, StringComparer.Ordinal) ? mapped : key;
    }

    // ─────────────────────────── 筛选条件（含 where 子句） ───────────────────────────

    /// <summary>判定一张卡是否命中筛选条件（json＝筛选对象原文）。</summary>
    public static bool Matches(Card candidate, string json)
    {
        using var document = JsonDocument.Parse(json);
        return MatchFilter(candidate, document.RootElement);
    }

    /// <summary>校验筛选条件形状；不认识的条件＝抛错（不静默放行成"全选"）。</summary>
    public static void ValidateFilter(JsonElement filter)
    {
        foreach (var property in filter.EnumerateObject())
        {
            if (property.Name is "minCost" or "maxCost" or "minAttack" or "maxAttack" or "minDefense" or "maxDefense")
            {
                if (!property.Value.TryGetInt32(out _)) throw new InvalidDataException("筛选阈值必须为整数。");
            }
            else if (property.Name is "unitType" or "keyword" or "nameIncludes")
            {
                if (property.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException("筛选名称必须为文字。");
            }
            else if (property.Name == "where") ValidateTest(property.Value);
            else throw new InvalidDataException("尚未接入筛选条件：" + property.Name);
        }
    }

    private static void ValidateTest(JsonElement test)
    {
        var kind = test.GetProperty("test").GetString();
        if (kind is "all" or "any")
        {
            foreach (var child in test.GetProperty("items").EnumerateArray()) ValidateTest(child);
            return;
        }

        if (kind == "not")
        {
            ValidateTest(test.GetProperty("item"));
            return;
        }

        if (kind != "compare") throw new InvalidDataException("筛选判断尚未接入：" + kind);
        if (test.GetProperty("cmp").GetString() is not ("==" or "!=" or "<" or ">" or "<=" or ">=" or "contains")) throw new InvalidDataException("筛选比较尚未接入。");
        foreach (var key in new[] { "left", "right" })
        {
            var value = test.GetProperty(key);
            if (value.ValueKind != JsonValueKind.Object) continue;
            if (value.GetProperty("expr").GetString() != "read"
                || value.TryGetProperty("of", out var who) && who.GetString() is not ("candidate" or "current"))
                throw new InvalidDataException("筛选表达式尚未接入。");
            if (value.GetProperty("path").GetString() is not ("attack" or "defense" or "cost" or "opCost" or "maxDefense" or "missingDefense" or "unitType" or "cardType" or "set" or "name" or "keywords" or "extraTypes"))
                throw new InvalidDataException("筛选属性尚未接入。");
        }
    }

    private static bool MatchFilter(Card candidate, JsonElement filter)
    {
        if (filter.TryGetProperty("where", out var where) && !Test(candidate, where)) return false;
        foreach (var property in filter.EnumerateObject())
        {
            if (property.Name == "where") continue;
            if (property.Name is "minCost" or "maxCost" or "minAttack" or "maxAttack" or "minDefense" or "maxDefense")
            {
                var field = char.ToLowerInvariant(property.Name[3]) + property.Name[4..];
                var value = Int(candidate, field);
                if (property.Name.StartsWith("min", StringComparison.Ordinal) ? value < property.Value.GetInt32() : value > property.Value.GetInt32()) return false;
            }
            else if (property.Name == "unitType")
            {
                if (candidate is not CardBase typed || !typed.Definition.UnitTypes.Contains(property.Value.GetString()!)) return false;
            }
            else if (property.Name == "keyword")
            {
                if (candidate is not CardBase typed || !typed.Keywords.Has(Keyword(property.Value.GetString()!))) return false;
            }
            else if (property.Name == "nameIncludes")
            {
                if (!candidate.Name.Contains(property.Value.GetString()!, StringComparison.Ordinal)) return false;
            }
            else throw new InvalidDataException("尚未接入筛选条件：" + property.Name);
        }

        return true;
    }

    private static object? Read(JsonElement value, Card candidate)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind == JsonValueKind.Number) return value.GetDouble();
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        var path = value.GetProperty("path").GetString()!;
        if (path == "extraTypes") return (candidate as CardBase)?.Definition.UnitTypes.ToArray() ?? [];
        if (path == "keywords") return candidate is CardBase typed ? typed.Keywords.Components.Select(k => k.Keyword).ToArray() : Array.Empty<string>();
        return path is "unitType" or "cardType" or "set" or "name" ? Text(candidate, path) : (object)(double)Int(candidate, path);
    }

    private static bool Test(Card candidate, JsonElement test)
    {
        var kind = test.GetProperty("test").GetString();
        if (kind == "all") return test.GetProperty("items").EnumerateArray().All(child => Test(candidate, child));
        if (kind == "any") return test.GetProperty("items").EnumerateArray().Any(child => Test(candidate, child));
        if (kind == "not") return !Test(candidate, test.GetProperty("item"));

        var left = Read(test.GetProperty("left"), candidate);
        var right = Read(test.GetProperty("right"), candidate);
        if (test.GetProperty("left").ValueKind == JsonValueKind.Object
            && test.GetProperty("left").TryGetProperty("path", out var attribute)
            && attribute.GetString() == "set" && right is string faction
            && Faction.TryNormalize(faction, out var canonical))
        {
            right = canonical;
        }

        return test.GetProperty("cmp").GetString() switch
        {
            "contains" => left is string[] items && right is string item && items.Contains(Keyword(item)),
            "==" => Equals(left, right),
            "!=" => !Equals(left, right),
            ">" => Convert.ToDouble(left) > Convert.ToDouble(right),
            ">=" => Convert.ToDouble(left) >= Convert.ToDouble(right),
            "<" => Convert.ToDouble(left) < Convert.ToDouble(right),
            "<=" => Convert.ToDouble(left) <= Convert.ToDouble(right),
            _ => throw new InvalidDataException("尚未接入筛选比较。"),
        };
    }

    // ─────────────────────────── 选靶 / 选项 ───────────────────────────

    /// <summary>跨效果回指（「…。友方回合结束时，将其消灭」）：取该声明最近一次的结果。</summary>
    public static Card[] RecallTargets(Card source, string declaration) => DesktopTargetContext.Recall(source, declaration);

    /// <summary>执行期选靶（声明 → 选择器 → 提交通道）。</summary>
    public static Task<Card[]> SelectAsync(Card source, string declaration, string scope, CancellationToken ct)
        => DesktopTargetContext.SelectAsync(source, declaration, scope, ct);

    /// <summary>让玩家从候选里选 <paramref name="count"/> 张卡。</summary>
    public static async Task<Card[]> ChooseAsync(Card source, Card[] candidates, int count, CancellationToken ct)
    {
        if (count < 1 || count > candidates.Length) return [];
        Card[] chosen = [];
        var outcome = await MatchOf(source).TargeterManager.RunAsync(async flow =>
        {
            var step = await flow.Step(SelectorTemplates.CardPickerSet,
                new ReferenceSetParameter(candidates.Select(c => c.Ref), count, count,
                    domainValidator: r => r.Value is Card card && candidates.Contains(card))
                {
                    Owner = Owner(source),
                    Action = SelectorActions.Effect,
                });
            while (step.IsFailed && step.Failure == SelectorFailureReason.InvalidSelection)
                step = await flow.Retry<IReadOnlyList<Ref<Entity>>>();
            if (step.IsCancelled) return TargeterResult.Cancelled();
            if (!step.IsOk) return TargeterResult.FromSelectorFailure(step.Failure);
            chosen = step.Value!.Select(r => (Card)r.Value).ToArray();
            return TargeterResult.Ok();
        });
        ct.ThrowIfCancellationRequested();
        return outcome.IsOk ? chosen : [];
    }

    /// <summary>让玩家在若干文本选项里选一个；返回序号（取消＝-1）。</summary>
    public static async Task<int> ChooseOptionAsync(Card source, string[] options, CancellationToken ct)
    {
        string? selected = null;
        var identifiers = options.Select((label, index) => index + ":" + label).ToArray();
        var outcome = await MatchOf(source).TargeterManager.RunAsync(async flow =>
        {
            var step = await flow.Step(SelectorTemplates.Option,
                new IdentifierSetParameter(identifiers, 1, 1) { Owner = Owner(source), Action = SelectorActions.Effect });
            while (step.IsFailed && step.Failure == SelectorFailureReason.InvalidSelection)
                step = await flow.Retry<string>();
            if (step.IsCancelled) return TargeterResult.Cancelled();
            if (!step.IsOk) return TargeterResult.FromSelectorFailure(step.Failure);
            selected = step.Value;
            return TargeterResult.Ok();
        });
        ct.ThrowIfCancellationRequested();
        return outcome.IsOk && selected is not null ? Array.IndexOf(identifiers, selected) : -1;
    }

    /// <summary>「三选一 / 发现」：从候选卡池里让玩家选一张，进手牌。</summary>
    public static async Task<Card[]> DevelopAsync(Card source, string[] definitions, string side, bool random, CancellationToken ct)
    {
        var match = MatchOf(source);
        var offers = (definitions.Length == 0
                ? match.CardService.RegisteredDefinitions
                    .Where(p => !p.Value.Tags.Contains("token") && !p.Value.Tags.Contains("reserved"))
                    .Select(p => p.Key)
                : definitions)
            .Distinct()
            .ToArray();
        if (offers.Any(id => !match.CardService.RegisteredDefinitions.ContainsKey(id))) throw new InvalidDataException("候选卡牌不在本局卡池内。");
        if (random) offers = match.RandomService.PickN(offers, Math.Min(3, offers.Length)).ToArray();
        if (offers.Length == 0) return [];

        string? selected = null;
        var outcome = await match.TargeterManager.RunAsync(async flow =>
        {
            var step = await flow.Step(SelectorTemplates.CardPickerList,
                new IdentifierSetParameter(offers, 1, 1) { Owner = Owner(source), Action = SelectorActions.Effect });
            while (step.IsFailed && step.Failure == SelectorFailureReason.InvalidSelection)
                step = await flow.Retry<IReadOnlyList<string>>();
            if (step.IsCancelled) return TargeterResult.Cancelled();
            if (!step.IsOk) return TargeterResult.FromSelectorFailure(step.Failure);
            selected = step.Value?.SingleOrDefault();
            return selected is null ? TargeterResult.Failed(TargeterFailureReason.InvalidSelection) : TargeterResult.Ok();
        });
        ct.ThrowIfCancellationRequested();
        return outcome.IsOk && selected is not null ? await CreateAsync(source, selected, side, "hand", 1, ct) : [];
    }

    // ─────────────────────────── 状态改写 ───────────────────────────

    /// <summary>改属性（add／set，支持"到本回合结束"的期限）。</summary>
    public static async Task AttributeAsync(Card target, string property, string mode, int value, string duration, CancellationToken ct)
    {
        var field = property switch
        {
            "attack" => CardStatFields.Attack,
            "defense" when target is Hq => CardStatFields.HqHealth,
            "defense" => CardStatFields.Defense,
            "opCost" => CardStatFields.OperateCost,
            "cost" => CardStatFields.DeployCost,
            _ => throw new InvalidDataException("尚未接入属性：" + property),
        };
        var expiry = duration == "turn" ? new ModifierExpiry(GameUpdates.TurnEnd) : null;
        Modifier modifier = mode == "set" ? new SetModifier(field, value, new object(), expiry) : new AddModifier(field, value, new object(), expiry);
        if (target is CardBase card) await card.Modifiers.AddModifierAsync(modifier, ct);
        else if (target is Hq hq) await hq.Modifiers.AddModifierAsync(modifier, ct);
    }

    /// <summary>治疗（单位＝修防御；HQ＝加血量）。</summary>
    public static async Task HealAsync(Card target, int amount, CancellationToken ct)
    {
        if (amount <= 0) return;
        if (target is UnitCard unit) await unit.RepairDefenseAsync(amount, ct);
        else if (target is Hq) await AttributeAsync(target, "defense", "add", amount, "permanent", ct);
    }

    /// <summary>把场上单位退回手牌／移出战场。</summary>
    public static async Task ReturnAsync(Card source, Card target, bool returnToHand, CancellationToken ct)
    {
        if (target is not UnitCard unit || unit.Owner is not { } owner || !unit.TryGetData<UnitStateData>(out var state) || state.Line is null) return;
        var match = MatchOf(source);
        var service = match.CardService;
        if (returnToHand && !ReferenceEquals(state.Line, match.Battlefield.GetSupportLine(owner)))
        {
            await match.EffectRuntime.MoveAsync(unit, "support", ct, source);
            return;
        }

        if (!service.TryResolveDefinitionId(unit, out var id)) throw new InvalidDataException("无法找到回手卡的定义。");
        await match.CommandManager.LeaveBattlefieldAsync(unit, ct);
        await source.Engine.DestroyCard(unit);
        if (returnToHand) await service.CreateAndPlaceToHandAsync(id, owner, ct);
    }

    /// <summary>造卡并放到指定区域（手牌／牌库／前线／支援线）。</summary>
    public static async Task<Card[]> CreateAsync(Card source, string idOrName, string side, string destination, int count, CancellationToken ct)
    {
        var match = MatchOf(source);
        var service = match.CardService;
        var owner = Owner(Viewer(source, side));
        var id = service.RegisteredDefinitions.ContainsKey(idOrName)
            ? idOrName
            : service.RegisteredDefinitions.FirstOrDefault(p => p.Value.Name == idOrName).Key;
        if (id is null) throw new InvalidDataException("本局卡池中没有卡牌：" + idOrName);

        var created = new List<Card>();
        for (var i = 0; i < Math.Clamp(count, 0, 100); i++)
        {
            var card = await service.CreateAsync(id, owner, ct);
            if (destination == "hand") await service.PlaceToHandAsync(card, owner, ct);
            else if (destination == "deck") await service.PlaceIntoDeckShuffledAsync(card, owner, ct);
            else if (card is UnitCard unit && destination == "frontline") await match.EffectRuntime.JoinFrontlineAsync(source, unit, ct: ct);
            else if (destination == "support" && card is UnitCard support) await match.EffectRuntime.JoinSupportLineAsync(source, support, ct: ct);
            else throw new InvalidDataException("卡牌不能加入所选区域。");
            created.Add(card);
        }

        return created.ToArray();
    }

    /// <summary>弃一张手牌（离手 ＋ 销毁 ＋ 发 <c>card.discarded</c>）。</summary>
    public static async Task DiscardAsync(Card source, Card target, CancellationToken ct)
    {
        if (target is not CardBase typed || typed.Owner is not { } owner || !owner.Hand.Contains(typed)) return;
        owner.Hand.Remove(typed);
        await source.Engine.DestroyCard(typed);
        await GameUpdates.EmitCardDiscarded(source.Engine, typed, owner, ct, source: source);
    }

    /// <summary>从牌库里"抽指定的那一张"（走与普通抽牌同一条 burn／事件规则）。</summary>
    public static async Task<Card[]> DrawSpecificAsync(Card source, Card[] candidates, CancellationToken ct)
    {
        var card = candidates.FirstOrDefault(c => c is CardBase typed && typed.Owner is { } owner
            && Enumerable.Range(0, owner.Deck.Count).Any(i => ReferenceEquals(owner.Deck.PeekInstance(i), c)));
        if (card is null) return [];
        await MatchOf(source).EffectRuntime.FetchFromDeckAsync(card, ct, source);
        return card is CardBase drawn && Owner(card).Hand.Contains(drawn) ? [card] : [];
    }
}
