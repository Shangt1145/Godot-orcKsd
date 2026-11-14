using Godot;
using Kards.Ui.OrcBridge.HandlerDsl;

namespace Kards.Ui;

public partial class HandlerBlockEditorScreen
{
    private readonly HashSet<HandlerNode> _expandedTargetSteps = [];

    private static (HandlerNode Action, string Pin)? TargetSequenceAt(List<HandlerNode> list, int index)
    {
        if (index < 0 || index + 1 >= list.Count || list[index].Op is not ("selectTargets" or "headquartersOf")) return null;
        var loop = list[index + 1];
        if (loop.Op != "iterate" || !loop.Bodies.TryGetValue("actions", out var actions) || actions.Count != 1
            || list[index].Text("bind") != loop.Value("over")?.Name) return null;
        var name = loop.Text("as");
        if (name?.StartsWith("currentTarget_", StringComparison.Ordinal) != true) return null;
        foreach (var (pin, value) in actions[0].Values)
            if (value.Kind == HandlerValueKind.Binding && value.Name == name) return (actions[0], pin);
        return null;
    }

    private HandlerTargetAuthoring.SelectionContext? SelectionFor(HandlerNode action)
    {
        foreach (var pin in action.Values.Keys)
            if (HandlerTargetAuthoring.FindSelection(_doc, action, pin) is { } context && TargetSequenceAt(context.Parent, context.SelectorIndex) is not null) return context;
        return null;
    }

    private static string SelectionLabel(HandlerNode selector)
    {
        var side = selector.Value("side")?.Text switch { "friendly" => "友方", "both" => "双方", _ => "敌方" };
        if (selector.Op == "headquartersOf") return side + "总部";
        var count = selector.Value("count")?.Number ?? 1;
        return selector.Value("mode")?.Text switch
        {
            "self" => "本卡自身", "all" => "全部" + side + "单位", "random" => $"随机 {count:0} 个{side}单位",
            _ => $"选择 {count:0} 个{side}单位",
        };
    }

    private Control EntityTargetChip(HandlerNode node, string pin, List<ScopeVar> scope)
    {
        var value = node.Value(pin);
        var selection = HandlerTargetAuthoring.FindSelection(_doc, node, pin);
        var chip = InputChip(selection is null ? value?.Describe() ?? "选择作用目标…" : SelectionLabel(selection.Selector), value is null);
        chip.Pressed += () =>
        {
            var dialog = new AcceptDialog { Title = "选择作用目标", OkButtonText = "取消", MinSize = new(560, 560) }; AddChild(dialog);
            var scroll = new ScrollContainer { CustomMinimumSize = new(520, 480), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; dialog.AddChild(scroll);
            var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; box.AddThemeConstantOverride("separation", 12); scroll.AddChild(box);
            var existing = HandlerTargetAuthoring.FindSelection(_doc, node, pin);
            box.AddChild(MenuStyles.Label("直接使用", 17, MenuStyles.Gold));
            foreach (var field in _view.Values.Where(f => f.Type == "entity"))
                box.AddChild(MenuStyles.Button(HandlerVocabulary.ViewFieldLabel(field.Name), () => { dialog.Hide(); dialog.QueueFree(); SetValue(node, pin, HandlerValue.ViewField(field.Name)); }));
            foreach (var bound in scope.Where(s => s.IsEntity))
                box.AddChild(MenuStyles.Button(HandlerVocabulary.BindingLabel(bound.Name), () => { dialog.Hide(); dialog.QueueFree(); SetValue(node, pin, HandlerValue.Var(bound.Name)); }));
            box.AddChild(MenuStyles.Label("指定新的目标范围", 17, MenuStyles.Gold));
            var kind = new OptionButton(); kind.AddItem("战场单位"); kind.AddItem("总部"); DiyEditorScreen.Field(box, "作用对象", kind);
            var side = new OptionButton(); foreach (var text in new[] { "敌方", "友方", "双方" }) side.AddItem(text); DiyEditorScreen.Field(box, "目标阵营", side);
            var mode = new OptionButton(); mode.AddItem("由玩家选择"); mode.AddItem("随机选择"); mode.AddItem("全部符合条件的目标"); mode.AddItem("本卡自身"); DiyEditorScreen.Field(box, "选取方式", mode);
            var count = new SpinBox { MinValue = 1, MaxValue = 99, Value = 1 }; DiyEditorScreen.Field(box, "目标数量", count);
            var zone = new OptionButton(); zone.AddItem("不限区域"); zone.AddItem("前线"); zone.AddItem("支援阵线"); DiyEditorScreen.Field(box, "所在区域", zone);
            kind.ItemSelected += _ => { mode.Disabled = zone.Disabled = kind.Selected == 1; count.Editable = kind.Selected == 0; if (kind.Selected == 1) mode.Select(2); };
            if (existing is not null)
            {
                var selection = existing.Selector;
                kind.Select(selection.Op == "headquartersOf" ? 1 : 0);
                side.Select(Math.Max(0, Array.IndexOf(new[] { "enemy", "friendly", "both" }, selection.Value("side")?.Text)));
                mode.Select(Math.Max(0, Array.IndexOf(new[] { "one", "random", "all", "self" }, selection.Value("mode")?.Text)));
                count.Value = selection.Value("count")?.Number ?? 1;
                zone.Select(selection.Value("zone")?.Text switch { "frontline" => 1, "support" => 2, _ => 0 });
                kind.EmitSignal(OptionButton.SignalName.ItemSelected, (long)kind.Selected);
            }
            box.AddChild(MenuStyles.Button("使用此目标范围", () =>
            {
                var list = FindParentList(node); if (list is null) return;
                var place = list.IndexOf(node);
                var index = 1; while (_doc.ToJsonText(_catalog).Contains("selectedTargets_" + index, StringComparison.Ordinal)) index++;
                var pickedSide = new[] { "enemy", "friendly", "both" }[side.Selected];
                dialog.Hide(); dialog.QueueFree();
                Edit(() =>
                {
                    if (existing is not null)
                    {
                        HandlerTargetAuthoring.UpdateSelection(existing, pickedSide, new[] { "one", "random", "all", "self" }[mode.Selected], kind.Selected == 1,
                            (int)count.Value, new[] { "", "frontline", "support" }[zone.Selected]);
                        return;
                    }
                    var nodes = HandlerTargetAuthoring.SelectForAction(node, pin, pickedSide, new[] { "one", "random", "all", "self" }[mode.Selected], kind.Selected == 1, index,
                        (int)count.Value, new[] { "", "frontline", "support" }[zone.Selected]);
                    list.RemoveAt(place); list.InsertRange(place, nodes);
                });
            }));
            box.AddChild(MenuStyles.Button("更多取值与属性读取…", () => { dialog.Hide(); dialog.QueueFree(); OpenValueMenu(chip, value, v => SetValue(node, pin, v), scope); }));
            dialog.Confirmed += dialog.QueueFree; dialog.Canceled += dialog.QueueFree; dialog.PopupCentered();
        };
        return chip;
    }
}
