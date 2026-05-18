"""LLM provider factory.

QuantAgent supports four backends — OpenAI, Anthropic, Qwen (DashScope OpenAI-compatible),
and MiniMax. Each returns a LangChain BaseChatModel so the agent code stays
provider-agnostic; the agents only know about ``model.invoke([HumanMessage(...)])``.

The vision-capable models for pattern/trend agents are passed separately because some
providers ship vision and text on different model ids (OpenAI: gpt-4o vs gpt-4; Anthropic:
claude-3.5-sonnet handles both).
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any


@dataclass(frozen=True)
class ProviderConfig:
    provider: str
    api_key: str
    model: str
    vision_model: str


def build_text_model(cfg: ProviderConfig) -> Any:
    """Return a LangChain chat model for plain-text reasoning."""
    return _build(cfg.provider, cfg.api_key, cfg.model)


def build_vision_model(cfg: ProviderConfig) -> Any:
    """Return a LangChain chat model capable of consuming image_url content blocks."""
    return _build(cfg.provider, cfg.api_key, cfg.vision_model)


def _build(provider: str, api_key: str, model: str) -> Any:
    provider = (provider or "").strip().lower()
    if not api_key:
        raise ValueError(f"API key for provider '{provider}' is empty.")

    if provider == "openai":
        from langchain_openai import ChatOpenAI

        return ChatOpenAI(model=model, api_key=api_key, temperature=0)

    if provider == "anthropic":
        from langchain_anthropic import ChatAnthropic

        return ChatAnthropic(model=model, api_key=api_key, temperature=0)

    if provider == "qwen":
        # Qwen / Alibaba DashScope exposes an OpenAI-compatible endpoint.
        from langchain_openai import ChatOpenAI

        return ChatOpenAI(
            model=model,
            api_key=api_key,
            base_url="https://dashscope.aliyuncs.com/compatible-mode/v1",
            temperature=0,
        )

    if provider == "minimax":
        from langchain_openai import ChatOpenAI

        return ChatOpenAI(
            model=model,
            api_key=api_key,
            base_url="https://api.minimax.chat/v1",
            temperature=0,
        )

    raise ValueError(f"Unknown provider '{provider}'. Expected one of: openai, anthropic, qwen, minimax.")


__all__ = ["ProviderConfig", "build_text_model", "build_vision_model"]
