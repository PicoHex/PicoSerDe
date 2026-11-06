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

1. **⛔ is terminal: either the format cannot hold the shape, or the type cannot be instantiated at all.**
   `FormatCapabilities.ImpossibleMarker` (`shared/Gen/FormatCapabilities.cs:260`); the diagnostic title is *this format cannot hold a type cycle*
   (`shared/Gen/GenInfrastructure.cs:373-375`, id `PICOSERDE003`) — a type with no instance (abstract, a static class, no accessible parameterless
   ctor, `DropVerdict.TypeUnconstructible`) is ⛔ on the same grounds. No generator change can help: a different format or a different type.
2. **⏸ is our debt, and it is reopenable.** `FormatCapabilities.DeferredMarker`
   (`shared/Gen/FormatCapabilities.cs:268`); the title is *not implemented yet (this format can hold a
   cycle)* (`shared/Gen/GenInfrastructure.cs:388-390`). It must not blame the format and must not send the
   reader off to change a type that is perfectly expressible.
3. **Calling a debt a "format limit" is wrong, and calling a format limit a debt is wrong too.** The two
   verdicts live in one place on purpose — no other file in `shared/Gen` may spell ⛔ or ⏸
   (`VerdictMarkerGuardTests.Capability_glyphs_are_spelled_only_in_the_capability_model`). Code that needs the
   answer asks the facts table, it does not write a sentence: `AnonCollectionIsFormatFact` derives the verdict
   from `CapabilityOf` (`Capability.Impossible`, never `IsRepresentable` — a DEFERRED cell is the format's yes
   and our not-yet) so "our progress must never be written as a format fact"
   (`FormatCapabilities.cs:476-487`).
4. **The `Format limits` matrix is the one authority.** `README.md:503-509` plus footnotes ¹²³
   (`:523-542`) is the fact source for "can this format express this shape". A README, a sub-README or a
   **skill** that contradicts a cell is the thing that is wrong — never edit the matrix to match prose. The
   matrix is itself pinned to the code: `MatrixCells` uses the **terminal** effects, so a shape that is
   currently dropped as a debt still reads ✅ there — with ONE exception, the `Deferred` cell (the format can
   hold the shape and what is missing is a wire convention, not an implementation; S2 × INI today), which
   stays ❌ because ✅ means the generated code both writes and reads the shape — and `FormatCapabilitiesTests
   .Matrix_cells_are_the_spec_section6_terminal_matrix` fails the moment the two disagree
   (`FormatCapabilities.cs:533-540`).
5. **The reverse duty: if the format can express it and the framework cannot, that is a debt and it must be
   registered** (a `reopen:` pointer in the message, and a matrix cell that never blames the format: ✅ for a
   debt payable in code, ❌ for rule 4's ONE exception, whose way out is a wire convention) — never dressed up
   as a format fact. YAML writes the `null` literal, so "YAML drops a nullable leaf" was never a limit; it was
   a stale claim that outlived the writer's `WriteNull` in three places of `PicoYaml/README.md` (`59537c1`)
   while the root README's cell and footnote ² said otherwise (`YamlNullLiteralTests`,
   `YamlNestedListDiagnosticTests.Yaml_NestedNullableElementMember_IsEmittedWithTheNullBranch`,
   `NestedNullableElementTests.cs:85`, matrix footnote ² at `README.md:528-532`).
6. **"This is how it is" is only allowed about the format standard.** A sentence that is true because *we*
   chose it — a fusion, a shortcut, a shape we happen to emit — is a ⏸ debt or an ordinary design choice,
   never a format fact; item 3 fixes where a verdict may be written, this fixes which verdict a sentence is.
   One question decides it: **would a conforming encoder or decoder of this format have to do the same?** If
   not, *we* did it. Today's instance: `- name: x` in a sequence is a block mapping in the YAML grammar, so a
   conforming token stream owes `ObjectStart`/`PropertyName`/value, yet we emit a bare `String("name: x")`
   (`YamlReader.cs:1069-1079`, pinned in `YamlSequenceTokenKindTests`) ⇒ our fusion, ⏸, not "contract-legal".
7. **`DesignChoice` is the fourth verdict: the format can express the shape, we choose not to support it, and
   we do not intend to** — the "ordinary design choice" item 6 leaves room for. The message says *not supported
   by this generator by design*: never "this format cannot hold the shape" (that is ⛔), never a `reopen:`
   pointer (that is ⏸). Its cell is ❌, unlike a code-payable ⏸ debt, which rule 4 reads as terminal ✅. The
   chain: ① must a conforming encoder or decoder of this format do it? no ⇒ ② do we intend to? yes ⇒ ⏸; no ⇒ `DesignChoice`.

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

## Evidence (a baseline is a gate, not a proof)

**A byte-identical baseline proves only the shapes its corpus covers**, and that is the whole rule: the corpus
decides which branches of the changed code the gate can even reach, so a branch it never enters comes back
green because it was not looked at, not because it was looked at and found equal. A pass is a *gate* — "nothing
the corpus shows has changed", never "nothing has changed". Cite it **with its coverage** (these branches are
exercised, these are not); a bare "96 baseline files … hash for hash" is an unproven claim wearing a proof's
clothes.

