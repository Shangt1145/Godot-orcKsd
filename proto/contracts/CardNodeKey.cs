namespace Kards.Ui.Contracts;

/// <summary>Uid is stable within one match. Cache keys always include MatchId.</summary>
public readonly record struct CardNodeKey(string MatchId, string Uid);
