using Kards.Ui.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Effects;
using Orc.UI.Contracts;
using Orc.Cards;
using Orc.Game.Players;

namespace Kards.Ui.OrcBridge;

public enum MatchDebugAction { Enable, Disable, AutoOpponent, Draw, GainKredits, GainSlots, CreateHand, Summon, Damage, Heal, SetAttack, SetDefense, Silence, Destroy, EndTurn, CancelChoice }
public sealed record MatchDebugCommand(MatchDebugAction Action, int Amount = 1, string Side = "self", string? TargetUid = null, string? CardId = null);
public sealed record MatchDebugResult(bool Success, string Message);
public sealed record DebugCardDefinition(string Id, string Name, bool Unit);
public sealed record DebugCardRuntime(string Uid, IReadOnlyList<string> Effects, IReadOnlyList<string> Modifiers);
public sealed record MatchDebugSnapshot(bool Enabled, bool AutoOpponent, bool Busy, string? PendingSelector, string? LastRejection,
    UiMatchView View, IReadOnlyList<DebugCardDefinition> Definitions, IReadOnlyList<DebugCardRuntime> Cards);

public sealed partial class OrcMatchSession
{
    private bool _debugEnabled;
    private bool _debugOriginalOpponent;
    public MatchDebugSnapshot ReadDebugSnapshot() => new(_debugEnabled, DriveOpponent, IsSubmitting || MulliganPending || TargetPending,
        PendingSelectorName, LastRejectionReason, View,
        Match.CardService.RegisteredDefinitions.Select(p=>new DebugCardDefinition(p.Key,p.Value.Name,
            p.Value.UnitTypes.Count>0)).OrderBy(p=>p.Name,StringComparer.Ordinal).ToArray(),
        _host.Index.Select(p=>new DebugCardRuntime(p.Key,
            p.Value.Effects.Select(e=>e.Name+" · "+e.DisposalState+(e is DynamicPassiveEffect native?" · "+string.Join(",",native.Snapshot.Root.MainTrigger.Hooks):"")).ToArray(),
            (p.Value is CardBase c?c.Modifiers.All:p.Value is Hq h?h.Modifiers.All:[]).Select(m=>
                m.Field+" "+(m is AddModifier add?"增量 "+add.Delta:m is SetModifier set?"设为 "+set.Value:m.GetType().Name)+" · "+(m.Expiry?.Phase??"永久")).ToArray())).ToArray());

