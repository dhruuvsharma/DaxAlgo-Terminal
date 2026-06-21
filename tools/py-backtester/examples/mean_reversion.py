"""Example Python strategy: rolling-window z-score mean reversion.

Mirrors the engine's native MeanReversionKernel — enter on extreme deviations, exit near the mean —
to show a researcher the whole loop in pure Python. Run it directly to act as a strategy host the
DaxAlgo engine drives, or point a Python-strategy descriptor at this file.

    python mean_reversion.py     # waits for the engine on stdin
"""

import os
import sys
from collections import deque

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from daxalgo_bt import Strategy, run


class MeanReversion(Strategy):
    def on_start(self):
        self.lookback = int(self.params.get("lookback", 50))
        self.entry_z = float(self.params.get("entryZ", 2.0))
        self.exit_z = float(self.params.get("exitZ", 0.5))
        self.qty = int(self.params.get("qty", 1))
        self.window = deque(maxlen=self.lookback)

    def on_quote(self, ts, bid, ask, position):
        mid = (bid + ask) / 2.0
        self.window.append(mid)
        if len(self.window) < self.lookback:
            return None

        mean = sum(self.window) / len(self.window)
        var = sum((x - mean) ** 2 for x in self.window) / len(self.window)
        sd = var ** 0.5
        if sd <= 0:
            return None
        z = (mid - mean) / sd

        if position == 0:
            if z <= -self.entry_z:
                return ("BUY", self.qty)
            if z >= self.entry_z:
                return ("SELL", self.qty)
        elif position > 0 and z >= -self.exit_z:
            return ("FLAT", 0)
        elif position < 0 and z <= self.exit_z:
            return ("FLAT", 0)
        return None


if __name__ == "__main__":
    run(MeanReversion)
