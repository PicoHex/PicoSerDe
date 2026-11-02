#!/usr/bin/env bash
# tools/release-build.sh — the release guard for PicoSerDe.
#
# Owner rule: **main is the only release branch.** A .nupkg built from any other
# tree is an artifact that lies about its age: the zip timestamp is today's, the
# code inside is whatever that side branch happened to be — see docs/releasing.md
# for the 69-commits-behind release this rule exists to prevent. Git cannot enforce
# that rule (a side branch can always run `dotnet pack`), so this script is the
# mechanism: it refuses to pack anywhere but on a clean main, writes the packages
# OUTSIDE the repository, and prints a sha256 for every one so the bytes can later
# be compared with what was actually published.
#
# Exit codes are part of the contract (docs/releasing.md quotes them):
#   0  packed; every artifact's name + sha256 listed
#   1  environment problem (not a git repo, no dotnet, no sha256 tool)
#   2  HEAD is not on branch main (a detached HEAD counts as "not main")
#   3  working tree is not clean (git status --porcelain is non-empty)
#   4  OUT resolves inside the repository
#   5  the build/pack failed, or the pack produced no .nupkg
#   6  the build left tracked residue in the repository
#
# Usage:
#   bash tools/release-build.sh                  # OUT=${TMPDIR:-/tmp}/rel-<short-sha>
#   OUT=/some/dir bash tools/release-build.sh    # elsewhere, still outside the repo
#   VERSION=2026.12.2 bash tools/release-build.sh # default: Directory.Build.props

set -uo pipefail

fail() {
  local code=$1
  shift
  printf '%s\n' "$@" >&2
  exit "$code"
}

command -v git >/dev/null 2>&1 || fail 1 "release guard: git is not on PATH"
repo=$(git rev-parse --show-toplevel 2>/dev/null) || fail 1 "release guard: not inside a git repository"
cd "$repo" || fail 1 "release guard: cannot cd to $repo"
repo_abs=$(pwd -P)

# ---------------------------------------------------------------------------
# R1 — main is the only release branch.
# Checked first, on purpose: a dirty side branch must be refused as "not main"
# (exit 2), not as "dirty" (exit 3) — the branch is the reason that matters.
# ---------------------------------------------------------------------------
branch=$(git rev-parse --abbrev-ref HEAD 2>/dev/null)
if [ "$branch" != "main" ]; then
  if [ "$branch" = "HEAD" ]; then
    branch_shown="(detached HEAD at $(git rev-parse --short HEAD))"
  else
    branch_shown="$branch"
  fi
  fail 2 \
    "RELEASE GUARD REFUSED (exit 2): HEAD is not on branch 'main'." \
    "  branch : $branch_shown" \
    "  HEAD   : $(git rev-parse HEAD)" \
    "  repo   : $repo_abs" \
    "" \
    "main is the only release branch. A package built here would carry a fresh" \
    "timestamp over a tree that main has already moved past (docs/releasing.md:" \
    "'69 commits behind main, and the date on the file was new'). Merge into main" \
    "and run this script there."
fi

# ---------------------------------------------------------------------------
# R2 — the tree must be the committed tree.
# ---------------------------------------------------------------------------
status=$(git status --porcelain)
if [ -n "$status" ]; then
  total=$(printf '%s\n' "$status" | wc -l | tr -d ' ')
  {
    echo "RELEASE GUARD REFUSED (exit 3): the working tree is not clean."
    echo "  $total path(s) modified or untracked, first 3:"
    printf '%s\n' "$status" | head -3 | sed 's/^/    /'
    echo ""
    echo "A release artifact must be reproducible from a commit, so nothing may be"
    echo "packed that exists only in the working tree. Commit or stash, re-run."
  } >&2
  exit 3
fi

# ---------------------------------------------------------------------------
# R3 — the facts of this build go into the log before anything is compiled.
# ---------------------------------------------------------------------------
head_sha=$(git rev-parse HEAD)
short_sha=$(git rev-parse --short HEAD)
command -v dotnet >/dev/null 2>&1 || fail 1 "release guard: dotnet is not on PATH"
dotnet_ver=$(dotnet --version 2>/dev/null)

echo "=== PicoSerDe release build ==============================================="
echo "repo    : $repo_abs"
echo "branch  : main"
echo "HEAD    : $head_sha"
echo "time    : $(date -u +%Y-%m-%dT%H:%M:%SZ) (UTC)"
echo "dotnet  : ${dotnet_ver:-unknown}"

