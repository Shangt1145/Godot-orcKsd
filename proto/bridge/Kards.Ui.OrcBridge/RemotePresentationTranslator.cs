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
/// **刻意只覆盖高频段**：回合、抽牌、部署、移除、弃置、洗牌、资源点。其余事件**直接跳过**——
/// 状态仍然由 <c>notify/view</c> 保证正确，跳过只意味着少一段动画。宁可少演，不可乱演。
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
        IReadOnlyList<JsonNode?> entries, UiMatchView after)
    {
        var index = IndexCards(after);
        var steps = new List<UiPresentationStep>();
        var announcedTurns = new HashSet<(string Side, int Turn)>();

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
                    var side = SideOf(Field(data, "Player"), after);
                    var turn = Int(Field(data, "TurnNumber")) ?? after.Turn;
                    // 同一回合可能被 before/after 两条描述，只播一次（与本地翻译器同口径）。
                    if (announcedTurns.Add((side, turn)))
                        steps.Add(new UiTurnPresentation(side, turn, UiTurnKind.TurnStarted));
                    break;
                }

                case "card.drawn":
                case "card.hand.add":
                {
                    steps.Add(new UiDrawPresentation(SideOf(Field(data, "Player"), after), 0,
                        Resolve(Field(data, "Card"), index)));
                    break;
                }

                case "unit.deployed":
                case "unit.joined":
                {
                    if (Resolve(Field(data, "Card") ?? Field(data, "Unit"), index) is { } card)
                        steps.Add(new UiDeploymentPresentation(card, after));
                    break;
                }

                case "card.died":
                case "card.leaving":
                {
                    if (Resolve(Field(data, "Card"), index) is { } card)
                        steps.Add(new UiRemovalPresentation(card));
                    break;
                }

                case "card.discarded":
                case "card.burned":
                {
                    steps.Add(new UiDiscardPresentation(SideOf(Field(data, "Player"), after),
                        Resolve(Field(data, "Card"), index),
                        message == "card.burned" ? UiDiscardKind.Burn : UiDiscardKind.Discard));
                    break;
                }

                case "deck.shuffled":
                    steps.Add(new UiShufflePresentation(SideOf(Field(data, "Player"), after)));
                    break;

                case "slot.gained":
                    steps.Add(new UiResourcePresentation(SideOf(Field(data, "Player"), after),
                        UiResourceCause.Gain, 0, Int(Field(data, "Amount")) ?? 0));
                    break;

                case "counter.triggered":
                {
                    steps.Add(new UiCounterPresentation(SideOf(Field(data, "Player"), after),
                        UiCounterStage.Triggered, Resolve(Field(data, "Card"), index), null));
                    break;
                }

                case "counter.disarmed":
                {
                    steps.Add(new UiCounterPresentation(SideOf(Field(data, "Player"), after),
                        UiCounterStage.Disarmed, Resolve(Field(data, "Card"), index), null));
                    break;
                }
            }
        }

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
