using Godot;
using Kards.Ui.Core;

namespace Kards.Ui;

public partial class AppearanceScreen : VBoxContainer
{
    private CosmeticLibrary _library=null!;
    private TextureCache _textures=null!;
    private CosmeticProfile _draft=new();
    private ItemList _list=null!;
    private CosmeticPreview _preview=null!;
    private int _category;
    private readonly List<Button> _categories=[];
    private string? _deckId, _mainNation;
    private Label _heading=null!, _scope=null!;
    private LineEdit _hqName=null!,_emoteLabel=null!;
    private OptionButton _emoteSlots=null!;
    private HBoxContainer _emoteRow=null!;
    private Label _status=null!;
    private Button _delete=null!;
    private CosmeticAsset[] _visibleAssets=[];
    private CosmeticEmote?[] _visiblePresets=[];
    private LineEdit _search=null!;
    private OptionButton _emoteFilter=null!;
    private Label _pageStatus=null!;
    private int _page;
    private const int PageSize=18;
    private int _slot;
    private bool _refreshing;
    private string Kind => new[]{"back","decoration","hq","emote"}[_category];
    public event Action? Equipped;
    public event Action? BackRequested;
    public void Initialize(CosmeticLibrary library,TextureCache textures)
    {
        _library=library;_textures=textures;
        AddThemeConstantOverride("separation",14);
        var header=new HBoxContainer();AddChild(header);
        _heading=MenuStyles.Label("卡组外观",28,MenuStyles.Ink);header.AddChild(_heading);
        header.AddChild(new Control{SizeFlagsHorizontal=SizeFlags.ExpandFill});
        header.AddChild(MenuStyles.Button("返回我的卡组",()=>BackRequested?.Invoke()));
        _scope=MenuStyles.Label("",14,MenuStyles.Gold);_scope.AutowrapMode=TextServer.AutowrapMode.WordSmart;AddChild(_scope);
        var columns=new HBoxContainer{SizeFlagsVertical=SizeFlags.ExpandFill};columns.AddThemeConstantOverride("separation",16);AddChild(columns);
        var categories=new VBoxContainer{CustomMinimumSize=new(128,0)};categories.AddThemeConstantOverride("separation",3);columns.AddChild(categories);
        foreach(var name in new[]{"卡背","桌饰","总部","表情"})
        {
            var index=_categories.Count;var button=new AppearanceCategoryButton{Caption=name,Category=index,TooltipText=name};
            button.Pressed+=()=>SelectCategory(index);
            button.ToggleMode=true;button.Alignment=HorizontalAlignment.Left;button.CustomMinimumSize=new(128,94);
            OriginalCosmetics.StyleButton(button);categories.AddChild(button);_categories.Add(button);
        }
        var left=new VBoxContainer{CustomMinimumSize=new(300,0)};columns.AddChild(left);
        _search=new LineEdit{PlaceholderText="搜索原版装扮…",ClearButtonEnabled=true};left.AddChild(_search);
        _search.TextChanged+=_=>{_page=0;RefreshControls();};
        _emoteFilter=new OptionButton();foreach(var caption in new[]{"当前国家表情","普通表情","特殊表情","全部国家（DIY）"})_emoteFilter.AddItem(caption);
        _emoteFilter.ItemSelected+=_=>{_page=0;RefreshControls();};left.AddChild(_emoteFilter);
        _list=new ItemList{SizeFlagsVertical=SizeFlags.ExpandFill,CustomMinimumSize=new(300,250),MaxColumns=2,SameColumnWidth=true,IconMode=ItemList.IconModeEnum.Top,FixedIconSize=new(90,100),FixedColumnWidth=112,MaxTextLines=2,TextOverrunBehavior=TextServer.OverrunBehavior.TrimEllipsis};left.AddChild(_list);
        _list.AddThemeConstantOverride("h_separation",8);
        _list.ItemSelected+=i=>SelectImage((int)i);
        var pages=new HBoxContainer();left.AddChild(pages);
        pages.AddChild(MenuStyles.Button("‹",()=>{if(_page>0){_page--;RefreshControls();}}));
        _pageStatus=MenuStyles.Label("",12,MenuStyles.Dim);_pageStatus.SizeFlagsHorizontal=SizeFlags.ExpandFill;pages.AddChild(_pageStatus);
        pages.AddChild(MenuStyles.Button("›",()=>{_page++;RefreshControls();}));
        left.AddChild(MenuStyles.Button("导入图片…",ChooseImage));
        _delete=MenuStyles.Button("删除所选图片",DeleteSelected);left.AddChild(_delete);
        var right=new VBoxContainer{SizeFlagsHorizontal=SizeFlags.ExpandFill};right.AddThemeConstantOverride("separation",12);columns.AddChild(right);
        _preview=new CosmeticPreview{CustomMinimumSize=new(420,310),SizeFlagsHorizontal=SizeFlags.ExpandFill,SizeFlagsVertical=SizeFlags.ExpandFill};_preview.Initialize(textures);right.AddChild(_preview);
        right.AddChild(MenuStyles.Label("原版图片与字体 · 装备会同步给对手",13,MenuStyles.Dim));
        _hqName=new LineEdit{PlaceholderText="总部名称（留空沿用原名）",MaxLength=24};right.AddChild(_hqName);
        _hqName.TextChanged+=text=>{if(!_refreshing){_draft=_draft with{HeadquartersName=text};UpdatePreview();}};
        _emoteRow=new HBoxContainer();right.AddChild(_emoteRow);
        _emoteSlots=new OptionButton();_emoteRow.AddChild(_emoteSlots);
        _emoteSlots.ItemSelected+=i=>{_slot=(int)i;RefreshControls();};
        _emoteLabel=new LineEdit{PlaceholderText="表情文字（DIY）",MaxLength=160,SizeFlagsHorizontal=SizeFlags.ExpandFill};_emoteRow.AddChild(_emoteLabel);
        _emoteLabel.TextChanged+=text=>{if(!_refreshing){var emotes=_draft.Emotes.ToArray();emotes[_slot]=emotes[_slot] with{Label=text};_draft=_draft with{Emotes=emotes};UpdatePreview();}};
        _emoteRow.AddChild(MenuStyles.Button("＋",()=>{if(_draft.Emotes.Length<CosmeticProfile.MaximumEmotes){_draft=_draft with{Emotes=_draft.Emotes.Append(new CosmeticEmote(Guid.NewGuid().ToString("N"),"你好！")).ToArray()};_slot=_draft.Emotes.Length-1;RefreshControls();}else _status.Text=$"最多装备 {CosmeticProfile.MaximumEmotes} 条表情。";}));
        _emoteRow.AddChild(MenuStyles.Button("移除",()=>{if(_draft.Emotes.Length>1){_draft=_draft with{Emotes=_draft.Emotes.Where((_,i)=>i!=_slot).ToArray()};_slot=0;RefreshControls();}}));
        var actions=new HBoxContainer();right.AddChild(actions);
        actions.AddChild(MenuStyles.Button("恢复本分类默认",ResetCategory));
        var equip=MenuStyles.Button("装备并保存",Save);MenuStyles.Primary(equip);actions.AddChild(equip);
        _status=MenuStyles.Label("",14,MenuStyles.Dim);_status.AutowrapMode=TextServer.AutowrapMode.WordSmart;right.AddChild(_status);
        Refresh();
    }
    public void OpenDeck(ConstructedDeck? deck)
    {
        _deckId=deck?.Id;_mainNation=deck?.Options.MainNation;
        _heading.Text=deck is null?"请先选择卡组":"自定义 · "+deck.Name;
        _heading.ClipText=true;_heading.SizeFlagsHorizontal=SizeFlags.ExpandFill;
        Refresh();
    }
    private void SelectCategory(int category){_category=category;_page=0;RefreshControls();}
    public void SetStatus(string text) { if(IsInsideTree()) _status.Text=text; }
    public void Refresh() { _draft=_library.Resolve(_deckId,_mainNation);_slot=Math.Min(_slot,_draft.Emotes.Length-1);RefreshControls();_status.Text=_library.LoadError??"选择物品查看预览，点击“装备并保存”生效。"; }
    private void RefreshControls()
    {
        _refreshing=true;
        for(var i=0;i<_categories.Count;i++){_categories[i].SetPressedNoSignal(i==_category);_categories[i].AddThemeColorOverride("font_color",i==_category?MenuStyles.Gold:MenuStyles.Ink);}
        _scope.Text=_deckId is null?"从“我的卡组 → 自定义外观”进入。":Kind is "back" or "hq"?"装备范围：仅当前卡组":"装备范围：所有以“"+SetupDeckBack.NationLabel(_mainNation??"")+"”为主国的卡组";
        var choices=Kind=="emote"
            ?OriginalCosmetics.Emotes.Where(e=>_emoteFilter.Selected==3||e.Nations.Length==0||e.Nations.Contains(_mainNation??"",StringComparer.OrdinalIgnoreCase))
                .Where(e=>_emoteFilter.Selected switch{1=>e.Style=="paper",2=>e.Style=="special",_=>true}).Select(e=>(Asset:new CosmeticAsset("emote",e.Title,e.Image,true),Preset:(CosmeticEmote?)e.Emote))
                .Concat(_library.Assets.Where(a=>a.Kind==Kind&&!a.BuiltIn).Select(a=>(Asset:a,Preset:(CosmeticEmote?)null)))
            :_library.Assets.Where(a=>a.Kind==Kind).Select(a=>(Asset:a,Preset:(CosmeticEmote?)null));
        var filtered=choices.Where(c=>c.Asset.Name.Contains(_search.Text,StringComparison.OrdinalIgnoreCase)).ToArray();
        var pageCount=Math.Max(1,(filtered.Length+PageSize-1)/PageSize);_page=Math.Min(_page,pageCount-1);
        var visible=filtered.Skip(_page*PageSize).Take(PageSize).ToArray();
        _visibleAssets=visible.Select(c=>c.Asset).ToArray();_visiblePresets=visible.Select(c=>c.Preset).ToArray();
        _pageStatus.Text=$"{_page+1}/{pageCount} · {filtered.Length} 项";
        _list.Clear();_list.AddItem(Kind=="decoration"?"不摆放桌饰":Kind=="emote"?"普通表情":"默认外观");
        foreach(var asset in _visibleAssets)_list.AddItem(asset.Name,AppearanceRuntime.Image(asset.Image,_textures));
        _emoteSlots.Clear();for(var i=0;i<_draft.Emotes.Length;i++)_emoteSlots.AddItem("表情 "+(i+1));_emoteSlots.Select(_slot);
        _hqName.Visible=Kind=="hq";_hqName.Text=_draft.HeadquartersName;
        _emoteFilter.Visible=Kind=="emote";
        _emoteRow.Visible=Kind=="emote";_emoteLabel.Text=_draft.Emotes[_slot].Label;
        var selected=Kind switch{"back"=>_draft.CardBack,"decoration"=>_draft.Decoration,"hq"=>_draft.Headquarters,_=>_draft.Emotes[_slot].Image};
        var index=Kind=="emote"?Array.FindIndex(_visiblePresets,e=>e?.Id==_draft.Emotes[_slot].Id)+1:Array.FindIndex(_visibleAssets,a=>a.Image==selected)+1;
        _list.Select(index);_delete.Disabled=index==0||_visibleAssets[index-1].BuiltIn;
        _refreshing=false;UpdatePreview();
    }
    private void SelectImage(int index)
    {
        var image=index==0?"":_visibleAssets[index-1].Image;
        if(Kind=="emote")
        {
            var emotes=_draft.Emotes.ToArray();var previous=emotes[_slot];
            var chosen=index>0&&_visiblePresets[index-1] is {} preset?preset:previous with{Image=image,Style="diy",EnemyImage="",Tint="433a29"};
            var duplicate=Array.FindIndex(emotes,e=>e.Id==chosen.Id);
            if(duplicate>=0&&duplicate!=_slot)emotes[duplicate]=previous;
            emotes[_slot]=chosen;_draft=_draft with{Emotes=emotes};_refreshing=true;_emoteLabel.Text=chosen.Label.Replace('\n',' ');_refreshing=false;
        }
        else _draft=Kind switch{"back"=>_draft with{CardBack=image},"decoration"=>_draft with{Decoration=image},_=>_draft with{Headquarters=image}};
        _delete.Disabled=index==0||_visibleAssets[index-1].BuiltIn;UpdatePreview();
    }
    private void UpdatePreview(){_preview.MainNation=_mainNation??"";_preview.Profile=_draft;_preview.EmoteSlot=_slot;_preview.QueueRedraw();}
    private void ResetCategory()
    {
        _draft=Kind switch{"back"=>_draft with{CardBack=""},"decoration"=>_draft with{Decoration=""},"hq"=>_draft with{Headquarters="",HeadquartersName=""},_=>_draft with{Emotes=OriginalCosmetics.Defaults(_mainNation??"")}};
        _slot=0;RefreshControls();
    }
    private void Save()
    {
        try{if(_deckId is null)throw new InvalidOperationException("请先选择要装扮的卡组。");_library.SaveFor(_deckId,_mainNation,_draft);_status.Text=Kind is "back" or "hq"?"已为当前卡组装备。":"已为该主国家的所有卡组装备。";Equipped?.Invoke();}
        catch(Exception e){_status.Text=e.Message;}
    }
    private void ChooseImage()
    {
        var kind=Kind;
        var dialog=new FileDialog{Access=FileDialog.AccessEnum.Filesystem,FileMode=FileDialog.FileModeEnum.OpenFile,UseNativeDialog=true,Title="导入外观图片",Filters=["*.png,*.jpg,*.jpeg,*.webp ; 图片"]};AddChild(dialog);
        dialog.FileSelected+=path=>{
            try{var asset=_library.Import(kind,Path.GetFileNameWithoutExtension(path),AppearanceRuntime.ImportImage(path));_search.Text=asset.Name;RefreshControls();if(Kind==kind)SelectImage(Array.FindIndex(_visibleAssets,a=>a.Image==asset.Image)+1);_status.Text="图片已导入，确认预览后点击装备。";}
            catch(Exception e){_status.Text=e.Message;}finally{dialog.QueueFree();}
        };
        dialog.Canceled+=()=>dialog.QueueFree();dialog.PopupCentered(new Vector2I(780,520));
    }
    private void DeleteSelected()
    {
        var selected=_list.GetSelectedItems();if(selected.Length==0||selected[0]==0)return;var asset=_visibleAssets[selected[0]-1];
        var dialog=new ConfirmationDialog{Title="删除外观图片",DialogText="删除“"+asset.Name+"”？已装备的此图片会恢复默认。原始导入文件会保留。"};AddChild(dialog);
        dialog.Confirmed+=()=>{try{_library.Remove(asset);Refresh();Equipped?.Invoke();}catch(Exception e){_status.Text=e.Message;}dialog.QueueFree();};dialog.Canceled+=()=>dialog.QueueFree();dialog.PopupCentered();
    }
}

