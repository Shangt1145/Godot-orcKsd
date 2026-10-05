using Orc.Cards;
using Orc.Core;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// Identity bridge. The engine identifies a card instance by <see cref="Entity.Id"/> (Guid);
/// the UI DTOs use a stable string uid. Engine references (<see cref="Ref{T}"/>) can die, so every
/// resolve goes through a liveness check instead of touching <c>.Value</c> directly.
/// </summary>
public static class OrcRefs
{
    public static string KeyOf(Entity entity) => entity.Id.ToString("N");

    public static string KeyOf(Card card) => KeyOf((Entity)card);

    /// <summary>True when the underlying entity is still alive; false for dead or non-entity values.</summary>
    public static bool IsAlive(object? value) => value switch
    {
        Entity entity => entity.Life.IsAlive,
        IRefInfo reference => reference.IsAlive,
        _ => true
    };

    /// <summary>Resolves a payload value to a live card, or null when it is stale/absent.</summary>
    public static Card? Card(object? value)
    {
        var entity = value switch
        {
            Card direct => (Entity)direct,
            Ref<Entity> alive when alive.IsAlive => alive.Value,
            IRefInfo => null,
            Entity raw => raw,
            _ => null
        };
        return entity is Card card && card.Life.IsAlive ? card : null;
    }

    public static Entity? EntityOf(object? value) => value switch
    {
        Entity direct => direct,
        Ref<Entity> alive when alive.IsAlive => alive.Value,
        _ => null
    };
}
