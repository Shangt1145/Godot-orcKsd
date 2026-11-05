using Godot;

namespace Kards.Ui;

public partial class AppearanceScreen
{
    internal async Task VerifyDeskEditorAsync(Kards.Ui.Core.CosmeticAsset cloth, Kards.Ui.Core.CosmeticAsset model)
    {
        async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        async Task Click(Vector2 point)
        {
            GetViewport().PushInput(new InputEventMouseMotion { Position = point, GlobalPosition = point }, true);
            GetViewport().PushInput(new InputEventMouseButton { Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left, Pressed = true }, true); await Frame();
            GetViewport().PushInput(new InputEventMouseButton { Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left, Pressed = false }, true); await Frame();
        }
        foreach (var pair in new[] { (Tab: 4, Asset: cloth), (Tab: 1, Asset: model) })
        {
            SelectCategory(pair.Tab); _search.Text = pair.Asset.Name; RefreshControls(); await Frame(); await Frame();
            var index = Array.FindIndex(_visibleAssets, a => a.Image == pair.Asset.Image) + 1;
            if (index == 0) throw new Exception("Imported desk asset missing in editor");
            var rect = _list.GetItemRect(index); await Click(_list.GetGlobalTransformWithCanvas() * rect.GetCenter());
            if (pair.Tab == 1)
            {
                _propSize.Value = 1.2; _propYaw.Value = 30; _animation.Select(1); ChangePropSettings();
            }
            Save();
        }
        if (!_propPreview.HasProp || _propPreview.Animations.Length == 0 || !_propPreview.PlayClick()) throw new Exception("Editor animation preview missing");
        var profile = _library.Resolve(_deckId, _mainNation);
        if (profile.Tablecloth != cloth.Image || profile.Prop.Model != model.Prop?.Model || profile.Prop.Animation.Length == 0
            || Math.Abs(profile.Prop.Size - 1.2f) > .001f || profile.Prop.Yaw != 30) throw new Exception("Editor failed to equip tabletop assets");
        GD.Print("DESK_EDITOR_VERIFY_OK five-tabs real-mouse-select model-preview animations equip");
    }
    internal async Task VerifyOriginalGalleryAsync()
    {
        _emoteFilter.Select(2);SelectCategory(3);
        await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        if(_visiblePresets.Any(e=>e is not null&&e.Style!="special"))throw new Exception("Special emote filter leaked ordinary emotes.");
        if(_list.ItemCount>1&&Math.Abs(_list.GetItemRect(0).Position.X-_list.GetItemRect(1).Position.X)<1)
            throw new Exception($"Original gallery collapsed to one column: width={_list.Size.X}, item={_list.GetItemRect(0)}.");
        var original=_draft;SelectImage(0);
        if(_draft.Emotes[_slot].Tint!="433a29"||_draft.Emotes[_slot].EnemyImage.Length!=0)throw new Exception("Switching to ordinary emotes retained special color or opponent texture.");
        _draft=original;RefreshControls();
        GD.Print("ORIGINAL_GALLERY_VERIFY_OK two-columns special-emote-filter native-previews");
    }
    internal async Task VerifyEquipmentAsync()
    {
        async Task Frame()=>await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        async Task Click(Vector2 point)
        {
            GetViewport().PushInput(new InputEventMouseMotion{Position=point,GlobalPosition=point},true);
            GetViewport().PushInput(new InputEventMouseButton{Position=point,GlobalPosition=point,ButtonIndex=MouseButton.Left,Pressed=true},true);await Frame();
            GetViewport().PushInput(new InputEventMouseButton{Position=point,GlobalPosition=point,ButtonIndex=MouseButton.Left,Pressed=false},true);await Frame();
        }
        IEnumerable<Node> Descend(Node root){foreach(var child in root.GetChildren()){yield return child;foreach(var sub in Descend(child))yield return sub;}}
        var equip=Descend(this).OfType<Button>().Single(b=>b.Text=="装备并保存");
        _search.Text="验证·";
        for(var tab=0;tab<4;tab++)
        {
            SelectCategory(tab);await Frame();await Frame();
            if(_visibleAssets.Length==0)throw new Exception("Appearance fixture has no imported image.");
            var rect=_list.GetItemRect(1);await Click(_list.GetGlobalTransformWithCanvas()*(rect.Position+rect.Size/2));
            if(_list.GetSelectedItems().SingleOrDefault()!=1)throw new Exception("Image row mouse selection failed.");
            if(Kind=="hq")
            {
                _hqName.GrabFocus();_hqName.SelectAll();
                foreach(var ch in "山城指挥所")GetViewport().PushInput(new InputEventKey{Pressed=true,Unicode=ch},true);
                await Frame();
            }
            await Click(equip.GetGlobalRect().GetCenter());
            var profile=_library.Resolve(_deckId,_mainNation);
            var image=Kind switch{"back"=>profile.CardBack,"decoration"=>profile.Decoration,"hq"=>profile.Headquarters,_=>profile.Emotes[_slot].Image};
            if(image!=_visibleAssets[0].Image)throw new Exception("Equip click did not persist "+Kind);
        }
        if(_library.Resolve(_deckId,_mainNation).HeadquartersName!="山城指挥所")throw new Exception("Headquarters name input did not persist.");
        GD.Print("APPEARANCE_EDITOR_VERIFY_OK four-tabs mouse-select mouse-equip typed-hq-name persistence");
        _search.Text="";
    }
}
