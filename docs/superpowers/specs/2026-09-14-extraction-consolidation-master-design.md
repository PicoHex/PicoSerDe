# PicoSerDe Extraction & Consolidation — Master Spec (Phase 5+)

**Status**: Proposed — awaiting user review
**Date**: 2026-09-14
**Topic**: Finish the one-way extraction of cross-format abstractions into shared layers (`PicoSerDe.Core`, new `PicoSerDe.Gen.Common`), remove dead per-format surface, and deduplicate the 5 format modules' structure.
**Scope**: Master planning spec. Each workstream (W1–W6) gets its own detailed spec + plan before implementation. This document fixes the direction, the boundaries, the dependency order, and the compatibility policy.
**Predecessor**: `docs/superpowers/specs/2026-08-22-design-simplification-master-design.md` (Phases 1–4). That effort landed facade consolidation, options threading, attribute consolidation, `ITokenReader`, audit-P0 hardening, TokenType cleanup, and AOT build centralization. This spec supersedes its remaining/incomplete claims (see §3 fact 12) and covers what it did not address.

---

## 1. Motivation

The 2026-09-14 architecture review found that the shared layer stops at the halfway point again: the 5 format modules still carry near-identical generator pipelines, three streaming implementations, copy-pasted reader/writer cores, inert options surface, and source-linking build plumbing. At the same time the review established the repo's long-term direction must be **one-way extraction**: concepts move from format components into shared layers, never back down. This spec encodes that law and executes the remaining extractions.

Three review conclusions drive the scope:

1. **Structural duplication is concentrated and measurable** — not diffuse. 5 generator `Initialize` skeletons are 77–100% identical; 4 local streaming loops duplicate one that already exists in Core; 3 facades are 6-lines-apart clones; 5 csproj + 5 identical build targets are boilerplate.
2. **Some of the duplication hides a correctness gap** — the 4 non-JSON streaming delegates lack the `partial` resume parameter that JSON has; code-path analysis indicates INI/TOML/YAML can lose already-parsed properties when a chunk boundary falls mid-object (to be confirmed/refuted by a repro test; §3 fact 2).
3. **The shared generator layer is still source-linked, not referenced** — `shared/Gen/*.cs` is compiled into each of the 5 `.Gen` assemblies via `<Compile Include>`, and the shared code carries global mutable state (`AssemblyPrefix`) plus 15 format-name comparisons. A real shared component is both the dedup and the removal of those anti-patterns.

## 2. Long-Term Architecture Principle (governing law for this spec and all future work)

**One-way extraction.** Abstract concepts are continually extracted from format components and consolidated into a shared layer. The reverse (moving a shared abstraction down into a format component, or duplicating shared source) is never allowed.

| Concept kind | Destination | Rationale |
|---|---|---|
| Runtime cross-format abstraction | `PicoSerDe.Core` (net10.0) | Single implementation consumed by all 5 formats; AOT-first contracts live here |
| Generator-side cross-format abstraction | **new** `PicoSerDe.Gen.Common` (netstandard2.0) | The 5 `.Gen` projects must stay netstandard2.0 (Roslyn host) and cannot reference net10.0 Core; the two carriers serve one law |

**Extraction criterion** (prevents speculative abstraction):
- A concept is extracted when it is consumed by ≥2 components, **or** it has been declared a cross-format contract.
- A single-format implementation with no contract status stays in its component — or is deleted if dead. It is not "extracted" speculatively.
- Applied here: `ScalarCodec` is a cross-format contract candidate (generic scalar codec; the JSON-only extended-scalar gating is a *feature-scope* decision in the resolver, not a layering defect) → stays in Core. `Indented` for INI/TOML/YAML has neither consumers nor contract status → deleted (W3); if a second format ever implements it, the capability is defined in `SerOptions` **first**, then implemented.