    /// <summary>Local-only debug boundary. Mutations share the normal command lock and native services.</summary>
    public async Task<MatchDebugResult> DebugAsync(MatchDebugCommand command, CancellationToken ct = default)
    {
        if(_disposed)return new(false,"对局已关闭");
        if(command.Action==MatchDebugAction.CancelChoice)
        {
            if(!_debugEnabled||!TargetPending)return new(false,"没有可取消的选靶");
            CancelTargetChoice();return Report("已取消选靶");
        }
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,_lifetime.Token);ct=linked.Token;
        if(!await _commands.WaitAsync(0,ct))return new(false,"上一操作尚未完成，请先完成或取消选靶");
        try
        {
            if(command.Action==MatchDebugAction.Enable)
            {
                if(MulliganPending||TargetPending)return new(false,"请先完成当前选择");
                if(!_debugEnabled){_debugOriginalOpponent=DriveOpponent;DriveOpponent=false;_debugEnabled=true;}
                return Report("已启用本地调试，对手自动行动已暂停");
            }
            if(!_debugEnabled)return new(false,"请先启用本地调试");
            if(command.Action==MatchDebugAction.Disable)
            { _debugEnabled=false;DriveOpponent=_debugOriginalOpponent;return Report("已退出本地调试"); }
            if(Match.Phase!=MatchPhase.Play||Match.State!=MatchState.InProgress||MulliganPending||TargetPending)
                return new(false,"当前阶段不接受调试修改");
            if(command.Side is not ("self" or "enemy"))return new(false,"无效的玩家");
            if(command.Amount is < 0 or > 100)return new(false,"数值范围为 0 到 100");
            var player=command.Side=="enemy"?Match.Players.Single(p=>p!=_host.Viewer):_host.Viewer;
            var runtime=Match.EffectRuntime;
            var target=command.TargetUid is { } uid?_host.Index.GetValueOrDefault(uid):null;
            if(command.Action is MatchDebugAction.Damage or MatchDebugAction.Heal or MatchDebugAction.SetAttack or MatchDebugAction.SetDefense or MatchDebugAction.Silence or MatchDebugAction.Destroy)
            {
                if(target is null)return new(false,"目标已离场，请重新选择");
                if(command.Action is MatchDebugAction.Destroy or MatchDebugAction.Silence && (target is not UnitCard unit||!unit.TryGetData<UnitStateData>(out var state)||state.Line is null))
                    return new(false,"此操作需要场上单位");
                if(command.Action==MatchDebugAction.SetAttack&&target is not UnitCard)return new(false,"此目标没有攻击力");
            }
            // Debug effects can also request native choices. Keep the same UI channel as normal play.
            _interactiveCommand=true;_activeCommandToken=ct;_choiceSourceUid=command.TargetUid;
            switch(command.Action)
            {
                case MatchDebugAction.AutoOpponent:DriveOpponent=command.Amount!=0;break;
                case MatchDebugAction.Draw:
                    if(command.Amount>10)return new(false,"单次最多抽 10 张");
                    await runtime.DrawAsync(player.Hq,command.Amount,ct);break;
                case MatchDebugAction.GainKredits:await runtime.GainPointsAsync(player.Hq,command.Amount,ct);break;
                case MatchDebugAction.GainSlots:await runtime.GainPointSlotsAsync(player.Hq,command.Amount,ct);break;
                case MatchDebugAction.CreateHand:case MatchDebugAction.Summon:
                    if(command.CardId is null||!Match.CardService.RegisteredDefinitions.ContainsKey(command.CardId))return new(false,"卡牌不在本局已加载卡池内");
                    if(command.Action==MatchDebugAction.Summon&&!ReadDebugSnapshot().Definitions.Any(d=>d.Id==command.CardId&&d.Unit))return new(false,"只能召唤单位");
                    await AuthoringRuntime.CreateAsync(player.Hq,command.CardId,"self",command.Action==MatchDebugAction.Summon?"support":"hand",1,ct);break;
                case MatchDebugAction.Damage:await runtime.DamageAsync(target!,command.Amount,ct:ct);break;
                case MatchDebugAction.Heal:await AuthoringRuntime.HealAsync(target!,command.Amount,ct);break;
                case MatchDebugAction.SetAttack:case MatchDebugAction.SetDefense:
                    await AuthoringRuntime.AttributeAsync(target!,command.Action==MatchDebugAction.SetAttack?"attack":"defense","set",command.Amount,"permanent",ct);break;
                case MatchDebugAction.Silence:await runtime.SilenceAsync(target!,ct);break;
                case MatchDebugAction.Destroy:await runtime.KillAsync(target!,ct:ct);break;
                case MatchDebugAction.EndTurn:await Match.EndTurn(ct);break;
                default:return new(false,"未知调试操作");
            }
            _host.PollEvents();_host.SettleProjection();
            return Report($"调试操作完成：{command.Action}，玩家={command.Side}，目标={command.TargetUid??command.CardId??"—"}，数值={command.Amount}");
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){return new(false,"调试操作已取消");}
        catch(Exception error){GameLog.Exception("debug",error);return new(false,error.Message);}
        finally
        {
            _interactiveCommand=false;_activeCommandToken=default;_choiceSourceUid=null;
            _host.SettleProjection();_commands.Release();
        }
    }
    private static MatchDebugResult Report(string message){GameLog.Info("debug",message);return new(true,message);}
}
