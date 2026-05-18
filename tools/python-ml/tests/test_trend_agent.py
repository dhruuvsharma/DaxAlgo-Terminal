import json

import numpy as np
import pandas as pd

from daxalgo_ml.analyst.agents.trend import fit_channel, run_agent
from tests.conftest import FakeChatModel


def test_fit_channel_upper_above_lower(synthetic_df):
    channel = fit_channel(synthetic_df)
    last_x = channel.bar_count - 1
    upper_last = channel.upper_intercept + channel.upper_slope * last_x
    lower_last = channel.lower_intercept + channel.lower_slope * last_x
    assert upper_last > lower_last


def test_fit_channel_picks_up_strong_uptrend():
    n = 30
    df = pd.DataFrame({
        "Open": np.linspace(100, 130, n),
        "High": np.linspace(101, 132, n),
        "Low": np.linspace(99, 128, n),
        "Close": np.linspace(100, 131, n),
        "Volume": np.full(n, 100),
    }, index=pd.date_range("2026-01-01", periods=n, freq="h", tz="UTC"))
    df.index.name = "Date"

    channel = fit_channel(df)
    assert channel.upper_slope > 0
    assert channel.lower_slope > 0


def test_trend_agent_parses_strict_json(synthetic_df):
    model = FakeChatModel(script=[json.dumps({
        "direction": "Up",
        "reasoning": "Channel sloping up.",
    })])
    report, png_b64 = run_agent(model, synthetic_df)
    assert report.direction == "Up"
    assert "channel" in report.reasoning.lower()
    assert png_b64


def test_trend_agent_falls_back_to_slope_when_llm_fails(synthetic_df):
    model = FakeChatModel(script=["not json at all"])
    report, _ = run_agent(model, synthetic_df)
    assert report.direction in ("Up", "Down", "Flat")
    assert "failed" in report.reasoning.lower() or report.reasoning
