using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DaxAlgo.Sdk;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.UI.Controls.Render;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// Verbs: a unit declaring something the viewer can ask it to DO, and a button that does it.
///
/// <para>Until now a unit could declare a value and nothing else, so every button on the hand-written
/// windows — reset the profile, clear the tape, re-centre — had no expression at all. The nearest an
/// author could get was a bool parameter, which reads as a setting, behaves as a command, and has to
/// be flipped twice to mean "now".</para>
///
/// <para>These assert the whole path rather than the type: the declaration reaches the presenter, the
/// button reaches the rendered window, pressing it reaches the unit, and a unit that misbehaves
/// costs its buttons rather than the window.</para>
/// </summary>
[Collection(AuthoringCollection.Name)]
public sealed class UnitActionTests
{
    private static readonly IReadOnlyList<UnitAction> Two =
    [
        new("reset", "Reset profile", "Clears everything accumulated so far."),
        new("centre", "Re-centre"),
    ];

    [WpfFact]
    public void A_declared_verb_becomes_a_button_in_the_rendered_window()
    {
        // The assertion that matters: not that the presenter holds it, but that the chrome the user
        // looks at actually contains a pressable control saying so.
        var host = new AuthoredUnitHost(
            "unit", _ => true, actions: () => Two, invokeAction: _ => Task.CompletedTask);

        var view = Render(host.Presenter);
        var labels = Buttons(view).Select(b => b.Content as string).ToArray();

        Assert.Contains("Reset profile", labels);
        Assert.Contains("Re-centre", labels);
    }

    [WpfFact]
    public void A_unit_with_verbs_and_no_parameters_still_gets_its_expander()
    {
        // The defect this area keeps producing, caught before shipping it. The setup expander keyed
        // off HasParameters, which was right while parameters were the only thing in it — so a
        // visualizer that declares no parameters and one action, which is the ordinary shape for
        // "clear the tape" on a picture with nothing to tune, would have had its button built, bound
        // and never shown.
        var host = new AuthoredUnitHost(
            "unit", _ => true, actions: () => Two, invokeAction: _ => Task.CompletedTask);

        Assert.False(host.Presenter.HasParameters, "this unit declares none");
        Assert.True(host.Presenter.HasSetup, "but it has verbs, so the expander must be shown");
        Assert.Contains("Reset profile", Buttons(Render(host.Presenter)).Select(b => b.Content as string));
    }

    [WpfFact]
    public void Pressing_it_reaches_the_unit_with_the_id_it_declared()
    {
        var invoked = new List<string>();
        var host = new AuthoredUnitHost(
            "unit", _ => true,
            actions: () => Two,
            invokeAction: id => { invoked.Add(id); return Task.CompletedTask; });

        var button = Buttons(Render(host.Presenter))
            .First(b => (b.Content as string) == "Reset profile");

        button.Command.Execute(button.CommandParameter);

        Assert.Equal(["reset"], invoked);
    }

    [WpfFact]
    public void A_host_given_no_way_to_run_them_offers_none()
    {
        // A button over nothing is worse than no button: it reads as a broken unit rather than a
        // window that was never wired for verbs.
        var host = new AuthoredUnitHost("unit", _ => true, actions: () => Two);

        Assert.False(host.Presenter.HasActions);
    }

    [WpfFact]
    public void A_verb_that_throws_costs_the_press_and_not_the_window()
    {
        // The handler is async void on the UI thread, so an escaping exception is a process kill.
        var host = new AuthoredUnitHost(
            "unit", _ => true,
            actions: () => Two,
            invokeAction: _ => throw new InvalidOperationException("boom"));

        // Selected by label, not First(): the chrome has buttons of its own and the first one in tree
        // order is not the unit's.
        var button = Buttons(Render(host.Presenter))
            .First(b => (b.Content as string) == "Reset profile");
        button.Command.Execute(button.CommandParameter);

        Assert.Contains(host.Presenter.Log, line => line.Message.Contains("boom", StringComparison.Ordinal));
    }

