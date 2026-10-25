# PicoJetson JSONL Support — Design Spec

**Date:** 2026-06-24
**Status:** Approved
**Reference:** [jsonlines.org](https://jsonlines.org/), [System.Text.Json `DeserializeAsyncEnumerable`](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsonserializer.deserializeasyncenumerable?view=net-10.0)

---

## 1. Overview

Add JSON Lines (JSONL / NDJSON) read/write support to PicoJetson. JSONL is a text format where each line contains one complete JSON value, separated by `\n`. Per the jsonlines.org spec: UTF-8, no BOM, each line must be a valid JSON value, empty lines are not allowed.

All capabilities reuse the existing `JsonReader` / `JsonWriter` / source-generated serializer infrastructure — no new parser, no new writer, no SG changes required.

---

## 2. API Surface

All methods are added to the existing `JsonSerializer` static class (not a new type).

### 2.1 Core: Streaming Deserialization

```csharp
/// <summary>
/// Wraps a UTF-8 stream into an IAsyncEnumerable that yields one T per
/// top-level JSON value (JSONL mode) or per root-level array element
/// (JSON array mode).
/// </summary>
public static IAsyncEnumerable<T?> DeserializeAsyncEnumerable<T>(
    Stream stream,
    bool topLevelValues = true,
    JsonOptions? options = null,
    CancellationToken ct = default);
```

| Parameter | Default | Description |
|---|---|---|
| `topLevelValues` | `true` | `true` = JSONL (whitespace-separated top-level values); `false` = root-level JSON array `[...]` |
| `options` | `null` | Standard `JsonOptions`; applies to every element deserialized |
| `ct` | `default` | Cancellation token forwarded to `Stream.ReadAsync` |

Return type is `IAsyncEnumerable<T?>` — user consumes via `await foreach`.

### 2.2 Sync Batch Convenience

```csharp
/// <summary>Deserialize a JSONL byte span into an array of T.</summary>
public static T?[] DeserializeLines<T>(
    ReadOnlySpan<byte> data,
    JsonOptions? options = null);

/// <summary>Serialize an enumerable of T into a JSONL byte array.</summary>
public static byte[] SerializeLines<T>(
    IEnumerable<T> values,
    JsonOptions? options = null);
```

### 2.3 What We Are NOT Adding (YAGNI)

| Skipped | Reason |
|---|---|
| `PipeReader` overloads | `Stream` → `PipeReader` is internal detail; single overload enough |
| `JsonTypeInfo<T>` overloads | PicoJetson has no `JsonTypeInfo` concept |
| `SerializeAsyncEnumerable` | JSONL write is trivially `foreach` + `SerializeToUtf8Bytes` + `\n`; users who need async write can do that themselves |
| `DeserializeLinesAsync` (non-enumerable batch) | `DeserializeAsyncEnumerable` + `.ToArrayAsync()` covers this |
| Separate `JsonLinesSerializer` class | JSONL values are valid JSON; no new parser/writer needed |

---

## 3. Internal Design

### 3.1 `DeserializeAsyncEnumerable<T>` — Read Loop

**Why `StreamingFunc<T>`, not `IDeserializer<T>.Deserialize`:**

`IDeserializer<T>.Deserialize(ReadOnlySpan<byte>)` does not return how many bytes it consumed — the caller cannot locate the next value's boundary. The SG already generates a `StreamingFunc<T>` for every type with properties, registered automatically via `ModuleInitializer` into `JsonSerializer.StreamingCache<T>`. This function works on `ref JsonReader` and supports pause/resume across chunk boundaries (returns `ReadStatus.NeedMoreData` when more bytes are needed). **No SG changes required.**

```
┌──────────────────────────────────────────────────────────────┐
│ DeserializeAsyncEnumerable<T>(Stream)                          │
│                                                                │
│  Precondition: StreamingCache<T>.Func is not null              │
│  PipeReader pipe = PipeReader.Create(stream)                   │
│  bool consumedOpeningBracket = false                           │
│                                                                │
│  while (true):                                                 │
│    ReadResult r = await pipe.ReadAsync(ct)                     │
│    var reader = new JsonReader(r.Buffer, r.IsCompleted)        │
│    long consumed = 0                                           │
│                                                                │
│    while (true):                                               │
│      // ── Consume inter-value whitespace ──                   │
│      reader.SkipWhitespace()                                   │
│      if (reader.IsAtEnd):                                      │
│        if (r.IsCompleted): goto DONE                           │
│        else: break → read more                                 │
│                                                                │
│      // ── Array mode: handle [ , ] ──                         │
│      if (!topLevelValues):                                     │
│        if (!consumedOpeningBracket):                           │
│          跳过 '['; consumedOpeningBracket = true; continue     │
│        跳过 ']' → goto DONE     (空数组或抵达末尾)             │
│        跳过 ',' → continue      (值间分隔符)                   │
│                                                                │
│      // ── Deserialize one value via StreamingFunc ──          │
│      status = StreamingCache<T>.Func(ref reader, out T? val)   │
│      if (status == NeedMoreData): break → read more            │
│      if (status == EndOfInput): goto DONE                      │
│      yield return val                                          │
│      consumed = reader.BytesConsumed                           │
│                                                                │
│    pipe.AdvanceTo(reader.ExportState().Position,               │
│                   r.Buffer.End)                                │
│                                                                │
│  DONE:                                                         │
│    pipe.Complete()                                             │
└──────────────────────────────────────────────────────────────┘
```

**Key invariants:**
- Each value is deserialized via `StreamingCache<T>.Func`, the same streaming delegate already generated by the SG and used by `DeserializeFromStreamAsync<T>`.
- Between values: `SkipWhitespace()` handles spaces, tabs, `\n`, `\r` (incl. `\r\n` line endings).
- Array mode: the outer loop consumes `[`, `,`, `]` tokens; the streaming func handles each element.
- After `yield return`, the reader is positioned right after the closing token of the value (e.g., after `}`); the next iteration's `SkipWhitespace()` advances past whitespace to the next value or delimiter.

### 3.2 `DeserializeLines<T>` — Sync Batch

```
DeserializeLines(ReadOnlySpan<byte> data):
  1. 按 \n 分割行
  2. 跳过空白行
  3. 对每行调用 JsonSerializer.Deserialize<T>(lineBytes)
  4. 返回 T?[]
```

Implementation approach: iterate line by line using `\n` splitting. Each line is self-contained JSON — no cross-line state. Trailing `\r` from `\r\n` line endings is naturally handled: `Deserialize<T>`'s internal `JsonReader` calls `SkipWhitespace()` (which includes `0x0D`) after consuming the JSON value, so the `\r` is silently skipped before `Read()` returns `false`.

### 3.3 `SerializeLines<T>` — Sync Batch

```
SerializeLines(IEnumerable<T> values):
  var buf = new ArrayBufferWriter<byte>();
  foreach (var v in values):
    JsonSerializer.Serialize(buf, v, options);
    buf.Write("\n"u8);
  return buf.WrittenSpan.ToArray();
```

Reuses `JsonSerializer.Serialize<T>(IBufferWriter, ...)` per element. No new writer logic.

---

## 4. Error Handling

| Scenario | Behavior |
|---|---|
| Empty stream (JSONL mode) | Empty async enumerable; no exception |
| Empty stream (array mode) | `FormatException`: expected `[` |
| Malformed line in JSONL | `FormatException` propagated from `Deserialize<T>`, wrapped with line-number context where feasible |
| Extra content after array `]` (array mode) | `FormatException` |
| `CancellationToken` triggered | `OperationCanceledException` |

---

## 5. Options Forwarding

All options in `JsonOptions` apply per-element:

| Option | Effect on JSONL mode |
|---|---|
| `MaxDepth` | Per-element depth limit |
| `PropertyNameCaseInsensitive` | Per-element property matching |
| `AllowTrailingCommas` | Per-element lenient parsing |
| `ReadCommentHandling` | Comments between elements (whitespace) are skipped naturally |
| `NumberHandling` | Per-element NaN/Inf handling |

---

## 6. Source Generator Impact

**None.** The SG already generates both `ISerializer<T>` / `IDeserializer<T>` (used by sync batch methods) and `StreamingFunc<T>` (used by `DeserializeAsyncEnumerable<T>`). Both are registered via `ModuleInitializer` into `Cache<T>` and `StreamingCache<T>` respectively.

**Prerequisite:** `StreamingFunc<T>` is only generated for types with properties. `DeserializeAsyncEnumerable<T>` requires `StreamingCache<T>.Func` to be non-null; calling it for an unregistered type throws `InvalidOperationException`. This matches the existing `DeserializeFromStreamAsync<T>` contract.

---

## 7. Test Plan

| Category | Tests |
|---|---|
| **JSONL deserialization** | Empty file, single record, multi-record, trailing newline, no trailing newline, `\r\n` line endings |
| **JSON array mode** | Empty `[]`, single element, multi-element, nested arrays |
| **Options forwarding** | `PropertyNameCaseInsensitive`, `AllowTrailingCommas`, `ReadCommentHandling` in JSONL mode |
| **Error cases** | Invalid JSON on line N, unclosed string, depth exceeded on single record |
| **Cancellation** | CancellationToken cancels mid-stream |
| **Serialization** | Round-trip: serialize → deserialize, empty collection, single, multi |
| **Sync batch** | `DeserializeLines` / `SerializeLines` correctness |

---

## 8. Files Changed

| File | Change |
|---|---|
| `PicoJetson/src/PicoJetson/JsonSerializer.cs` | Add `DeserializeAsyncEnumerable<T>`, `DeserializeLines<T>`, `SerializeLines<T>` |
| `PicoJetson/tests/.../JsonLinesTests.cs` | New test file |
