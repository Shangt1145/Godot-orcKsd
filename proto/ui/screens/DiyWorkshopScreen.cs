using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

/// <summary>Pack authoring navigation; all executable behavior is compiled by the native bridge.</summary>
public partial class DiyWorkshopScreen : VBoxContainer
{
    private DiyWorkshopLibrary _store=null!;
    private DiyCardLibrary _cards=null!;
    private DiyEditorScreen _editor=null!;
    private DeckConstructionService _service=null!;
    private TextureCache _textures=null!;
    private Action _changed=null!;
    private Func<bool> _reduced=null!;
    private VBoxContainer _navigation=null!,_workspace=null!;
    private Label _notice=null!;
    private TabContainer _tabs=null!;
    private VBoxContainer _keywordPane=null!,_poolPane=null!;
    private DiyPackDraft? _pack;
    private bool _animating;
    private int _navigationVersion;
    private int _page;
    internal Button PackChoice=null!,ExpansionChoice=null!,CreateChoice=null!,ModifyChoice=null!,ConfirmPack=null!;
    internal OptionButton BaseChoice=null!,ModifyPick=null!;
    internal LineEdit PackName=null!,KeywordName=null!;
    internal TextEdit KeywordText=null!;
    internal Button SaveKeyword=null!;
    internal TabContainer Tabs=>_tabs;
    internal DiyPackDraft? CurrentPack=>_pack;
    public void Initialize(DiyWorkshopLibrary store,DiyCardLibrary cards,DiyEditorScreen editor,DeckConstructionService service,TextureCache textures,Action changed,Func<bool> reduced)
    {
        _store=store;_cards=cards;_editor=editor;_service=service;_textures=textures;_changed=changed;_reduced=reduced;
        SizeFlagsVertical=SizeFlags.ExpandFill;AddThemeConstantOverride("separation",12);
        var top=new HBoxContainer();AddChild(top);top.AddChild(UiStyles.Label("DIY 工坊",28,UiStyles.Gold));top.AddChild(new Control{SizeFlagsHorizontal=SizeFlags.ExpandFill});
        top.AddChild(UiStyles.Button("工坊首页",()=>_editor.GuardNavigation(ShowHome)));
        _notice=UiStyles.Label("",13,UiStyles.Dim);_notice.AutowrapMode=TextServer.AutowrapMode.WordSmart;AddChild(_notice);
        _navigation=new VBoxContainer{SizeFlagsVertical=SizeFlags.ExpandFill};AddChild(_navigation);
        _workspace=new VBoxContainer{SizeFlagsVertical=SizeFlags.ExpandFill};AddChild(_workspace);AddChild(_editor);ShowHome();
        VisibilityChanged+=()=>{if(!Visible&&_animating)ShowHome();};
    }
    private static Button Choice(Node row,string title,string detail)
    {
        var card=new Button{CustomMinimumSize=new(290,350),SizeFlagsHorizontal=SizeFlags.ShrinkCenter,SizeFlagsVertical=SizeFlags.ShrinkCenter};row.AddChild(card);
        card.AddThemeStyleboxOverride("normal",UiStyles.Box(new Color("263d35"),UiStyles.Gold,12,20));
        var content=new VBoxContainer{MouseFilter=MouseFilterEnum.Ignore,Alignment=BoxContainer.AlignmentMode.Center};card.AddChild(content);content.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var icon=UiStyles.Label("✦",48,UiStyles.Gold);icon.HorizontalAlignment=HorizontalAlignment.Center;content.AddChild(icon);
        var name=UiStyles.Label(title,26,UiStyles.Gold);name.HorizontalAlignment=HorizontalAlignment.Center;content.AddChild(name);
        var hint=UiStyles.Label(detail,16);hint.HorizontalAlignment=HorizontalAlignment.Center;content.AddChild(hint);
        foreach(var child in content.GetChildren().OfType<Control>())child.MouseFilter=MouseFilterEnum.Ignore;
        return card;
    }
    private HBoxContainer ChoiceRow(string hint)
    {
        UiStyles.Clear(_navigation);_navigation.AddChild(UiStyles.Label(hint,18));
        var row=new HBoxContainer{Alignment=BoxContainer.AlignmentMode.Center,SizeFlagsVertical=SizeFlags.ExpandFill};row.AddThemeConstantOverride("separation",60);_navigation.AddChild(row);return row;
    }
    internal void ShowHome()
    {
        _navigationVersion++;
        _workspace.Hide();_editor.Hide();_navigation.Show();_pack=null;_animating=false;
        _notice.Text=_store.LoadError??"从一个卡包开始，或基于已有卡包制作扩展内容。";
        var row=ChoiceRow("选择你要制作的内容");
        PackChoice=Choice(row,"卡包","创建 / 修改卡包");ExpansionChoice=Choice(row,"扩展包","创建 / 修改扩展包");
        PackChoice.Pressed+=()=>ChooseKind(false,PackChoice,ExpansionChoice);ExpansionChoice.Pressed+=()=>ChooseKind(true,ExpansionChoice,PackChoice);
    }
    private async void ChooseKind(bool expansion,Button selected,Button other)
    {
        if(_animating)return;_animating=true;selected.Disabled=other.Disabled=true;
        var version=_navigationVersion;
        selected.PivotOffset=selected.Size/2;other.PivotOffset=other.Size/2;
        if(!_reduced())
        {
            var tween=CreateTween().SetParallel();tween.TweenProperty(selected,"scale",new Vector2(1.08f,1.08f),.24).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
            tween.TweenProperty(other,"modulate:a",0f,.2);tween.TweenProperty(other,"scale",new Vector2(.85f,.85f),.2);await ToSignal(tween,Tween.SignalName.Finished);
        }
        if(version!=_navigationVersion)return;
        var row=ChoiceRow(expansion?"扩展包：创建还是修改？":"卡包：创建还是修改？");
        CreateChoice=Choice(row,"创建",expansion?"先选择基础卡包":"建立新的卡包");ModifyChoice=Choice(row,"修改","打开已保存的内容");
        CreateChoice.Pressed+=()=>OpenSelection(expansion,false);ModifyChoice.Pressed+=()=>OpenSelection(expansion,true);_animating=false;
    }
    internal void OpenSelection(bool expansion,bool modifying)
    {
        _navigationVersion++;
        UiStyles.Clear(_navigation);_navigation.AddChild(UiStyles.Label((modifying?"修改":"创建")+(expansion?"扩展包":"卡包"),23,UiStyles.Gold));
        var form=new VBoxContainer{CustomMinimumSize=new(500,0),SizeFlagsHorizontal=SizeFlags.ShrinkCenter};_navigation.AddChild(form);
        DiyPackDraft draft=new(){Name=expansion?"新扩展包":"新卡包"};
        ModifyPick=new OptionButton();
        if(modifying)
        {
            ModifyPick.AddItem("请选择要修改的内容");ModifyPick.SetItemMetadata(0,"");
            foreach(var p in _store.Packs.Where(p=>p.IsExpansion==expansion)){ModifyPick.AddItem(p.Name);ModifyPick.SetItemMetadata(ModifyPick.ItemCount-1,p.Id);}
            DiyEditorScreen.Field(form,"已保存的内容",ModifyPick);
        }
        else{form.AddChild(ModifyPick);ModifyPick.Hide();}
        PackName=new LineEdit{Text=draft.Name,MaxLength=60};DiyEditorScreen.Field(form,"名称",PackName);
        BaseChoice=new OptionButton();BaseChoice.AddItem("请先选择基础卡包");BaseChoice.SetItemMetadata(0,"");
        if(expansion)
        {
            var bases=new Dictionary<string,string>{{"source","太空战争"},{"pack/builtin","内置原版卡包"}};
            foreach(var p in _store.Packs.Where(p=>!p.IsExpansion))bases[p.Id]=p.Name;
            foreach(var p in bases){BaseChoice.AddItem(p.Value);BaseChoice.SetItemMetadata(BaseChoice.ItemCount-1,p.Key);}
            DiyEditorScreen.Field(form,"基于哪个卡包",BaseChoice);
        }
        else{form.AddChild(BaseChoice);BaseChoice.Hide();}
        ConfirmPack=UiStyles.Button(modifying?"进入修改":"创建并进入",()=>
        {
            try
            {
                var parent=expansion?DiyEditorScreen.Value(BaseChoice):null;
                if(expansion&&string.IsNullOrEmpty(parent))throw new InvalidDataException("请先选择基础卡包");
                if(modifying&&DiyEditorScreen.Value(ModifyPick).Length==0)throw new InvalidDataException("请选择要修改的内容");
                var pack=draft with{Name=PackName.Text.Trim(),BasePackId=parent};
                _store.Save(pack);_changed();OpenPack(pack);
            }catch(Exception e){_notice.Text=e.Message;}
        });form.AddChild(ConfirmPack);
        void Ready()=>ConfirmPack.Disabled=string.IsNullOrWhiteSpace(PackName.Text)||(expansion&&DiyEditorScreen.Value(BaseChoice).Length==0)||(modifying&&DiyEditorScreen.Value(ModifyPick).Length==0);
        PackName.TextChanged+=_=>Ready();BaseChoice.ItemSelected+=_=>Ready();
        ModifyPick.ItemSelected+=_=>
        {
            var id=DiyEditorScreen.Value(ModifyPick);var saved=_store.Packs.FirstOrDefault(p=>p.Id==id);if(saved is not null){draft=saved;PackName.Text=saved.Name;if(expansion)DiyEditorScreen.Select(BaseChoice,saved.BasePackId!);}Ready();
        };
        if(modifying&&ModifyPick.ItemCount==1)_notice.Text="还没有已保存的"+(expansion?"扩展包":"卡包")+"，可以先创建一个。";Ready();
    }
    internal void OpenPack(DiyPackDraft pack)
    {
        _navigationVersion++;
        _pack=pack;_page=0;_navigation.Hide();_workspace.Show();
        if(_editor.GetParent() is { } parent)parent.RemoveChild(_editor);
        UiStyles.Clear(_workspace);
        var heading=new HBoxContainer();_workspace.AddChild(heading);heading.AddChild(UiStyles.Label(pack.Name,23,UiStyles.Gold));
        heading.AddChild(UiStyles.Button("修改名称 / 基础卡包",()=>_editor.GuardNavigation(()=>{_workspace.Hide();_navigation.Show();OpenSelection(pack.IsExpansion,true);DiyEditorScreen.Select(ModifyPick,pack.Id);ModifyPick.EmitSignal(OptionButton.SignalName.ItemSelected,(long)ModifyPick.Selected);})));
        if(pack.IsExpansion)heading.AddChild(UiStyles.Label("基于："+(_store.Packs.FirstOrDefault(p=>p.Id==pack.BasePackId)?.Name??(pack.BasePackId=="source"?"太空战争":"内置原版卡包")),14,UiStyles.Dim));
        _tabs=new TabContainer{SizeFlagsVertical=SizeFlags.ExpandFill};_workspace.AddChild(_tabs);
        if(!pack.IsExpansion){_keywordPane=new VBoxContainer{Name="创建词条",SizeFlagsVertical=SizeFlags.ExpandFill};_tabs.AddChild(_keywordPane);BuildKeywords();}
        _editor.Name="创建卡牌";_tabs.AddChild(_editor);_editor.Show();_editor.SetPackScope(pack);
        _poolPane=new VBoxContainer{Name="卡池预览",SizeFlagsVertical=SizeFlags.ExpandFill};_tabs.AddChild(_poolPane);
        _tabs.TabChanged+=_=>{if(_poolPane.Visible)RefreshPool();};
        _tabs.CurrentTab=pack.IsExpansion?0:1;
        _notice.Text=pack.IsExpansion?"扩展包继承基础卡包的词条与国家；这里只修改本扩展包的卡牌。":"词条、卡牌和国家保存在此卡包中。卡牌完整编译后即可用于构筑。";
    }
    private void BuildKeywords()
    {
        UiStyles.Clear(_keywordPane);var pack=_store.Packs.Single(p=>p.Id==_pack!.Id);DiyKeywordDraft draft=new();
        var row=new HBoxContainer{SizeFlagsVertical=SizeFlags.ExpandFill};_keywordPane.AddChild(row);
        var sidebar=new VBoxContainer{CustomMinimumSize=new(220,0)};row.AddChild(sidebar);
        var list=new ItemList{SizeFlagsVertical=SizeFlags.ExpandFill,CustomMinimumSize=new(210,120)};sidebar.AddChild(list);
        foreach(var k in pack.Keywords)list.AddItem(k.Name);
        var form=new VBoxContainer{SizeFlagsHorizontal=SizeFlags.ExpandFill};row.AddChild(form);
        KeywordName=new LineEdit{Text=draft.Name,MaxLength=30};DiyEditorScreen.Field(form,"词条名称",KeywordName);
        var description=new TextEdit{CustomMinimumSize=new(0,65),WrapMode=TextEdit.LineWrappingMode.Boundary};DiyEditorScreen.Field(form,"给玩家看的说明",description);
        KeywordText=new TextEdit{CustomMinimumSize=new(0,110),PlaceholderText="例如：友方回合开始时，获得 +1+1。",WrapMode=TextEdit.LineWrappingMode.Boundary};DiyEditorScreen.Field(form,"实际效果",KeywordText);
        var battle=new CheckBox{Text="属于对战词条"};form.AddChild(battle);
        var status=UiStyles.Label("填写效果或打开结构编辑器，然后保存词条。",13,UiStyles.Dim);status.AutowrapMode=TextServer.AutowrapMode.WordSmart;form.AddChild(status);
        void Load(DiyKeywordDraft k){draft=k.Snapshot();KeywordName.Text=k.Name;description.Text=k.Description;KeywordText.Text=k.EffectText;battle.ButtonPressed=k.Battle;status.Text=WorkshopAuthoring.Errors.GetValueOrDefault(k.Id)??"效果可在下方重新校验。";}
        sidebar.AddChild(UiStyles.Button("新建词条",()=>Load(new())));list.ItemSelected+=i=>Load(pack.Keywords[(int)i]);
        var edit=UiStyles.Button("打开效果结构编辑器…",()=>
        {
            var fx=new DiyEffectEditorScreen{SizeFlagsVertical=SizeFlags.ExpandFill};_keywordPane.AddChild(fx);row.Hide();fx.Initialize();
            fx.Applied+=(effects,_)=>draft=draft with{Effects=effects};
            fx.DraftEdited+=card=>draft=draft with{Effects=card.OrcEffects,EffectText=card.EffectText};
            fx.InspectorSaved+=card=>draft=draft with{Effects=card.OrcEffects,EffectText=card.EffectText};
            fx.Finished+=()=>{KeywordText.Text=draft.EffectText;fx.QueueFree();row.Show();status.Text="结构已更新，点击保存词条。";};
            fx.Open(new(){Id="diy/"+draft.Id,Name=KeywordName.Text,EffectText=KeywordText.Text,OrcEffects=draft.Effects});
        });form.AddChild(edit);
        KeywordText.TextChanged+=()=>{if(KeywordText.Text!=draft.EffectText)draft=draft with{Effects=[]};};
        SaveKeyword=UiStyles.Button("校验并保存词条",()=>
        {
            var keyword=draft with{Name=KeywordName.Text.Trim(),Description=description.Text,EffectText=KeywordText.Text,Battle=battle.ButtonPressed};
            if(WorkshopAuthoring.ValidateKeyword(keyword) is { } error){status.Text="请修正效果："+error;return;}
            try{_store.Save(pack with{Keywords=pack.Keywords.Where(k=>k.Id!=keyword.Id).Append(keyword).ToArray()});_changed();_pack=_store.Packs.Single(p=>p.Id==pack.Id);BuildKeywords();_notice.Text="词条已保存，可在本卡包及其扩展包的卡牌上选择。";}
            catch(Exception e){status.Text=e.Message;}
        });form.AddChild(SaveKeyword);
    }
    public void RefreshData(){if(_pack is not null&&_poolPane is not null&&_poolPane.Visible)RefreshPool();}
    private void RefreshPool()
    {
        if(_pack is null)return;UiStyles.Clear(_poolPane);
        var saved=_cards.Cards;var family=DeckCardPacks.Family(_pack.Id);
        var available=_service.PlayableCards.Where(c=>DeckCardPacks.Of(c.CardId)==family)
            .Where(c=>!_pack.IsExpansion||saved.FirstOrDefault(d=>d.Id==c.CardId) is not { } d||d.PackId==_pack.Id||d.PackId==_pack.BasePackId).ToList();
        var drafts=saved.Where(c=>c.PackId==_pack.Id&&!available.Any(a=>a.CardId==c.Id)).ToArray();
        var all=available.Concat(drafts.Select(d=>DiyCardCompiler.Preview(d,_service.Cards))).ToArray();
        var bar=new HBoxContainer();_poolPane.AddChild(bar);bar.AddChild(UiStyles.Label($"可游玩 {available.Count} 张 · 待完善 {drafts.Length} 张"+(_pack.IsExpansion?" · 包含基础卡包":" · 包含扩展内容"),16));
        var count=Math.Max(1,(all.Length+23)/24);_page=Math.Clamp(_page,0,count-1);
        bar.AddChild(UiStyles.Button("上一页",()=>{_page=Math.Max(0,_page-1);RefreshPool();}));bar.AddChild(UiStyles.Label($"{_page+1}/{count}",14));bar.AddChild(UiStyles.Button("下一页",()=>{_page=Math.Min(count-1,_page+1);RefreshPool();}));
        var scroll=new ScrollContainer{SizeFlagsVertical=SizeFlags.ExpandFill};_poolPane.AddChild(scroll);var grid=new GridContainer{Columns=6};scroll.AddChild(grid);
        foreach(var card in all.Skip(_page*24).Take(24))
        {
            var box=new VBoxContainer();grid.AddChild(box);var face=new CardControl();face.Bind(card,_textures,145);box.AddChild(face);
            var isDraft=drafts.Any(d=>d.Id==card.CardId);box.AddChild(UiStyles.Label(isDraft?"待完善 · "+card.Name:card.Name,12,isDraft?new Color("e6ad7b"):UiStyles.Dim));
        }
    }
}
