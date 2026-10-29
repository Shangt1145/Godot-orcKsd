using System.Runtime.CompilerServices;
using System.Text.Json;
using Orc.Cards;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Effects;
using Orc.Game.Players;
using Orc.Game.Targeting;
using Kards.Ui.Contracts;
using Kards.Ui.Core;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// 选靶适配层。**v4 适配（2026-10-28）**：引擎删除了 <c>AuthoringTargetContext</c>／<c>AuthoringRuntime</c>
/// （"效果内选靶从不产生 targeter"的老问题由新模型从根上改掉了），选靶统一走
/// <see cref="EffectRuntime.SelectAsync"/> 的提交通道，本类只做「声明 JSON → 选择器」的翻译与候选投影。
/// <para>
/// **与旧实现的语义差异（写清楚，别当成等价）**：
/// </para>
/// <list type="bullet">
/// <item>旧实现会在**预打出**阶段先问玩家一次并把选择存起来（<c>PrepareCard</c>／<c>TryReplayNativeSelection</c>）；
///   新引擎在执行期自己向前端要选择，所以这两步退化为空操作——不是丢失，是职责移到了引擎。</item>
/// <item>旧的 <c>filter</c>（minCost／where 表达式等）**只用于候选收窄**，新引擎的选择器只吃
///   side／zone／unitType／keyword／count／threshold；超出这些的过滤条件在本层做客户端匹配，
///   匹配不出来的条件**明确抛错**，不静默放行。</item>
/// <item><c>prompt</c>（提示文案）在新通道里没有对应字段，暂不下发。</item>
/// </list>
/// </summary>
public sealed class DesktopTargetContext : IDisposable
{
    private readonly Match _match;
    private readonly IReadOnlyDictionary<string,string[]> _plans;
    public DesktopTargetContext(Match match,IReadOnlyDictionary<string,string[]> plans)
    { _match=match;_plans=plans; }

