using System.IO;
using System.Net.Http;
using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;
using Xunit.Abstractions;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// A fact that runs only when a provider key is present, and says why it did not otherwise.
///
/// <para>xUnit takes <c>Skip</c> as a constant, so the decision has to be made in the attribute's
/// constructor. A silently-absent test would be worse than none: this area's whole discipline is that
/// something built and never run is the defect, and a benchmark that quietly stops running is exactly
/// that.</para>
/// </summary>
public sealed class LiveProviderFactAttribute : FactAttribute
{
    public LiveProviderFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(HyperionBenchmarkTests.LiveKey))
            Skip = "Set HYPERION_LIVE_KEY (or OPENROUTER_API_KEY) and HYPERION_LIVE=1 to drive the benchmark against a model.";
        else if (Environment.GetEnvironmentVariable("HYPERION_LIVE") != "1")
            Skip = "Set HYPERION_LIVE=1 to spend tokens driving the benchmark against a model.";
    }
}

/// <summary>
/// The benchmark drive, offline and live.
///
/// <para>The offline test is the one that matters on an ordinary build: it proves the DRIVER works —
/// that the session composes, that the escape reply is the app's own, that a compiled unit reaches the
/// ladder, that the artifacts land on disk. The live test changes one argument. That split is the
/// reason this can be trusted after a month of nobody running it.</para>
/// </summary>
public sealed class HyperionBenchmarkTests(ITestOutputHelper output)
{
    /// <summary>
    /// The provider, from the environment, because the useful free model changes month to month and a
    /// benchmark hard-wired to one vendor stops being run the day that vendor stops being the answer.
    ///
    /// <para>Four variables, all optional but the key: <c>HYPERION_LIVE_KEY</c> (or
    /// <c>OPENROUTER_API_KEY</c>), <c>HYPERION_LIVE_BASE_URL</c>, <c>HYPERION_LIVE_MODEL</c> and
    /// <c>HYPERION_LIVE_PROVIDER</c> — the last only labels the run. Anything OpenAI-compatible works;
    /// the defaults are the openrouter free tier, which is the weakest realistic case and therefore the
    /// right floor to record.</para>
    /// </summary>
    internal static string? LiveKey =>
        Env("HYPERION_LIVE_KEY") ?? Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");

    private static string LiveBaseUrl => Env("HYPERION_LIVE_BASE_URL") ?? "https://openrouter.ai/api/v1";

    private static string LiveModel => Env("HYPERION_LIVE_MODEL") ?? "minimax/minimax-m3:free";

