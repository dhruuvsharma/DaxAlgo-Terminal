"""Resolve a paper URL → arXiv metadata + candidate code repos.

STATIC, read-only network lookups only:
  * arXiv Atom API for the paper's id / title / canonical URL.
  * Papers-with-Code API for repos linked to that arXiv id.
  * As a fallback, GitHub-style links scraped out of the abstract text.

No repo is cloned or executed here — that is the plan stage's (read-only) and the C# sandbox's
(execution) job. Every network call goes through the module-level ``httpx.get`` so tests can
monkeypatch it; any failure degrades to an empty repos list rather than raising.
"""

from __future__ import annotations

import logging
import re
import xml.etree.ElementTree as ET

import httpx

from ..schemas import ResolvedPaper, ResolvedRepo, ResolveResponse

logger = logging.getLogger("daxalgo_ml.research")

# arXiv id forms: 2507.22712 (new) or hep-th/9901001 (old, with optional version suffix).
_ARXIV_ID_RE = re.compile(
    r"(\d{4}\.\d{4,5})(v\d+)?|([a-z\-]+(?:\.[A-Z]{2})?/\d{7})(v\d+)?",
    re.IGNORECASE,
)
# GitHub / GitLab repo links scraped from the abstract as a last-resort source.
_REPO_LINK_RE = re.compile(
    r"https?://(?:github\.com|gitlab\.com)/[A-Za-z0-9_.\-]+/[A-Za-z0-9_.\-]+",
    re.IGNORECASE,
)

_ARXIV_API = "http://export.arxiv.org/api/query"
_PWC_API = "https://paperswithcode.com/api/v1/papers/{arxiv_id}/repositories/"

# Conservative per-call timeout; the C# side has its own outer ceiling.
_HTTP_TIMEOUT = 15.0


def extract_arxiv_id(url_or_id: str) -> str | None:
    """Pull a bare arXiv id out of a URL or raw id string. Returns None when none is present."""
    if not url_or_id:
        return None
    m = _ARXIV_ID_RE.search(url_or_id.strip())
    if not m:
        return None
    # Group 1 = new-style id, group 3 = old-style id; strip any version suffix.
    return m.group(1) or m.group(3)


def resolve_paper(url: str) -> ResolveResponse:
    """Resolve a paper URL to a :class:`ResolveResponse`. Never raises — degrades to empty."""
    arxiv_id = extract_arxiv_id(url)
    if not arxiv_id:
        return ResolveResponse.empty("Could not extract an arXiv id from the URL.")

    paper, abstract = _fetch_arxiv_metadata(arxiv_id, url)
    if paper is None:
        return ResolveResponse.empty(f"arXiv metadata lookup failed for {arxiv_id}.")

    repos = _discover_repos(arxiv_id, abstract)
    return ResolveResponse(resolved=True, paper=paper, repos=repos, error=None)


def _fetch_arxiv_metadata(arxiv_id: str, fallback_url: str) -> tuple[ResolvedPaper | None, str]:
    """Query the arXiv Atom API for title + canonical URL. Returns (paper, abstract_text)."""
    try:
        resp = httpx.get(
            _ARXIV_API,
            params={"id_list": arxiv_id, "max_results": 1},
            timeout=_HTTP_TIMEOUT,
        )
        resp.raise_for_status()
        return _parse_arxiv_atom(resp.text, arxiv_id, fallback_url)
    except Exception:  # noqa: BLE001 — network / parse failures degrade, never raise.
        logger.debug("arXiv metadata lookup failed for %s", arxiv_id, exc_info=True)
        # Degrade to a minimal paper record so resolution still succeeds with no repos.
        return (
            ResolvedPaper(arxiv_id=arxiv_id, title="", url=fallback_url or f"https://arxiv.org/abs/{arxiv_id}"),
            "",
        )


def _parse_arxiv_atom(xml_text: str, arxiv_id: str, fallback_url: str) -> tuple[ResolvedPaper | None, str]:
    ns = {"atom": "http://www.w3.org/2005/Atom"}
    root = ET.fromstring(xml_text)
    entry = root.find("atom:entry", ns)
    if entry is None:
        return None, ""

    title_el = entry.find("atom:title", ns)
    summary_el = entry.find("atom:summary", ns)
    id_el = entry.find("atom:id", ns)

    title = (title_el.text or "").strip() if title_el is not None else ""
    abstract = (summary_el.text or "").strip() if summary_el is not None else ""
    url = (id_el.text or "").strip() if id_el is not None else ""
    if not url:
        url = fallback_url or f"https://arxiv.org/abs/{arxiv_id}"

    return ResolvedPaper(arxiv_id=arxiv_id, title=title, url=url), abstract


def _discover_repos(arxiv_id: str, abstract: str) -> list[ResolvedRepo]:
    """Find candidate code repos: Papers-with-Code first, then abstract links. Degrades to []."""
    repos: list[ResolvedRepo] = []
    seen: set[str] = set()

    def _key(url: str) -> str:
        # Normalise so "repo", "repo/", and "repo.git" dedup to one entry.
        k = url.rstrip("/").lower()
        return k[:-4] if k.endswith(".git") else k

    for repo in _papers_with_code_repos(arxiv_id):
        key = _key(repo.git_url)
        if key and key not in seen:
            seen.add(key)
            repos.append(repo)

    for url in _REPO_LINK_RE.findall(abstract or ""):
        git_url = url if url.endswith(".git") else url + ".git"
        key = _key(url)
        if key not in seen:
            seen.add(key)
            # No commit known from a bare link — empty pin; the C# side filters these out.
            repos.append(ResolvedRepo(git_url=git_url, commit=""))

    return repos


def _papers_with_code_repos(arxiv_id: str) -> list[ResolvedRepo]:
    try:
        resp = httpx.get(_PWC_API.format(arxiv_id=arxiv_id), timeout=_HTTP_TIMEOUT)
        if resp.status_code != 200:
            return []
        data = resp.json()
    except Exception:  # noqa: BLE001
        logger.debug("Papers-with-Code lookup failed for %s", arxiv_id, exc_info=True)
        return []

    results = data.get("results", []) if isinstance(data, dict) else []
    out: list[ResolvedRepo] = []
    for item in results:
        url = (item or {}).get("url") or ""
        if not url:
            continue
        git_url = url if url.endswith(".git") else url.rstrip("/") + ".git"
        # PwC does not pin a commit; the env-resolution stage needs a pin, so leave it empty here
        # and let the user/C# layer supply or reject it (the C# RepoFetcher requires a hex SHA).
        out.append(ResolvedRepo(git_url=git_url, commit=""))
    return out


__all__ = ["resolve_paper", "extract_arxiv_id"]
