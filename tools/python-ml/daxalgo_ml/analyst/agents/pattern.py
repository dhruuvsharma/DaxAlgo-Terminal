"""Pattern agent — renders a candlestick PNG of the last N bars and asks the vision LLM
to match it against the QuantAgent 16-pattern catalog.
"""

from __future__ import annotations

import json
from typing import Any

import pandas as pd
from langchain_core.messages import HumanMessage, SystemMessage
from tenacity import retry, stop_after_attempt, wait_exponential, retry_if_exception_type

from ...schemas import PatternReport
from ..charting import render_candles


PATTERN_CATALOG: tuple[str, ...] = (
    "Inverse Head and Shoulders",
    "Double Bottom",
    "Rounded Bottom",
    "Hidden Base",
    "Falling Wedge",
    "Rising Wedge",
    "Ascending Triangle",
    "Descending Triangle",
    "Bull Flag",
    "Bear Flag",
    "Rectangle",
    "Island Reversal",
    "V-reversal",
    "Rounded Top",
    "Expanding Triangle",
    "Symmetrical Triangle",
)


_SYSTEM_PROMPT = (
    "You are a technical analyst scoring a candlestick chart against a fixed catalog of "
    "classical chart patterns. Return STRICT JSON only — no prose, no markdown fences — "
    'with shape: {"pattern_name": str, "confidence": float in [0,1], "reasoning": str}. '
    "If no pattern fits cleanly, return pattern_name=\"None\" with confidence below 0.3."
)


@retry(
    reraise=True,
    stop=stop_after_attempt(3),
    wait=wait_exponential(multiplier=1, min=1, max=8),
    retry=retry_if_exception_type(Exception),
)
def _invoke_with_retry(model: Any, messages: list) -> Any:
    return model.invoke(messages)


def run_agent(vision_model: Any, df: pd.DataFrame) -> tuple[PatternReport, str]:
    """Returns (report, png_base64). The PNG is returned separately so the caller can
    forward it to the WPF view without re-rendering."""
    png_b64 = render_candles(df, title="Pattern view")

    catalog = ", ".join(PATTERN_CATALOG)
    user_text = (
        f"Catalog: {catalog}.\n"
        "Score the chart against this catalog and respond with strict JSON only."
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
        report = _parse_pattern_json(raw)
    except Exception as ex:  # noqa: BLE001
        report = PatternReport(
            pattern_name="None",
            confidence=0.0,
            reasoning=f"Vision pattern agent failed: {ex}",
        )

    return report, png_b64


def _parse_pattern_json(raw: str) -> PatternReport:
    # Strip ```json fences the LLM sometimes still emits despite the prompt.
    text = raw.strip()
    if text.startswith("```"):
        text = text.strip("`")
        if text.lower().startswith("json"):
            text = text[4:]
        text = text.strip()
    obj = json.loads(text)
    name = str(obj.get("pattern_name") or "None")
    if name not in PATTERN_CATALOG and name != "None":
        # Tolerate near-misses (e.g. "Head and Shoulders" vs "Inverse Head and Shoulders").
        match = next((p for p in PATTERN_CATALOG if p.lower() in name.lower() or name.lower() in p.lower()), None)
        if match is not None:
            name = match
        else:
            name = "None"
    return PatternReport(
        pattern_name=name,
        confidence=float(obj.get("confidence") or 0.0),
        reasoning=str(obj.get("reasoning") or "").strip(),
    )


__all__ = ["PATTERN_CATALOG", "run_agent"]
