using Godot;

namespace Kards.Ui;

public partial class Main
{
    /// <summary>
    /// <c>--tune-cardface</c>：直接弹一个卡图对齐浮层（用第一张有卡图的卡做样张），
    /// 调好截图后退出。人类玩家正常从「DIY 工坊 → 卡牌编辑 → 微调卡图…」进入。
    /// </summary>
    private async void OpenCardFaceTuner()
    {
        Show("editor");
        await MenuSettledAsync();
        var tuner = new CardFaceTuner();
        AddChild(tuner);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var sample = _catalog.Cards.FirstOrDefault(c => c.ArtPath.Length > 0) ?? _catalog.Cards.First();
        tuner.Open(sample, ResourceLoader.Load<Texture2D>(sample.ArtPath));
        await ToSignal(GetTree().CreateTimer(.6), SceneTreeTimer.SignalName.Timeout);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var dir = ProjectSettings.GlobalizePath("res://artifacts/cardface-tuner");
        System.IO.Directory.CreateDirectory(dir);
        GetViewport().GetTexture().GetImage().SavePng(dir + "/tuner.png");
        GD.Print("CARDFACE_TUNER_OK card=" + sample.CardId);
        GetTree().Quit();
    }
}
