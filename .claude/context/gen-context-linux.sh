#!/usr/bin/env bash
# Generate the isolated, lazy Linux/Avalonia context slice.
#
# Writes only .claude/context/linux/. Windows context files are never read as inputs or replaced.
# Project references are evaluated by MSBuild so inactive WPF-only ItemGroups are excluded.
#
# Usage:
#   bash .claude/context/gen-context-linux.sh
#   bash .claude/context/gen-context-linux.sh --check
set -euo pipefail

MODE="write"
case "${1:-}" in
  "") ;;
  --check) MODE="check" ;;
  *) echo "usage: $0 [--check]" >&2; exit 2 ;;
esac

for cmd in rg dotnet awk sed sort diff mktemp realpath; do
  command -v "$cmd" >/dev/null 2>&1 || { echo "gen-context-linux.sh: missing required command: $cmd" >&2; exit 1; }
done

[ -d src/linux ] && [ -d tests/linux ] && [ -f TradingTerminal.Linux.slnx ] || {
  echo "gen-context-linux.sh: run from the public repository root" >&2
  exit 1
}

ROOT=$(pwd -P)
TARGET=.claude/context/linux
SCRATCH=$(mktemp -d)
WORK="$SCRATCH/work"
OUT="$SCRATCH/linux"
mkdir -p "$WORK" "$OUT/index" "$OUT/symbols"
trap 'rm -rf "$SCRATCH"' EXIT

REVISION=$(git rev-parse HEAD 2>/dev/null || printf 'unknown')
SHORT_REVISION=${REVISION:0:12}

# ---------- project and solution inventory ----------
rg --files -g '*.csproj' src/linux tests/linux | tr '\\' '/' | sort > "$WORK/csprojs.txt"
awk -F/ 'BEGIN { OFS="\t" }
  $1 == "src"   { group=$3; module=$4; role="product" }
  $1 == "tests" { group="Tests"; module=$3; role="test" }
  {
    dir=$0; sub(/\/[^/]*$/, "", dir)
    print module, $0, dir, group, role
  }
' "$WORK/csprojs.txt" > "$WORK/projects.tsv"

cut -f1 "$WORK/projects.tsv" | sort | uniq -d > "$WORK/duplicate-modules.txt"
if [ -s "$WORK/duplicate-modules.txt" ]; then
  echo "gen-context-linux.sh: duplicate Linux module names:" >&2
  cat "$WORK/duplicate-modules.txt" >&2
  exit 1
fi

rg -o 'Project Path="[^"]+"' TradingTerminal.Linux.slnx \
  | sed 's/^Project Path="//; s/"$//; s#\\#/#g' | sort > "$WORK/solution-projects.txt"
cut -f2 "$WORK/projects.tsv" | sort > "$WORK/scope-projects.txt"
if ! diff -u "$WORK/scope-projects.txt" "$WORK/solution-projects.txt" > "$WORK/solution.diff"; then
  echo "gen-context-linux.sh: TradingTerminal.Linux.slnx does not exactly match the Linux project scope" >&2
  cat "$WORK/solution.diff" >&2
  exit 1
fi

# ---------- file inventory and LOC ----------
rg --files -g '*.cs' -g '*.xaml' -g '*.axaml' src/linux tests/linux \
  | tr '\\' '/' | sort > "$WORK/files.txt"
rg -c '^' -g '*.cs' -g '*.xaml' -g '*.axaml' src/linux tests/linux \
  | tr '\\' '/' | sed 's/:/\t/' | sort > "$WORK/loc.tsv"

cut -f1 "$WORK/loc.tsv" > "$WORK/loc-files.txt"
if ! diff -u "$WORK/files.txt" "$WORK/loc-files.txt" > "$WORK/files.diff"; then
  echo "gen-context-linux.sh: LOC inventory does not match the Linux file inventory" >&2
  cat "$WORK/files.diff" >&2
  exit 1
fi

awk -F'\t' 'BEGIN { OFS="\t" }
  {
    n=split($1, p, "/")
    module=(p[1] == "src" ? p[4] : p[3])
    loc[module]+=$2; files[module]++
  }
  END { for (module in loc) print module, loc[module], files[module] }
' "$WORK/loc.tsv" | sort > "$WORK/project-loc.tsv"

