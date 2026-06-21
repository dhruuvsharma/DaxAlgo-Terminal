"""daxalgo_bt — author DaxAlgo backtest strategies in Python.

A strategy subclasses :class:`Strategy` and implements ``on_quote`` (and optionally ``on_start``).
The DaxAlgo engine (C#) drives it over stdin/stdout: it streams quotes in and reads back an order
action per quote. Orders, fills, accounting, costs, and the report all stay in the C# engine — your
Python only decides *what to do* given the latest quote and your current position.

Run a strategy module with::

    from daxalgo_bt import Strategy, run

    class MyStrategy(Strategy):
        def on_start(self):
            self.qty = int(self.params.get("qty", 1))
        def on_quote(self, ts, bid, ask, position):
            ...
            return ("BUY", self.qty)   # or ("SELL", n), ("FLAT", 0), or None

    if __name__ == "__main__":
        run(MyStrategy)

The wire protocol (one line each) is deliberately trivial so the host stays robust:
  in : ``START {json params}`` · ``Q <ts> <bid> <ask> <position>`` · ``END``
  out: ``READY`` · (``BUY n`` | ``SELL n`` | ``FLAT`` | ``NONE``) · ``DONE``
"""

import json
import sys


class Strategy:
    """Base class for a Python-authored backtest strategy."""

    def __init__(self, params):
        # params: dict[str, float] supplied by the run (the tunables).
        self.params = params or {}

    def on_start(self):
        """Called once before any quotes. Read parameters, set up state."""

    def on_quote(self, ts, bid, ask, position):
        """Called for each quote. ``ts`` is unix seconds, ``position`` is the current signed size.

        Return one of: ``("BUY", qty)``, ``("SELL", qty)``, ``("FLAT", 0)``, or ``None`` to do nothing.
        """
        return None


def _format(action):
    if not action:
        return "NONE"
    side, qty = action
    side = str(side).upper()
    if side == "FLAT":
        return "FLAT"
    return f"{side} {int(qty)}"


def run(strategy_cls):
    """Host loop: read events from the engine on stdin, write order actions on stdout."""
    out = sys.stdout
    strat = None
    for raw in sys.stdin:
        line = raw.strip()
        if not line:
            continue
        if line.startswith("START"):
            payload = line[len("START"):].strip() or "{}"
            strat = strategy_cls(json.loads(payload))
            strat.on_start()
            out.write("READY\n")
            out.flush()
        elif line.startswith("Q "):
            p = line.split()
            ts, bid, ask, pos = float(p[1]), float(p[2]), float(p[3]), int(p[4])
            action = strat.on_quote(ts, bid, ask, pos) if strat else None
            out.write(_format(action) + "\n")
            out.flush()
        elif line == "END":
            out.write("DONE\n")
            out.flush()
            break
