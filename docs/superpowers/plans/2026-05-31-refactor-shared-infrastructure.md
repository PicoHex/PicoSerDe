# Refactor: Shared Infrastructure Extraction + Beta Format Alignment — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract duplicated source generator infrastructure from 5 SGs into `shared/Gen/GenInfrastructure.cs`, move `SimdHelpers.cs` into `PicoSerDe.Core`, then align mature patterns (SIMD, stackalloc, bitmask) from PicoJetson/PicoMsgPack to PicoIni/PicoToml/PicoYaml.

**Architecture:** Two phases. Phase 1 moves files and extracts shared SG methods (Transform, ExtractNestedProperties, CollectNestedTypes, attribute helpers) into a parameterized `GenInfrastructure` class with `PropertyInfo`/`TypeInfo` record structs. Phase 2 adds SIMD whitespace skip to IniReader, stackalloc number formatting to TomlWriter/YamlWriter, ulong bitmask comma tracking to TomlWriter/YamlWriter, and inline RawBuffer int[] scanning to MsgPack SG.

**Tech Stack:** C# 14, .NET 10 (runtime), netstandard2.0 (SGs), Roslyn 4.x, TUnit, Microsoft.Testing.Platform

---

## File Structure

```
BEFORE                                    AFTER
────────────────────────────────────────────────────────────
shared/
  SimdHelpers.cs                          → PicoSerDe.Core/src/SimdHelpers.cs
  TextHelpers.cs                          → PicoSerDe.Core/src/TextHelpers.cs (already there)
  TypeKindResolver.cs                     → shared/Gen/TypeKindResolver.cs
  IsExternalInit.cs                       → shared/Gen/IsExternalInit.cs
  BenchModels.cs                          → shared/BenchModels.cs (unchanged)
                                          + shared/Gen/GenInfrastructure.cs  (NEW)

PicoSerDe.Core/src/
  ISerializer.cs                          (unchanged)
  IDeserializer.cs                        (unchanged)
  TokenType.cs                            (unchanged)
  SerializerExtensions.cs                 (unchanged)
  DeserializerExtensions.cs               (unchanged)
  SimdHelpers.cs                          (NEW — moved from shared/)
  TextHelpers.cs                          (unchanged)

Each SG (*.Gen) project:
  FooSerializerGenerator.cs               (modified — uses GenInfrastructure)
  shared/Gen/TypeKindResolver.cs          (link updated)
  shared/Gen/IsExternalInit.cs            (link updated)
  shared/Gen/GenInfrastructure.cs         (NEW link)
```

---

## Phase 1: Extract Shared Infrastructure

### Task 1: Move SimdHelpers.cs to PicoSerDe.Core

**Files:**
- Move: `shared/SimdHelpers.cs` → `PicoSerDe.Core/src/SimdHelpers.cs`
- Modify: `PicoSerDe.Core/src/PicoSerDe.Core.csproj`
- Modify: `PicoIni/src/PicoIni/PicoIni.csproj`
- Modify: `PicoJetson/src/PicoJetson/PicoJetson.csproj`
- Modify: `PicoMsgPack/src/PicoMsgPack/PicoMsgPack.csproj`
- Modify: `PicoToml/src/PicoToml/PicoToml.csproj`
- Modify: `PicoYaml/src/PicoYaml/PicoYaml.csproj`

All steps must pass: `dotnet test --configuration Release --no-build`

- [ ] **Step 1: Move the file**

```bash
mv shared/SimdHelpers.cs PicoSerDe.Core/src/SimdHelpers.cs
```

- [ ] **Step 2: Add to PicoSerDe.Core.csproj — remove the link and make it a direct file**

The file `PicoSerDe.Core/src/PicoSerDe.Core.csproj` currently has:
```xml
<Compile Include="..\..\shared\SimdHelpers.cs" Link="SimdHelpers.cs" />
```
Remove that line. Since the file is now directly under `src/`, the SDK will pick it up automatically.

```xml
<!-- DELETE this line: -->
    <Compile Include="..\..\shared\SimdHelpers.cs" Link="SimdHelpers.cs" />
```

- [ ] **Step 3: Remove SimdHelpers link from all 5 runtime csproj files**

Each of these files has a line like:
```xml
<Compile Include="..\..\shared\SimdHelpers.cs" Link="SimdHelpers.cs" />
```
or (for PicoJetson):
```xml
<Compile Include="..\..\..\shared\SimdHelpers.cs" Link="SimdHelpers.cs" />
```

Delete that line from each:
- `PicoIni/src/PicoIni/PicoIni.csproj`
- `PicoJetson/src/PicoJetson/PicoJetson.csproj`
- `PicoMsgPack/src/PicoMsgPack/PicoMsgPack.csproj`
- `PicoToml/src/PicoToml/PicoToml.csproj`
- `PicoYaml/src/PicoYaml/PicoYaml.csproj`

> Check first with: `rg "SimdHelpers" --type csproj` to find exact lines.

- [ ] **Step 4: Verify move — check no project still links the old path**

```bash
rg "shared.*SimdHelpers" --type csproj -g '!**/bin/**' -g '!**/obj/**'
```
Expected: no output.

- [ ] **Step 5: Build and test**

```bash
dotnet build --configuration Release
dotnet test --configuration Release --no-build --verbosity normal
```
Expected: build succeeds, all 369 tests pass.

- [ ] **Step 6: Commit**

```bash
git add PicoSerDe.Core/src/SimdHelpers.cs PicoSerDe.Core/src/PicoSerDe.Core.csproj
git add PicoIni/src/PicoIni/PicoIni.csproj PicoJetson/src/PicoJetson/PicoJetson.csproj
git add PicoMsgPack/src/PicoMsgPack/PicoMsgPack.csproj PicoToml/src/PicoToml/PicoToml.csproj
git add PicoYaml/src/PicoYaml/PicoYaml.csproj
git rm shared/SimdHelpers.cs
git commit -m "refactor: move SimdHelpers.cs into PicoSerDe.Core"
```