**Shared-layer purity rules** (enforced by W2):
- No format names or `format == "..."` switches in shared code — format differences travel as capability data (`FormatCapabilities`), the string tag survives only for diagnostics/naming.
- No global mutable state in shared generator code — per-compilation inputs (assembly name) travel as pipeline data, never as statics.
- No new cross-project `<Compile Include>` source linking — shared code is a referenced component.

## 3. Verified Baseline (HEAD `fe17628`, 2026-09-14)

All facts below were re-verified against the working tree with grep/diff/brace-matching. No build or test run was part of the review.

| # | Fact | Evidence |
|---|------|----------|
| 1 | 5 generator `Initialize` methods are 138–139 lines each; after format-name normalization they are 100%/99%/95%/77% identical (MsgPack vs Toml, Ini vs MsgPack/Toml, Json vs Ini, Ini vs Yaml). MsgPack additionally uses implicit `static` modifiers while the other 4 use `private static` | brace-matched bodies; `difflib` per pair |
| 2 | Streaming: Core `StreamingRunner` has exactly one caller (JSON, `JsonSerializer.cs:119`). INI/MsgPack/TOML/YAML each keep a local `DeserializeStreamingCore` (29/46/29/29 lines) + local `StreamingFunc<T>` + `StreamingCache<T>`. JSON's generated delegate takes `T? partial` (`result = partial ?? new T()`); the other 4 take no partial and allocate `new T()` on every invocation. Only `JsonReader` has `RewindToPropertyName`; the INI/TOML/YAML pipe loops resume from the exported position while the delegate restarts — **suspected mid-object property loss on chunk boundaries (code-path analysis, NOT yet reproduced)**. MsgPack is immune by accumulating all bytes and re-parsing per chunk (documented O(n²)) | `StreamingRunner.RunAsync` grep; serializer line counts; generator emit sites (`JsonSerializerGenerator` 4410/4417 vs `IniSerializerGenerator` 873+, `TomlSerializerGenerator` 959+, `YamlSerializerGenerator` 1626+) |
| 3 | `IniSerializer`/`TomlSerializer`/`YamlSerializer` are 107 lines each; name-normalized diffs are 6/6/2 lines (Ini↔Toml, Ini↔Yaml, Toml↔Yaml) — the duplicated streaming block + forwarders | `diff` after name normalization |
| 4 | `TomlReader.cs` (1685 L) vs `YamlReader.cs` (2353 L): 723 matching lines (31%); `TryGetInt32`/`TryGetInt64`/`TrySkip`/`Skip` are byte-identical, `TryGetBool` 75%. Writer primitives `WriteRaw`/`WriteByte`/`WriteNewLine` are 100%/71–100% identical across Ini/Toml/Yaml/Json | method-body `difflib` after name normalization |
| 5 | `IniOptions`/`TomlOptions`/`YamlOptions` + 3 `*IgnoreCondition` enums are unreferenced by production code: no options parameter exists on those facades (grep finds zero `Options` mentions in the 3 serializer files); generated handlers accept `SerOptions? options` but never read it; readers/writers have no options fields. Only JSON and MsgPack consume options (`jw.Options?.DefaultIgnoreCondition`, `mw.Options?.DefaultIgnoreCondition`). The only src reference to the 3 classes is an unreachable mapping in `AnonTypeHandler.cs:305-309` (`FacadeTakesOptions=false` for those formats). README:286 documents them as working | grep/read |
| 6 | Repo-wide there is exactly one `[Obsolete]` member: `IniKeyAttribute.Name` (`PicoIni/src/PicoIni/IniKeyAttribute.cs:10`) | grep |
| 7 | `TextHelpers.ScanUntilLineEnd` (shared/TextHelpers.cs:32) is defined and unit-tested but has zero production callers; the audit-P0 fixes inline the scan at 5 sites (`IniReader.cs:362-367`, `TomlReader.cs:648,657`, `YamlReader.cs:631,928`) | grep |
| 8 | Build: 5 runtime csproj are 90% identical, 5 `.Gen` csproj 89%, 5 `build/*.targets` byte-identical. Each `.Gen` csproj `Compile Include`s the same 5 `shared/Gen` files. **Both** the runtime packages and the `.Gen` packages embed the format analyzer DLL (`PicoJetson/src/PicoJetson/PicoJetson.csproj` pack block; `Ensure*GenDllExists` checks only the single DLL) | csproj diffs |
| 9 | Tests: `UnsignedIntegerTests` ×5 (81%/75%/57% identical), `PolymorphicTests` ×5, `AttributeDiscoveryTests` ×5, `OptionsTests` ×4, `GlobalUsings.cs` ×9. INI has **zero** `DeserializeFromStreamAsync` tests; TOML/YAML/MsgPack one each | file listing + diffs |
| 10 | Generator infra: `GenInfrastructure`/`AnonTypeHandler`/`TypeKindResolver` are `internal`, source-linked into all 5 `.Gen` assemblies. `AssemblyPrefix` is a static mutable property written by 5 generators (JSON twice) from output callbacks and read in `GenInfrastructure`, `AnonTypeHandler` and 4 JSON emit sites. `FormatTag` is threaded through 20+ call sites; `TypeKindResolver` has 12 `format ==` checks (11 `json`, 1 `msgpack`); `AnonTypeHandler` has 3 format switches (writer names 296-300, options names 305-309, YAML sequence methods 526-533) | grep/read |
| 11 | Source links outside `shared/Gen`: `TextHelpers.cs` → Core only; `BenchModels.cs` → 5 benchmark projects; `CrossValidationModels.cs` → 6 test projects. `IsExternalInit.cs` is needed only by the shared files (the 5 `.Gen` projects declare no records/init themselves; the one PicoIni hit is a comment) | csproj grep + record grep |
| 12 | The local (gitignored) predecessor design marks Phase 1 "Implemented", but at HEAD the 4-format streaming migration (its Task 7) and the "other 4 formats gain a nullable options parameter" item are not done. This spec carries its own HEAD baseline and does not rely on that document | commit `bdb5ad3` touched only JSON+Core; HEAD greps |
| 13 | `PicoJetson.Integration.Tests` accesses `internal GenInfrastructure` via `InternalsVisibleTo("PicoJetson.Integration.Tests")` on `PicoJetson.Gen.csproj` (with `OutputItemType="Analyzer" ReferenceOutputAssembly="true"`). `build/*.targets` only set `InterceptorsNamespaces` (no analyzer path references) | csproj + test source |
| 14 | Central package version: `Microsoft.CodeAnalysis.CSharp` 5.9.0 (`Directory.Packages.props:19`) | read |

