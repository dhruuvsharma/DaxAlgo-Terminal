using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.UI.Controls.Render;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// Editing a unit's parameters and watching the picture move.
///
/// <para>Issue #42 specified an MT5-style parameter panel; what existed was an MT5-style parameter
/// <i>display</i> — a list of label/value strings rendered as read-only text. Changing a look-back
/// and seeing the result is the most common thing anyone does with a trading tool, and it was the one
/// thing an authored window could not do.</para>
/// </summary>
public sealed class AuthoredUnitParameterTests
{
    // ── Parsing ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AWholeNumberFieldRefusesTextRatherThanReachingTheSandbox()
    {
        // A bad value that reaches the runtime comes back as a failed start, which reads to the user
        // as the unit being broken rather than the number being wrong.
        var row = Row(ParameterKind.Integer);
        row.Value = "twenty";

        Assert.False(row.TryParse(out _));
        Assert.Contains("whole number", row.Error);
        Assert.True(row.HasError);
    }

    [Fact]
    public void TheDeclaredRangeIsEnforcedWhereItCanStillBeCorrected()
    {
        // A schema that says 2..500 and a box that takes -1 is a schema the author wrote for nothing.
        var row = Row(ParameterKind.Integer, minimum: 2, maximum: 500);

        row.Value = "1";
        Assert.False(row.TryParse(out _));
        Assert.Contains("at least 2", row.Error);

        row.Value = "9000";
        Assert.False(row.TryParse(out _));
        Assert.Contains("at most 500", row.Error);

        row.Value = "20";
        Assert.True(row.TryParse(out var parsed));
        Assert.Equal(20L, parsed);
        Assert.False(row.HasError);
    }

    [Fact]
    public void NumbersAreParsedInvariantlySoADecimalCommaCannotChangeTheMeaning()
    {
        // The text goes to a sandbox that compares it against literals in generated code. A machine
        // with a comma separator round-tripping 1.5 into 15 is a parameter that silently means
        // something else on one desk.
        var row = Row(ParameterKind.Number);
        row.Value = "1.5";

        Assert.True(row.TryParse(out var parsed));
        Assert.Equal(1.5d, (double)parsed!);

        row.Value = "1,5";
        Assert.False(row.TryParse(out _));
    }

    [Fact]
    public void ANonFiniteNumberIsRefused()
    {
        // NaN compares false against every threshold, so a unit holding one stops acting and never
        // says why. Cheaper to refuse it in the box.
        var row = Row(ParameterKind.Number);
        row.Value = "NaN";

        Assert.False(row.TryParse(out _));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("False", false)]
    [InlineData("on", true)]
    [InlineData("0", false)]
    public void AToggleAcceptsTheFormsAUserActuallyTypes(string text, bool expected)
    {
        var row = Row(ParameterKind.Boolean);
        row.Value = text;

        Assert.True(row.TryParse(out var parsed));
        Assert.Equal(expected, parsed);
    }

    [Fact]
    public void AChoiceRefusesAValueThatIsNotOneOfTheChoices()
    {
        var row = new AuthoredUnitParameter
        {
            Key = "mode",
            Kind = ParameterKind.Choice,
            Choices = ["fast", "slow"],
        };

        row.Value = "sideways";
        Assert.False(row.TryParse(out _));

        row.Value = "slow";
        Assert.True(row.TryParse(out var parsed));
        Assert.Equal("slow", parsed);
    }

    [Fact]
    public void AnInstrumentRowParsesToAnInstrumentIdRatherThanANumber()
    {
        var row = Row(ParameterKind.Instrument);
        row.Value = "7";

        Assert.True(row.TryParse(out var parsed));
        Assert.Equal(new InstrumentId(7), parsed);

        row.Value = "0";
        Assert.False(row.TryParse(out _));
    }

    // ── Dirty tracking ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ARowIsCleanUntilItIsEditedAndCleanAgainOnceApplied()
    {
        var row = Row(ParameterKind.Integer);
        row.Seed("20");

        Assert.False(row.IsDirty);

        row.Value = "30";
        Assert.True(row.IsDirty);

        row.Commit();
        Assert.False(row.IsDirty);
        Assert.Equal("30", row.AppliedValue);
    }

    [Fact]
    public void ResettingPutsTheRowBackToWhatTheUnitIsRunningWith()
    {
        var row = Row(ParameterKind.Integer);
        row.Seed("20");
        row.Value = "999";

        row.Revert();

        Assert.Equal("20", row.Value);
        Assert.False(row.IsDirty);
    }

    // ── The presenter ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParametersAreReadOnlyUntilTheHostSaysItCanApplyThem()
    {
        // An editable box over a value nothing reads is worse than a label.
        var presenter = new AuthoredUnitPresenter();

        Assert.False(presenter.CanEditParameters);
    }

