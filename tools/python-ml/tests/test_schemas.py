from datetime import datetime, timezone

import pytest

from daxalgo_ml.schemas import (
    AnalystBar,
    AnalystReport,
    AnalystRequest,
    IndicatorReport,
    PatternReport,
    TrendReport,
)


def test_bar_count_must_be_positive():
    with pytest.raises(Exception):
        AnalystRequest(
            symbol="ES",
            timeframe="1h",
            bar_count=0,
            provider="openai",
            model="gpt-4o",
            vision_model="gpt-4o",
            bars=[],
        )


def test_confidence_is_clamped():
    p = PatternReport(pattern_name="None", confidence=1.5, reasoning="x")
    assert p.confidence == 1.0
    p2 = PatternReport(pattern_name="None", confidence=-0.2, reasoning="x")
    assert p2.confidence == 0.0


def test_unavailable_helper_returns_no_call():
    r = AnalystReport.unavailable("nope")
    assert r.decision == "no_call"
    assert r.pattern.pattern_name == "None"
    assert r.pattern_chart_png_base64 == ""


def test_bar_roundtrips_json():
    b = AnalystBar(
        timestamp_utc=datetime(2026, 1, 1, tzinfo=timezone.utc),
        open=1.0,
        high=2.0,
        low=0.5,
        close=1.5,
        volume=10,
    )
    j = b.model_dump_json()
    b2 = AnalystBar.model_validate_json(j)
    assert b == b2


def test_indicator_report_accepts_arbitrary_value_dict():
    r = IndicatorReport(summary="x", values={"rsi_14": 50.0, "macd": -0.2})
    assert r.values["rsi_14"] == 50.0


def test_trend_report_accepts_negative_slope():
    t = TrendReport(direction="Down", slope=-0.5, channel_upper=10, channel_lower=8, reasoning="x")
    assert t.slope == -0.5
