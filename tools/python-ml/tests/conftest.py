"""Shared pytest fixtures — synthetic OHLCV bars and a fake LLM that returns canned JSON.

The full agent graph hits OpenAI / Anthropic / etc. in production. For unit tests we
substitute a FakeChatModel that exposes the LangChain ``.invoke(messages)`` shape and
returns deterministic content. This keeps the suite fully offline.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone

import numpy as np
import pandas as pd
import pytest

from daxalgo_ml.schemas import AnalystBar


@dataclass
class FakeResponse:
    content: str


@dataclass
class FakeChatModel:
    """Records every invocation; replies with whatever's in ``script`` (round-robin)."""
    script: list[str] = field(default_factory=list)
    calls: list[list] = field(default_factory=list)
    _idx: int = 0

    def invoke(self, messages):
        self.calls.append(messages)
        if not self.script:
            return FakeResponse(content="")
        reply = self.script[self._idx % len(self.script)]
        self._idx += 1
        return FakeResponse(content=reply)


@pytest.fixture
def fake_text_model() -> FakeChatModel:
    return FakeChatModel(script=["Market is trending up with steady momentum."])


@pytest.fixture
def fake_vision_model() -> FakeChatModel:
    return FakeChatModel(script=[])


@pytest.fixture
def synthetic_bars() -> list[AnalystBar]:
    """40 bars of upward-drifting random walk; deterministic seed."""
    rng = np.random.default_rng(42)
    n = 40
    start = datetime(2026, 1, 1, tzinfo=timezone.utc)
    price = 100.0
    bars: list[AnalystBar] = []
    for i in range(n):
        drift = rng.normal(0.05, 0.5)
        open_p = price
        close_p = open_p + drift
        high_p = max(open_p, close_p) + abs(rng.normal(0, 0.2))
        low_p = min(open_p, close_p) - abs(rng.normal(0, 0.2))
        vol = int(rng.integers(100, 1000))
        bars.append(
            AnalystBar(
                timestamp_utc=start + timedelta(hours=i),
                open=open_p,
                high=high_p,
                low=low_p,
                close=close_p,
                volume=vol,
            )
        )
        price = close_p
    return bars


@pytest.fixture
def synthetic_df(synthetic_bars: list[AnalystBar]) -> pd.DataFrame:
    from daxalgo_ml.analyst.charting import bars_to_dataframe

    return bars_to_dataframe([b.model_dump(mode="json") for b in synthetic_bars])
