using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Xunit;
using System.Text.Json;

namespace Kards.Ui.Tests;

public sealed class ConsumptionTests
{
    private static UiEventSegment Segment(long sequence, params UiEvent[] entries) =>
        new("match", sequence, DateTimeOffset.UtcNow, entries, [new(entries, [])]);

    [Fact]
    public async Task EntriesAndChildrenDoNotDoublePlay()
    {
        var feed = new MockFeed();
        feed.Enqueue(Segment(1, new UiEvent("draw")));
        using var player = new SegmentPlayer("match", feed);
        var played = new List<UiEvent>();
        await player.DrainAsync((e, _) => { played.Add(e); return Task.CompletedTask; });
        Assert.Single(played);
        Assert.Equal(2, player.NextSequence);
    }
    [Fact]
    public async Task OutOfOrderSegmentsWaitForMissingSequenceAndDuplicatesAreIgnored()
    {
        var feed = new MockFeed();
        feed.Enqueue(Segment(2, new UiEvent("second")));
        using var player = new SegmentPlayer("match", feed);
        var played = new List<string>();
        Task Play(UiEvent e, CancellationToken _)
        {
            played.Add(e.Kind);
            return Task.CompletedTask;
        }
        await player.DrainAsync(Play);
        Assert.Empty(played);
        Assert.Equal(1L, player.WaitingForSequence);
        feed.Enqueue(Segment(1, new UiEvent("first")));
        feed.Enqueue(Segment(2, new UiEvent("duplicate")));
        await player.DrainAsync(Play);
        Assert.Equal(new[] { "first", "second" }, played);
        feed.Enqueue(Segment(1, new UiEvent("stale")));
        await player.DrainAsync(Play);
        Assert.Equal(2, played.Count);
    }
    [Fact]
    public async Task NewMatchIgnoresOldSegmentsAndStartsAtOne()
    {
        var feed = new MockFeed();
        feed.Enqueue(Segment(1, new UiEvent("old")));
        feed.Enqueue(Segment(1, new UiEvent("fresh")) with
        {
            MatchId = "new-match"
        });
        using var player = new SegmentPlayer("new-match", feed);
        var played = new List<string>();
        await player.DrainAsync((e, _) => { played.Add(e.Kind); return Task.CompletedTask; });
        Assert.Equal(new[] { "fresh" }, played);
    }
    [Fact]
    public void DisposeUnsubscribesAndImmediateEventsDoNotDuplicateSegmentAnimations()
    {
        var feed = new MockFeed();
        var player = new SegmentPlayer("match", feed);
        feed.EmitImmediate(new("draw"));
        Assert.Empty(player.TakeImmediate());
        feed.EmitImmediate(new("interaction-hint"));
        Assert.Single(player.TakeImmediate());
        player.Dispose();
        Assert.Equal(0, feed.SubscriberCount);
        feed.EmitImmediate(new("interaction-hint"));
        Assert.Empty(player.TakeImmediate());
        player.Dispose();
    }
    [Fact]
    public async Task DisposeDuringPlaybackCancelsAndDoesNotPlayFollowingEntries()
    {
        var feed = new MockFeed();
        feed.Enqueue(Segment(1, new("first"), new("second")));
        var player = new SegmentPlayer("match", feed);
        var calls = 0;
        await player.DrainAsync((_, token) => { calls++; player.Dispose(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        Assert.Equal(1, calls);
        Assert.Equal(0, feed.SubscriberCount);
    }
    [Fact]
    public async Task CandidateCollectionIncludesDisallowedUidsAndAdapterReturnsReferences()
    {
        var bridge = new TargetInteraction(() => new[] { "allowed", "disallowed" });
        var uids = await bridge.CollectCandidateUidsAsync("req");
        Assert.Equal(2, uids.Count);
        var registry = new EngineReferenceRegistry<object>();
        var a = new object();
        var b = new object();
        registry.Register("allowed", a);
        registry.Register("disallowed", b);
        Assert.Equal(new object?[] { a, b }, registry.ResolveCandidates(uids));
        registry.Clear();
        Assert.Empty(registry.ResolveCandidates(uids));
    }
    [Fact]
    public void BeginReturnsImmediatelyAndRejectedSubmissionCanRetryAcrossAllSlots()
    {
        var request = new UiTargetRequest("req", [
            new("unit", ChoiceKind.Reference, 1, 1, [new("u", "unit", ChoiceKind.Reference)]),
            new("option", ChoiceKind.Option, 1, 1, [new("o", "option", ChoiceKind.Option)])]);
        var bridge = new TargetInteraction(() => new[] { "u" });
        var responder = new MockTargetResponder(request, true);
        bridge.BeginInteraction(request, responder);
        Assert.Equal(request, bridge.Active);
        IReadOnlyDictionary<string, IReadOnlyList<string>> choices = new Dictionary<string, IReadOnlyList<string>> { ["unit"] = new[] { "u" }, ["option"] = new[] { "o" } };
        Assert.False(bridge.Submit("wrong", choices));
        Assert.NotNull(bridge.Active);
        Assert.False(bridge.Submit("req", choices));
        Assert.NotNull(bridge.Active);
        Assert.True(bridge.Submit("req", choices));
        Assert.Null(bridge.Active);
        Assert.False(bridge.Submit("req", choices));
    }
    [Fact]
    public void MissingSlotIsRejectedAndCancelChecksRequestId()
    {
        var request = new UiTargetRequest("req", [new("unit", ChoiceKind.Reference, 1, 1, [new("u", "unit", ChoiceKind.Reference)])]);
        var bridge = new TargetInteraction(() => new[] { "u" });
        bridge.BeginInteraction(request, new MockTargetResponder(request));
        Assert.False(bridge.Submit("req", new Dictionary<string, IReadOnlyList<string>>()));
        Assert.False(bridge.Cancel("wrong"));
        Assert.NotNull(bridge.Active);
        Assert.True(bridge.Cancel("req"));
        Assert.Null(bridge.Active);
    }
    [Fact]
    public void CatalogContains315DefinitionsAndPreservesUnknownAndNullValues()
    {
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../proto/data/nations"));
        var catalog = new CardCatalog();
        catalog.Load(directory, CardCatalog.ResolveArtResource);
        Assert.Equal(315, catalog.Cards.Count);
        Assert.Equal(315, catalog.Cards.Select(c => c.CardId).Distinct().Count());
        Assert.Null(catalog.Cards.Single(c => c.CardId == "USG/commands/_1").BaseAttack);
        Assert.All(catalog.Filter(set: "USG", type: "order"), c => { Assert.Equal("USG", c.Set); Assert.Equal("order", c.CardType); });
        Assert.Equal(39, catalog.Cards.Count(c => c.ArtPath.Length == 0));
        Assert.All(catalog.Cards.Where(c => c.ArtPath.Length > 0), c => Assert.True(File.Exists(Path.Combine(directory, "../../art", c.ArtPath["res://proto/art/".Length..])), c.CardId));
    }
    [Fact]
    public void MissingTypeAndStatsAreNotInvented()
    {
        var folder = Path.Combine(Path.GetTempPath(), "kards-catalog-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "sample.json");
        try
        {
            File.WriteAllText(file, """{"cards":{"sample":{"name":"sample","attack":null,"defense":null,"cost":null,"keywords":[]}}}""");
            var catalog = new CardCatalog();
            catalog.Load(folder);
            var card = Assert.Single(catalog.Cards);
            Assert.Equal("unknown", card.CardType);
            Assert.Null(card.Cost);
            Assert.Null(card.BaseAttack);
            Assert.Null(card.BaseDefense);
        }
        finally { File.Delete(file); Directory.Delete(folder); }
    }
    [Theory]
    [InlineData("../../../../C:/private.png")]
    [InlineData("../USG/../../secret.png")]
    [InlineData("/absolute.png")]
    public void ArtResolverRejectsUnsafeSuffixes(string path) => Assert.Throws<InvalidDataException>(() => CardCatalog.ResolveArtResource(path));
    [Fact]
    public void SameDefinitionInstancesHaveSeparateMatchScopedKeys()
    {
        Assert.NotEqual(new CardNodeKey("a", "u1"), new CardNodeKey("a", "u2"));
        Assert.NotEqual(new CardNodeKey("a", "u1"), new CardNodeKey("b", "u1"));
    }
    [Fact]
    public void EmissionSnapshotDoesNotObserveLaterMutablePayloadChanges()
    {
        var values = new Dictionary<string, int> { { "armor", 3 } };
        var keywords = new List<string> { "armor" };
        var definition = new UiCardDefinition { CardId = "c", Keywords = keywords, KeywordValues = values };
        var card = new UiCardView { Uid = "u", Definition = definition, Keywords = keywords, KeywordValues = values, EffectiveDefense = 4 };
        var entries = new List<UiEvent> { new("buff", Card: card) };
        var feed = new MockFeed();
        feed.Enqueue(new("match", 1, DateTimeOffset.UtcNow, entries, []));
        values["armor"] = 99;
        keywords.Clear();
        entries.Clear();
        var captured = Assert.Single(Assert.Single(feed.TakeSegments()).Entries).Card!;
        Assert.Equal(3, captured.KeywordValues["armor"]);
        Assert.Equal(3, captured.Definition.KeywordValues["armor"]);
        Assert.Single(captured.Keywords);
        Assert.Equal(4, captured.EffectiveDefense);
    }
    [Fact]
    public void NestedPresentationOpsRetainSourcePriorityAndSupportQualifiedNames()
    {
        using var tree = JsonDocument.Parse("""{"op":"chooseOne","choices":[{"effects":[{"op":"effects.buff"},{"op":"effects.damage"}]}]}""");
        var ops = EffectClassifier.CollectOps(tree.RootElement);
        Assert.Equal(new[] { "chooseOne", "effects.buff", "effects.damage" }, ops);
        Assert.Equal("fire", EffectClassifier.ClassifyOps(ops));
        Assert.Equal("generic", EffectClassifier.ClassifyOps(new[] { "futureUnknownEffect" }));
    }
    [Fact]
    public void PresentationClassifierNeverExecutesUnknownOrNestedEffects()
    {
        using var tree = JsonDocument.Parse("""[{"effects":[{"op":"heal"},{"op":"destroy"}]},{"unused":{"op":"draw"}}]""");
        Assert.Equal("destroy", EffectClassifier.ClassifyOps(EffectClassifier.CollectOps(tree.RootElement)));
        Assert.Equal(9, EffectClassifier.CategoryLabels.Count);
    }
}
