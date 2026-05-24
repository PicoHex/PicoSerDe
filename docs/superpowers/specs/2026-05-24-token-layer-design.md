# PicoSerDe Token Layer Design

**Status**: Approved  
**Date**: 2026-05-24  
**Topic**: Core serialization token abstraction — reader/writer contract

---

## 1. Motivation

PicoSerDe is a greenfield .NET serialization library targeting **extreme performance**: zero heap allocation, SIMD-accelerated parsing, stack-only ref structs, and multi-format support (JSON, MessagePack, future formats undecided).

This spec defines the **token layer** — the foundational abstraction that all format implementations share. It is the single most impactful architectural decision in the project.

### Why Tokens?

Serializers that skip token abstraction (direct byte-to-object) optimize for one format but sacrifice composability, multi-format reuse, and debuggability. Token-based designs (System.Text.Json, MessagePack-CSharp, Protobuf wire types) have proven these benefits:

| Property | Without Tokens | With Tokens |
|---|---|---|
| Multi-format reuse | Each format must reimplement everything | Shared deserializer layer |
| Zero-copy value access | Hard (format-specific code everywhere) | Natural — Reader hands out Spans |
| SIMD vectorization | Must redo per format | Token parser is format-agnostic |
| Error localization | Error at byte offset with no context | "Expected X, got Y at depth N" |
| Testability | Need real byte streams | Token sequences are testable in isolation |

---

## 2. Design Goals

1. **Zero allocation** — TokenReader and TokenWriter are `ref struct`, all value access returns `ReadOnlySpan<byte>`
2. **Format-agnostic** — One token enum covers JSON, MessagePack, YAML, and binary formats
3. **Forward-only streaming** — No buffering the entire document; suitable for network streams and large files
4. **Depth-bounded** — Malicious nesting can't overflow the stack; max depth configurable
5. **Clean separation** — TokenReader knows "what is this?" (structural parsing). Deserializer knows "what does this mean?" (semantic mapping)
6. **SIMD-ready** — Token parsing hot paths operate on raw byte buffers, amenable to vectorization

---

## 3. Architecture Overview

```
                         ┌──────────────────┐
        byte[] ─────────►│   TokenReader    │  ref struct, stack-only
                         │  (format impl)   │  forward-only, zero-copy
                         └────────┬─────────┘
                                  │ token stream
                                  ▼
                         ┌──────────────────┐
                         │   Deserializer   │  consumes tokens → builds objects
                         └──────────────────┘

                         ┌──────────────────┐
        byte[] ◄─────────│   TokenWriter    │  ref struct, stack-only
                         │  (format impl)   │
                         └────────┬─────────┘
                                  ▲
                         ┌────────┴─────────┐
                         │    Serializer    │  produces tokens from objects
                         └──────────────────┘
```

Four layers:

| Layer | Role | Allocations |
|---|---|---|
| **TokenType** enum | Structural vocabulary shared by all formats | Zero (enum) |
| **TokenReader** | Pulls typed tokens from a byte buffer | Zero (ref struct) |
| **TokenWriter** | Pushes typed tokens into an output buffer | Zero (ref struct) |
| **Deserializer / Serializer** | Maps between tokens and .NET objects | Close to zero (struct-based converters) |

---

## 4. TokenType Definition

Tokens are organized into three categories by semantic role:

### 4.1 Structural Tokens

```csharp
enum SerDeTokenType {
    None = 0,

    // Object / Map
    ObjectStart,
    ObjectEnd,

    // Array / List
    ArrayStart,
    ArrayEnd,

    // Key within an object
    PropertyName,

    // ---- Scalar Value Tokens (typed by precision) ----
    Null,
    Bool,

    // Signed integers
    Int8, Int16, Int32, Int64,

    // Unsigned integers
    UInt8, UInt16, UInt32, UInt64,

    // Floating point
    Float16, Float32, Float64,

    // Variable-length
    String,
    Bytes,

    // ---- Extension ----
    Extension,  // Format-specific (e.g., MessagePack Timestamp)
}
```

### 4.2 Design Rationale

**Why typed scalars (Int8..Int64, not just `Number`)?**

System.Text.Json uses a single `Number` token and forces the consumer to `TryGetInt32()` / `TryGetInt64()` / `TryGetDouble()`. This:
- Requires the consumer to know the target type at read time
- Forces boxing of precision metadata

Typed scalar tokens let the deserializer know the *wire format's* intent without guessing. A MessagePack `int 8` is a distinct token from `int 32` — the consumer can decide to widen or reject.

**Why Extension?**

