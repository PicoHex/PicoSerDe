# PicoSerDe — tests, cross-validation and CI

How this repository is tested, how each module is cross-validated against a competitor
implementation, how the test projects are split, and how a commit becomes a NuGet package.
These are maintainer-process notes rather than front-page material: the three sections below are
reproduced from [`README.md`](../README.md) unchanged, in their original order — the only edit is
one cross-reference that used to point inside the README and now points back at it.

Contents: [Test Coverage](#test-coverage) ·
[Three-Layer Test Structure](#three-layer-test-structure) · [CI/CD](#cicd)

---

## Test Coverage

Every module has its own suite, cross-validated against a competitor implementation:

| Module | Competitor | Cross-Validation |
|--------|:-----------|:----------------:|
| PicoJetson | System.Text.Json | ✅ bidirectional, all 19 property types |
| PicoToml | Tomlyn | ✅ bidirectional, 20 property types, NestedList via `[[key]]` |
| PicoYaml | YamlDotNet | ✅ bidirectional, 19 property types, DateOnly/TimeOnly converters |
| PicoIni | Microsoft.Extensions.Configuration.Ini | ✅ bidirectional, 16 property types |
| PicoMsgPack | MessagePack-CSharp | ✅ map/array dual-format, 14 property types |
| PicoSerDe.Core | — | — |
| Integration (cross-format) | — | Ignore-condition matrix, anon types, round-trips |

> Test counts are deliberately not pinned in this document: they change with every
> PR and a stale number is worse than none. Run `dotnet test PicoSerDe.slnx -c Debug`
> for the current totals. PicoJetson's suite is split across three projects
> (unit / integration / functional).
>
> Strictness/robustness regression tests live in dedicated per-format suites
> (`Strict*Tests`, `*StrictnessTests`, `*RobustnessTests`): wrong-typed input,
> trailing data, missing `required` members, malformed documents, chunked
> streaming and comment handling all fail loudly instead of silently producing
> defaults.

---

### Three-Layer Test Structure

PicoJetson tests are split into Unit / Integration / Functional projects with clear boundaries.

> **No non-generic `Serialize(Type, object?)` overloads.** PicoSerDe is designed for AOT-first
> usage where all types are known at compile time. `SerRegistry<TFormat, T>` static fields (PicoSerDe.Core) are shared
> across assemblies and provide faster lookup than a `ConcurrentDictionary<Type, ...>`.
> Framework wrappers should call the generic API internally — the type's serializer is
> guaranteed to be registered via `ModuleInitializer` as long as the type was discovered
> by any pipeline (usage-driven, attribute, or shorthand).

```
┌──────────────────────────────────────────────┐
│                 User Code                     │
└──────────────────┬───────────────────────────┘
                   │  Static SerRegistry<TFormat, T>
┌──────────────────▼───────────────────────────┐
│           PicoSerDe.Core                      │
│  ISerializer<T>  │  IDeserializer<T>         │
│  SerRegistry     │  DesRegistry              │
│  TokenType       │  SimdHelpers (Vector128)  │
│  TextHelpers     │  SerializerExtensions     │
└────┬────────┬─────────┬─────────┬─────────┬──┘
     │        │         │         │         │
 PicoJetson  PicoIni  PicoMsgPack PicoToml PicoYaml
   ││       ││         ││        ││       ││
  .Gen     .Gen       .Gen      .Gen     .Gen      ← embedded in each runtime nupkg
```

- **One-package install**: each format → runtime library (net10.0) with its source generator (netstandard2.0) **embedded** in the nupkg (`analyzers/dotnet/cs` + `build/PicoX.targets`) — a single `PackageReference` is enough; standalone `.Gen` packages are legacy/optional
- **`ref struct`** readers/writers — stack-allocated, zero heap allocation on hot path
- **Static `SerRegistry<TFormat, T>`** — per-format registries in PicoSerDe.Core; JIT/AOT inlineable, no dictionary lookups
- **`file struct`** generated implementations — devirtualization without sealed class overhead
- **Ref struct serialization** — `ref struct` types are supported as serializable types across all 5 formats. Source-generator-generated static methods + delegate dispatch bypass the `ISerializer<T>` interface constraint.
- **`JsonOptions`** — runtime configuration (indentation, naming policy, ignore conditions, etc.) passed **explicitly per call** (no ambient ThreadStatic state; options thread through reader/writer instances and SG-generated code)
- **Polymorphic deserialization** — type discriminator dispatch via `[PicoDerivedType]`; serialization + deserialization + streaming (v2026.3.0); record types (v2026.3.23); TOML/YAML poly (v2026.3.24); INI/MsgPack poly (v2026.4.0)
- **Anonymous type serialization** — `Serialize(new { A = 1, B = "x" })` with nested types, collections, `PropertyNamingPolicy.CamelCase`, `DefaultIgnoreCondition.WhenWritingNull`, and `MaxDepth` enforcement. Works across all 5 formats via C# 12 interceptors + unsafe field access (serialization only, C# 12+, `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`) (v2026.4.1)
- **`PicoDocument` / `PicoElement`** — zero-copy JSON DOM for schema-less inspection (v2026.3.4)
- **C# records** — primary constructor auto-detection, `init`-only support (v2026.3.3); poly+record (v2026.3.23); complex/collection ctor params (v2026.3.24)
- **Top-level arrays** — `Serialize<T[]>()` / `Deserialize<T[]>()` with streaming (v2026.3.2)
- **Top-level scalars / `Nullable<T>` are not supported** — `Serialize<Guid>(...)`, `Deserialize<int?>(...)` etc. fail loudly with `InvalidOperationException` ("no serializer registered") instead of emitting an empty object or non-compiling generated code
- **Deep object nesting** — JSON/YAML/MsgPack round-trip objects nested 3+ levels; INI/TOML currently support a single nested level and throw `NotSupportedException` beyond that (loudly, instead of silently losing data).
- **INI nested object lists** — a `List<SomeDto>` property cannot be represented by INI's flat sections and is dropped by `IniSerializer` (reported as `PICOSERDE004` since this release, with the member name and format; see [Format limits](../README.md#format-limits-what-each-wire-format-cannot-express)); scalar lists (`List<int>`, `List<string>`, ...) round-trip.

---

## CI/CD

| Target | Runner |
|--------|--------|
| win-x64 | windows-latest |
| win-arm64 | windows-latest |
| linux-x64 | ubuntu-latest |
| linux-arm64 | ubuntu-24.04-arm |
| osx-arm64 | macos-latest |

Every push: build + the full test suite + 5 benchmarks smoke + 5 AOT sample publishes.
Release: `v*` tag → packs 11 packages in dependency order → NuGet.org.
Local feed: run `./scripts/release.ps1 -Version <ver>` **before** pushing the
tag — it runs the test suite, packs all 11 packages into `artifacts/nupkg`
(declared as the `local` NuGet source in `NuGet.config`), then tags and pushes.
Sibling PicoHex repos add the same folder path after nuget.org in their
`NuGet.config` to consume the new version instantly, bypassing nuget.org's
indexing window and 30-minute HTTP cache.

AOT tiers - declare `<AotOptimizationLevel>` per project (`minimal` default /
`aggressive` for samples/benchmarks). Implementations: `minimal` in
`Directory.Build.props` (PublishAot + TrimMode=full), `aggressive` in
`Directory.Build.targets` (adds `StackTraceSupport=false` +
`UseSystemResourceKeys=true`; classic `Ilc*` MSBuild properties are removed in
.NET 10 SDK >= 10.0.400 and silently ignored) - the aggressive block lives in
targets so a csproj-level declaration is visible when it evaluates.
Libraries declare `IsAotCompatible`+`IsTrimmable`; only source generators
(`PicoXxx.Gen`, netstandard2.0) never AOT.