awk -F'\t' 'BEGIN { OFS="\t" }
  NR == FNR { loc[$1]=$2; files[$1]=$3; next }
  { print $1, $2, $4, $5, loc[$1]+0, files[$1]+0 }
' "$WORK/project-loc.tsv" "$WORK/projects.tsv" | sort > "$WORK/project-details.tsv"

TOTAL_PROJECTS=$(wc -l < "$WORK/projects.tsv" | tr -d ' ')
TOTAL_FILES=$(wc -l < "$WORK/files.txt" | tr -d ' ')
TOTAL_LOC=$(awk -F'\t' '{ n += $2 } END { print n+0 }' "$WORK/loc.tsv")

# ---------- evaluated ProjectReference graph ----------
# Raw XML includes WPF-only references in false ItemGroup conditions. MSBuild evaluation returns
# only references active for each Linux project's actual TargetFramework.
cut -f1 "$WORK/projects.tsv" | sort > "$WORK/modules.txt"
: > "$WORK/reference-edges.tsv"
while IFS=$'\t' read -r module project_path project_dir group role; do
  evaluation="$WORK/msbuild-$module.json"
  if ! dotnet msbuild "$project_path" -nologo -getItem:ProjectReference > "$evaluation"; then
    echo "gen-context-linux.sh: failed to evaluate ProjectReference items for $project_path" >&2
    exit 1
  fi

  sed -n 's/^[[:space:]]*"Filename": "\([^"]*\)",[[:space:]]*$/\1/p' "$evaluation" \
    | sort -u > "$WORK/refs-$module.txt"
  while IFS= read -r dependency; do
    [ -n "$dependency" ] || continue
    if ! grep -Fxq "$dependency" "$WORK/modules.txt"; then
      echo "gen-context-linux.sh: evaluated reference outside Linux scope: $module -> $dependency" >&2
      exit 1
    fi
    printf '%s\t%s\n' "$module" "$dependency" >> "$WORK/reference-edges.tsv"
  done < "$WORK/refs-$module.txt"

  sed -n 's/^[[:space:]]*"FullPath": "\([^"]*\)",[[:space:]]*$/\1/p' "$evaluation" \
    | sed 's#\\\\#/#g' > "$WORK/ref-paths-$module.txt"
  while IFS= read -r reference_path; do
    [ -n "$reference_path" ] || continue
    shell_path="$reference_path"
    if [[ "$shell_path" =~ ^[A-Za-z]:/ ]] && command -v cygpath >/dev/null 2>&1; then
      shell_path=$(cygpath -u "$shell_path")
    fi
    resolved=$(realpath -m "$shell_path")
    case "$resolved" in
      "$ROOT"/src/linux/*|"$ROOT"/tests/linux/*) ;;
      *) echo "gen-context-linux.sh: evaluated reference escapes Linux scope: $module -> $reference_path" >&2; exit 1 ;;
    esac
    [ -f "$resolved" ] || { echo "gen-context-linux.sh: missing evaluated reference: $module -> $reference_path" >&2; exit 1; }
  done < "$WORK/ref-paths-$module.txt"
done < "$WORK/projects.tsv"
sort -u "$WORK/reference-edges.tsv" -o "$WORK/reference-edges.tsv"

# ---------- per-file index ----------
while IFS=$'\t' read -r path loc; do
  IFS=/ read -r p1 p2 p3 p4 _rest <<< "$path"
  if [ "$p1" = "src" ]; then
    group=$p3; project=$p4; role="product"
  else
    group="Tests"; project=$p3; role="test"
  fi

  public_surface="N"
  case "$path" in
    *.cs) grep -qE '^[[:space:]]*(public|protected)[[:space:]]' "$path" && public_surface="Y" || true ;;
  esac

  purpose=""
  case "$path" in
    *.cs)
      purpose=$(rg -m1 -oP '(?<=<summary>)[^<]{3,}' "$path" 2>/dev/null | head -1 || true)
      if [ -z "$purpose" ]; then
        purpose=$(rg -m1 -A1 -N '/// <summary>' "$path" 2>/dev/null | tail -1 | sed 's|.*///||; s|<[^>]*>||g' || true)
      fi
      if [ -z "$purpose" ]; then
        purpose=$(rg -m1 -oE '(class|record|interface|enum|struct) [A-Za-z_][A-Za-z0-9_]*' "$path" 2>/dev/null | head -1 || true)
      fi
      ;;
    *.xaml|*.axaml)
      purpose=$(rg -m1 -oE 'x:Class="[^"]+"' "$path" 2>/dev/null | sed 's|x:Class=||; s|"||g' || true)
      purpose="UI $purpose"
      ;;
  esac
  purpose=$(printf '%s' "$purpose" | tr -d '\r|`' | awk '{ for (i=1; i<=12 && i<=NF; i++) printf "%s%s", (i==1 ? "" : " "), $i }')
  printf '| `%s` | %s | linux | %s | %s | %s | %s |\n' \
    "$path" "$loc" "$project" "$role" "$public_surface" "$purpose" >> "$WORK/index-$group.rows"
