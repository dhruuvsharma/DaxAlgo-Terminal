"""Paper Lab reproduction — STATIC analysis only.

The sidecar resolves a paper URL to its code repo(s) and produces a reproduction *plan*
(base image + setup commands + entrypoint + declared data deps + a deterministic env hash).
It NEVER executes untrusted repo code: cloning + reading manifest files for analysis is OK,
running the repo is NOT — that happens only inside the C# Docker sandbox.

Public entry points:
    from daxalgo_ml.research import resolve_paper, plan_repro
"""

from .plan import plan_repro
from .resolve import resolve_paper

__all__ = ["resolve_paper", "plan_repro"]
