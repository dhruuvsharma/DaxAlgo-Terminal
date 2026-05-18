"""Trend agent — fits a linear channel on the recent highs and lows with simple outlier
rejection, renders an annotated candlestick PNG, and asks the vision LLM to interpret
the regime.
"""

from __future__ import annotations

import json
from typing import Any

import numpy as np
import pandas as pd
from langchain_core.messages import HumanMessage, SystemMessage
from scipy import stats
from tenacity import retry, stop_after_attempt, wait_exponential, retry_if_exception_type

from ...schemas import TrendReport
from ..charting import TrendChannel, render_candles_with_trend


_SYSTEM_PROMPT = (
    "You are a technical analyst describing the trend regime of a candlestick chart that "
    "has a fitted linear channel overlaid. Return STRICT JSON only — no prose, no markdown "
    'fences — with shape: {"direction": one of [\"Up\", \"Down\", \"Flat\"], '
    '"reasoning": str}. Use \"Flat\" when the channel is roughly horizontal.'
)


def fit_channel(df: pd.DataFrame) -> TrendChannel:
    """Fit upper line on highs and lower line on lows via least-squares, dropping any
    point more than 2 sigma off the first fit (one iteration of outlier rejection)."""
    n = len(df)
    xs = np.arange(n, dtype=float)
    highs = df["High"].to_numpy(dtype=float)
    lows = df["Low"].to_numpy(dtype=float)

    upper_slope, upper_intercept = _fit_with_rejection(xs, highs)
    lower_slope, lower_intercept = _fit_with_rejection(xs, lows)

    return TrendChannel(
        upper_intercept=float(upper_intercept),
        upper_slope=float(upper_slope),
        lower_intercept=float(lower_intercept),
        lower_slope=float(lower_slope),
        bar_count=n,
    )


def _fit_with_rejection(xs: np.ndarray, ys: np.ndarray) -> tuple[float, float]:
    if len(xs) < 3:
        return 0.0, float(ys[-1]) if len(ys) else 0.0
    slope, intercept, *_ = stats.linregress(xs, ys)
    residuals = ys - (intercept + slope * xs)
    sigma = residuals.std(ddof=1) or 1e-9
    mask = np.abs(residuals) <= 2.0 * sigma
    if mask.sum() >= 3 and mask.sum() < len(xs):
        slope, intercept, *_ = stats.linregress(xs[mask], ys[mask])
    return float(slope), float(intercept)


@retry(
    reraise=True,
    stop=stop_after_attempt(3),
    wait=wait_exponential(multiplier=1, min=1, max=8),
    retry=retry_if_exception_type(Exception),
)
def _invoke_with_retry(model: Any, messages: list) -> Any:
    return model.invoke(messages)


def run_agent(vision_model: Any, df: pd.DataFrame) -> tuple[TrendReport, str]:
    channel = fit_channel(df)
    png_b64 = render_candles_with_trend(df, channel, title="Trend view")

    avg_slope = (channel.upper_slope + channel.lower_slope) / 2
    user_text = (
        f"Upper-channel slope per bar: {channel.upper_slope:.4f}.\n"
        f"Lower-channel slope per bar: {channel.lower_slope:.4f}.\n"
        f"Average slope: {avg_slope:.4f}.\n"
        "Describe the trend and respond with strict JSON only."
    )
    content = [
        {"type": "text", "text": user_text},
        {"type": "image_url", "image_url": {"url": f"data:image/png;base64,{png_b64}"}},
    ]

    try:
        resp = _invoke_with_retry(
            vision_model,
            [SystemMessage(content=_SYSTEM_PROMPT), HumanMessage(content=content)],
        )
        raw = (getattr(resp, "content", "") or "").strip()
        direction, reasoning = _parse_trend_json(raw, avg_slope)
    except Exception as ex:  # noqa: BLE001
        direction = _slope_to_direction(avg_slope)
        reasoning = f"Vision trend agent failed: {ex}. Slope-based fallback."

    last_x = max(0, channel.bar_count - 1)
    channel_upper_last = channel.upper_intercept + channel.upper_slope * last_x
    channel_lower_last = channel.lower_intercept + channel.lower_slope * last_x

    report = TrendReport(
        direction=direction,
        slope=avg_slope,
        channel_upper=float(channel_upper_last),
        channel_lower=float(channel_lower_last),
        reasoning=reasoning,
    )
    return report, png_b64


def _parse_trend_json(raw: str, slope_hint: float) -> tuple[str, str]:
    text = raw.strip()
    if text.startswith("```"):
        text = text.strip("`")
        if text.lower().startswith("json"):
            text = text[4:]
        text = text.strip()
    obj = json.loads(text)
    direction = str(obj.get("direction") or _slope_to_direction(slope_hint))
    if direction not in ("Up", "Down", "Flat"):
        direction = _slope_to_direction(slope_hint)
    reasoning = str(obj.get("reasoning") or "").strip()
    return direction, reasoning


def _slope_to_direction(slope: float) -> str:
    if slope > 1e-6:
        return "Up"
    if slope < -1e-6:
        return "Down"
    return "Flat"


__all__ = ["fit_channel", "run_agent"]