---

### Task 2: Create shared/Gen/ directory and move TypeKindResolver + IsExternalInit

**Files:**
- Create: `shared/Gen/TypeKindResolver.cs` (moved from `shared/TypeKindResolver.cs`)
- Create: `shared/Gen/IsExternalInit.cs` (moved from `shared/IsExternalInit.cs`)
- Modify: 5 SG `.csproj` files — update link paths

- [ ] **Step 1: Create the directory and move files**

```bash
mkdir -p shared/Gen
mv shared/TypeKindResolver.cs shared/Gen/TypeKindResolver.cs
mv shared/IsExternalInit.cs shared/Gen/IsExternalInit.cs
```

- [ ] **Step 2: Update all 5 SG csproj files — change link paths**

In each of these files:
- `PicoJetson/src/PicoJetson.Gen/PicoJetson.Gen.csproj`
- `PicoIni/src/PicoIni.Gen/PicoIni.Gen.csproj`
- `PicoMsgPack/src/PicoMsgPack.Gen/PicoMsgPack.Gen.csproj`
- `PicoToml/src/PicoToml.Gen/PicoToml.Gen.csproj`
- `PicoYaml/src/PicoYaml.Gen/PicoYaml.Gen.csproj`

Change:
```xml
    <Compile Include="..\..\..\shared\IsExternalInit.cs" Link="IsExternalInit.cs" />
    <Compile Include="..\..\..\shared\TypeKindResolver.cs" Link="TypeKindResolver.cs" />
```
To:
```xml
    <Compile Include="..\..\..\shared\Gen\IsExternalInit.cs" Link="Gen\IsExternalInit.cs" />
    <Compile Include="..\..\..\shared\Gen\TypeKindResolver.cs" Link="Gen\TypeKindResolver.cs" />
```

- [ ] **Step 3: Build and test**

```bash
dotnet build --configuration Release
dotnet test --configuration Release --no-build --verbosity normal
```
Expected: all 369 tests pass.

- [ ] **Step 4: Commit**

```bash
git add shared/Gen/TypeKindResolver.cs shared/Gen/IsExternalInit.cs
git rm shared/TypeKindResolver.cs shared/IsExternalInit.cs
git add PicoJetson/src/PicoJetson.Gen/PicoJetson.Gen.csproj
git add PicoIni/src/PicoIni.Gen/PicoIni.Gen.csproj
git add PicoMsgPack/src/PicoMsgPack.Gen/PicoMsgPack.Gen.csproj
git add PicoToml/src/PicoToml.Gen/PicoToml.Gen.csproj
git add PicoYaml/src/PicoYaml.Gen/PicoYaml.Gen.csproj
git commit -m "refactor: move TypeKindResolver and IsExternalInit to shared/Gen/"
```

---

### Task 3: Create shared/Gen/GenInfrastructure.cs (shared record types)

**Files:**
- Create: `shared/Gen/GenInfrastructure.cs`
- This task creates ONLY the record types and `ShortName` helper. Shared methods come in later tasks.

- [ ] **Step 1: Write the file**