# ---------------------------------------------------------------------------
# R4 — artifacts go outside the repository, and nowhere else.
# An in-tree package path is how a stale .nupkg turns into a "build output" that
# nobody re-checks; the repo's own artifacts/ folder is a local NuGet source
# (NuGet.config), so it is exactly the place a release must not write to.
# ---------------------------------------------------------------------------
OUT=${OUT:-${TMPDIR:-/tmp}/rel-$short_sha}
mkdir -p "$OUT" || fail 1 "release guard: cannot create OUT=$OUT"
out_abs=$(cd "$OUT" && pwd -P) || fail 1 "release guard: cannot resolve OUT=$OUT"
case "$out_abs/" in
"$repo_abs"/*)
  rmdir "$OUT" 2>/dev/null
  fail 4 \
    "RELEASE GUARD REFUSED (exit 4): OUT resolves inside the repository." \
    "  OUT  : $out_abs" \
    "  repo : $repo_abs" \
    "" \
    "Release artifacts must land outside the tree. Pass OUT=/some/path/outside," \
    "or leave OUT unset for the default (\${TMPDIR:-\/tmp}/rel-<short-sha>)."
  ;;
esac
echo "OUT     : $out_abs"

# NuGet.config lists artifacts/nupkg/ as a local source, so a fresh clone fails
# restore with NU1301 unless that directory exists — the same mkdir CI does
# (.github/workflows/ci.yml). It is gitignored, like bin/ and obj/: build
# intermediates stay in their normal gitignored places, so no tracked residue.
mkdir -p artifacts/nupkg

# ---------------------------------------------------------------------------
# R6 — build + pack. The solution decides what is packable (IsPackable=true:
# eleven projects today), so a newly packable project is covered without a
# hand-edited list drifting away from release.yml's. Any failure below is fatal.
# ---------------------------------------------------------------------------
pack_args=(PicoSerDe.slnx --configuration Release --output "$out_abs" -p:ContinuousIntegrationBuild=true)
if [ -n "${VERSION:-}" ]; then
  pack_args+=("-p:Version=$VERSION")
fi

echo "--- dotnet pack ${pack_args[*]}"
dotnet pack "${pack_args[@]}"
pack_rc=$?
if [ "$pack_rc" -ne 0 ]; then
  fail 5 \
    "" \
    "RELEASE GUARD FAILED (exit 5): dotnet pack returned $pack_rc." \
    "  HEAD : $head_sha" \
    "  OUT  : $out_abs" \
    "Nothing may be published from a red pack."
fi

shopt -s nullglob
artifacts=("$out_abs"/*.nupkg "$out_abs"/*.snupkg)
shopt -u nullglob
if [ "${#artifacts[@]}" -eq 0 ]; then
  fail 5 \
    "" \
    "RELEASE GUARD FAILED (exit 5): dotnet pack succeeded but produced no .nupkg." \
    "  OUT : $out_abs" \
    "An empty release is worse than a red one: it looks like a pass."
fi

# ---------------------------------------------------------------------------
# R5 — name + sha256 for every artifact. Record these with the tag
# (docs/releasing.md); they are the only proof later of which bytes shipped.
# ---------------------------------------------------------------------------
sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d' ' -f1
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$1" | cut -d' ' -f1
  elif command -v openssl >/dev/null 2>&1; then
    openssl dgst -sha256 "$1" | sed 's/^.*= *//'
  elif command -v certutil >/dev/null 2>&1; then
    certutil -hashfile "$(cygpath -w "$1" 2>/dev/null || printf '%s' "$1")" SHA256 | sed -n 2p | tr -d ' \r'
  else
    return 1
  fi
}

echo ""
echo "=== artifacts (${#artifacts[@]}) ==========================================="
for f in "${artifacts[@]}"; do
  sum=$(sha256_of "$f") || fail 1 "release guard: no sha256 tool (sha256sum, shasum, openssl or certutil)"
  printf '%-44s %s\n' "${f##*/}" "$sum"
done

# ---------------------------------------------------------------------------
# R4, second half — "no residue in the repo" is asserted, not hoped for.
# ---------------------------------------------------------------------------
residue=$(git status --porcelain)
if [ -n "$residue" ]; then
  {
    echo ""
    echo "RELEASE GUARD FAILED (exit 6): the build left tracked residue in the repo."
    printf '%s\n' "$residue" | head -3 | sed 's/^/    /'
    echo "A release build must not modify the tree it was run from."
  } >&2
  exit 6
fi

echo ""
echo "OK: ${#artifacts[@]} artifact(s) in $out_abs"
echo "    HEAD $head_sha on main, tree clean after the build."
echo "    Record the name + sha256 lines above with the tag (docs/releasing.md)."
exit 0
