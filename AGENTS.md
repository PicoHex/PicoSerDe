# AGENTS.md

Orientation for agents (and humans) working in this repository. Keep it short and factual, and **add to it
when you learn something the hard way**. Process ledgers under `.superpowers/` are gitignored
(`.gitignore:415`), so they never leave this machine: anything that must outlive the workspace belongs in a
tracked doc or a commit message instead.

## Build & test

- Solution build, and the bar is **0 warning / 0 error**: `mkdir -p ./artifacts/nupkg`, then
  `dotnet build PicoSerDe.slnx -c Debug -m:1`. The `mkdir` is not optional — `NuGet.config:6` lists
  `artifacts/nupkg/` as a local source, so a fresh checkout fails restore with nine `NU1301 ... doesn't exist`
  errors before it builds anything (CI does the same `mkdir` at `.github/workflows/ci.yml:47-49`). `-m:1` is
  deliberate: one project graph at a time keeps the incremental generator's output readable and the warning
  count honest.
- `dotnet test PicoSerDe.slnx -c Debug --no-progress` is the convenient shape, but **CI does not use it**: TUnit
  on Microsoft.Testing.Platform shows flaky discovery in this repo, so each test project is executed directly —
  `dotnet run --project <proj>.csproj -c Release --no-build -p:PublishAot=false`
  (`.github/workflows/ci.yml:51-55`). When a local run reports fewer tests than CI, that is the form to use.
- Runner arguments go **after** `--`: `... -- --treenode-filter '/*/*/Name'`.
- Release dry run: `pwsh -NoProfile -File scripts/release.ps1 -DryRun` — prints `API changed` and
  `next version`, writes nothing and tags nothing (`scripts/release.ps1:309-316`).

## Format capability rule (iron rule)

**Never support a construct the wire format itself cannot express, and never simulate one with an encoding
trick.** No comma-joined strings, no escape gymnastics, no synthesised wrapper keys, no private extensions to a
format's grammar. A shape the format cannot hold is **dropped during extraction and reported** (`PICOSERDE003` /
`PICOSERDE004`, `Warning`, `Location.None`, so `#pragma` cannot silence it) — the drop shows up in the build log
instead of surfacing later as a mysteriously empty property (`README.md:494-501`, `:511-514`).

1. **⛔ is a format fact, and it is terminal.** `FormatCapabilities.ImpossibleMarker`
   (`shared/Gen/FormatCapabilities.cs:192`); the diagnostic title is *this format cannot hold a type cycle*
   (`shared/Gen/GenInfrastructure.cs:373-375`, id `PICOSERDE003`). No generator change can help: the reader's
   only ways out are a different format or a different type.
2. **⏸ is our debt, and it is reopenable.** `FormatCapabilities.DeferredMarker`
   (`shared/Gen/FormatCapabilities.cs:200`); the title is *not implemented yet (this format can hold a
   cycle)* (`shared/Gen/GenInfrastructure.cs:388-390`). It must not blame the format and must not send the
   reader off to change a type that is perfectly expressible.
3. **Calling a debt a "format limit" is wrong, and calling a format limit a debt is wrong too.** The two
   verdicts live in one place on purpose — no other file in `shared/Gen` may spell ⛔ or ⏸
   (`VerdictMarkerGuardTests.Capability_glyphs_are_spelled_only_in_the_capability_model`). Code that needs the
   answer asks the facts table, it does not write a sentence: `AnonCollectionIsFormatFact` derives the verdict
   from `CanRepresent` so "our progress must never be written as a format fact"
   (`FormatCapabilities.cs:333-345`).
4. **The `Format limits` matrix is the one authority.** `README.md:503-509` plus footnotes ¹²³
   (`:523-542`) is the fact source for "can this format express this shape". A README, a sub-README or a
   **skill** that contradicts a cell is the thing that is wrong — never edit the matrix to match prose. The
   matrix is itself pinned to the code: `MatrixCells` uses the **terminal** effects, so a shape that is
   currently dropped as a debt still reads ✅ there, and `FormatCapabilitiesTests
   .Matrix_cells_are_the_spec_section6_terminal_matrix` fails the moment the two disagree
   (`FormatCapabilities.cs:353-378`).