```csharp
// Shared source generator infrastructure for PicoSerDe
// Included via <Compile Include> in all 5 format SG projects.

using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PicoSerDe.Gen;

/// <summary>Format context passed from each SG to shared infrastructure.</summary>
public readonly record struct FormatConfig(
    string SerializerClassName,
    string Namespace,
    string FormatTag // "json" | "ini" | "msgpack" | "toml" | "yaml"
);

/// <summary>Shared type descriptor used by all 5 format SGs.</summary>
internal readonly record struct TypeInfo(
    string FullyQualifiedName,
    string Namespace,
    string Name,
    ImmutableArray<PropertyInfo> Properties
)
{
    public bool Equals(TypeInfo other) =>
        FullyQualifiedName == other.FullyQualifiedName
        && Namespace == other.Namespace
        && Name == other.Name
        && Properties.SequenceEqual(other.Properties);

    public override int GetHashCode()
    {
        var hash = FullyQualifiedName.GetHashCode();
        hash = (hash * 397) ^ Namespace.GetHashCode();
        hash = (hash * 397) ^ Name.GetHashCode();
        foreach (var p in Properties)
            hash = (hash * 397) ^ p.GetHashCode();
        return hash;
    }
}

/// <summary>Shared property descriptor used by all 5 format SGs.</summary>
internal readonly record struct PropertyInfo(
    string Name,
    string JsonName,       // Serialized name (may differ from C# name via attribute or camelCase)
    string TypeKind,        // "string" | "int32" | "float64" | "boolean" | "object" | "list" | "array" | "dict" | ...
    string TypeFullName,    // Fully qualified C# type name
    bool IsNullable,
    string? ElementTypeKind,
    string? ElementTypeName,
    string? KeyTypeKind,
    string? KeyTypeName,
    ImmutableArray<PropertyInfo> NestedProperties,
    string? ConverterTypeFullName,
    string? DateTimeFormat = null
);

/// <summary>Attribute detection helpers — each SG provides its own attribute class names.</summary>
public readonly record struct AttributeHelpers(
    Func<ITypeSymbol, bool> HasCamelCase,
    Func<IPropertySymbol, string?> GetCustomName,
    Func<IPropertySymbol, bool> HasIgnore,
    Func<IPropertySymbol, string?> GetConverterType,
    Func<IPropertySymbol, string?> GetDateTimeFormat
);

internal static class GenInfrastructure
{
    /// <summary>Checks if a SyntaxNode is a candidate serializer invocation.</summary>
    public static bool IsCandidate(SyntaxNode node)
    {
        if (node is not InvocationExpressionSyntax { Expression: var expr })
            return false;

        var name = expr switch
        {
            MemberAccessExpressionSyntax { Name: var n } => n,
            MemberBindingExpressionSyntax { Name: var n } => n,
            _ => null
        };

        var methodName = name switch
        {
            GenericNameSyntax gn => gn.Identifier.Text,
            SimpleNameSyntax sn => sn.Identifier.Text,
            _ => null
        };

        return methodName is "Serialize" or "SerializeToUtf8Bytes" or "Deserialize";
    }

    /// <summary>Shortens "global::Ns.SubNs.Type" → "Type".</summary>
    public static string ShortName(string fullName)
    {
        var name = fullName.Replace("global::", "");
        var lastDot = name.LastIndexOf('.');
        return lastDot >= 0 ? name.Substring(lastDot + 1) : name;
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

```bash
dotnet build --configuration Release
```
Expected: build succeeds (SG projects pick up the new file but don't call it yet).

- [ ] **Step 3: Commit**

```bash
git add shared/Gen/GenInfrastructure.cs
git commit -m "feat: add shared GenInfrastructure with TypeInfo/PropertyInfo/AttributeHelpers"
```

---

### Task 4: Add shared Transform logic to GenInfrastructure

**Files:**
- Modify: `shared/Gen/GenInfrastructure.cs` — add `TransformType` and `ExtractNestedProperties` methods

- [ ] **Step 1: Add `TransformType` method to GenInfrastructure**

Append to `shared/Gen/GenInfrastructure.cs` inside the `GenInfrastructure` class:

```csharp
    /// <summary>
    /// Shared transform logic. Each SG calls this with its own FormatConfig and AttributeHelpers.
    /// Returns null if the invocation is not a recognized serializer call for this format.
    /// </summary>
    public static TypeInfo? TransformType(
        GeneratorSyntaxContext ctx,
        FormatConfig config,
        AttributeHelpers attrs)
    {
        if (ctx.SemanticModel.GetSymbolInfo(ctx.Node).Symbol is not IMethodSymbol method)
            return null;

        if (method.ContainingType.Name != config.SerializerClassName
            || method.ContainingType.ContainingNamespace?.ToDisplayString() != config.Namespace)
            return null;

        if (method.TypeArguments.Length != 1)
            return null;

        var typeArg = method.TypeArguments[0];
        if (typeArg.SpecialType != SpecialType.None)
            return null;
        if (typeArg is not INamedTypeSymbol namedType)
            return null;
        if (namedType.ContainingType is not null)
            return null;

        var ns = namedType.ContainingNamespace?.ToDisplayString() ?? "";
        if (ns == "<global namespace>")
            ns = "";

        var useCamelCase = attrs.HasCamelCase(namedType);
        var properties = new List<PropertyInfo>();

        foreach (var member in namedType.GetMembers())
        {
            if (member is not IPropertySymbol prop)
                continue;
            if (prop.DeclaredAccessibility != Accessibility.Public)
                continue;
            if (prop.IsStatic || prop.IsIndexer)
                continue;
            if (prop.IsReadOnly && prop.SetMethod is null)
                continue;
            if (prop.GetMethod is null)
                continue;
            if (attrs.HasIgnore(prop))
                continue;

            var (typeKind, isNullable, innerTypeSymbol) = TypeKindResolver.Resolve(prop.Type, config.FormatTag);
            if (typeKind is null)
                continue;

            string? elementTypeKind = null;
            string? elementTypeName = null;
            string? keyTypeKind = null;
            string? keyTypeName = null;
            ImmutableArray<PropertyInfo> nestedProperties = ImmutableArray<PropertyInfo>.Empty;

            if (typeKind is "list" or "array")
            {
                ITypeSymbol? elementType;
                if (prop.Type is IArrayTypeSymbol arrType)
                    elementType = arrType.ElementType;
                else if (prop.Type is INamedTypeSymbol ntsElem && ntsElem.TypeArguments.Length == 1)
                    elementType = ntsElem.TypeArguments[0];
                else
                    continue;

                var (ek, _, _) = TypeKindResolver.Resolve(elementType, config.FormatTag);
                if (ek is null)
                    continue;
                elementTypeKind = ek;
                elementTypeName = TypeKindResolver.MapTypeName(ek, elementType);
                if (ek is "object" && elementType is INamedTypeSymbol eNtsObj)
                    nestedProperties = ExtractNestedProperties(eNtsObj, attrs, config.FormatTag);
            }
            else if (typeKind is "dict")
            {
                if (prop.Type is INamedTypeSymbol ntsDict && ntsDict.TypeArguments.Length == 2)
                {
                    var keyType = ntsDict.TypeArguments[0];
                    var valType = ntsDict.TypeArguments[1];
                    var (kk, _, _) = TypeKindResolver.Resolve(keyType, config.FormatTag);
                    var (vk, _, _) = TypeKindResolver.Resolve(valType, config.FormatTag);
                    if (kk is null || vk is null)
                        continue;
                    keyTypeKind = kk;
                    keyTypeName = TypeKindResolver.MapTypeName(kk, keyType);
                    elementTypeKind = vk;
                    elementTypeName = TypeKindResolver.MapTypeName(vk, valType);
                    if (vk is "object" && valType is INamedTypeSymbol vNtsObj)
                        nestedProperties = ExtractNestedProperties(vNtsObj, attrs, config.FormatTag);
                }
                else
                    continue;
            }
            else if (typeKind is "object" && prop.Type is INamedTypeSymbol objNts)
            {
                nestedProperties = ExtractNestedProperties(objNts, attrs, config.FormatTag);
            }

            properties.Add(new PropertyInfo(
                prop.Name,
                attrs.GetCustomName(prop) ?? (useCamelCase ? ToCamelCase(prop.Name) : prop.Name),
                typeKind,
                prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                isNullable,
                elementTypeKind,
                elementTypeName,
                keyTypeKind,
                keyTypeName,
                nestedProperties,
                attrs.GetConverterType(prop),
                attrs.GetDateTimeFormat(prop)
            ));
        }

        return new TypeInfo(
            namedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ns,
            namedType.Name,
            properties.ToImmutableArray()
        );
    }

    /// <summary>Extracts public properties from a nested object type.</summary>
    public static ImmutableArray<PropertyInfo> ExtractNestedProperties(
        INamedTypeSymbol type,
        AttributeHelpers attrs,
        string formatTag)
    {
        var list = new List<PropertyInfo>();
        foreach (var member in type.GetMembers())
        {
            if (member is not IPropertySymbol prop)
                continue;
            if (prop.DeclaredAccessibility != Accessibility.Public)
                continue;
            if (prop.IsStatic || prop.IsIndexer)
                continue;
            if (prop.IsReadOnly && prop.SetMethod is null)
                continue;
            if (prop.GetMethod is null)
                continue;
            if (attrs.HasIgnore(prop))
                continue;

            var (typeKind, isNullable, _) = TypeKindResolver.Resolve(prop.Type, formatTag);
            if (typeKind is null)
                continue;

            string? elementTypeKind = null;
            string? elementTypeName = null;
            string? keyTypeKind = null;
            string? keyTypeName = null;
            ImmutableArray<PropertyInfo> nestedProperties = ImmutableArray<PropertyInfo>.Empty;

            if (typeKind is "list" or "array")
            {
                ITypeSymbol? elementType;
                if (prop.Type is IArrayTypeSymbol arrType)
                    elementType = arrType.ElementType;
                else if (prop.Type is INamedTypeSymbol ntsElem && ntsElem.TypeArguments.Length == 1)
                    elementType = ntsElem.TypeArguments[0];
                else
                    continue;

                var (ek, _, _) = TypeKindResolver.Resolve(elementType, formatTag);
                if (ek is null)
                    continue;
                elementTypeKind = ek;
                elementTypeName = TypeKindResolver.MapTypeName(ek, elementType);
                if (ek is "object" && elementType is INamedTypeSymbol eNtsObj)
                    nestedProperties = ExtractNestedProperties(eNtsObj, attrs, formatTag);
            }
            else if (typeKind is "dict")
            {
                if (prop.Type is INamedTypeSymbol ntsDict && ntsDict.TypeArguments.Length == 2)
                {
                    var keyType = ntsDict.TypeArguments[0];
                    var valType = ntsDict.TypeArguments[1];
                    var (kk, _, _) = TypeKindResolver.Resolve(keyType, formatTag);
                    var (vk, _, _) = TypeKindResolver.Resolve(valType, formatTag);
                    if (kk is null || vk is null)
                        continue;
                    keyTypeKind = kk;
                    keyTypeName = TypeKindResolver.MapTypeName(kk, keyType);
                    elementTypeKind = vk;
                    elementTypeName = TypeKindResolver.MapTypeName(vk, valType);
                    if (vk is "object" && valType is INamedTypeSymbol vNtsObj)
                        nestedProperties = ExtractNestedProperties(vNtsObj, attrs, formatTag);
                }
                else
                    continue;
            }
            else if (typeKind is "object" && prop.Type is INamedTypeSymbol objNts)
            {
                nestedProperties = ExtractNestedProperties(objNts, attrs, formatTag);
            }

            list.Add(new PropertyInfo(
                prop.Name,
                attrs.GetCustomName(prop) ?? prop.Name,
                typeKind,
                prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                isNullable,
                elementTypeKind,
                elementTypeName,
                keyTypeKind,
                keyTypeName,
                nestedProperties,
                attrs.GetConverterType(prop),
                attrs.GetDateTimeFormat(prop)
            ));
        }
        return list.ToImmutableArray();
    }

    /// <summary>Collects all unique nested object types (M×N dedup).</summary>
    public static void CollectNestedTypes(
        TypeInfo type,
        Dictionary<string, ImmutableArray<PropertyInfo>> nestedTypes)
    {
        foreach (var prop in type.Properties)
        {
            if (prop.TypeKind == "object" && !string.IsNullOrEmpty(prop.TypeFullName))
                AddNestedType(prop.TypeFullName, prop.NestedProperties, nestedTypes);
            if ((prop.TypeKind == "list" || prop.TypeKind == "array")
                && prop.ElementTypeKind == "object"
                && !string.IsNullOrEmpty(prop.ElementTypeName))
                AddNestedType(prop.ElementTypeName!, prop.NestedProperties, nestedTypes);
            if (prop.TypeKind == "dict"
                && prop.ElementTypeKind == "object"
                && !string.IsNullOrEmpty(prop.ElementTypeName))
                AddNestedType(prop.ElementTypeName!, prop.NestedProperties, nestedTypes);
        }
    }

    private static void AddNestedType(
        string fullName,
        ImmutableArray<PropertyInfo> props,
        Dictionary<string, ImmutableArray<PropertyInfo>> nestedTypes)
    {
        if (nestedTypes.ContainsKey(fullName))
            return;
        nestedTypes[fullName] = props;

        foreach (var np in props)
        {
            if (np.TypeKind == "object" && !string.IsNullOrEmpty(np.TypeFullName))
                AddNestedType(np.TypeFullName, np.NestedProperties, nestedTypes);
            if ((np.TypeKind == "list" || np.TypeKind == "array")
                && np.ElementTypeKind == "object"
                && !string.IsNullOrEmpty(np.ElementTypeName))
                AddNestedType(np.ElementTypeName!, np.NestedProperties, nestedTypes);
            if (np.TypeKind == "dict"
                && np.ElementTypeKind == "object"
                && !string.IsNullOrEmpty(np.ElementTypeName))
                AddNestedType(np.ElementTypeName!, np.NestedProperties, nestedTypes);
        }
    }

    public static string ToCamelCase(string name)
    {
        if (name.Length == 0)
            return name;
        int upperCount = 0;
        while (upperCount < name.Length && char.IsUpper(name[upperCount]))
            upperCount++;
        if (upperCount <= 1)
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        int keepUpper = upperCount > 1 && upperCount < name.Length ? upperCount - 1 : upperCount;
        return name.Substring(0, keepUpper).ToLowerInvariant() + name.Substring(keepUpper);
    }
