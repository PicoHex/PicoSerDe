# PicoSerDe Token Layer Design

**Status**: Draft  
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
    bool Read();        // Advance to next token. Returns false at EOF.
    void Skip();        // Skip current token and any nested subtree.
                        // Zero allocation — only depth counting.

    // ── Zero-copy value access ──
    ReadOnlySpan<byte> RawValue { get; }  // Raw bytes of current token

    // Typed accessors (parse from RawValue on demand)
    byte   GetUInt8();
    short  GetInt16();
    int    GetInt32();
    long   GetInt64();
    ushort GetUInt16();
    uint   GetUInt32();
    ulong  GetUInt64();
    float  GetFloat32();
    double GetFloat64();
    bool   GetBool();

    // Zero-copy spans (point directly into the source buffer)
    ReadOnlySpan<byte> GetStringRaw();    // UTF-8 bytes, not copied
    ReadOnlySpan<byte> GetBytesRaw();     // Binary data, not copied
    ReadOnlySpan<byte> GetPropertyNameRaw(); // Property name, not copied

    // ── Extension ──
    byte GetExtensionTag();
    ReadOnlySpan<byte> GetExtensionRaw();
}
```

### 5.1 Key Design Decisions

| Decision | Choice | Rationale |
|---|---|---|
| `ref struct` | Yes | Prevents heap escape. Guarantees stack lifetime. Enables `Span<byte>` fields. |
| `Read()` returns `bool` | Yes | Clean EOF signal. No need for a separate `IsEOF` property. |
| `Skip()` subtree skipping | Yes | When deserializing, skip unknown fields without parsing their contents. Cost: O(depth) depth counting only. |
| No `TryReadXxx()` on reader | Yes | Reader doesn't know target types. Those methods belong on the deserializer layer. |
| `GetStringRaw()` returns `Span<byte>` | Yes | Consumer decides: keep as Span (zero-copy) or allocate `string` via `Encoding.UTF8.GetString()`. |
| `Depth` tracked by reader | Yes | Central defense against stack-overflow attacks. Max depth configurable per reader instance. |

### 5.2 Usage Pattern

```csharp
var reader = new SerDeReader(buffer);
while (reader.Read()) {
    switch (reader.TokenType) {
        case SerDeTokenType.ObjectStart: /* handle */ break;
        case SerDeTokenType.String:      /* use reader.GetStringRaw() */ break;
        case SerDeTokenType.Int32:       /* use reader.GetInt32() */ break;
        // ...
    }
}
```

---

## 6. TokenWriter API

```csharp
ref struct SerDeWriter {
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
    void WriteUInt8(byte value);
    void WriteInt16(short value);
    void WriteInt32(int value);
    void WriteInt64(long value);
    void WriteUInt16(ushort value);
    void WriteUInt32(uint value);
    void WriteUInt64(ulong value);

    // ── Floating ──
    void WriteFloat32(float value);
    void WriteFloat64(double value);

    // ── Variable-length ──
    void WriteString(ReadOnlySpan<byte> utf8Value);
    void WriteBytes(ReadOnlySpan<byte> value);

    // ── Extension ──
    void WriteExtension(byte tag, ReadOnlySpan<byte> data);

    // ── Output ──
    ReadOnlySpan<byte> GetWrittenSpan(); // Retrieve the written buffer
    long BytesWritten { get; }
}
```

Writer is simpler than Reader — no "current token" state, just "write this". The format implementation handles encoding each `Write*` call into the wire format.

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

## 8. Format Implementation

Each format provides its own `SerDeReader` / `SerDeWriter` implementation:

| Format | Reader Implementation | Writer Implementation |
|---|---|---|
| JSON | `JsonSerDeReader` — parses UTF-8 JSON byte stream | `JsonSerDeWriter` — emits UTF-8 JSON |
| MessagePack | `MsgPackSerDeReader` — reads MessagePack binary | `MsgPackSerDeWriter` — writes MessagePack binary |
| YAML | `YamlSerDeReader` (future) | `YamlSerDeWriter` (future) |

Format implementations share the same `SerDeTokenType` enum and the same `Deserializer<T>` / `Serializer<T>`. This is the key payoff: add a format, get all converters for free.

---

## 9. Design Principles

1. **Reader does one thing** — Structural token extraction from bytes. Never allocates. Never knows about target types.
2. **Zero-copy by default** — `GetStringRaw()`, `GetBytesRaw()`, `GetPropertyNameRaw()` return `ReadOnlySpan<byte>` pointing into the source buffer. Allocating `string` is opt-in at the consumer.
3. **Depth defense** — `Depth` is tracked on every `ObjectStart`/`ArrayStart`. Configurable max prevents stack overflow from malicious nesting.
4. **Skip is zero-cost** — `Skip()` does no parsing of the skipped content. It counts `ObjectStart`/`ArrayStart` depth and advances to the matching end. O(skip depth), not O(skip size).
5. **Format-agnostic core** — TokenType enum is the only shared vocabulary. Format-specific features go through Extension tokens or format-specific reader/writer subclasses.

---

## 10. Trade-offs & Risks

| Risk | Mitigation |
|---|---|
| **Typed scalars increase enum size** | 14 value tokens is manageable. Compiler switch exhaustiveness ensures correctness. |
| **`GetStringRaw()` may contain invalid UTF-8** | Document that validity is format-dependent. JSON guarantees valid UTF-8. MessagePack does not. Consumer validates if needed. |
| **Extension token is a compatibility trap** | Tag registry prevents collisions. Reserve extension tags per format. |
| **`ref struct` limits composition** | Cannot store reader in a class field or async method. Acceptable — the hot path is synchronous and stack-local. |
| **Forward-only means no random access** | Acceptable constraint. Random access would require buffering, violating the zero-alloc goal. |

---

## 11. Open Questions

1. **Should `Skip()` validate structural integrity?** Current design: No (performance). Alternative: validate that skipped content is well-formed. Trade-off: speed vs. error detection quality.
2. **Maximum depth default?** Suggested: 256. Matches System.Text.Json's default.
3. **Should writer support `WriteRaw(ReadOnlySpan<byte>)` for pass-through scenarios?** Useful for format conversion (JSON → MessagePack without deserializing). Risk: bypasses structural validation.

---

## 12. References

- System.Text.Json `Utf8JsonReader` / `Utf8JsonWriter` — [source](https://github.com/dotnet/runtime/tree/main/src/libraries/System.Text.Json/src/System/Text/Json/Reader)
- MessagePack-CSharp `MessagePackReader` — [source](https://github.com/MessagePack-CSharp/MessagePack-CSharp)
- Protocol Buffers Wire Types — [spec](https://protobuf.dev/programming-guides/encoding/)
