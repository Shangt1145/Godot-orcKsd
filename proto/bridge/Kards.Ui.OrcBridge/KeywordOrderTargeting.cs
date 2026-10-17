using Kards.Ui.Contracts;
using Orc.Game.Cards;
using Orc.Game.Players;

namespace Kards.Ui.OrcBridge;

internal static class KeywordOrderTargeting
{
    public static IEnumerable<UiOrderTargeting> Read(Player viewer)
    {
        foreach (var card in viewer.Hand.OfType<CommandCard>())
            foreach (var component in KeywordProgramRuntime.Components(card))
            {
                var input = component.ReadFirstPlayInput();
                if (!input.Required) continue;
                if (input.Targets is { } targets)
                    yield return new(OrcRefs.KeyOf(card), targets.Select(OrcRefs.KeyOf).ToArray());
                break;
            }
    }
}
