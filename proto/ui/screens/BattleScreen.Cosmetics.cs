using Godot;
using Kards.Ui.Core;

namespace Kards.Ui;

public partial class BattleScreen
{
    private OriginalPropView _selfProp=null!,_enemyProp=null!;
    private TextureRect _selfDecoration=null!, _enemyDecoration=null!;
    private BattleEmoteButton _emoteMenu=null!;
    private PanelContainer _emotePanel=null!;
    private VBoxContainer _emoteChoices=null!;
    private bool _communicationsCut, _cosmeticClosingClick;
    private ulong _lastEmotePress;
    private CosmeticEmote[] _equippedEmotes=[];
    public event Action<string>? EmoteRequested;

    private void InitializeCosmetics()
    {
        TextureRect Decoration(Vector2 at)
        {
            var image=new TextureRect {
                Position=at, Size=new(138,178), ExpandMode=TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode=TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter=MouseFilterEnum.Ignore, ZIndex=2
            };
            _canvas.AddChild(image); return image;
        }
        _selfDecoration=Decoration(new(105,430)); _enemyDecoration=Decoration(new(105,70));
        _selfProp=new OriginalPropView{Position=new(105,430),Size=new(138,178),ZIndex=2};_canvas.AddChild(_selfProp);
        _enemyProp=new OriginalPropView{Position=new(105,70),Size=new(138,178),ZIndex=2};_canvas.AddChild(_enemyProp);
        _emoteMenu=new BattleEmoteButton {
            Position=new(1210,500), Size=new(42,38), Flat=true, ZIndex=180,
            ToggleMode=true, TooltipText="表情与通讯"
        };
        _emoteMenu.Pressed+=()=>{
            RefreshEmoteChoices(); _emotePanel.Visible=_emoteMenu.ButtonPressed;
        };
        _canvas.AddChild(_emoteMenu);
        _emotePanel=new PanelContainer { Position=new(1005,195), Size=new(247,0), Visible=false, ZIndex=180 };
        _emotePanel.AddThemeStyleboxOverride("panel",MenuStyles.Box(new("20211f"),new("adae9d"),0,8));
        _emoteChoices=new VBoxContainer(); _emoteChoices.AddThemeConstantOverride("separation",1);
        _emotePanel.AddChild(_emoteChoices); _canvas.AddChild(_emotePanel);
        Resized+=CloseEmoteMenu;
        VisibilityChanged+=CloseEmoteMenu;
        RefreshCosmetics();
    }
    private void CloseEmoteMenu()
    {
        if(_emotePanel is null)return;
        _emotePanel.Hide(); _emoteMenu.SetPressedNoSignal(false);
    }
    private void RefreshEmoteChoices()
    {
        MenuStyles.Clear(_emoteChoices);
        foreach(var emote in _equippedEmotes)
        {
            var row=EmoteRow(emote.Label,()=>SendEquippedEmote(emote.Id));
            row.Disabled=_communicationsCut || _readOnlyMode is not null;
            _emoteChoices.AddChild(row);
        }
        _emoteChoices.AddChild(new HSeparator());
        _emoteChoices.AddChild(EmoteRow(_communicationsCut?"恢复通讯":"切断通讯",ToggleCommunications));
        // Board coordinates stay correct when the window is scaled.
        _emotePanel.Size=new(247,0);
        _emotePanel.Position=new(1005,500-(_equippedEmotes.Length+1)*30-27);
    }
    private static Button EmoteRow(string text,Action pressed)
    {
        var row=MenuStyles.Button(text,pressed);
        row.CustomMinimumSize=new(225,29); row.Alignment=HorizontalAlignment.Left;
        row.AddThemeFontSizeOverride("font_size",14);
        row.AddThemeColorOverride("font_color",new("e2dfcd"));
        row.AddThemeStyleboxOverride("normal",MenuStyles.Box(Colors.Transparent,Colors.Transparent,0,6));
        row.AddThemeStyleboxOverride("hover",MenuStyles.Box(new("3d4039"),Colors.Transparent,0,6));
        row.AddThemeStyleboxOverride("disabled",MenuStyles.Box(Colors.Transparent,Colors.Transparent,0,6));
        return row;
    }
    private void SendEquippedEmote(string id)
    {
        CloseEmoteMenu();
        if(_communicationsCut || _readOnlyMode is not null)return;
        var now=Time.GetTicksMsec();
        if(_lastEmotePress!=0 && now-_lastEmotePress<2000){Hint("稍等一下再发送表情");return;}
        _lastEmotePress=now; EmoteRequested?.Invoke(id);
    }
    private void ToggleCommunications()
    {
        _communicationsCut=!_communicationsCut;
        ClearCosmeticEmotes(); _emoteMenu.Cut=_communicationsCut; _emoteMenu.QueueRedraw();
        _emoteMenu.TooltipText=_communicationsCut?"通讯已切断：本端隐藏双方表情，点击可恢复":"表情与通讯";
        Hint(_communicationsCut?"通讯已切断，本端隐藏双方表情":"通讯已恢复");
    }
    public void ClearCosmeticEmotes()
    {
        CloseEmoteMenu();
        if(_canvas is null)return;
        foreach(var bubble in _canvas.GetChildren().OfType<BattleEmoteBubble>())
        { _canvas.RemoveChild(bubble); bubble.QueueFree(); }
    }
    // Skip card input, leaving GUI events available to the menu controls.
    private bool HandleCosmeticInput(InputEvent input)
    {
        if(_emotePanel is null)return false;
        if(input is InputEventMouseButton { ButtonIndex:MouseButton.Left, Pressed:false } && _cosmeticClosingClick)
        { _cosmeticClosingClick=false; GetViewport().SetInputAsHandled(); return true; }
        if(_emotePanel.Visible && input is InputEventKey { Pressed:true, Keycode:Key.Escape })
        { CloseEmoteMenu(); GetViewport().SetInputAsHandled(); return true; }
        if(input is not InputEventMouse mouse)return false;
        var at=_canvas.GetGlobalTransformWithCanvas().AffineInverse()*mouse.Position;
        if(new Rect2(_emoteMenu.Position,_emoteMenu.Size).HasPoint(at))return true;
        if(!_emotePanel.Visible)return false;
        if(new Rect2(_emotePanel.Position,_emotePanel.Size).HasPoint(at))return true;
        if(input is InputEventMouseButton { Pressed:true })
        { CloseEmoteMenu(); _cosmeticClosingClick=true; GetViewport().SetInputAsHandled(); return true; }
        return false;
    }
    public void RefreshCosmetics()
    {
        if(_selfDecoration is null)return;
        _selfDecoration.Texture=AppearanceRuntime.Image(AppearanceRuntime.Profile("self").Decoration,_textures);
        _enemyDecoration.Texture=AppearanceRuntime.Image(AppearanceRuntime.Profile("enemy").Decoration,_textures);
        _selfDecoration.Visible=!_selfProp.Bind(AppearanceRuntime.Profile("self").Decoration)&&_selfDecoration.Texture is not null; _enemyDecoration.Visible=!_enemyProp.Bind(AppearanceRuntime.Profile("enemy").Decoration)&&_enemyDecoration.Texture is not null;
        _equippedEmotes=AppearanceRuntime.Profile("self").Emotes;
        RefreshEmoteChoices();
        void Redraw(Node node){if(node is CanvasItem canvas)canvas.QueueRedraw();foreach(var child in node.GetChildren())Redraw(child);}
        Redraw(_canvas);
    }
    public void ShowCosmeticEmote(string side,string id)
    {
        var emote=AppearanceRuntime.Profile(side).Emotes.FirstOrDefault(e=>e.Id==id);
        if(emote is not null)ShowCosmeticEmote(side,emote);
    }
    public void ShowCosmeticEmote(string side,CosmeticEmote emote)
    {
        if(_communicationsCut || !IsVisibleInTree())return;
        foreach(var old in _canvas.GetChildren().OfType<BattleEmoteBubble>().Where(b=>b.Side==side))
        { _canvas.RemoveChild(old); old.QueueFree(); }
        // Project placement outside card rows; original-client motion is still unverified.
        var bubble=new BattleEmoteBubble {
            Side=side, Position=new(1035,side=="self"?522:120), Size=new(217,123),
            ZIndex=150, MouseFilter=MouseFilterEnum.Ignore, Quiet=_clock.ReducedMotion
        };
        var variant=side=="enemy"&&emote.EnemyImage.Length>0;
        bubble.Configure(emote,AppearanceRuntime.Image(variant?emote.EnemyImage:emote.Image,_textures),variant); _canvas.AddChild(bubble);
    }
}

