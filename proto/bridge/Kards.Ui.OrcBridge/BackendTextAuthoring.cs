using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kards.Ui.Contracts;
using Orc.Cards;
using Orc.Game.EffectParsing;

namespace Kards.Ui.OrcBridge;

public sealed record BackendTextResult(bool Success,OrcEffectDraft[] Effects,string Text,IReadOnlyList<string> Problems);

/// <summary>Backend Parse/Compile with lossless DTO mapping and a round-trip checked text writer.</summary>
public static class BackendTextAuthoring
{
    private static readonly object AssetGate=new();
    private static bool _assetsReady;
    public static void EnsureAssets()
    {
        lock(AssetGate)
        {
            if(_assetsReady)return;
            // The native compiler resolves its own assets relative to AppContext.BaseDirectory.
            // Godot can load assemblies from bytes, so freeze the referenced engine's bundled assets there.
            var folder=Path.Combine(AppContext.BaseDirectory,"EffectParsing","Templates","ops");
            const string prefix="Kards.Ui.OrcBridge.DiyOps.";
            var assembly=typeof(BackendTextAuthoring).Assembly;
            var names=assembly.GetManifestResourceNames().Where(n=>n.StartsWith(prefix,StringComparison.Ordinal)).ToArray();
            if(names.Length==0)throw new InvalidDataException("缺少原生文本编译资产，请重新构建。");
            Directory.CreateDirectory(folder);
            foreach(var name in names)
            {
                using var reader=new StreamReader(assembly.GetManifestResourceStream(name)!);var text=reader.ReadToEnd();var file=Path.Combine(folder,name[prefix.Length..]);
                if(File.Exists(file))
                {if(File.ReadAllText(file)!=text)throw new InvalidDataException("原生编译资产版本不一致，未覆盖现有文件："+Path.GetFileName(file));}
                else File.WriteAllText(file,text);
            }
            _assetsReady=true;
        }
    }
    public static BackendTextResult Parse(string cardId,string text,string cardType)
    {
        try
        {
            if(cardType is not ("unit" or "order"))throw new InvalidDataException("该卡牌类型尚无已验证的文本触发适配；原文与效果保持不变。");
            var rows=CardTextEffectCompiler.Parse(text,cardType);
            if(rows is null)throw new InvalidDataException("后端未接受这段 KARDS 规则语法。请使用数字和语法示例，或直接组合效果；原有结构未改动。");
            // Detect the backend's known lexical instability by calling the backend twice, never by
            // guessing a target or silently rewriting the parsed program in the frontend.
            var normalized=Regex.Replace(text,@"(?<=\d)\s*(?=个)"," ");
            var spaced=CardTextEffectCompiler.Parse(normalized,cardType);
            if(spaced is not null&&!Equivalent(rows,spaced))throw new InvalidDataException("后端对数量与“个”之间的空格产生了不同选靶。请保留一个空格（例如“1 个敌方单位”）；本次未应用。");
            if(rows.Count==0)return new(true,[],"",[]);
            EnsureAssets();_=CardTextEffectCompiler.Compile(cardType,rows,cardId);
            var effect=new OrcEffectDraft{Template=cardType=="unit"?"deploy_basic":"played_basic",Hook=cardType=="unit"?"unit.deployed":"card.played",Guard=cardType=="unit"?"SelfUnit":"SelfCard",TextCardType=cardType,Ops=rows.Select(ToOperation).ToArray()};
            if(!TryWrite(cardType,[effect],out var canonical))throw new InvalidDataException("文本不能无损往返，未替换原效果。");
            return new(true,[effect],canonical,[]);
        }
        catch(Exception e) when(e is InvalidDataException or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or FormatException)
        {return new(false,[],text,[e.Message]);}
    }
    public static EffectSnapshot Compile(string id,OrcEffectDraft effect)
    {
        EnsureAssets();return CardTextEffectCompiler.Compile(effect.TextCardType??throw new InvalidDataException("缺少文本效果来源"),ToProgram(effect.TextCardType,effect),id);
    }
    public static IReadOnlyList<CardTextEffectDraft> ToProgram(string cardType,OrcEffectDraft effect)
    {
        if(effect.DesktopJson is not null||effect.PrefabJson is not null||effect.ExtraFields is {Count:>0})throw new InvalidDataException("混合或扩展声明不能隐式转换为基础文本效果。");
        if(effect.Template!=(cardType=="unit"?"deploy_basic":"played_basic")||effect.Hook!=(cardType=="unit"?"unit.deployed":"card.played")||effect.Guard!=(cardType=="unit"?"SelfUnit":"SelfCard"))
            throw new InvalidDataException("当前时机超出基础文本范围；请先解除文字联动，再编辑高级结构。");
        return effect.Ops.Select(op=>
        {
            if(op.Op is not ("draw" or "gainPoint" or "gainSlot" or "damage" or "buff" or "pin" or "silence" or "costMod" or "destroy")||op.Condition is {Length:>0}||op.ConditionRaw is {Length:>0}||op.Counter is {Length:>0}||op.AmountFromCounter||op.Name is {Length:>0}||op.Keyword is {Length:>0}||op.OpZone is {Length:>0}||op.Value is not null||op.ExtraFields is {Count:>0}||op.Filter is {} f&&(f.UnitType is {Length:>0}||f.Keyword is {Length:>0}||f.Faction is {Length:>0}||f.ExcludeSelf||f.ExtraFields is {Count:>0})||op.SelZone is {Length:>0}||op.SelCount is not null)
                throw new InvalidDataException("该动作包含基础文本无法表达的结构；原有参数保留，请解除文字联动后继续使用高级效果。");
            var target=op.Op is "draw" or "gainPoint" or "gainSlot"?"self":(op.Sel??"self",op.Side??"both") switch
            {("self",_)=>"self",("all","friendly")=>"friendlyUnits",("all","enemy")=>"enemyUnits",("all","both")=>"bothUnits",("one","friendly")=>"friendlyUnit",("one","enemy")=>"enemyUnit",("one","both" or "any")=>"anyUnit",_=>throw new InvalidDataException("选靶方式超出基础文本范围。")};
            if(op.Op is "draw" or "gainPoint" or "gainSlot"&&op.Sel is not (null or "self"))throw new InvalidDataException("资源效果的归属不是自己，不能转换为基础文本。");
            return new CardTextEffectDraft{Op=op.Op,Target=target,Amount=op.Op=="draw"?op.Count??0:op.Op=="pin"?1:op.Amount??0,Attack=op.Attack??0,Defense=op.Defense??0,Until=op.Until??""};
        }).ToArray();
    }
    private static OrcOpDraft ToOperation(CardTextEffectDraft row)
    {
        var resource=row.Op is "draw" or "gainPoint" or "gainSlot";
        var (sel,side)=row.Target switch{"self"=>("self",(string?)null),"friendlyUnits"=>("all","friendly"),"enemyUnits"=>("all","enemy"),"bothUnits"=>("all","both"),"friendlyUnit"=>("one","friendly"),"enemyUnit"=>("one","enemy"),_=>("one","both")};
        return new(){Op=row.Op,Sel=resource?null:sel,Side=resource?null:side,Count=row.Op=="draw"?row.Amount:null,Amount=row.Op is "damage" or "gainPoint" or "gainSlot" or "costMod"?row.Amount:null,Attack=row.Op=="buff"?row.Attack:null,Defense=row.Op=="buff"?row.Defense:null,Until=row.Until.Length>0?row.Until:null};
    }
    public static bool TryWrite(string cardType,IReadOnlyList<OrcEffectDraft> effects,out string text)
    {
        text="";
        try
        {
            if(effects.Count==0)return cardType=="unit";
            var rows=effects.SelectMany(e=>ToProgram(cardType,e)).ToArray();
            if(rows.Any(r=>r.Until.Length>0))return false;
            string Number(int n)=>n.ToString(CultureInfo.InvariantCulture);
            string Signed(int n)=>(n>=0?"+":"")+Number(n);
            string Target(string id)=>DiyCardCompiler.Targets.FirstOrDefault(t=>t.Id==id&&id!="self")?.Label??throw new InvalidDataException("后端文本语法不能表达本单位选靶。");
            var sentences=rows.Select(r=>r.Op switch
            {
                "draw"=>$"抽{Number(r.Amount)}张牌","gainPoint"=>$"获得{Number(r.Amount)}个指挥点","gainSlot"=>$"获得{Number(r.Amount)}个指挥点槽",
                "damage"=>$"对{Target(r.Target)}造成{Number(r.Amount)}点伤害","destroy"=>$"消灭{Target(r.Target)}","pin"=>$"压制{Target(r.Target)}","silence"=>$"抑制{Target(r.Target)}",
                "buff"=>$"使{Target(r.Target)}获得{Signed(r.Attack)}/{Signed(r.Defense)}","costMod"=>$"使{Target(r.Target)}获得{Signed(r.Amount)}行动花费",_=>throw new InvalidDataException("未知文本动作")
            });
            var candidate=(cardType=="unit"?"部署：":"")+string.Join("；",sentences)+"。";
            var reparsed=CardTextEffectCompiler.Parse(candidate,cardType);
            if(reparsed is null||!Equivalent(rows,reparsed))return false;
            text=candidate;return true;
        }
        catch(Exception e) when(e is InvalidDataException or ArgumentException or InvalidOperationException or JsonException){return false;}
    }
    private static bool Equivalent(IReadOnlyList<CardTextEffectDraft> left,IReadOnlyList<CardTextEffectDraft> right)=>left.Count==right.Count&&left.SequenceEqual(right);
}
