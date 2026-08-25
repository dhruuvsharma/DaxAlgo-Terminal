using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// Rung 5 of the verification ladder (#46) — declared parameters against the ones actually read.
/// </summary>
public sealed class SchemaCoherenceProbeTests
{
    private static StrategyParameterSchema Schema(params string[] keys) =>
        new(keys.Select(key => StrategyParameter.Int(key, key, 1, min: 0, max: 10)).ToArray());

    [Fact]
    public void ReadingEverythingDeclaredPasses()
    {
        SchemaCoherenceProbe.Run(Schema("lookback", "threshold"), ["lookback", "threshold"])
            .Outcome.Should().Be(VerificationOutcome.Passed);
    }

    [Fact]
    public void DeclaringAParameterAndNeverReadingItFails()
    {
        // The commonest silent failure a model produces. Nothing else on the ladder can see it: the unit
        // compiles, starts, trades and draws — and the slider does nothing.
        var step = SchemaCoherenceProbe.Run(Schema("lookback", "threshold"), ["lookback"]);

        step.Outcome.Should().Be(VerificationOutcome.Failed);
        var finding = step.Findings.Should().ContainSingle().Subject;
        finding.Code.Should().Be("schema.declared-not-read");
        finding.Message.Should().Contain("threshold");
        finding.Remedy.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ReadingAKeyThatWasNeverDeclaredFails()
    {
        // Harder than the above: the real IParameters throws on an unknown key, so this unit dies at
        // start-up rather than merely behaving oddly.
        var step = SchemaCoherenceProbe.Run(Schema("lookback"), ["lookback", "lookBack"]);

        step.Outcome.Should().Be(VerificationOutcome.Failed);
        step.Findings.Should().ContainSingle().Which.Code.Should().Be("schema.undeclared");
    }

    [Fact]
    public void TheUndeclaredKeyIsReportedEvenWhenSomethingElseIsAlsoUnread()
    {
        // Report the fault that stops the unit running, not the one that makes it subtly wrong. A
        // repair agent fixing the wrong one first wastes a round trip of the user's money.
        var step = SchemaCoherenceProbe.Run(Schema("a", "b"), ["a", "typo"]);

        step.Findings.Should().ContainSingle().Which.Code.Should().Be("schema.undeclared");
    }

    [Fact]
    public void KeysAreComparedCaseSensitively()
    {
        // The lookup they feed is ordinal, so 'Lookback' genuinely is a different parameter from
        // 'lookback' — treating them as the same here would wave through a unit that throws.
        SchemaCoherenceProbe.Run(Schema("lookback"), ["Lookback"])
            .Findings.Should().ContainSingle().Which.Code.Should().Be("schema.undeclared");
    }

    [Fact]
    public void AUnitWithNoParametersIsSkippedRatherThanPassed()
    {
        // Nothing was checked, so nothing was earned. Passing here would let a unit collect credit for
        // a rung it never faced.
        SchemaCoherenceProbe.Run(StrategyParameterSchema.Empty, [])
            .Outcome.Should().Be(VerificationOutcome.NotApplicable);
    }

    [Fact]
    public void AnIncompleteDriveCannotAccuseTheUnitOfNotReading()
    {
        // If the drive stopped early, an unread parameter is evidence of nothing — the code that would
        // have read it may never have run. Reporting it would send a repair agent to rewrite correct
        // code, which is worse than staying silent.
        SchemaCoherenceProbe.Run(Schema("a", "b"), ["a"], drivenToCompletion: false)
            .Outcome.Should().Be(VerificationOutcome.NotApplicable);
    }

    [Fact]
    public void AnUndeclaredKeyIsStillReportedFromAnIncompleteDrive()
    {
        // The reverse of the above: an undeclared read already happened, so it is a fact regardless of
        // how far the drive got.
        SchemaCoherenceProbe.Run(Schema("a"), ["ghost"], drivenToCompletion: false)
            .Findings.Should().ContainSingle().Which.Code.Should().Be("schema.undeclared");
    }

    // ── The recorder that feeds it ───────────────────────────────────────────────────────────────

    [Fact]
    public void TheRecorderNotesEveryKindOfRead()
    {
        var schema = new StrategyParameterSchema(
            StrategyParameter.Int("i", "i", 1),
            StrategyParameter.Number("d", "d", 1d),
            StrategyParameter.Bool("b", "b", true),
            StrategyParameter.Instrument("ins", "ins", new InstrumentId(1)));
        var parameters = new RecordingParameters(new StubParameters(schema));

        parameters.GetInt("i");
        parameters.GetDouble("d");
        parameters.GetBool("b");
        parameters.GetInstrument("ins");

        parameters.KeysRead.Should().BeEquivalentTo(["i", "d", "b", "ins"]);
    }

    [Fact]
    public void TheRecorderNotesAKeyThatThrows()
    {
        // Recorded before delegating on purpose: a read that throws because the key was never declared
        // is precisely the fault rung 5 reports, so losing it would hide the thing worth reporting.
        var parameters = new RecordingParameters(new StubParameters(Schema("known")));

        var act = () => parameters.GetInt("ghost");

        act.Should().Throw<KeyNotFoundException>();
        parameters.KeysRead.Should().Contain("ghost");
    }

    [Fact]
    public void TheRecorderPassesValuesThroughUnchanged()
    {
        new RecordingParameters(new StubParameters(Schema("n"))).GetInt("n").Should().Be(42);
    }

    /// <summary>Minimal parameters: known keys return a fixed value, unknown keys throw the way the real
    /// implementation does.</summary>
    private sealed class StubParameters(StrategyParameterSchema schema) : DaxAlgo.Sdk.IParameters
    {
        public StrategyParameterSchema Schema { get; } = schema;

        private T Get<T>(string name, T value) =>
            Schema.Parameters.Any(p => p.Key == name)
                ? value
                : throw new KeyNotFoundException($"No parameter '{name}'.");

        public bool GetBool(string name) => Get(name, true);
        public double GetDouble(string name) => Get(name, 42d);
        public TEnum GetEnum<TEnum>(string name) where TEnum : struct, Enum => Get(name, default(TEnum));
        public InstrumentId GetInstrument(string name) => Get(name, new InstrumentId(7));
        public int GetInt(string name) => Get(name, 42);
        public long GetLong(string name) => Get(name, 42L);
        public string GetString(string name) => Get(name, "x");
        public string GetText(string name) => Get(name, "x");
    }
}