    [WpfFact]
    public void A_getter_that_throws_costs_the_buttons_and_not_the_window()
    {
        // Reading the property is author code like any other, and it runs while the window is being
        // built — so a throw here would stop the window opening at all.
        var host = new AuthoredUnitHost(
            "unit", _ => true,
            actions: () => throw new InvalidOperationException("boom"),
            invokeAction: _ => Task.CompletedTask);

        Assert.False(host.Presenter.HasActions);
        Assert.NotNull(Render(host.Presenter));
    }

    [Theory]
    [InlineData("over the limit")]
    [InlineData("a duplicate id")]
    [InlineData("a missing label")]
    public void A_malformed_set_is_refused_whole(string shape)
    {
        // Refused rather than truncated, the same rule the layout tree follows: half a set of
        // controls, silently, is worse than a set that visibly did not apply.
        IReadOnlyList<UnitAction> declared = shape switch
        {
            "over the limit" =>
                [.. Enumerable.Range(0, UnitAction.Maximum + 1).Select(i => new UnitAction($"a{i}", $"A{i}"))],
            "a duplicate id" => [new("same", "One"), new("same", "Two")],
            _ => [new("ok", "Fine"), new("bad", "  ")],
        };

        Assert.Empty(UnitAction.Sanitise(declared));

        var host = new AuthoredUnitHost(
            "unit", _ => true, actions: () => declared, invokeAction: _ => Task.CompletedTask);

        Assert.False(host.Presenter.HasActions);
    }

    [WpfFact]
    public void Exactly_the_limit_is_allowed()
    {
        // The bound is not vacuous in the other direction either.
        IReadOnlyList<UnitAction> declared =
            [.. Enumerable.Range(0, UnitAction.Maximum).Select(i => new UnitAction($"a{i}", $"A{i}"))];

        Assert.Equal(UnitAction.Maximum, UnitAction.Sanitise(declared).Count);
    }

    [WpfFact]
    public void The_contract_defaults_to_no_verbs()
    {
        // Both interfaces default-implement Actions, so every unit written before this existed keeps
        // compiling and shows nothing new.
        Assert.Empty(((IVisualizer)new Silent()).Actions);
        Assert.Empty(UnitAction.Sanitise(null));
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────

    private sealed class Silent : IVisualizer
    {
        public StrategyParameterSchema Schema { get; } = new();

        public TradingTerminal.Core.Strategies.StrategyDataRequirement DataRequirement =>
            TradingTerminal.Core.Strategies.StrategyDataRequirement.Bars;

        public Task OnStartAsync(IVisualizerContext context, CancellationToken ct) => Task.CompletedTask;

        public void Draw(IRenderSurface surface) => surface.Text(2d, 10d, "x");
    }

    private static AuthoredUnitView Render(AuthoredUnitPresenter presenter)
    {
        // Expanded, because a collapsed Expander does not realise its content and the buttons would
        // not be in the visual tree to find. The host closes it once a unit is running — see the note
        // in docs/authored-unit-gaps.md about a verb living behind one click.
        presenter.IsSetupExpanded = true;

        var view = new AuthoredUnitView { DataContext = presenter };
        view.Measure(new Size(800d, 600d));
        view.Arrange(new Rect(0d, 0d, 800d, 600d));
        view.UpdateLayout();
        return view;
    }

    /// <summary>Every button in the rendered chrome — the window as built, not as constructed.</summary>
    private static IReadOnlyList<Button> Buttons(DependencyObject root)
    {
        var found = new List<Button>();
        Walk(root, found);
        return found;
    }

    private static void Walk(DependencyObject node, List<Button> found)
    {
        if (node is Button button) found.Add(button);

        var children = VisualTreeHelper.GetChildrenCount(node);
        for (var index = 0; index < children; index++)
            Walk(VisualTreeHelper.GetChild(node, index), found);
    }
}
