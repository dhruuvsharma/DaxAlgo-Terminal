import json

from daxalgo_ml.analyst.agents.pattern import PATTERN_CATALOG, run_agent
from tests.conftest import FakeChatModel


def test_pattern_agent_parses_strict_json(synthetic_df):
    model = FakeChatModel(script=[json.dumps({
        "pattern_name": "Bull Flag",
        "confidence": 0.78,
        "reasoning": "Tight consolidation after a clear uptrend.",
    })])
    report, png_b64 = run_agent(model, synthetic_df)
    assert report.pattern_name == "Bull Flag"
    assert 0 <= report.confidence <= 1
    assert "uptrend" in report.reasoning.lower()
    assert png_b64  # non-empty base64


def test_pattern_agent_tolerates_markdown_fences(synthetic_df):
    fenced = "```json\n" + json.dumps({
        "pattern_name": "Rounded Bottom",
        "confidence": 0.6,
        "reasoning": "Smooth U.",
    }) + "\n```"
    model = FakeChatModel(script=[fenced])
    report, _ = run_agent(model, synthetic_df)
    assert report.pattern_name == "Rounded Bottom"


def test_pattern_agent_falls_back_when_name_not_in_catalog(synthetic_df):
    model = FakeChatModel(script=[json.dumps({
        "pattern_name": "Imaginary Pattern",
        "confidence": 0.5,
        "reasoning": "made up",
    })])
    report, _ = run_agent(model, synthetic_df)
    assert report.pattern_name == "None"


def test_catalog_has_sixteen_entries():
    assert len(PATTERN_CATALOG) == 16
