using System.Text.RegularExpressions;
using Orc.Game.Cards;
using Orc.Game.EffectParsing.Dsl;

namespace Kards.Ui.OrcBridge.OrcText;

// ─────────────────────────────────────────────────────────────────────────────
// OrC 语义层（移植自 orc.js 的 target/action/actionsFor/basicParse/compile）。
// 与 JS 版的根本差异：JS 产出旧动作系统 op 再经 primitives.js 降级——那条链不在
// OrC-KSD 引擎编译面内；本移植把语义动作**直接映射到引擎 DSL 面**（DslOpRegistry
// 21 op ＋ 触发模板 31 张 ＋ DslSelector 三选靶），引擎装不下的语义 → needsCsx
// 留痕 op ＋ 诊断（待核对），绝不擅自造引擎没有的原语。
// 每行独立解析：某行失败只把该行标 needsCsx，其余行照常（批量导入按行核对）。
// ─────────────────────────────────────────────────────────────────────────────

internal static partial class OrcParser
{
    // ── 触发词 → 引擎触发模板（id, 槽位）。模板/hook 名逐字对齐 Templates/*.tpl.json。──
    private static readonly IReadOnlyDictionary<string, (string Tpl, string Slot)> Heads =
        new Dictionary<string, (string, string)>
        {
            ["部署"] = ("deploy_basic", "on_deploy"),
            ["亡计"] = ("death_basic", "on_event"),
            ["遗言"] = ("death_basic", "on_event"),
            ["使用时"] = ("played_basic", "on_event"),
            ["己方回合开始时"] = ("turn_start_basic", "on_event"),
            ["友方回合开始时"] = ("turn_start_basic", "on_event"),
            ["己方回合结束时"] = ("turn_end_basic", "on_event"),
            ["友方回合结束时"] = ("turn_end_basic", "on_event"),
        };

    // unit 卡无头自事件 → 触发模板（hooks: unit.acted? no—— attack_basic=攻击时, damaged_basic=card.damaged）。
    private static readonly IReadOnlyDictionary<string, (string Tpl, string Slot)> SelfEvents =
        new Dictionary<string, (string, string)>
        {
            ["攻击时"] = ("attack_basic", "on_event"),
            ["受到伤害时"] = ("damaged_basic", "on_event"),
            ["造成伤害时"] = ("damage_dealt_basic", "on_event"),
            ["攻击后"] = ("acted_basic", "on_event"),
        };

    // 监听句（其他友方 X 部署时/被消灭时/受到伤害时）→ 监听模板（filter 走兵种/词条维）。
    private static readonly IReadOnlyDictionary<string, (string Tpl, string Slot, string Ref)> Listeners =
        new Dictionary<string, (string, string, string)>
        {
            ["部署时"] = ("deploy_listener", "on_event", "eventUnit"),
            ["被消灭时"] = ("killed_basic", "on_event", "eventUnit"),
            ["受到伤害时"] = ("damaged_basic", "on_event", "eventVictim"),
        };

    private sealed class Fx
    {
        public DslSelector? LastTarget;
        public readonly List<OrcDiagnostic> Diagnostics = new();
        public int Line;
    }

