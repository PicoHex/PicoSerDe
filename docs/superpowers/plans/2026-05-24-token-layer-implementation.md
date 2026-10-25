# PicoSerDe.Abs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement PicoSerDe.Abs — `TokenType` enum, `ISerializer<T>` / `IDeserializer<T>` core contracts, and convenience extensions — as the shared vocabulary and API surface for the PicoHex serialization ecosystem.

**Architecture:** Four source files. `TokenType` is the 22-member format-agnostic vocabulary. Two simple generic interfaces define the core contracts (no Reader/Writer leakage — serialize uses `IBufferWriter<byte>`, deserialize uses `ReadOnlySpan<byte>`). `SerializerExtensions` / `DeserializerExtensions` provide friendly wrappers (`ToBytes`, `ToString`, `FromStream`, `FromPipe`) and streaming bridges.

**Tech Stack:** .NET 10, C# latest, TUnit (testing)

---

## File Structure

```
PicoSerDe.Abs/
  src/
    PicoSerDe.Abs.csproj
    TokenType.cs                    ← 22-member enum
    ISerializer.cs                  ← ISerializer<T>
    IDeserializer.cs                ← IDeserializer<T>
    SerializerExtensions.cs         ← ToBytes/ToStream/ToString + streaming
    DeserializerExtensions.cs       ← FromBytes/FromStream/FromPipe + streaming
  tests/
    PicoSerDe.Abs.Tests/
      PicoSerDe.Abs.Tests.csproj
      TokenTypeTests.cs
      InterfaceTests.cs
      ExtensionsTests.cs
  benchmarks/
  samples/

PicoSerDe.slnx
```

---

### Task 1: Project Scaffold

**Files:**
- Create: `PicoSerDe.Abs/src/PicoSerDe.Abs.csproj`
- Create: `PicoSerDe.Abs/tests/PicoSerDe.Abs.Tests/PicoSerDe.Abs.Tests.csproj`
- Modify: `PicoSerDe.slnx`
- Delete: `src/README.md`, `tests/README.md`, `samples/README.md`

- [ ] **Step 1: Delete root-level template placeholders**

```bash
Remove-Item -LiteralPath "src\README.md" -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath "tests\README.md" -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath "samples\README.md" -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath "src" -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath "tests" -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath "samples" -Force -ErrorAction SilentlyContinue
```

- [ ] **Step 2: Create library project**

```xml
<!-- PicoSerDe.Abs/src/PicoSerDe.Abs.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="PicoSerDe.Abs.Tests" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Create test project**

```xml
<!-- PicoSerDe.Abs/tests/PicoSerDe.Abs.Tests/PicoSerDe.Abs.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <OutputType>Exe</OutputType>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="TUnit" Version="0.*" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\PicoSerDe.Abs.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 4: Update solution file**

```xml
<!-- PicoSerDe.slnx -->
<Solution>
  <Project Path="PicoSerDe.Abs\src\PicoSerDe.Abs.csproj" />
  <Project Path="PicoSerDe.Abs\tests\PicoSerDe.Abs.Tests\PicoSerDe.Abs.Tests.csproj" />
</Solution>
```

- [ ] **Step 5: Build and verify**

Run: `dotnet restore && dotnet build`
Expected: Build succeeds, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add PicoSerDe.Abs/src/PicoSerDe.Abs.csproj PicoSerDe.Abs/tests/PicoSerDe.Abs.Tests/PicoSerDe.Abs.Tests.csproj PicoSerDe.slnx
git add src/ tests/ samples/
git commit -m "feat: scaffold PicoSerDe.Abs module (net10.0, C# latest)"
```

---

### Task 2: TokenType Enum

**Files:**
- Create: `PicoSerDe.Abs/src/TokenType.cs`
- Create: `PicoSerDe.Abs/tests/PicoSerDe.Abs.Tests/TokenTypeTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// PicoSerDe.Abs/tests/PicoSerDe.Abs.Tests/TokenTypeTests.cs
namespace PicoSerDe.Abs.Tests;

public class TokenTypeTests
{
    [Test]
    public async Task None_HasValueZero()
    {
        await Assert.That((int)TokenType.None).IsEqualTo(0);
    }

