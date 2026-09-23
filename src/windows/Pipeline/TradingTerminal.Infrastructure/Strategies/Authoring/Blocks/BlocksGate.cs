using System.Security.Cryptography;
using TradingTerminal.Blocks.Runtime.Verification;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Blocks;

/// <summary>
/// The objective half of a Blocks build: compile the unit, drive it against a synthetic market, and —
/// when it has a page and the machine can open one — do that with its page open, and photograph it.
///
/// <para>Every finding carries the rung it belongs to and a stable code, so the swarm's router and its
/// best-version keeping work exactly as they do for the widget SDK's ladder: a page fault names
/// <c>ui/index.html</c> and reaches whoever owns the page; a handler that throws names no file and
/// reaches the unit's class.</para>
///
/// <para>Warnings are left out of the verdict. A setting the unit never read, or market data it dropped
/// under a burst, is worth telling a user and not worth a repair turn.</para>
/// </summary>
public sealed class BlocksGate(
    BlocksUnitCompiler compiler,
    string unitId,
    IPageProbe? probe = null,
    DriveOptions? drive = null) : IUnitGate
{
    private readonly BlocksUnitCompiler _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));

    /// <summary>The file page findings are addressed to — the page's entry point, owned by the page's task.</summary>
    public const string PageEntry = "ui/index.html";

    /// <summary>How long and how hard a gate drives a unit: shorter than the default, because it runs every round.</summary>
    public static DriveOptions DefaultDrive { get; } = new(Steps: 120, SettleTime: TimeSpan.FromMilliseconds(600), PageReadyTimeout: TimeSpan.FromSeconds(15));

    /// <summary>The compile behind the latest verdict — what a caller registers.</summary>
    public BlocksCompileResult? Latest { get; private set; }

    /// <summary>The drive behind the latest verdict, warnings included.</summary>
    public DriveReport? LatestDrive { get; private set; }

    /// <summary>The most recent photograph of the page, kept across a later round that could not take
    /// one — the preview shows it.</summary>
    public UnitRaster? LatestPicture { get; private set; }

    public async Task<GateResult> RunAsync(IReadOnlyList<StrategyFile> files, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(files);

        var compiled = _compiler.Compile(unitId, files);
        Latest = compiled;
        LatestDrive = null;

        if (!compiled.Success || compiled.Factory is null)
            return new GateResult(Refused(compiled), Compile: null) { Compiled = false };

        var pages = compiled.PageFiles ?? [];
        var options = (drive ?? DefaultDrive) with { HasPage = pages.Count > 0 };

        DriveReport report;
        IReadOnlyList<DriveFinding> findings;
        UnitRaster? picture = null;

        if (pages.Count > 0 && probe is { IsAvailable: true })
        {
            var check = await probe.RunAsync(compiled.Factory, pages, unitId, options, ct).ConfigureAwait(false);
            (report, findings) = (check.Drive, check.Findings);

            if (check.Png is { Length: > 0 } png)
                picture = new UnitRaster(png, check.Width, check.Height, Convert.ToHexString(SHA256.HashData(png)));
        }
        else
        {
            report = await BlocksDrive.RunAsync(compiled.Factory, options, ct).ConfigureAwait(false);
            findings = report.Findings;
        }

        LatestDrive = report;

        var failures = findings.Where(f => f.Severity == DriveSeverity.Failure).ToArray();
        var lifecycle = failures.Where(f => !IsPage(f.Code)).Select(f => ToFinding(f, file: null)).ToArray();

        // A page error names the page file it came from when the browser said which, so a fault in a
        // module reaches whoever owns the module rather than whoever owns index.html. And a file the page
        // loads that the unit does not have is a page failure whatever the probe saw: see PageAssets.
        VerificationFinding[] page =
        [
            .. failures.Where(f => IsPage(f.Code)).Select(f => ToFinding(f, PageFileIn(f.Message) ?? PageEntry)),
            .. PageAssets.Missing(pages).Select(m => new VerificationFinding(
                "page.missing-file",
                $"{m.From} loads '{m.Reference}', but {m.Path} is not one of the unit's files, so it never loads.",
                $"Write {m.Path}, or stop loading it.",
                m.Path)),

            // A page that never became ready AND makes its own dax: that is why, named by file and line.
            // Only then — an alias or a wrapper on a page that works is nobody's problem.
            .. (failures.Any(f => f.Code == "page.never-ready") ? PageAssets.OwnBridges(pages) : []).Select(b => new VerificationFinding(
                "page.own-bridge",
                $"{b.File} line {b.Line} makes its own dax (`{b.Text}`). The terminal injects dax — on, send, ready — "
                + "before any page script runs; a page's own copy talks to nothing, so the page never became ready "
                + "and the unit never sent it anything.",
                "Delete that object and every assignment to window.dax; call the global dax.on / dax.send / dax.ready directly.",
                b.File)),
        ];

        var verdict = new VerificationReport(
        [
            VerificationStep.Pass(VerificationRung.Compile),
            VerificationStep.Pass(VerificationRung.Policy),
            VerificationStep.Pass(VerificationRung.Shape),
            lifecycle.Length > 0 ? VerificationStep.Fail(VerificationRung.Lifecycle, lifecycle) : VerificationStep.Pass(VerificationRung.Lifecycle),
            pages.Count == 0 ? VerificationStep.Skip(VerificationRung.DrawProbe)
                : page.Length > 0 ? VerificationStep.Fail(VerificationRung.DrawProbe, page)
                : VerificationStep.Pass(VerificationRung.DrawProbe),
        ]);

        if (picture is not null) LatestPicture = picture;

        // The page's measured layout: warnings the verdict leaves out, carried beside it. See
        // GateResult.Advisories.
        VerificationFinding[] advisories =
        [
            .. findings
                .Where(f => f.Severity == DriveSeverity.Warning && f.Code.StartsWith(LayoutPrefix, StringComparison.Ordinal))
                .Select(f => ToFinding(f, PageEntry)),
        ];

        return new GateResult(verdict, Compile: null) { Compiled = true, Picture = picture, Advisories = advisories };
    }

    /// <summary>The code prefix of the page probe's layout measurements.</summary>
    public const string LayoutPrefix = "page.layout.";

    /// <summary>A unit that did not compile, pass the scan or have the right shape, sorted onto the rung
    /// that refused it.</summary>
    private static VerificationReport Refused(BlocksCompileResult compiled)
    {
        var errors = compiled.Errors.ToArray();

        VerificationFinding Finding(StrategyDiagnostic d) => new(
            d.Id,
            string.IsNullOrEmpty(d.File) ? d.Message : $"{d.Location}: {d.Message}",
            d.Id.StartsWith("DAXSCAN", StringComparison.Ordinal)
                ? "Remove the call. Files, processes, threads and reflection are not available; use the state, schedule and network blocks."
                : "Fix the diagnostic. The line and column are in the message.",
            File: string.IsNullOrEmpty(d.File) ? null : d.File);

        var scan = errors.Where(d => d.Id.StartsWith("DAXSCAN", StringComparison.Ordinal)).Select(Finding).ToArray();
        var shape = errors.Where(d => d.Id.StartsWith("DAXB", StringComparison.Ordinal)).Select(Finding).ToArray();
        var compile = errors.Except(errors.Where(d => d.Id.StartsWith("DAX", StringComparison.Ordinal))).Select(Finding).ToArray();

        if (compile.Length > 0)
            return new VerificationReport([VerificationStep.Fail(VerificationRung.Compile, compile)]);

        if (scan.Length > 0)
            return new VerificationReport(
                [VerificationStep.Pass(VerificationRung.Compile), VerificationStep.Fail(VerificationRung.Policy, scan)]);

        return new VerificationReport(
        [
            VerificationStep.Pass(VerificationRung.Compile),
            VerificationStep.Pass(VerificationRung.Policy),
            VerificationStep.Fail(VerificationRung.Shape,
                shape.Length > 0 ? shape : [new VerificationFinding("compile.failed", "The unit did not compile.", "Read the diagnostics.")]),
        ]);
    }

    private static bool IsPage(string code) =>
        code.StartsWith("page.", StringComparison.Ordinal) || code == "ui.never-sent";

    /// <summary>The page file a probe message names — "(scene.js:42)" — as a unit file path, or null.</summary>
    public static string? PageFileIn(string message)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            message ?? string.Empty, @"\(([\w.\-/]+\.(?:m?js|html?|css)):\d+\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        var leaf = match.Groups[1].Value.Replace('\\', '/').TrimStart('/');
        return leaf.StartsWith("ui/", StringComparison.OrdinalIgnoreCase) ? leaf : "ui/" + leaf;
    }

    // ui.never-sent is the unit's silence, not the page's fault, so it names no file and reaches the C#.
    private static VerificationFinding ToFinding(DriveFinding finding, string? file) =>
        new(finding.Code, finding.Message, finding.Remedy, finding.Code == "ui.never-sent" ? null : file);
}
