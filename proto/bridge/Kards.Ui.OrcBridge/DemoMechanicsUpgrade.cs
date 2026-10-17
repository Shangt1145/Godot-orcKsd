using Kards.Ui.Core;
using Orc.Game.Cards;

namespace Kards.Ui.OrcBridge;

/// <summary>Upgrade only the two exact, previously delivered demo definitions; preserve user edits.</summary>
public static class DemoMechanicsUpgrade
{
    public static KeywordProgram Retreat() => new() { Rules = [new() { Rule = "move.retreat", Value = "1" }] };
    public static KeywordProgram Mobility() => new()
    {
        Rules = [new() { Rule = "attack.limit", Value = "2" }],
        Handlers = [new() { Event = "unit.attack.after", Subject = "self", When = [new("attacks", "gte", "2")],
            Actions = [new() { Op = "keyword.remove", Target = "self", Key = "" }] }]
    };

    public static bool Apply(DiyWorkshopLibrary library, string path)
    {
        if (library.LoadError is not null) return false;
        var pack = library.Packs.SingleOrDefault(p => p.Id == "pack/diy-demo-mechanics");
        if (pack is null) return false;
        var changed = false;
        for (var i = 0; i < pack.Keywords.Length; i++)
        {
            var keyword = pack.Keywords[i];
            if (keyword.ProgramJson is null) continue;
            KeywordProgram program;
            try { program = KeywordProgram.Parse(keyword.ProgramJson); }
            catch (InvalidDataException) { continue; }
            var retreat = keyword.Id == "kw-demo-kuijun";
            var mobility = keyword.Id == "kw-demo-jidong";
            if (!retreat && !mobility) continue;
            var legacy = new KeywordProgram { Skills = [new() { Id = retreat ? "fallback" : "extra",
                Name = retreat ? "撤退" : "额外行动", Cost = "0", SpendMove = false, SpendAttack = false,
                Actions = retreat ? [new() { Op = "retreat", Target = "self" }] :
                    [new() { Op = "move.set", Value = "1" }, new() { Op = "attack.set", Value = "1" },
                     new() { Op = "keyword.remove", Target = "self", Key = "" }] }] };
            if (program.ToJson() != legacy.ToJson()) continue;
            pack.Keywords[i] = keyword with { ProgramJson = (retreat ? Retreat() : Mobility()).ToJson(),
                EffectText = retreat ? "本单位可以消耗正常移动机会和行动花费，从前线移动回己方支援线。"
                : "本单位可以攻击两次，完成第二次攻击后失去机动。" };
            changed = true;
        }
        if (!changed) return false;
        File.Copy(path, path + ".before-drag-mechanics-" + Guid.NewGuid().ToString("N") + ".bak");
        library.Save(pack);
        return true;
    }
}