## 4. Cross-Cutting Constraints

- **Public API preservation**: consumer source keeps compiling across every workstream, **except** the explicit breaks enumerated in §7. SG-generated code is internal to the pipeline and may change freely.
- **No obsolete shims** (user decision): anything already `[Obsolete]` is removed outright in this effort; new breaks are direct removals with migration notes, not deprecation dances.
- **Zero-allocation hot paths**: registry dispatch, reader token loops, and the streaming loop stay allocation-free. No per-call closures on serialize/deserialize paths.
- **AOT-first**: no reflection, no Microsoft.* dependencies, `IsAotCompatible` stays clean.
- **Gen TFM unchanged**: `.Gen` projects and `PicoSerDe.Gen.Common` stay netstandard2.0.
- **Perf parity gate**: PicoBench must not regress the measured hot paths after W1/W2 (parity, not optimization).
- **Existing tests stay green** per workstream; each workstream adds its own tests. The only deletions are tests that cover APIs removed per §7 (deleted together with the API).
- **One-way dependencies**: formats → `PicoSerDe.Core` / `PicoSerDe.Gen.Common` only; no format-to-format references; no shared-layer format names.

## 5. Workstreams

Each workstream ends in a green build + green full test suite and gets its own spec + implementation plan.

### W1 — Streaming contract unification + resume correctness

**Goal**: one streaming contract, one loop, one registry in Core; all 5 formats resume correctly at arbitrary chunk boundaries.