    private static string LiveProvider => Env("HYPERION_LIVE_PROVIDER") ?? "openrouter";

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    private static string RunDirectory(string label) => Path.Combine(
        RepositoryRoot(), "artifacts", "hyperion-benchmark",
        $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{label}");

    [Fact]
    public async Task The_driver_composes_compiles_and_verifies_without_a_provider()
    {
        var fake = FakeCodegenClient.ForKind(AuthoringKind.Visualizer);

        var result = await HyperionBenchmark.RunAsync(
            fake, new RoslynStrategyCompiler(), AuthoringKind.Visualizer,
            StrategyBuildEffort.Standard, HyperionBenchmark.OrderBookBrief,
            RunDirectory("offline"), new TestWriter(output));

        // The composition REACHED the model. Not "the session can compose" — what the client was handed.
        fake.LastRequest.Should().NotBeNull();
        fake.LastRequest!.SystemContext.Should().Contain("IVisualizer");
        fake.LastRequest.SystemContext.Should().Contain("questions");

        result.Compiled.Should().BeTrue(
            "the canned reply is a current-contract unit: " + Errors(result.Compile));
        result.Report.Should().NotBeNull();
        result.Report!.Passed.Should().BeTrue(
            string.Join(" | ", result.Report.Findings.Select(f => f.ToString())));

        File.Exists(Path.Combine(result.Directory, "summary.md")).Should().BeTrue();
        File.Exists(Path.Combine(result.Directory, "system-prompt.md")).Should().BeTrue();
    }

    [Fact]
    public async Task The_kernel_the_offline_path_uses_is_a_hostable_strategy()
    {
        // The other half of the same fix. Both canned replies were IOrderRoutedStrategy, which the
        // verifier refuses by name before it constructs anything.
        var result = await HyperionBenchmark.RunAsync(
            FakeCodegenClient.ForKind(AuthoringKind.Strategy), new RoslynStrategyCompiler(),
            AuthoringKind.Strategy, StrategyBuildEffort.Standard,
            "a moving-average strategy", RunDirectory("offline-kernel"), new TestWriter(output));

        result.Compiled.Should().BeTrue(Errors(result.Compile));
        result.Compile!.UsesRetiredContract.Should().BeFalse();
        result.Report!.Passed.Should().BeTrue(
            string.Join(" | ", result.Report.Findings.Select(f => f.ToString())));
    }

    [Fact]
    public void The_escape_the_benchmark_sends_is_the_one_the_button_sends()
    {
        // The benchmark answers an interview with AuthoringAction.JustBuildIt. If the pane and the
        // harness ever say different words, the harness stops measuring the prompt a user can produce.
        AuthoringAction.JustBuildIt.Should().Be(AuthoringAction.WhenAsked[0].Reply);
        AuthoringAction.JustBuildIt.Should().Contain("Stop asking and build it now");
    }

    [LiveProviderFact]
    public async Task A_model_answers_the_order_book_brief()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        var client = new OpenAiCompatibleCodegenClient(
            http, LiveProvider, LiveProvider, LiveBaseUrl, LiveModel, LiveKey,
            effort: StrategyBuildProfile.For(StrategyBuildEffort.Standard).Reasoning);

        output.WriteLine($"{LiveProvider} · {LiveModel} · {LiveBaseUrl}");
        var directory = RunDirectory("live");
        var result = await HyperionBenchmark.RunAsync(
            client, new RoslynStrategyCompiler(), AuthoringKind.Visualizer,
            StrategyBuildEffort.Standard, HyperionBenchmark.OrderBookBrief, directory,
            new TestWriter(output));

        output.WriteLine(File.ReadAllText(Path.Combine(directory, "summary.md")));

        // The provider working is ours to assert. What the MODEL produced is measured, not asserted —
        // a benchmark that fails when a free model has a bad day stops being run, and then stops being
        // true. The numbers go to docs/authored-unit-gaps.md by hand, with the transcript behind them.
        result.Turns.Should().NotBeEmpty();
        result.Turns.Should().NotContain(
            t => t.Kind == BuildTurnKind.ProviderError,
            because: string.Join(" | ", result.Turns.Select(t => t.Error)));
    }

    /// <summary>
    /// Re-judges a unit a previous run produced: <c>HYPERION_REVERIFY=&lt;path to the .cs&gt;</c>.
    ///
    /// <para>Not an assertion — it prints the ladder. It exists so a change to the VERIFIER can be
    /// measured against the same generated file rather than against a fresh generation, which would
    /// change two things at once and make the delta meaningless.</para>
    /// </summary>
    [Fact]
    public void Reverify_a_saved_unit_when_one_is_named()
    {
        var path = Environment.GetEnvironmentVariable("HYPERION_REVERIFY");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            output.WriteLine("Set HYPERION_REVERIFY to a generated .cs to re-judge it. Nothing to do.");
            return;
        }

        var (compile, report) = HyperionBenchmark.Reverify(new RoslynStrategyCompiler(), path);

        output.WriteLine($"{Path.GetFileName(path)} — compiled: {compile.Success}");
        foreach (var diagnostic in compile.Errors.Take(20))
            output.WriteLine($"  {diagnostic.Id} {diagnostic.Location} {diagnostic.Message}");

        foreach (var step in report?.Steps ?? [])
            output.WriteLine($"  {step.Rung}: {step.Outcome}");
        foreach (var finding in report?.Findings ?? [])
            output.WriteLine($"    {finding}");
    }

    private static string Errors(StrategyCompileResult? compile) => compile is null
        ? "nothing compiled"
        : string.Join(" | ", compile.Errors.Select(d => $"{d.Id} {d.Location} {d.Message}"));

    /// <summary>Walks up to the folder holding the solution — the test runs from bin/.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TradingTerminal.Windows.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }

    private sealed class TestWriter(ITestOutputHelper output) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void WriteLine(string? value) => output.WriteLine(value ?? string.Empty);

        public override void Write(char value)
        {
            // The benchmark only ever calls WriteLine; this exists because TextWriter demands it.
        }
    }
}
