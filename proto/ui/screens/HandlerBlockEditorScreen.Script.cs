using Godot;
using System.Text.Json.Nodes;
using Kards.Ui.OrcBridge.HandlerDsl;

namespace Kards.Ui;

public partial class HandlerBlockEditorScreen
{
    private Control ScriptChip(HandlerNode node)
    {
        var button = InputChip("编辑脚本与输入输出…", string.IsNullOrEmpty(node.Text("body")));
        button.Pressed += () =>
        {
            var dialog = new ConfirmationDialog { Title = "高级脚本", OkButtonText = "应用到积木", CancelButtonText = "取消", DialogHideOnOk = false };
            AddChild(dialog);
            var scroll = new ScrollContainer { CustomMinimumSize = new(680, 460), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            dialog.AddChild(scroll);
            var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; scroll.AddChild(box);
            box.AddChild(MenuStyles.Label("脚本正文（由引擎编译和执行）", 16, MenuStyles.Gold));
            var body = new CodeEdit { Text = node.Text("body") ?? "", CustomMinimumSize = new(640, 240), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            box.AddChild(body);
            var fields = new Dictionary<string, List<(LineEdit Name, OptionButton Type, JsonObject Original)>>();
            var types = new[] { "number", "string", "boolean", "entity", "list<entity>", "list<number>", "list<string>" };
            var labels = new[] { "数值", "文字", "是或否", "实体", "实体列表", "数值列表", "文字列表" };
            foreach (var field in new[] { "inputs", "outputs" })
            {
                var rows = new VBoxContainer(); fields[field] = [];
                box.AddChild(MenuStyles.Label(field == "inputs" ? "输入声明" : "输出声明", 16, MenuStyles.Gold)); box.AddChild(rows);
                void Add(JsonObject original)
                {
                    var row = new HBoxContainer(); rows.AddChild(row);
                    var name = new LineEdit { Text = original["name"]?.GetValue<string>() ?? "", PlaceholderText = "脚本中的变量名", SizeFlagsHorizontal = SizeFlags.ExpandFill };
                    var type = new OptionButton(); foreach (var label in labels) type.AddItem(label);
                    var current = original["type"]?.GetValue<string>() ?? "number";
                    var selected = Array.IndexOf(types, current);
                    if (selected < 0) { type.AddItem("保留原类型：" + current); selected = types.Length; }
                    type.Select(selected); row.AddChild(name); row.AddChild(type);
                    var entry = (name, type, original); fields[field].Add(entry);
                    row.AddChild(MenuStyles.Button("删除", () => { fields[field].Remove(entry); row.QueueFree(); }));
                }
                if (node.ScriptPins.TryGetValue(field, out var pins)) foreach (var pin in pins.OfType<JsonObject>()) Add((JsonObject)pin.DeepClone());
                box.AddChild(MenuStyles.Button(field == "inputs" ? "＋ 添加输入" : "＋ 添加输出", () => Add(new JsonObject())));
            }
            var error = MenuStyles.Label("", 13, MenuStyles.Gold); box.AddChild(error);
            dialog.Confirmed += () =>
            {
                var result = new Dictionary<string, JsonArray>();
                foreach (var (field, entries) in fields)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal); var array = new JsonArray();
                    foreach (var (name, type, original) in entries)
                    {
                        var value = name.Text.Trim();
                        if (value.Length == 0 || !names.Add(value)) { error.Text = "变量名不能为空，同一组中的名称不能重复。"; return; }
                        var pin = (JsonObject)original.DeepClone(); pin["name"] = value;
                        pin["type"] = type.Selected < types.Length ? types[type.Selected] : original["type"]?.GetValue<string>(); array.Add(pin);
                    }
                    result[field] = array;
                }
                dialog.Hide(); dialog.QueueFree();
                Edit(() => { node.SetText("body", body.Text); foreach (var (field, pins) in result) node.SetScriptPins(field, pins); });
            };
            dialog.Canceled += dialog.QueueFree; dialog.PopupCentered();
        };
        return button;
    }
}