    /// <summary>compile 主入口：文本 → 触发效果序列（引擎 DSL 面）＋ 卡面词条 ＋ 诊断。</summary>
    public static OrcCompileResult Compile(string text, string cardType, Wordbook wordbook)
    {
        var source = OrcLexical.Normalize(text, wordbook.NormalizeWords);
        var effects = new List<OrcParsedEffect>();
        var keywords = new List<OrcKeywordDecl>();
        var diagnostics = new List<OrcDiagnostic>();
        var fx = new Fx();
        var sawBody = false;

        foreach (var raw in OrcLexical.Split(source, "\n。；;"))
        {
            fx.Line++;
            var items = OrcLexical.KeywordLine(raw, wordbook.KeywordNames, wordbook.NormalizeWords);
            if (items is not null)
            {
                foreach (var (id, value, quantified) in items)
                {
                    var decl = new OrcKeywordDecl(id, value, quantified);
                    if (!keywords.Any(k => k.Id == id)) keywords.Add(decl);
                }
                continue;
            }
            if (raw.Length == 0) continue;
            sawBody = true;

            try
            {
                effects.Add(ParseLine(raw, cardType, wordbook, fx));
            }
            catch (OrcReject e)
            {
                // 行级待核对：该行整体 needsCsx 留痕（原语面外 ≠ 丢弃）。
                effects.Add(new OrcParsedEffect(FallbackTemplate(cardType), FallbackSlot(cardType),
                    [new DslOp(DslOpRegistry.NeedsCsxOpName, script: e.Message)]));
                diagnostics.Add(new OrcDiagnostic("unparsed-text", $"第 {fx.Line} 行待核对：{e.Message}"));
            }
        }

        if (sawBody && effects.Count == 0)
            diagnostics.Add(new OrcDiagnostic("empty-effect", "文本非空，但未生成任何效果，需核对。"));
        return new OrcCompileResult(source, effects, keywords, diagnostics);
    }

    private static string FallbackTemplate(string cardType) =>
        cardType == "order" ? "played_basic" : cardType == "counter" ? "counter_basic" : "deploy_basic";

    private static string FallbackSlot(string cardType) => cardType == "unit" ? "on_deploy" : "on_event";

    /// <summary>单行 → 一个触发效果（触发头识别 ＋ 动作体解析）。</summary>
    private static OrcParsedEffect ParseLine(string line, string cardType, Wordbook wordbook, Fx fx)
    {
        // ① 显式触发头："部署：…" / "亡计，…"。
        var head = HeadPattern().Match(line);
        if (head.Success && Heads.TryGetValue(head.Groups[1].Value, out var mapped))
            return new(mapped.Tpl, mapped.Slot, ActionsFor(head.Groups[2].Value, wordbook, fx));

        if (cardType != "unit")
            return new(FallbackTemplate(cardType), FallbackSlot(cardType),
                ActionsFor(line, wordbook, fx)); // order/counter：整行就是动作体

        // ② unit 自事件："攻击时，…"。
        var self = SelfEventPattern().Match(line);
        if (self.Success && SelfEvents.TryGetValue(self.Groups[1].Value, out var selfMap))
        {
            fx.LastTarget = new DslSelector("self");
            return new(selfMap.Tpl, selfMap.Slot, ActionsFor(self.Groups[2].Value, wordbook, fx));
        }

        // ③ 监听句："其他友方 X 部署时，…"（"敌方"面外 → 待核对）。X 须为泛指"单位"——
        // 引擎监听模板的填写面没有 filter 槽，兵种/词条限定的监听无法无损承载 → 拒绝。
        var listener = ListenerPattern().Match(line);
        if (listener.Success && Listeners.TryGetValue(listener.Groups[4].Value, out var listenMap))
        {
            if (listener.Groups[2].Value == "敌方")
                throw new OrcReject("监听敌方单位事件超出引擎监听模板面（watchOpponent 不在 DSL 填写面）");
            if (listener.Groups[3].Value.Trim() is not ("单位" or "其他单位"))
                throw new OrcReject($"监听目标的限定（{listener.Groups[3].Value}）超出引擎监听模板填写面（无 filter 槽）");
            // 动作体内的"其/该单位"指向事件载荷，引擎 DSL 无 ref 选择器——LastTarget 保持空，
            // Target("其") 会拒绝而非误导性映射。
            return new(listenMap.Tpl, listenMap.Slot, ActionsFor(listener.Groups[5].Value, wordbook, fx));
        }

        // ④ 被动句（具有 X / 无法… / 双倍伤害 / 每具有…）→ 引擎无被动 op 面 → 待核对。
        if (PassivePattern().IsMatch(line))
            throw new OrcReject($"被动句（{Truncate(line)}）需要持续态修饰或光环，引擎 DSL 触发面未覆盖");

        // ⑤ 无头 unit 行＝部署触发（KARDS 惯例：正文即部署效果）。
        return new("deploy_basic", "on_deploy", ActionsFor(line, wordbook, fx));
    }

