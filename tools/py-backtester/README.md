# daxalgo_bt — Python strategy authoring SDK

Write DaxAlgo backtest strategies in Python. The C# engine owns data, orders, fills, costs, and the
report; your Python decides *what to do* per quote given your current position. This is the headline
path for distributing DaxAlgo to quant researchers who live in Python.

## Write a strategy

```python
from daxalgo_bt import Strategy, run

class MyStrategy(Strategy):
    def on_start(self):
        self.qty = int(self.params.get("qty", 1))

    def on_quote(self, ts, bid, ask, position):
        # return ("BUY", n), ("SELL", n), ("FLAT", 0), or None
        ...

if __name__ == "__main__":
    run(MyStrategy)
```

See `examples/mean_reversion.py` for a complete strategy mirroring the engine's native kernel.

## Run it from the Studio

Drop your `.py` file into `python-strategies/` next to the DaxAlgo app. The Backtest Studio discovers
it on startup and lists it as `Python: <filename>`; pick it like any other strategy. It runs with the
parameter defaults in your `on_start` for now (a per-strategy parameter manifest — to expose tunables
in the Studio UI — is a planned refinement).

Requires a Python interpreter on `PATH` (`python` or `python3`). No third-party packages — the SDK is
stdlib only.

## How it works

The engine drives your strategy over stdin/stdout, one line per event:

| Direction | Message |
|---|---|
| engine → py | `START {json params}` · `Q <ts> <bid> <ask> <position>` · `END` |
| py → engine | `READY` · (`BUY n` \| `SELL n` \| `FLAT` \| `NONE`) · `DONE` |

The per-quote round-trip is slower than a native C# kernel — fine for research. For large parameter
sweeps, use a native or GPU-portable kernel.
