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
        var top=new HBoxContainer();AddChild(top);_nativeToolbar=top;top.AddChild(MenuStyles.Label("DIY 工坊",32,MenuStyles.Ink));top.AddChild(new Control{SizeFlagsHorizontal=SizeFlags.ExpandFill});
        top.AddChild(MenuStyles.Button("工坊首页",()=>_editor.GuardNavigation(ShowHome)));
        _notice=MenuStyles.Label("",13,MenuStyles.Dim);_notice.AutowrapMode=TextServer.AutowrapMode.WordSmart;AddChild(_notice);
        _navigation=new VBoxContainer{SizeFlagsVertical=SizeFlags.ExpandFill};AddChild(_navigation);
        _workspace=new VBoxContainer{SizeFlagsVertical=SizeFlags.ExpandFill};AddChild(_workspace);AddChild(_editor);ShowHome();
        VisibilityChanged+=()=>{if(!Visible&&_animating)ShowHome();};
    }
    internal void OpenPack(DiyPackDraft pack)
    {
        if (IsNativePackage(pack.Id)
            && !_cards.Cards.Any(c => c.PackId == pack.Id && c.SourceCardId.Length > 0)) { _editor.GuardNavigation(() => OpenNativePack(pack.Id[5..])); return; }
        OpenCompatiblePack(pack);
    }
    private void BuildKeywords()
    {
        MenuStyles.Clear(_keywordPane);var pack=_store.Packs.Single(p=>p.Id==_pack!.Id);DiyKeywordDraft draft=new();
        var row=new HBoxContainer{SizeFlagsVertical=SizeFlags.ExpandFill};_keywordPane.AddChild(row);
        var sidebar=new VBoxContainer{CustomMinimumSize=new(220,0)};row.AddChild(sidebar);
        var list=new ItemList{SizeFlagsVertical=SizeFlags.ExpandFill,CustomMinimumSize=new(210,120)};sidebar.AddChild(list);
        foreach(var k in pack.Keywords)list.AddItem(k.Name);
        var form=new VBoxContainer{SizeFlagsHorizontal=SizeFlags.ExpandFill};row.AddChild(form);
        KeywordName=new LineEdit{Text=draft.Name,MaxLength=30};DiyEditorScreen.Field(form,"词条名称",KeywordName);
        var description=new TextEdit{CustomMinimumSize=new(0,65),WrapMode=TextEdit.LineWrappingMode.Boundary};DiyEditorScreen.Field(form,"给玩家看的说明",description);
        KeywordText=new TextEdit{CustomMinimumSize=new(0,110),PlaceholderText="例如：友方回合开始时，获得 +1+1。",WrapMode=TextEdit.LineWrappingMode.Boundary};DiyEditorScreen.Field(form,"实际效果",KeywordText);
        var battle=new CheckBox{Text="属于对战词条"};form.AddChild(battle);
        var status=MenuStyles.Label("填写效果或打开结构编辑器，然后保存词条。",13,MenuStyles.Dim);status.AutowrapMode=TextServer.AutowrapMode.WordSmart;form.AddChild(status);
        form.AddChild(MenuStyles.Button("编辑规则、计数与主动技能…",()=>
        {
            var editor=new KeywordProgramEditor{SizeFlagsVertical=SizeFlags.ExpandFill,Catalog=()=>_service.Cards};_keywordPane.AddChild(editor);row.Hide();
            editor.Applied+=json=>{draft=draft with{ProgramJson=json};status.Text="规则程序已应用，保存词条后生效。";};
            editor.Finished+=()=>{editor.QueueFree();row.Show();};editor.Open(draft.ProgramJson);
        }));
        form.AddChild(MenuStyles.Button("移除规则程序，使用文本 / 结构效果",()=>{draft=draft with{ProgramJson=null};status.Text="已移除规则程序，保存后使用下方效果。";}));
        void Load(DiyKeywordDraft k){draft=k.Snapshot();KeywordName.Text=k.Name;description.Text=k.Description;KeywordText.Text=k.EffectText;battle.ButtonPressed=k.Battle;status.Text=WorkshopAuthoring.Errors.GetValueOrDefault(k.Id)??"效果可在下方重新校验。";}
        sidebar.AddChild(MenuStyles.Button("新建词条",()=>Load(new())));list.ItemSelected+=i=>Load(pack.Keywords[(int)i]);
        var edit=MenuStyles.Button("打开效果结构编辑器…",()=>
        {
            var fx=new DiyEffectEditorScreen{SizeFlagsVertical=SizeFlags.ExpandFill};_keywordPane.AddChild(fx);row.Hide();fx.Initialize();
            fx.Applied+=(effects,_)=>draft=draft with{Effects=effects};
            fx.DraftEdited+=card=>draft=draft with{Effects=card.OrcEffects,EffectText=card.EffectText};
            fx.InspectorSaved+=card=>draft=draft with{Effects=card.OrcEffects,EffectText=card.EffectText};
            fx.Finished+=()=>{KeywordText.Text=draft.EffectText;fx.QueueFree();row.Show();status.Text="结构已更新，点击保存词条。";};
            fx.Open(new(){Id="diy/"+draft.Id,Name=KeywordName.Text,EffectText=KeywordText.Text,OrcEffects=draft.Effects});
        });form.AddChild(edit);
        KeywordText.TextChanged+=()=>{if(KeywordText.Text!=draft.EffectText)draft=draft with{Effects=[]};};
        SaveKeyword=MenuStyles.Button("校验并保存词条",()=>
        {
            var keyword=draft with{Name=KeywordName.Text.Trim(),Description=description.Text,EffectText=KeywordText.Text,Battle=battle.ButtonPressed};
            if(WorkshopAuthoring.ValidateKeyword(keyword) is { } error){status.Text="请修正效果："+error;return;}
            try{_store.Save(pack with{Keywords=pack.Keywords.Where(k=>k.Id!=keyword.Id).Append(keyword).ToArray()});_changed();_pack=_store.Packs.Single(p=>p.Id==pack.Id);BuildKeywords();_notice.Text="词条已保存，可在本卡包及其扩展包的卡牌上选择。";}
            catch(Exception e){status.Text=e.Message;}
        });form.AddChild(SaveKeyword);
    }
    public void RefreshData(){if(_compatiblePack is not null)return;if(_pack is not null)_pack=_store.Packs.FirstOrDefault(p=>p.Id==_pack.Id);if(_pack is not null&&_poolPane is not null&&_poolPane.Visible)RefreshPool();}
    private void RefreshPool()
    {
        if(_pack is null)return;MenuStyles.Clear(_poolPane);
        var saved=_cards.Cards;var family=DeckCardPacks.Family(_pack.Id);
        var available=_service.Cards.Where(c=>DeckCardPacks.Of(c.CardId)==family)
            .Where(c=>!_pack.IsExpansion||DeckCardPacks.RawOf(c.CardId)==_pack.Id||DeckCardPacks.RawOf(c.CardId)==_pack.BasePackId).ToArray();
        var drafts=saved.Where(c=>c.PackId==_pack.Id&&_service.CustomErrors.ContainsKey(c.Id)).ToArray();
        var all=available.Concat(drafts.Select(d=>DiyCardCompiler.Preview(d,_service.Cards)))
            .GroupBy(c=>c.CardId,StringComparer.Ordinal).Select(g=>g.Last()).ToArray();
        var bar=new HBoxContainer();_poolPane.AddChild(bar);bar.AddChild(MenuStyles.Label($"可游玩 {available.Count(c=>_service.PlayableIds.Contains(c.CardId))} 张 · 待完善 {drafts.Length} 张"+(_pack.IsExpansion?" · 包含基础卡包":" · 包含扩展内容"),16));
        var count=Math.Max(1,(all.Length+23)/24);_page=Math.Clamp(_page,0,count-1);
        bar.AddChild(MenuStyles.Button("上一页",()=>{_page=Math.Max(0,_page-1);RefreshPool();}));bar.AddChild(MenuStyles.Label($"{_page+1}/{count}",14));bar.AddChild(MenuStyles.Button("下一页",()=>{_page=Math.Min(count-1,_page+1);RefreshPool();}));
        var scroll=new ScrollContainer{SizeFlagsVertical=SizeFlags.ExpandFill};_poolPane.AddChild(scroll);var grid=new GridContainer{Columns=6};scroll.AddChild(grid);
        foreach(var card in all.Skip(_page*24).Take(24))
        {
            var box=new VBoxContainer();grid.AddChild(box);var face=new CardControl();face.Bind(card,_textures,145);box.AddChild(face);
            var isDraft=drafts.Any(d=>d.RuntimeId==card.CardId);box.AddChild(MenuStyles.Label(isDraft?"待完善 · "+card.Name:card.Name,12,isDraft?new Color("e6ad7b"):MenuStyles.Dim));
            var authored=saved.FirstOrDefault(c=>c.RuntimeId==card.CardId);
            if(authored?.PackId==_pack.Id)
            {
                face.Activated+=_=>_editor.GuardNavigation(()=>{_tabs.CurrentTab=_pack.IsExpansion?0:1;_editor.Load(authored.Snapshot());});
                face.TooltipText="点击编辑此卡牌";
            }
            else if(_pack.IsBuiltIn&&DeckCardPacks.RawOf(card.CardId)==_pack.Id)
            {
                face.Activated+=_=>_editor.GuardNavigation(()=>
                {
                    try { _tabs.CurrentTab=_pack.IsExpansion?0:1;_editor.Load(BuiltInWorkshopCatalog.Edit(card,_pack.Id,RuntimeGameData.Directory("nations"))); }
                    catch(Exception error){_notice.Text=error.Message;}
                });
                face.TooltipText="点击修改原版卡牌";
            }
            else box.AddChild(MenuStyles.Label("继承内容",11,MenuStyles.Dim));
        }
    }
}