1. **The counter-example this section exists for.** `60e090a` collapsed twenty-eight hand-written attribute
   lookups — INI 8, JSON 5, TOML 5, YAML 5 (twenty-three across those four) plus five inline copies in
   MessagePack — into one `shared/Gen/AttributeLookup.cs`, and cited as its evidence
   "the generated output is byte-identical: 96 baseline files … hash for hash". The corpus those files come
   from (`tests/FormatCapabilities.Probe/generated-baseline/`) is **deliberately attribute-free** —
   `tests/FormatCapabilities.Probe/Models.cs:15-19` says why ("The models are deliberately plain POCOs …
   Adding an attribute here would multiply the emitted code by five"), and the receipt is one command:
   `grep -c '^\s*\[' tests/FormatCapabilities.Probe/Models.cs` → `0`. So every replaced lookup
   (`GetIniDateTimeFormat`, `GetJsonPropertyName`, `GetTomlConverter`, `GetYamlConverter`, `GetMsgPackKey`, …)
   returned null for every one of the probe's 216 symbols: the gate exercised those lookups' **negative path
   and nothing else**. The merge was still safe, but not for the reason it gave — the proof was a differential
   harness kept outside the tree (1200 comparisons over three corpora, plus four mutants that each turned it
   red).
2. **When a change lands on a path the corpus never enters, the baseline is empty there — add evidence of
   another kind, and name it.** A differential comparison, a targeted unit test, a mutant that proves the
   harness can go red. Then put the gap in the commit message, in these words or their equivalent: "the
   baseline cannot see this; the evidence is X". A green gate with no coverage behind it is worse than no
   gate, because the next reader believes it.
3. **A blind spot gets written down where the next reader runs into it.** `Models.cs:15-19` is why the probe
   corpus is narrow; `docs/fused-emission-registry.md` is the same lesson for "implemented, never called" and
   `FusedMemberEmissionGuardTests` keeps that register honest. This rule is that register's cousin — both turn
   an invisible gap into a line somebody has to read. Nothing in the tree re-hashes `generated-baseline/`
   against a stored manifest either, so before/after sha256 is a *manual* step: it can be blind and silent at
   the same time.

## Releasing

- `pwsh -NoProfile -File scripts/release.ps1 -DryRun` first; read `API changed` / `next version` before touching
  anything. `-Push` refreshes the `api/` baselines and commits the refresh, then tags and pushes.
- **A baseline holds the surface of the LAST RELEASE** (`scripts/release.ps1:15-17`), so a mid-cycle refresh
  hides a public-API change from the version rule. Refresh it in the release commit, not before.
- **Clean up `.worktrees/` before releasing.** The packable-project scan sweeps the tree, and a leftover
  worktree — a second copy carrying the same `PackageId` — aborted releases with *duplicate PackageId across
  packable projects* until `b55c14f` made the scan skip `.worktrees/`, `.superpowers/`, `artifacts/` and every
  dot-prefixed directory (`scripts/release.ps1:136-158`; `b55c14f` added the block at `:113-122` of that time,
  and it has since moved down the file). That exclusion is a safety net, not a licence.
- **Publishing authenticates over OIDC, not a stored key** (the `publish` job in `release.yml`). The job
  carries `id-token: write`, and `NuGet/login@v1` (`id: login`) trades a GitHub OIDC token for a **1-hour**
  API key that `dotnet nuget push` reads from `steps.login.outputs.NUGET_API_KEY`; `secrets.NUGET_API_KEY`
  is gone — the v2026.12.0 attempt is what expired (`403 The specified API key has expired`). Two things
  follow. The login step has to stay **immediately before** the push: one OIDC token buys exactly one key,
  and that key dies in an hour. And nuget.org must carry a matching **trusted publishing policy** —
  Repository Owner `PicoHex`, Repository `PicoSerDe`, Workflow File `release.yml` (**file name only**, never
  the `.github/workflows/` path), Environment `release` (this job sets `environment: release`). The one
  secret left is `NUGET_USER`: the nuget.org **profile name**, not the email address.
- **Docs and skills ship in the release commit.** The gate is the `verify` job in `release.yml`
  (`.github/workflows/release.yml:37`): restore, Release build, and all ten test projects, on `ubuntu-latest`.
  `pack` waits on it (`jobs.pack.needs: verify`, `release.yml:76`) and `publish` on `pack`
  (`release.yml:180`), so a red test is a failed release rather than a published one — v2026.11.0 went out
  while CI had been red for three days, which is what this job exists to stop.
- **The tag is still the wrong place to discover a first failure, so keep the manual check as the first of the
  two gates.** `verify` is one platform and build+tests only; the five-platform matrix, `aot-validation` and
  `benchmark-aot` are CI's job. Before tagging: `main` green in the full CI run
  (`gh run list --workflow=ci.yml --limit 1` → `completed / success` for the commit you are tagging), and the
  API diff walked (`-DryRun`). Note the script's own header still describes the pre-gate workflow — *"There is
  no test job and no dependency on the CI workflow"* (`scripts/release.ps1:30-41`); the file is what counts. The
  skills (`picohex-*`) live outside this repository: nothing here fails when one goes stale, which is why
  `picohex-release`'s step 4 makes them part of the release.

## Environment quirks (this developer machine)

- `.worktrees/` and `.superpowers/` are gitignored (`.gitignore:415`, `:423`): a lane's files are invisible to
  `git status` in the main checkout, so check the right tree before believing a clean status.
- PowerShell 7 (`pwsh`) runs `scripts/*.ps1`; `dotnet` is 10.0.x (`global.json`), and the tool manifest is
  `dotnet-tools.json` (`dotnet tool restore` once per clone).
