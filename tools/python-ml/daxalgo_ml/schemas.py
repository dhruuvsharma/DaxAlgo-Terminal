"""Pydantic models mirroring the C# wire types under TradingTerminal.Core.AiAnalyst.

The C# side serialises with JsonNamingPolicy.SnakeCaseLower, so every alias here is
snake_case. The validator on AnalystReport enforces structure — if the LLM produces
something that doesn't fit, the decision agent retries with a stricter system prompt
up to three times before falling back to NoCall.
"""

from __future__ import annotations

from datetime import datetime
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field, field_validator

Decision = Literal["long", "short", "no_call"]


class AnalystBar(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    timestamp_utc: datetime
    open: float
    high: float
    low: float
    close: float
    volume: int


class AnalystRequest(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    symbol: str
    timeframe: str
    bar_count: int
    provider: str
    model: str
    vision_model: str
    api_key: str = Field(default="", repr=False)
    bars: list[AnalystBar]

    @field_validator("bar_count")
    @classmethod
    def _bar_count_positive(cls, v: int) -> int:
        if v <= 0:
            raise ValueError("bar_count must be > 0")
        return v


class IndicatorReport(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    summary: str
    values: dict[str, float]


class PatternReport(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    pattern_name: str
    confidence: float
    reasoning: str

    @field_validator("confidence")
    @classmethod
    def _conf_in_unit_interval(cls, v: float) -> float:
        return max(0.0, min(1.0, v))


class TrendReport(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    direction: str
    slope: float
    channel_upper: float
    channel_lower: float
    reasoning: str


class AnalystReport(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    decision: Decision
    forecast_horizon: str
    risk_reward_ratio: float
    confidence: float
    justification: str
    indicator: IndicatorReport
    pattern: PatternReport
    trend: TrendReport
    pattern_chart_png_base64: str = ""
    trend_chart_png_base64: str = ""
    elapsed_ms: int = 0

    @field_validator("confidence")
    @classmethod
    def _conf_in_unit_interval(cls, v: float) -> float:
        return max(0.0, min(1.0, v))

    @classmethod
    def unavailable(cls, reason: str) -> "AnalystReport":
        return cls(
            decision="no_call",
            forecast_horizon="—",
            risk_reward_ratio=0.0,
            confidence=0.0,
            justification=reason,
            indicator=IndicatorReport(summary=reason, values={}),
            pattern=PatternReport(pattern_name="None", confidence=0.0, reasoning=reason),
            trend=TrendReport(
                direction="Flat", slope=0.0, channel_upper=0.0, channel_lower=0.0, reasoning=reason
            ),
            pattern_chart_png_base64="",
            trend_chart_png_base64="",
            elapsed_ms=0,
        )


__all__ = [
    "AnalystBar",
    "AnalystRequest",
    "AnalystReport",
    "IndicatorReport",
    "PatternReport",
    "TrendReport",
    "Decision",
]
