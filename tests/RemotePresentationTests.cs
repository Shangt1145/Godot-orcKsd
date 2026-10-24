using System.Text.Json;
using System.Text.Json.Nodes;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;
using Xunit;

namespace Kards.Ui.Tests;

public class RemotePresentationTests
{
    private static UiCardView Card(string uid, string side = "self", string zone = "support", int health = 3) => new()
    {
        Uid = uid, OwnerSide = side, Zone = zone, Visibility = Visibility.Full, Health = health,
        EffectiveDefense = health, EffectiveAttack = 2,
        Definition = new() { CardId = uid, CardType = "unit", Name = uid }
    };
    private static UiMatchView View(UiCardView[]? self = null, UiCardView[]? enemy = null, UiCardView[]? hand = null) => new()
    { MatchId = "remote", Phase = "play", Turn = 1, ActivePlayerSide = "self", SelfLine = self ?? [], EnemyLine = enemy ?? [], SelfHand = hand ?? [] };
    private static JsonNode Event(string name, string data, int? seat = null) => new JsonObject
    { ["message"] = name, ["kind"] = "update", ["data"] = JsonNode.Parse(data), ["playerSeat"] = seat };

    [Fact]
    public void DeploymentUsesTheNewBoardAndDrawSignalsAreDeduplicated()
    {
        var hand = Card("a", zone: "hand"); var deployed = hand with { Zone = "support" };
        var steps = RemotePresentationTranslator.Translate(
            [Event("unit.deployed", "{\"Unit\":{\"$ref\":\"a\"}}")], View(hand: [hand]), View([deployed]));
        Assert.Equal("support", Assert.IsType<UiDeploymentPresentation>(Assert.Single(steps)).Card.Zone);
        steps = RemotePresentationTranslator.Translate([
            Event("card.drawn", "{\"Card\":{\"$ref\":\"a\"}}", 0),
            Event("card.hand.add", "{\"Card\":{\"$ref\":\"a\"}}", 0)], View(), View(hand: [hand]));
        Assert.Single(steps.OfType<UiDrawPresentation>());
    }

    [Fact]
    public void LethalCombatRetainsBothSnapshotsAndTheDeadAttackersWeaponSource()
    {
        var attacker = Card("a"); var victim = Card("v", "enemy", health: 2);
        var steps = RemotePresentationTranslator.Translate([
            Event("card.stat.changed", "{\"Card\":{\"$ref\":\"v\"}}"),
            Event("card.damaged", "{\"Card\":{\"$ref\":\"v\"},\"Amount\":2}"),
            Event("unit.damage.dealt", "{\"Unit\":{\"$ref\":\"a\"},\"Card\":{\"$ref\":\"v\"},\"Amount\":2}"),
            Event("card.died", "{\"Card\":{\"$ref\":\"v\"}}")], View([attacker], [victim]), View());
        var impact = Assert.Single(Assert.Single(steps.OfType<UiBoardImpactsPresentation>()).Impacts);
        Assert.Equal("v", impact.Before.Uid); Assert.Null(impact.After);
        Assert.Equal(2, impact.Damage); Assert.Equal("a", impact.Source!.Uid);
        Assert.Empty(steps.OfType<UiRemovalPresentation>());
    }

    [Fact]
    public void HiddenEnemyDrawKeepsItsSideWithoutRevealingACardOrDoubleAnimation()
    {
        var steps = RemotePresentationTranslator.Translate([
            Event("card.drawn", "{\"Card\":{\"$hidden\":true},\"Player\":{\"$hidden\":true}}", 1),
            Event("card.hand.add", "{\"Card\":{\"$hidden\":true}}", 1)], View(), View() with { EnemyHandCount = 5 });
        var draw = Assert.Single(steps.OfType<UiDrawPresentation>());
        Assert.Equal("enemy", draw.Side); Assert.Null(draw.Card);
    }

    [Fact]
    public void RemovedSmokeIsProjectedAsAKeywordChangeWithoutInventingDamage()
    {
        var smoked = Card("smoke") with { Keywords = ["smokescreen"] };
        var cleared = smoked with { Keywords = [] };
        var steps = RemotePresentationTranslator.Translate(
            [Event("card.stat.changed", "{\"Card\":{\"$ref\":\"smoke\"},\"ChangedFields\":[\"keywords\"]}")],
            View([smoked]), View([cleared]));
        var change = Assert.Single(steps.OfType<UiStatusPresentation>());
        Assert.Equal(UiStatusKind.KeywordsChanged, change.Status);
        Assert.DoesNotContain("smokescreen", change.After.Keywords);
        Assert.Empty(steps.OfType<UiBoardImpactsPresentation>());
    }

    [Fact]
    public void PublicEnemyOrderFaceCanAnimateAfterLeavingTheBoard()
    {
        var order = Card("order", "enemy", "hand") with { Definition = new() { CardId = "order", CardType = "order", Name = "Order" } };
        var entry = Event("card.played", "{\"Card\":{\"$hidden\":true}}", 1);
        entry["cardView"] = JsonSerializer.SerializeToNode(order, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        entry["ownerSeat"] = 1;
        var steps = RemotePresentationTranslator.Translate([entry], View(), View(), artLookup: _ => "local.png");
        var animation = Assert.Single(steps.OfType<UiOrderPresentation>());
        Assert.Equal("enemy", animation.Card.OwnerSide); Assert.Equal("local.png", animation.Card.Definition.ArtPath);
    }
}
