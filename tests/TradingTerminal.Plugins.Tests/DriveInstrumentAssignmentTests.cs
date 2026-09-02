using DaxAlgo.Sdk;
using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// What a unit declaring many members actually RECEIVES.
///
/// <para>Written after three hypotheses about one blank window were each wrong. A regime screen with
/// twenty member slots drew "no member has enough bars" forever, and I guessed at peers, then at
/// parameter assignment, then at bar size — three changes, all worth making, none of them the cause.
/// This measures instead of guessing, which is what should have happened first.</para>
/// </summary>
public sealed class DriveInstrumentAssignmentTests
{
    /// <summary>Twenty slots defaulting to None, exactly as a generated index screen declares them —
    /// "leave unset to exclude this slot".</summary>
    private sealed class ManyMemberKernel : IStrategyKernel
    {
        public const int Slots = 20;

        public List<InstrumentId> Resolved { get; } = [];

        public StrategyParameterSchema Schema { get; } = new(
        [
            StrategyParameter.Instrument("index", "Index", new InstrumentId(1), group: "Market"),
            .. Enumerable.Range(0, Slots).Select(i =>
                StrategyParameter.Instrument($"m{i + 1:00}", $"Member {i + 1:00}", InstrumentId.None,
                    group: "Members")),
        ]);

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

        public Task OnStartAsync(IStrategyRuntimeContext context, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(context);

            for (var i = 0; i < Slots; i++)
                Resolved.Add(context.Parameters.GetInstrument($"m{i + 1:00}"));

            return Task.CompletedTask;
        }
    }

    [Fact]
    public void EveryMemberSlotResolvesToARealInstrument()
    {
        // The gate the generated screen uses is `if (id.IsNone) continue;`. A slot left as None is a
        // member excluded from the matrix, so a drive that leaves them None hands the unit an empty
        // basket and then reports the empty picture as the unit's fault.
        var unit = new ManyMemberKernel();
        SyntheticDrive.Run(unit);

        unit.Resolved.Should().HaveCount(ManyMemberKernel.Slots);
        unit.Resolved.Should().NotContain(
            InstrumentId.None,
            "a member slot left unresolved is a member the unit drops, and the drive then judges the "
            + "empty matrix it caused");
    }

    [Fact]
    public void ThoseInstrumentsAreOnesTheDriveActuallyFeeds()
    {
        // Resolving to an id nobody publishes is the same defect one step later: the parameter looks
        // set and the member still never receives a bar.
        var unit = new ManyMemberKernel();
        SyntheticDrive.Run(unit);

        unit.Resolved.Distinct().Should().OnlyContain(
            id => SyntheticDrive.Universe.Contains(id),
            "every assigned instrument must be one the drive delivers bars for");
    }
}