    [Test]
    public async Task EnumContainsAllStructuralTokens()
    {
        var values = Enum.GetValues<TokenType>();
        await Assert.That(values).Contains(TokenType.ObjectStart);
        await Assert.That(values).Contains(TokenType.ObjectEnd);
        await Assert.That(values).Contains(TokenType.ArrayStart);
        await Assert.That(values).Contains(TokenType.ArrayEnd);
        await Assert.That(values).Contains(TokenType.PropertyName);
    }

    [Test]
    public async Task EnumContainsAllScalarTokens()
    {
        var values = Enum.GetValues<TokenType>();
        await Assert.That(values).Contains(TokenType.Null);
        await Assert.That(values).Contains(TokenType.Bool);
        await Assert.That(values).Contains(TokenType.String);
        await Assert.That(values).Contains(TokenType.Bytes);

        await Assert.That(values).Contains(TokenType.Int8);
        await Assert.That(values).Contains(TokenType.Int16);
        await Assert.That(values).Contains(TokenType.Int32);
        await Assert.That(values).Contains(TokenType.Int64);
        await Assert.That(values).Contains(TokenType.UInt8);
        await Assert.That(values).Contains(TokenType.UInt16);
        await Assert.That(values).Contains(TokenType.UInt32);
        await Assert.That(values).Contains(TokenType.UInt64);
        await Assert.That(values).Contains(TokenType.Float16);
        await Assert.That(values).Contains(TokenType.Float32);
        await Assert.That(values).Contains(TokenType.Float64);
    }

    [Test]
    public async Task EnumContainsExtensionToken()
    {
        var values = Enum.GetValues<TokenType>();
        await Assert.That(values).Contains(TokenType.Extension);
    }

    [Test]
    public async Task TotalMemberCount_IsTwentyTwo()
    {
        var count = Enum.GetValues<TokenType>().Length;
        await Assert.That(count).IsEqualTo(22);
    }

    [Test]
    public async Task StructuralTokens_HaveDistinctValues()
    {
        await Assert.That(TokenType.ObjectStart).IsNotEqualTo(TokenType.ObjectEnd);
        await Assert.That(TokenType.ArrayStart).IsNotEqualTo(TokenType.ArrayEnd);
        await Assert.That(TokenType.ObjectStart).IsNotEqualTo(TokenType.ArrayStart);
    }
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~TokenTypeTests"`
Expected: FAIL — `TokenType` not defined.

- [ ] **Step 3: Write the enum**

```csharp
// PicoSerDe.Abs/src/TokenType.cs
namespace PicoSerDe.Abs;

public enum TokenType
{
    None = 0,

    // Structural
    ObjectStart,
    ObjectEnd,
    ArrayStart,
    ArrayEnd,
    PropertyName,

    // Scalar
    Null,
    Bool,
    Int8, Int16, Int32, Int64,
    UInt8, UInt16, UInt32, UInt64,
    Float16, Float32, Float64,
    String,
    Bytes,

    // Extension
    Extension,
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~TokenTypeTests"`
Expected: All 6 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add PicoSerDe.Abs/src/TokenType.cs PicoSerDe.Abs/tests/PicoSerDe.Abs.Tests/TokenTypeTests.cs
git commit -m "feat: add TokenType enum — 22 format-agnostic token types"
```

---

### Task 3: ISerializer and IDeserializer Contracts

**Files:**
- Create: `PicoSerDe.Abs/src/ISerializer.cs`
- Create: `PicoSerDe.Abs/src/IDeserializer.cs`
- Create: `PicoSerDe.Abs/tests/PicoSerDe.Abs.Tests/InterfaceTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// PicoSerDe.Abs/tests/PicoSerDe.Abs.Tests/InterfaceTests.cs
using System.Buffers;

namespace PicoSerDe.Abs.Tests;

public class InterfaceTests
{
    private struct Int32Serializer : ISerializer<int>
    {
        public void Serialize(IBufferWriter<byte> writer, int value)
        {
            var span = writer.GetSpan(4);
            BitConverter.TryWriteBytes(span, value);
            writer.Advance(4);
        }
    }

    private struct Int32Deserializer : IDeserializer<int>
    {
        public int Deserialize(ReadOnlySpan<byte> data)
        {
            return BitConverter.ToInt32(data);
        }
    }