5. **The reverse duty: if the format can express it and the framework cannot, that is a debt and it must be
   registered** (a `reopen:` pointer in the message, a ❌-free cell in the matrix) — not dressed up as a format
   fact. YAML writes the `null` literal, so "YAML drops a nullable leaf" was never a limit; it was a stale
   claim that outlived the writer's `WriteNull` in three places of `PicoYaml/README.md` (`59537c1`) while the
   root README's cell and footnote ² said otherwise (`YamlNullLiteralTests`,
   `YamlNestedListDiagnosticTests.Yaml_NestedNullableElementMember_IsEmittedWithTheNullBranch`,
   `NestedNullableElementTests.cs:85`, matrix footnote ² at `README.md:528-532`).

## Working rules

- **A test must not encode the environment it happens to run in.** This machine is UTC+8 (`China Standard
  Time`); CI runs on UTC and reports the invariant culture (`LANG=C.UTF-8`, `b0985d1`) ⇒ **local green is not CI
  green**. Pin an oracle, not an answer:
  `DateTime_MaxValue_WithAnOffset_IsStillAnError` locked a UTC+8-only answer and was red on every UTC runner
  since 2026-10-29 — it is now `..._AgreesWithTheReferenceParser`, and the must-fail half moved to a payload
  that overflows in *every* zone (`68549df`; `JsonScalarReadFastPathTests.cs:216` under
  `PicoJetson/tests/PicoJetson.Integration.Tests`). The same class of pin cost a second test its green
  (`b056c09`).
- **Numbers in protocol text and docs never go through the ambient culture** — and `InvariantCulture` is not
  automatically the answer (`PercentPositivePattern 0` prints `12 %`, which is what the Linux leg produced).
  Every figure in `shared/BenchProtocol.cs` goes through one `FormatNumber` over a declared `NumberFormatInfo`
  (`BenchProtocol.cs:835`, `b0985d1`, `tests/PicoBench.Protocol.Tests/ProtocolCultureTests.cs`).
- **Check links mechanically, and make the label match the target.** Resolve `file#anchor` against the target's
  headings; a label that says "the root README's section" while pointing into `docs/` is worse than a dead
  link — that was eight dead anchors (`00cafab`) plus eight labels that named a destination they did not
  point at (`4afebde`).
- **One fact written in several places is edited in all of them in one commit** (root README, sub-README,
  skill). The stale YAML-null sentence above was a 26-line rewrite of one file with two neighbouring bullets
  saying the same thing (`59537c1`); the eight stale labels cost eight edits (`4afebde`).
- **A number in a doc must be traceable to the archive it came from**, and recomputed by script before it is
  published. The per-scenario tables live in `docs/perf/measurement.md`; when the sixteen README rows that
  carried an em dash were filled, each multiplier was recomputed as pair ÷ pair rather than carried over
  (`b31b305`).
- **Verdict glyphs, diagnostics and capability answers have exactly one home** — extend the model, not a
  message (`VerdictMarkerGuardTests`).

## Releasing

- `pwsh -NoProfile -File scripts/release.ps1 -DryRun` first; read `API changed` / `next version` before touching
  anything. `-Push` refreshes the `api/` baselines and commits the refresh, then tags and pushes.
- **A baseline holds the surface of the LAST RELEASE** (`scripts/release.ps1:15-17`), so a mid-cycle refresh
  hides a public-API change from the version rule. Refresh it in the release commit, not before.
- **Clean up `.worktrees/` before releasing.** The packable-project scan sweeps the tree, and a leftover
  worktree — a second copy carrying the same `PackageId` — aborted releases with *duplicate PackageId across
  packable projects* until `b55c14f` made the scan skip `.worktrees/`, `.superpowers/`, `artifacts/` and every
  dot-prefixed directory (`scripts/release.ps1:110-122`). That exclusion is a safety net, not a licence.
- **Docs and skills ship in the release commit.** The tag workflow runs **no tests at all** — the `pack` job
  (`.github/workflows/release.yml:20`) only calls `dotnet pack`, and `publish` (`:123`) hangs off `needs: pack`
  (`:125`) — so `main` must be green **before** the tag, and the API diff is walked before it too. Note the
  script's own header still says the workflow "runs the tests" (`scripts/release.ps1:19-20`); the file is what
  counts. The skills (`picohex-*`) live outside this repository: nothing here fails when one goes stale, which
  is why `picohex-release`'s step 4 makes them part of the release.

## Environment quirks (this developer machine)

- `.worktrees/` and `.superpowers/` are gitignored (`.gitignore:415`, `:423`): a lane's files are invisible to
  `git status` in the main checkout, so check the right tree before believing a clean status.
- PowerShell 7 (`pwsh`) runs `scripts/*.ps1`; `dotnet` is 10.0.x (`global.json`), and the tool manifest is
  `dotnet-tools.json` (`dotnet tool restore` once per clone).