done < "$WORK/loc.tsv"

cut -f3 "$WORK/project-details.tsv" | sort -u > "$WORK/groups.txt"
while IFS= read -r group; do
  rows="$WORK/index-$group.rows"
  {
    echo "# Linux index / $group"
    echo
    echo "Generated from public repository revision \`$SHORT_REVISION\`. Linux/Avalonia slice only."
    echo
    echo "| File | LOC | Tree | Project | Role | Public surface | Purpose |"
    echo "|---|---:|---|---|---|---|---|"
    sort -t'|' -k5,5 -k2,2 "$rows"
  } > "$OUT/index/$group.md"
done < "$WORK/groups.txt"

# ---------- public/protected declaration surfaces ----------
gen_symbols() { # directory, output, title, optional root-only flag
  local directory=$1 output=$2 title=$3 root_only=${4:-0} list
  if [ "$root_only" = "1" ]; then
    list=$(rg --files -g '*.cs' --max-depth 1 "$directory" 2>/dev/null | tr '\\' '/' | sort || true)
  else
    list=$(rg --files -g '*.cs' "$directory" 2>/dev/null | tr '\\' '/' | sort || true)
  fi
  [ -n "$list" ] || return 0
  {
    echo "# $title — public API surface (Linux/Avalonia)"
    echo
    echo "Generated from public repository revision \`$SHORT_REVISION\`. Declaration lines only;"
    echo "multi-line signatures show their first line. \`[ObservableProperty]\` generated properties are not listed."
  } > "$output"

  mapfile -t symbol_files <<< "$list"
  awk '
      function emit(s, o) {
        o=s; if (length(o) > 168) o=substr(o,1,165) "..."
        if (!printed) { printf "\n## %s\n```cs\n", fn; printed=1 }
        printf "%5d: %s\n", FNR, o
      }
      function close_file() { if (printed) print "```" }
      FNR == 1 {
        if (NR > 1) close_file()
        fn=FILENAME; printed=0; inif=0; started=0; depth=0
      }
      {
        raw=$0; line=raw
        gsub(/^[[:space:]]+/, "", line); gsub(/[[:space:]]+$/, "", line)
        if (inif) {
          if (line != "" && line !~ /^[{}]/ && line !~ /^\[/ && line !~ /^\/\// && line !~ /^#/) emit("    " line)
          depth += split(raw,a,"{") - 1; depth -= split(raw,b,"}") - 1
          if (index(raw,"{") > 0) started=1
          if (started && depth <= 0) inif=0
          next
        }
        if (line ~ /^(public|protected)[[:space:]]/) {
          if (line ~ /^(public|protected)[[:space:]]*(get|set)[;{ ]/) next
          emit(line)
          if (line ~ /^(public|protected)[[:space:]]+(partial[[:space:]]+)?interface[[:space:]]/) {
            inif=1; started=0; depth=0
            depth += split(raw,a,"{") - 1; depth -= split(raw,b,"}") - 1
            if (index(raw,"{") > 0) started=1
            if (started && depth <= 0) inif=0
          }
        }
      }
      END { close_file() }
    ' "${symbol_files[@]}" >> "$output"

  grep -q '^## ' "$output" || rm -f "$output"
}

while IFS=$'\t' read -r module project_path project_dir group role; do
  [ "$role" = "test" ] && continue
  short=${module#TradingTerminal.}
  case "$module" in
    TradingTerminal.Core|TradingTerminal.Infrastructure)
      gen_symbols "$project_dir" "$OUT/symbols/$short-Root.md" "$module / project root" 1
      for subdir in "$project_dir"/*/; do
        [ -d "$subdir" ] || continue
        subsection=$(basename "$subdir")
        case "$subsection" in bin|obj|Properties) continue ;; esac
        gen_symbols "$subdir" "$OUT/symbols/$short-$subsection.md" "$module / $subsection"
      done
      ;;
    *) gen_symbols "$project_dir" "$OUT/symbols/$short.md" "$module" ;;
  esac
done < "$WORK/projects.tsv"

# ---------- generated masters ----------
{
  echo "# Linux context index"
  echo
  echo "Lazy slice for \`src/linux/\` and \`tests/linux/\`. Load this only for Linux/Avalonia work;"
  echo "the Windows context remains in the parent directory. Paths are relative to the public repository root."
  echo
  echo "Generated from public repository revision \`$SHORT_REVISION\`: **$TOTAL_PROJECTS projects / $TOTAL_FILES files / $TOTAL_LOC LOC**."
  echo "Indexed extensions: \`.cs\`, \`.xaml\`, \`.axaml\`. Regenerate with \`bash .claude/context/gen-context-linux.sh\`."
  echo
  echo "## Groups"
  echo
  echo "| Index | Rows | Projects |"
  echo "|---|---:|---|"
  while IFS= read -r group; do
    rows=$(grep -c '^| `' "$OUT/index/$group.md")
    projects=$(awk -F'\t' -v g="$group" '$3 == g { printf "%s%s", (seen++ ? ", " : ""), $1 }' "$WORK/project-details.tsv")
    printf '| [%s](index/%s.md) | %s | %s |\n' "$group" "$group" "$rows" "$projects"
  done < "$WORK/groups.txt"
  echo
  echo "## Project rollup"
  echo
  echo "| Project | Group | Role | LOC | Files |"
  echo "|---|---|---|---:|---:|"
  sort -t$'\t' -k5,5nr -k1,1 "$WORK/project-details.tsv" \
    | awk -F'\t' '{ printf "| %s | %s | %s | %d | %d |\n", $1, $3, $4, $5, $6 }'
} > "$OUT/index.md"

SYMBOL_FILES=$(find "$OUT/symbols" -maxdepth 1 -type f -name '*.md' | wc -l | tr -d ' ')
SYMBOL_LINES=$(find "$OUT/symbols" -maxdepth 1 -type f -name '*.md' -exec grep -hEc '^[[:space:]]*[0-9]+:' {} \; \
  | awk '{ n += $1 } END { print n+0 }')
CORE_SYMBOL_FILES=$(find "$OUT/symbols" -maxdepth 1 -type f -name 'Core-*.md' | wc -l | tr -d ' ')
INFRA_SYMBOL_FILES=$(find "$OUT/symbols" -maxdepth 1 -type f -name 'Infrastructure-*.md' | wc -l | tr -d ' ')
PROJECT_SYMBOL_FILES=$((SYMBOL_FILES - CORE_SYMBOL_FILES - INFRA_SYMBOL_FILES))
{
  echo "# Linux symbol index"
  echo
  echo "Generated public/protected declaration surfaces for the Linux/Avalonia tree:"
  echo "**$SYMBOL_FILES files / $SYMBOL_LINES declaration lines**. Grep this directory before opening source:"
  echo
  echo '```sh'
  echo "rg -n \"SubscribeTicksAsync\" .claude/context/linux/symbols/"
  echo '```'
  echo
  echo "| Family | Files | Naming |"
  echo "|---|---:|---|"
  echo "| Core | $CORE_SYMBOL_FILES | \`symbols/Core-<area>.md\` |"
  echo "| Infrastructure | $INFRA_SYMBOL_FILES | \`symbols/Infrastructure-<area>.md\` |"
  echo "| Other product projects | $PROJECT_SYMBOL_FILES | \`symbols/<project>.md\` |"
  echo
  echo "Tests are intentionally omitted from the API surface. Multi-line signatures show their first line;"
  echo "source-generated properties such as \`[ObservableProperty]\` are not visible to the extractor."
} > "$OUT/symbols.md"

# deps.json is fully mechanical: forward and reverse edges come from evaluated ProjectReference items.
awk -F'\t' -v revision="$REVISION" -v projects="$TOTAL_PROJECTS" -v files="$TOTAL_FILES" -v loc="$TOTAL_LOC" '
  function esc(s) { gsub(/\\/, "\\\\", s); gsub(/"/, "\\\"", s); return s }
  function array(csv,    n,a,i,out) {
    if (csv == "") return "[]"
    n=split(csv,a,","); out="["
    for (i=1; i<=n; i++) out=out (i>1 ? ", " : "") "\"" esc(a[i]) "\""
    return out "]"
  }
  FNR == NR {
    deps[$1]=deps[$1] (deps[$1] == "" ? "" : ",") $2
    reverse[$2]=reverse[$2] (reverse[$2] == "" ? "" : ",") $1
    next
  }
  {
    count++
    module[count]=$1; path[count]=$2; group[count]=$3; role[count]=$4; ploc[count]=$5; pfiles[count]=$6
  }
  END {
    print "{"
    print "  \"_meta\": {"
    print "    \"tree\": \"linux\","
    print "    \"sourceRevision\": \"" esc(revision) "\","
    print "    \"solution\": \"TradingTerminal.Linux.slnx\","
    print "    \"source\": \"MSBuild-evaluated ProjectReference items from src/linux and tests/linux\","
    print "    \"projectCount\": " projects ","
    print "    \"indexedFileCount\": " files ","
    print "    \"indexedLoc\": " loc
    print "  },"
    print "  \"modules\": ["
    for (i=1; i<=count; i++) {
      n=split(reverse[module[i]], tmp, ",")
      if (reverse[module[i]] == "") n=0
      blast=(n >= 6 ? "high" : (n >= 2 ? "med" : "low"))
      print "    {"
      print "      \"id\": \"linux/" esc(module[i]) "\","
      print "      \"module\": \"" esc(module[i]) "\","
      print "      \"path\": \"" esc(path[i]) "\","
      print "      \"group\": \"" esc(group[i]) "\","
      print "      \"role\": \"" esc(role[i]) "\","
      print "      \"loc\": " ploc[i] ","
      print "      \"files\": " pfiles[i] ","
      print "      \"dependsOn\": " array(deps[module[i]]) ","
      print "      \"dependedBy\": " array(reverse[module[i]]) ","
      print "      \"blastRadius\": \"" blast "\""
      print "    }" (i<count ? "," : "")
    }
    print "  ]"
    print "}"
  }
' "$WORK/reference-edges.tsv" "$WORK/project-details.tsv" > "$OUT/deps.json"

# Every generated row must name an existing source file; generation into a fresh staging tree
# guarantees there can be no orphan group or symbol outputs.
INDEX_ROWS=$(grep -h '^| `' "$OUT"/index/*.md | wc -l | tr -d ' ')
[ "$INDEX_ROWS" = "$TOTAL_FILES" ] || {
  echo "gen-context-linux.sh: generated $INDEX_ROWS index rows for $TOTAL_FILES source files" >&2
  exit 1
}

if [ "$MODE" = "check" ]; then
  [ -d "$TARGET" ] || { echo "gen-context-linux.sh: $TARGET does not exist; regenerate it" >&2; exit 1; }
  if diff -qr "$OUT" "$TARGET"; then
    echo "Linux context is current: $TOTAL_PROJECTS projects, $TOTAL_FILES files, $TOTAL_LOC LOC."
    exit 0
  fi
  echo "gen-context-linux.sh: Linux context is stale; regenerate with bash .claude/context/gen-context-linux.sh" >&2
  exit 1
fi

# Replace only the isolated Linux subtree. Staging first makes orphan removal deterministic and
# leaves the previous tree recoverable until the new tree is in place.
PARENT=$(dirname "$TARGET")
NEW="$PARENT/.linux.new.$$"
OLD="$PARENT/.linux.old.$$"
[ ! -e "$NEW" ] && [ ! -e "$OLD" ] || { echo "gen-context-linux.sh: temporary replacement path already exists" >&2; exit 1; }
cp -R "$OUT" "$NEW"
if [ -e "$TARGET" ]; then mv "$TARGET" "$OLD"; fi
if ! mv "$NEW" "$TARGET"; then
  [ ! -e "$OLD" ] || mv "$OLD" "$TARGET"
  exit 1
fi
rm -rf "$OLD"
echo "Generated $TARGET: $TOTAL_PROJECTS projects, $TOTAL_FILES files, $TOTAL_LOC LOC."