    [Test]
    public async Task Serializer_WritesToBuffer()
    {
        var buf = new ArrayBufferWriter<byte>(64);
        var serializer = new Int32Serializer();
        serializer.Serialize(buf, 42);
        await Assert.That(buf.WrittenCount).IsEqualTo(4);
    }

    [Test]
    public async Task Deserializer_ReadsFromSpan()
    {
        var data = BitConverter.GetBytes(42);
        var deserializer = new Int32Deserializer();
        var result = deserializer.Deserialize(data);
        await Assert.That(result).IsEqualTo(42);
    }

    [Test]
    public async Task Serialize_Deserialize_RoundTrip()
    {
        var serializer = new Int32Serializer();
        var deserializer = new Int32Deserializer();

        var buf = new ArrayBufferWriter<byte>(64);
        serializer.Serialize(buf, 12345);
        var result = deserializer.Deserialize(buf.WrittenSpan);

        await Assert.That(result).IsEqualTo(12345);
    }

    [Test]
    public async Task Serializer_InterfaceAssignment_Compiles()
    {
        ISerializer<int> serializer = new Int32Serializer();
        var buf = new ArrayBufferWriter<byte>(64);
        serializer.Serialize(buf, 10);
        await Assert.That(buf.WrittenCount).IsEqualTo(4);
    }

    [Test]
    public async Task Deserializer_InterfaceAssignment_Compiles()
    {
        IDeserializer<int> deserializer = new Int32Deserializer();
        var result = deserializer.Deserialize(BitConverter.GetBytes(99));
        await Assert.That(result).IsEqualTo(99);
    }
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~InterfaceTests"`
Expected: FAIL — `ISerializer<T>` / `IDeserializer<T>` not defined.

- [ ] **Step 3: Write contracts**

```csharp
// PicoSerDe.Abs/src/ISerializer.cs
using System.Buffers;

namespace PicoSerDe.Abs;

public interface ISerializer<T>
{
    void Serialize(IBufferWriter<byte> writer, T value);
}
```

```csharp
// PicoSerDe.Abs/src/IDeserializer.cs
namespace PicoSerDe.Abs;

public interface IDeserializer<T>
{
    T Deserialize(ReadOnlySpan<byte> data);
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~InterfaceTests"`
Expected: All 5 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add PicoSerDe.Abs/src/ISerializer.cs PicoSerDe.Abs/src/IDeserializer.cs PicoSerDe.Abs/tests/PicoSerDe.Abs.Tests/InterfaceTests.cs
git commit -m "feat: add ISerializer<T> and IDeserializer<T> core contracts"
```

---

### Task 4: Convenience Extensions

**Files:**
- Create: `PicoSerDe.Abs/src/SerializerExtensions.cs`
- Create: `PicoSerDe.Abs/src/DeserializerExtensions.cs`
- Create: `PicoSerDe.Abs/tests/PicoSerDe.Abs.Tests/ExtensionsTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// PicoSerDe.Abs/tests/PicoSerDe.Abs.Tests/ExtensionsTests.cs
using System.Buffers;
using System.Text;

namespace PicoSerDe.Abs.Tests;

public class ExtensionsTests
{
    private struct StringSerializer : ISerializer<string>
    {
        public void Serialize(IBufferWriter<byte> writer, string value)
        {
            var span = writer.GetSpan(Encoding.UTF8.GetByteCount(value));
            var len = Encoding.UTF8.GetBytes(value, span);
            writer.Advance(len);
        }
    }

    private struct StringDeserializer : IDeserializer<string>
    {
        public string Deserialize(ReadOnlySpan<byte> data)
        {
            return Encoding.UTF8.GetString(data);
        }
    }

    [Test]
    public async Task SerializeToBytes_ReturnsByteArray()
    {
        var serializer = new StringSerializer();
        var bytes = serializer.SerializeToBytes("hello");
        var result = Encoding.UTF8.GetString(bytes);
        await Assert.That(result).IsEqualTo("hello");
    }

    [Test]
    public async Task SerializeToString_ReturnsString()
    {
        var serializer = new StringSerializer();
        var result = serializer.SerializeToString("hello");
        await Assert.That(result).IsEqualTo("hello");
    }

    [Test]
    public async Task SerializeToStream_WritesBytes()
    {
        var serializer = new StringSerializer();
        using var stream = new MemoryStream();
        serializer.SerializeToStream(stream, "hello");
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var result = reader.ReadToEnd();
        await Assert.That(result).IsEqualTo("hello");
    }