Formats have non-standard types. MessagePack has `Timestamp`, `FixExt`. Avro has schema-driven unions. Rather than bloating the core token set, `Extension` carries a format-specific tag + raw payload. The deserializer handles it or rejects it.

---

## 5. TokenReader API

```csharp
ref struct SerDeReader {
    // ── Position ──
    SerDeTokenType TokenType { get; }
    int Depth { get; }
    long BytesConsumed { get; }

    // ── Navigation ──
    bool Read();        // Advance to next token. Returns false at EOF (not at error).
                        // Format errors throw. Following S.T.J's early-return
                        // pattern for JIT inlining friendliness.
    void Skip();        // Skip current token and any nested subtree.
                        // Validates structural integrity — corrupted/malformed
                        // content within the skipped subtree throws.

    // ── Zero-copy value access ──
    ReadOnlySpan<byte> RawValue { get; }  // Raw bytes of current token

    // Typed accessors — Try pattern. No silent truncation.
    // Returns false on overflow or type mismatch.
    bool TryGetInt8(out sbyte value);
    bool TryGetInt16(out short value);
    bool TryGetInt32(out int value);
    bool TryGetInt64(out long value);
    bool TryGetUInt8(out byte value);
    bool TryGetUInt16(out ushort value);
    bool TryGetUInt32(out uint value);
    bool TryGetUInt64(out ulong value);
    bool TryGetFloat16(out Half value);
    bool TryGetFloat32(out float value);
    bool TryGetFloat64(out double value);
    bool TryGetBool(out bool value);

    // Zero-copy spans.
    // String: reader handles unescape internally (fast path: no '\' → points
    // to source buffer; slow path: stackalloc decode). Deserializer always
    // receives decoded UTF-8 bytes regardless of source format.
    ReadOnlySpan<byte> GetStringRaw();
    ReadOnlySpan<byte> GetBytesRaw();
    ReadOnlySpan<byte> GetPropertyNameRaw();

    // ── Extension ──
    byte GetExtensionTag();
    ReadOnlySpan<byte> GetExtensionRaw();

    // ── Array length hint (optional) ──
    int? ArrayLength { get; }   // non-null for formats that know array size
                                // upfront (MessagePack). null for JSON.
}
```

### 5.1 Key Design Decisions