**Direction**:
- `PicoSerDe.Core.StreamingFunc<TReader,T>` (with `partial`) becomes the single delegate contract; the 4 local `StreamingFunc<T>` delegates and `StreamingCache<T>` copies are deleted; a Core `StreamingRegistry<TFormat,T>` replaces all 5 registration caches; `StreamingRunner` hosts the only pipe loop.
- **First task (gate): chunk-boundary regression matrix for all 5 formats** — reuse the `ChunkedStream` pattern from `PicoJetson` tests, run every chunk size for multi-property payloads, and confirm or refute the suspected INI/TOML/YAML mid-object loss (§3 fact 2). If confirmed, the 4 generators' streaming emit gains partial-resume semantics (`result = partial ?? new T()` plus any needed reader rewind) — the same shape JSON already uses.
- MsgPack migrates from accumulate-and-reparse to the state-export pattern (`MsgPackReader.ExportState()` exists; the pipe-tail rebase semantics are already proven by the other 4 readers). **Fallback** (only if a gate test disproves the state path): MsgPack keeps its accumulator behind a documented exception in this spec — no half-migrated readers.
- Boundaries: no behavior change to JSON's public semantics; strict-deserialization and options behavior must stay identical; generated-code contract changes are internal.

**Deliverables**: workstream spec; chunk matrix tests ×5 formats (class, struct-excluded per existing rules, polymorphic); benchmark gate; README streaming section updated to the unified contract.

### W2 — Generator infrastructure extraction (`PicoSerDe.Gen.Common`)

**Goal**: shared generator code is one referenced netstandard2.0 component; pipeline skeleton exists once; shared code has no format names and no global mutable state.

