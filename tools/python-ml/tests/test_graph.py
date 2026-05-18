"""End-to-end graph test using a stubbed provider factory so we never touch a real LLM."""

import json
from unittest.mock import patch

import pytest

from daxalgo_ml.analyst.graph import run_graph
from tests.conftest import FakeChatModel


@pytest.fixture
def stubbed_models(monkeypatch):
    indicator_reply = "RSI is rising into bullish territory."
    pattern_reply = json.dumps({
        "pattern_name": "Bull Flag", "confidence": 0.78, "reasoning": "consolidation post-trend"
    })
    trend_reply = json.dumps({"direction": "Up", "reasoning": "channel sloping up"})
    decision_reply = json.dumps({
        "decision": "long",
        "forecast_horizon": "next 4 bars",
        "risk_reward_ratio": 2.0,
        "confidence": 0.7,
        "justification": "All three agents align bullishly.",
    })

    text_model = FakeChatModel(script=[indicator_reply, decision_reply])
    vision_model = FakeChatModel(script=[pattern_reply, trend_reply])

    monkeypatch.setattr("daxalgo_ml.analyst.graph.build_text_model", lambda _cfg: text_model)
    monkeypatch.setattr("daxalgo_ml.analyst.graph.build_vision_model", lambda _cfg: vision_model)

    # Also reset the compiled graph cache so the test sees a fresh build (no real impact
    # since the graph wiring is stateless, but defensive).
    import daxalgo_ml.analyst.graph as g
    g._GRAPH = None
    return text_model, vision_model


def test_graph_runs_end_to_end(synthetic_bars, stubbed_models):
    report = run_graph(
        bars=synthetic_bars,
        provider="openai",
        api_key="fake",
        model="gpt-4o",
        vision_model="gpt-4o",
    )
    assert report.decision == "long"
    assert report.pattern.pattern_name == "Bull Flag"
    assert report.trend.direction == "Up"
    assert report.pattern_chart_png_base64
    assert report.trend_chart_png_base64
    assert report.elapsed_ms >= 0