```

- [ ] **Step 2: Build and test**

```bash
dotnet build --configuration Release
dotnet test --configuration Release --no-build --verbosity normal
```
Expected: all 369 tests pass (new code is not yet called by any SG — no semantic change).

- [ ] **Step 3: Commit**

```bash
git add shared/Gen/GenInfrastructure.cs
git commit -m "feat: add TransformType, ExtractNestedProperties, CollectNestedTypes to GenInfrastructure"
```

---

### Task 5: Add GenInfrastructure link to all 5 SG csproj files

**Files:**
- Modify: 5 SG `.csproj` files

- [ ] **Step 1: Add the link line to each SG csproj**

In each of these files add after the existing `<Compile Include` lines:
```xml
    <Compile Include="..\..\..\shared\Gen\GenInfrastructure.cs" Link="Gen\GenInfrastructure.cs" />
```

Files:
- `PicoJetson/src/PicoJetson.Gen/PicoJetson.Gen.csproj`
- `PicoIni/src/PicoIni.Gen/PicoIni.Gen.csproj`
- `PicoMsgPack/src/PicoMsgPack.Gen/PicoMsgPack.Gen.csproj`
- `PicoToml/src/PicoToml.Gen/PicoToml.Gen.csproj`
- `PicoYaml/src/PicoYaml.Gen/PicoYaml.Gen.csproj`

- [ ] **Step 2: Build and test**

```bash
dotnet build --configuration Release
dotnet test --configuration Release --no-build --verbosity normal
```
Expected: all 369 tests pass.

- [ ] **Step 3: Commit**

```bash
git add PicoJetson/src/PicoJetson.Gen/PicoJetson.Gen.csproj
git add PicoIni/src/PicoIni.Gen/PicoIni.Gen.csproj
git add PicoMsgPack/src/PicoMsgPack.Gen/PicoMsgPack.Gen.csproj
git add PicoToml/src/PicoToml.Gen/PicoToml.Gen.csproj
git add PicoYaml/src/PicoYaml.Gen/PicoYaml.Gen.csproj
git commit -m "feat: add GenInfrastructure link to all 5 SG projects"
```

---

### Task 6: Refactor PicoJetson SG to use GenInfrastructure (first SG — establishes pattern)

**Files:**
- Modify: `PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs`

This is the most complex SG refactoring. We replace `TypeInfo`, `PropertyInfo`, `IsCandidate`, `Transform`, `ExtractNestedProperties`, `CollectNestedTypes`, `AddNestedType`, `ShortName`, `HasJsonIgnore`, `GetJsonPropertyName`, `GetJsonConverterType`, `GetDateTimeFormat`, `HasJsonCamelCase`, `ToCamelCase` with calls to `GenInfrastructure`.

- [ ] **Step 1: Delete private TypeInfo and PropertyInfo record structs**

Delete the entire `TypeInfo` and `PropertyInfo` record definitions from the bottom of `JsonSerializerGenerator.cs` (approximately lines 1736-1773). These are now provided by `GenInfrastructure`.

- [ ] **Step 2: Replace IsCandidate with GenInfrastructure call**

Replace:
```csharp
    private static bool IsCandidate(SyntaxNode node)
    {
        if (node is not InvocationExpressionSyntax { Expression: var expr })
            return false;
        SimpleNameSyntax? name = expr switch { ... };
        ...
        return methodName is "Serialize" or "SerializeToUtf8Bytes" or "Deserialize";
    }