    // ───────────────────────── 动作切分 ─────────────────────────

    /// <summary>多子句切分（js actionsFor）：连接词前缀剥除 → 逗号切分逐句 Action。期限后缀提取后随句传入。</summary>
    private static List<DslOp> ActionsFor(string text, Wordbook wordbook, Fx fx)
    {
        var s = text.Trim();
        s = ThenPrefix().Replace(s, "").Trim();
        // "，直到回合结束" / "，本回合" 是前句的期限修饰（引擎 buff/costMod 的 until 参），提取后随子句传入。
        var temporary = TemporarySuffix().Match(s);
        string until = "";
        if (temporary.Success)
        {
            var tail = temporary.Value.TrimStart('，', ',');
            until = tail.Contains("下个友方回合") ? Untils.NextOwnerTurnStart : Untils.TurnEnd;
            s = s[..temporary.Index].TrimEnd('，', ',').Trim();
        }
        if (ControlPattern().IsMatch(s))
            throw new OrcReject($"控制流句（{Truncate(s)}）——抉择/如果/每有 超出引擎 DSL 原语面（无 branch/loop/choice）");

        var parts = OrcLexical.Split(s, "，,");
        var ops = new List<DslOp>();
        for (var i = 0; i < parts.Count; i++)
            ops.AddRange(Action(parts[i].Trim(), wordbook, fx, i == parts.Count - 1 ? until : ""));
        return ops;
    }

