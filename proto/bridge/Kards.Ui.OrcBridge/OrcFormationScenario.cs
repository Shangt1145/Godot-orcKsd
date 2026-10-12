using Kards.Ui.Contracts;
using Orc.Game.Cards;

namespace Kards.Ui.OrcBridge;

/// <summary>Native verification fixture. Exports only UI values and never enters a product match.</summary>
public sealed class OrcFormationScenario : IDisposable
{
    private readonly OrcMatchSession _session;
    private readonly List<UiPresentationResolution> _resolutions = [];
    private OrcFormationScenario(OrcMatchSession session) => _session = session;
    public UiMatchView View => _session.View;
    public UiBattleActions Actions => _session.Actions;
    public IReadOnlyList<UiPresentationResolution> Resolutions => _resolutions;
    public string ActorUid { get; private set; } = "";
    public void SetInputReader(Func<UiActionInteractionState> read) => _session.InteractionStateReader = read;
    public async Task<UiSubmitOutcome> SubmitAsync(UiCommand command)
    {
        var result = await _session.SubmitAsync(command); _session.Pump(); return result;
    }
    public static async Task<OrcFormationScenario> CreateAsync(int units, Func<string, string>? artLookup = null)
    {
        var s = await OrcMatchSession.CreateProbeAsync("formation-probe", 20, 37, $"formation-{units}", artLookup: artLookup);
        var fixture = new OrcFormationScenario(s);
        try
        {
            await s.SettleMulliganAsync(false); s.DriveOpponent = false;
            var match = s.Host.Match; var player = match.Players[0]; var line = match.Battlefield.PlayerASupportLine;
            await match.ResourceManager.GainPointsAsync(player, 20);
            var cards = player.Hand.OfType<UnitCard>().ToArray();
            for (var i = 0; i < units; i++)
                if (!(await match.PlayManager.PlayUnitAsync(cards[i], line.GetInsertionGaps().Last())).IsSuccess)
                    throw new InvalidOperationException("Native formation fixture failed to prepare a unit.");
            fixture.ActorUid = OrcRefs.KeyOf(cards[units]);
            s.SettleProjection();
            s.Host.PresentationReady += (resolution, _) => fixture._resolutions.Add(resolution);
            return fixture;
        }
        catch { s.Dispose(); throw; }
    }
    public void Dispose() => _session.Dispose();
}
