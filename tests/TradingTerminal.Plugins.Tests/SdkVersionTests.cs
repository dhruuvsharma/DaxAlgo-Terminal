using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DaxAlgo.Sdk;
using FluentAssertions;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The SDK version lives in two places — <c>SdkInfo.Version</c>, which plugins read at runtime, and the
/// <c>DaxAlgoSdkVersion</c> MSBuild property, which stamps every published package. One number, two
/// declarations, and nothing but this test stopping them from drifting.
///
/// <para>They have drifted before, in the direction that matters: the shipped AI context pack announced
/// itself as SDK 0.2.0-alpha while the SDK was at 0.3.0. A version a plugin author cannot trust is
/// worse than no version, because compatibility gates are decided on it.</para>
/// </summary>
public sealed class SdkVersionTests
{
    [Fact]
    public void TheRuntimeConstantAndTheBuildPropertyAgree()
    {
        var props = File.ReadAllText(Path.Combine(RepositoryRoot(), "Directory.Build.props"));
        var declared = Regex.Match(props, @"<DaxAlgoSdkVersion>(?<v>[^<]+)</DaxAlgoSdkVersion>");

        declared.Success.Should().BeTrue("Directory.Build.props must declare DaxAlgoSdkVersion");
        declared.Groups["v"].Value.Should().Be(
            SdkInfo.Version,
            "SdkInfo.Version is what a plugin reads; the property is what stamps the package");
    }

    [Fact]
    public void TheVersionIsAtLeastTheOneThatRenamedTheEngineLeftovers()
    {
        // 0.4.0 is a breaking change: IBacktestStrategy became IOrderRoutedStrategy,
        // BacktestStrategyOption became StrategyCatalogEntry, and TradingTerminal.Core.Backtest is gone.
        // Going backwards would tell a plugin built against 0.4 that it is running on a host that still
        // has the old names.
        Version.Parse(SdkInfo.Version).Should().BeGreaterThanOrEqualTo(new Version(0, 4, 0));
    }

    private static string RepositoryRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
}
