"""Decision agent — synthesises the indicator/pattern/trend reports into the final
AnalystReport. Validates against the strict Pydantic schema and retries up to 3 times
with progressively stricter prompts; if all retries fail, returns a NoCall report.
"""

from __future__ import annotations

import json
from typing import Any

from langchain_core.messages import HumanMessage, SystemMessage
from pydantic import ValidationError

from ...schemas import (
    AnalystReport,
    IndicatorReport,
    PatternReport,
    TrendReport,
)


_BASE_PROMPT = (
    "You synthesise three separate technical-analysis reports into one structured trading "
    "verdict. Return STRICT JSON only — no prose, no markdown fences — with shape:\n"
    '{"decision": one of [\"long\", \"short\", \"no_call\"], '
    '"forecast_horizon": str (e.g. \"next 4 bars\"), '
    '"risk_reward_ratio": float, '
    '"confidence": float in [0,1], '
    '"justification": str (2-3 sentences)}\n'
    "Lean toward no_call when the three reports disagree."
)


def run_agent(
    text_model: Any,
    indicator: IndicatorReport,
    pattern: PatternReport,
    trend: TrendReport,
) -> AnalystReport:
    user_prompt = _build_user_prompt(indicator, pattern, trend)
    system = _BASE_PROMPT

    for attempt in range(3):
        try:
            resp = text_model.invoke(
                [SystemMessage(content=system), HumanMessage(content=user_prompt)]
            )
            raw = (getattr(resp, "content", "") or "").strip()
            return _parse_decision(raw, indicator, pattern, trend)
        except (ValidationError, json.JSONDecodeError, KeyError, TypeError, ValueError) as ex:
            system = _BASE_PROMPT + (
                f"\nPrevious attempt {attempt + 1} failed to parse ({ex.__class__.__name__}). "
                "Return ONLY a single JSON object with the exact keys above. Do not include "
                "code fences, prose, or any text outside the JSON."
            )
        except Exception:  # noqa: BLE001 — provider errors fall through to NoCall.
            break

    return AnalystReport(
        decision="no_call",
        forecast_horizon="—",
        risk_reward_ratio=0.0,
        confidence=0.0,
        justification="Decision agent failed to produce structured JSON after 3 retries.",
        indicator=indicator,
        pattern=pattern,
        trend=trend,
    )


def _build_user_prompt(
    indicator: IndicatorReport, pattern: PatternReport, trend: TrendReport
) -> str:
    return (
        "Indicator summary:\n"
        f"{indicator.summary}\n"
        f"Values: {json.dumps(indicator.values)}\n\n"
        "Pattern:\n"
        f"- name: {pattern.pattern_name}\n"
        f"- confidence: {pattern.confidence:.2f}\n"
        f"- reasoning: {pattern.reasoning}\n\n"
        "Trend:\n"
        f"- direction: {trend.direction}\n"
        f"- slope: {trend.slope:.4f}\n"
        f"- reasoning: {trend.reasoning}\n\n"
        "Now produce the verdict."
    )


def _parse_decision(
    raw: str, indicator: IndicatorReport, pattern: PatternReport, trend: TrendReport
) -> AnalystReport:
    text = raw.strip()
    if text.startswith("```"):
        text = text.strip("`")
        if text.lower().startswith("json"):
            text = text[4:]
        text = text.strip()
    obj = json.loads(text)

    decision = str(obj["decision"]).lower().replace("-", "_")
    if decision not in ("long", "short", "no_call"):
        decision = "no_call"

    return AnalystReport(
        decision=decision,  # type: ignore[arg-type]
        forecast_horizon=str(obj.get("forecast_horizon") or "—"),
        risk_reward_ratio=float(obj.get("risk_reward_ratio") or 0.0),
        confidence=float(obj.get("confidence") or 0.0),
        justification=str(obj.get("justification") or "").strip(),
        indicator=indicator,
        pattern=pattern,
        trend=trend,
    )


__all__ = ["run_agent"]
