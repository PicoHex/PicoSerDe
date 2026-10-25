# Polymorphic Deserialization (Type Discriminator)

**Date**: 2026-07-01  
**Status**: Design approved, pending implementation plan

## Overview

Add polymorphic serialization/deserialization support to PicoSerDe — enabling base-type APIs that dispatch to derived types at runtime based on a discriminator field. Follows System.Text.Json's field-based discriminator pattern. All five formats benefit from a single cross-format declaration.

## Motivation

Current PicoSerDe requires `JsonSerializer.Deserialize<T>(...)` where `T` is a concrete type known at compile time. There is no way to deserialize into a base type and have the runtime dispatch to the correct derived type. This forces users to fall back to System.Text.Json for scenarios like:

- Event sourcing: `SessionEntry` base with `MessageEntry`, `CompactionEntry` subtypes
- Plugin systems: `IPluginConfig` with provider-specific configs
- SSE event streams: heterogeneous event types distinguished by `"type"` field

All of these are AOT-compatible (discriminator switch is closed at compile time) — the feature gap is purely one of code generation, not a fundamental AOT limitation.

## Design

### Attributes (PicoSerDe.Core)

Two new attributes in `PicoSerDe.Core/src/`:

```csharp
/// Optional: configures discriminator behavior. Default discriminator field name is "$type".
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class PicoPolymorphicAttribute : Attribute
{
    public string TypeDiscriminatorPropertyName { get; set; } = "$type";
}

/// Declares a derived type and its discriminator value. Multiple allowed on the same base type.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
public sealed class PicoDerivedTypeAttribute : Attribute
{
    public Type DerivedType { get; }
    public string TypeDiscriminator { get; }
    public PicoDerivedTypeAttribute(Type derivedType, string typeDiscriminator) { ... }
}
```

### User API

```csharp
[PicoSerializable]
[PicoPolymorphic(TypeDiscriminatorPropertyName = "kind")]  // optional, default "$type"
[PicoDerivedType(typeof(MessageEntry), "message")]
[PicoDerivedType(typeof(CompactionEntry), "compaction")]
abstract class SessionEntry { }

// Serialization — runtime type auto-detected, discriminator written
var json = JsonSerializer.Serialize<SessionEntry>(messageEntry);
// → {"$type":"message","content":"hello","sequence":1}

// Deserialization — discriminator read, correct derived type returned
var entry = JsonSerializer.Deserialize<SessionEntry>(bytes);
// entry is MessageEntry at runtime
```

### Key Behaviors (matching System.Text.Json)

1. **Discriminator must be first field** in the JSON object. Reader is forward-only; encountering any other property before the discriminator throws `FormatException`.

2. **Flat discriminator namespace**: all leaf types are declared directly on the root base class. Intermediate abstract types (e.g., `Mammal : Animal`) are transparent to the root's discriminator space. Intermediate types can declare their own `[PicoDerivedType]` for independent hierarchies.

3. **Write behavior**: discriminator is written only when the static type is the base (polymorphic) type. Serializing a concrete derived type directly does not write the discriminator.

4. **Unknown discriminator** throws `FormatException` by default.

5. **Cross-assembly**: derived types must be compile-time reachable from the base type's assembly (required by `typeof()`). This is the same constraint as System.Text.Json.

### Source Generator Architecture

#### TypeInfo Changes (shared/Gen/GenInfrastructure.cs)

```csharp
internal readonly record struct TypeInfo(
    // ... existing fields ...
    string? DiscriminatorPropertyName,           // null = non-polymorphic; default "$type"
    ImmutableArray<DerivedTypeInfo> DerivedTypes  // empty = non-polymorphic
);

internal readonly record struct DerivedTypeInfo(
    string FullyQualifiedName,
    string TypeDiscriminator
);
```

#### New Pipeline E (each SG's Initialize)

```csharp
var polyPipeline = context
    .SyntaxProvider.ForAttributeWithMetadataName(
        "PicoSerDe.Core.PicoDerivedTypeAttribute",
        predicate: static (node, _) => node is TypeDeclarationSyntax,
        transform: static (ctx, _) => ExpandPolymorphicTypes(ctx, Config, Attrs)
    )
    .SelectMany(static (types, _) => types);
```

`ExpandPolymorphicTypes`:
- Extracts `[PicoDerivedType]` and `[PicoPolymorphic]` from `ctx.TargetSymbol`
- Returns TypeInfo for the base type (with `DerivedTypes` populated and `DiscriminatorPropertyName` set)
- Returns TypeInfo for each derived type (with `IsPolymorphicDerived = true` marker)
- For each derived type: calls `DetectConstructor` to populate `CtorParams` for `[JsonConstructor]` support

Pipeline E output is merged into `all` alongside existing pipelines A-D. The `seen` HashSet in `GenerateAll` handles deduplication.

#### Generated Code (JSON example)

**Poly helper** for each derived type — generated in `GenerateAll` alongside existing inner helpers:

