"""Unit tests for static env-resolution (plan stage).

The repo is never really cloned or executed: ``_run_git`` is monkeypatched to a fake that serves
manifest blobs for ``git show`` and a success for clone/fetch. This proves the plan is built from
file CONTENT only, that the env hash is deterministic, and that bad pins / missing entrypoints
degrade rather than raise.
"""

from __future__ import annotations

from dataclasses import dataclass

import pytest

from daxalgo_ml.research import plan as plan_mod
from daxalgo_ml.research.plan import plan_repro

GOOD_COMMIT = "a" * 40


@dataclass
class FakeGit:
    returncode: int = 0
    stdout: str = ""
    stderr: str = ""


def _fake_git_factory(blobs: dict[str, str]):
    """Return a _run_git replacement: clone/fetch succeed; `show <commit>:<name>` serves blobs."""

    def fake_run_git(args, cwd):
        if args[:1] == ["clone"]:
            return FakeGit(returncode=0)
        if args[:1] == ["fetch"]:
            return FakeGit(returncode=0)
        if args[:1] == ["show"]:
            ref = args[1]  # "<commit>:<filename>"
            name = ref.split(":", 1)[1]
            if name in blobs:
                return FakeGit(returncode=0, stdout=blobs[name])
            return FakeGit(returncode=128, stderr="path not found")
        return FakeGit(returncode=1, stderr="unexpected git call")

    return fake_run_git


def test_rejects_non_hex_commit():
    resp = plan_repro("https://github.com/example/repo.git", "not-a-sha")
    assert resp.error is not None
    assert "hex SHA" in resp.error
    assert resp.entrypoint == ""


def test_rejects_empty_inputs():
    assert plan_repro("", GOOD_COMMIT).error is not None
    assert plan_repro("https://x/y.git", "").error is not None


def test_requirements_repo_builds_pip_plan(monkeypatch):
    blobs = {
        "requirements.txt": "numpy==1.26.0\npandas==2.2.0\nbinance-connector==3.0\n",
        "repro.py": "print('hi')\n",
    }
    monkeypatch.setattr(plan_mod, "_run_git", _fake_git_factory(blobs))

    resp = plan_repro("https://github.com/example/repo.git", GOOD_COMMIT)

    assert resp.error is None
    assert resp.image == "python:3.11-slim"
    assert any("pip install" in c and "requirements.txt" in c for c in resp.setup_commands)
    assert resp.entrypoint == "python repro.py --out $RESULT_JSON"
    assert "binance" in resp.declared_data_deps
    assert len(resp.env_hash) == 64  # sha-256 hex


def test_dockerfile_base_image_and_cmd_are_honoured(monkeypatch):
    blobs = {
        "Dockerfile": 'FROM python:3.10-bullseye\nCMD ["python", "main.py", "--out", "x"]\n',
        "requirements.txt": "scipy\n",
    }
    monkeypatch.setattr(plan_mod, "_run_git", _fake_git_factory(blobs))

    resp = plan_repro("https://github.com/example/repo.git", GOOD_COMMIT)

    assert resp.error is None
    assert resp.image == "python:3.10-bullseye"
    assert resp.entrypoint == "python main.py --out x"


def test_env_hash_is_deterministic_and_content_sensitive(monkeypatch):
    blobs_a = {"requirements.txt": "numpy==1.26.0\n", "repro.py": "x"}
    blobs_b = {"requirements.txt": "numpy==1.26.1\n", "repro.py": "x"}

    monkeypatch.setattr(plan_mod, "_run_git", _fake_git_factory(blobs_a))
    h1 = plan_repro("https://github.com/example/repo.git", GOOD_COMMIT).env_hash
    h1_again = plan_repro("https://github.com/example/repo.git", GOOD_COMMIT).env_hash

    monkeypatch.setattr(plan_mod, "_run_git", _fake_git_factory(blobs_b))
    h2 = plan_repro("https://github.com/example/repo.git", GOOD_COMMIT).env_hash

    assert h1 == h1_again
    assert h1 != h2


def test_clone_failure_degrades_to_empty(monkeypatch):
    def failing_clone(args, cwd):
        if args[:1] == ["clone"]:
            return FakeGit(returncode=128, stderr="repository not found")
        return FakeGit(returncode=0)

    monkeypatch.setattr(plan_mod, "_run_git", failing_clone)

    resp = plan_repro("https://github.com/example/missing.git", GOOD_COMMIT)
    assert resp.error is not None
    assert "git clone failed" in resp.error
    assert resp.entrypoint == ""


def test_pyproject_only_uses_editable_install(monkeypatch):
    blobs = {
        "pyproject.toml": "[project]\nname='ofi'\ndependencies=['numpy']\n",
        "repro.py": "x",
    }
    monkeypatch.setattr(plan_mod, "_run_git", _fake_git_factory(blobs))

    resp = plan_repro("https://github.com/example/repo.git", GOOD_COMMIT)
    assert resp.error is None
    assert any("pip install" in c and "." in c for c in resp.setup_commands)
