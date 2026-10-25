# Phase 4 — Build Configuration Centralization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove per-project AOT repetition (20 projects repeat `PublishAot=true` + `TrimMode=full`) by adopting the org-level `AotOptimizationLevel` pattern, add the netstandard2.0 guard for the 5 Gen projects, and clean stale `PicoSerDe.Abs.*` artifacts.

**Architecture:** `Directory.Build.props` gains the AOT strategy blocks copied from the PicoHex org repo (`D:/MyProjects/PicoHex/PicoHex/Directory.Build.props`): `AotOptimizationLevel` (default `minimal` → `PublishAot=true`, `TrimMode=full`; `aggressive` → +10 Ilc optimizations) and the netstandard2.0 guard block placed AFTER the level blocks (Gen must never AOT). `Directory.Build.targets` (currently empty) gains the `PublishAotIfRequested` target. Tests inherit `minimal` by declaring nothing; samples/benchmarks declare `aggressive`; libraries get `IsAotCompatible=true` automatically. The repo's existing props (InterceptorsNamespaces, Deterministic, SourceLink) are preserved; the org's package-metadata/docs blocks are NOT adopted (out of scope — keeps the warning baseline stable).

**Tech Stack:** MSBuild, .NET 10 SDK (global.json 10.0.201).

**Spec:** `docs/superpowers/specs/2026-08-22-design-simplification-master-design.md` (§7, §9)

## Global Constraints

- Gen stays netstandard2.0: keep `TreatAsLocalProperty="PublishAot"` + `<PublishAot>false</PublishAot>` in each Gen csproj (belt-and-suspenders per csproj-format skill), plus the guard block.
- Do not adopt the org's package-metadata, XML-doc, or `AnalysisLevel` blocks — only the AOT strategy blocks (§7 scope).
- Warning baseline must not regress materially: `dotnet build PicoSerDe.slnx -c Release` stays at 35 warnings / 0 errors (verified 2026-08-22).
- `PicoSerDe.Core.csproj` drops its explicit `IsAotCompatible`/`IsTrimmable` (now inherited for libraries).

---

### Task 1: Directory.Build.props AOT levels + guard; Directory.Build.targets target