public partial class BattleEmoteButton : Button
{
    public bool Cut {get;set;}
    public override void _Draw()
    {
        if(OriginalCosmetics.Ui("Emote_256px") is {} texture)
            DrawTextureRect(texture,CosmeticPaper.Fit(texture,new(3,3,36,33)),false,Cut?new Color("88857c"):Colors.White);
    }
}

/// <summary>Compatibility helper: artwork comes exclusively from original texture files.</summary>
internal static class CosmeticPaper
{
    public static void DrawBack(Control canvas,Texture2D texture,Rect2 bounds,bool selected=false)
    {
        canvas.DrawTextureRect(texture,Fit(texture,bounds),false);
        if(selected&&OriginalCosmetics.Ui("T_customization-selection-border-2048_") is {} border)
            canvas.DrawTextureRect(border,bounds,false);
    }
    public static Rect2 Fit(Texture2D texture,Rect2 area)
    {
        var scale=Math.Min(area.Size.X/texture.GetWidth(),area.Size.Y/texture.GetHeight());
        var size=texture.GetSize()*scale;return new(area.GetCenter()-size/2,size);
    }
    public static void Draw(Control canvas,Rect2 rect)
    {
        if(OriginalCosmetics.DefaultPaper is {} texture)canvas.DrawTextureRect(texture,rect,false);
    }
}
public partial class BattleEmoteBubble : Control
{
    public string Side {get;set;}="self";
    public bool Quiet {get;set;}
    private string _caption="";
    private Texture2D? _image;
    private string _style="diy", _tint="433a29";
    private bool _enemyVariant;
    private double _age;
    // These are this client's readability settings, not measured original-client timings.
    private const double Lifetime=3.2;
    public void Configure(CosmeticEmote emote,Texture2D? image,bool enemyVariant=false)
    {
        _caption=emote.Label;_image=image;_style=emote.Style;_tint=emote.Tint;_enemyVariant=enemyVariant;
        if(_style is "paper" or "special"&&image is not null)Size=new(236,236f*image.GetHeight()/image.GetWidth());
    }
    public override void _Ready()
    {
        var label=new Label {
            Text=_caption, Position=new(_style=="diy"&&_image is not null?77:12,Size.Y*(_enemyVariant?.32f:.06f)), Size=new(_style=="diy"&&_image is not null?Size.X-89:Size.X-24,Size.Y*.53f),
            AutowrapMode=TextServer.AutowrapMode.WordSmart, HorizontalAlignment=HorizontalAlignment.Center,
            VerticalAlignment=VerticalAlignment.Center, MouseFilter=MouseFilterEnum.Ignore
        };
        label.AddThemeFontOverride("font",OriginalCosmetics.Font(_style=="special"?"NotoSansSC-Bold":"NotoSansSC-Regular"));
        var fontSize=_style=="special"?20:17;
        var font=OriginalCosmetics.Font(_style=="special"?"NotoSansSC-Bold":"NotoSansSC-Regular");
        while(fontSize>12&&font.GetMultilineStringSize(_caption,width:label.Size.X,fontSize:fontSize).Y>label.Size.Y)fontSize--;
        label.AddThemeFontSizeOverride("font_size",fontSize); label.AddThemeColorOverride("font_color",new(_tint)); AddChild(label);
    }
    public override void _Process(double delta)
    {
        _age+=delta;
        if(_age>Lifetime){QueueFree();return;}
        if(!Quiet)Modulate=new(1,1,1,(float)Math.Min(Math.Min(1,_age/.12),(Lifetime-_age)/.25));
    }
    public override void _Draw()
    {
        if(_style is "paper" or "special" && _image is not null)DrawTextureRect(_image,new(Vector2.Zero,Size),false);
        else
        {
            CosmeticPaper.Draw(this,new(Vector2.Zero,Size));
            if(_image is not null)DrawTextureRect(_image,CosmeticPaper.Fit(_image,new(9,9,60,Size.Y*.53f)),false);
        }
    }
}