**Direction**:
1. Create top-level `PicoSerDe.Gen.Common/` project (netstandard2.0, `IsPackable=false`, `Microsoft.CodeAnalysis.CSharp` 5.9.0 `PrivateAssets=all`), namespace `PicoSerDe.Gen` unchanged; add to the solution.
2. Move `shared/Gen/{GenInfrastructure,TypeKindResolver,AnonTypeHandler,AnonFormatConfig,IsExternalInit}` into it; delete `shared/Gen`; replace each `.Gen` csproj's 5 `Compile Include` lines with a `ProjectReference`.
3. **De-static `AssemblyPrefix`**: it becomes data, not state — both the anon pipeline and the main generation pipeline `Combine(asmName)`, and `GenInfrastructure.InnerClassName(..., prefix)` / `AnonTypeHandler.GenerateInterceptorClass(..., prefix)` take it explicitly. The static property is deleted. (An instance field is explicitly rejected: output callbacks would still depend on execution order.)
4. `FormatGeneratorBase` (public abstract, template method): implements `Initialize` (the 6 pipelines + diagnostic wiring) once; subclasses provide `Config`, `Attrs`, `AnonCfg`, the two diagnostics, `IsCandidate`/`Transform`, `GenerateAll`, and the anon emit callback. `Yaml`'s cosmetic differences (local names, `.HasValue`) are absorbed.
5. **Format capability data**: replace format-name switches with `FormatCapabilities` carried by `FormatConfig` (`ExtendedScalars`, `ByteArrayKind`, `WriterTypeName`, `OptionsTypeName`, `SequenceStartMethod`, `SequenceEndMethod`); `TypeKindResolver` takes capabilities instead of the `format` string; `FormatTag` remains only for diagnostics/hint naming.
6. **Packaging**: `PicoSerDe.Gen.Common.dll` is embedded in **all 10 packages that ship the analyzer** — 5 runtime packages + 5 `.Gen` packages (verified both families embed it, §3 fact 8) — and the 5 `Ensure*GenDllExists` targets check both DLLs. `build/*.targets` need no change.
7. **Tests/refs**: add `InternalsVisibleTo("PicoJetson.Integration.Tests")` to Gen.Common (the generator project's IVT does not cover the moved internals); give test projects that use `GenInfrastructure` a direct Gen.Common reference; rewrite `InnerHelperNameTests` to pass the prefix argument and drop `[NotInParallel("GenInfrastructure.AssemblyPrefix")]`.

**Deliverables**: workstream spec; in-repo build green with the ProjectReference; packed-consumer smoke tests (single format; two formats referenced together; AOT publish smoke); full generator test suites; naming-helper tests.

### W3 — Dead options surface removal

**Goal**: no public API that cannot be used; docs tell the truth about which formats have options.

**Direction**:
- Delete `IniOptions`/`TomlOptions`/`YamlOptions` and `IniIgnoreCondition`/`TomlIgnoreCondition`/`YamlIgnoreCondition` (§3 fact 5: inert, unreachable, no behavioral value — nulls are omitted unconditionally for these formats and `Indented` was never implemented).
- Delete `IniKeyAttribute.Name` (the repo's only `[Obsolete]`, §3 fact 6).
- Update `AnonTypeHandler` options mapping (already de-formatted in W2) to `null` for these formats; update README: the options section states JSON = full options, MsgPack = `DefaultIgnoreCondition`, INI/TOML/YAML = no options (use `[PicoIgnore]` for per-property control).
- Rejected alternatives: **wire them up** (adds a facade overload plus reader/writer/generator plumbing, and `Indented` would require new writer features — feature work outside consolidation scope); **keep as reserved** (violates the no-obsolete-shims policy).
- Future rule: if a second text format ever implements `Indented`, define the capability in `SerOptions` first (§2).

**Deliverables**: workstream spec; deletion + README truth; migration notes; full suite green (tests that asserted the options' defaults are removed).

### W4 — Build boilerplate centralization

**Goal**: format csproj files declare only identity; pack/analyzer/Ensure plumbing exists once.

**Direction**:
- Single shared implementation (props + targets, parameterized by project name) for: the analyzer DLL + `PicoSerDe.Gen.Common.dll` embed into both package families, the per-package `build/$(PackageId).targets` pack, and the `Ensure*GenDllExists` checks. The 5 `build/*.targets` files (byte-identical, §3 fact 8) collapse to one source file packed 5×.
- Runtime and `.Gen` csproj keep only PackageId/Description/tags; the 90%/89% boilerplate is deleted.
- Gates: `dotnet pack` all 10 packages; the negative Ensure test (rename a DLL → pack warns); consumer smoke build.

**Deliverables**: workstream spec; slim csproj; pack matrix green.

### W5 — Cross-format test consolidation

**Goal**: contract-level behavior is tested once across 5 formats; per-format suites keep wire-format specifics.

**Direction**:
- Move the format-agnostic suites (`UnsignedIntegerTests`, `PolymorphicTests`, `AttributeDiscoveryTests`, ignore-condition matrix, token-reader conformance) into `tests/PicoSerDe.Integration.Tests` as parameterized suites over a `Func<...>` format adapter using the shared `CrossValidationModels`.
- Keep per-format tests for wire-format behavior (INI sections, TOML array-of-tables, YAML escapes/anchors, MsgPack encodings, JSON DOM/JSONL).
- PicoJetson keeps its Unit/Integration/Functional split (flagship scope) — no project merging.
- Depends on W1/W3 landing so the moved tests target the final API.

**Deliverables**: workstream spec; parameterized suites; per-format duplication removed; full suite green.

### W6 — Hygiene (extraction leftovers)

**Goal**: remove the remaining source-linking and dead helpers; classify micro-duplication explicitly.

**Direction**:
- `shared/TextHelpers.cs` moves into `PicoSerDe.Core/src/` (it already compiles only into Core); Core csproj loses the `Compile Include`.
- `shared/BenchModels.cs` + `shared/CrossValidationModels.cs` move into a shared test-models component (default: a new `tests/PicoSerDe.TestModels` project added to the solution; exact layout in the W6 spec) referenced by the 5 benchmarks and 6 test projects; `shared/` disappears.
- `ScanUntilLineEnd` earns its place: add a `ScanUntilOrLineEnd(data, position, stopByte)` variant (or equivalent) and adopt it at the 5 inline audit-P0 sites (`IniReader`, `TomlReader` ×2, `YamlReader` ×2), with tests.
- MsgPack generator modifier style aligned to `private static` (§3 fact 1).
- **Assessed and intentionally not extracted** (criterion from §2): reader/writer scalar micro-duplication (`TryGetInt32` byte-identical across Toml/Yaml; `WriteRaw`/`WriteByte` identical across writers). These are 3–5 line wrappers over `Utf8Parser`/`_buffer`; extracting them adds indirection without removing real logic. Recorded here so the decision is not re-litigated.
- `ScalarCodec` stays in Core; add an XML doc note that it is the cross-format scalar codec and that format gating is a feature-scope decision in the resolver.

**Deliverables**: workstream spec; source-linking eliminated; scan helper adopted; full suite + benchmark build green.

### Traceability (review findings → workstream)

| Review finding | Workstream |
|---|---|
| R1 generator skeleton ×5, MsgPack style | W2 (style: W6) |
| R2 3 streaming implementations + suspected resume gap | W1 |
| R3 facade duplication (streaming block) | W1 |
| R4 reader/writer copy-paste | W6 (scan helpers; micro-duplication assessed) |
| R5/C4/C5 inert options + enum sprawl | W3 |
| R6 `ScanUntilLineEnd` unused | W6 |
| R7 build boilerplate + source linking | W4 (build), W6 (shared files) |
| R8 duplicated tests | W5 |
| C1 `ScalarCodec` placement | §2 classification (no move) |
| C2 dual streaming contracts | W1 |
| C3 `AssemblyPrefix` static | W2 |

## 6. Sequencing & Dependencies

```
W4 (build prep)        ── independent, may land first
W6 (source links, scan helper) ── independent
        │
W2 (Gen.Common + base + de-static + capability data)
        │
W1 (streaming contract + resume fix)   ← escalation: if the W1 repro confirms
        │                                 active consumer impact, W1 moves
W3 (dead options removal)                 ahead of W2
        │
W5 (test consolidation)
```

- W4 and W6 land first: no API impact, shrink the surface the later workstreams touch.
- W2 before W3 because both touch the options-name mapping (W2 turns it into capability data; W3 nulls the dead entries).
- W2 before W1 to avoid rebasing generator emit against a moving class structure.
- W5 last: it targets the final API shapes.
- Each workstream is independently shippable and independently green.

## 7. Compatibility & Breaking Changes

Baseline: consumer **source** compatibility is the contract; SG-generated binaries may change. Explicit breaks (direct removals, no deprecation shims — §4):

| # | Break | Package(s) | Migration |
|---|-------|-----------|-----------|
| 1 | `IniOptions`, `IniIgnoreCondition` removed | PicoIni | No replacement (options were inert); use `[PicoIgnore]` for per-property control |
| 2 | `TomlOptions`, `TomlIgnoreCondition` removed | PicoToml | Same |
| 3 | `YamlOptions`, `YamlIgnoreCondition` removed | PicoYaml | Same |
| 4 | `IniKeyAttribute.Name` removed | PicoIni | Use `.Key` |
| 5 | `RegisterStreaming<T>` signature changes to Core `StreamingFunc<TReader,T>` (gains `partial`) | PicoIni/MsgPack/Toml/Yaml | Hand-written streaming registrations adapt; SG-generated ones are internal |
| 6 | Per-format public `StreamingFunc<T>` delegate types removed | PicoIni/MsgPack/Toml/Yaml | Use the Core delegate type |
| 7 | `GenInfrastructure.AssemblyPrefix` removed; `InnerClassName`/anon generation take the prefix argument | internal (Gen assemblies) | Test projects updated in W2; not consumer-facing |
| 8 | Package layout: `PicoSerDe.Gen.Common.dll` added to `analyzers/dotnet/cs/` of the 10 packages | all 10 | No source change |

README updates required by this spec: options truth (W3), unified streaming contract + chunk-boundary guarantees (W1), architecture diagram showing a shared-abstractions layer (W2), FAQ streaming entries for the non-JSON formats (W1).

## 8. Testing & Verification Strategy

- **W1**: chunk-boundary matrix (all chunk sizes; multi-property payloads; every format; poly + nested), confirming/refuting the §3 fact 2 suspicion first; streaming options tests per format; strict-deserialization parity; benchmark gate.
- **W2**: full build with ProjectReference; generator test suites; rewritten naming-helper tests; **packed-consumer smoke**: `dotnet pack` all 10, install single-format and two-format consumers, build with generator diagnostics visible, plus one AOT publish smoke.
- **W3**: full suite green after deletions; grep check that the removed names have no remaining references outside changelog/migration notes.
- **W4**: pack matrix; negative Ensure test (missing DLL must warn, not silently produce a broken package); consumer smoke.
- **W5**: parameterized suites green over 5 formats; removed duplicates accounted for (no orphaned models).
- **W6**: scan-helper tests; full suite + benchmark builds green; `shared/` gone and no `Compile Include` crossing project boundaries remains.

## 9. Risks & Open Questions

| Risk | Mitigation |
|---|---|
| Suspected INI/TOML/YAML mid-object streaming loss is real and affects consumers | W1 task 1 reproduces it first; escalation rule in §6 moves W1 ahead of W2 if confirmed with active impact |
| MsgPack state-export migration fails a chunk-boundary test (position rebase semantics) | Gate task; documented fallback keeps the accumulator as the single exception (no half-migrated readers) |
| Roslyn analyzer dependency loading in packed consumers (Gen.Common.dll next to the format DLL) | Explicit packed-consumer smoke gate before W2 is considered done; standard analyzers-directory dependency layout |
| Duplicate `PicoSerDe.Gen.Common.dll` copies across packages when multiple formats are referenced | Accepted trade-off of analyzer packaging (per-package load contexts); covered by the two-format smoke test |
| Capability data changes incremental-generator caching | Capabilities are records with value equality and no delegates; prefix travels at output combine time, not inside cached transforms |
| `InnerHelperNameTests` relied on the static; rewriting may weaken coverage | Tests pass the prefix explicitly per case (stronger isolation); the prior `[NotInParallel]` coupling is removed |
| Extraction criterion judgment call (`ScalarCodec`, micro-duplication) | Recorded in §2/§W6 for review; user can veto before workstream specs |
| The old local master design's "Implemented" claims are misleading | New spec carries its own HEAD baseline (§3 fact 12); no reliance on the old doc |

**Open questions for the per-workstream specs**: exact `FormatCapabilities` shape; whether `StreamingRegistry<TFormat,T>` lives beside `SerRegistry` or inside the facade; the exact layout of the shared test-models component (standalone `tests/PicoSerDe.TestModels` project vs. a folder in the integration test project).

## 10. Out of Scope

- Merging format-specific emit logic (measured similarity is 5–57%; merging would add coupling, not remove it).
- Per-format attribute subclass names/shapes (Phase 2 consolidation is complete and kept).
- TokenType, new formats, new type support; enabling extended scalars in non-JSON formats (gating becomes capability data in W2, enabling remains a feature decision).
- Performance optimization beyond parity.
- The pre-existing dual analyzer embedding (runtime **and** `.Gen` packages both ship the analyzer DLL) — unchanged behavior, not touched here.
- `InterceptorsNamespaces` mechanism redesign.
- `SerOptions` capability extraction for `Indented`/`DefaultIgnoreCondition` (rule of two; future work if a second format implements them).

## 11. Phased Execution

Each workstream gets its own spec (writing-plans flow) after this master spec is approved, in the sequence of §6. Per-workstream specs must restate: the abstractions being extracted and their destination, the public surface delta, the test gates, and the README/doc updates. No workstream may introduce shared-layer format names, global mutable state, or source linking (§2).