| Decision | Choice | Rationale |
|---|---|---|
| `ref struct` | Yes | Prevents heap escape. Guarantees stack lifetime. Enables `Span<byte>` fields. |
| `Read()` returns `bool` | Yes | Clean EOF signal. Format errors throw. Follows S.T.J's early-return pattern for JIT inlining. |
| `Skip()` subtree skipping | Validates | Silent corruption from malformed skipped content is a catastrophic bug class. Cost: O(skip size), acceptable for a non-hot-path operation. |
| Typed accessors use `Try` pattern | Yes | No silent truncation. `TryGetInt32()` returns false on overflow. No bare `GetInt32()` that throws — keeps error handling explicit. |
| `GetStringRaw()` returns decoded UTF-8 | Yes | Reader handles escape sequences internally. Fast path (no `\`): zero-copy into source buffer. Slow path: stackalloc decode. Deserializer always receives clean decoded bytes. |
| `Depth` tracked by reader | Yes | Central defense against stack-overflow attacks. Default max depth: 256 (configurable). |
| Token order not validated | Yes | Reader outputs raw token stream. Semantic order validation (e.g., PropertyName must be followed by a value) is the Deserializer's responsibility. |
| Multi-document support | Yes | After `Read()` returns false (EOF on current document), caller advances buffer offset and creates a new reader for the next document. Reader does not own buffer lifecycle. |

### 5.2 Usage Pattern

```csharp
var reader = new SerDeReader(buffer);
while (reader.Read()) {
    switch (reader.TokenType) {
        case SerDeTokenType.ObjectStart: /* handle */ break;
        case SerDeTokenType.String:
            var strSpan = reader.GetStringRaw(); // already unescaped
            break;
        case SerDeTokenType.Int32:
            if (reader.TryGetInt32(out var val)) { /* use val */ }
            break;
        // ...
    }
}
// Read() returned false → EOF for this document.
// To read next document in a multi-doc stream:
//   advance buffer past BytesConsumed, create new reader.
```

### 5.3 String Unescape Strategy

JSON strings may contain escape sequences (`\n`, `\u0041`). Reader uses a fast-path approach:

```
Read() → scan string for 0x5C ('\')   (SIMD-accelerable)
  ├─ No '\' found → GetStringRaw() points directly into source buffer (zero-copy, ~95% of strings)
  └─ '\' found   → stackalloc decode into internal Span<byte>
                    → if too long (rare), rent from ArrayPool<byte>.Shared
```

This keeps the zero-alloc promise for the common case while correctly handling escaped strings. MessagePack and binary formats never have escape sequences — their `GetStringRaw()` always takes the fast path.

---

## 6. TokenWriter API

```csharp
ref struct SerDeWriter {
    // Writer receives an IBufferWriter<byte> from the caller.
    // Writer does NOT own buffer lifecycle or expansion policy.
    // Standard .NET pattern (System.Text.Json, MessagePack-CSharp).

    // ── Structural ──
    void WriteObjectStart();
    void WriteObjectEnd();
    void WriteArrayStart();
    void WriteArrayEnd();
    void WritePropertyName(ReadOnlySpan<byte> utf8Name);

    // ── Null / Bool ──
    void WriteNull();
    void WriteBool(bool value);

    // ── Integers ──
    void WriteInt8(sbyte value);
    void WriteInt16(short value);
    void WriteInt32(int value);
    void WriteInt64(long value);
    void WriteUInt8(byte value);
    void WriteUInt16(ushort value);
    void WriteUInt32(uint value);
    void WriteUInt64(ulong value);

    // ── Floating ──
    void WriteFloat16(Half value);
    void WriteFloat32(float value);
    void WriteFloat64(double value);

    // ── Variable-length ──
    void WriteString(ReadOnlySpan<byte> utf8Value);
    void WriteBytes(ReadOnlySpan<byte> value);

    // ── Extension ──
    void WriteExtension(byte tag, ReadOnlySpan<byte> data);

    // ── Output ──
    long BytesWritten { get; }
    void Flush();   // Commit pending bytes to underlying IBufferWriter
}
```

Writer is simpler than Reader — no "current token" state, just "write this". The format implementation handles encoding each `Write*` call into the wire format. Buffer management is delegated to `IBufferWriter<byte>` (typically `ArrayPoolBufferWriter<byte>`).

---

## 7. Serialization Bridge

### 7.1 Deserializer Contract

```csharp
interface ISerDeDeserializer<T> {
    T Deserialize(ref SerDeReader reader);
}
```

The deserializer consumes a token stream. It advances the reader, switching on `TokenType` to build the target object:

```csharp
// Example: deserializing Person { Name: string, Age: int, Tags: string[] }
static Person DeserializePerson(ref SerDeReader reader) {
    reader.Read(); // Expect ObjectStart
    var person = new Person();

    while (reader.TokenType != SerDeTokenType.ObjectEnd) {
        var propName = reader.GetPropertyNameRaw();
        reader.Read(); // Advance to value
        if (propName.SequenceEqual("Name"u8))
            person.Name = Encoding.UTF8.GetString(reader.GetStringRaw());
        else if (propName.SequenceEqual("Age"u8))
            person.Age = reader.GetInt32();
        else if (propName.SequenceEqual("Tags"u8))
            person.Tags = DeserializeStringArray(ref reader);
        else
            reader.Skip(); // Unknown field — skip entire subtree
        reader.Read(); // Advance past value to next token
    }
    return person;
}
```

### 7.2 Serializer Contract

```csharp
interface ISerDeSerializer<T> {
    void Serialize(ref SerDeWriter writer, T value);
}
```

Symmetrical: serializer calls `Write*` methods on the writer to emit tokens.

---

## 8. Format Implementation (PicoHex Ecosystem)

The token layer (`SerDeTokenType`, `SerDeReader`, `SerDeWriter`) is the **shared base** of the PicoHex serialization ecosystem. Each format library provides its own reader/writer implementation while sharing the same token vocabulary and deserializer/serializer contracts.

| Library | Reader | Writer | Status |
|---|---|---|---|
| **PicoSerDe** (this repo) | Abstract base + shared contracts | Abstract base + shared contracts | Current |
| **PicoJson** | `JsonSerDeReader` — UTF-8 JSON parsing | `JsonSerDeWriter` — UTF-8 JSON emission | Future |
| **PicoProtobuf** | `ProtobufSerDeReader` — wire type decoding | `ProtobufSerDeWriter` — wire type encoding | Future |
| **PicoYml** | `YamlSerDeReader` | `YamlSerDeWriter` | Future |
| **PicoMsgPack** | `MsgPackSerDeReader` | `MsgPackSerDeWriter` | Future |

All format libraries share the same `Deserializer<T>` / `Serializer<T>`. This is the key payoff: add a format, get all converters for free.

---

## 9. Design Principles

1. **Reader does one thing** — Structural token extraction from bytes. Never allocates. Never knows about target types.
2. **Zero-copy by default** — `GetStringRaw()`, `GetBytesRaw()`, `GetPropertyNameRaw()` return `ReadOnlySpan<byte>` pointing into the source buffer. Allocating `string` is opt-in at the consumer.
3. **Depth defense** — `Depth` is tracked on every `ObjectStart`/`ArrayStart`. Configurable max prevents stack overflow from malicious nesting.
4. **Skip validates** — `Skip()` traverses the skipped subtree, validating every token. Silent data corruption from malformed skipped content is unacceptable. Cost: O(skip size), not just O(skip depth). The trade-off is correctness over raw speed.
5. **Format-agnostic core** — TokenType enum is the only shared vocabulary. Format-specific features go through Extension tokens or format-specific reader/writer subclasses.

---

## 10. Trade-offs & Risks

| Risk | Mitigation |
|---|---|
| **Typed scalars increase enum size** | 14 value tokens is manageable. Compiler switch exhaustiveness ensures correctness. |
| **`GetStringRaw()` slow path allocates** | Only triggers when `\` is present (~5% of strings). Stackalloc handles short escapes; ArrayPool for the rare long ones. |
| **Extension token is a compatibility trap** | Tag registry prevents collisions. Reserve extension tags per format. |
| **`ref struct` limits composition** | Cannot store reader in a class field or async method. Acceptable — the hot path is synchronous and stack-local. |
| **Forward-only means no random access** | Acceptable constraint. Random access would require buffering, violating the zero-alloc goal. |
| **Skip validation costs O(skip size)** | Acceptable. Skipping is a non-hot path (unknown fields, optional sections). Correctness > raw throughput for this operation. |
| **Token order validation deferred to Deserializer** | Deserializers (especially generated ones) are the natural place for semantic constraints. Reader stays format-agnostic. |

---

## 11. Resolved Design Decisions

1. **Token granularity: 14 scalar types, no MessagePack fixint/fixarray exposure.** Reader normalizes wire-format encoding details. MessagePack `positive fixint(42)` and `int8(42)` both emit `Int8`. Deserializer sees unified token types.

2. **Array uses single `ArrayStart`/`ArrayEnd` pair.** MessagePack's `fixarray`/`array16`/`array32` are wire-format optimizations. Reader normalizes them. Optional `ArrayLength` hint available for formats that know size upfront.

3. **DateTime NOT a core token.** Token layer describes what the byte stream contains (`String` or `Extension`). Semantic interpretation ("this string is a datetime") belongs to the Deserializer, driven by target type metadata from Source Generators.

4. **`Skip()` MUST validate structural integrity.** Silent corruption from malformed skipped content is a catastrophic bug class. Cost: O(skip size), acceptable for a non-hot-path operation.

5. **Maximum depth default: 256.** Configurable per reader instance.

6. **No `WriteRaw(ReadOnlySpan<byte>)` on Writer.** Raw bytes from one format are not valid raw bytes in another (e.g., JSON-escaped `\n` vs literal `0x0A`). Cross-format pass-through must go through proper deserialization and re-serialization.

7. **String unescape in Reader.** Fast path (no `\`): zero-copy into source buffer. Slow path: stackalloc decode. Deserializer always receives clean decoded UTF-8 bytes regardless of source format.

8. **Error model: `Read()` returns `bool` for EOF, throws on format error.** Follows S.T.J's pattern — early-return for JIT inlining friendliness. No error-code tuples.

9. **Typed value accessors: `TryGet*` only.** No silent truncation. `TryGetInt32()` returns false on overflow. No bare `GetInt32()` that throws.

10. **Token order NOT validated by Reader.** Semantic order (e.g., `PropertyName` must be followed by a value token) is the Deserializer's responsibility. This keeps the Reader format-agnostic.

11. **Multi-document stream support.** After `Read()` returns false (EOF on current document), caller advances buffer offset and creates a new reader. Reader does not own buffer lifecycle.

12. **Writer buffer: `IBufferWriter<byte>`.** Standard .NET pattern. Writer delegates buffer expansion to the caller-supplied implementation (typically `ArrayPoolBufferWriter<byte>`).

---

## 12. References

- System.Text.Json `Utf8JsonReader` / `Utf8JsonWriter` — [source](https://github.com/dotnet/runtime/tree/main/src/libraries/System.Text.Json/src/System/Text/Json/Reader)
- MessagePack-CSharp `MessagePackReader` — [source](https://github.com/MessagePack-CSharp/MessagePack-CSharp)
- Protocol Buffers Wire Types — [spec](https://protobuf.dev/programming-guides/encoding/)
