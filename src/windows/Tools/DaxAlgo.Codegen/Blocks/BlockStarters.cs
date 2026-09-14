using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Blocks;

/// <summary>
/// The starter a new Blocks unit opens on, in the authoring pane and in an agent CLI workspace alike.
///
/// <para>One copy for both entry points, because two copies is how the workspace went on teaching a
/// retired contract long after the pane had moved on. Each starter compiles, and shows the three things
/// every unit does: read a setting, subscribe, send its page whole state.</para>
/// </summary>
public static class BlockStarters
{
    /// <summary>The C# starter for a unit of <paramref name="kind"/>.</summary>
    public static string For(AuthoringKind kind) => kind == AuthoringKind.Visualizer ? Visualizer : Strategy;

    /// <summary>True when <paramref name="content"/> is one of the C# starters, untouched.</summary>
    public static bool IsStarter(string? content) => content is Strategy or Visualizer;

    public const string Strategy = """
        // A unit built from blocks. Already imported: System, System.Collections.Generic, System.Linq,
        // System.Net.Http, System.Text.Json, System.Threading(.Tasks), DaxAlgo.Blocks, DaxAlgo.Sdk.Quant,
        // TradingTerminal.Core.Domain / MarketData / Strategies.Parameters.
        //
        // Exactly ONE public class implementing IUnit. Using context.Orders makes it a strategy.
        // Its look is ui/index.html (add it with +): the page receives what the unit sends with
        // context.Ui.Send through dax.on(topic, payload => …), and calls dax.ready() once it listens.

        public sealed class MyUnit : IUnit
        {
            public UnitInfo Info { get; } = new("My strategy", "Long above its average, short below.",
            [
                StrategyParameter.Instrument("instrument", "Instrument", new InstrumentId(1)),
                StrategyParameter.Int("lookback", "Look-back", 20, min: 2, max: 500),
            ]);

            private Ema _average = new(20);

            public Task StartAsync(IUnitContext context, CancellationToken ct)
            {
                var instrument = context.Settings.Instrument("instrument");
                _average = new Ema(context.Settings.Int("lookback"));

                context.Market.OnBar(instrument, BarSize.OneMinute, bar =>
                {
                    _average.Update(bar.Close);
                    if (_average.IsReady) context.Orders.SetTarget(instrument, bar.Close > _average.Value ? 1 : -1);
                    context.Ui.Send("state", new { close = bar.Close, average = _average.IsReady ? _average.Value : 0 });
                });

                return Task.CompletedTask;
            }
        }
        """;

    public const string Visualizer = """
        // A unit built from blocks. Already imported: System, System.Collections.Generic, System.Linq,
        // System.Net.Http, System.Text.Json, System.Threading(.Tasks), DaxAlgo.Blocks, DaxAlgo.Sdk.Quant,
        // TradingTerminal.Core.Domain / MarketData / Strategies.Parameters.
        //
        // Exactly ONE public class implementing IUnit. It never uses context.Orders, so it is a visualizer.
        // Its look is ui/index.html (add it with +): the page receives what the unit sends with
        // context.Ui.Send through dax.on(topic, payload => …), and calls dax.ready() once it listens.

        public sealed class MyUnit : IUnit
        {
            public UnitInfo Info { get; } = new("My visualizer", "The last price against its average.",
            [
                StrategyParameter.Instrument("instrument", "Instrument", new InstrumentId(1)),
                StrategyParameter.Int("lookback", "Look-back", 20, min: 2, max: 500),
            ]);

            private Ema _average = new(20);

            public Task StartAsync(IUnitContext context, CancellationToken ct)
            {
                var instrument = context.Settings.Instrument("instrument");
                _average = new Ema(context.Settings.Int("lookback"));

                context.Market.OnBar(instrument, BarSize.OneMinute, bar =>
                {
                    _average.Update(bar.Close);
                    context.Ui.Send("state", new { close = bar.Close, average = _average.IsReady ? _average.Value : 0 });
                });

                return Task.CompletedTask;
            }
        }
        """;

    /// <summary>The page that goes with either starter: it listens for <c>state</c> and draws it.</summary>
    public const string Page = """
        <!doctype html>
        <html>
        <head>
          <meta charset="utf-8">
          <style>
            body { margin: 0; background: #0e1117; color: #e6edf3; font: 14px "Segoe UI", sans-serif; }
            main { padding: 24px; display: grid; gap: 6px; }
            .label { color: #7d8590; font-size: 12px; text-transform: uppercase; letter-spacing: .06em; }
            .value { font-size: 48px; font-weight: 600; font-variant-numeric: tabular-nums; }
          </style>
        </head>
        <body>
          <main>
            <div class="label">Close</div>
            <div class="value" id="close">waiting…</div>
            <div class="label">Average <span id="average">–</span></div>
          </main>
          <script>
            dax.on("state", s => {
              document.getElementById("close").textContent = s.close.toFixed(2);
              document.getElementById("average").textContent = s.average ? s.average.toFixed(2) : "–";
            });
            dax.ready();
          </script>
        </body>
        </html>
        """;
}