    [Fact]
    public void ApplyingSendsEveryParsedValueAtOnce()
    {
        var presenter = Presenter(
            (Row(ParameterKind.Integer, key: "lookback"), "20"),
            (Row(ParameterKind.Number, key: "threshold"), "1.5"));

        IReadOnlyDictionary<string, object?>? applied = null;
        presenter.ApplyRequested += (_, values) => applied = values;

        presenter.Parameters[0].Value = "30";
        presenter.ApplyParametersCommand.Execute(null);

        Assert.NotNull(applied);
        Assert.Equal(30L, applied!["lookback"]);
        Assert.Equal(1.5d, applied["threshold"]);
    }

    [Fact]
    public void NothingIsAppliedWhileAnyRowIsInvalid()
    {
        // Applying the valid half would leave the unit running a mixture the user never asked for,
        // and the picture would then be evidence for a configuration that exists nowhere.
        var presenter = Presenter(
            (Row(ParameterKind.Integer, key: "lookback"), "20"),
            (Row(ParameterKind.Number, key: "threshold"), "1.5"));

        var applied = 0;
        presenter.ApplyRequested += (_, _) => applied++;

        presenter.Parameters[0].Value = "not a number";
        presenter.Parameters[1].Value = "2.5";
        presenter.ApplyParametersCommand.Execute(null);

        Assert.Equal(0, applied);
        Assert.Contains("not valid", presenter.ParameterStatus);
        Assert.True(presenter.Parameters[1].IsDirty, "the valid edit is kept, not discarded");
    }

    [Fact]
    public void PendingChangesAreVisibleFromTheHeaderSoAnEditIsNotLeftBehind()
    {
        // Once the expander is collapsed an unapplied edit is invisible, and then the picture
        // disagrees with the numbers on screen.
        var presenter = Presenter((Row(ParameterKind.Integer, key: "lookback"), "20"));

        Assert.False(presenter.HasPendingChanges);

        presenter.Parameters[0].Value = "30";
        Assert.True(presenter.HasPendingChanges);

        presenter.ParametersApplied();
        Assert.False(presenter.HasPendingChanges);
    }

    [Fact]
    public void AFailedApplyKeepsTheEditsAndSaysWhatWentWrong()
    {
        var presenter = Presenter((Row(ParameterKind.Integer, key: "lookback"), "20"));
        presenter.Parameters[0].Value = "30";
        presenter.ApplyParametersCommand.Execute(null);

        presenter.ParametersFailed("Could not apply: the feed refused depth.");

        Assert.False(presenter.IsApplying);
        Assert.Contains("refused depth", presenter.ParameterStatus);
        Assert.True(presenter.HasPendingChanges, "the user typed them; throwing them away is a second failure");
    }

    [Fact]
    public void ResetClearsEveryPendingEditAtOnce()
    {
        var presenter = Presenter(
            (Row(ParameterKind.Integer, key: "a"), "1"),
            (Row(ParameterKind.Integer, key: "b"), "2"));

        presenter.Parameters[0].Value = "9";
        presenter.Parameters[1].Value = "9";

        presenter.ResetParametersCommand.Execute(null);

        Assert.False(presenter.HasPendingChanges);
        Assert.Equal("1", presenter.Parameters[0].Value);
        Assert.Equal("2", presenter.Parameters[1].Value);
    }

    // ── Run state ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PauseIsOfferedOnlyWhenTheHostCanActuallyPause()
    {
        // A control that does nothing is worse than no control.
        Assert.False(new AuthoredUnitPresenter().CanPause);
    }

    [Fact]
    public void PausingAsksForTheOppositeOfWhatIsRunning()
    {
        var presenter = new AuthoredUnitPresenter { CanPause = true, IsLive = true };
        bool? asked = null;
        presenter.PauseRequested += (_, pause) => asked = pause;

        presenter.TogglePauseCommand.Execute(null);
        Assert.True(asked);

        presenter.IsLive = false;
        presenter.TogglePauseCommand.Execute(null);
        Assert.False(asked);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static AuthoredUnitParameter Row(
        ParameterKind kind, string key = "p", double? minimum = null, double? maximum = null) =>
        new() { Key = key, Kind = kind, Minimum = minimum, Maximum = maximum };

    private static AuthoredUnitPresenter Presenter(params (AuthoredUnitParameter Row, string Seed)[] rows)
    {
        var presenter = new AuthoredUnitPresenter { CanEditParameters = true };
        foreach (var (row, seed) in rows)
        {
            presenter.Parameters.Add(row);
            row.Seed(seed);
        }

        return presenter;
    }
}