public partial class AppearanceCategoryButton : Button
{
    public string Caption {get;set;}="";
    public int Category {get;set;}
    public override void _Draw()
    {
        var names=new[]{"T_nui-icon-Cardback-gritty-256","T_nui-icon-boarditem-gritty-256","T_nui-icon-HQ-gritty-256","T_nui-icon-emote-gritty-256"};
        var ink=ButtonPressed?new Color("dba343"):new Color("d3d0bd");
        if(OriginalCosmetics.Ui(names[Category]) is {} texture)
            DrawTextureRect(texture,CosmeticPaper.Fit(texture,new(Size.X/2-25,9,50,49)),false,ink);
        var font=OriginalCosmetics.Font("NotoSansSC-Bold");var width=font.GetStringSize(Caption,fontSize:16).X;
        DrawString(font,new((Size.X-width)/2,Size.Y-13),Caption,fontSize:16,modulate:ink);
    }
}
public partial class CosmeticPreview : Control
{
    public CosmeticProfile Profile {get;set;}=new();
    public int EmoteSlot {get;set;}
    public string MainNation {get;set;}="";
    private TextureCache _textures=null!;
    public void Initialize(TextureCache textures){_textures=textures;Resized+=QueueRedraw;MouseFilter=MouseFilterEnum.Ignore;}
    public override void _Draw()
    {
        if(_textures is null)return;
        var board=new Rect2(Vector2.Zero,Size);
        var wood=OriginalCosmetics.Ui("T_nui-board-render-default-2048");
        DrawRect(board,new("392f23"));if(wood is not null)DrawTextureRect(wood,board,false,new Color(.72f,.70f,.64f));
        DrawRect(board,new("746b4a"),false,1);
        var back=AppearanceRuntime.Image(Profile.CardBack,_textures)??AppearanceRuntime.Image(OriginalCosmetics.DefaultBack(MainNation),_textures);
        var hq=AppearanceRuntime.Image(Profile.Headquarters,_textures)??AppearanceRuntime.Image(OriginalCosmetics.DefaultHq(MainNation),_textures);
        var cardSize=new Vector2(94,132);var y=Math.Max(24,(Size.Y-285)/2);
        if(back is not null)CosmeticPaper.DrawBack(this,back,new(new(Size.X*.15f,y),cardSize));
        var headquarters=new Rect2(new(Size.X*.62f,y),cardSize);
        if(hq is not null)DrawTextureRect(hq,headquarters,false);
        var font=OriginalCosmetics.Font();
        DrawString(font,new(Size.X*.15f,y+155),"卡背",fontSize:15,modulate:new("e9e1c9"));
        var title=Profile.HeadquartersName.Length>0?Profile.HeadquartersName:"总部";
        if(Profile.HeadquartersName.Length>0)
        {DrawRect(new Rect2(headquarters.Position+new Vector2(4,5),new(86,23)),new("222821cc"));DrawString(font,headquarters.Position+new Vector2(8,21),title,HorizontalAlignment.Left,78,12,new("eee6cc"));}
        var badge=headquarters.Position+new Vector2(47,107);
        if(OriginalCosmetics.Ui("IconDefenceBase") is {} shield)DrawTextureRect(shield,new(badge-new Vector2(18,18),new(36,39)),false);
        var numberFont=OriginalCosmetics.Font("D_Day_Stencil");
        DrawString(numberFont,badge+new Vector2(-13,7),"20",fontSize:22,modulate:new("eee6cc"));
        DrawString(font,new(Size.X*.62f,y+155),"总部 · 防御示意",fontSize:15,modulate:new("e9e1c9"));
        if(AppearanceRuntime.Image(Profile.Decoration,_textures) is {} decoration)DrawTextureRect(decoration,CosmeticPaper.Fit(decoration,new(new(Size.X*.12f,y+182),new(95,110))),false);
        var emote=Profile.Emotes[Math.Clamp(EmoteSlot,0,Profile.Emotes.Length-1)];
        var bubbleWidth=Math.Min(235,Size.X*.52f);
        var bubble=new Rect2(Size.X-bubbleWidth-24,y+178,bubbleWidth,134);
        var face=AppearanceRuntime.Image(emote.Image,_textures);
        if(emote.Style is "paper" or "special"&&face is not null){bubble=CosmeticPaper.Fit(face,bubble);DrawTextureRect(face,bubble,false);}else CosmeticPaper.Draw(this,bubble);
        var body=new Rect2(bubble.Position+new Vector2(12,bubble.Size.Y*.06f),new Vector2(bubble.Size.X-24,bubble.Size.Y*.53f));
        if(emote.Style=="diy"&&face is not null)
        {DrawTextureRect(face,CosmeticPaper.Fit(face,new(bubble.Position+new Vector2(9,8),new(48,52))),false);body.Position+=new Vector2(53,0);body.Size-=new Vector2(53,0);}
        var emoteFont=OriginalCosmetics.Font(emote.Style=="special"?"NotoSansSC-Bold":"NotoSansSC-Regular");
        var fontSize=emote.Style=="special"?20:17;
        while(fontSize>12&&emoteFont.GetMultilineStringSize(emote.Label,width:body.Size.X,fontSize:fontSize).Y>body.Size.Y)fontSize--;
        var textHeight=emoteFont.GetMultilineStringSize(emote.Label,width:body.Size.X,fontSize:fontSize).Y;
        DrawMultilineString(emoteFont,body.Position+new Vector2(0,Math.Max(0,(body.Size.Y-textHeight)/2)+emoteFont.GetAscent(fontSize)),emote.Label,HorizontalAlignment.Center,body.Size.X,fontSize,-1,new(emote.Tint));

    }
}
