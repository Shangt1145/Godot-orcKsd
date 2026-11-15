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
        side += selector.Value("zone")?.Text switch { "frontline" => "前线", "support" => "支援阵线", _ => "" };
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
        void OpenTargets()
        {
            var dialog = new AcceptDialog { Title = "选择作用目标", OkButtonText = "返回", MinSize = new(560, 560) }; AddChild(dialog);
            var scroll = new ScrollContainer { CustomMinimumSize = new(520, 480), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; dialog.AddChild(scroll);
            var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; box.AddThemeConstantOverride("separation", 12); scroll.AddChild(box);
            var existing = HandlerTargetAuthoring.FindSelection(_doc, node, pin);
            void Apply(string side, string mode, bool headquarters, string zone, int count)
            {
                var list = FindParentList(node); if (list is null) return;
                var place = list.IndexOf(node);
                var index = 1; while (_doc.ToJsonText(_catalog).Contains("selectedTargets_" + index, StringComparison.Ordinal)) index++;
                dialog.Hide(); dialog.QueueFree();
                Edit(() =>
                {
                    if (existing is not null) { HandlerTargetAuthoring.UpdateSelection(existing, side, mode, headquarters, count, zone); return; }
                    var nodes = HandlerTargetAuthoring.SelectForAction(node, pin, side, mode, headquarters, index, count, zone);
                    list.RemoveAt(place); list.InsertRange(place, nodes);
                });
            }
            box.AddChild(MenuStyles.Label("目标数量", 14, MenuStyles.Gold));
            var count = new SpinBox { MinValue = 1, MaxValue = 99, Value = existing?.Selector.Value("count")?.Number ?? 1 }; box.AddChild(count);
            var shelf = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; shelf.AddThemeConstantOverride("separation", 8); box.AddChild(shelf);
            void RebuildTargets()
            {
                foreach (var child in shelf.GetChildren()) { shelf.RemoveChild(child); child.QueueFree(); }
                foreach (var field in _view.Values.Where(f => f.Type == "entity"))
                    shelf.AddChild(PaletteBlock(HandlerVocabulary.ViewFieldLabel(field.Name), BlockTheme.Query, BlockShape.Form.Reporter,
                        () => { dialog.Hide(); dialog.QueueFree(); SetValue(node, pin, HandlerValue.ViewField(field.Name)); }));
                foreach (var bound in scope.Where(s => s.IsEntity))
                    shelf.AddChild(PaletteBlock(HandlerVocabulary.BindingLabel(bound.Name), BlockTheme.Query, BlockShape.Form.Reporter,
                        () => { dialog.Hide(); dialog.QueueFree(); SetValue(node, pin, HandlerValue.Var(bound.Name)); }));
                foreach (var side in new[] { ("enemy", "敌方"), ("friendly", "友方"), ("both", "双方") })
                {
                    foreach (var zone in new[] { ("", ""), ("frontline", "前线"), ("support", "支援阵线") })
                        foreach (var mode in new[] { ("one", "选择"), ("random", "随机"), ("all", "全部") })
                        {
                            var label = mode.Item2 + (mode.Item1 == "all" ? "" : $" {count.Value:0} 个") + side.Item2 + zone.Item2 + "单位";
                            var block = PaletteBlock(label, BlockTheme.Query, BlockShape.Form.Reporter,
                                () => Apply(side.Item1, mode.Item1, false, zone.Item1, (int)count.Value));
                            block.SetMeta("target_variant", side.Item1 + "/" + mode.Item1 + "/" + zone.Item1); shelf.AddChild(block);
                        }
                    var hq = PaletteBlock(side.Item2 + "总部", BlockTheme.Query, BlockShape.Form.Reporter, () => Apply(side.Item1, "all", true, "", 1));
                    hq.SetMeta("target_variant", side.Item1 + "/hq"); shelf.AddChild(hq);
                }
                var self = PaletteBlock("本卡自身", BlockTheme.Query, BlockShape.Form.Reporter, () => Apply("friendly", "self", false, "", 1));
                self.SetMeta("target_variant", "self"); shelf.AddChild(self);
            }
            count.ValueChanged += _ => RebuildTargets(); RebuildTargets();
            box.AddChild(MenuStyles.Button("更多取值与属性读取…", () => { dialog.Hide(); dialog.QueueFree(); OpenValueMenu(value, v => SetValue(node, pin, v), scope); }));
            dialog.Confirmed += dialog.QueueFree; dialog.Canceled += dialog.QueueFree; dialog.PopupCentered();
        }
        var result = PaletteBlock(selection is null ? value?.Describe() ?? "放入作用目标积木…" : SelectionLabel(selection.Selector), BlockTheme.Query, BlockShape.Form.Reporter, OpenTargets);
        result.SetMeta("target_socket", pin); return result;
    }
}
