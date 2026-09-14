using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DaxAlgo.Blocks;
using FluentAssertions;
using TradingTerminal.Blocks.Runtime.Verification;
using TradingTerminal.Core.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Blocks.WebHost.Tests;

/// <summary>
/// A unit's real page in real WebView2, driven by the unit it ships with. Skipped on a machine without
/// the WebView2 runtime rather than failed, because the runtime is a user-machine prerequisite.
/// </summary>
public sealed class PageProbeTests
{
    private static readonly DriveOptions Quick = new(Steps: 30, SettleTime: TimeSpan.FromMilliseconds(400), PageReadyTimeout: TimeSpan.FromSeconds(20));

    [Fact]
    public async Task A_page_and_its_unit_talk_both_ways_and_the_page_is_photographed()
    {
        if (!WebUnitView.RuntimeAvailable) return;

        var pinged = 0;
        var unit = () => new PageUnit(context =>
        {
            context.Ui.OnOpened(() => context.Ui.Send("state", new { Value = 7, Label = "Spread" }));
            context.Ui.On("ping", payload => pinged = payload.GetProperty("n").GetInt32());
        });

        const string page = """
            <!doctype html>
            <html><head><link rel="stylesheet" href="style.css"></head>
            <body>
              <main><h1 id="label">waiting</h1><div id="value">—</div><div class="bar"></div></main>
              <script src="app.js"></script>
            </body></html>
            """;
        const string css = """
            body { margin: 0; background: #0e1117; color: #e6edf3; font-family: Segoe UI, sans-serif; }
            main { padding: 48px; } h1 { font-size: 28px; color: #7d8590; }
            #value { font-size: 120px; font-weight: 600; color: #3fb950; }
            .bar { height: 24px; width: 60%; background: linear-gradient(90deg, #1f6feb, #a371f7); border-radius: 6px; }
            """;
        const string js = """
            dax.on('state', s => {
              document.getElementById('label').textContent = s.label;
              document.getElementById('value').textContent = s.value;
              dax.send('ping', { n: s.value });
            });
            dax.ready();
            """;

        var report = await PageProbe.RunAsync(unit,
            [new StrategyFile("ui/index.html", page), new StrategyFile("ui/style.css", css), new StrategyFile("ui/app.js", js)],
            "talks", new PageProbeOptions(Drive: Quick));

        report.Findings.Should().NotContain(f => f.Severity == DriveSeverity.Failure, string.Join("\n", report.Findings));
        report.Passed.Should().BeTrue();
        pinged.Should().Be(7, "the page received the unit's state and answered it");
        report.Drive.PagePosts.Should().BeGreaterThan(0);
        report.Blank.Should().BeFalse();
        report.Png.Should().NotBeNull();

        // Set DAXALGO_PROBE_SNAPSHOTS to a folder to keep the picture and look at it.
        if (Environment.GetEnvironmentVariable("DAXALGO_PROBE_SNAPSHOTS") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(Path.Combine(folder, "talks.png"), report.Png!);
        }
    }

    [Fact]
    public async Task A_script_error_fails_with_its_message()
    {
        if (!WebUnitView.RuntimeAvailable) return;

        var unit = () => new PageUnit(context => context.Ui.OnOpened(() => context.Ui.Send("state", new { Value = 1 })));
        const string page = """
            <body style="background:#123;color:#fff"><p>content</p>
            <script>
              dax.on('state', s => { renderChart(s); });
              dax.ready();
            </script></body>
            """;

        var report = await PageProbe.RunAsync(unit, [new StrategyFile("ui/index.html", page)], "throws", new PageProbeOptions(Drive: Quick));

        var seen = string.Join(" / ", report.Findings) + " / errors: " + string.Join(" | ", report.PageErrors.Select(e => e.Message));
        report.Passed.Should().BeFalse(seen);
        report.Findings.Should().Contain(f => f.Code == "page.threw" && f.Message.Contains("renderChart"), seen);
    }

