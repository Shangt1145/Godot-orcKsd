using Orc.Core;
using Orc.Game.Targeting;

namespace Kards.Ui.OrcBridge;

/// <summary>Consumes native selector sessions in order. Only the engine supplies candidates.</summary>
public sealed class OrcTargeterBridge(Action<ISelectorInstance>? present = null) : ITargeterBridge
{
    public void BeginTargeting(ITargeterSession session) => _ = ConsumeAsync(session);

    private async Task ConsumeAsync(ITargeterSession session)
    {
        while (await session.NextAsync() is { } selector)
        {
            try
            {
                if (present is null) AutoRespond(selector);
                else present(selector);
            }
            catch { selector.Submit(new CancelEvent()); }
        }
    }

    public static void AutoRespond(ISelectorInstance selector)
    {
        var data = selector.Presentation;
        if (selector.SelectorName == SelectorNames.Mulligan && data.Min == 0)
            selector.Submit(new PickEvent());
        else if (data.Identifiers is { Count: > 0 } ids && data.Min <= 1)
            selector.Submit(new PickEvent(identifiers: [ids[0]]));
        else if (data.Candidates.FirstOrDefault(r => r.IsAlive) is { } first && data.Min <= 1)
            selector.Submit(selector.Mode == SelectorInteractionMode.Drag ? new DropEvent(first) : new PickEvent([first]));
        else selector.Submit(new CancelEvent());
    }

    public static IReadOnlyList<Ref<Entity>> SelectReplace(IEnumerable<Ref<Entity>> allowed, IEnumerable<string> keepUids)
    {
        var keep = keepUids.ToHashSet(StringComparer.Ordinal);
        return allowed.Where(reference => !keep.Contains(OrcRefs.KeyOf(reference.Value))).ToArray();
    }
}

/// <summary>Labels attached by reviewed source effects; never an engine rule.</summary>
internal sealed record OrderSelectorTag(string SlotName, IReadOnlyDictionary<string, string>? Labels = null);