    /// <summary>单子句 → op 序列（严格引擎 21 op 面；读不进来的语义 → OrcReject 待核对）。</summary>
    private static List<DslOp> Action(string text, Wordbook wordbook, Fx fx, string until = "")
    {
        var s = text.Trim();
        if (s.Length == 0) return [];

        // ① 嵌套引号效果：使 X 获得：“…”（引擎 nested 原语＝引号内效果文本递归解析）。
        var nested = NestedGainPattern().Match(s);
        if (nested.Success)
        {
            var target = Target(nested.Groups[1].Value, wordbook, fx);
            var inner = Compile(nested.Groups[2].Value, "unit", wordbook);
            if (inner.Diagnostics.Count > 0 || inner.Effects.Count == 0)
                throw new OrcReject($"引号内嵌效果无法完整解析：{Truncate(nested.Groups[2].Value)}");
            var instances = inner.Effects.Select(e => new DslEffectInstance(e.TemplateId,
                new Dictionary<string, DslSlotFill> { [e.Slot] = new(e.Ops) })).ToList();
            return [new DslOp(DslOpRegistry.NestedOpName, target: target, nested: instances)];
        }

        // ② 抽牌：抽 N 张牌（引擎 draw 无 side——"敌方抽牌"面外）。
        var draw = DrawPattern().Match(s);
        if (draw.Success)
        {
            if (draw.Groups[1].Success)
                throw new OrcReject("使敌方/对手抽牌超出引擎 draw 原语面（无 side 参数）");
            var n = OrcPredicates.Value(draw.Groups[2].Value);
            if (n is < 1 or > 10) throw new OrcReject($"抽牌数量 {n} 超出引擎值域（1–10）");
            return [new DslOp("draw", count: n)];
        }

        // ③ 指挥点 / 槽：获得|失去 N 个(临时)?指挥点(槽)?（引擎 gain/losePoint/Slot 无 side）。
        var res = ResourcePattern().Match(s);
        if (res.Success)
        {
            if (res.Groups[1].Success)
                throw new OrcReject("使敌方/对手获得或失去指挥点超出引擎资源原语面（无 side 参数）");
            var n = OrcPredicates.Value(res.Groups[3].Value);
            var isSlot = res.Groups[4].Success;
            var lose = res.Groups[2].Value is "失去" or "减少";
            var op = (isSlot, lose) switch
            {
                (false, false) => "gainPoint", (false, true) => "losePoint",
                (true, false) => "gainSlot", (true, true) => "loseSlot",
            };
            if (n is < 1 or > 10) throw new OrcReject($"资源数量 {n} 超出引擎值域（1–10）");
            return [new DslOp(op, amount: n)];
        }

        // ④ 伤害：对 X 造成 N 点伤害。
        var dmg = DamagePattern().Match(s);
        if (dmg.Success)
        {
            var target = Target(dmg.Groups[1].Value, wordbook, fx);
            var n = OrcPredicates.Value(dmg.Groups[2].Value);
            if (n is < 1 or > 99) throw new OrcReject($"伤害 {n} 超出引擎值域（1–99）");
            return [new DslOp("damage", target: target, amount: n)];
        }

        // ⑤ 单动词目标句：消灭/压制/解除压制/抑制 X；移除 → 面外。
        var verb = VerbPattern().Match(s);
        if (verb.Success)
        {
            var kind = verb.Groups[1].Value;
            if (kind == "移除") throw new OrcReject("「移除」（leaveUnit）不在引擎原语面");
            var target = Target(verb.Groups[2].Value, wordbook, fx);
            return [kind switch
            {
                "消灭" => new DslOp("destroy", target: target),
                "压制" => new DslOp("pin", target: target),
                "解除压制" => new DslOp("pin", target: target), // 引擎 pin 模板无 enabled 参——语义有损，借诊断提示
                _ => new DslOp("silence", target: target),
            }];
        }
        if (s.StartsWith("解除压制")) throw new OrcReject("「解除压制」不在引擎 pin 原语面（模板无 enabled 参）");

        // ⑥ 移动：将 X 移至 前线|支援阵线（主语省略＝本单位，KARDS 惯例）。
        if (s.StartsWith("移至") || s.StartsWith("移到")) s = "本单位" + s;
        var mv = MovePattern().Match(s);
        if (mv.Success)
        {
            var target = Target(mv.Groups[1].Value, wordbook, fx);
            return [new DslOp("move", target: target, zone: mv.Groups[2].Value.Contains("前线") ? "frontline" : "support")];
        }

        // ⑦ 返回手牌 / 撤退 → 面外（无 returnUnit）。
        if (ReturnPattern().IsMatch(s))
            throw new OrcReject("「返回手牌/撤退」（returnUnit）不在引擎原语面");

        // ⑧ 老兵 / 揭示。
        var up = UpgradePattern().Match(s);
        if (up.Success) return [new DslOp("upgrade", target: Target(up.Groups[1].Value, wordbook, fx))];
        var rv = RevealPattern().Match(s);
        if (rv.Success) return [new DslOp("reveal", target: Target(rv.Groups[1].Value, wordbook, fx))];

        // ⑨ 卡牌生成：将[N 张]“卡名”加入手牌（addToHand 带 count 参）/ 洗入卡组。
        var hand = AddHandPattern().Match(s);
        if (hand.Success)
        {
            var n = hand.Groups[1].Success ? OrcLexical.Number(hand.Groups[1].Value) ?? 1 : 1;
            return [new DslOp("addToHand", name: hand.Groups[2].Value, count: n)];
        }
        var shf = ShufflePattern().Match(s);
        if (shf.Success) return [new DslOp("shuffleIn", name: shf.Groups[1].Value)];

        // ⑩ 增益（buff/costMod/grant）：句式对齐引擎解析口径（"使 X 获得 …"）。期限（until）仅 buff/costMod 承载。
        if (TryStatMods(s, wordbook, fx, until, out var statOps)) return statOps;
        var kw = GrantPattern().Match(s);
        if (kw.Success)
        {
            var target = Target(kw.Groups[1].Value, wordbook, fx);
            var id = wordbook.LookupKeyword(kw.Groups[2].Value)
                ?? throw new OrcReject($"未知词条“{kw.Groups[2].Value}”");
            var value = kw.Groups[3].Success ? OrcLexical.Number(kw.Groups[3].Value) ?? 1 : 1;
            return [new DslOp("grant", target: target, keyword: id, value: value)];
        }

        throw new OrcReject($"无法映射到引擎原语面：“{Truncate(s)}”");
    }