```
With:
```csharp
    private static bool IsCandidate(SyntaxNode node) => GenInfrastructure.IsCandidate(node);
```

- [ ] **Step 3: Create FormatConfig and AttributeHelpers fields**

Add at the top of the class:
```csharp
    private static readonly FormatConfig Config = new("JsonSerializer", "PicoJetson", "json");

    private static readonly AttributeHelpers Attrs = new(
        HasJsonCamelCase,
        GetJsonPropertyName,
        HasJsonIgnore,
        GetJsonConverterType,
        GetDateTimeFormat
    );
```

- [ ] **Step 4: Replace Transform method**

Replace the entire `Transform` method body with a call to `GenInfrastructure.TransformType`:

```csharp
    private static TypeInfo? Transform(GeneratorSyntaxContext ctx) =>
        GenInfrastructure.TransformType(ctx, Config, Attrs);
```

- [ ] **Step 5: Delete ExtractNestedProperties, CollectNestedTypes, AddNestedType**

Delete these three methods entirely. The SG now uses `GenInfrastructure.ExtractNestedProperties` and `GenInfrastructure.CollectNestedTypes` (called from within `TransformType`).

- [ ] **Step 6: Delete HasJsonCamelCase, ToCamelCase**

Delete `HasJsonCamelCase` and `ToCamelCase` (these are now in `GenInfrastructure`). But KEEP the `HasJsonCamelCase` method as a static local that wraps the Roslyn attribute check — we need it for the `AttributeHelpers` instance. Actually, keep it as a private method but make it match the delegate signature.

Wait — `HasJsonCamelCase` currently takes `INamedTypeSymbol` — this matches `Func<ITypeSymbol, bool>`. It stays.

- [ ] **Step 7: Replace ShortName calls with GenInfrastructure.ShortName**

Find all calls to `ShortName(...)` and replace with `GenInfrastructure.ShortName(...)`.

- [ ] **Step 8: Update GenerateAll to use GenInfrastructure.CollectNestedTypes**

Replace the inline nested type collection logic in `GenerateAll`:
```csharp
    var nestedTypes = new Dictionary<string, ImmutableArray<PropertyInfo>>();
    foreach (var type in types)
        GenInfrastructure.CollectNestedTypes(type, nestedTypes);
