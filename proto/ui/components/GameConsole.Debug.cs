using System.Text.Json;
using Godot;
using Kards.Ui.OrcBridge;
using Kards.Ui.Core;

namespace Kards.Ui;

public partial class GameConsole
{
    private Label _debugStatus=null!,_debugResult=null!;
    private RichTextLabel _cardDetails=null!;
    private OptionButton _debugSide=null!,_debugTargets=null!,_debugDefinitions=null!;
    private SpinBox _debugAmount=null!;
    private LineEdit _definitionSearch=null!;
    private Button _enableDebug=null!,_autoOpponent=null!,_cancelChoice=null!;
    private readonly List<Button> _mutationButtons=[];
    private readonly Dictionary<Button,MatchDebugAction> _buttonActions=[];
    private readonly List<string> _targetIds=[],_definitionIds=[];
    private string _targetsKey="",_definitionsKey="";
    private bool _debugBusy;
    private MatchDebugSnapshot? _snapshot;

    private static Button AddButton(Node parent,string text,Action clicked)
    {
        var button=new Button{Text=text,CustomMinimumSize=new(0,MobileSim.Enabled?56:36),FocusMode=Control.FocusModeEnum.None};
        button.Pressed+=clicked;parent.AddChild(button);return button;
    }
    private void BuildDebug(TabContainer tabs)
    {
        var scroll=new ScrollContainer{Name="对局调试",HorizontalScrollMode=ScrollContainer.ScrollMode.Disabled};tabs.AddChild(scroll);
        var body=new VBoxContainer{SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};body.AddThemeConstantOverride("separation",8);scroll.AddChild(body);
        _debugStatus=new Label{AutowrapMode=TextServer.AutowrapMode.WordSmart};body.AddChild(_debugStatus);
        var session=new HFlowContainer();body.AddChild(session);
        _enableDebug=AddButton(session,"启用本地调试",()=>_ = SendDebug(_snapshot?.Enabled==true?MatchDebugAction.Disable:MatchDebugAction.Enable));
        _autoOpponent=AddButton(session,"暂停对手自动行动",()=>_ = SendDebug(MatchDebugAction.AutoOpponent,_snapshot?.AutoOpponent==true?0:1));
        Mutation(session,"推进当前回合",MatchDebugAction.EndTurn);
        _cancelChoice=AddButton(session,"取消当前选靶",()=>_ = SendDebug(MatchDebugAction.CancelChoice));
        var values=new HFlowContainer();body.AddChild(values);
        values.AddChild(new Label{Text="作用玩家"});_debugSide=new OptionButton();_debugSide.AddItem("我方");_debugSide.AddItem("敌方");values.AddChild(_debugSide);
        values.AddChild(new Label{Text="数值"});_debugAmount=new SpinBox{MinValue=0,MaxValue=100,Step=1,Value=1,CustomMinimumSize=new(100,36)};values.AddChild(_debugAmount);
        Mutation(values,"抽牌",MatchDebugAction.Draw);Mutation(values,"增加可用资源",MatchDebugAction.GainKredits);Mutation(values,"增加资源上限",MatchDebugAction.GainSlots);
        body.AddChild(new HSeparator());
        var targetRow=new HBoxContainer();body.AddChild(targetRow);targetRow.AddChild(new Label{Text="检查卡牌"});
        _debugTargets=new OptionButton{SizeFlagsHorizontal=Control.SizeFlags.ExpandFill,ClipText=true};targetRow.AddChild(_debugTargets);
        _debugTargets.ItemSelected+=_=>RefreshDebug();
        _cardDetails=new RichTextLabel{BbcodeEnabled=false,SelectionEnabled=true,CustomMinimumSize=new(0,100),FitContent=true};body.AddChild(_cardDetails);
        var cardActions=new HFlowContainer();body.AddChild(cardActions);
        Mutation(cardActions,"造成伤害",MatchDebugAction.Damage);Mutation(cardActions,"回复",MatchDebugAction.Heal);
        Mutation(cardActions,"设置攻击",MatchDebugAction.SetAttack);Mutation(cardActions,"设置防御",MatchDebugAction.SetDefense);
        Mutation(cardActions,"沉默",MatchDebugAction.Silence);Mutation(cardActions,"消灭",MatchDebugAction.Destroy);
        body.AddChild(new HSeparator());
        var createRow=new HFlowContainer();body.AddChild(createRow);
        _definitionSearch=new LineEdit{PlaceholderText="搜索本局卡池名称或 ID",CustomMinimumSize=new(220,36),ClearButtonEnabled=true};createRow.AddChild(_definitionSearch);
        _definitionSearch.TextChanged+=_=>{_definitionsKey="";RefreshDebug();};
        _debugDefinitions=new OptionButton{CustomMinimumSize=new(300,36),ClipText=true};createRow.AddChild(_debugDefinitions);
        Mutation(createRow,"加入手牌",MatchDebugAction.CreateHand);Mutation(createRow,"召唤到支援线",MatchDebugAction.Summon);
        _debugResult=new Label{Text="选择卡牌查看实时属性；启用本地调试后可执行操作。",AutowrapMode=TextServer.AutowrapMode.WordSmart};body.AddChild(_debugResult);
    }
    private void Mutation(Node row,string label,MatchDebugAction action)
    {var button=AddButton(row,label,()=>_ = SendDebug(action));_mutationButtons.Add(button);_buttonActions[button]=action;}
    private void RefreshDebug()
    {
        _snapshot=ReadDebugState?.Invoke();var view=_snapshot?.View??ReadProjection?.Invoke();
        var writable=_snapshot is not null;var enabled=_snapshot?.Enabled==true;var busy=_debugBusy||_snapshot?.Busy==true;
        _debugStatus.Text=view is null?"尚未启动真实对局。进入本地对局后可调试。"
            :$"{(writable?(enabled?"本地调试已启用":"本地对局") :"联机 / 回放 · 只读诊断")}　回合 {view.Turn}　阶段 {view.Phase}　行动方：{(view.ActivePlayerSide=="self"?"我方":"敌方")}\n"+
             $"我方：总部 {view.SelfHq?.Health}　资源 {view.SelfKredits}/{view.SelfMaxKredits}　手牌 {view.SelfHandCount}　牌库 {view.SelfDeckCount}　|　敌方：总部 {view.EnemyHq?.Health}　资源 {view.EnemyKredits}/{view.EnemyMaxKredits}　手牌 {view.EnemyHandCount}　牌库 {view.EnemyDeckCount}\n"+
             $"当前选靶：{_snapshot?.PendingSelector??"无"}　最近拒绝原因：{_snapshot?.LastRejection??"无"}";
        _enableDebug.Text=enabled?"退出本地调试":"启用本地调试";_enableDebug.Disabled=!writable||busy;
        _autoOpponent.Text=_snapshot?.AutoOpponent==true?"暂停对手自动行动":"恢复对手自动行动";_autoOpponent.Disabled=!enabled||busy;
        _cancelChoice.Disabled=!enabled||_snapshot?.PendingSelector is null;
        foreach(var button in _mutationButtons)button.Disabled=!enabled||busy||view?.Phase!="play";
        var cards=VisibleCards(view);var key=(view?.MatchId??"")+string.Join('|',cards.Select(c=>c.Uid+":"+c.Zone));
        if(key!=_targetsKey)
        {
            var selected=Selected(_debugTargets,_targetIds);_targetsKey=key;_debugTargets.Clear();_targetIds.Clear();
            foreach(var card in cards){_targetIds.Add(card.Uid);_debugTargets.AddItem($"{(card.OwnerSide=="self"?"我方":"敌方")} / {ZoneLabel(card.Zone)} / {card.Definition.Name}");}
            var index=selected is null?-1:_targetIds.IndexOf(selected);if(index>=0)_debugTargets.Select(index);
        }
        ShowDebugCard();
        var target=cards.FirstOrDefault(c=>c.Uid==Selected(_debugTargets,_targetIds));
        foreach(var (button,action) in _buttonActions)
        {
            if(action is MatchDebugAction.Destroy or MatchDebugAction.Silence)
                button.Disabled|=target is null||target.IsHq||target.Zone is not ("support" or "frontline");
            if(action is MatchDebugAction.Damage or MatchDebugAction.Heal)
                button.Disabled|=target is null||!target.IsHq&&target.Zone=="hand";
            if(action==MatchDebugAction.SetAttack)button.Disabled|=target?.Definition.CardType!="unit";
            if(action==MatchDebugAction.SetDefense)button.Disabled|=target is null||!target.IsHq&&target.Definition.CardType!="unit";
            if(action==MatchDebugAction.Draw)button.Disabled|=_debugAmount.Value>10;
        }
        var definitions=_snapshot?.Definitions??[];var search=_definitionSearch.Text;
        var definitionKey=(view?.MatchId??"")+":"+search+":"+definitions.Count;
        if(definitionKey!=_definitionsKey)
        {
            var selected=Selected(_debugDefinitions,_definitionIds);_definitionsKey=definitionKey;_debugDefinitions.Clear();_definitionIds.Clear();
            foreach(var definition in definitions.Where(d=>search.Length==0||d.Name.Contains(search,StringComparison.OrdinalIgnoreCase)||d.Id.Contains(search,StringComparison.OrdinalIgnoreCase)))
            {_definitionIds.Add(definition.Id);_debugDefinitions.AddItem(definition.Name+" / "+definition.Id);}
            var index=selected is null?-1:_definitionIds.IndexOf(selected);if(index>=0)_debugDefinitions.Select(index);
        }
    }
    private static UiCardView[] VisibleCards(UiMatchView? view)=>view is null?[]:view.SelfHand.Concat(view.SelfLine).Concat(view.EnemyLine)
        .Concat(new[]{view.SelfHq,view.EnemyHq}.OfType<UiCardView>()).Where(c=>c.Visibility==Visibility.Full).DistinctBy(c=>c.Uid).ToArray();
    private static string? Selected(OptionButton picker,List<string> ids)=>picker.Selected>=0&&picker.Selected<ids.Count?ids[picker.Selected]:null;
    private void ShowDebugCard()
    {
        var uid=Selected(_debugTargets,_targetIds);var card=VisibleCards(_snapshot?.View??ReadProjection?.Invoke()).FirstOrDefault(c=>c.Uid==uid);
        _cardDetails.Text=card is null?"当前没有可检查的卡牌。":$"{card.Definition.Name}　ID：{card.CardId}\n实例：{card.Uid}\n"+
            $"攻击 {card.EffectiveAttack} / 印刷 {card.Definition.BaseAttack}　防御 {card.EffectiveDefense??card.Health} / 印刷 {card.Definition.BaseDefense}　部署费 {card.EffectiveCost}　行动费 {card.EffectiveOpCost}\n"+
            $"当前词条：{string.Join("、",card.Keywords)}　沉默：{Yes(card.IsSilenced)}　压制：{Yes(card.IsSuppressed)}　老兵：{Yes(card.IsVeteran)}\n"+
            $"行动限制：{string.Join("；",card.BlockedReasons)}\n{card.Definition.Text}";
        if(card is not null&&_snapshot?.Cards.FirstOrDefault(c=>c.Uid==card.Uid) is { } runtime)
            _cardDetails.Text+="\n原生效果："+(runtime.Effects.Count==0?"无":string.Join("\n",runtime.Effects))+"\n属性修饰："+(runtime.Modifiers.Count==0?"无":string.Join("\n",runtime.Modifiers));
    }
    private static string Yes(bool value)=>value?"是":"否";
    private static string ZoneLabel(string value)=>value switch{"hand"=>"手牌","support"=>"支援线","frontline"=>"前线","hq"=>"总部",_=>value};
    private async Task SendDebug(MatchDebugAction action,int? amount=null)
    {
        var cancelling=action==MatchDebugAction.CancelChoice;
        if(_debugBusy&&!cancelling)return;
        if(!cancelling)_debugBusy=true;RefreshDebug();
        try
        {
            var command=new MatchDebugCommand(action,amount??(int)_debugAmount.Value,_debugSide.Selected==1?"enemy":"self",Selected(_debugTargets,_targetIds),Selected(_debugDefinitions,_definitionIds));
            var result=ExecuteDebug is { } execute?await execute(command):new MatchDebugResult(false,"当前数据源只支持查看");
            if(!IsInsideTree())return;_debugResult.Text=(result.Success?"✓ ":"未执行：")+result.Message;
        }
        catch(Exception error){_debugResult.Text="执行失败："+error.Message;GameLog.Exception("debug",error);}
        finally{if(!cancelling)_debugBusy=false;if(IsInsideTree())RefreshDebug();}
    }
    private void CopySnapshot()
    {
        DisplayServer.ClipboardSet(JsonSerializer.Serialize(new{state=DescribeState?.Invoke(),debug=ReadDebugState?.Invoke(),view=ReadProjection?.Invoke(),logs=GameLog.Snapshot().TakeLast(100)},new JsonSerializerOptions{WriteIndented=true}));
        _debugResult.Text="已复制状态与最近 100 条日志。";
    }
}
