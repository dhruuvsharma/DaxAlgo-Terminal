"""Unit tests for paper resolution (arXiv + Papers-with-Code), with all HTTP mocked.

The resolver never touches the real network here: ``httpx.get`` is monkeypatched in the
``daxalgo_ml.research.resolve`` module namespace. Failures must degrade to an empty repos list
(or an empty response) rather than raising.
"""

from __future__ import annotations

from dataclasses import dataclass

import pytest

from daxalgo_ml.research import resolve
from daxalgo_ml.research.resolve import extract_arxiv_id, resolve_paper

_ARXIV_ATOM = """<?xml version="1.0" encoding="UTF-8"?>
<feed xmlns="http://www.w3.org/2005/Atom">
  <entry>
    <id>http://arxiv.org/abs/2507.22712v1</id>
    <title>Order-Flow Imbalance Regimes</title>
    <summary>We study OBI(T). Code at https://github.com/example/ofi-repo for reproduction.</summary>
  </entry>
</feed>
"""


@dataclass
class FakeResp:
    status_code: int = 200
    text: str = ""
    _json: dict | None = None

    def raise_for_status(self) -> None:
        if self.status_code >= 400:
            raise RuntimeError(f"HTTP {self.status_code}")

    def json(self) -> dict:
        return self._json or {}


def _install_http(monkeypatch, *, atom: str, pwc: dict | None, pwc_status: int = 200):
    def fake_get(url, params=None, timeout=None):
        if "export.arxiv.org" in url:
            return FakeResp(status_code=200, text=atom)
        if "paperswithcode.com" in url:
            return FakeResp(status_code=pwc_status, _json=pwc or {})
        raise AssertionError(f"unexpected URL {url}")

    monkeypatch.setattr(resolve.httpx, "get", fake_get)


def test_extract_arxiv_id_from_abs_url():
    assert extract_arxiv_id("https://arxiv.org/abs/2507.22712") == "2507.22712"
    assert extract_arxiv_id("https://arxiv.org/abs/2507.22712v3") == "2507.22712"
    assert extract_arxiv_id("https://arxiv.org/pdf/2507.22712.pdf") == "2507.22712"
    assert extract_arxiv_id("no id here") is None


def test_resolve_returns_paper_and_pwc_repo(monkeypatch):
    pwc = {"results": [{"url": "https://github.com/example/ofi-repo"}]}
    _install_http(monkeypatch, atom=_ARXIV_ATOM, pwc=pwc)

    resp = resolve_paper("https://arxiv.org/abs/2507.22712")

    assert resp.resolved is True
    assert resp.paper is not None
    assert resp.paper.arxiv_id == "2507.22712"
    assert resp.paper.title == "Order-Flow Imbalance Regimes"
    assert resp.paper.url == "http://arxiv.org/abs/2507.22712v1"
    assert len(resp.repos) == 1
    assert resp.repos[0].git_url == "https://github.com/example/ofi-repo.git"


def test_resolve_falls_back_to_abstract_link_when_pwc_empty(monkeypatch):
    _install_http(monkeypatch, atom=_ARXIV_ATOM, pwc={"results": []})

    resp = resolve_paper("https://arxiv.org/abs/2507.22712")

    assert resp.resolved is True
    # The github link in the summary is discovered as a fallback.
    assert any("example/ofi-repo" in r.git_url for r in resp.repos)


def test_resolve_degrades_to_empty_repos_when_pwc_errors(monkeypatch):
    _install_http(monkeypatch, atom=_ARXIV_ATOM.replace(
        "Code at https://github.com/example/ofi-repo for reproduction.", "No code."
    ), pwc=None, pwc_status=500)

    resp = resolve_paper("https://arxiv.org/abs/2507.22712")

    assert resp.resolved is True
    assert resp.repos == []


def test_resolve_rejects_non_arxiv_url():
    resp = resolve_paper("https://example.com/not-a-paper")
    assert resp.resolved is False
    assert resp.repos == []
    assert "arXiv id" in (resp.error or "")
