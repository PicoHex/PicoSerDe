# Top-Level `List<T>` Serialization Support

## Status Overview

All 5 format generators support top-level `List<T>` serialization with **primitive** element types (`int`, `string`, `bool`, etc.).  
Support for **object** element types (`List<SomeDto>`) is partially complete.

| Generator | `List<int>` | `List<string>` | `List<SomeDto>` | Streaming |
|-----------|:---:|:---:|:---:|:---:|
| JSON      | ✅ | ✅ | ✅ | ✅ |
| MsgPack   | ✅ | ✅ | ❌ | — |
| INI       | ✅ | ✅ | ❌ | — |
| TOML      | ✅ | ✅ | ❌ | — |
| YAML      | ✅ | ✅ | ❌ | — |

## Architecture

### Shared Layer (`shared/Gen/GenInfrastructure.cs`)

```
IsGenericList(ITypeSymbol)     → bool        // detects System.Collections.Generic.List<T>
TransformTopLevelList(namedType) → TypeInfo   // creates TypeInfo with IsTopLevelList=true
```

All 5 generators call these in their `Transform`/`Tf` method, right after the `IArrayTypeSymbol` check (if present):

```csharp
// In each generator's Transform method:
if (typeArg is INamedTypeSymbol nts && GenInfrastructure.IsGenericList(nts))
    return GenInfrastructure.TransformTopLevelList(nts, Config, Attrs);
```

### TypeInfo fields used

| Field | Purpose |
|-------|---------|
| `IsTopLevelList` | Routes to collection code gen instead of object code gen |
| `ArrayElementKind` | Element type kind ("int32", "string", "object", etc.) |
| `ArrayElementName` | Element type fully qualified name |
| `ArrayElementNestedProps` | For object elements: the type's properties (populated but not always consumed) |

### Element Serialization Pattern

Each generator's `GenList` method creates a synthetic `PropertyInfo` and delegates to existing element-level helpers:

| Generator | Serialize Helper | Deserialize Helper |
|-----------|------------------|--------------------|
| JSON | `EmitArraySerializer` (shared array path) | `EmitArrayDeserializer` + `isList` branch |
| MsgPack | `WriteSerElem(p, "__item", …)` | `ReadDeserElem(p, "__list", ".Add", …)` |
| INI | `WriteValue(p, "__elem")` | `EmitReadValue(p)` via `__rv` |
| TOML | `EmitSerializeListElement(p, …)` | `EmitDeserializeListElementTemp(p, …)` |
| YAML | `EmitSerializeListElement(p, …)` | `EmitDeserializeListElementTemp(p, …)` + String token |

## Gaps: Object Element Support (`List<SomeDto>`)

To support `List<SomeDto>`, each generator needs:

1. **Inner helper generation** — a class that knows how to serialize/deserialize `SomeDto`
2. **Element helper support** — the `EmitSerializeListElement`/`WriteSerElem` equivalent must handle `"object"` element kind
3. **GenerateAll collection** — `GenerateAll` must extract `ArrayElementNestedProps` and trigger inner helper generation

### Per-Generator Gap Analysis

#### JSON ✅ Complete

- `GenerateAll` collects `ArrayElementNestedProps` → generates `SomeDtoJsonInner`
- `EmitArraySerializer`/`EmitArrayDeserializer` references `SomeDtoJsonInner.Serialize/Deserialize`
- Streaming support included

#### YAML ⚠️ Partial

- ✅ `GenerateAll` already has `CollectNestedTypes` → generates `SomeDtoYamlInner`
- ✅ `GenInner` (`GenerateInnerHelper`) exists — generates `Serialize/SerializeBlock/Deserialize`
- ❌ `GenerateAll` does NOT collect `ArrayElementNestedProps` from top-level list types  
  **Fix**: Add collection loop (same pattern as JSON line 366-379)
- ❌ `EmitSerializeListElement` has no `"object"` case — falls to `default` (`ToString()`)  
  **Fix**: Add `case "object"` that calls `SomeDtoYamlInner.SerializeBlock(yw, __item)`
- ❌ `EmitDeserializeListElementTemp` has no `"object"` case — falls to `default` (raw string)  
  **Fix**: Add `case "object"` that calls `SomeDtoYamlInner.Deserialize(ref r)`

#### TOML ⚠️ Partial

- ✅ `GenerateAll` already has `CollectNestedTypes` → generates `SomeDtoTomlInner`
- ✅ `GenInner` exists — generates `Serialize(TomlWriter, SomeDto)` and `Deserialize(ref TomlReader)`
- ❌ `GenerateAll` does NOT collect `ArrayElementNestedProps`  
  **Fix**: Add collection loop (collection code already added in this PR)
- ❌ Top-level object lists need **array-of-tables** format (`[[key]]`), not inline array  
  `EmitSerializeListElement` currently writes inline arrays (`tw.WriteArrayValue`),  
  but objects require `tw.WriteArrayTable("key")` + `sn.Serialize(tw, __item)`  
  **This is a larger refactoring of the list serialization path for object elements**

#### MsgPack ❌ Not Started

- ❌ No `GenInner` for object types (only `GenDictInner` for Dictionary)
- ❌ `GenerateAll` does not collect `ArrayElementNestedProps`
- ❌ `WriteSerElem` has no `"object"` case
- ❌ `ReadDeserElem` has no `"object"` case
- **Effort**: Medium — need `GenInner` + collection step + element helper object cases

#### INI ❌ Not Started

- ❌ No inner helper infrastructure at all — no `GenInner`, no `CollectNestedTypes`, no `InnerClassName` calls
- ❌ `WriteValue` falls to `default` for objects
- ❌ `EmitReadValue` falls to `else` for objects  
- **Effort**: Large — need full inner helper infrastructure from scratch

## Implementation Priority

1. **YAML** — smallest gap (collection step + 2 object cases in element helpers)
2. **TOML** — medium gap (collection step done, needs array-of-tables refactoring)
3. **MsgPack** — medium gap (GenInner + element helper object cases)
4. **INI** — largest gap (full inner helper infrastructure)

## Related Files

| File | Role |
|------|------|
| `shared/Gen/GenInfrastructure.cs` | `TypeInfo.IsTopLevelList`, `IsGenericList()`, `TransformTopLevelList()` |
| `shared/Gen/TypeKindResolver.cs` | `Resolve()`, `MapTypeName()` |
| `PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs` | Reference implementation — full support |
| `PicoMsgPack/src/PicoMsgPack.Gen/MsgPackSerializerGenerator.cs` | `GenList`, `WriteSerElem`, `ReadDeserElem` |
| `PicoIni/src/PicoIni.Gen/IniSerializerGenerator.cs` | `GenList`, `WriteValue`, `EmitReadValue` |
| `PicoToml/src/PicoToml.Gen/TomlSerializerGenerator.cs` | `GenList`, `GenInner`, `EmitSerializeListElement` |
| `PicoYaml/src/PicoYaml.Gen/YamlSerializerGenerator.cs` | `GenList`, `GenerateInnerHelper`, `EmitSerializeListElement` |
