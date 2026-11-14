namespace Kards.Ui.OrcBridge.HandlerDsl;

/// <summary>Builds ordinary engine DSL nodes; no private targeting model or generated script.</summary>
public static class HandlerTargetAuthoring
{
    public sealed record SelectionContext(List<HandlerNode> Parent, int SelectorIndex, HandlerNode Selector, HandlerNode Loop, int Index);

    public static SelectionContext? FindSelection(HandlerDocument document, HandlerNode action, string pin)
    {
        var name = action.Value(pin)?.Name;
        if (name is null || !name.StartsWith("currentTarget_", StringComparison.Ordinal) || !int.TryParse(name[14..], out var suffix)) return null;
        SelectionContext? Walk(List<HandlerNode> list)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var node = list[i];
                if (i > 0 && node.Op == "iterate" && node.Text("as") == name && node.Bodies.TryGetValue("actions", out var body) && body.Contains(action)
                    && list[i - 1].Op is "selectTargets" or "headquartersOf" && list[i - 1].Text("bind") == node.Value("over")?.Name)
                    return new(list, i - 1, list[i - 1], node, suffix);
                foreach (var child in node.Bodies.Values) if (Walk(child) is { } found) return found;
                foreach (var option in node.Options) if (Walk(option.Actions) is { } found) return found;
            }
            return null;
        }
        return Walk(document.Actions);
    }

    public static void UpdateSelection(SelectionContext context, string side, string selection, bool headquarters, int count, string? zone)
    {
        var op = headquarters ? "headquartersOf" : "selectTargets";
        var node = context.Selector.Op == op ? context.Selector.Clone() : new HandlerNode(op).SetText("bind", context.Selector.Text("bind"));
        node.Set("side", HandlerValue.Str(!headquarters && selection == "self" ? "friendly" : side));
        if (!headquarters)
        {
            node.Set("mode", HandlerValue.Str(selection));
            if (selection is "one" or "random") node.Set("count", HandlerValue.Num(count)); else node.Values.Remove("count");
            if (string.IsNullOrWhiteSpace(zone)) node.Values.Remove("zone"); else node.Set("zone", HandlerValue.Str(zone));
        }
        context.Parent[context.SelectorIndex] = node;
    }

    public static IReadOnlyList<HandlerNode> SelectForAction(HandlerNode action, string pin, string side, string selection, bool headquarters, int index, int count = 1, string? zone = null)
    {
        if (side is not ("friendly" or "enemy" or "both")) throw new ArgumentException("请选择友方、敌方或双方。", nameof(side));
        var selected = "selectedTargets_" + index;
        var current = "currentTarget_" + index;
        var select = headquarters
            ? new HandlerNode("headquartersOf").Set("side", HandlerValue.Str(side)).SetText("bind", selected)
            : new HandlerNode("selectTargets").Set("mode", HandlerValue.Str(selection)).Set("side", HandlerValue.Str(selection == "self" ? "friendly" : side)).SetText("bind", selected);
        if (!headquarters && selection is "one" or "random") select.Set("count", HandlerValue.Num(count));
        if (!headquarters && !string.IsNullOrWhiteSpace(zone)) select.Set("zone", HandlerValue.Str(zone));
        var each = new HandlerNode("iterate").Set("over", HandlerValue.Var(selected)).SetText("as", current);
        each.Body("actions").Add(action.Set(pin, HandlerValue.Var(current)));
        return [select, each];
    }
}