    /// <summary>攻防增益与行动费修饰。句式面：+A/+D、获得 +N 攻击力、属性 增加/减少 N、行动费 ±N、获得 N 行动花费。</summary>
    private static bool TryStatMods(string s, Wordbook wordbook, Fx fx, string until, out List<DslOp> ops)
    {
        ops = [];
        // "+A/+D" 复合："使 X 获得 +2/+1"
        var both = BuffBothPattern().Match(s);
        if (both.Success)
        {
            var target = Target(both.Groups[1].Value, wordbook, fx);
            ops.Add(new DslOp("buff", target: target, until: NullIfEmpty(until),
                attack: int.Parse(both.Groups[2].Value), defense: int.Parse(both.Groups[3].Value)));
            return true;
        }
        if (SetToPattern().IsMatch(s)) throw new OrcReject("「变为/设为」（set 语义）超出引擎 buff 原语面（仅增量）");
        // "获得 +N 攻击力"（数字在属性词前——alpha.27 DIY 口径）与"防御力 减少 N"（变化式）。
        var before = BuffBeforePattern().Match(s);
        if (before.Success)
        {
            var target = Target(before.Groups[1].Value, wordbook, fx);
            var v = int.Parse(before.Groups[2].Value);
            var field = OrcPredicates.AttributeName(before.Groups[3].Value);
            if (field != "attack" && field != "defense")
                throw new OrcReject($"增益字段 {before.Groups[3].Value} 不在引擎 buff 原语面（仅攻击力/防御力）");
            ops.Add(new DslOp("buff", target: target, until: NullIfEmpty(until),
                attack: field == "attack" ? v : null, defense: field == "defense" ? v : null));
            return true;
        }
        var change = BuffChangePattern().Match(s);
        if (change.Success)
        {
            var target = Target(change.Groups[1].Value, wordbook, fx);
            var v = int.Parse(change.Groups[3].Value);
            if (change.Groups[2].Value is "减少" or "减") v = -v;
            if (v == 0) throw new OrcReject("增益为 0 无语义");
            var field = OrcPredicates.AttributeName(change.Groups[4].Value);
            if (field != "attack" && field != "defense")
                throw new OrcReject($"增益字段 {change.Groups[4].Value} 不在引擎 buff 原语面（仅攻击力/防御力）");
            ops.Add(new DslOp("buff", target: target, until: NullIfEmpty(until),
                attack: field == "attack" ? v : null, defense: field == "defense" ? v : null));
            return true;
        }
        // 行动费："使 X 行动费 +1" / "使 X 行动花费 增加 2" / "使 X 获得 1 行动花费"。
        var cost = CostModPattern().Match(s);
        if (cost.Success)
        {
            var target = Target(cost.Groups[1].Value, wordbook, fx);
            var v = int.Parse(cost.Groups[3].Value);
            if (cost.Groups[2].Value is "减少" or "减") v = -v;
            if (v == 0 || v is < -20 or > 20) throw new OrcReject($"行动费变化 {v} 超出引擎值域（-20–20，非零）");
            ops.Add(new DslOp("costMod", target: target, amount: v, until: NullIfEmpty(until)));
            return true;
        }
        var gain = GainOpCostPattern().Match(s);
        if (gain.Success)
        {
            var target = Target(gain.Groups[1].Value, wordbook, fx);
            var v = OrcLexical.Number(gain.Groups[2].Value)
                ?? throw new OrcReject($"行动费变化非静态数字：“{gain.Groups[2].Value}”");
            if (v == 0 || v is < -20 or > 20) throw new OrcReject($"行动费变化 {v} 超出引擎值域（-20–20，非零）");
            ops.Add(new DslOp("costMod", target: target, amount: v, until: NullIfEmpty(until)));
            return true;
        }
        return false;
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    // ───────────────────────── 选靶 ─────────────────────────

    /// <summary>
    /// 目标短语 → 引擎 DslSelector（sel ∈ self/all/random/one；zone ∈ 场/frontline/support）。
    /// js 口径：无数量词且非双面 → all；相邻/手牌/卡组/总部/其他(排除自身) → 面外拒绝。
    /// </summary>
    private static DslSelector Target(string text, Wordbook wordbook, Fx fx)
    {
        var s = text.Trim();
        if (s is "本单位" or "此单位") return fx.LastTarget = new DslSelector("self");
        if (s is "其" or "该单位" or "该牌" or "此牌")
            return fx.LastTarget ?? throw new OrcReject($"“{s}”无先行目标可指代");

        var side = SideOf(s, out var sideWord);
        if (sideWord.Length > 0) s = s.Replace(sideWord, "").Trim();
        if (UnsupportTargetPattern().IsMatch(s))
            throw new OrcReject($"目标“{Truncate(text)}”超出引擎选靶面（相邻/手牌/卡组/总部/其他 均不在 selector 面）");
        s = QuantityWords().Replace(s, "").Trim();

        var filter = s.Length > 0 && s != "单位" && s != "单位目标" && s != "目标"
            ? OrcPredicates.UnitFilter(s.Replace("目标", "单位"), wordbook) : null;
        // 引擎 DSL JSON 通道（FilterDto）无 threshold 维——内存面支持但 CompileDsl 往返必丢，
        // 带阈值的过滤句在此拒绝待核对（否则编译期以引擎异常文本失败，诊断不可读）。
        if (filter?.ThresholdField is not null)
            throw new OrcReject($"属性阈值过滤（{s}）超出引擎 DSL JSON 通道（FilterDto 无 threshold 维）");

        var random = RandomWord().IsMatch(text);
        if (random) return fx.LastTarget = new DslSelector("random", side, filter: filter, count: 1);
        // js 口径：有"所有|全部"或（无数量词且 side≠both）→ all；否则 one。
        var quantified = QuantityMatch().IsMatch(text);
        if (AllWord().IsMatch(text) || (!quantified && side != "both"))
            return fx.LastTarget = new DslSelector("all", side, filter: filter);
        return fx.LastTarget = new DslSelector("one", side == "both" ? "any" : side, filter: filter);
    }

    private static string SideOf(string s, out string word)
    {
        word = EnemyWord().Match(s).Value switch { "" => "", var v => v };
        if (word.Length > 0) return "enemy";
        word = FriendlyWord().Match(s).Value;
        return word.Length > 0 ? "friendly" : "both";
    }

    private static string Truncate(string s) => s.Length <= 40 ? s : s[..40] + "…";

    // ── 正则面（词序对齐 js 版）──
    [GeneratedRegex(@"^(部署|亡计|遗言|使用时|己方回合开始时|己方回合结束时|友方回合开始时|友方回合结束时)[：:,，]\s*(.+)$")]
    private static partial Regex HeadPattern();
    [GeneratedRegex(@"^(?:本单位)?(攻击时|被攻击时|受到伤害时|造成伤害时|移至前线时|攻击后)[，,](.+)$")]
    private static partial Regex SelfEventPattern();
    [GeneratedRegex(@"^(其他)?(友方|敌方)(.+?)(部署时|被消灭时|受到伤害时)[，,](.+)$")]
    private static partial Regex ListenerPattern();
    [GeneratedRegex(@"具有|无法|双倍伤害|每具有|每拥有")]
    private static partial Regex PassivePattern();
    [GeneratedRegex(@"^(然后|并且|并|则)")]
    private static partial Regex ThenPrefix();
    [GeneratedRegex(@"[，,]?(?:直到(?:本|下个友方)?回合结束|本回合(?:内)?$)$")]
    private static partial Regex TemporarySuffix();
    [GeneratedRegex(@"^(抉择[：:]|如果|若|每有|每具有)")]
    private static partial Regex ControlPattern();
    [GeneratedRegex(@"^(?:使)?(.+?)获得[：]?[“「""](.+)[""」”]$")]
    private static partial Regex NestedGainPattern();
    [GeneratedRegex(@"^(?:(友方|我方|敌方|对手)(?:玩家)?)?抽(?:取)?([\d一二两三四五六七八九十百]+)张(?:卡)?牌$")]
    private static partial Regex DrawPattern();
    [GeneratedRegex(@"^(?:使)?(?:(友方|我方|敌方|对手)(?:玩家)?)?(?:额外)?(获得|增加|失去|减少)\s*([\d一二两三四五六七八九十百]+)\s*(?:个|点)?(?:临时)?指挥点(槽)?$")]
    private static partial Regex ResourcePattern();
    [GeneratedRegex(@"^对(.+?)造成([\d一二两三四五六七八九十百]+)点伤害$")]
    private static partial Regex DamagePattern();
    [GeneratedRegex(@"^(消灭|压制|解除压制|抑制|移除)(.+)$")]
    private static partial Regex VerbPattern();
    [GeneratedRegex(@"^(?:将|使)?(.+?)(?:移至|移到)(前线|支援阵线|支援线)$")]
    private static partial Regex MovePattern();
    [GeneratedRegex(@"(?:返回|送回)(?:手牌|手中)|撤退$")]
    private static partial Regex ReturnPattern();
    [GeneratedRegex(@"^(?:将|使)?(.+?)升为老兵$")]
    private static partial Regex UpgradePattern();
    [GeneratedRegex(@"^(?:揭示|检视)(.+)$")]
    private static partial Regex RevealPattern();
    [GeneratedRegex(@"^(?:将)?(?:([\d一二两三四五六七八九十百]+)\s*(?:张|个|辆|架)\s*)?[“「""](.+)[""」”](?:加入|添加到)(?:友方|我方|敌方)?(?:手牌|手中)$")]
    private static partial Regex AddHandPattern();
    [GeneratedRegex(@"^(?:将)?[“「""](.+)[""」”]洗入(?:卡组|牌库)$")]
    private static partial Regex ShufflePattern();
    [GeneratedRegex(@"^(?:使)?(.+?)获得\s*([+-]\d+)\s*(?:/|(?=[+-]))\s*([+-]?\d+)$")]
    private static partial Regex BuffBothPattern();
    [GeneratedRegex(@"^(?:使)?(.+?)获得\s*([+-]?\d+)\s*(攻击力|防御力)$")]
    private static partial Regex BuffBeforePattern();
    [GeneratedRegex(@"^(?:使)?(.+?)(攻击力|防御力)(增加|减少|减|加)?\s*([+-]?\d+)$")]
    private static partial Regex BuffChangePattern();
    [GeneratedRegex(@"变为|设为|等同于|等于")]
    private static partial Regex SetToPattern();
    [GeneratedRegex(@"^(?:使)?(.+?)行动费(?:用)?\s*(增加|减少|减|加)?\s*([+-]?\d+)$")]
    private static partial Regex CostModPattern();
    [GeneratedRegex(@"^(?:使)?(.+?)获得\s*([+-]?[\d一二两三四五六七八九十百]+)\s*行动花费$")]
    private static partial Regex GainOpCostPattern();
    [GeneratedRegex(@"^(?:使)?(.+?)(?:获得|具有)(.+?)([\d一二两三四五六七八九十百]+)?$")]
    private static partial Regex GrantPattern();
    [GeneratedRegex(@"相邻|手牌|卡组|牌库|总部|其他|其它")]
    private static partial Regex UnsupportTargetPattern();
    [GeneratedRegex(@"随机")]
    private static partial Regex RandomWord();
    [GeneratedRegex(@"所有|全部")]
    private static partial Regex AllWord();
    [GeneratedRegex(@"(?:\d+|一)(?:个|辆|架|支|名)")]
    private static partial Regex QuantityMatch();
    [GeneratedRegex(@"所有|全部|随机|使|一个|一辆|一架|一支|一名|1个|1辆|1架|1支|1名")]
    private static partial Regex QuantityWords();
    [GeneratedRegex(@"敌方|对手")]
    private static partial Regex EnemyWord();
    [GeneratedRegex(@"友方|我方")]
    private static partial Regex FriendlyWord();
}
