# Releasing PicoSerDe

## main is the only release branch

A package may be built from `main`, and from nothing else: not a feature branch, not a
`docs/*` branch, not a git worktree, not a detached HEAD. The rule is not stylistic — a
`.nupkg` carries the time it was packed, not the time its code was written, so an artifact
from a side branch is *newer by metadata and older by content*. Packing one commit twice
proves it: the payload bytes are identical, the zip bytes are not, because every entry is
stamped with the pack time. That is the failure this page exists to stop (see
[Why this rule exists](#why-this-rule-exists)).

Packing is done **only** with `tools/release-build.sh`, run from the repository root:

```bash
bash tools/release-build.sh            # → ${TMPDIR:-/tmp}/rel-<short-sha>
OUT=/some/dir/outside bash tools/release-build.sh
```

The guard refuses the cases that produced the bad artifacts instead of trusting the
operator: it exits **2** off `main`, **3** on a dirty tree, **4** if `OUT` resolves inside
the repository, **5** if the build/pack fails or produces no package, and **6** if the build
left tracked residue behind. It prints HEAD, branch, UTC time, and then the name and
sha256 of every `.nupkg`. `OUT` is outside the tree on purpose: `artifacts/` inside the repo
is a **local NuGet source** (`NuGet.config`), exactly the place a stale package becomes a
"build output" nobody re-checks.

Not a release: `dotnet pack` typed by hand, a `.nupkg` from `artifacts/nupkg/`, a package
that a teammate built on their own branch and handed over. If the bytes did not come out of
the guard on `main`, they are not release bytes.

## Before you tag: three checks

1. **The commit is `main`'s tip.** `git fetch origin && git status -sb` reads
   `## main...origin/main` with nothing ahead or behind, and `git rev-parse HEAD` equals
   `git rev-parse origin/main`. Then confirm `main` is green in the **full** CI run
   (`gh run list --workflow=ci.yml --limit 1` → `completed / success` for that commit).
   The `verify` job in `.github/workflows/release.yml` is a one-platform backstop, not a
   substitute — the tag is the wrong place to discover a first failure.
2. **The artifact set came out of the guard.** `bash tools/release-build.sh` exits **0** and
   ends with `OK: N artifact(s) in <OUT>`, `HEAD <sha> on main, tree clean after the build`.
   Keep the `OUT` directory — it is the only thing that may be published.
3. **The sha256 goes with the tag.** Paste the guard's `name + sha256` lines into
   `docs/release-notes/v<version>.md` **in the release commit**, so the tag itself carries
   the hashes of the bytes it shipped. This has to be recorded at pack time, not
   reconstructed: the same commit packed twice gives equal payloads and **different**
   `.nupkg` hashes (the zip entries carry the pack time), so a hash you did not write down
   is a hash you cannot recover. After publishing, `sha256sum` the artifacts downloaded
   from nuget.org / the GitHub release and compare — equal hashes mean the published bytes
   are the bytes the guard built on `main`. This record is also the step that would have
   caught the incident below on the day it happened.

## Why this rule exists

A release was once packed from a side branch. The packages came out with **today's
timestamp**, so they looked like the newest build in the folder and were handed around as
one; the code inside them was **69 commits behind `main`**. Nothing in the artifact said so
— a `.nupkg` is a timestamped zip, not a claim about a commit. The result was worse than a
broken release: the bits were stale, the metadata was fresh, and the two could not be told
apart after the fact, because nobody had written down which commit the hashes belonged to.

Three properties made that possible, and the guard removes all three:

- **the branch was not checked** → the script refuses anything but `main` (exit 2);
- **the tree was not checked** → it refuses an uncommitted tree (exit 3), so what is packed
  is a commit and can be rebuilt from it;
- **the artifact's identity was not recorded** → it prints HEAD and a sha256 per package,
  and the release notes carry those hashes next to the tag.

The rule is a mechanism, not a habit, because a habit is exactly what failed: the person who
packed from the side branch was not careless, they were doing what the tooling allowed.

See also: `AGENTS.md` → *Releasing* (version rule, `scripts/release.ps1`, OIDC publishing)
and `.github/workflows/release.yml` (`verify` → `pack` → `publish`).
