# Phase 3 — Token Reader Contract + Text-Scan Hardening + TokenType Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One `ITokenReader` contract implemented by all 5 readers, one set of line-bound-aware scan helpers in `TextHelpers` (closing the audit's P0 bounds-bug class), and removal of the unused `TokenType` values.

**Architecture:** `ITokenReader` (a plain interface; all 5 readers are `ref struct`s and can implement interfaces in C# 13) exposes the verified common surface. Generated code keeps concrete reader types (JIT quality) — the contract serves cross-format tooling and a shared conformance test suite in `tests/PicoSerDe.Integration.Tests`. `TextHelpers.ScanUntilLineEnd` gives the Yaml/Ini/Toml readers a correct key/value scan primitive; each reader's audit call site is fixed with a `FormatException` on malformed lines. `TokenType.Int8/Int16/Float16` are removed (unused by every format; only the completeness test references them).

**Tech Stack:** .NET 10, C# 14, TUnit.

**Spec:** `docs/superpowers/specs/2026-08-22-design-simplification-master-design.md` (§6, §9, §10, §12)
**Audit:** `docs/bug-audit-report-2026-08-16.md` (BUG-01 YamlReader, BUG-02 IniReader, BUG-03 TomlReader)

## Global Constraints

- Readers remain `ref struct`; zero-alloc guarantee stays (spec §3).
- Generated code keeps concrete reader types — no genericization in this phase (spec §6 fallback).
- TokenType removal is a spec §8 Phase-3 break; `TokenTypeTests` updated in the same task.
- Audit P0 acceptance bar: the exact audit inputs must throw `FormatException` (or parse) — never `ArgumentOutOfRangeException`.
- No behavior change for valid inputs: the full 1110-test suite stays green.

---

### Task 1: `ITokenReader` contract + 5 implementations

**Files:**
- Create: `PicoSerDe.Core/src/ITokenReader.cs`
- Modify: `PicoJetson/src/PicoJetson/JsonReader.cs`, `PicoIni/src/PicoIni/IniReader.cs`, `PicoMsgPack/src/PicoMsgPack/MsgPackReader.cs`, `PicoToml/src/PicoToml/TomlReader.cs`, `PicoYaml/src/PicoYaml/YamlReader.cs` — add `: ITokenReader` and any missing members
- Test: create `tests/PicoSerDe.Integration.Tests/TokenReaderConformanceTests.cs`

**Interfaces:**
- Produces:
```csharp
namespace PicoSerDe.Core;

/// <summary>Cross-format token-stream contract implemented by all format readers.</summary>
public interface ITokenReader
{
    bool Read();
    TokenType TokenType { get; }
    bool TryGetInt32(out int value);
    bool TryGetInt64(out long value);
    bool TryGetFloat64(out double value);
    bool TryGetBool(out bool value);
    bool TrySkip();
    bool NeedsMoreData { get; }
    int Depth { get; }
}
```

- [ ] **Step 1: Write failing conformance tests**

```csharp
// tests/PicoSerDe.Integration.Tests/TokenReaderConformanceTests.cs
// References all 5 format modules + PicoSerDe.Core.
// NOTE: ref structs cannot be boxed to ITokenReader — the generic
// Run<TReader> helper (allows ref struct) is mandatory; never store
// a reader in an ITokenReader-typed variable.

public class TokenReaderConformanceTests
{
    [Test]
    [Arguments("json", "{ \"a\": 1 }"u8.ToArray())]
    [Arguments("ini", "a=1\n"u8.ToArray())]
    [Arguments("msgpack", new byte[] { 0x81, 0xA1, (byte)'a', 0x01 })]
    [Arguments("toml", "a = 1\n"u8.ToArray())]
    [Arguments("yaml", "a: 1\n"u8.ToArray())]
    public async Task Reader_ProducesObjectStartPropertyValueSequence(string format, byte[] data)
    {
        bool ok = format switch
        {
            "json" => Run(new JsonReader(data)),
            "ini" => Run(new IniReader(data)),
            "msgpack" => Run(new MsgPackReader(data)),
            "toml" => Run(new TomlReader(data)),
            "yaml" => Run(new YamlReader(data)),
            _ => false,
        };
        await Assert.That(ok).IsTrue();
    }

    // Sync helper returning bool: ref structs cannot cross into async methods,
    // and TUnit assertions only execute when awaited — so the checks run here
    // synchronously and the single awaited assertion reports the result.
    private static bool Run<TReader>(TReader reader)
        where TReader : ITokenReader, allows ref struct
    {
        return reader.Read()
            && reader.TokenType == TokenType.ObjectStart
            && reader.Read()
            && reader.TokenType == TokenType.PropertyName
            && reader.Read()
            && reader.TryGetInt32(out var v)
            && v == 1;
    }

    [Test]
    [Arguments("json", "{ \"a\": 1 }"u8.ToArray())]
    [Arguments("ini", "a=1\n"u8.ToArray())]
    [Arguments("msgpack", new byte[] { 0x81, 0xA1, (byte)'a', 0x01 })]
    [Arguments("toml", "a = 1\n"u8.ToArray())]
    [Arguments("yaml", "a: 1\n"u8.ToArray())]
    public async Task Reader_DepthReturnsToZero(string format, byte[] data)
    {
        bool ok = format switch
        {
            "json" => RunDepth(new JsonReader(data)),
            "ini" => RunDepth(new IniReader(data)),
            "msgpack" => RunDepth(new MsgPackReader(data)),
            "toml" => RunDepth(new TomlReader(data)),
            "yaml" => RunDepth(new YamlReader(data)),
            _ => false,
        };
        await Assert.That(ok).IsTrue();
    }

    private static bool RunDepth<TReader>(TReader reader)
        where TReader : ITokenReader, allows ref struct
    {
        while (reader.Read()) { }
        return reader.Depth == 0;
    }
}
```
(If a format's token shape differs from the ObjectStart/PropertyName expectation — e.g. the reader emits different structural tokens for its document — keep the format-specific assertion in a dedicated test and keep only the `Depth == 0` and end-of-input assertions in the shared helper. The IniReader emits ObjectStart/PropertyName/String per the 2026-05-29 spec precedent.)

- [ ] **Step 2: Run tests to verify they fail**

Expected: FAIL — `ITokenReader` undefined.

- [ ] **Step 3: Implement**

Create `ITokenReader` per Interfaces. For each reader, add `: ITokenReader` to the declaration and verify each member exists with a compatible signature (`Read()`, `TokenType`, `TryGetInt32/64/Float64/Bool`, `TrySkip`, `NeedsMoreData`, `Depth` — all verified present in the 2026-08-22 surface scan; add any missing member by forwarding to the existing implementation).

- [ ] **Step 4: Run conformance + full suites**

Run: `dotnet test tests/PicoSerDe.Integration.Tests/PicoSerDe.Integration.Tests.csproj -c Release` + `dotnet test PicoSerDe.slnx -c Release`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add PicoSerDe.Core/src/ITokenReader.cs PicoJetson/src/PicoJetson/JsonReader.cs PicoIni/src/PicoIni/IniReader.cs PicoMsgPack/src/PicoMsgPack/MsgPackReader.cs PicoToml/src/PicoToml/TomlReader.cs PicoYaml/src/PicoYaml/YamlReader.cs tests/PicoSerDe.Integration.Tests
git commit -m "feat(core): add ITokenReader contract implemented by all 5 readers"
```

---

### Task 2: `TextHelpers.ScanUntilLineEnd` + unit tests

**Files:**
- Modify: `shared/TextHelpers.cs` (compiled into PicoSerDe.Core)
- Test: `PicoSerDe.Core/tests/PicoSerDe.Core.Tests/` — create `TextHelpersScanTests.cs`

**Interfaces:**
- Produces:
```csharp
/// <summary>
/// Advances to the first '\n' or '\r' at or after <paramref name="position"/>,
/// or <paramref name="data"/>.Length if none exists. Never scans past a line end.
/// </summary>
public static int ScanUntilLineEnd(ReadOnlySpan<byte> data, int position);
```

- [ ] **Step 1: Write failing tests**

```csharp
[Test]
public async Task ScanUntilLineEnd_StopsAtNewline()
{
    var data = "abc\ndef"u8;
    await Assert.That(TextHelpers.ScanUntilLineEnd(data, 0)).IsEqualTo(3);
    await Assert.That(TextHelpers.ScanUntilLineEnd(data, 4)).IsEqualTo(7);
}

[Test]
public async Task ScanUntilLineEnd_StopsAtCarriageReturn()
{
    var data = "abc\rdef"u8;
    await Assert.That(TextHelpers.ScanUntilLineEnd(data, 0)).IsEqualTo(3);
}

[Test]
public async Task ScanUntilLineEnd_ReturnsLength_WhenNoLineEnd()
{
    var data = "abcdef"u8;
    await Assert.That(TextHelpers.ScanUntilLineEnd(data, 0)).IsEqualTo(6);
}

[Test]
public async Task ScanUntilLineEnd_HandlesStartAtLineEnd()
{
    var data = "ab\ncd"u8;
    await Assert.That(TextHelpers.ScanUntilLineEnd(data, 2)).IsEqualTo(2);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Expected: FAIL — method undefined.

- [ ] **Step 3: Implement**

```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public static int ScanUntilLineEnd(ReadOnlySpan<byte> data, int position)
{
    int len = data.Length;
    while (position < len && data[position] is not ((byte)'\n' or (byte)'\r'))
        position++;
    return position;
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test PicoSerDe.Core/tests/PicoSerDe.Core.Tests/PicoSerDe.Core.Tests.csproj -c Release`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add shared/TextHelpers.cs PicoSerDe.Core/tests/PicoSerDe.Core.Tests
git commit -m "feat(core): add TextHelpers.ScanUntilLineEnd"
```

---

### Task 3: IniReader P0 fix (BUG-02)

**Files:**
- Modify: `PicoIni/src/PicoIni/IniReader.cs` (key-scan ~lines 352-357, value slice ~line 402)
- Test: `PicoIni/tests/PicoIni.Tests/` — create `IniReaderAuditRegressionTests.cs`

- [ ] **Step 1: Write failing tests (audit inputs)**

```csharp
public class IniReaderAuditRegressionTests
{
    [Test]
    [Arguments("x")]
    [Arguments("]")]
    [Arguments("{")]
    [Arguments("}")]
    [Arguments("\"unterminated")]
    public async Task MalformedLine_ThrowsFormatException_NotArgumentOutOfRange(string line)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(line);
        var reader = new IniReader(data);
        await Assert.That(() =>
        {
            while (reader.Read()) { }
        }).Throws<FormatException>();
    }

    [Test]
    public async Task ValidLine_StillParses()
    {
        var reader = new IniReader("k=v\n"u8);
        await Assert.That(reader.Read()).IsTrue();
    }
}
```
(Check the IniReader constructor shape and `Read()` behavior against the existing test file first; the exact exception type must be `FormatException`.)

- [ ] **Step 2: Run tests to verify they fail**

Expected: FAIL — `ArgumentOutOfRangeException` (audit BUG-02 repro).

- [ ] **Step 3: Implement the fix**

Replace the key-scan (audit lines 352-357) with a line-bound scan + explicit failure:

```csharp
int keyStart = _position;
while (_position < _data.Length
    && _data[_position] != (byte)'='
    && _data[_position] is not ((byte)'\n' or (byte)'\r'))
    _position++;
_currentValue = TrimEnd(_data[keyStart.._position]);
if (_position >= _data.Length || _data[_position] != (byte)'=')
    throw new FormatException("Invalid INI line: expected '='.");
_position++;   // safe: _position now points at '='
```
(Use `TextHelpers.ScanUntilLineEnd` where it fits; verify the subsequent value slice at ~line 402 can no longer exceed `_data.Length`. Also check any other scan loop in the file for the same pattern — the audit found this class in 3 readers; scan IniReader for other `!= (byte)'='` / unconditional `_position++` loops.)

- [ ] **Step 4: Run tests**

Run: `dotnet test PicoIni/tests/PicoIni.Tests/PicoIni.Tests.csproj -c Release`
Expected: PASS (new regression tests + existing 125-test suite).

- [ ] **Step 5: Commit**

```bash
git add PicoIni/src/PicoIni/IniReader.cs PicoIni/tests/PicoIni.Tests
git commit -m "fix(picoini): line-bound key scan throws FormatException instead of out-of-bounds (audit BUG-02)"
```

---

### Task 4: TomlReader P0 fix (BUG-03)

**Files:**
- Modify: `PicoToml/src/PicoToml/TomlReader.cs` (key/value scans, `ReadKeyValueSpan` ~line 665, `ReadValueSpan` ~line 759)
- Test: `PicoToml/tests/PicoToml.Tests/` — create `TomlReaderAuditRegressionTests.cs`

- [ ] **Step 1: Write failing tests (audit inputs)**

```csharp
[Test]
[Arguments("\x00")]
[Arguments("\x01\x02")]
[Arguments("\"a\x01b\"")]
[Arguments("\"unterminated")]
public async Task ControlCharsOrUnterminatedString_ThrowFormatException(string input)
{
    var data = System.Text.Encoding.UTF8.GetBytes(input);
    var reader = new TomlReader(data);
    await Assert.That(() =>
    {
        while (reader.Read()) { }
    }).Throws<FormatException>();
}
```
(Verify the TomlReader ctor shape against the existing test file.)

- [ ] **Step 2: Run tests to verify they fail**

Expected: FAIL — `ArgumentOutOfRangeException` (audit BUG-03 repro).

- [ ] **Step 3: Implement the fix**

In `ReadKeyValueSpan` (~665): bound the key scan at line end like Task 3; if no `=` before line end → `FormatException("Invalid TOML line: expected '='.")`. In `ReadValueSpan` (~759): before slicing, check `_position <= _data.Length`; on encountering a control character (`< 0x20`, except `\t`) inside a basic string → `FormatException("Control character in string literal.")`; on unterminated quote → `FormatException("Unterminated string literal.")`. Ensure every scan loop stops at `_data.Length` and no unconditional `_position++` can push the index past the end. Scan the file for sibling loops with the same pattern and fix them all.

- [ ] **Step 4: Run tests**

Run: `dotnet test PicoToml/tests/PicoToml.Tests/PicoToml.Tests.csproj -c Release`
Expected: PASS (new regression tests + existing 120-test suite).

- [ ] **Step 5: Commit**

```bash
git add PicoToml/src/PicoToml/TomlReader.cs PicoToml/tests/PicoToml.Tests
git commit -m "fix(picotoml): guard value/string scans against out-of-bounds (audit BUG-03)"
```

---

### Task 5: YamlReader P0 fix (BUG-01)

**Files:**
- Modify: `PicoYaml/src/PicoYaml/YamlReader.cs` (key-scan ~lines 597-601, value slice ~line 849)
- Test: `PicoYaml/tests/PicoYaml.Tests/` — create `YamlReaderAuditRegressionTests.cs`

- [ ] **Step 1: Write failing tests (audit inputs)**

```csharp
[Test]
[Arguments("hello")]
[Arguments("12345")]
[Arguments("null")]
[Arguments("true")]
[Arguments("~")]
[Arguments("[1, 2]")]
[Arguments("[2147483648]")]
public async Task TopLevelScalarOrFlow_DoesNotCrash(string input)
{
    var data = System.Text.Encoding.UTF8.GetBytes(input);
    var reader = new YamlReader(data);
    // Acceptance: either parses or throws FormatException — never ArgumentOutOfRangeException.
    try
    {
        while (reader.Read()) { }
    }
    catch (FormatException)
    {
        return;
    }
}

[Test]
public async Task OrphanLineInMapping_ThrowsFormatException()
{
    var data = "key: value\norphan\n"u8;
    var reader = new YamlReader(data);
    await Assert.That(() =>
    {
        while (reader.Read()) { }
    }).Throws<FormatException>();
}
```
(Verify the YamlReader ctor shape against the existing test file.)

- [ ] **Step 2: Run tests to verify they fail**

Expected: FAIL — `ArgumentOutOfRangeException` (audit BUG-01 repro).

- [ ] **Step 3: Implement the fix**

Replace the key-scan (audit lines 597-601) with a line-bound scan:

```csharp
int ks = _position;
while (_position < _data.Length
    && _data[_position] != (byte)':'
    && _data[_position] is not ((byte)'\n' or (byte)'\r'))
    _position++;
_keySpan = TrimEnd(_data[ks.._position]);
if (_position >= _data.Length || _data[_position] != (byte)':')
    throw new FormatException("Invalid YAML mapping line: expected ':'.");
_position++;   // safe: _position points at ':'
```
Verify the value slice at ~line 849 (`_data[vs2.._position]`) can no longer exceed the buffer. For flow sequences (`[1, 2]`): ensure the mapping-key path is not entered for lines starting with `[`/`{` — if the reader dispatches flow structures after the key scan, move the flow detection before it; if it parses them as scalars, the line-bound scan + FormatException path is acceptable per the audit bar (parse or FormatException, never AOORE). Scan the file for sibling scan loops with the same pattern and fix them all.

- [ ] **Step 4: Run tests**

Run: `dotnet test PicoYaml/tests/PicoYaml.Tests/PicoYaml.Tests.csproj -c Release`
Expected: PASS (new regression tests + existing 130-test suite).

- [ ] **Step 5: Commit**

```bash
git add PicoYaml/src/PicoYaml/YamlReader.cs PicoYaml/tests/PicoYaml.Tests
git commit -m "fix(picoyaml): line-bound key scan throws FormatException instead of out-of-bounds (audit BUG-01)"
```

---

### Task 6: TokenType cleanup (remove Int8/Int16/Float16)

**Files:**
- Modify: `PicoSerDe.Core/src/TokenType.cs` (remove the 3 values + fix the stale "reserved" comment)
- Modify: `PicoSerDe.Core/tests/PicoSerDe.Core.Tests/TokenTypeTests.cs` (remove the 3 `Contains` lines in `EnumContainsAllScalarTokens`; update the stale comment)
- Test: `PicoSerDe.Core/tests/PicoSerDe.Core.Tests/TokenTypeTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
[Test]
public async Task RemovedScalarTokens_AreGone()
{
    var values = Enum.GetValues<TokenType>();
    await Assert.That(values).DoesNotContain(TokenType.Int8);
    await Assert.That(values).DoesNotContain(TokenType.Int16);
    await Assert.That(values).DoesNotContain(TokenType.Float16);
}
```

- [ ] **Step 2: Run test to verify it fails**

Expected: FAIL — values still present.

- [ ] **Step 3: Implement**

Remove `Int8`, `Int16`, `Float16` from `TokenType`. Replace the "Reserved for future format support" comment block with an accurate note: remaining numeric tokens are emitted by real readers/writers (MsgPack UInt8/16/32/64, Int32/64, Float32/64; JSON/TOML/YAML/INI Int32/64, Float64). Update `TokenTypeTests.EnumContainsAllScalarTokens` (drop the 3 lines) and remove the stale comment.

- [ ] **Step 4: Run full suite**

Run: `dotnet test PicoSerDe.slnx -c Release`
Expected: PASS. Also `grep -rn "TokenType.Int8\|TokenType.Int16\|TokenType.Float16" . --include="*.cs"` → empty.

- [ ] **Step 5: Commit**

```bash
git add PicoSerDe.Core/src/TokenType.cs PicoSerDe.Core/tests/PicoSerDe.Core.Tests/TokenTypeTests.cs
git commit -m "refactor(core): remove unused TokenType.Int8/Int16/Float16 (spec §8 Phase-3 break)"
```

---

### Task 7: Full verification + benchmarks

**Files:**
- Modify: `README.md` (root) — ITokenReader note; TokenType change note (breaking, if documented)

- [ ] **Step 1: Full clean rebuild + full suite**

Run: `dotnet build PicoSerDe.slnx -c Release` + `dotnet test PicoSerDe.slnx -c Release`
Expected: build succeeds; all tests pass.

- [ ] **Step 2: Benchmarks (reader hot paths)**

Run: `dotnet run -c Release --project PicoYaml/benchmarks/PicoYaml.Benchmarks/PicoYaml.Benchmarks.csproj` and PicoIni.Benchmarks
Expected: no regression vs the Phase-1-era baseline (reader changes are scan-boundary checks only; the hot paths are unchanged for valid input).

- [ ] **Step 3: Audit cross-check**

Walk `docs/bug-audit-report-2026-08-16.md` BUG-01/02/03 sections and confirm each repro input is covered by the new regression tests (Tasks 3-5).

- [ ] **Step 4: Update README + commit**

```bash
git add README.md
git commit -m "docs: document ITokenReader and TokenType value removal"
```
