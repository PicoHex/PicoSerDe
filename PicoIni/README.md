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

## Custom Serializers (`RegisterCustom`)

`Register` overrides the top level only. `RegisterCustom<T>` additionally overrides T
**wherever it appears as a nested value** — the same pair and the same signature
`PicoJetson.JsonSerializer` has:

```csharp
struct MoneySer : ISerializer<Money>
{
    public void Serialize(IBufferWriter<byte> writer, Money value) =>
        writer.Write(Encoding.UTF8.GetBytes($"{value.Amount.ToString(CultureInfo.InvariantCulture)} {value.Currency}"));
}
struct MoneyDes : IDeserializer<Money>
{
    public Money Deserialize(ReadOnlySpan<byte> data) => Money.Parse(Encoding.UTF8.GetString(data));
}

IniSerializer.RegisterCustom<Money>(new MoneySer(), new MoneyDes());
IniSerializer.HasCustomSerializer<Money>();   // true

IniSerializer.Serialize(new Account { Balance = new Money(12.5m, "EUR") });
// Balance = 12.5 EUR      <- the registration's bytes, not the `[Balance]` section
```

The positions it covers are the two INI writes a value in:

| Position | Built-in form | With `RegisterCustom<T>` |
|---|---|---|
| Member (`Money M`) | its own section, `[M]` + its keys | one `M = …` line |
| Top-level list element (`List<Money>`) | numbered sections, `[0]`, `[1]`, … | the comma-joined line INI already writes for `List<int>` |

A `List<T>` / `Dictionary<string,T>` **member** is not in that table and cannot be: INI's
decision table drops those shapes before any emitter sees them (`PICOSERDE004` — a section
carries members, not values), so there is no built-in form for a registration to replace. Use
a top-level list, or a format that can hold the shape.

**What your serializer writes is up to you — the format only provides the writer.** INI
carries a value as text, so the bytes become that value's text and `IniWriter` lays it down
as-is. Nothing in the format reinterprets them; a document is only as readable as the text you
produce (an unescaped separator inside your text is a separator).

Registration is a runtime fact, so it is tested where the value is written: with nothing
registered the output is byte-for-byte what it was before. Deserialization override applies
at the **top level only** — nested reads still use the generated deserializer, exactly as in
JSON and MessagePack.

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

- **No object-list *members* — `⛔`, a format fact.** A `List<SomeDto>` property is dropped with a
  `PICOSERDE004` warning
  (`SilentDropDiagnosticTests.Ini_ObjectListMember_ReportsDiagnostic` for the drop,
  `….ReportsTheFactVerdict_AndNoReopenPointer` for the verdict) — visible in the
  build log, never silent. A section is a **flat, member-named** slot: a nested object is written as
  `iw.WriteSection("<member>")`, so the section name *is* the member's name and there is no second
  dimension left to carry an item index. The one object-list form **this generator writes** is the
  numbered section at the **top** of the document (`[0]`, `[1]`, …), where no member name is being
  displaced. A member-level list would need a composite name (`[Rows.0]`) that no INI reader turns
  back into a list — a new wire convention, not generator plumbing — which is why spec §3 draws the
  boundary here ("对象列表只在顶层 …；成员内不承载任何嵌套") and the matrix cell is ❌ (root README
  footnote ¹). If you need an object list inside a DTO, use **TOML**, **YAML** or **JSON**.
- **A top-level `List<SomeDto>` does round-trip** — each item is written as a numbered section
  (`[0]`, `[1]`, ...): a plain section header plus key/value lines, with no extension to the INI
  grammar. That is the same header shape the bullet above is about, and the difference is of KIND, not
  of progress: at the top of the document the section-name slot is free, so an index can be spent on
  it; for a member that slot already carries the member's name.
- **Nested lists, nullable elements and recursion are reported.** `List<List<int>>`, `List<int?>`
  and a recursive member (`Node Child` on `Node`) are dropped with `PICOSERDE004` /
  `PICOSERDE003` — visible in the build log, not silent.
- Objects nested deeper than one level throw `NotSupportedException` instead of silently losing data.
- **An `object?` member round-trips as its text — never silently.** INI carries no value *types* on the
  wire (a value is the text between `=` and the end of the line), so `object? Result = 42L` comes back
  as the string `"42"` and `= "boom"` comes back as `"boom"`; the value is kept, only its type is not
  (`AnyMemberCrossFormatTests.Ini_any_member_*`). Nothing is dropped, so no diagnostic fires.
- List values parse strictly and culture-invariantly: malformed elements throw `FormatException`; `List<int>`, `List<bool>`, `List<DateOnly>`, `List<TimeSpan>`, etc. round-trip.
  The comma-joined text (`V = 1,2,3`) is **this generator's value-level encoding**, not an INI convention:
  another INI reader sees one opaque string, which is exactly what the format carries. It may only ever be one
  layer and must stay reversible (`"a,b"` is written `a\,b` and read back) — a nested shape packed into a
  value would make the reader parse a grammar we invented (`AGENTS.md`, the value-level exception).
**Duplicate keys and duplicate sections are accepted, and that is a choice rather than a fact.**
INI has no specification, so the arbiter is the de-facto standard - and it splits here, measured
2026-11: `configparser` with its default `strict=True` raises `DuplicateOptionError` /
`DuplicateSectionError`, while `git config` keeps both values (`--get-all` returns them in order,
`--get` returns the last) and `configparser(strict=False)` merges a repeated section. Because only
some implementations refuse, refusal is not part of the format, and because the majority default does
refuse, acceptance is not something the format settles either. This reader takes the permissive
reading: a repeated key leaves the last value, a repeated section merges. If you need the strict
reading, TOML refuses duplicate keys by specification (`PICOSERDE`-free, it is a plain
`FormatException`) - and a strictness option here would be a new public API rather than a fix.

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
does both. Run `dotnet run -c Release --project PicoIni/benchmarks/PicoIni.Benchmarks -f net10.0` for current local timings.

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
