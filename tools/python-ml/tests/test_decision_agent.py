import json

from daxalgo_ml.analyst.agents.decision import run_agent
from daxalgo_ml.schemas import IndicatorReport, PatternReport, TrendReport
from tests.conftest import FakeChatModel


def _reports():
    indicator = IndicatorReport(summary="rsi rising", values={"rsi_14": 60.0})
    pattern = PatternReport(pattern_name="Bull Flag", confidence=0.78, reasoning="tight consolidation")
    trend = TrendReport(direction="Up", slope=0.1, channel_upper=110, channel_lower=100, reasoning="up")
    return indicator, pattern, trend


def test_decision_agent_returns_long_on_clean_json():
    model = FakeChatModel(script=[json.dumps({
        "decision": "long",
        "forecast_horizon": "next 4 bars",
        "risk_reward_ratio": 2.1,
        "confidence": 0.72,
        "justification": "Pattern + trend align.",
    })])
    indicator, pattern, trend = _reports()
    report = run_agent(model, indicator, pattern, trend)
    assert report.decision == "long"
    assert report.risk_reward_ratio == 2.1
    assert report.indicator == indicator


def test_decision_agent_retries_on_invalid_json_then_falls_back():
    model = FakeChatModel(script=["not json", "still not json", "definitely not json"])
    indicator, pattern, trend = _reports()
    report = run_agent(model, indicator, pattern, trend)
    assert report.decision == "no_call"
    assert "3 retries" in report.justification


def test_decision_agent_succeeds_on_second_attempt():
    model = FakeChatModel(script=[
        "garbage",
        json.dumps({
            "decision": "short",
            "forecast_horizon": "next 2 bars",
            "risk_reward_ratio": 1.5,
            "confidence": 0.6,
            "justification": "second-attempt parse.",
        }),
    ])
    indicator, pattern, trend = _reports()
    report = run_agent(model, indicator, pattern, trend)
    assert report.decision == "short"
