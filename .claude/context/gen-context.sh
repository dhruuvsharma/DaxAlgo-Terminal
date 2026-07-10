#!/usr/bin/env bash
# gen-context.sh — regenerates the MECHANICAL parts of .claude/context/ from the Windows tree:
#   .claude/context/index/<Group>.md   (per-file rows: path, LOC, project, edition, purpose)
#   .claude/context/symbols/<Proj>.md  (public/protected declaration lines with file:line)
#   <scratch>/deps.tsv                 (project -> ProjectReference list; feeds deps.json by hand)
# Hand-written files (index.md, symbols.md, deps.json, modules/, adr/, RECIPES/, PROTOCOL.md,
# glossary.md) are NOT touched. Run from the repo root:  bash .claude/context/gen-context.sh
# See MAINTENANCE.md for when to run. Git Bash on Windows is fine; needs rg + coreutils.
set -u
# rg shim: Claude Code exposes ripgrep as a shell function (not on PATH for child shells).
if ! command -v rg >/dev/null 2>&1; then
  _cc_bin="${CLAUDE_CODE_EXECPATH:-$HOME/.local/bin/claude.exe}"
  if [ -x "$_cc_bin" ]; then rg() { ARGV0=rg "$_cc_bin" "$@"; }
  else echo "gen-context.sh: ripgrep (rg) not found" >&2; exit 1; fi
fi
S="${1:-$(mktemp -d)}"
[ -d src/windows ] || { echo "gen-context.sh: run from the repo root" >&2; exit 1; }
DATE=$(date +%F)
mkdir -p .claude/context/index .claude/context/symbols

# ---------- 0 · project list + LOC ----------
rg --files -g '*.csproj' src/windows tests samples | tr '\\' '/' | grep -v 'tests/linux' | sort > "$S/csprojs.txt"
awk -F/ '{n=$NF; sub(/[.]csproj$/,"",n); d=$0; sub(/\/[^\/]*$/,"",d); print n "\t" d}' "$S/csprojs.txt" > "$S/projlist.txt"
rg -c '^' -g '*.cs' -g '*.xaml' src/windows tests/TradingTerminal.Tests tests/TradingTerminal.Tests.Headless samples | tr '\\' '/' > "$S/locn.txt"

# ---------- 1 · deps.tsv ----------
: > "$S/deps.tsv"
while IFS=$'\t' read -r name dir; do
  refs=$(rg -o 'ProjectReference Include="[^"]*"' -g '*.csproj' "$dir" --no-filename 2>/dev/null \
        | tr '\\' '/' | sed 's|.*/||; s|[.]csproj"$||; s|.*"||' | sort -u | paste -sd, -)
  printf '%s\t%s\n' "$name" "${refs:-}" >> "$S/deps.tsv"
done < "$S/projlist.txt"