**Files:**
- Modify: `Directory.Build.props`
- Modify: `Directory.Build.targets` (currently empty — replace with org's target)

- [ ] **Step 1: Write failing "test" (build-config verification script)**

Create `scripts/verify-aot-levels.ps1` (or `.sh`):

```powershell
# Fails when a project does not inherit the expected AOT level.
$failures = @()
Get-ChildItem -Recurse -Filter *.csproj | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object {
    $content = Get-Content $_.FullName -Raw
    if ($content -match '<TargetFramework>netstandard2.0</TargetFramework>') {
        if ($content -notmatch 'TreatAsLocalProperty="PublishAot"' -or $content -notmatch '<PublishAot>false</PublishAot>') {
            $failures += "Gen guard missing: $($_.Name)"
        }
    } elseif ($content -match '<OutputType>Exe</OutputType>') {
        if ($_.Name -match 'Sample' -and $content -notmatch '<AotOptimizationLevel>aggressive</AotOptimizationLevel>') {
            $failures += "Sample missing aggressive: $($_.Name)"
        }
        if ($_.Name -match 'Benchmarks' -and $content -notmatch '<AotOptimizationLevel>aggressive</AotOptimizationLevel>') {
            $failures += "Benchmark missing aggressive: $($_.Name)"
        }
        if ($content -match '<PublishAot>' -or $content -match '<TrimMode>') {
            $failures += "Per-project AOT props still present: $($_.Name)"
        }
    }
}
if ($failures.Count -gt 0) { $failures | ForEach-Object { Write-Error $_ }; exit 1 }
Write-Host "AOT level inheritance OK"
```

Run it: `powershell -File scripts/verify-aot-levels.ps1`
Expected: FAIL (exit 1) — the props don't exist yet and per-project repetition is still present.

- [ ] **Step 2: Implement Directory.Build.props additions**

Append to `Directory.Build.props` (after the existing Deterministic/SourceLink blocks; keep `InterceptorsNamespaces` and existing content untouched):

```xml
  <!-- ===== AOT Strategy ===== -->

  <!--
    AotOptimizationLevel:
      minimal  — Tests and default. PublishAot + TrimMode only, fast compilation.
      aggressive — Samples and Benchmarks. Full trimming, no reflection, no debug info.
    Override per-project via <AotOptimizationLevel>aggressive</AotOptimizationLevel>.
  -->
  <PropertyGroup>
    <AotOptimizationLevel Condition="'$(AotOptimizationLevel)' == ''">minimal</AotOptimizationLevel>
  </PropertyGroup>

  <PropertyGroup Condition="'$(AotOptimizationLevel)' == 'minimal'">
    <PublishAot Condition="'$(PublishAot)' == ''">true</PublishAot>
    <TrimMode Condition="'$(TrimMode)' == ''">full</TrimMode>
  </PropertyGroup>

  <PropertyGroup Condition="'$(AotOptimizationLevel)' == 'aggressive'">
    <PublishAot Condition="'$(PublishAot)' == ''">true</PublishAot>
    <TrimMode Condition="'$(TrimMode)' == ''">full</TrimMode>
    <IlcGenerateStackTraceData>false</IlcGenerateStackTraceData>
    <IlcOptimizationPreference>Size</IlcOptimizationPreference>
    <PublishTrimmed>true</PublishTrimmed>
    <IlcTrimMetadata>true</IlcTrimMetadata>
    <IlcFoldIdenticalMethodBodies>true</IlcFoldIdenticalMethodBodies>
    <IlcDisableReflection>true</IlcDisableReflection>
    <IlcGenerateDebugInfo>false</IlcGenerateDebugInfo>
    <IlcGenerateCompleteTypeMetadata>false</IlcGenerateCompleteTypeMetadata>
    <IlcGenerateUnwindStackTraceData>false</IlcGenerateUnwindStackTraceData>
  </PropertyGroup>

  <!-- ⚠️ netstandard2.0 must NEVER AOT — this block MUST come after the AOT level blocks.
       Only source generators (PicoXxx.Gen) remain on netstandard2.0 (Roslyn loader
       compatibility). -->
  <PropertyGroup Condition="'$(TargetFrameworkIdentifier)' == '.NETStandard'">
    <PublishAot>false</PublishAot>
    <IsAotCompatible>false</IsAotCompatible>
    <IsTrimmable>true</IsTrimmable>
    <TrimMode>partial</TrimMode>
    <EnableTrimAnalyzer>true</EnableTrimAnalyzer>
  </PropertyGroup>

  <!-- Libraries: declare AOT-compatible but do not self-publish. -->
  <PropertyGroup>
    <IsAotCompatible
      Condition="'$(IsAotCompatible)' == '' and '$(OutputType)' != 'Exe' and $([MSBuild]::IsTargetFrameworkCompatible('$(TargetFramework)', 'net8.0'))"
      >true</IsAotCompatible>
  </PropertyGroup>

  <!-- PublishAotOnRun: dotnet run auto-publishes as AOT in CI only. -->
  <PropertyGroup>
    <PublishAotOnRun Condition="'$(PublishAotOnRun)' == '' and '$(CI)' == 'true'">true</PublishAotOnRun>
    <PublishAotOnRun Condition="'$(PublishAotOnRun)' == ''">false</PublishAotOnRun>
  </PropertyGroup>
```

Replace the empty `Directory.Build.targets` with the org's `PublishAotIfRequested` target (copy verbatim from `D:/MyProjects/PicoHex/PicoHex/Directory.Build.targets`).

- [ ] **Step 3: Verify the build still succeeds (expected: tests/samples/benchmarks still have their own props — nothing removed yet)**

Run: `dotnet build PicoSerDe.slnx -c Release`
Expected: succeeds (props are additive).

- [ ] **Step 4: Commit**

```bash
git add Directory.Build.props Directory.Build.targets scripts/verify-aot-levels.ps1
git commit -m "build: add AotOptimizationLevel strategy and netstandard2.0 guard to Directory.Build.props"
```

---

### Task 2: Remove per-project AOT props; declare levels; simplify Core.csproj

**Files:**
- Modify (remove `<PublishAot>true</PublishAot>` and `<TrimMode>full</TrimMode>`; add `<AotOptimizationLevel>aggressive</AotOptimizationLevel>` for samples/benchmarks): all 5 samples, all 5 benchmarks, all 9 test projects — see the full list in Step 1
- Modify: `PicoSerDe.Core/src/PicoSerDe.Core.csproj` — remove `<IsAotCompatible>true</IsAotCompatible>` and `<IsTrimmable>true</IsTrimmable>` (inherited now)
- Gen csprojs: unchanged (guard + TreatAsLocalProperty already in place — verify)

- [ ] **Step 1: Enumerate and edit the 20 projects**

The 20 projects with `PublishAot=true` (verified 2026-08-22):
- Samples (6): `PicoIni/samples/PicoIni.Sample`, `PicoJetson/samples/PicoJetson.Sample`, `PicoMsgPack/samples/PicoMsgPack.Sample`, `PicoSerDe.Core/samples/PicoSerDe.Core.Sample`, `PicoToml/samples/PicoToml.Sample`, `PicoYaml/samples/PicoYaml.Sample` → add `<AotOptimizationLevel>aggressive</AotOptimizationLevel>`, delete the 2 props.
- Benchmarks (5): `PicoIni.Benchmarks`, `PicoJetson.Benchmarks`, `PicoMsgPack.Benchmarks`, `PicoToml.Benchmarks`, `PicoYaml.Benchmarks` → same.
- Tests (9): `PicoIni.Tests`, `PicoJetson.Functional.Tests`, `PicoJetson.Integration.Tests`, `PicoJetson.Unit.Tests`, `PicoMsgPack.Tests`, `PicoSerDe.Core.Tests`, `PicoToml.Tests`, `PicoYaml.Tests`, `PicoSerDe.Integration.Tests` → delete the 2 props only (inherit `minimal`).

Example result (sample):
```xml
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <AotOptimizationLevel>aggressive</AotOptimizationLevel>
    <LangVersion>latest</LangVersion>
    ...
```
(Property order convention: `TargetFramework` → `LangVersion` → `Nullable` → `ImplicitUsings` → `AotOptimizationLevel` — place it with the other PropertyGroup entries; keep `OutputType` first for Exe projects.)

- [ ] **Step 2: Verify**

Run: `powershell -File scripts/verify-aot-levels.ps1`
Expected: PASS (exit 0).

- [ ] **Step 3: Full rebuild**

Run: `dotnet build PicoSerDe.slnx -c Release --no-incremental`
Expected: Build succeeded; warning count ≈ 35 (may shift slightly — new props add no warnings; `IlcDisableReflection` on samples/benchmarks is publish-time only).

- [ ] **Step 4: Run full test suite**

Run: `dotnet test PicoSerDe.slnx -c Release`
Expected: all tests pass.

- [ ] **Step 5: Commit**

```bash
git add PicoIni PicoJetson PicoMsgPack PicoSerDe.Core PicoToml PicoYaml tests
git commit -m "build: centralize AOT config via AotOptimizationLevel across 20 projects"
```

---

### Task 3: Stale artifact cleanup + final verification

**Files:**
- Delete: `out/PicoIni.Benchmarks/PicoSerDe.Abs.pdb`, `out/PicoJson.Benchmarks/PicoSerDe.Abs.pdb`, `out/PicoMsgPack.Benchmarks/PicoSerDe.Abs.dll`, `out/PicoMsgPack.Benchmarks/PicoSerDe.Abs.pdb`, `out/PicoToml.Benchmarks/PicoSerDe.Abs.pdb`, `out/PicoYaml.Benchmarks/PicoSerDe.Abs.dll`, `out/PicoYaml.Benchmarks/PicoSerDe.Abs.pdb` (pre-rename leftovers; check `ls out/**/PicoSerDe.Abs.*` for the full list)
- Modify: `docs/superpowers/specs/2026-08-22-design-simplification-master-design.md` — mark Phase 4 complete after this task

- [ ] **Step 1: Delete stale artifacts**

Run: `find out -name "PicoSerDe.Abs.*" -delete` then `ls out | wc -l` to confirm the `out/` tree remains otherwise intact. Do NOT delete anything else in `out/` (it holds build outputs used by local tooling).

- [ ] **Step 2: Grep-verify no Abs references remain in build files**

Run: `grep -rn "PicoSerDe.Abs" --include="*.csproj" --include="*.props" --include="*.targets" --include="*.slnx" .`
Expected: no output. (Docs under `docs/superpowers/plans/` are historical — leave them.)

- [ ] **Step 3: Final gate**

Run: `dotnet build PicoSerDe.slnx -c Release` + `dotnet test PicoSerDe.slnx -c Release` + `powershell -File scripts/verify-aot-levels.ps1`
Expected: build succeeds, all tests pass, verify script PASS.

- [ ] **Step 4: Commit**

```bash
git add out docs/superpowers/specs/2026-08-22-design-simplification-master-design.md
git commit -m "chore: remove stale PicoSerDe.Abs artifacts; mark Phase 4 complete in master spec"
```

---

### Task 4: Master-spec status update

**Files:**
- Modify: `docs/superpowers/specs/2026-08-22-design-simplification-master-design.md` — §11: mark Phase 4 done (only after all 4 tasks above are merged and green)

- [ ] **Step 1: Update spec status**

Change the Phase 4 entry in §11 to `✅ implemented` with the commit refs, and update the doc Status line if the other phases are complete. Leave the spec's phase sections intact (they document the design).

- [ ] **Step 2: Commit**

```bash
git add docs/superpowers/specs/2026-08-22-design-simplification-master-design.md
git commit -m "docs: mark Phase 4 complete in master spec"
```
