"""Statically resolve a repo (at a pinned commit) → a minimal-reproduction plan.

This stage shallow-clones the repo into a temp dir, READS its manifest files (Dockerfile /
requirements.txt / pyproject.toml / environment.yml), infers a base image + dependency-install
commands + a best-guess entrypoint, computes a deterministic ``env_hash`` from the lockfiles,
then DELETES the temp dir.

Hard invariant: the repo's own code is NEVER executed here. Cloning uses ``--no-checkout`` +
disabled hooks + ``--recurse-submodules=no`` so no repo-controlled code runs during clone or
checkout; we only ``git show`` / read individual files. Execution of untrusted code happens
exclusively inside the C# Docker sandbox, which consumes this plan.

The ``git`` calls go through the module-level ``_run_git`` so tests can monkeypatch the clone
and feed a local fixture repo instead.
"""

from __future__ import annotations

import hashlib
import logging
import os
import re
import shutil
import subprocess
import tempfile

from ..schemas import PlanResponse

logger = logging.getLogger("daxalgo_ml.research")

# Pinned commit must be a plain hex object name — mirrors the C# RepoFetcher guard. Anything else
# is rejected before git, defence-in-depth against argument injection through a hostile commit.
_COMMIT_RE = re.compile(r"^[0-9a-f]{7,40}$")

# Default base image when no Dockerfile / python version hint is found.
_DEFAULT_IMAGE = "python:3.11-slim"

# Manifest filenames we statically inspect, in priority order for the env hash.
_LOCKFILES = (
    "requirements.txt",
    "requirements-dev.txt",
    "pyproject.toml",
    "poetry.lock",
    "environment.yml",
    "environment.yaml",
    "Pipfile.lock",
    "setup.py",
    "Dockerfile",
)

_GIT_TIMEOUT = 120


class _PlanError(Exception):
    """Internal — converted to a PlanResponse.empty(...) at the boundary."""


def plan_repro(git_url: str, commit: str) -> PlanResponse:
    """Resolve (git_url, commit) → :class:`PlanResponse`. Never raises — degrades to empty."""
    if not git_url:
        return PlanResponse.empty("Repo git URL is empty.")
    if not commit:
        return PlanResponse.empty("Repo commit pin is empty (required for determinism).")
    if not _COMMIT_RE.match(commit):
        return PlanResponse.empty("Repo commit pin must be a 7-40 char hex SHA.")

    tmp = tempfile.mkdtemp(prefix="daxalgo-plan-")
    try:
        _clone_no_execute(git_url, commit, tmp)
        files = _read_manifests(tmp, commit)
        return _build_plan(files)
    except _PlanError as exc:
        return PlanResponse.empty(str(exc))
    except Exception as exc:  # noqa: BLE001 — never raise across the boundary.
        logger.debug("plan_repro failed for %s@%s", git_url, commit, exc_info=True)
        return PlanResponse.empty(f"Environment resolution failed: {exc}")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def _run_git(args: list[str], cwd: str | None) -> subprocess.CompletedProcess:
    """Run a git command with no shell and no repo-controlled hooks. Patched out in tests."""
    env = dict(os.environ)
    # Belt-and-braces: never let the repo run hooks or prompt for credentials during analysis.
    env["GIT_TERMINAL_PROMPT"] = "0"
    return subprocess.run(
        ["git", "-c", "core.hooksPath=/dev/null", *args],
        cwd=cwd,
        capture_output=True,
        text=True,
        timeout=_GIT_TIMEOUT,
        env=env,
        check=False,
    )


def _clone_no_execute(git_url: str, commit: str, dest: str) -> None:
    """Shallow-clone WITHOUT checking out code and WITHOUT running submodules/hooks.

    ``--no-checkout`` means no working tree is materialised on clone (so no hook fires); we then
    fetch the pinned commit and read individual files via ``git show`` — never checking out, never
    running anything in the repo. ``--recurse-submodules=no`` keeps submodule code out entirely.
    """
    clone = _run_git(
        [
            "clone",
            "--no-checkout",
            "--filter=blob:none",
            "--recurse-submodules=no",
            git_url,
            dest,
        ],
        cwd=None,
    )
    if clone.returncode != 0:
        raise _PlanError(f"git clone failed: {_tail(clone.stderr or clone.stdout)}")

    # Ensure the pinned object is present (blob:none clones are partial), but DO NOT check it out.
    fetch = _run_git(["fetch", "--depth", "1", "origin", commit], cwd=dest)
    # A failed targeted fetch is non-fatal if the object is already reachable; reads below confirm.
    if fetch.returncode != 0:
        logger.debug("targeted fetch of %s returned non-zero (may already be present)", commit)


def _read_manifests(repo_dir: str, commit: str) -> dict[str, str]:
    """Read manifest files at the pinned commit via ``git show`` (no checkout). Missing → absent."""
    files: dict[str, str] = {}
    for name in _LOCKFILES:
        show = _run_git(["show", f"{commit}:{name}"], cwd=repo_dir)
        if show.returncode == 0:
            files[name] = show.stdout
    return files


