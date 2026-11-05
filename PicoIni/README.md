# PicoIni

AOT-first, reflection-free INI serializer.

[![NuGet](https://img.shields.io/nuget/v/PicoIni)](https://www.nuget.org/packages/PicoIni)

## Install

```bash
dotnet add package PicoIni
# The source generator is embedded in PicoIni (analyzers/dotnet/cs) — no separate .Gen reference needed
```

## Quick Start

```csharp
using PicoIni;

public class AppConfig
{
    public string Title { get; set; } = string.Empty;
    public ServerConfig Server { get; set; } = new();
}

public class ServerConfig
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
}

var config = new AppConfig { Title = "MyApp", Server = new() { Host = "localhost", Port = 8080 } };
var ini = IniSerializer.Serialize(config);
// Title = MyApp
//
// [Server]
// Host = localhost
// Port = 8080
```

## Features

- **INI format** — sections, key-value pairs, comments (`;` / `#`)
- **`ref struct`** reader/writer — stack-allocated on the hot path
- **AOT-compatible** — `IsAotCompatible=true`, zero reflection
- **Ref struct serialization** — serialize `ref struct` types directly
- **Quoted values** — with escape sequence support (`\"`, `\n`, `\t`, `\r`, `\\`)
- **Case-insensitive** property name matching
- **Zero-allocation formatting** — stackalloc-based UTF-8 encoding for DateTime, Guid, TimeSpan
- **Dual-mode** reader: `ReadOnlySpan<byte>` + `ReadOnlySequence<byte>`

## Customization

```csharp
[PicoSerializable]                     // all formats
[PicoIniSerializable]                  // INI only
[IniKey("custom_name")]               // override key name
[IniSection("MySection")]              // override section name
[IniIgnore]                            // exclude property
[IniComment("doc comment")]            // emit comment
[IniCamelCase]                         // camelCase keys
[IniConverter(typeof(MyConverter))]    // custom converter
[IniDateTimeFormat("yyyy-MM-dd")]     // custom DateTime format
```

## Options

`IniSerializer.Serialize` / `Deserialize` accept `IniOptions`:

- `Indented` — indents section contents by two spaces (default: compact)
- `DefaultIgnoreCondition` — accepted for API uniformity; INI has no null value on the write side, so null members are always omitted

## Null Handling

- **Write:** a null member is omitted — INI has no null value. Omission means a
  null does **not** round-trip: the property keeps its initializer (or stays
  `null` when it has none) on read.
- **Read:** `Key =` with nothing after it, and the unquoted word `null` (any
  case), are null literals and assign `null` to nullable members. An unquoted
  empty value used to produce `""`; it is now `null`.
- A quoted `""` is the empty string and a quoted `"null"` is the four-character
  string — quoted scalars are never null.
- A null literal aimed at a **non-nullable value type** throws
  `FormatException`; an unannotated `string` member receives `null` (the
  generators' NRT-lenient contract).

## Limits

- **No object-list *members* — `⏸`, a boundary of *our* convention, not a format fact.** A
  `List<SomeDto>` property is dropped with a `PICOSERDE004` warning
  (`SilentDropDiagnosticTests.Ini_ObjectListMember_ReportsDiagnostic` pins the drop) — visible in
  the build log, never silent. **INI has no independent standard implementation to appeal to**
  (`configparser` accepts any input as a string), so the boundary of this dialect is **what this
  specification says it is**, not what the format cannot do — and what it says here is that the
  member-level form is **not implemented**: our debt, reopenable. A section is a **flat,
  member-named** slot: a nested object is written as `iw.WriteSection("<member>")`, so the section
  name *is* the member's name. The one object-list form this dialect has is the numbered section at
  the **top** of the document (`[0]`, `[1]`, …), where no member name is being displaced; a
  member-level list would need a composite name (`[Rows.0]`) — **a new wire convention, an
  `x+1`-level public-convention change recorded in spec §8.1, not this release.** Reopening it is
  therefore a specification decision, not generator plumbing.
  The strongest argument for staying closed is worth writing down: `[Rows.0]` would claim a
  **two-dimensional** name inside the **section namespace users also write by hand**, and the
  index in it is one **we would not read as an index** — a reader sees a section called `Rows.0`,
  never element 0 — so the convention would promise an addressing scheme we do not honour. Against
  that: the comma-joined scalar list (`V = 1,2,3` — a ✅ cell in the matrix), the `\,` escape
  inside it and the top-level numbered section (`[0]`, which round-trips) are no less our own
  inventions — which is exactly why "member-level is `⛔`" read as selective application, and why
  the verdict here is `⏸`.
  Until that convention exists the build log still shows the terminal wording
  (`….ReportsTheFactVerdict_AndNoReopenPointer` pins today's text) — flipping the wording is part
  of the `x+1` change, not this one. If you need an object list inside a DTO today, use **TOML**,
  **YAML** or **JSON**.
- **A top-level `List<SomeDto>` does round-trip** — each item is written as a numbered section
  (`[0]`, `[1]`, ...): a plain section header plus key/value lines, with no extension to the INI
  grammar. That is the same header shape the bullet above is about, and the difference is in **which
  name the section slot spends**, not in what the format allows: at the top of the document the slot
  is free, so an index can be spent on it; for a member that slot already carries the member's name —
  which is why the member-level form needs the `[Rows.0]` convention above, and is a `⏸` debt
  rather than a format fact.
- **Nested lists, nullable elements and recursion are reported.** `List<List<int>>`, `List<int?>`
  and a recursive member (`Node Child` on `Node`) are dropped with `PICOSERDE004` /
  `PICOSERDE003` — visible in the build log, not silent.
- Objects nested deeper than one level throw `NotSupportedException` instead of silently losing data.
- List values parse strictly and culture-invariantly: malformed elements throw `FormatException`; `List<int>`, `List<bool>`, `List<DateOnly>`, `List<TimeSpan>`, etc. round-trip.
- Enum values parse strictly too: an unparseable enum text throws `FormatException` naming the member and offset. It previously fell back silently to the default (`0`) enum member.

## Performance

**From the 2026-10-29 window (see the [archive](../docs/perf/measurement.md#per-scenario-tables-2026-10-29-window)).**
**No ratio is published for this format; the absolute cost is.** The reason is not modesty but that **no equivalent
operation exists to divide by**: ini-parser 2.5.3 exposes **0 public generic methods and 0 parameters of type
`System.Type`**, and its only entry point is `Parse(string) → IniData` — a DOM. Any POCO mapping on its side is
therefore the caller's own code, by definition, so it is never "one library call" against ours, in any execution
mode. The archive carries PicoIni's own per-scenario cost — nine scenarios, µs/op as the median of three rounds
(`r23`/`r24`/`r25`) — under a table labelled `NON-EQUIVALENT`, its competitor column a caller-written DOM walk
printed beside ours and never divided: no multiple is computed on this page, and none is offered for division.

Absolute cost is also the only claim a window change leaves intact, so it is stated with its window: on the paths
no commit touched, our arms read 0 %–13 % faster and the DOM walk 6 %–18 % faster than in the window this one
replaces — the same offset on both sides of a ratio this table does not have, which is why no µs here is compared
across windows.

The ratio that used to be here was therefore not a measurement of the two libraries, and both actions also went
through `Benchmark.Compare` in the wrong slots; it has been withdrawn rather than corrected in place.

Capability fact that does not depend on measurement: **ini-parser's DOM parsing works under NativeAOT** — it has
no reflection-free *typed* mapping, so it can parse a document ahead of time but cannot produce a POCO. PicoIni
does both. Run `dotnet run -c Release --project PicoIni/benchmarks/PicoIni.Benchmarks` for current local timings.

## Low-Level API

```csharp
var reader = new IniReader(data);
while (reader.Read())
{
    if (reader.TokenType == TokenType.PropertyName)
        var key = Encoding.UTF8.GetString(reader.GetStringRaw());
    // value follows on next Read() call
}
```

## Packages

| Package | Description |
|---------|-------------|
| `PicoIni` | Runtime library |
| `PicoIni.Gen` | Roslyn source generator — embedded in the runtime package (standalone package is legacy/optional) |

## License

MIT
