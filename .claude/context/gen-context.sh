#!/usr/bin/env bash
# gen-context.sh — regenerates the MECHANICAL parts of .claude/context/ from the Windows tree:
#   .claude/context/index/<Group>.md   (per-file rows: path, LOC, project, edition, purpose)
#   .claude/context/symbols/<Proj>.md  (public/protected declaration lines with file:line)
#   <scratch>/deps.tsv                 (project -> ProjectReference list; feeds deps.json by hand)
# Hand-written files (index.md, symbols.md, deps.json, modules/, adr/, RECIPES/, PROTOCOL.md,
# glossary.md) are NOT touched. The locked context manager invokes this script for sync/check;
# direct calls delegate back to it so simultaneous terminals cannot expose partial output.
# See MAINTENANCE.md for when to run. Git Bash on Windows is fine; needs rg + coreutils.
set -euo pipefail
SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd -- "$SCRIPT_DIR/../.." && pwd)
# rg shim: Claude Code exposes ripgrep as a shell function (not on PATH for child shells).
if ! command -v rg >/dev/null 2>&1; then
  _cc_bin="${CLAUDE_CODE_EXECPATH:-$HOME/.local/bin/claude.exe}"
  if [ -x "$_cc_bin" ]; then rg() { ARGV0=rg "$_cc_bin" "$@"; }
  else echo "gen-context.sh: ripgrep (rg) not found" >&2; exit 1; fi
fi
MODE="write"
if [ "${1:-}" = "--check" ]; then MODE="check"; shift; fi
if [ "$#" -ne 0 ]; then echo "Usage: $0 [--check]" >&2; exit 2; fi
if [ "${DAXALGO_CONTEXT_LOCK_HELD:-}" != "1" ]; then
  action=sync; [ "$MODE" = check ] && action=deep-check
  if command -v powershell.exe >/dev/null 2>&1; then
    exec powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$SCRIPT_DIR/manage-context.ps1" "$action"
  elif command -v pwsh >/dev/null 2>&1; then
    exec pwsh -NoProfile -File "$SCRIPT_DIR/manage-context.ps1" "$action"
  fi
  echo "gen-context.sh: PowerShell is required to acquire the context-manager lock." >&2
  exit 2
fi
cd -- "$REPO_ROOT"
S="$(mktemp -d)"
trap 'rm -rf "$S"' EXIT
[ -d src/windows ] || { echo "gen-context.sh: run from the repo root" >&2; exit 1; }
OUT="$S/output"
INDEX_OUT="$OUT/index"
SYMBOLS_OUT="$OUT/symbols"
mkdir -p "$INDEX_OUT" "$SYMBOLS_OUT"

# ---------- 0 · project list + LOC ----------
rg --files -g '*.csproj' src/windows tests samples | tr '\\' '/' | sort > "$S/csprojs.txt"
awk -F/ '{n=$NF; sub(/[.]csproj$/,"",n); d=$0; sub(/\/[^\/]*$/,"",d); print n "\t" d}' "$S/csprojs.txt" > "$S/projlist.txt"
rg -c '^' -g '*.cs' -g '*.xaml' src/windows tests/TradingTerminal.Tests tests/TradingTerminal.Tests.Headless samples | tr '\\' '/' > "$S/locn.txt"

# ---------- 1 · deps.tsv ----------
: > "$S/deps.tsv"
while IFS=$'\t' read -r name dir; do
  refs=$({ rg -o 'ProjectReference Include="[^"]*"' -g '*.csproj' "$dir" --no-filename 2>/dev/null || true; } \
        | tr '\\' '/' | sed 's|.*/||; s|[.]csproj"$||; s|.*"||' | sort -u | paste -sd, -)
  printf '%s\t%s\n' "$name" "${refs:-}" >> "$S/deps.tsv"
done < "$S/projlist.txt"

