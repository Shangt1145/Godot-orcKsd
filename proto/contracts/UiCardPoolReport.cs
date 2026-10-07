namespace Kards.Ui.Contracts;

public enum UiCardSupport { Verified, GeneratedOnly, Unreviewed, Unsupported, SourceChanged }
public sealed record UiCardPoolRow(string CardId, string Name, UiCardSupport Support, string Reason);
public sealed record UiCardPoolReport(string ReviewVersion, int Total, int StructurallyRepresentable,
    IReadOnlyList<UiCardPoolRow> Cards)
{
    public int Verified => Cards.Count(c => c.Support == UiCardSupport.Verified);
}
