"""FastAPI app — the WPF terminal's loopback bridge to the AI Market Analyst.

Endpoints:
    GET  /healthz     — readiness probe.
    POST /analyst/run — runs the LangGraph analyst on a window of bars.

Lifetime: spawned on demand by the WPF app, killed on shutdown. Binds to 127.0.0.1 only
(no remote access, no auth — same trust boundary as the WPF process). A free port is
chosen at spawn unless overridden via DAXALGO_ML_PORT.
"""

from __future__ import annotations

import argparse
import asyncio
import logging
import os
import socket
from contextlib import asynccontextmanager
from typing import Any

import uvicorn
from fastapi import FastAPI, HTTPException
from fastapi.concurrency import run_in_threadpool

from .research import plan_repro, resolve_paper
from .schemas import (
    AnalystReport,
    AnalystRequest,
    PlanRequest,
    PlanResponse,
    ResolveRequest,
    ResolveResponse,
)

logger = logging.getLogger("daxalgo_ml")

# Top-level wall-clock ceiling for one /analyst/run. The C# side has its own per-call
# timeout (AiAnalystOptions.TimeoutSeconds) so this is the outer safety net.
RUN_TIMEOUT_SECONDS = 60


@asynccontextmanager
async def lifespan(app: FastAPI):
    logger.info("daxalgo-ml starting up")
    yield
    logger.info("daxalgo-ml shutting down")


app = FastAPI(title="daxalgo-ml", version="0.1.0", lifespan=lifespan)


@app.get("/healthz")
def healthz() -> dict[str, str]:
    return {"status": "ok"}


@app.post("/analyst/run", response_model=AnalystReport)
async def analyst_run(req: AnalystRequest) -> AnalystReport:
    if not req.bars:
        raise HTTPException(status_code=400, detail="bars list is empty")
    if not req.api_key:
        raise HTTPException(status_code=400, detail="api_key is required")

    # Imported lazily so the lightweight research endpoints (/research/*) don't drag in the heavy
    # LangGraph/LLM stack at module import time. The analyst path requires it; research does not.
    from .analyst import run_graph

    try:
        return await asyncio.wait_for(
            run_in_threadpool(
                run_graph,
                req.bars,
                req.provider,
                req.api_key,
                req.model,
                req.vision_model,
            ),
            timeout=RUN_TIMEOUT_SECONDS,
        )
    except asyncio.TimeoutError as exc:
        logger.warning("analyst/run timed out after %ss", RUN_TIMEOUT_SECONDS)
        raise HTTPException(status_code=504, detail="analyst run timed out") from exc
    except Exception as exc:  # noqa: BLE001
        logger.exception("analyst/run failed")
        raise HTTPException(status_code=500, detail=str(exc)) from exc


# ── Paper Lab reproduction (Phase 2) ──────────────────────────────────────────────────────
#
# STATIC analysis only: resolve a paper → repo(s), and resolve a repo → a reproduction PLAN.
# These endpoints NEVER execute untrusted repo code (cloning + reading files is OK; running the
# repo happens only inside the C# Docker sandbox). No api_key required — same loopback-only trust
# boundary as the rest of the sidecar. Both fold failures into an empty response, never 500.

# Resolution/planning can clone over the network; give them a generous outer ceiling.
RESEARCH_TIMEOUT_SECONDS = 180


@app.post("/research/resolve", response_model=ResolveResponse)
async def research_resolve(req: ResolveRequest) -> ResolveResponse:
    if not req.url:
        return ResolveResponse.empty("Paper URL is empty.")
    try:
        return await asyncio.wait_for(
            run_in_threadpool(resolve_paper, req.url),
            timeout=RESEARCH_TIMEOUT_SECONDS,
        )
    except asyncio.TimeoutError:
        logger.warning("research/resolve timed out")
        return ResolveResponse.empty("Paper resolution timed out.")
    except Exception as exc:  # noqa: BLE001 — never throw across the seam.
        logger.exception("research/resolve failed")
        return ResolveResponse.empty(f"Paper resolution failed: {exc}")


@app.post("/research/plan", response_model=PlanResponse)
async def research_plan(req: PlanRequest) -> PlanResponse:
    if not req.git_url:
        return PlanResponse.empty("Repo git URL is empty.")
    if not req.commit:
        return PlanResponse.empty("Repo commit pin is empty (required for determinism).")
    try:
        return await asyncio.wait_for(
            run_in_threadpool(plan_repro, req.git_url, req.commit),
            timeout=RESEARCH_TIMEOUT_SECONDS,
        )
    except asyncio.TimeoutError:
        logger.warning("research/plan timed out")
        return PlanResponse.empty("Environment resolution timed out.")
    except Exception as exc:  # noqa: BLE001 — never throw across the seam.
        logger.exception("research/plan failed")
        return PlanResponse.empty(f"Environment resolution failed: {exc}")


def _pick_free_port() -> int:
    """Bind to 127.0.0.1:0 and return whichever ephemeral port the OS hands back."""
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def main(argv: list[str] | None = None) -> None:
    parser = argparse.ArgumentParser(prog="daxalgo-ml")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=None)
    parser.add_argument("--log-level", default="info")
    args = parser.parse_args(argv)

    port = args.port or int(os.environ.get("DAXALGO_ML_PORT", "0")) or _pick_free_port()
    logging.basicConfig(level=args.log_level.upper())
    logger.info("Listening on %s:%d", args.host, port)

    uvicorn.run(
        "daxalgo_ml.app:app",
        host=args.host,
        port=port,
        log_level=args.log_level,
        access_log=False,
    )


if __name__ == "__main__":
    main()
