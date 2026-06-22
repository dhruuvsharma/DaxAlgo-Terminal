"""Endpoint contract tests for /research/resolve and /research/plan via FastAPI TestClient.

The underlying resolve/plan functions are monkeypatched (their own logic is unit-tested
elsewhere); here we pin the JSON wire contract the C# HttpPaperIngestClient / HttpEnvResolverClient
expect (snake_case fields), that no api_key is required, and that bad input / failures fold into an
empty body rather than a 500.
"""

from __future__ import annotations

from fastapi.testclient import TestClient

from daxalgo_ml import app as app_mod
from daxalgo_ml.schemas import PlanResponse, ResolvedPaper, ResolvedRepo, ResolveResponse

client = TestClient(app_mod.app)


def test_resolve_contract(monkeypatch):
    def fake_resolve(url: str) -> ResolveResponse:
        return ResolveResponse(
            resolved=True,
            paper=ResolvedPaper(arxiv_id="2507.22712", title="OFI", url="https://arxiv.org/abs/2507.22712"),
            repos=[ResolvedRepo(git_url="https://github.com/example/repo.git", commit="abc1234")],
            error=None,
        )

    monkeypatch.setattr(app_mod, "resolve_paper", fake_resolve)

    r = client.post("/research/resolve", json={"url": "https://arxiv.org/abs/2507.22712"})
    assert r.status_code == 200
    body = r.json()
    assert body["resolved"] is True
    assert body["paper"]["arxiv_id"] == "2507.22712"
    assert body["repos"][0]["git_url"] == "https://github.com/example/repo.git"
    assert body["repos"][0]["commit"] == "abc1234"


def test_resolve_empty_url_returns_empty_not_500():
    r = client.post("/research/resolve", json={"url": ""})
    assert r.status_code == 200
    body = r.json()
    assert body["resolved"] is False
    assert body["repos"] == []
    assert body["error"]


def test_resolve_internal_failure_folds_to_empty(monkeypatch):
    def boom(url: str):
        raise RuntimeError("kaboom")

    monkeypatch.setattr(app_mod, "resolve_paper", boom)

    r = client.post("/research/resolve", json={"url": "https://arxiv.org/abs/2507.22712"})
    assert r.status_code == 200
    assert r.json()["resolved"] is False


def test_plan_contract(monkeypatch):
    def fake_plan(git_url: str, commit: str) -> PlanResponse:
        return PlanResponse(
            image="python:3.11-slim",
            setup_commands=["pip install --no-cache-dir -r requirements.txt"],
            entrypoint="python repro.py --out $RESULT_JSON",
            declared_data_deps=["binance"],
            env_hash="deadbeef",
            error=None,
        )

    monkeypatch.setattr(app_mod, "plan_repro", fake_plan)

    r = client.post("/research/plan", json={"git_url": "https://x/y.git", "commit": "a" * 40})
    assert r.status_code == 200
    body = r.json()
    assert body["image"] == "python:3.11-slim"
    assert body["setup_commands"] == ["pip install --no-cache-dir -r requirements.txt"]
    assert body["entrypoint"] == "python repro.py --out $RESULT_JSON"
    assert body["declared_data_deps"] == ["binance"]
    assert body["env_hash"] == "deadbeef"
    assert body["error"] is None


def test_plan_missing_commit_returns_empty_not_500():
    r = client.post("/research/plan", json={"git_url": "https://x/y.git", "commit": ""})
    assert r.status_code == 200
    assert r.json()["error"]


def test_plan_requires_no_api_key(monkeypatch):
    # Proves the endpoint has no api_key gate (unlike /analyst/run).
    monkeypatch.setattr(app_mod, "plan_repro", lambda g, c: PlanResponse.empty("ok-noauth"))
    r = client.post("/research/plan", json={"git_url": "https://x/y.git", "commit": "a" * 7})
    assert r.status_code == 200
