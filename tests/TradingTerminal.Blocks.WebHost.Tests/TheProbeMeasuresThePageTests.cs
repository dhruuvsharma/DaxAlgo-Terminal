using DaxAlgo.Blocks;
using FluentAssertions;
using TradingTerminal.Blocks.Runtime.Verification;
using TradingTerminal.Core.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Blocks.WebHost.Tests;

/// <summary>
/// The page probe measures the page's layout in the real browser — the look a text-only model cannot
/// see. Measured faults are warnings: they never fail a page, and a page that fits its window, keeps its
/// main view clear and prints real numbers measures clean.
/// </summary>
public sealed class TheProbeMeasuresThePageTests
{
    private static readonly DriveOptions Quick = new(Steps: 30, SettleTime: TimeSpan.FromMilliseconds(400), PageReadyTimeout: TimeSpan.FromSeconds(20));

    private static Func<IUnit> Unit() => () => new PageUnit(context => context.Ui.OnOpened(() => context.Ui.Send("state", new { Value = 64_250.5 })));

    /// <summary>A full-window canvas painted from script, the way a scene is.</summary>
    private const string Scene = """
        <canvas id="scene" style="position:fixed;inset:0;width:100%;height:100%"></canvas>
        <script>
          const c = document.getElementById('scene');
          c.width = innerWidth; c.height = innerHeight;
          const g = c.getContext('2d');
          const grad = g.createLinearGradient(0, 0, c.width, c.height);
          grad.addColorStop(0, '#0b1a3a'); grad.addColorStop(1, '#3a0b2a');
          g.fillStyle = grad; g.fillRect(0, 0, c.width, c.height);
        </script>
        """;

    private static async Task<IReadOnlyList<DriveFinding>> Measure(string body, string id)
    {
        var page = $"""
            <!doctype html>
            <html><head><style>html,body{"{"}margin:0;background:#05070d;color:#e6edf3;font:14px Segoe UI,sans-serif{"}"}</style></head>
            <body>{body}
            <script>dax.on('state', s => {"{"} for (const el of document.querySelectorAll('[data-value]')) el.textContent = s.value.toFixed(1); {"}"}); dax.ready();</script>
            </body></html>
            """;

        var report = await PageProbe.RunAsync(Unit(), [new StrategyFile("ui/index.html", page)], id, new PageProbeOptions(Drive: Quick));
        report.Findings.Should().NotContain(f => f.Severity == DriveSeverity.Failure, string.Join("\n", report.Findings));
        return [.. report.Findings.Where(f => f.Code.StartsWith(PageLayoutAudit.CodePrefix, StringComparison.Ordinal))];
    }

    [Fact]
    public async Task A_panel_covering_the_scene_a_page_that_scrolls_and_a_NaN_are_measured_and_the_page_still_passes()
    {
        if (!WebUnitView.RuntimeAvailable) return;

        // The delivered Nemotron Battlefield page, reduced: a depth chart over the whole scene, a page
        // taller than its window, and a number that was never set.
        var findings = await Measure(Scene + """
            <div id="depth-root" style="position:absolute;left:3%;top:3%;width:94%;height:94%;background:#0b0f17">
              <canvas style="width:100%;height:100%"></canvas>
            </div>
            <div style="position:absolute;top:0;left:0;height:1900px;width:10px"></div>
            <span id="price" style="position:fixed;top:8px;left:8px">NaN</span>
            """, "covered");

        var seen = string.Join("\n", findings);
        findings.Should().Contain(f => f.Code == "page.layout.covered" && f.Message.Contains("#depth-root") && f.Message.Contains("#scene"), seen);
        findings.Should().Contain(f => f.Code == "page.layout.scrolls", seen);
        findings.Should().Contain(f => f.Code == "page.layout.broken-value" && f.Message.Contains("\"NaN\" in #price"), seen);
        findings.Should().OnlyContain(f => f.Severity == DriveSeverity.Warning);
    }

    [Fact]
    public async Task A_hud_docked_around_a_scene_that_fits_its_window_measures_clean()
    {
        if (!WebUnitView.RuntimeAvailable) return;

        // Translucent columns over the scene, a solid depth panel docked in a corner, and a feed longer
        // than its panel that scrolls inside it — the Liquidity Wars layout, which is fine.
        var findings = await Measure(Scene + """
            <style>body{overflow:hidden;height:100vh} .card{background:rgba(6,10,22,0.66);border:1px solid #234;padding:8px}</style>
            <aside class="card" style="position:absolute;left:12px;top:12px;width:22%;bottom:12px">
              <div>Price <b data-value>—</b></div><div>Pressure <b>+29</b></div>
            </aside>
            <aside class="card" style="position:absolute;right:12px;top:12px;width:24%;height:40%;overflow:hidden">
              <div>Feed</div>
              <div>$612K bought @ 64,180</div><div>$402K sold @ 64,214</div><div>$188K shorts liquidated</div>
              <div style="height:2000px">older events…</div>
            </aside>
            <section id="depth" style="position:absolute;left:25%;bottom:12px;width:30%;height:28%;background:#0b0f17">
              <canvas style="width:100%;height:100%"></canvas>
            </section>
            """, "clean");

        findings.Should().BeEmpty(string.Join("\n", findings));
    }