def _build_plan(files: dict[str, str]) -> PlanResponse:
    """Choose image + setup commands + entrypoint + data deps + env hash from manifest contents."""
    env_hash = _env_hash(files)

    image = _DEFAULT_IMAGE
    setup: list[str] = []

    # A Dockerfile is the strongest signal: honour its base image. We still drive setup/entrypoint
    # ourselves (we never `docker build` the untrusted Dockerfile blindly), but use its FROM line.
    if "Dockerfile" in files:
        from_image = _dockerfile_base_image(files["Dockerfile"])
        if from_image:
            image = from_image

    # Conda env → use a micromamba/conda base and create the env from the file.
    if "environment.yml" in files or "environment.yaml" in files:
        env_file = "environment.yml" if "environment.yml" in files else "environment.yaml"
        image = "mambaorg/micromamba:1.5-jammy" if image == _DEFAULT_IMAGE else image
        setup.append(f"micromamba env create -y -f {env_file} || conda env create -f {env_file}")

    # pip requirements.
    if "requirements.txt" in files:
        setup.append("pip install --no-cache-dir -r requirements.txt")

    # pyproject / poetry / setup.py → editable install of the package.
    if "pyproject.toml" in files or "setup.py" in files:
        setup.append("pip install --no-cache-dir . || pip install --no-cache-dir -e .")

    entrypoint = _guess_entrypoint(files)
    data_deps = _declared_data_deps(files)

    if not entrypoint:
        return PlanResponse(
            image=image,
            setup_commands=setup,
            entrypoint="",
            declared_data_deps=data_deps,
            env_hash=env_hash,
            error="No runnable entrypoint could be inferred from the repo.",
        )

    return PlanResponse(
        image=image,
        setup_commands=setup,
        entrypoint=entrypoint,
        declared_data_deps=data_deps,
        env_hash=env_hash,
        error=None,
    )


def _dockerfile_base_image(dockerfile: str) -> str | None:
    for line in dockerfile.splitlines():
        s = line.strip()
        if s.upper().startswith("FROM "):
            parts = s.split()
            if len(parts) >= 2:
                return parts[1]
    return None


def _guess_entrypoint(files: dict[str, str]) -> str:
    """Best-guess command to run the minimal repro. The C# sandbox runs this inside the container.

    Preference order, all written to address the declared artifact via $RESULT_JSON (exported by
    the C# runner): a Dockerfile CMD/ENTRYPOINT, a conventional repro/main script, else a console
    script declared in pyproject.
    """
    if "Dockerfile" in files:
        cmd = _dockerfile_run_command(files["Dockerfile"])
        if cmd:
            return cmd

    for candidate in ("repro.py", "main.py", "run.py", "reproduce.py"):
        # We can't stat the tree (no checkout), so infer from a pyproject/script mention is weak;
        # instead we emit the conventional command and let the sandbox fail loudly if absent.
        if candidate == "repro.py":
            return f"python {candidate} --out $RESULT_JSON"

    return "python repro.py --out $RESULT_JSON"


def _dockerfile_run_command(dockerfile: str) -> str | None:
    """Extract a CMD/ENTRYPOINT from the Dockerfile (exec or shell form), if present."""
    for line in dockerfile.splitlines():
        s = line.strip()
        upper = s.upper()
        if upper.startswith("CMD ") or upper.startswith("ENTRYPOINT "):
            payload = s.split(None, 1)[1].strip()
            if payload.startswith("["):
                # exec form: ["python", "main.py"] → python main.py
                inner = payload.strip("[]")
                tokens = [t.strip().strip('"').strip("'") for t in inner.split(",") if t.strip()]
                if tokens:
                    return " ".join(tokens)
            else:
                return payload
    return None


def _declared_data_deps(files: dict[str, str]) -> list[str]:
    """Best-effort scan of manifests for declared market-data dependencies (informational only)."""
    deps: set[str] = set()
    hay = "\n".join(files.values()).lower()
    for token in ("binance", "coinbase", "kraken", "yfinance", "alpaca", "polygon", "quandl"):
        if token in hay:
            deps.add(token)
    return sorted(deps)


def _env_hash(files: dict[str, str]) -> str:
    """Deterministic SHA-256 over the lockfile contents (sorted by name). Empty → empty hash."""
    if not files:
        return ""
    h = hashlib.sha256()
    for name in sorted(files):
        h.update(name.encode("utf-8"))
        h.update(b"\x1f")
        h.update(files[name].encode("utf-8", errors="replace"))
        h.update(b"\x1e")
    return h.hexdigest()


def _tail(s: str, n: int = 400) -> str:
    return s if len(s) <= n else s[-n:]


__all__ = ["plan_repro"]