    /// <summary>校验一份选靶声明；形状不认识就抛，不猜。</summary>
    public static void ValidateTarget(JsonElement target)
    {
        var allowed=new[]{"id","kind","side","zone","prompt","filter","reuse"};
        if(string.IsNullOrWhiteSpace(Text(target,"id")))throw new InvalidDataException("声明目标必须具有唯一 id");
        foreach(var p in target.EnumerateObject())if(!allowed.Contains(p.Name,StringComparer.Ordinal))throw new InvalidDataException("OrC-KSD 选靶字段尚无无损适配："+p.Name);
        if(target.TryGetProperty("reuse",out var reuse)&&reuse.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new InvalidDataException("选靶复用标记必须是布尔值");
        if(Text(target,"kind") is not ("unit" or "hq" or "any")||Text(target,"zone") is not ("" or "field" or "frontline" or "support" or "hq"))throw new InvalidDataException("声明选靶区域无效");
        if(target.TryGetProperty("filter",out var filter))ValidateFilter(filter);
        if(Text(target,"side") is not ("" or "any" or "friendly" or "enemy" or "both"))throw new InvalidDataException("选靶阵营无效");
    }

    /// <summary>图 → 作用域键（同一份图在同一张卡上复用同一批预选结果时用）。纯函数。</summary>
    internal static string Scope(JsonElement graph)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(graph.GetRawText())))[..24];

    /// <summary>
    /// 执行期选靶：把声明翻成引擎选择器，交给提交通道（<c>one</c>）或确定性求值（<c>all</c>）。
    /// </summary>
    public static async Task<Card[]> SelectAsync(Card source,string declaration,string scope,CancellationToken ct)
    {
        var chosen=await ResolveAsync(source,declaration,includeSource:false,ct);
        GameLog.Debug("target",$"{OrcRefs.KeyOf(source)} 选靶 {declaration}（scope={scope}）→ "
            +(chosen.Length==0?"空":string.Join("、",chosen.Select(OrcRefs.KeyOf))));
        return chosen;
    }

    /// <summary>跨效果回指（「将其消灭」）：不再发起一次选靶，取该声明最近一次的结果。</summary>
    public static Card[] Recall(Card source,string declaration)
    {
        // **v4 没有引擎侧的跨触发回指**：引擎保证的只是"同一执行链上的目标仍在数据面"，
        // 而「打出时选中 → 回合结束时消灭它」是 card.played 与 turn.end 两条不同的链，
        // 靠执行链根本带不过去。所以回指必须由宿主自己记（见 Remember）。
        if (!Recalled.TryGetValue(source, out var table)) return [];
        var id = DeclarationId(declaration);
        if (id.Length == 0 || !table.TryGetValue(id, out var picked)) return [];
        // 记住的是卡引用，不是快照：期间它可能已经死了/离场了，回来时必须现查存活。
        return picked.Where(OrcRefs.IsAlive).ToArray();
    }

    /// <summary>回指表：宿主卡 →（声明 id → 最近一次选中的卡）。随卡存活，卡没了表项一起回收。</summary>
    private static readonly ConditionalWeakTable<Card, Dictionary<string, Card[]>> Recalled = new();

    private static string DeclarationId(string declaration)
    {
        try
        {
            using var document = JsonDocument.Parse(declaration);
            return document.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()! : "";
        }
        catch (JsonException) { return ""; }
    }

    private static void Remember(Card source, string id, Card[] picked)
    {
        if (id.Length == 0 || picked.Length == 0) return; // 空选＝这次没选到，不覆盖上一次的有效回指
        var table = Recalled.GetOrCreateValue(source);
        lock (table) { table[id] = picked; }
    }

    /// <summary>旧预打出捕获的提交重放；新引擎不需要，保留签名给调用方。</summary>
    internal bool TryReplayNativeSelection(ISelectorInstance selector)
    {
        _ = selector;
        return false;
    }

    /// <summary>旧预打出捕获的准备；新引擎在执行期问，不需要提前准备。</summary>
    internal void PrepareCard(CardBase card) => _ = card;

    /// <summary>
    /// 手牌里"需要玩家指"的卡与其候选（编辑器提示用）。
    /// **v4 退化**：旧实现靠 <c>AuthoringTargetContext.ResolveCandidates</c> 做**纯查询**；
    /// 该入口随 <c>EffectRuntime.QueryTargetCandidates</c> 一起删除，而唯一存活的
    /// <c>EffectRuntime.SelectAsync</c> 在返回名单后会**对每个目标触发"指向"通知**——
    /// 拿它当查询用会在玩家还没选的时候就触发指向类效果（真实副作用）。
    /// 宁可这里返回空（界面不显示候选提示），也不制造隐形副作用；等引擎补回纯查询入口再恢复。
    /// </summary>
    public IReadOnlyList<UiOrderTargeting> ReadOrderTargets(Player viewer) => [];

    /// <summary>
    /// 这张卡在该时机是否有「需要玩家指」的效果。
    /// 旧实现查 <c>DynamicPassiveEffect.Snapshot.Root.MainTrigger.Hooks</c>——v4 里效果不再是运行时对象，
    /// 改为查编排目录：该卡实体上是否挂着 <c>engine#card.played</c>／<c>engine#unit.deployed</c> 的边。
    /// </summary>
    public static bool HasInteractivePlayEffect(CardBase card,string trigger)
    {
        var hook=trigger=="deploy"?"unit.deployed":trigger is "play" or "order"?"card.played":null;
        if(hook is null)return false;
        var address="engine#"+hook;
        // v4 寻址口径：卡宿主实体＝card.ModelEntity；组件实体的宿主引用是 **实体**（Host）／**绑定对象**（Bound）。
        // 旧写法拿 CardBase 去和 Host 比引用恒为 false（Host 是 OrchestrationEntity）——这里按 Bound 比。
        return card.ModelEntity is { } model
            && model.Runtime.Entities.Any(e=>ReferenceEquals(e.Bound,card)
                && e.Type.Mounts.Any(m=>string.Equals(m.Target,address,StringComparison.Ordinal)));
    }

    /// <summary>把声明翻成引擎选择器，经提交通道取结果（<c>one</c>＝外部裁定）。</summary>
    private static async Task<Card[]> ResolveAsync(Card source,string declaration,bool includeSource,CancellationToken ct)
    {
        using var doc=JsonDocument.Parse(declaration);
        var target=doc.RootElement;ValidateTarget(target);
        var runtime=EffectRuntime.ResolveFor(source)??throw new InvalidOperationException("原生效果服务未装配");

        var kind=Text(target,"kind");var side=Text(target,"side");var zone=Text(target,"zone");
        var selector=new EffectSelector("one",NormalizeSide(side),NormalizeZone(zone),KindUnitType(target));

        var picked=await runtime.SelectAsync(source,selector,ct);
        IEnumerable<Card> matches=picked;
        // kind 只影响"单位／HQ／都行"：选择器没有 HQ 维度，这里按类型收窄。
        if(kind=="unit")matches=matches.Where(c=>c is not Hq);
        else if(kind=="hq")matches=matches.Where(c=>c is Hq);
        if(target.TryGetProperty("filter",out var filter))matches=matches.Where(c=>DesktopNativeRuntime.Matches(c,filter.GetRawText()));
        // 部署指向：这张牌即将落位，但在预打出阶段场面里还查不到它。
        if(includeSource&&source is UnitCard self&&kind!="hq")matches=matches.Prepend(self);
        var resolved=matches.ToArray();
        // 记下这次选中的目标：同一张卡的后续触发（turn.end 等）靠 Recall 回指它。
        Remember(source, Text(target,"id"), resolved);
        return resolved;
    }

    private static string NormalizeSide(string side)=>side is "any" or ""?"both":side;
    private static string? NormalizeZone(string zone)=>zone is "frontline" or "support"?zone:null;
    private static string? KindUnitType(JsonElement target)
        => target.TryGetProperty("filter",out var f)&&f.ValueKind==JsonValueKind.Object
            &&f.TryGetProperty("unitType",out var t)&&t.ValueKind==JsonValueKind.String?t.GetString():null;

    private static string Text(JsonElement e,string key)=>e.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()!:"";
    public void Dispose(){ }

    // ── 过滤条件：引擎侧原本由 AuthoringRuntime 提供，v4 删除后搬到本仓库 ──
    // 唯一实现放在 DesktopNativeRuntime（DIY 生成的 csx 也要调它，必须是 public）；
    // 这里只做转调，避免两套判据漂移。遇到不认识的条件抛错（不静默放行成"全选"）。

    internal static void ValidateFilter(JsonElement filter)=>DesktopNativeRuntime.ValidateFilter(filter);

    /// <summary>筛选条件判定（供 <see cref="DesktopEffectRuntime"/> 转调；json＝筛选对象原文）。</summary>
    internal static bool Matches(Card card,string json)=>DesktopNativeRuntime.Matches(card,json);
}
