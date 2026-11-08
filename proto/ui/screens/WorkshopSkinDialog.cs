using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

internal static class WorkshopSkinDialog
{
    public static void Open(Node owner, UiCardDefinition card, CardSkin? skin, TextureCache textures, Texture2D? image,
        Action<CardSkin, byte[]?> save, Action reset)
    {
        var dialog = new AcceptDialog { Title = "工坊 · 异画与数值位置", OkButtonText = "关闭", MinSize = new(960, 650) };
        owner.AddChild(dialog);
        var editor = new CardSkinEditor { CustomMinimumSize = new(920, 540) }; dialog.AddChild(editor);
        editor.Initialize(card, skin, textures, image, save, reset);
        dialog.Confirmed += dialog.QueueFree; dialog.Canceled += dialog.QueueFree; dialog.PopupCentered();
    }
}
