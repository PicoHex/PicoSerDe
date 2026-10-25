# Ref Struct Serialization Support — Design

**Date**: 2026-06-28  
**Status**: Draft  
**Scope**: PicoJetson (JSON first; Ini/MsgPack/Toml/Yaml follow same pattern)

---

## 1. Motivation

PicoSerDe currently cannot serialize `ref struct` types. The root cause is a
C# type-system incompatibility:

- `ISerializer<in T>` cannot be used when `T` is a `ref struct` because
  interfaces do not support `where T : allows ref struct` (C# 13 restriction).
- `Cache<T>`—the internal static generic cache—cannot store
  `ISerializer<ref struct>` for the same reason.

This design adds ref struct serialization (only) to PicoSerDe without changing
the public API surface for existing callers.

## 2. Non-Goals

- **Anonymous types** — C# anonymous types have illegal identifier characters
  (`<>f__AnonymousType0'2`) and cannot be referenced in source-gen output.
  Users should use `record` instead.
- **Ref struct deserialization** — ref structs may contain `ref` fields,
  cannot be heap-allocated, and have no parameterless constructor guarantee.
  Deserialization is architecturally infeasible.
- **Implementing `ISerializer<T>` for ref structs** — impossible due to the
  interface constraint issue described above.

## 3. Design

### 3.1 New delegate: `SerDelegate<T>`

A delegate type in `PicoSerDe.Core` that carries `where T : allows ref struct`,
making it the universal serialization carrier for both regular and ref struct
types.

```csharp
// PicoSerDe.Core
public delegate void SerDelegate<T>(
    IBufferWriter<byte> writer,
    T value
) where T : allows ref struct;
```

### 3.2 Cache split

The single `Cache<T>` is split into two:

```csharp
// Serialization — allows ref struct
private static class SerCache<T> where T : allows ref struct
{
    internal static SerDelegate<T>? Handler;
}

// Deserialization — unchanged
private static class Cache<T>
{
    internal static IDeserializer<T>? Deserializer;
}
```

This works because:
- `SerCache<T> where T : allows ref struct` can be accessed from any caller
  whose `T` also carries `allows ref struct`.
- `Register<T>(ISerializer<T>, IDeserializer<T>)` does **not** carry
  `allows ref struct` on `T`. Passing a non-ref-struct `T` into
  `SerCache<T>` (which *allows* ref structs) is legal—`allows` is
  permissive, not restrictive.

### 3.3 Public API

Serialization entry points add `where T : allows ref struct`.
Registration methods are added; the existing two-parameter overload
is preserved for backward compatibility.

```csharp
// Serialization — constraint widened
public static void Serialize<T>(IBufferWriter<byte> writer, T value, ...)
    where T : allows ref struct;
public static string Serialize<T>(T value, ...)
    where T : allows ref struct;
public static byte[] SerializeToUtf8Bytes<T>(T value, ...)
    where T : allows ref struct;

// Registration — new delegate-based (SG primary path)
public static void Register<T>(SerDelegate<T> handler)
    where T : allows ref struct;

// Registration — compat (existing hand-written ISerializer<T> users)
public static void Register<T>(ISerializer<T> serializer, IDeserializer<T> deserializer);

// Registration — deserializer only
public static void RegisterDeserializer<T>(IDeserializer<T> deserializer);

// Deserialization — unchanged
public static T? Deserialize<T>(ReadOnlySpan<byte> data, ...);
```

### 3.4 Source Generator changes

#### 3.4.1 Type detection

In `GenInfrastructure.TransformTypeSymbol`, add a check:

```csharp
if (namedType.IsRefLikeType)
{
    // Ref struct path — generate static method + delegate registration
}
```

#### 3.4.2 Generated code shape — regular type

```csharp
// SERIALIZER: static method (replaces ISerializer<T> implementation)
file static class FooJsonSer
{
    public static void Serialize(IBufferWriter<byte> w, Foo v) { ... }
}

// DESERIALIZER: unchanged
file readonly struct FooJsonDeserializer : IDeserializer<Foo> { ... }

// REGISTRATION
file static class FooSerDeRegistration
{
    [ModuleInitializer]
    internal static void Register()
    {
        JsonSerializer.Register<Foo>(FooJsonSer.Serialize);
        JsonSerializer.RegisterDeserializer<Foo>(new FooJsonDeserializer());
    }
}
```

#### 3.4.3 Generated code shape — ref struct

```csharp
// SERIALIZER only (no deserializer)
file static class Span3DJsonSer
{
    public static void Serialize(IBufferWriter<byte> w, Span3D v) { ... }
}

// REGISTRATION (no deserializer call)
file static class Span3DSerDeRegistration
{
    [ModuleInitializer]
    internal static void Register()
    {
        JsonSerializer.Register<Span3D>(Span3DJsonSer.Serialize);
    }
}
```

The serializer body (`EmitSerializer` logic) is identical for both shapes;
only the enclosing type changes from `file readonly struct ... : ISerializer<T>`
to `file static class`.

#### 3.4.4 Nested ref struct

If a ref struct `Outer` contains another ref struct `Inner`, the SG generates
an inner helper `InnerJsonInner` as usual. No special handling is needed — the
inner helper is always a static class with a static method, which works for all
type kinds.

#### 3.4.5 Deserializer emission

`EmitDeserializer` and `EmitStreamingDeserializer` are **skipped** when
`IsRefLikeType` is true.

#### 3.4.6 `TransformType` (usage-driven pipeline)

The usage-driven pipeline currently has this filter:

```csharp
if (namedType.ContainingType is not null)
    return null;
```

This already passes for ref structs (they are not nested). The
`includeReadOnlyProperties` path tied to `[JsonConstructor]` is irrelevant.

## 4. Hot-path performance

```
Before (regular type):
  Cache<T>.Serializer.Serialize(writer, value)
    → interface virtual dispatch (1 indirection)

After (regular type):
  SerCache<T>.Handler.Invoke(writer, value)
    → delegate dispatch (1 indirection — equivalent cost)

After (ref struct):
  SerCache<T>.Handler.Invoke(writer, value)
    → direct static method call (0 virtual dispatch overhead)
```

No regression for regular types. Ref structs are slightly faster than interface
dispatch.

## 5. Backward compatibility

| Caller | Impact |
|--------|--------|
| `JsonSerializer.Serialize(new Foo())` | None — `allows ref struct` is invisible to non-ref-struct callers |
| `JsonSerializer.Register(new FooSer(), new FooDes())` | None — two-param overload preserved, internally wraps to delegate |
| Hand-written `struct FooSer : ISerializer<Foo>` | None — still registers through compat path |
| `JsonSerializer.Deserialize<Foo>(data)` | None — unchanged |

## 6. How `Register(ISerializer<T>, IDeserializer<T>)` works internally

```csharp
public static void Register<T>(ISerializer<T> serializer, IDeserializer<T> deserializer)
{
    SerCache<T>.Handler = (w, v) => serializer.Serialize(w, v);
    Cache<T>.Deserializer = deserializer;
}
```

The lambda captures `serializer` (a heap-allocated interface reference to the
boxed struct) and wraps it as `SerDelegate<T>`. One extra call frame in the
compat path; negligible overhead.

## 7. Affected files

| File | Change |
|------|--------|
| `PicoSerDe.Core/src/ISerializer.cs` (or new file) | Add `SerDelegate<T>` delegate |
| `PicoJetson/src/PicoJetson/JsonSerializer.cs` | Cache split, `allows ref struct` on serialization methods, new `Register<T>(SerDelegate<T>)`, compat `Register` kept |
| `PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs` | `TransformType` detects `IsRefLikeType`; `GenerateTypeCode` branches on serialization shape; skip deserializer for ref structs |
| `shared/Gen/GenInfrastructure.cs` | Possibly add a helper method for `IsRefLikeType` detection |
| `PicoIni`, `PicoMsgPack`, `PicoToml`, `PicoYaml` | Same pattern (deferred — JSON first) |

## 8. Test plan

1. **Unit — Regular type round-trip** — serialize via new delegate path,
   deserialize via existing path
2. **Unit — Ref struct serialize** — verify correct JSON output for
   `ref struct` with value-type fields
3. **Unit — Nested ref struct** — outer ref struct containing inner
   ref struct
4. **Unit — Ref struct with `ref` field** — verify serialization
   reads the referenced value
5. **Unit — Generic `ref struct Wrapper<T>`** — verify generic type
   arguments are handled
6. **Unit — Compat path** — hand-written `ISerializer<T>` + two-param
   `Register` produces correct output
7. **Unit — All 5 format SGs** — each format generates correct
   code for ref struct types (deferred, JSON-only initially)
8. **Integration — ModuleInitializer registration** — ref struct
   serializer is available at startup without explicit calls

## 9. Risks & mitigations

| Risk | Mitigation |
|------|-----------|
| `LangVersion` must support `allows ref struct` on delegates | All projects use `LangVersion>latest</LangVersion>` on `net10.0`; verified compiling |
| SG shape change for regular types | Emit identical serializer body; only outer wrapper differs |
| `ModuleInitializer` ordering with mixed regular + ref struct registrations | Both use `SerCache<T>`, no ordering dependency |
