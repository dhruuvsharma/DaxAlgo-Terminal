using System.IO;
using System.Text.Json;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;
using Xunit.Abstractions;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// Compiles a saved authoring session from disk and prints what the ladder says about it.
///
/// <para><b>For the report that begins "it failed to compile".</b> The session file already holds the
/// exact files the builder produced, so the answer is a compile away — and reading the source looking
/// for the mistake is slower and worse than asking the compiler that rejected it.</para>
///
/// <para>Off unless <c>SESSION_FILE</c> names one, because a test that reads a developer's home
/// directory has no business running in anybody's suite.</para>
/// </summary>
public sealed class CompileASavedSession(ITestOutputHelper output)
{
    [Fact]
    public void Report_what_the_ladder_says()
    {
        var path = Environment.GetEnvironmentVariable("SESSION_FILE");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        var files = root.GetProperty("Files").EnumerateArray()
            .Select(f => new StrategyFile(f.GetProperty("Name").GetString()!, f.GetProperty("Content").GetString()!))
            .ToArray();

        output.WriteLine($"{files.Length} file(s): {string.Join(", ", files.Select(f => f.Name))}");

        var gate = new UnitGate(
            new RoslynStrategyCompiler(),
            root.GetProperty("StrategyId").GetString() ?? "session",
            root.GetProperty("DisplayName").GetString() ?? "Session");

        var result = gate.Run(files);

        output.WriteLine($"passed: {result.Passed}  rungs cleared: {result.Report.RungsCleared}  " +
                         $"failed at: {result.Report.FailedAt?.ToString() ?? "nothing"}");

        foreach (var finding in result.Report.Findings)
            output.WriteLine($"  {finding}");
    }
}
