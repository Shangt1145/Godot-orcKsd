using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class DiyWorkshopScreen
{
    private Action? _focusNativeParser;
    internal TextEdit NativeRuleText = null!;
    internal Label NativeParseStatus = null!;
    internal Button NativeParseReplace = null!, NativeParseAppend = null!;

    internal void ReturnToTextAuthoring()
    {
        if (_blockCard is { } card && _native?.SessionPackage is { } packId) OpenNativeCard(packId, card);
        else ReturnFromBlocks();
        if (_focusNativeParser is null) _notice.Text = "请先打开一张卡牌，在“词条与效果”中自动解析文字。";
        else _focusNativeParser();
    }

    private void BuildNativeTextAuthoring(VBoxContainer parent, TextEdit input, string packId, NativeWorkshopAsset asset,
        Func<string> flavor, IReadOnlyList<OrcEffectDraft> currentRules, Action focus)
    {
        NativeRuleText = input; _focusNativeParser = focus;
        var row = new HFlowContainer(); row.AddThemeConstantOverride("h_separation", 8); parent.AddChild(row);
        NativeParseStatus = MenuStyles.Label("输入卡牌文字可自动检查；点击解析按钮后写入草稿，积木和导入效果会保留。", 13, MenuStyles.Dim);
        NativeParseStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart; parent.AddChild(NativeParseStatus);
        void Check()
        {
            try
            {
                if (input.Text.Trim().Length == 0) { NativeParseStatus.Text = "请输入效果文字，例如：部署：对一个敌方单位造成 2 点伤害。"; return; }
                var result = OrcInspector.CompileText(asset.AppId, input.Text, _cardDocument!.CardType, _service.Cards);
                NativeParseStatus.Text = result.ParseComplete && result.AllCompiled
                    ? $"已识别 {result.Parse.Effects.Count} 条效果、{result.Parse.Keywords.Count} 个词条；可解析到草稿。"
                    : "尚未应用：" + string.Join("；", result.Problems);
            }
            catch (Exception error) { NativeParseStatus.Text = "检查失败：" + error.Message; }
        }
        void Apply(bool append)
        {
            var quickText = string.Join("\n", currentRules
                .Select(WorkshopQuickEffect.From).OfType<WorkshopQuickEffect>().Select(effect => effect.Description(_cardDocument!.CardType)));
            if (quickText.Length > 0 && input.Text.Trim() == quickText)
            {
                NativeParseStatus.Text = "这段文字已经由快捷效果生成。要修改效果，请用上方的「编辑」。";
                return;
            }
            var result = NativeTextAuthoring.Apply(_native!, asset, _cardDocument!.Json, input.Text, flavor(), append, _service.Cards, currentRules);
            if (!result.Success) { NativeParseStatus.Text = "未应用，原有效果保持完整：" + string.Join("；", result.Problems); return; }
            _nativeDirty = false; OpenNativeCard(packId, asset); _focusNativeParser?.Invoke();
            NativeParseStatus.Text = append ? "已解析并追加到草稿。可继续编辑，应用修改后用于对局。" : "已替换自动解析的效果；快捷效果、积木和导入效果已保留。";
        }
        row.AddChild(MenuStyles.Button("检查解析", Check));
        NativeParseAppend = MenuStyles.Button("解析并追加", () => Apply(true)); row.AddChild(NativeParseAppend);
        NativeParseReplace = MenuStyles.Button("解析并替换", () => Apply(false)); row.AddChild(NativeParseReplace);
        var timer = new Godot.Timer { OneShot = true, WaitTime = .6 }; parent.AddChild(timer); timer.Timeout += Check;
        input.TextChanged += () => timer.Start();
    }
}