```

- [ ] **Step 9: Delete old ShortName method**

Delete `private static string ShortName(string fullName)` from the class.

- [ ] **Step 10: Build and test**

```bash
dotnet build --configuration Release
dotnet test --configuration Release --no-build --verbosity normal
```
Expected: all 369 tests pass. SG output must be semantically equivalent.

- [ ] **Step 11: Commit**

```bash
git add PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs
git commit -m "refactor: JsonSerializerGenerator uses GenInfrastructure for TypeInfo, Transform, ExtractNestedProperties"
```

---

### Task 7: Refactor PicoIni SG to use GenInfrastructure

**Files:**
- Modify: `PicoIni/src/PicoIni.Gen/IniSerializerGenerator.cs`

Same pattern as Task 6 but for Ini format. Key differences:
- `FormatConfig`: `("IniSerializer", "PicoIni", "ini")`
- Attribute class names: `IniIgnoreAttribute` → `IniKeyAttribute` → `IniCamelCaseAttribute` → `IniConverterAttribute` → `IniDateTimeFormatAttribute`
- INI has additional `IniSectionAttribute` and `IniCommentAttribute` — these stay format-specific

- [ ] **Step 1: Delete TypeInfo/PropertyInfo record structs** (if present)
- [ ] **Step 2: Add Config + Attrs fields, replace IsCandidate, Transform, ExtractNestedProperties**
- [ ] **Step 3: Replace ShortName → GenInfrastructure.ShortName**
- [ ] **Step 4: Delete duplicated methods (ExtractNestedProperties, CollectNestedTypes, ToCamelCase, etc.)**
- [ ] **Step 5: Build and test**

```bash
dotnet build --configuration Release
dotnet test --configuration Release --no-build --verbosity normal
```
Expected: all 369 tests pass.

- [ ] **Step 6: Commit**

```bash
git add PicoIni/src/PicoIni.Gen/IniSerializerGenerator.cs
git commit -m "refactor: IniSerializerGenerator uses GenInfrastructure"
```

---

### Task 8: Refactor PicoToml SG to use GenInfrastructure

**Files:**
- Modify: `PicoToml/src/PicoToml.Gen/TomlSerializerGenerator.cs`

Same pattern. `FormatConfig`: `("TomlSerializer", "PicoToml", "toml")`.

- [ ] **Step 1-4: Same refactoring steps as Task 7**
- [ ] **Step 5: Build and test — all 369 pass**
- [ ] **Step 6: Commit**

---

### Task 9: Refactor PicoYaml SG to use GenInfrastructure

**Files:**
- Modify: `PicoYaml/src/PicoYaml.Gen/YamlSerializerGenerator.cs`

Same pattern. `FormatConfig`: `("YamlSerializer", "PicoYaml", "yaml")`.

- [ ] **Step 1-4: Same refactoring steps**
- [ ] **Step 5: Build and test — all 369 pass**
- [ ] **Step 6: Commit**

---

### Task 10: Refactor PicoMsgPack SG to use GenInfrastructure

**Files:**
- Modify: `PicoMsgPack/src/PicoMsgPack.Gen/MsgPackSerializerGenerator.cs`

Same pattern. `FormatConfig`: `("MsgPackSerializer", "PicoMsgPack", "msgpack")`.

- [ ] **Step 1-4: Same refactoring steps**
- [ ] **Step 5: Build and test — all 369 pass**
- [ ] **Step 6: Commit**

---

### Task 11: Phase 1 final verification

- [ ] **Step 1: Verify no duplicated methods remain across SGs**

```bash
rg "private static ImmutableArray<PropertyInfo> ExtractNestedProperties" --type cs -g '!**/shared/Gen/**' -g '!**/bin/**' -g '!**/obj/**'
rg "private static void CollectNestedTypes" --type cs -g '!**/shared/Gen/**' -g '!**/bin/**' -g '!**/obj/**'
rg "private static string ShortName" --type cs -g '!**/shared/Gen/**' -g '!**/bin/**' -g '!**/obj/**'
```

Expected: no output for any of these (except possibly method variants inside GenInfrastructure itself).

- [ ] **Step 2: Full CI build**

```bash
dotnet build --configuration Release
dotnet test --configuration Release --no-build --verbosity normal
```

Expected: all 369 tests pass. Build succeeds.

- [ ] **Step 3: Commit**

```bash
git commit -m "chore: Phase 1 complete — all SGs use shared GenInfrastructure"
```

---

## Phase 2: Align Mature Patterns

### Task 12: P2-1 — Add SIMD whitespace skip to IniReader

**Files:**
- Modify: `PicoIni/src/PicoIni/IniReader.cs`
- New test: `PicoIni/tests/PicoIni.Tests/IniReaderSimdTests.cs`

- [ ] **Step 1: Write the failing test (SIMD whitespace equivalence)**

Create `PicoIni/tests/PicoIni.Tests/IniReaderSimdTests.cs`:

```csharp
namespace PicoIni.Tests;

public class IniReaderSimdTests
{
    [Test]
    public async Task WhitespaceHeavy_Ini_WithSimd_ParsesCorrectly()
    {
        // INI with lots of whitespace between tokens — exercises SIMD path
        var ini = """


            key1 = value1


            key2 = value2


            """;
        var bytes = Encoding.UTF8.GetBytes(ini);
        var reader = new IniReader(bytes);

        var keys = new List<string>();
        var values = new List<string>();
        while (reader.Read())
        {
            if (reader.TokenType == TokenType.PropertyName)
            {
                keys.Add(Encoding.UTF8.GetString(reader.GetStringRaw()));
                reader.ReadValue();
                values.Add(Encoding.UTF8.GetString(reader.GetStringRaw()));
            }
        }

        await Assert.That(keys).HasCount(2);
        await Assert.That(keys[0]).IsEqualTo("key1");
        await Assert.That(values[0]).IsEqualTo("value1");
        await Assert.That(keys[1]).IsEqualTo("key2");
        await Assert.That(values[1]).IsEqualTo("value2");
    }