# ---------- 2 · symbols ----------
gen_symbols() { # $1=dir  $2=out.md  $3=title  $4="1" => project-root files only
  local dir="$1" out="$2" title="$3" depth="${4:-}" list
  if [ "$depth" = "1" ]; then
    list=$({ rg --files -g '*.cs' --max-depth 1 "$dir" 2>/dev/null || true; } | tr '\\' '/' | sort)
  else
    list=$({ rg --files -g '*.cs' "$dir" 2>/dev/null || true; } | tr '\\' '/' | sort)
  fi
  [ -z "$list" ] && return 0
  {
    echo "# $title — public API surface"
    echo
    echo "Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;"
    echo 'note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.'
    echo 'Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.'
  } > "$out"
  printf '%s\n' "$list" | awk '
      function emit(s,  o) {
        o=s; if (length(o) > 168) o = substr(o,1,165) "..."
        if (!printed) { printf "\n## %s\n```cs\n", fn; printed=1 }
        printf "%5d: %s\n", lineno, o
      }
      function scan_file(path) {
        fn=path; printed=0; inif=0; started=0; depth=0; lineno=0
        while ((getline raw < path) > 0) {
          lineno++
          line=raw
          gsub(/^[[:space:]]+/,"",line); gsub(/[[:space:]]+$/,"",line)
          # inside a public interface: members carry no modifier -> print everything meaningful
          if (inif) {
            if (line != "" && line !~ /^[{}]/ && line !~ /^\[/ && line !~ /^\// && line !~ /^#/) emit("    " line)
            depth += split(raw,_a,"{") - 1; depth -= split(raw,_b,"}") - 1
            if (index(raw,"{") > 0) started=1
            if (started && depth <= 0) inif=0
            continue
          }
          if (line ~ /^(public|protected)[[:space:]]/) {
            if (line ~ /^(public|protected)[[:space:]]*(get|set)[;{ ]/) continue
            emit(line)
            if (line ~ /^(public|protected)[[:space:]]+(partial[[:space:]]+)?interface[[:space:]]/) {
              inif=1; started=0; depth=0
              depth += split(raw,_a,"{") - 1; depth -= split(raw,_b,"}") - 1
              if (index(raw,"{") > 0) started=1
              if (started && depth <= 0) inif=0
            }
          }
        }
        close(path)
        if (printed) print "```"
      }
      NF { scan_file($0) }
    ' >> "$out"
}

while IFS=$'\t' read -r name dir; do
  case "$name" in TradingTerminal.Tests|TradingTerminal.Tests.Headless) continue;; esac
  short="${name#TradingTerminal.}"
  case "$name" in
    TradingTerminal.Core|TradingTerminal.Infrastructure)
      gen_symbols "$dir" "$SYMBOLS_OUT/$short-Root.md" "$name (project-root files)" 1
      for sub in "$dir"/*/; do
        [ -d "$sub" ] || continue
        sb=$(basename "$sub")
        case "$sb" in bin|obj|Properties) continue;; esac
        gen_symbols "$sub" "$SYMBOLS_OUT/$short-$sb.md" "$name / $sb"
      done
      ;;
    *) gen_symbols "$dir" "$SYMBOLS_OUT/$short.md" "$name";;
  esac
done < "$S/projlist.txt"
# drop header-only outputs
for f in "$SYMBOLS_OUT"/*.md; do
  [ -e "$f" ] || continue
  if [ "$(grep -c '^## ' "$f")" = "0" ]; then rm -f "$f"; fi
done

# ---------- 3 · index ----------
sort -t: -k1,1 "$S/locn.txt" | awk -v out="$INDEX_OUT" '
  function normalize_purpose(value,  words,n,i,result) {
    gsub(/[\r|`]/,"",value)
    gsub(/^[[:space:]]+|[[:space:]]+$/,"",value)
    n=split(value,words,/[[:space:]]+/)
    result=""
    for (i=1; i<=n && i<=12; i++) if (words[i] != "") result=result (result == "" ? "" : " ") words[i]
    return result
  }
  function inspect(path,  raw,line,next_is_summary) {
    pub="N"; same_summary=""; block_summary=""; next_is_summary=0
    while ((getline raw < path) > 0) {
      line=raw
      if (line ~ /^[[:space:]]*public/) pub="Y"
      if (same_summary == "" && match(line,/<summary>[^<][^<][^<]+/))
        same_summary=substr(line,RSTART+9,RLENGTH-9)
      if (next_is_summary && block_summary == "") {
        block_summary=line
        sub(/^.*\/\/\//,"",block_summary)
        gsub(/<[^>]*>/,"",block_summary)
        next_is_summary=0
      }
      if (block_summary == "" && line ~ /\/\/\/ <summary>/) next_is_summary=1
    }
    close(path)
  }
  {
    sep=index($0,":"); if (!sep) next
    path=substr($0,1,sep-1); loc=substr($0,sep+1)
    split(path,parts,"/")
    if (parts[1] == "src" && parts[2] == "windows") { group=parts[3]; proj=parts[4] }
    else if (parts[1] == "tests") { group="Tests"; proj=parts[2] }
    else if (parts[1] == "samples") { group="Samples"; proj=parts[2] }
    else next

    if (proj == "TradingTerminal.App.Basic") ed="B"
    else if (proj == "TradingTerminal.App.Intermediate") ed="I"
    else if (proj ~ /^TradingTerminal.Tests/ || proj == "DaxAlgo.SamplePlugin") ed="dev"
    else ed="B I P"

    inspect(path)
    if (path ~ /[.]cs$/) purpose=(same_summary != "" ? same_summary : block_summary)
    else purpose="XAML"
    purpose=normalize_purpose(purpose)
    target=out "/" group ".tmp"
    printf "| `%s` | %s | win | %s | %s | %s | %s |\n", path,loc,proj,ed,pub,purpose >> target
    close(target)
  }
'
for t in "$INDEX_OUT"/*.tmp; do
  [ -e "$t" ] || continue
  g=$(basename "$t" .tmp)
  {
    echo "# index/$g — per-file index (Windows tree)"
    echo
    echo "Generated from the current source tree. Grep by filename/keyword. LOC > 400 => never read whole; rg then ranged reads."
    echo "Editions: B=Basic, I=Intermediate, P=Pro (private repo consumes this tree); dev=test-only."
    echo
    echo "| File | LOC | Tree | Project | Ed | Pub | Purpose |"
    echo "|---|---|---|---|---|---|---|"
    sort -t'|' -k5,5 -k2,2 "$t"
  } > "$INDEX_OUT/$g.md"
  rm -f "$t"
done

# ---------- 4 · report ----------
if [ "$MODE" = "check" ]; then
  if ! diff -qr .claude/context/index "$INDEX_OUT" >/dev/null ||
     ! diff -qr .claude/context/symbols "$SYMBOLS_OUT" >/dev/null; then
    echo "gen-context.sh: generated context is stale" >&2
    diff -qr .claude/context/index "$INDEX_OUT" || true
    diff -qr .claude/context/symbols "$SYMBOLS_OUT" || true
    exit 1
  fi
else
  for name in index symbols; do
    target=".claude/context/$name"
    source="$OUT/$name"
    replacement=".claude/context/.$name.next"
    previous=".claude/context/.$name.previous"
    if [ ! -e "$target" ] && [ -e "$previous" ]; then mv "$previous" "$target"; fi
    rm -rf -- "$replacement"
    if [ -e "$target" ]; then rm -rf -- "$previous"; fi
    cp -R "$source" "$replacement"
    if [ -e "$target" ]; then mv "$target" "$previous"; fi
    if mv "$replacement" "$target"; then
      rm -rf -- "$previous"
    else
      [ ! -e "$target" ] && [ -e "$previous" ] && mv "$previous" "$target"
      exit 1
    fi
  done
fi

echo "== deps.tsv =="; cat "$S/deps.tsv"
echo; echo "== symbols files (lines) =="; wc -l "$SYMBOLS_OUT"/*.md | sort -nr | head -45
echo; echo "== index files (rows) =="; for f in "$INDEX_OUT"/*.md; do echo "$(grep -c '^| ' "$f") $f"; done
