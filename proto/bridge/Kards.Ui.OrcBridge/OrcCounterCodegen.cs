using System.Text;
using System.Text.Json;
using Kards.Ui.Contracts;
using Orc.Game.Cards;
using Orc.Game.EffectParsing.Dsl;

namespace Kards.Ui.OrcBridge;

/// <summary>Generated, typed EngineVariables access; all game changes use native EffectRuntime.</summary>
public static class OrcCounterCodegen
{
    public const string Notice="对局变量只在本局内存里累计，不随读档或回放恢复；同名计数器在本局双方所有卡牌之间共享。";
    public static string Normalize(string? name)
    {
        var value=(name??"").Trim().Normalize(NormalizationForm.FormC);
        if(value.Length is <1 or >40 || value.Any(c=>!char.IsLetterOrDigit(c)&&c is not (' ' or '_' or '-')))
            throw new InvalidDataException("计数器名称须为 1–40 个中文、字母、数字、空格、下划线或连字符");
        return value;
    }
    public static string Key(string? name)=>"diy.counter."+Normalize(name);
    public static IReadOnlyList<string> CountersIn(IEnumerable<OrcEffectDraft> effects)=>effects.SelectMany(e=>e.Ops)
        .Where(o=>o.Op.StartsWith("counter.",StringComparison.Ordinal)||o.AmountFromCounter).Select(o=>(o.Counter??"").Trim())
        .Where(s=>s.Length>0).Distinct(StringComparer.Ordinal).ToArray();
    public static OrcEffectDraft[] Rename(IEnumerable<OrcEffectDraft> effects,string from,string to)
    {
        var target=Normalize(to);
        return effects.Select(e=>e.Snapshot() with{Ops=e.Ops.Select(o=>(o.Op.StartsWith("counter.",StringComparison.Ordinal)||o.AmountFromCounter)
            &&(o.Counter??"").Trim()==from ? o.Snapshot() with{Counter=target}:o.Snapshot()).ToArray()}).ToArray();
    }
    internal static DslOp Generate(OrcOpDraft op,DslCondition? condition)
    {
        var key=JsonSerializer.Serialize(Key(op.Counter));
        var read=$"var counterVariables=ctx.Engine!.Variables; counterVariables.TryGet<int>({key},out var counterValue); counterValue=Math.Max(0,counterValue);";
        string code;
        if(op.Op.StartsWith("counter.",StringComparison.Ordinal))
        {
            var amount=op.Amount??1;if(amount is <0 or >1_000_000)throw new InvalidDataException("计数器改变量须为 0–1000000");
            var value=op.Op switch
            {
                "counter.add"=>$"(int)Math.Min(int.MaxValue,(long)counterValue+{amount})",
                "counter.sub"=>$"Math.Max(0,counterValue-{amount})",
                "counter.set"=>amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _=>throw new InvalidDataException("未知计数器动作："+op.Op)
            };
            code=read+$" counterVariables.Set({key},{value});";
        }
        else
        {
            if(!op.AmountFromCounter||op.Op is not ("damage" or "buff"))throw new InvalidDataException("仅伤害和属性增减支持计数器数值");
            var selector=op.Sel??"self";
            if(!OrcEffectEditing.Sels.Any(s=>s.Id==selector))throw new InvalidDataException("计数器选靶方式无效");
            if(selector is "one" or "random" && (op.Filter?.Faction is {Length:>0}||op.Filter?.ExcludeSelf==true))
                throw new InvalidDataException("计数器的单个／随机选靶暂不支持阵营过滤与排除自身；原始参数已保留");
            string Q(string? value)=>JsonSerializer.Serialize(string.IsNullOrEmpty(value)?null:value);
            var count=selector=="random"?(op.SelCount??1).ToString(System.Globalization.CultureInfo.InvariantCulture):"null";
            var pick=$"new Orc.Game.Effects.EffectSelector({Q(selector)},{Q(op.Side=="both"&&selector=="one"?"any":op.Side)},{Q(op.SelZone)},{Q(op.Filter?.UnitType)},{Q(op.Filter?.Keyword)},{count})";
            var filter=op.Filter?.ExcludeSelf==true?"if(object.ReferenceEquals(counterTarget,self))continue;":"";
            if(op.Filter?.Faction is {Length:>0} faction)
            {
                if(!Faction.TryNormalize(faction,out var normalized))throw new InvalidDataException("计数器过滤阵营未注册");
                filter+=$"if(counterTarget is not Orc.Game.Cards.CardBase counterBase || counterBase.Definition.Faction!={Q(normalized)})continue;";
            }
            var duration=op.Until switch {null or ""=>"Permanent","turnEnd"=>"TurnEnd","nextOwnerTurnStart"=>"NextOwnerTurnStart",_=>throw new InvalidDataException("计数器增益期限无效")};
            var apply=op.Op=="damage"?"await counterRuntime.DamageAsync(counterTarget,counterValue,self,ct);"
                :$"await counterRuntime.BuffAsync(counterTarget,(int)Math.Clamp((long)counterValue*{op.Attack??1},int.MinValue,int.MaxValue),(int)Math.Clamp((long)counterValue*{op.Defense??1},int.MinValue,int.MaxValue),Orc.Game.Effects.EffectDuration.{duration},ct);";
            code=read+$" var counterRuntime=self is null?null:Orc.Game.Effects.EffectRuntime.ResolveFor(self); if(counterRuntime is not null) {{ foreach(var counterTarget in await counterRuntime.SelectAsync(self!,{pick},ct)) {{ {filter} {apply} }} }}";
        }
        return new DslOp("csx",script:code,condition:condition);
    }
}
