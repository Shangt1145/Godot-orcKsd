namespace Kards.Ui.Contracts;

public enum Visibility
{
    Full, Silhouette, Hidden
}
public enum DtoKind
{
    CardDefinition, CardInstance
}
public interface IDtoKind
{
    DtoKind Kind
    {
        get;
    }
}

/// <summary>Uid is stable within one match. Cache keys always include MatchId.</summary>
public readonly record struct CardNodeKey(string MatchId, string Uid);