    [Fact]
    public async Task Text_run_off_the_window_and_text_drawn_over_text_are_measured()
    {
        if (!WebUnitView.RuntimeAvailable) return;

        var findings = await Measure(Scene + """
            <style>body{overflow:hidden;height:100vh}</style>
            <div id="ticker" style="position:absolute;right:-180px;top:40px;width:360px;background:#111">BTC 64,250.5 · ETH 3,120.4 · SOL 182.3</div>
            <span id="bid" style="position:absolute;left:200px;top:300px;font-size:20px">Bid wall $42.1M</span>
            <span id="ask" style="position:absolute;left:204px;top:302px;font-size:20px">Ask wall $39.1M</span>
            """, "offscreen");

        var seen = string.Join("\n", findings);
        findings.Should().Contain(f => f.Code == "page.layout.offscreen" && f.Message.Contains("#ticker"), seen);
        findings.Should().Contain(f => f.Code == "page.layout.overlap" && f.Message.Contains("#bid") && f.Message.Contains("#ask"), seen);
    }

    [Fact]
    public async Task A_3D_scene_squeezed_into_a_strip_by_a_larger_panel_is_measured()
    {
        if (!WebUnitView.RuntimeAvailable) return;

        // What the Nemotron page actually did: the scene's canvas got a strip at the top, and the depth
        // chart took the window. Nothing covers the scene — it is simply small.
        var findings = await Measure("""
            <style>body{overflow:hidden;height:100vh}</style>
            <canvas id="scene" data-engine="three.js r170" style="display:block;width:100%;height:140px;background:#123"></canvas>
            <div id="depth-root" style="height:calc(100vh - 140px)"><canvas style="width:100%;height:100%;background:#0b0f17"></canvas></div>
            <span style="position:fixed;left:12px;top:160px;color:#e6edf3">Depth <b data-value>—</b></span>
            """, "squeezed");

        var seen = string.Join("\n", findings);
        findings.Should().Contain(f => f.Code == "page.layout.squeezed" && f.Message.Contains("#scene") && f.Message.Contains("#depth-root"), seen);
    }

    [Fact]
    public async Task A_page_that_loads_three_js_but_never_starts_a_renderer_is_measured_with_what_it_logged()
    {
        if (!WebUnitView.RuntimeAvailable) return;

        // The Nemotron page's scene: its WebGL set-up threw, the page caught it, logged it and drew a 2D
        // strip instead — no script error ever reached the gate.
        var findings = await Measure("""
            <script type="importmap">{ "imports": { "three": "https://cdn.jsdelivr.net/npm/three@0.170.0/build/three.module.js" } }</script>
            <canvas id="fallback" style="display:block;width:100%;height:140px;background:#123"></canvas>
            <p>Price <b data-value>—</b></p>
            <script>
              try { throw new TypeError("geometry.setAttribute is not a function"); }
              catch (err) { console.error("WebGL init failed:", err); }
            </script>
            """, "no-scene");

        var seen = string.Join("\n", findings);
        findings.Should().Contain(f => f.Code == "page.layout.no-scene"
                                       && f.Message.Contains("WebGL init failed")
                                       && f.Message.Contains("geometry.setAttribute is not a function"), seen);
    }

    [Fact]
    public async Task A_page_that_replaces_the_bridge_is_told_so_when_it_never_becomes_ready()
    {
        if (!WebUnitView.RuntimeAvailable) return;

        // The Nemotron page's app.js, reduced: its own dax, whose ready() posts to nothing.
        const string page = """
            <body style="background:#123;color:#fff"><p>battlefield</p>
            <script type="module">
              const dax = { on() {}, ready() { window.daxHost?.postMessage({ topic: 'ready' }, '*'); } };
              window.dax = dax;
              dax.ready();
            </script></body>
            """;

        var report = await PageProbe.RunAsync(Unit(), [new StrategyFile("ui/index.html", page)], "own-bridge",
            new PageProbeOptions(Drive: Quick with { PageReadyTimeout = TimeSpan.FromSeconds(4) }));

        var notReady = report.Findings.Single(f => f.Code == "page.never-ready");
        notReady.Message.Should().Contain("no longer the terminal's bridge");
    }

    [Fact]
    public void What_the_page_reported_is_read_whether_WebView2_returns_it_as_an_object_or_a_string()
    {
        const string measured = """
            { "width": 1280, "height": 800, "scroll": { "width": 1280, "height": 1650 },
              "covered": [ { "element": "#depth-root", "main": "#scene", "percent": 94 } ],
              "broken": [], "offscreen": [], "overlap": [] }
            """;

        var direct = PageLayoutAudit.Read(measured);
        var encoded = PageLayoutAudit.Read(System.Text.Json.JsonSerializer.Serialize(measured));

        direct.Select(f => f.Code).Should().Equal("page.layout.scrolls", "page.layout.covered");
        encoded.Select(f => f.Code).Should().Equal(direct.Select(f => f.Code));
        direct[0].Message.Should().Contain("1280×1650 in a 1280×800 window");
        direct[1].Message.Should().Contain("#depth-root covers 94%");

        PageLayoutAudit.Read(null).Should().BeEmpty();
        PageLayoutAudit.Read("null").Should().BeEmpty();
        PageLayoutAudit.Read("not json").Should().BeEmpty();
    }

    private sealed class PageUnit(Action<IUnitContext> start) : IUnit
    {
        public UnitInfo Info { get; } = new("Page unit");

        public Task StartAsync(IUnitContext context, CancellationToken ct)
        {
            start(context);
            return Task.CompletedTask;
        }
    }
}
