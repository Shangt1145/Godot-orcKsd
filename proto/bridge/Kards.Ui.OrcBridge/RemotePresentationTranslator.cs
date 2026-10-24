using System.Text.Json;
using System.Text.Json.Nodes;
using Kards.Ui.Contracts;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// 远端演出翻译：把 <c>notify/segment</c> 的 JSON 条目翻译成 <see cref="UiPresentationStep"/>。
///
/// <para>
/// **为什么不能复用 <c>OrcUpdateTranslator</c>**：那套的入参是引擎活对象
/// （<c>Func&lt;Card, Player, string, int, UiCardView?&gt;</c>、payload 里直接是 <c>Card</c>／<c>Player</c>），
/// 而远端 payload 里只有 <c>{"$ref": "&lt;GUID&gt;"}</c>。两边唯一的共同点是**键**——
/// <c>$ref</c> 与 <c>UiCardView.Uid</c> 都是 <c>entity.Id.ToString("N")</c>，
/// 所以远端可以用 <c>$ref</c> 在投影里查卡。
/// </para>
///
/// <para>
/// 使用动作前后快照翻译抽牌、部署、指令、伤害、死亡与状态反馈。
/// 移动由同一演出队列应用快照时的阵型位移动画呈现。
/// </para>
///
/// <para>
/// **不猜**：<c>{"$hidden":true}</c> 的条目没有 <c>$ref</c>，一律返回 null 并被跳过；
/// 不按名字去投影里"找一张像它的卡"。
/// </para>
/// </summary>
internal static class RemotePresentationTranslator
{
    public static IReadOnlyList<UiPresentationStep> Translate(
        IReadOnlyList<JsonNode?> entries, UiMatchView before, UiMatchView after, int viewerSeat = 0,
        Func<string, string?>? artLookup = null)
    {
        var index = IndexCards(after);
        var previous = IndexCards(before);
        var known = new Dictionary<string, UiCardView>(previous, StringComparer.Ordinal);
        foreach (var pair in index) known[pair.Key] = pair.Value;
        var steps = new List<UiPresentationStep>();
        var announcedTurns = new HashSet<(string Side, int Turn)>();
        var arrivals = new HashSet<string>();
        var deployments = new HashSet<string>();
        var impacts = new Dictionary<string, UiOrderImpact>();
        var assaults = new Dictionary<string, (string Source, int Amount)>();

        string Side(JsonObject obj, JsonObject? data) => obj["playerSeat"] is { } seat
            ? seat.GetValue<int>() == viewerSeat ? "self" : "enemy" : SideOf(Field(data, "Player"), after);
        UiCardView? Card(JsonObject obj, JsonObject? data)
        {
            if (Resolve(Field(data, "Card") ?? Field(data, "Unit"), known) is { } card) return card;
            if (obj["cardView"]?.Deserialize<UiCardView>(new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
                { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }) is not { } extra) return null;
            var side = obj["ownerSeat"]?.GetValue<int>() == viewerSeat ? "self" : "enemy";
            extra = extra with { OwnerSide = side, Definition = extra.Definition with
                { ArtPath = artLookup?.Invoke(extra.Definition.CardId) ?? extra.Definition.ArtPath } };
            known[extra.Uid] = extra;
            return extra;
        }
        void Hit(UiCardView? card, int? amount)
        {
            if (card is null || !previous.TryGetValue(card.Uid, out var old) || old.Visibility != Visibility.Full) return;
            index.TryGetValue(card.Uid, out var current);
            var damage = amount ?? Math.Max(0, (old.Health ?? 0) - (current?.Health ?? 0));
            if (damage <= 0) return;
            if (!impacts.ContainsKey(card.Uid) || amount is not null)
                impacts[card.Uid] = new(old, current, damage);
        }

        foreach (var entry in entries)
        {
            if (entry is not JsonObject obj) continue;
            var message = obj["message"]?.GetValue<string>();
            if (string.IsNullOrEmpty(message)) continue;
            var data = obj["data"] as JsonObject;

            switch (message)
            {
                case "turn.start":
                case "turn.start.after":
                {
                    var side = Side(obj, data);
                    var turn = Int(Field(data, "TurnNumber")) ?? after.Turn;
                    // 同一回合可能被 before/after 两条描述，只播一次（与本地翻译器同口径）。
                    if (announcedTurns.Add((side, turn)))
                        steps.Add(new UiTurnPresentation(side, turn, UiTurnKind.TurnStarted));
                    break;
                }

                case "card.drawn":
                case "card.hand.add":
                {
                    var side = Side(obj, data);
                    var card = Card(obj, data);
                    if (card is not null)
                    {
                        if (!arrivals.Add(card.Uid) || before.SelfHand.Any(c => c.Uid == card.Uid)) break;
                        steps.Add(new UiDrawPresentation(side, card.SlotIndex, card));
                    }
                    else if (side == "enemy" && (message == "card.drawn" || !entries.Any(e => e?["message"]?.GetValue<string>() == "card.drawn")))
                        steps.Add(new UiDrawPresentation("enemy", Math.Max(0, (after.EnemyHandCount ?? 0) - 1)));
                    break;
                }

                case "unit.deployed":
                case "unit.joined":
                {
                    if (Card(obj, data) is { } card && card.Zone != "hand" && deployments.Add(card.Uid))
                        steps.Add(new UiDeploymentPresentation(card, after));
                    break;
                }

                case "card.died":
                case "card.leaving":
                {
                    if (Card(obj, data) is { } card)
                        steps.Add(new UiRemovalPresentation(card));
                    break;
                }

                case "card.discarded":
                case "card.burned":
                {
                    steps.Add(new UiDiscardPresentation(Side(obj, data),
                        Card(obj, data),
                        message == "card.burned" ? UiDiscardKind.Burn : UiDiscardKind.Discard));
                    break;
                }

                case "deck.shuffled":
                    steps.Add(new UiShufflePresentation(Side(obj, data)));
                    break;

                case "point.changed":
                    var oldPoints = Int(Field(data, "OldPoints")) ?? 0;
                    var newPoints = Int(Field(data, "NewPoints")) ?? 0;
                    steps.Add(new UiResourcePresentation(Side(obj, data), newPoints < oldPoints ? UiResourceCause.Spend : UiResourceCause.Gain,
                        oldPoints, newPoints));
                    break;
                case "slot.changed":
                    var resourceSide = Side(obj, data);
                    steps.Add(new UiResourcePresentation(resourceSide, UiResourceCause.Turn,
                        resourceSide == "self" ? before.SelfKredits : before.EnemyKredits ?? 0,
                        resourceSide == "self" ? after.SelfKredits : after.EnemyKredits ?? 0,
                        Int(Field(data, "OldSlots")), Int(Field(data, "NewSlots"))));
                    break;
                case "card.played":
                    if (Card(obj, data) is { Definition.CardType: "order" } order
                        && !steps.OfType<UiOrderPresentation>().Any(s => s.Card.Uid == order.Uid))
                        steps.Add(new UiOrderPresentation(order, []));
                    break;
                case "card.damaged":
                    Hit(Card(obj, data), Int(Field(data, "Amount")));
                    break;
                case "card.stat.changed":
                    Hit(Card(obj, data), null);
                    break;
                case "unit.damage.dealt":
                    if (Resolve(Field(data, "Unit"), known) is { } attacker && Card(obj, data) is { } victim)
                        assaults[victim.Uid] = (attacker.Uid, Int(Field(data, "Amount")) ?? 0);
                    break;

                case "counter.triggered":
                {
                    steps.Add(new UiCounterPresentation(Side(obj, data),
                        UiCounterStage.Triggered, Card(obj, data), null));
                    break;
                }

                case "counter.disarmed":
                {
                    steps.Add(new UiCounterPresentation(Side(obj, data),
                        UiCounterStage.Disarmed, Card(obj, data), null));
                    break;
                }
            }
        }

        foreach (var (victim, hit) in assaults)
        {
            if (!impacts.TryGetValue(victim, out var impact)) { Hit(known.GetValueOrDefault(victim), hit.Amount); impact = impacts.GetValueOrDefault(victim); }
            if (impact is not null) impacts[victim] = impact with { Source = known.GetValueOrDefault(hit.Source), Damage = hit.Amount };
        }
        if (impacts.Count > 0)
        {
            steps.RemoveAll(s => s is UiRemovalPresentation removal && impacts.ContainsKey(removal.Card.Uid));
            var orderIndex = steps.FindIndex(s => s is UiOrderPresentation);
            if (orderIndex >= 0) steps[orderIndex] = ((UiOrderPresentation)steps[orderIndex]) with { Impacts = impacts.Values.ToArray() };
            else steps.Add(new UiBoardImpactsPresentation(impacts.Values.ToArray()));
        }
        OrcUpdateTranslator.AddVisibleChanges(steps, before, after);
        return steps;
    }