    [Test]
    public async Task WhitespaceHeavy_Ini_WithSections_SimdParsing()
    {
        var ini = """

            [section1]

            a = 1

            b = 2

            [section2]

            x = hello

            """;
        var bytes = Encoding.UTF8.GetBytes(ini);
        var reader = new IniReader(bytes);

        var sections = new List<string>();
        while (reader.Read())
        {
            if (reader.TokenType == TokenType.ObjectStart)
                sections.Add(Encoding.UTF8.GetString(reader.GetStringRaw()));
        }

        await Assert.That(sections).HasCount(2);
        await Assert.That(sections[0]).IsEqualTo("section1");
        await Assert.That(sections[1]).IsEqualTo("section2");
    }
}
```

- [ ] **Step 2: Run test — verify it passes (IniReader already handles whitespace, just not via SIMD)**

```bash
dotnet test --configuration Release --filter "FullyQualifiedName~IniReaderSimdTests" --no-build
```
Expected: tests pass (the existing scalar whitespace skip handles these correctly).

- [ ] **Step 3: Add SIMD whitespace skip to IniReader.ReadSpan()**

In `IniReader.cs`, find the `ReadSpan()` method. Replace the scalar whitespace loop:

```csharp
// BEFORE (scalar loop):
    while (_position < _data.Length)
    {
        var b = _data[_position];
        if (b is (byte)'\n' or (byte)'\r')
        {
            ...
        }
        if (b is (byte)' ' or (byte)'\t')
        {
            _position++;
            continue;
        }
        ...
    }

// AFTER (add SIMD skip at the whitespace check point):
    while (_position < _data.Length)
    {
        var b = _data[_position];
        if (b is (byte)'\n' or (byte)'\r')
        {
            ...
        }
        if (b is (byte)' ' or (byte)'\t')
        {
            _position = PicoSerDe.Core.SimdHelpers.SkipWhitespace(_data, _position);
            continue;
        }
        ...
    }
```

> Note: `IniReader` must have `using PicoSerDe.Core;`. Since `SimdHelpers` is now in `PicoSerDe.Core`, and `PicoIni.csproj` already references `PicoSerDe.Core`, this works.

- [ ] **Step 4: Run all Ini tests**

```bash
dotnet test --configuration Release --filter "FullyQualifiedName~PicoIni" --no-build
```
Expected: all Ini tests pass.

- [ ] **Step 5: Commit**

```bash
git add PicoIni/src/PicoIni/IniReader.cs PicoIni/tests/PicoIni.Tests/IniReaderSimdTests.cs
git commit -m "feat: add SIMD whitespace skip to IniReader"
```

---

### Task 13: P2-2a — Add stackalloc number formatting to TomlWriter

**Files:**
- Modify: `PicoToml/src/PicoToml/TomlWriter.cs`

- [ ] **Step 1: Read the current TomlWriter.cs to find number write methods**

```bash
rg -n "void Write\|void WriteNumber\|void WriteInt\|void WriteFloat\|void WriteBool" PicoToml/src/PicoToml/TomlWriter.cs
```

- [ ] **Step 2: Add stackalloc to int/long/double/bool writes**

For each method that writes a number, apply the pattern used by JsonWriter:

```csharp
// BEFORE (example — constructing string then encoding):
public void WriteInt(int value)
{
    // ... writes value.ToString() via Encoding.UTF8 ...
}

// AFTER:
public void WriteInt(int value)
{
    BeforeWriteValue();
    Span<byte> buf = stackalloc byte[16];
    value.TryFormat(buf, out var w);
    _buffer.Write(buf[..w]);
    _bytesWritten += w;
}
```

Apply to: int, long, double, bool (as "true"/"false" UTF-8), float.

- [ ] **Step 3: Run all Toml tests**

```bash
dotnet test --configuration Release --filter "FullyQualifiedName~PicoToml" --no-build
```
Expected: all Toml tests pass.

- [ ] **Step 4: Commit**

```bash
git add PicoToml/src/PicoToml/TomlWriter.cs
git commit -m "perf: add stackalloc number formatting to TomlWriter"
```

---

### Task 14: P2-2b — Add stackalloc number formatting to YamlWriter

**Files:**
- Modify: `PicoYaml/src/PicoYaml/YamlWriter.cs`

Same pattern as Task 13, applied to YamlWriter's number write methods.

- [ ] **Step 1: Apply stackalloc pattern to number writes**
- [ ] **Step 2: Run all Yaml tests**

```bash
dotnet test --configuration Release --filter "FullyQualifiedName~PicoYaml" --no-build
```
Expected: all Yaml tests pass.

- [ ] **Step 3: Commit**

---

### Task 15: P2-3a — Add ulong bitmask comma tracking to TomlWriter

**Files:**
- Modify: `PicoToml/src/PicoToml/TomlWriter.cs`

- [ ] **Step 1: Read TomlWriter to understand current comma/depth tracking**

```bash
rg -n "needsComma\|_depth\|comma\|BeforeValue" PicoToml/src/PicoToml/TomlWriter.cs
```

- [ ] **Step 2: Replace current tracking with ulong bitmask pattern**

Follow the JsonWriter pattern:
```csharp
private ulong _needsComma;  // bitmask — bit N set = comma needed at depth N

private void BeforeWriteValue()
{
    if ((_needsComma & (1UL << _depth)) != 0)
        WriteByte((byte)',');
    _needsComma |= (1UL << _depth);
}

public void WriteEndArray()  // or WriteEndTable / WriteEndInlineTable
{
    _needsComma &= ~(1UL << _depth);
    _depth--;
    ...
}
```

- [ ] **Step 3: Run all Toml tests**

```bash
dotnet test --configuration Release --filter "FullyQualifiedName~PicoToml" --no-build
```
Expected: all Toml tests pass.

- [ ] **Step 4: Commit**

---

### Task 16: P2-3b — Add ulong bitmask comma tracking to YamlWriter

**Files:**
- Modify: `PicoYaml/src/PicoYaml/YamlWriter.cs`

Same pattern as Task 15.

- [ ] **Step 1: Apply bitmask pattern**
- [ ] **Step 2: Run all Yaml tests**
- [ ] **Step 3: Commit**

---

### Task 17: P2-4 — Add inline RawBuffer int[] scanning to MsgPack SG

**Files:**
- Modify: `PicoMsgPack/src/PicoMsgPack.Gen/MsgPackSerializerGenerator.cs`
- New test: `PicoMsgPack/tests/PicoMsgPack.Tests/MsgPackIntArrayInlineTests.cs`

- [ ] **Step 1: Write the failing test (int[] round-trip)**

Create `PicoMsgPack/tests/PicoMsgPack.Tests/MsgPackIntArrayInlineTests.cs`:

```csharp
namespace PicoMsgPack.Tests;

