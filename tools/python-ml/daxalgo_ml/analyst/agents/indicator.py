"""Indicator agent — computes a fixed indicator panel with TA-Lib, then asks the text
LLM to summarise the readings in plain English.

If TA-Lib is not installed we fall back to numpy-only implementations so unit tests
can still exercise the agent. The C# side never sees this distinction.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any

import numpy as np
import pandas as pd
from langchain_core.messages import HumanMessage, SystemMessage
from tenacity import retry, stop_after_attempt, wait_exponential, retry_if_exception_type

from ...schemas import IndicatorReport

try:
    import talib  # type: ignore

    _HAS_TALIB = True
except ImportError:  # pragma: no cover — TA-Lib is a C-extension, optional in tests
    _HAS_TALIB = False


_SYSTEM_PROMPT = (
    "You are a concise quantitative trading analyst. Given a panel of technical indicator "
    "readings on a single instrument, write ONE short paragraph (3-5 sentences) describing "
    "the regime: overbought/oversold, trending vs ranging, volatility expansion vs "
    "compression. Do not give buy/sell advice. Do not include disclaimers."
)


@dataclass(frozen=True)
class IndicatorPanel:
    rsi_14: float
    macd: float
    macd_signal: float
    macd_hist: float
    atr_14: float
    ema_20: float
    ema_50: float
    last_close: float

    def to_dict(self) -> dict[str, float]:
        return {
            "rsi_14": self.rsi_14,
            "macd": self.macd,
            "macd_signal": self.macd_signal,
            "macd_hist": self.macd_hist,
            "atr_14": self.atr_14,
            "ema_20": self.ema_20,
            "ema_50": self.ema_50,
            "last_close": self.last_close,
        }


def compute_panel(df: pd.DataFrame) -> IndicatorPanel:
    """OHLC DataFrame in (with Date index, columns Open/High/Low/Close/Volume)."""
    close = df["Close"].to_numpy(dtype=float)
    high = df["High"].to_numpy(dtype=float)
    low = df["Low"].to_numpy(dtype=float)

    if _HAS_TALIB:
        rsi = talib.RSI(close, timeperiod=14)
        macd, macd_signal, macd_hist = talib.MACD(close)
        atr = talib.ATR(high, low, close, timeperiod=14)
        ema_20 = talib.EMA(close, timeperiod=20)
        ema_50 = talib.EMA(close, timeperiod=50)
    else:
        rsi = _rsi_numpy(close, 14)
        macd, macd_signal, macd_hist = _macd_numpy(close)
        atr = _atr_numpy(high, low, close, 14)
        ema_20 = _ema_numpy(close, 20)
        ema_50 = _ema_numpy(close, 50)

    return IndicatorPanel(
        rsi_14=_last_finite(rsi),
        macd=_last_finite(macd),
        macd_signal=_last_finite(macd_signal),
        macd_hist=_last_finite(macd_hist),
        atr_14=_last_finite(atr),
        ema_20=_last_finite(ema_20),
        ema_50=_last_finite(ema_50),
        last_close=float(close[-1]),
    )


@retry(
    reraise=True,
    stop=stop_after_attempt(3),
    wait=wait_exponential(multiplier=1, min=1, max=8),
    retry=retry_if_exception_type(Exception),
)
def _invoke_with_retry(model: Any, messages: list) -> Any:
    return model.invoke(messages)


def run_agent(text_model: Any, df: pd.DataFrame) -> IndicatorReport:
    panel = compute_panel(df)
    user_prompt = (
        "Indicator panel:\n"
        f"- RSI(14): {panel.rsi_14:.2f}\n"
        f"- MACD: {panel.macd:.4f} signal {panel.macd_signal:.4f} hist {panel.macd_hist:.4f}\n"
        f"- ATR(14): {panel.atr_14:.4f}\n"
        f"- EMA20: {panel.ema_20:.4f} EMA50: {panel.ema_50:.4f}\n"
        f"- Last close: {panel.last_close:.4f}\n"
        "Summarise the regime."
    )

    try:
        resp = _invoke_with_retry(
            text_model,
            [SystemMessage(content=_SYSTEM_PROMPT), HumanMessage(content=user_prompt)],
        )
        summary = (getattr(resp, "content", "") or "").strip() or "No commentary produced."
    except Exception as ex:  # noqa: BLE001
        summary = f"Indicator summary unavailable: {ex}"

    return IndicatorReport(summary=summary, values=panel.to_dict())


# --- numpy fallbacks (used only when TA-Lib isn't installed) -------------------


def _last_finite(arr) -> float:
    a = np.asarray(arr, dtype=float)
    finite = a[np.isfinite(a)]
    return float(finite[-1]) if finite.size else float("nan")


def _ema_numpy(x: np.ndarray, period: int) -> np.ndarray:
    alpha = 2.0 / (period + 1)
    out = np.empty_like(x, dtype=float)
    out[:] = np.nan
    if len(x) == 0:
        return out
    out[0] = x[0]
    for i in range(1, len(x)):
        out[i] = alpha * x[i] + (1 - alpha) * out[i - 1]
    return out


def _rsi_numpy(x: np.ndarray, period: int) -> np.ndarray:
    deltas = np.diff(x)
    gains = np.where(deltas > 0, deltas, 0.0)
    losses = np.where(deltas < 0, -deltas, 0.0)
    out = np.full_like(x, np.nan, dtype=float)
    if len(gains) < period:
        return out
    avg_gain = gains[:period].mean()
    avg_loss = losses[:period].mean()
    for i in range(period, len(x)):
        if i > period:
            avg_gain = (avg_gain * (period - 1) + gains[i - 1]) / period
            avg_loss = (avg_loss * (period - 1) + losses[i - 1]) / period
        rs = avg_gain / avg_loss if avg_loss > 0 else float("inf")
        out[i] = 100.0 - (100.0 / (1.0 + rs))
    return out


def _macd_numpy(x: np.ndarray, fast: int = 12, slow: int = 26, signal: int = 9):
    ema_fast = _ema_numpy(x, fast)
    ema_slow = _ema_numpy(x, slow)
    macd = ema_fast - ema_slow
    sig = _ema_numpy(macd, signal)
    hist = macd - sig
    return macd, sig, hist


def _atr_numpy(high: np.ndarray, low: np.ndarray, close: np.ndarray, period: int) -> np.ndarray:
    tr = np.maximum.reduce([
        high - low,
        np.abs(high - np.roll(close, 1)),
        np.abs(low - np.roll(close, 1)),
    ])
    tr[0] = high[0] - low[0]
    out = np.full_like(close, np.nan, dtype=float)
    if len(tr) < period:
        return out
    out[period - 1] = tr[:period].mean()
    for i in range(period, len(tr)):
        out[i] = (out[i - 1] * (period - 1) + tr[i]) / period
    return out


__all__ = ["IndicatorPanel", "compute_panel", "run_agent"]