    [Fact]
    public async Task A_page_that_never_says_ready_fails()
    {
        if (!WebUnitView.RuntimeAvailable) return;

        var unit = () => new PageUnit(context => context.Ui.OnOpened(() => context.Ui.Send("state", new { Value = 1 })));
        const string page = """<body style="background:#123;color:#fff"><p>forgot dax.ready()</p></body>""";

        var report = await PageProbe.RunAsync(unit, [new StrategyFile("ui/index.html", page)], "unready",
            new PageProbeOptions(Drive: Quick with { PageReadyTimeout = TimeSpan.FromSeconds(4) }));

        report.Passed.Should().BeFalse();
        report.Findings.Should().Contain(f => f.Code == "page.never-ready");
    }

    [Fact]
    public async Task A_page_that_draws_nothing_fails_as_blank()
    {
        if (!WebUnitView.RuntimeAvailable) return;

        var unit = () => new PageUnit(context => context.Ui.OnOpened(() => context.Ui.Send("state", new { Value = 1 })));
        const string page = """<body style="background:#fff"><script>dax.on('state', () => {}); dax.ready();</script></body>""";

        var report = await PageProbe.RunAsync(unit, [new StrategyFile("ui/index.html", page)], "blank", new PageProbeOptions(Drive: Quick));

        report.Passed.Should().BeFalse();
        report.Findings.Should().Contain(f => f.Code == "page.blank");
    }

    [Fact]
    public async Task A_page_cannot_navigate_itself_off_the_units_own_host()
    {
        if (!WebUnitView.RuntimeAvailable) return;

        var stillHere = false;
        var unit = () => new PageUnit(context => context.Ui.On("still-here", _ => stillHere = true));
        const string page = """
            <body style="background:#123;color:#fff"><p>leaving?</p>
            <script>
              dax.ready();
              location.href = 'https://example.com/';
              setTimeout(() => dax.send('still-here', {}), 800);
            </script></body>
            """;

        var report = await PageProbe.RunAsync(unit, [new StrategyFile("ui/index.html", page)], "escape",
            new PageProbeOptions(Drive: Quick with { SettleTime = TimeSpan.FromMilliseconds(1500) }));

        stillHere.Should().BeTrue("the navigation was cancelled, so the page's own script kept running");
        report.PageErrors.Should().Contain(e => e.Message.Contains("example.com") && e.Message.Contains("blocked"));
    }

    [Fact]
    public void Blank_detection_tells_a_flat_picture_from_one_with_content()
    {
        PageProbe.IsBlank(null).Should().BeTrue();
        PageProbe.IsBlank(Png(64, 64, (_, _) => Colors.White)).Should().BeTrue();
        PageProbe.IsBlank(Png(64, 64, (x, _) => x < 32 ? Colors.Black : Colors.White)).Should().BeTrue("two colours is still too little to call content");
        PageProbe.IsBlank(Png(64, 64, (x, y) => x < 20 ? Colors.Black : y < 30 ? Colors.Orange : Colors.SteelBlue)).Should().BeFalse();
    }

    [Fact]
    public void Page_files_are_written_under_the_units_folder_and_cannot_climb_out()
    {
        var root = Path.Combine(Path.GetTempPath(), "daxalgo-page-folder-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var folder = UnitPageFolder.Write(root, "my unit", [new StrategyFile("ui/index.html", "<p>x</p>"), new StrategyFile("ui/js/app.js", "1")]);
            File.ReadAllText(Path.Combine(folder, "index.html")).Should().Be("<p>x</p>");
            File.Exists(Path.Combine(folder, "js", "app.js")).Should().BeTrue();

            var climb = () => UnitPageFolder.Write(root, "my unit", [new StrategyFile("ui/../../evil.html", "x")]);
            climb.Should().Throw<ArgumentException>();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static byte[] Png(int width, int height, Func<int, int, Color> colourAt)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var c = colourAt(x, y);
            var i = (y * width + x) * 4;
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (c.B, c.G, c.R, 255);
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
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