public class IntArrayModel
{
    public int[] Scores { get; set; } = [];
}

public class MsgPackIntArrayInlineTests
{
    [Test]
    public async Task IntArray_RoundTrip_Correctness()
    {
        var model = new IntArrayModel { Scores = [1, -5, int.MaxValue, int.MinValue, 0, 42] };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(model);
        var result = MsgPackSerializer.Deserialize<IntArrayModel>(bytes);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Scores).IsNotNull();
        await Assert.That(result.Scores).HasCount(6);
        await Assert.That(result.Scores[0]).IsEqualTo(1);
        await Assert.That(result.Scores[1]).IsEqualTo(-5);
        await Assert.That(result.Scores[2]).IsEqualTo(int.MaxValue);
        await Assert.That(result.Scores[3]).IsEqualTo(int.MinValue);
        await Assert.That(result.Scores[4]).IsEqualTo(0);
        await Assert.That(result.Scores[5]).IsEqualTo(42);
    }

    [Test]
    public async Task EmptyIntArray_RoundTrip()
    {
        var model = new IntArrayModel { Scores = [] };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(model);
        var result = MsgPackSerializer.Deserialize<IntArrayModel>(bytes);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Scores).IsNotNull();
        await Assert.That(result.Scores).HasCount(0);
    }

    [Test]
    public async Task LargeIntArray_RoundTrip()
    {
        var data = new int[1000];
        for (int i = 0; i < data.Length; i++) data[i] = i * 7 - 3500;
        var model = new IntArrayModel { Scores = data };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(model);
        var result = MsgPackSerializer.Deserialize<IntArrayModel>(bytes);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Scores).IsNotNull();
        await Assert.That(result.Scores).HasCount(1000);
        for (int i = 0; i < 1000; i++)
            await Assert.That(result.Scores[i]).IsEqualTo(data[i]);
    }
}
```

- [ ] **Step 2: Run test — verify it passes (existing MsgPack SG already handles int[] via reader loop)**

```bash
dotnet test --configuration Release --filter "FullyQualifiedName~MsgPackIntArrayInlineTests"
```
Expected: tests pass (existing reader-loop-based deserialization works).

- [ ] **Step 3: Modify MsgPack SG to emit inline RawBuffer scanning for int[] arrays**

In `MsgPackSerializerGenerator.cs`, find the array deserialization emit path (similar to JsonSerializerGenerator's `EmitDeserializeValue` for "list"/"array" with `ElementTypeKind == "int32"`).

Apply the same optimization pattern used in PicoJetson SG: when element type is `int32` and the reader supports `RawBuffer`/`SetRawPos`, emit inline code that directly scans the binary buffer.

For MsgPack specifically, the format is:
```
[array header byte] [value1] [value2] ... [valueN]
```
MsgPack int values are encoded as fixint (0x00-0x7F positive, 0xE0-0xFF negative), int8, int16, int32, int64. The inline scanner must handle these formats.

Since this is a significant optimization for the MsgPack binary format, the inline scanner should:
1. Read the array header to get element count
2. For each element, decode the MsgPack integer format directly from the buffer
3. Call `SetRawPos` to advance the reader

> **Note**: If the full MsgPack integer decoding inline scan is too complex for this task, scope it to fixint + int8 only (covers values -32..127), and fall back to the reader loop for other formats.

- [ ] **Step 4: Build and run tests**

```bash
dotnet build --configuration Release
dotnet test --configuration Release --filter "FullyQualifiedName~MsgPackIntArrayInlineTests" --no-build
```
Expected: all tests pass with the optimized code.

- [ ] **Step 5: Run full test suite**

```bash
dotnet test --configuration Release --no-build
```
Expected: all 369 tests pass.

- [ ] **Step 6: Commit**

```bash
git add PicoMsgPack/src/PicoMsgPack.Gen/MsgPackSerializerGenerator.cs
git add PicoMsgPack/tests/PicoMsgPack.Tests/MsgPackIntArrayInlineTests.cs
git commit -m "perf: add inline RawBuffer int[] scanning to MsgPackSerializerGenerator"
```

---

### Task 18: Final verification — Phase 2 complete

- [ ] **Step 1: Full build and test**

```bash
dotnet build --configuration Release
dotnet test --configuration Release --no-build --verbosity normal
```
Expected: all tests pass. Count should be ≥ 369 (new tests added).

- [ ] **Step 2: Verify AOT compatibility**

```bash
dotnet publish PicoJetson/samples/PicoJetson.Sample/PicoJetson.Sample.csproj -c Release -r win-x64 --self-contained
dotnet publish PicoIni/samples/PicoIni.Sample/PicoIni.Sample.csproj -c Release -r win-x64 --self-contained
dotnet publish PicoToml/samples/PicoToml.Sample/PicoToml.Sample.csproj -c Release -r win-x64 --self-contained
dotnet publish PicoYaml/samples/PicoYaml.Sample/PicoYaml.Sample.csproj -c Release -r win-x64 --self-contained
dotnet publish PicoMsgPack/samples/PicoMsgPack.Sample/PicoMsgPack.Sample.csproj -c Release -r win-x64 --self-contained
```
Expected: all 5 AOT publishes succeed.

- [ ] **Step 3: Commit**

```bash
git commit -m "chore: Phase 2 complete — mature patterns aligned across all formats"
```