# ---------- 2 · symbols ----------
gen_symbols() { # $1=dir  $2=out.md  $3=title  $4="1" => project-root files only
  local dir="$1" out="$2" title="$3" depth="${4:-}" list
  if [ "$depth" = "1" ]; then
    list=$(rg --files -g '*.cs' --max-depth 1 "$dir" 2>/dev/null | tr '\\' '/' | sort)
  else
    list=$(rg --files -g '*.cs' "$dir" 2>/dev/null | tr '\\' '/' | sort)
  fi
  [ -z "$list" ] && return 0
  {
    echo "# $title — public API surface"
    echo
    echo "Generated $DATE. Declaration lines only; multi-line signatures show their first line;"
    echo 'note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.'
    echo 'Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.'
  } > "$out"
  echo "$list" | while read -r f; do
    [ -n "$f" ] || continue
    awk -v fn="$f" '
      function emit(s,  o) {
        o=s; if (length(o) > 168) o = substr(o,1,165) "..."
        if (!printed) { printf "\n## %s\n```cs\n", fn; printed=1 }
        printf "%5d: %s\n", FNR, o
      }
      BEGIN { printed=0; inif=0; started=0; depth=0 }
      {
        raw=$0; line=raw
        gsub(/^[[:space:]]+/,"",line); gsub(/[[:space:]]+$/,"",line)
        # inside a public interface: members carry no modifier -> print everything meaningful
        if (inif) {
          if (line != "" && line !~ /^[{}]/ && line !~ /^\[/ && line !~ /^\// && line !~ /^#/) emit("    " line)
          depth += split(raw,_a,"{") - 1; depth -= split(raw,_b,"}") - 1
          if (index(raw,"{") > 0) started=1
          if (started && depth <= 0) inif=0
          next
        }
        if (line ~ /^(public|protected)[[:space:]]/) {
          if (line ~ /^(public|protected)[[:space:]]*(get|set)[;{ ]/) next
          emit(line)
          if (line ~ /^(public|protected)[[:space:]]+(partial[[:space:]]+)?interface[[:space:]]/) {
            inif=1; started=0; depth=0
            depth += split(raw,_a,"{") - 1; depth -= split(raw,_b,"}") - 1
            if (index(raw,"{") > 0) started=1
            if (started && depth <= 0) inif=0
          }
        }
      }
      END { if (printed) print "```" }
    ' "$f" >> "$out"
  done
}

while IFS=$'\t' read -r name dir; do
  case "$name" in TradingTerminal.Tests|TradingTerminal.Tests.Headless) continue;; esac
  short="${name#TradingTerminal.}"
  case "$name" in
    TradingTerminal.Core|TradingTerminal.Infrastructure)
      gen_symbols "$dir" ".claude/context/symbols/$short-Root.md" "$name (project-root files)" 1
      for sub in "$dir"/*/; do
        [ -d "$sub" ] || continue
        sb=$(basename "$sub")
        case "$sb" in bin|obj|Properties) continue;; esac
        gen_symbols "$sub" ".claude/context/symbols/$short-$sb.md" "$name / $sb"
      done
      ;;
    *) gen_symbols "$dir" ".claude/context/symbols/$short.md" "$name";;
  esac
done < "$S/projlist.txt"
# drop header-only outputs
for f in .claude/context/symbols/*.md; do
  [ -e "$f" ] || continue
  if [ "$(grep -c '^## ' "$f")" = "0" ]; then rm -f "$f"; fi
done

# ---------- 3 · index ----------
rm -f .claude/context/index/*.tmp
sort -t: -k1,1 "$S/locn.txt" | while IFS=: read -r path loc; do
  case "$path" in
    src/windows/*) group=$(printf '%s' "$path" | cut -d/ -f3); proj=$(printf '%s' "$path" | cut -d/ -f4);;
    tests/*)       group="Tests"; proj=$(printf '%s' "$path" | cut -d/ -f2);;
    samples/*)     group="Samples"; proj=$(printf '%s' "$path" | cut -d/ -f2);;
    *) continue;;
  esac
  case "$proj" in
    TradingTerminal.App.Basic) ed="B";;
    TradingTerminal.App.Intermediate) ed="I";;
    TradingTerminal.Tests*|DaxAlgo.SamplePlugin) ed="dev";;
    *) ed="B I P";;
  esac
  pub="N"; grep -qE '^[[:space:]]*public' "$path" 2>/dev/null && pub="Y"
  purpose=""
  case "$path" in
    *.cs)
      purpose=$(rg -m1 -oP '(?<=<summary>)[^<]{3,}' "$path" 2>/dev/null | head -1)
      if [ -z "$purpose" ]; then
        purpose=$(rg -m1 -A1 -N '/// <summary>' "$path" 2>/dev/null | tail -1 | sed 's|.*///||; s|<[^>]*>||g')
      fi
      if [ -z "$purpose" ]; then
        purpose=$(rg -m1 -oE '(class|record|interface|enum|struct) [A-Za-z_][A-Za-z0-9_]*' "$path" 2>/dev/null | head -1)
      fi;;
    *.xaml)
      purpose=$(rg -m1 -oE 'x:Class="[^"]+"' "$path" 2>/dev/null | sed 's|x:Class=||; s|"||g')
      purpose="XAML $purpose";;
  esac
  purpose=$(printf '%s' "$purpose" | tr -d '\r|`' | awk '{for(i=1;i<=12&&i<=NF;i++) printf "%s ",$i}' | sed 's/[[:space:]]*$//')
  printf '| `%s` | %s | win | %s | %s | %s | %s |\n' "$path" "$loc" "$proj" "$ed" "$pub" "$purpose" >> ".claude/context/index/$group.tmp"
done
for t in .claude/context/index/*.tmp; do
  [ -e "$t" ] || continue
  g=$(basename "$t" .tmp)
  {
    echo "# index/$g — per-file index (Windows tree)"
    echo
    echo "Generated $DATE. Grep by filename/keyword. LOC > 400 => never read whole; rg then ranged reads."
    echo "Editions: B=Basic, I=Intermediate, P=Pro (private repo consumes this tree); dev=test-only."
    echo
    echo "| File | LOC | Tree | Project | Ed | Pub | Purpose |"
    echo "|---|---|---|---|---|---|---|"
    sort -t'|' -k5,5 -k2,2 "$t"
  } > ".claude/context/index/$g.md"
  rm -f "$t"
done

# ---------- 4 · report ----------
echo "== deps.tsv =="; cat "$S/deps.tsv"
echo; echo "== symbols files (lines) =="; wc -l .claude/context/symbols/*.md | sort -nr | head -45
echo; echo "== index files (rows) =="; for f in .claude/context/index/*.md; do echo "$(grep -c '^| ' "$f") $f"; done