```csharp
file static class MessageEntryJsonPoly
{
    // Serialize properties only (no { } — caller controls object boundaries)
    internal static void SerializeProps(ref JsonWriter jw, MessageEntry value)
    {
        jw.WritePropertyName("Content"u8);
        jw.WriteString(Encoding.UTF8.GetBytes(value.Content));
        jw.WritePropertyName("Sequence"u8);
        jw.WriteNumber(value.Sequence);
    }

    // Deserialize from current reader position (reader past discriminator)
    internal static MessageEntry Deserialize(ref JsonReader reader)
    {
        // If type has [JsonConstructor]: declare temp vars, use ctor at end
        // Otherwise: new T() + property assignment
        var obj = new MessageEntry();
        while (reader.Read() && reader.TokenType == TokenType.PropertyName)
        {
            var __n = reader.GetStringRaw();
            reader.Read();
            if (TextHelpers.Eq(__n, "Content"u8, ...))
                obj.Content = Encoding.UTF8.GetString(reader.GetStringRaw());
            else if (TextHelpers.Eq(__n, "Sequence"u8, ...))
                { reader.TryGetInt32(out var __v); obj.Sequence = __v; }
            else reader.TrySkip();
        }
        return obj;
    }
}
```

**Base type serializer** — runtime type switch:

```csharp
file static class SessionEntryJsonSer
{
    public static void Serialize(IBufferWriter<byte> writer, SessionEntry value)
    {
        var jw = new JsonWriter(writer, ...);
        jw.WriteStartObject();
        jw.WritePropertyName("$type"u8);  // or from [PicoPolymorphic]
        
        switch (value)
        {
            case MessageEntry me:
                jw.WriteString("message"u8);
                MessageEntryJsonPoly.SerializeProps(ref jw, me);
                break;
            case CompactionEntry ce:
                jw.WriteString("compaction"u8);
                CompactionEntryJsonPoly.SerializeProps(ref jw, ce);
                break;
        }
        jw.WriteEndObject();
    }
}
```

**Base type deserializer** — discriminator dispatch (zero-alloc if/else if chain):

```csharp
file readonly struct SessionEntryJsonDeserializer : IDeserializer<SessionEntry>
{
    public SessionEntry Deserialize(ReadOnlySpan<byte> data)
    {
        var reader = new JsonReader(data);
        reader.Read(); // {
        reader.Read(); // discriminator property name
        reader.Read(); // discriminator value
        var __disc = reader.GetStringRaw();
        
        if (TextHelpers.Eq(__disc, "message"u8, caseInsensitive: false))
            return MessageEntryJsonPoly.Deserialize(ref reader);
        else if (TextHelpers.Eq(__disc, "compaction"u8, caseInsensitive: false))
            return CompactionEntryJsonPoly.Deserialize(ref reader);
        
        throw new FormatException($"Unknown type discriminator: {Encoding.UTF8.GetString(__disc)}");
    }
}
```

### Format Compatibility

All five formats use the same "object field" approach:

| Format | Discriminator mechanism |
|--------|------------------------|
| JSON | Object property `"$type":"msg"` |
| INI | Section key `$type=msg` |
| MsgPack | Map key `"$type" → "msg"` |
| TOML | Table key `$type = "msg"` |
| YAML | Mapping key `$type: msg` |

Each SG generates the same logical pattern (write discriminator field → switch on runtime type / dispatch on discriminator value) using its format-specific reader/writer. No format-specific attribute overrides needed.

### Runtime API

No changes. `JsonSerializer.Register<T>()` / `RegisterDeserializer<T>()` unchanged. Base class SG code auto-registers via `ModuleInitializer`.

### Edge Cases

| Case | Behavior |
|------|----------|
| Discriminator not first | `FormatException: "The discriminator property '...' must be the first"` |
| Unknown discriminator value | `FormatException: "Unknown type discriminator: '...'"` |
| Derived type with `[JsonConstructor]` | Poly helper uses ctor temp vars + `EmitDeserializeCtorParam` |
| Serialize concrete type directly | No discriminator written |
| Multi-level inheritance | Flat namespace on root; intermediate types have independent namespace |
| Cross-assembly derived types | Compile-time reachable via `typeof()` — same as STJ constraint |

### Files Changed

| File | Change |
|------|--------|
| `PicoSerDe.Core/src/PicoPolymorphicAttribute.cs` | **New** |
| `PicoSerDe.Core/src/PicoDerivedTypeAttribute.cs` | **New** |
| `shared/Gen/GenInfrastructure.cs` | Add `DerivedTypeInfo` record, `TypeInfo.DerivedTypes` field, `ExpandPolymorphicTypes` method |
| `PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs` | Add Pipeline E, merge, `GeneratePolyHelper`, modify `GenerateTypeCode`/`GenerateAll` |
| `PicoIni/src/PicoIni.Gen/IniSerializerGenerator.cs` | Add Pipeline E, merge, `GeneratePolyHelper`, modify `GenerateTypeCode`/`GenerateAll` |
| `PicoMsgPack/src/PicoMsgPack.Gen/...` | Same pattern |
| `PicoToml/src/PicoToml.Gen/...` | Same pattern |
| `PicoYaml/src/PicoYaml.Gen/...` | Same pattern |
| `PicoJetson/tests/PicoJetson.Tests/PolymorphicTests.cs` | **New** — test coverage |
| Other format tests | **New** — test coverage |