    [Test]
    public async Task Deserialize_FromByteArray()
    {
        var deserializer = new StringDeserializer();
        var bytes = Encoding.UTF8.GetBytes("hello");
        var result = deserializer.Deserialize(bytes);
        await Assert.That(result).IsEqualTo("hello");
    }

    [Test]
    public async Task Deserialize_FromString()
    {
        var deserializer = new StringDeserializer();
        var result = deserializer.Deserialize("hello");
        await Assert.That(result).IsEqualTo("hello");
    }

    [Test]
    public async Task DeserializeFromStream_ReadsBytes()
    {
        var deserializer = new StringDeserializer();
        var bytes = Encoding.UTF8.GetBytes("hello");
        using var stream = new MemoryStream(bytes);
        var result = deserializer.DeserializeFromStream(stream);
        await Assert.That(result).IsEqualTo("hello");
    }
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~ExtensionsTests"`
Expected: FAIL — extension methods not defined.

- [ ] **Step 3: Write extensions**

```csharp
// PicoSerDe.Abs/src/SerializerExtensions.cs
using System.Buffers;
using System.Text;

namespace PicoSerDe.Abs;

public static class SerializerExtensions
{
    public static byte[] SerializeToBytes<T>(this ISerializer<T> serializer, T value)
    {
        var writer = new ArrayBufferWriter<byte>();
        serializer.Serialize(writer, value);
        return writer.WrittenSpan.ToArray();
    }

    public static string SerializeToString<T>(this ISerializer<T> serializer, T value)
    {
        var writer = new ArrayBufferWriter<byte>();
        serializer.Serialize(writer, value);
        return Encoding.UTF8.GetString(writer.WrittenSpan);
    }

    public static void SerializeToStream<T>(this ISerializer<T> serializer, Stream stream, T value)
    {
        var writer = new ArrayBufferWriter<byte>();
        serializer.Serialize(writer, value);
        stream.Write(writer.WrittenSpan);
    }
}
```

```csharp
// PicoSerDe.Abs/src/DeserializerExtensions.cs
using System.Text;

namespace PicoSerDe.Abs;

public static class DeserializerExtensions
{
    public static T Deserialize<T>(this IDeserializer<T> deserializer, byte[] data)
    {
        return deserializer.Deserialize(data.AsSpan());
    }

    public static T Deserialize<T>(this IDeserializer<T> deserializer, string data)
    {
        var bytes = Encoding.UTF8.GetBytes(data);
        return deserializer.Deserialize(bytes.AsSpan());
    }

    public static T DeserializeFromStream<T>(this IDeserializer<T> deserializer, Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return deserializer.Deserialize(ms.ToArray().AsSpan());
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~ExtensionsTests"`
Expected: All 6 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add PicoSerDe.Abs/src/SerializerExtensions.cs PicoSerDe.Abs/src/DeserializerExtensions.cs PicoSerDe.Abs/tests/PicoSerDe.Abs.Tests/ExtensionsTests.cs
git commit -m "feat: add serializer/deserializer convenience extensions"
```

---

### Task 5: Full Verification

- [ ] **Step 1: Run full test suite**

Run: `dotnet test`
Expected: All tests pass (6 + 5 + 6 = 17 tests).

- [ ] **Step 2: Verify build is clean**

Run: `dotnet build --no-restore`
Expected: 0 warnings, 0 errors.

- [ ] **Step 3: Final commit**

```bash
git status
# Expected: clean. Otherwise commit remaining changes.
```

---

## Plan Self-Review

1. **Spec coverage**: TokenType enum ✓, ISerializer<T> + IDeserializer<T> core contracts ✓, convenience extensions (ToBytes/ToString/FromStream) ✓. Streaming (PipeWriter/PipeReader) covered by design — PipeWriter implements IBufferWriter<byte> natively, PipeReader extension deferred to future task.

2. **Placeholder scan**: No TBD/TODO. All code shown inline.

3. **Type consistency**: `TokenType` members consistent across tests. `ISerializer<T>.Serialize(IBufferWriter<byte>, T)` and `IDeserializer<T>.Deserialize(ReadOnlySpan<byte>)` used consistently. Extension methods match interface signatures.