    /// <summary>投影里所有可见卡，按 uid 索引。卡可能在手牌、两条线或总部位置。</summary>
    private static Dictionary<string, UiCardView> IndexCards(UiMatchView view)
    {
        var index = new Dictionary<string, UiCardView>(StringComparer.Ordinal);
        void Add(UiCardView? card)
        {
            if (card is not null) index[card.Uid] = card;
        }

        foreach (var card in view.SelfHand) Add(card);
        foreach (var card in view.SelfLine) Add(card);
        foreach (var card in view.EnemyLine) Add(card);
        foreach (var card in view.PendingChoices) Add(card);
        Add(view.SelfHq);
        Add(view.EnemyHq);
        Add(view.SelectedTarget);
        return index;
    }

    /// <summary>
    /// 引擎 payload 的键是 PascalCase（<c>Card</c>／<c>Player</c>），
    /// 而协议序列化按 Web 约定可能转成 camelCase——两种都认，不赌。
    /// </summary>
    private static JsonNode? Field(JsonObject? data, string pascal)
    {
        if (data is null) return null;
        var camel = char.ToLowerInvariant(pascal[0]) + pascal[1..];
        return data[camel] ?? data[pascal];
    }

    /// <summary>从 <c>{"$ref": "&lt;uid&gt;"}</c> 取投影里的卡；<c>$hidden</c>／缺键＝null（跳过，不猜）。</summary>
    private static UiCardView? Resolve(JsonNode? node, IReadOnlyDictionary<string, UiCardView> index)
    {
        var key = node?["$ref"]?.GetValue<string>();
        return key is not null && index.TryGetValue(key, out var card) ? card : null;
    }

    /// <summary>
    /// 归属判断：payload 里有玩家名就比对，没有就按"自方"。
    /// 与本地翻译器同口径（本地也是 <c>player is null → self</c>）。
    /// </summary>
    private static string SideOf(JsonNode? player, UiMatchView view)
    {
        var name = player?["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name)) return "self";
        return name == view.SelfPlayerName ? "self" : "enemy";
    }

    private static int? Int(JsonNode? node)
    {
        try { return node?.GetValue<int>(); }
        catch (Exception) { return null; }
    }
}
