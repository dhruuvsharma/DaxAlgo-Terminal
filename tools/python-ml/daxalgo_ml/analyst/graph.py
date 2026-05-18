"""LangGraph wiring — indicator → pattern → trend → decision.

The four nodes are sequential because each one's output may feed the next (the decision
agent needs all three reports). Running them as a graph rather than a plain sequence
keeps the door open for adding parallel branches (e.g. a sentiment or news agent) later.
"""

from __future__ import annotations

import time
from typing import Any, TypedDict

import pandas as pd
from langgraph.graph import END, StateGraph

from ..providers import ProviderConfig, build_text_model, build_vision_model
from ..schemas import (
    AnalystBar,
    AnalystReport,
    IndicatorReport,
    PatternReport,
    TrendReport,
)
from .agents import decision as decision_agent
from .agents import indicator as indicator_agent
from .agents import pattern as pattern_agent
from .agents import trend as trend_agent
from .charting import bars_to_dataframe


class AnalystState(TypedDict, total=False):
    df: pd.DataFrame
    text_model: Any
    vision_model: Any
    indicator: IndicatorReport
    pattern: PatternReport
    trend: TrendReport
    pattern_png: str
    trend_png: str
    report: AnalystReport


def _node_indicator(state: AnalystState) -> AnalystState:
    state["indicator"] = indicator_agent.run_agent(state["text_model"], state["df"])
    return state


def _node_pattern(state: AnalystState) -> AnalystState:
    report, png = pattern_agent.run_agent(state["vision_model"], state["df"])
    state["pattern"] = report
    state["pattern_png"] = png
    return state


def _node_trend(state: AnalystState) -> AnalystState:
    report, png = trend_agent.run_agent(state["vision_model"], state["df"])
    state["trend"] = report
    state["trend_png"] = png
    return state


def _node_decision(state: AnalystState) -> AnalystState:
    report = decision_agent.run_agent(
        state["text_model"],
        state["indicator"],
        state["pattern"],
        state["trend"],
    )
    # Re-attach the chart bytes — the decision agent only sees the structured reports.
    report.pattern_chart_png_base64 = state.get("pattern_png", "")
    report.trend_chart_png_base64 = state.get("trend_png", "")
    state["report"] = report
    return state


def set_graph() -> Any:
    """Build and compile the LangGraph. Compiled graphs are reusable across requests."""
    graph = StateGraph(AnalystState)
    graph.add_node("indicator", _node_indicator)
    graph.add_node("pattern", _node_pattern)
    graph.add_node("trend", _node_trend)
    graph.add_node("decision", _node_decision)

    graph.set_entry_point("indicator")
    graph.add_edge("indicator", "pattern")
    graph.add_edge("pattern", "trend")
    graph.add_edge("trend", "decision")
    graph.add_edge("decision", END)
    return graph.compile()


_GRAPH = None


def _get_graph() -> Any:
    global _GRAPH
    if _GRAPH is None:
        _GRAPH = set_graph()
    return _GRAPH


def run_graph(
    bars: list[AnalystBar],
    provider: str,
    api_key: str,
    model: str,
    vision_model: str,
) -> AnalystReport:
    """One-shot synchronous run. Returns a fully populated AnalystReport including
    ``elapsed_ms`` and both chart PNGs."""
    cfg = ProviderConfig(provider=provider, api_key=api_key, model=model, vision_model=vision_model)
    text = build_text_model(cfg)
    vision = build_vision_model(cfg)

    df = bars_to_dataframe([b.model_dump(mode="json") for b in bars])

    start = time.perf_counter()
    state: AnalystState = {"df": df, "text_model": text, "vision_model": vision}
    final = _get_graph().invoke(state)
    elapsed_ms = int((time.perf_counter() - start) * 1000)

    report: AnalystReport = final["report"]
    report.elapsed_ms = elapsed_ms
    return report


__all__ = ["set_graph", "run_graph"]
