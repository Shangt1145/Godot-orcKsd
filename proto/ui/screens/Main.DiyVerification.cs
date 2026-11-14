using Godot;
namespace Kards.Ui;
public partial class Main
{
    private void VerifyDiy() => VerifyDiyTutorial();
    private void VerifyDiyLayout() => VerifyDiyTutorial();
    private void VerifyDesktopAuthoring() => VerifyDiyTutorial();
    private void VerifyInspectorComposition() => VerifyDiyTutorial();
    private void VerifyInspectorEntry() => VerifyDiyTutorial();
    private async Task DiyClickAsync(Control control)
    {
        _diyEditor.EnsureEditingVisible(control);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await DeckClickAsync(control);
    }
}
