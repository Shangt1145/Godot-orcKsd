using Orc.Core;
using Orc.Game.Targeting;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// Minimal engine-side targeter bridge. The engine inverts control when it needs a player choice, and a
/// missing bridge surfaces as a failed targeting result rather than an exception, so every match needs one.
/// The interactive panel is supplied by the UI later; until then this answers with the conservative policy:
/// an empty mulligan selection (keeping the opening hand) and a cancel for anything else.
/// </summary>
public sealed class OrcTargeterBridge : ITargeterBridge
{
    private readonly Func<IReadOnlyList<object?>> _collect;
    private readonly Action<TargetingRequestDescription, ITargetingResponder>? _present;

    /// <param name="collect">Everything the UI can currently interact with, as engine references.</param>
    /// <param name="present">Interactive handler; when null the conservative auto policy is used.</param>
    public OrcTargeterBridge(Func<IReadOnlyList<object?>> collect,
        Action<TargetingRequestDescription, ITargetingResponder>? present = null)
    {
        _collect = collect;
        _present = present;
    }

    public Task<IReadOnlyList<object?>> CollectCandidatesAsync(TargetingCollectionContext context)
        => Task.FromResult(_collect());

    public void BeginInteraction(TargetingRequestDescription description, ITargetingResponder responder)
    {
        if (_present is not null)
        {
            _present(description, responder);
            return;
        }
        AutoRespond(description, responder);
    }

    private static void AutoRespond(TargetingRequestDescription description, ITargetingResponder responder)
    {
        var slot = description.Slots.Count > 0 ? description.Slots[0] : null;
        if (slot is { Kind: TargetSlotKind.MulliganSelect } && slot.Min == 0)
        {
            // Keeping the whole opening hand is always a legal answer: the slot allows an empty selection.
            responder.Complete(description.RequestId,
                new Dictionary<string, IReadOnlyList<TargetSelection>>(StringComparer.Ordinal)
                {
                    [slot.Name] = Array.Empty<TargetSelection>()
                });
            return;
        }
        responder.Cancel(description.RequestId);
    }
}
