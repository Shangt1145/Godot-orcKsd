using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class DiyWorkshopScreen
{
    private Control BuildQuickEffects(List<OrcEffectDraft> rules, NativeWorkshopAsset asset, TextEdit cardText, Action changed)
    {
        var area = new VBoxContainer();
        area.AddThemeConstantOverride("separation", 8);
        area.AddChild(MenuStyles.Label("对局效果", 18, MenuStyles.Gold));
        var rows = new VBoxContainer();
        area.AddChild(rows);
        var add = new Button { Text = "＋ 添加效果", MouseDefaultCursorShape = CursorShape.PointingHand };
        MenuStyles.Primary(add);
        area.AddChild(add);
        var status = MenuStyles.Label("", 14, MenuStyles.Dim);
        status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        area.AddChild(status);

        string AutomaticText() => string.Join("\n", rules.Select(WorkshopQuickEffect.From).OfType<WorkshopQuickEffect>()
            .Select(effect => effect.Description(_cardDocument!.CardType)));
        void UpdateText(string previous)
        {
            if (cardText.Text.Trim().Length == 0 || cardText.Text == previous)
            {
                cardText.Text = AutomaticText();
                cardText.EmitSignal(TextEdit.SignalName.TextChanged);
            }
            changed();
            Render();
        }
        void Render()
        {
            MenuStyles.Clear(rows);
            add.Disabled = _cardDocument!.CardType is not ("unit" or "order");
            status.Text = add.Disabled ? "反制使用下方的高级积木编辑。" : "";
            for (var i = 0; i < rules.Count; i++)
            {
                if (WorkshopQuickEffect.From(rules[i]) is not { } effect) continue;
                var index = i;
                var row = new HFlowContainer();
                rows.AddChild(row);
                row.AddChild(MenuStyles.Label(effect.Description(_cardDocument.CardType), 16));
                row.AddChild(MenuStyles.Button("编辑", () => Edit(index)));
                row.AddChild(MenuStyles.Button("移除", () =>
                {
                    var previous = AutomaticText();
                    rules.RemoveAt(index);
                    UpdateText(previous);
                }));
            }
            if (rows.GetChildCount() == 0) rows.AddChild(MenuStyles.Label("还没有快捷效果。", 14, MenuStyles.Dim));
        }
        void Edit(int index)
        {
            var effect = index < 0 ? new WorkshopQuickEffect() : WorkshopQuickEffect.From(rules[index])!;
            var dialog = new ConfirmationDialog { Title = "效果", OkButtonText = "完成", DialogHideOnOk = false };
            AddChild(dialog);
            var fields = new VBoxContainer { CustomMinimumSize = new(340, 0) };
            dialog.AddChild(fields);
            fields.AddChild(MenuStyles.Label(_cardDocument!.CardType == "unit" ? "时机 · 部署这张单位时" : "时机 · 打出这张指令时", 16));
            var action = new OptionButton();
            foreach (var (key, title) in new[] { ("draw", "抽牌"), ("damage", "伤害敌方单位"), ("buff", "增强友方单位") })
            {
                action.AddItem(title);
                action.SetItemMetadata(action.ItemCount - 1, key);
            }
            DiyEditorScreen.Select(action, effect.Action);
            NativeField(fields, "要做什么", action);
            var amount = new SpinBox { MinValue = 1, MaxValue = 20, Step = 1, Value = effect.Amount };
            NativeField(fields, "数值", amount);
            var amountLabel = (Label)amount.GetParent().GetChild(0);
            var defense = new SpinBox { MinValue = 1, MaxValue = 20, Step = 1, Value = effect.Defense };
            NativeField(fields, "增加防御", defense);
            var sentence = MenuStyles.Label("", 15);
            sentence.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            fields.AddChild(sentence);
            WorkshopQuickEffect Read() => new(action.GetItemMetadata(action.Selected).AsString(), (int)amount.Value, (int)defense.Value);
            void Refresh()
            {
                var next = Read();
                amountLabel.Text = next.Action switch { "draw" => "抽几张牌", "damage" => "伤害点数", _ => "增加攻击" };
                ((Control)defense.GetParent()).Visible = next.Action == "buff";
                sentence.Text = next.Description(_cardDocument.CardType);
            }
            action.ItemSelected += _ => Refresh();
            amount.ValueChanged += _ => Refresh();
            defense.ValueChanged += _ => Refresh();
            dialog.Confirmed += () =>
            {
                try
                {
                    var next = Read().Compile(asset.AppId, _cardDocument.CardType, _service.Cards);
                    var previous = AutomaticText();
                    if (index < 0) rules.Add(next); else rules[index] = next;
                    UpdateText(previous);
                    dialog.QueueFree();
                }
                catch (InvalidDataException error) { sentence.Text = error.Message; }
            };
            dialog.Canceled += dialog.QueueFree;
            Refresh();
            dialog.PopupCentered();
        }
        area.VisibilityChanged += Render;
        add.Pressed += () => Edit(-1);
        Render();
        return area;
    }
}
