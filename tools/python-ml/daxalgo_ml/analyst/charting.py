"""Chart-rendering helpers — produce base64-encoded PNGs for the vision agents and the
WPF pane. We avoid showing the figure (no GUI on the user's box) and always close it
so matplotlib's memory doesn't grow across requests.
"""

from __future__ import annotations

import base64
import io
from dataclasses import dataclass

import matplotlib

matplotlib.use("Agg")  # headless backend; required before pyplot import

import matplotlib.pyplot as plt  # noqa: E402
import mplfinance as mpf  # noqa: E402
import pandas as pd  # noqa: E402


@dataclass(frozen=True)
class TrendChannel:
    upper_intercept: float
    upper_slope: float
    lower_intercept: float
    lower_slope: float
    bar_count: int


def bars_to_dataframe(bars: list[dict]) -> pd.DataFrame:
    """Convert the JSON bar list into the OHLCV DataFrame mplfinance wants."""
    df = pd.DataFrame(bars)
    df = df.rename(
        columns={
            "timestamp_utc": "Date",
            "open": "Open",
            "high": "High",
            "low": "Low",
            "close": "Close",
            "volume": "Volume",
        }
    )
    df["Date"] = pd.to_datetime(df["Date"], utc=True)
    return df.set_index("Date").sort_index()


def render_candles(df: pd.DataFrame, title: str = "") -> str:
    """Plain candlestick chart, base64 PNG."""
    fig, axes = mpf.plot(
        df,
        type="candle",
        style="charles",
        volume=False,
        returnfig=True,
        figratio=(12, 6),
        title=title,
    )
    return _fig_to_base64(fig)


def render_candles_with_trend(df: pd.DataFrame, channel: TrendChannel, title: str = "") -> str:
    """Candlestick chart with the fitted upper/lower trend channel overlaid."""
    fig, axes = mpf.plot(
        df,
        type="candle",
        style="charles",
        volume=False,
        returnfig=True,
        figratio=(12, 6),
        title=title,
    )
    ax = axes[0]
    xs = list(range(len(df)))
    upper = [channel.upper_intercept + channel.upper_slope * x for x in xs]
    lower = [channel.lower_intercept + channel.lower_slope * x for x in xs]
    ax.plot(xs, upper, linewidth=1.4, linestyle="--", label="Upper channel")
    ax.plot(xs, lower, linewidth=1.4, linestyle="--", label="Lower channel")
    ax.legend(loc="upper left", fontsize=8)
    return _fig_to_base64(fig)


def _fig_to_base64(fig) -> str:
    buf = io.BytesIO()
    try:
        fig.savefig(buf, format="png", bbox_inches="tight", dpi=120)
    finally:
        plt.close(fig)
    buf.seek(0)
    return base64.b64encode(buf.read()).decode("ascii")


__all__ = ["TrendChannel", "bars_to_dataframe", "render_candles", "render_candles_with_trend"]
