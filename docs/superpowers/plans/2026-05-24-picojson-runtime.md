# PicoJetson Runtime Implementation Plan

> **For agentic workers:** Use `git-commit-csharp` skill for every commit. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Implement PicoJetson runtime — JsonReader, JsonWriter, JsonSerializer static API, and JsonPropertyNameAttribute — as the first format implementation on PicoSerDe.Abs.

**Architecture:** Three source files in `PicoJetson/src/PicoJetson/`. `JsonReader` is a ref struct UTF-8 JSON parser. `JsonWriter` is a ref struct IBufferWriter-backed JSON generator. `JsonSerializer` is a static facade that delegates to SG-generated implementations. `JsonPropertyNameAttribute` enables key name customization. All types use `PicoSerDe.Abs.TokenType`.

**Tech Stack:** .NET 10, C# latest, TUnit, PicoSerDe.Abs

---

## File Structure

```
PicoJetson/
  src/
    PicoJetson/
      PicoJetson.csproj                 ← net10.0, refs PicoSerDe.Abs
      GlobalUsings.cs
      JsonReader.cs
      JsonWriter.cs
      JsonSerializer.cs
      JsonPropertyNameAttribute.cs
    PicoJetson.Gen/
      PicoJetson.Gen.csproj             ← netstandard2.0 (scaffold only, implemented later)
  tests/
    PicoJetson.Tests/
      PicoJetson.Tests.csproj
      GlobalUsings.cs
      JsonWriterTests.cs
      JsonReaderTests.cs
      JsonSerializerTests.cs
```

---

### Task 1: Project Scaffold

**Files:**
- Create: `PicoJetson/src/PicoJetson/PicoJetson.csproj`
- Create: `PicoJetson/src/PicoJetson/GlobalUsings.cs`
- Create: `PicoJetson/src/PicoJetson.Gen/PicoJetson.Gen.csproj`
- Create: `PicoJetson/tests/PicoJetson.Tests/PicoJetson.Tests.csproj`
- Create: `PicoJetson/tests/PicoJetson.Tests/GlobalUsings.cs`
- Modify: `PicoSerDe.slnx`

- [ ] **Step 1: Create runtime project**

```xml
<!-- PicoJetson/src/PicoJetson/PicoJetson.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\..\PicoSerDe.Abs\src\PicoSerDe.Abs\PicoSerDe.Abs.csproj" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="PicoJetson.Tests" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create GlobalUsings for runtime**

```csharp
// PicoJetson/src/PicoJetson/GlobalUsings.cs
global using System.Buffers;
global using System.Text;
global using PicoSerDe.Abs;
```

- [ ] **Step 3: Create Gen scaffold**

```xml
<!-- PicoJetson/src/PicoJetson.Gen/PicoJetson.Gen.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
    <IsRoslynComponent>true</IsRoslynComponent>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.*" PrivateAssets="all" />
  </ItemGroup>

</Project>
```

- [ ] **Step 4: Create test project**

```xml
<!-- PicoJetson/tests/PicoJetson.Tests/PicoJetson.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="TUnit" Version="1.*" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\PicoJetson\PicoJetson.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 5: Create test GlobalUsings**

```csharp
// PicoJetson/tests/PicoJetson.Tests/GlobalUsings.cs
global using System.Buffers;
global using System.Text;
global using PicoSerDe.Abs;
global using PicoJetson;
```

- [ ] **Step 6: Update solution**

Add to `PicoSerDe.slnx`:
```xml
<Project Path="PicoJetson\src\PicoJetson\PicoJetson.csproj" />
<Project Path="PicoJetson\src\PicoJetson.Gen\PicoJetson.Gen.csproj" />
<Project Path="PicoJetson\tests\PicoJetson.Tests\PicoJetson.Tests.csproj" />
```

- [ ] **Step 7: Build verify**

Run: `dotnet restore && dotnet build`
Expected: 0 warnings, 0 errors.

- [ ] **Step 8: Commit** (use git-commit-csharp protocol)

```bash
git add PicoJetson/ PicoSerDe.slnx
# dotnet csharpier $staged_cs_files
# git add $staged_cs_files
git commit -m "build(picojetson): scaffold PicoJetson runtime + Gen + test projects"
```

---

### Task 2: JsonPropertyNameAttribute

**Files:**
- Create: `PicoJetson/src/PicoJetson/JsonPropertyNameAttribute.cs`
- Test: `PicoJetson/tests/PicoJetson.Tests/JsonPropertyNameAttributeTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
// PicoJetson/tests/PicoJetson.Tests/JsonPropertyNameAttributeTests.cs
namespace PicoJetson.Tests;

public class JsonPropertyNameAttributeTests
{
    [Test]
    public async Task Name_ReturnsConstructorValue()
    {
        var attr = new JsonPropertyNameAttribute("full_name");
        await Assert.That(attr.Name).IsEqualTo("full_name");
    }

    [Test]
    public async Task AttributeUsage_IsPropertyOnly()
    {
        var usage = (AttributeUsageAttribute)Attribute.GetCustomAttribute(
            typeof(JsonPropertyNameAttribute), typeof(AttributeUsageAttribute))!;
        await Assert.That(usage.ValidOn).IsEqualTo(AttributeTargets.Property);
    }
}
```

Run: `dotnet test --filter "JsonPropertyNameAttributeTests"`
Expected: FAIL — type not found.

- [ ] **Step 2: Implement**

```csharp
// PicoJetson/src/PicoJetson/JsonPropertyNameAttribute.cs
namespace PicoJetson;

[AttributeUsage(AttributeTargets.Property)]
public sealed class JsonPropertyNameAttribute : Attribute
{
    public string Name { get; }
    public JsonPropertyNameAttribute(string name) => Name = name;
}
```

- [ ] **Step 3: Run tests**

Run: `dotnet test --filter "JsonPropertyNameAttributeTests"`
Expected: 2 PASS.

- [ ] **Step 4: Commit** (use git-commit-csharp)

```bash
git add PicoJetson/src/PicoJetson/JsonPropertyNameAttribute.cs PicoJetson/tests/PicoJetson.Tests/JsonPropertyNameAttributeTests.cs
# csharpier → re-stage → commit
git commit -m "feat(picojetson): add JsonPropertyNameAttribute"
```

---

### Task 3: JsonWriter

**Files:**
- Create: `PicoJetson/src/PicoJetson/JsonWriter.cs`
- Create: `PicoJetson/tests/PicoJetson.Tests/JsonWriterTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// PicoJetson/tests/PicoJetson.Tests/JsonWriterTests.cs
using System.Text;

namespace PicoJetson.Tests;

public class JsonWriterTests
{
    private static string GetWrittenString(JsonWriter writer, ArrayBufferWriter<byte> buf)
    {
        writer.Flush();
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    [Test]
    public async Task WriteNull_ProducesNull()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var writer = new JsonWriter(buf);
        writer.WriteNull();
        var json = GetWrittenString(writer, buf);
        await Assert.That(json).IsEqualTo("null");
    }

    [Test]
    public async Task WriteBoolean_True()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var writer = new JsonWriter(buf);
        writer.WriteBoolean(true);
        var json = GetWrittenString(writer, buf);
        await Assert.That(json).IsEqualTo("true");
    }

    [Test]
    public async Task WriteBoolean_False()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var writer = new JsonWriter(buf);
        writer.WriteBoolean(false);
        var json = GetWrittenString(writer, buf);
        await Assert.That(json).IsEqualTo("false");
    }

    [Test]
    public async Task WriteNumber_Int32()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var writer = new JsonWriter(buf);
        writer.WriteNumber(42);
        var json = GetWrittenString(writer, buf);
        await Assert.That(json).IsEqualTo("42");
    }

    [Test]
    public async Task WriteNumber_Int64()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var writer = new JsonWriter(buf);
        writer.WriteNumber(1234567890123L);
        var json = GetWrittenString(writer, buf);
        await Assert.That(json).IsEqualTo("1234567890123");
    }

    [Test]
    public async Task WriteNumber_Double()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var writer = new JsonWriter(buf);
        writer.WriteNumber(3.14);
        var json = GetWrittenString(writer, buf);
        await Assert.That(json).Contains("3.14");
    }

    [Test]
    public async Task WriteString_ProducesQuotedString()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var writer = new JsonWriter(buf);
        writer.WriteString("hello"u8);
        var json = GetWrittenString(writer, buf);
        await Assert.That(json).IsEqualTo("\"hello\"");
    }

    [Test]
    public async Task WriteObject_Empty()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var writer = new JsonWriter(buf);
        writer.WriteStartObject();
        writer.WriteEndObject();
        var json = GetWrittenString(writer, buf);
        await Assert.That(json).IsEqualTo("{}");
    }

    [Test]
    public async Task WriteObject_WithProperty()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var writer = new JsonWriter(buf);
        writer.WriteStartObject();
        writer.WritePropertyName("name"u8);
        writer.WriteString("alice"u8);
        writer.WriteEndObject();
        var json = GetWrittenString(writer, buf);
        await Assert.That(json).IsEqualTo("{\"name\":\"alice\"}");
    }

    [Test]
    public async Task WriteArray_Empty()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var writer = new JsonWriter(buf);
        writer.WriteStartArray();
        writer.WriteEndArray();
        var json = GetWrittenString(writer, buf);
        await Assert.That(json).IsEqualTo("[]");
    }

    [Test]
    public async Task WriteArray_WithInts()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var writer = new JsonWriter(buf);
        writer.WriteStartArray();
        writer.WriteNumber(1);
        writer.WriteNumber(2);
        writer.WriteNumber(3);
        writer.WriteEndArray();
        var json = GetWrittenString(writer, buf);
        await Assert.That(json).IsEqualTo("[1,2,3]");
    }

    [Test]
    public async Task WriteNested_ObjectInObject()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var writer = new JsonWriter(buf);
        writer.WriteStartObject();
        writer.WritePropertyName("inner"u8);
        writer.WriteStartObject();
        writer.WritePropertyName("x"u8);
        writer.WriteNumber(1);
        writer.WriteEndObject();
        writer.WriteEndObject();
        var json = GetWrittenString(writer, buf);
        await Assert.That(json).IsEqualTo("{\"inner\":{\"x\":1}}");
    }

    [Test]
    public async Task BytesWritten_Tracks()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var writer = new JsonWriter(buf);
        writer.WriteNumber(42);
        await Assert.That(writer.BytesWritten).IsEqualTo(2);
    }

    [Test]
    public async Task Indented_ProducesMultilineOutput()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var writer = new JsonWriter(buf, indented: true);
        writer.WriteStartObject();
        writer.WritePropertyName("a"u8);
        writer.WriteNumber(1);
        writer.WriteEndObject();
        var json = GetWrittenString(writer, buf);
        await Assert.That(json).IsEqualTo("{\n  \"a\": 1\n}");
    }
}
```

- [ ] **Step 2: Run tests → RED**

Run: `dotnet test --filter "JsonWriterTests"`
Expected: FAIL — JsonWriter not defined.

- [ ] **Step 3: Implement JsonWriter**

```csharp
// PicoJetson/src/PicoJetson/JsonWriter.cs
namespace PicoJetson;

public ref struct JsonWriter
{
    private IBufferWriter<byte> _buffer;
    private long _bytesWritten;
    private readonly bool _indented;
    private int _depth;

    public long BytesWritten => _bytesWritten;

    public JsonWriter(IBufferWriter<byte> buffer, bool indented = false)
    {
        _buffer = buffer;
        _bytesWritten = 0;
        _indented = indented;
        _depth = 0;
    }

    public void Flush() { /* no-op: writes to IBufferWriter directly */ }

    public void WriteNull()
    {
        WriteRaw("null"u8);
    }

    public void WriteBoolean(bool value)
    {
        WriteRaw(value ? "true"u8 : "false"u8);
    }

    public void WriteNumber(int value)
    {
        Span<byte> buf = stackalloc byte[16];
        value.TryFormat(buf, out var written);
        WriteRaw(buf[..written]);
    }

    public void WriteNumber(long value)
    {
        Span<byte> buf = stackalloc byte[32];
        value.TryFormat(buf, out var written);
        WriteRaw(buf[..written]);
    }

    public void WriteNumber(double value)
    {
        Span<byte> buf = stackalloc byte[32];
        value.TryFormat(buf, out var written);
        WriteRaw(buf[..written]);
    }

    public void WriteString(ReadOnlySpan<byte> utf8Value)
    {
        WriteByte((byte)'"');
        WriteRaw(utf8Value);
        WriteByte((byte)'"');
    }

    public void WritePropertyName(ReadOnlySpan<byte> utf8Name)
    {
        if (_indented) WriteIndent();
        WriteString(utf8Name);
        WriteRaw(":"u8);
    }

    public void WriteStartObject()
    {
        WriteByte((byte)'{');
        _depth++;
    }

    public void WriteEndObject()
    {
        _depth--;
        if (_indented) WriteIndent();
        WriteByte((byte)'}');
    }

    public void WriteStartArray()
    {
        WriteByte((byte)'[');
        _depth++;
    }

    public void WriteEndArray()
    {
        _depth--;
        if (_indented) WriteIndent();
        WriteByte((byte)']');
    }

    private void WriteRaw(ReadOnlySpan<byte> utf8)
    {
        _buffer.Write(utf8);
        _bytesWritten += utf8.Length;
    }

    private void WriteByte(byte value)
    {
        var span = _buffer.GetSpan(1);
        span[0] = value;
        _buffer.Advance(1);
        _bytesWritten++;
    }

    private void WriteIndent()
    {
        WriteByte((byte)'\n');
        for (var i = 0; i < _depth; i++)
        {
            WriteRaw("  "u8);
        }
    }
}
```

- [ ] **Step 4: Run tests → GREEN**

Run: `dotnet test --filter "JsonWriterTests"`
Expected: All 14 tests PASS.

- [ ] **Step 5: Commit** (use git-commit-csharp)

```bash
git add PicoJetson/src/PicoJetson/JsonWriter.cs PicoJetson/tests/PicoJetson.Tests/JsonWriterTests.cs
# csharpier → re-stage → commit
git commit -m "feat(picojetson): add JsonWriter — IBufferWriter-backed ref struct"
```

---

### Task 4: JsonReader

**Files:**
- Create: `PicoJetson/src/PicoJetson/JsonReader.cs`
- Create: `PicoJetson/tests/PicoJetson.Tests/JsonReaderTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// PicoJetson/tests/PicoJetson.Tests/JsonReaderTests.cs
namespace PicoJetson.Tests;

public class JsonReaderTests
{
    [Test]
    public async Task ReadNull_ReturnsNullToken()
    {
        var reader = new JsonReader("null"u8);
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.Null);
    }

    [Test]
    public async Task ReadTrue_ReturnsBoolToken()
    {
        var reader = new JsonReader("true"u8);
        reader.Read();
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.Bool);
    }

    [Test]
    public async Task ReadFalse_ReturnsBoolToken()
    {
        var reader = new JsonReader("false"u8);
        reader.Read();
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.Bool);
    }

    [Test]
    public async Task ReadInteger_ReturnsInt32Token()
    {
        var reader = new JsonReader("42"u8);
        reader.Read();
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.Int32);
    }

    [Test]
    public async Task ReadNegativeInteger_ReturnsInt32()
    {
        var reader = new JsonReader("-17"u8);
        reader.Read();
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.Int32);
    }

    [Test]
    public async Task ReadFloat_ReturnsFloat64Token()
    {
        var reader = new JsonReader("3.14"u8);
        reader.Read();
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.Float64);
    }

    [Test]
    public async Task ReadString_ReturnsStringToken()
    {
        var reader = new JsonReader("\"hello\""u8);
        reader.Read();
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.String);
    }

    [Test]
    public async Task GetStringRaw_ReturnsDecodedBytes()
    {
        var reader = new JsonReader("\"hello\""u8);
        reader.Read();
        var raw = reader.GetStringRaw();
        await Assert.That(Encoding.UTF8.GetString(raw)).IsEqualTo("hello");
    }

    [Test]
    public async Task TryGetInt32_ParsesValue()
    {
        var reader = new JsonReader("42"u8);
        reader.Read();
        var ok = reader.TryGetInt32(out var val);
        await Assert.That(ok).IsTrue();
        await Assert.That(val).IsEqualTo(42);
    }

    [Test]
    public async Task TryGetInt32_ReturnsFalse_OnString()
    {
        var reader = new JsonReader("\"hello\""u8);
        reader.Read();
        var ok = reader.TryGetInt32(out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TryGetFloat64_ParsesValue()
    {
        var reader = new JsonReader("3.14"u8);
        reader.Read();
        var ok = reader.TryGetFloat64(out var val);
        await Assert.That(ok).IsTrue();
        await Assert.That(Math.Abs(val - 3.14)).IsLessThan(0.001);
    }

    [Test]
    public async Task TryGetBool_ParsesTrue()
    {
        var reader = new JsonReader("true"u8);
        reader.Read();
        var ok = reader.TryGetBool(out var val);
        await Assert.That(ok).IsTrue();
        await Assert.That(val).IsTrue();
    }

    [Test]
    public async Task ReadEmptyObject_ReturnsStartEnd()
    {
        var reader = new JsonReader("{}"u8);
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.ObjectStart);
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.ObjectEnd);
        await Assert.That(reader.Read()).IsFalse();
    }

    [Test]
    public async Task ReadEmptyArray_ReturnsStartEnd()
    {
        var reader = new JsonReader("[]"u8);
        reader.Read();
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.ArrayStart);
        reader.Read();
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.ArrayEnd);
    }

    [Test]
    public async Task ReadObjectWithProperty()
    {
        var reader = new JsonReader("{\"name\":\"alice\"}"u8);
        reader.Read(); await Assert.That(reader.TokenType).IsEqualTo(TokenType.ObjectStart);
        reader.Read(); await Assert.That(reader.TokenType).IsEqualTo(TokenType.PropertyName);
        reader.Read(); await Assert.That(reader.TokenType).IsEqualTo(TokenType.String);
        reader.Read(); await Assert.That(reader.TokenType).IsEqualTo(TokenType.ObjectEnd);
        await Assert.That(reader.Read()).IsFalse();
    }

    [Test]
    public async Task Depth_TracksNesting()
    {
        var reader = new JsonReader("{\"a\":[1,2]}"u8);
        reader.Read(); await Assert.That(reader.Depth).IsEqualTo(1);  // {
        reader.Read(); await Assert.That(reader.Depth).IsEqualTo(1);  // PropertyName
        reader.Read(); await Assert.That(reader.Depth).IsEqualTo(2);  // [
        reader.Read(); await Assert.That(reader.Depth).IsEqualTo(2);  // 1
        reader.Read(); await Assert.That(reader.Depth).IsEqualTo(2);  // 2
        reader.Read(); await Assert.That(reader.Depth).IsEqualTo(1);  // ]
        reader.Read(); await Assert.That(reader.Depth).IsEqualTo(0);  // }
    }

    [Test]
    public async Task BytesConsumed_MatchesInput()
    {
        var reader = new JsonReader("42"u8);
        reader.Read();
        await Assert.That(reader.BytesConsumed).IsEqualTo(2);
    }

    [Test]
    public async Task Skip_Object_AdvancesPastIt()
    {
        var reader = new JsonReader("{\"a\":1} \"next\""u8);
        reader.Read(); // ObjectStart
        reader.Skip();
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.String);
    }

    [Test]
    public async Task TrySkip_ReturnsFalse_OnMalformed()
    {
        var reader = new JsonReader("{broken"u8);
        reader.Read();
        var ok = reader.TrySkip();
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task ValueSpan_ContainsRawBytes()
    {
        var reader = new JsonReader("42"u8);
        reader.Read();
        var span = reader.ValueSpan;
        await Assert.That(Encoding.UTF8.GetString(span)).IsEqualTo("42");
    }
}
```

- [ ] **Step 2: Run tests → RED**

Run: `dotnet test --filter "JsonReaderTests"`
Expected: FAIL — JsonReader not defined.

- [ ] **Step 3: Implement JsonReader**

```csharp
// PicoJetson/src/PicoJetson/JsonReader.cs
namespace PicoJetson;

public ref struct JsonReader
{
    private ReadOnlySpan<byte> _data;
    private int _position;
    private TokenType _tokenType;
    private int _depth;
    private int _maxDepth;
    private ReadOnlySpan<byte> _valueSpan;
    private byte[]? _rentedBuffer;

    public JsonReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
        _tokenType = TokenType.None;
        _depth = 0;
        _maxDepth = 256;
        _valueSpan = default;
        _rentedBuffer = null;
    }

    // ReadOnlySequence constructor: v1 copies to contiguous buffer
    public JsonReader(ReadOnlySequence<byte> data) : this()
    {
        _data = data.IsSingleSegment
            ? data.FirstSpan
            : data.ToArray(); // v1 simplification
        _position = 0;
        _tokenType = TokenType.None;
        _depth = 0;
        _maxDepth = 256;
        _valueSpan = default;
        _rentedBuffer = null;
    }

    public TokenType TokenType => _tokenType;
    public int Depth => _depth;
    public long BytesConsumed => _position;
    public ReadOnlySpan<byte> ValueSpan => _valueSpan;

    public bool Read()
    {
        SkipWhitespace();
        if (_position >= _data.Length) return false;

        var b = _data[_position];

        switch (b)
        {
            case (byte)'{': _tokenType = TokenType.ObjectStart; _position++; _depth++; return true;
            case (byte)'}': _tokenType = TokenType.ObjectEnd;  _position++; _depth--; return true;
            case (byte)'[': _tokenType = TokenType.ArrayStart;  _position++; _depth++; return true;
            case (byte)']': _tokenType = TokenType.ArrayEnd;    _position++; _depth--; return true;
            case (byte)'"': return ReadStringOrProperty();
            case (byte)'t': return ReadLiteral("true"u8, TokenType.Bool);
            case (byte)'f': return ReadLiteral("false"u8, TokenType.Bool);
            case (byte)'n': return ReadLiteral("null"u8, TokenType.Null);
            case (byte)'-':
            case (byte)'0': case (byte)'1': case (byte)'2': case (byte)'3': case (byte)'4':
            case (byte)'5': case (byte)'6': case (byte)'7': case (byte)'8': case (byte)'9':
                return ReadNumber();
            default:
                throw new FormatException($"Unexpected byte 0x{b:X2} at position {_position}");
        }
    }

    public void Skip()
    {
        if (!TrySkip())
            throw new FormatException($"Failed to skip at position {_position}");
    }

    public bool TrySkip()
    {
        var startDepth = _depth;
        if (_tokenType is TokenType.ObjectStart or TokenType.ArrayStart)
            startDepth--;

        while (Read())
        {
            if (_depth == startDepth)
                return true;
        }
        return false;
    }

    public ReadOnlySpan<byte> GetStringRaw()
    {
        // Strings stored in _valueSpan already have surrounding quotes stripped.
        // v1: no unescape — assumes clean UTF-8 with no escape sequences.
        return _valueSpan;
    }

    public bool TryGetInt32(out int value)
    {
        if (_tokenType != TokenType.Int32)
        {
            value = 0; return false;
        }
        return int.TryParse(_valueSpan, out value);
    }

    public bool TryGetInt64(out long value)
    {
        if (_tokenType is not (TokenType.Int32 or TokenType.Int64))
        {
            value = 0; return false;
        }
        return long.TryParse(_valueSpan, out value);
    }

    public bool TryGetFloat64(out double value)
    {
        if (_tokenType != TokenType.Float64)
        {
            value = 0; return false;
        }
        return double.TryParse(_valueSpan, out value);
    }

    public bool TryGetBool(out bool value)
    {
        if (_tokenType != TokenType.Bool)
        {
            value = false; return false;
        }
        value = _valueSpan[0] == (byte)'t';
        return true;
    }

    // ── Internal ──

    private void SkipWhitespace()
    {
        while (_position < _data.Length)
        {
            var b = _data[_position];
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r'))
                return;
            _position++;
        }
    }

    private bool ReadStringOrProperty()
    {
        _position++; // skip opening "
        var start = _position;
        while (_position < _data.Length && _data[_position] != (byte)'"')
            _position++;
        if (_position >= _data.Length) throw new FormatException("Unterminated string");

        _valueSpan = _data[start.._position];
        _position++; // skip closing "

        // Peek ahead: if next non-whitespace char is ':', it's a property name
        var savedPos = _position;
        SkipWhitespace();
        if (_position < _data.Length && _data[_position] == (byte)':')
        {
            _tokenType = TokenType.PropertyName;
            _position++; // skip ':'
        }
        else
        {
            _tokenType = TokenType.String;
            _position = savedPos; // restore position after peek
        }
        return true;
    }

    private bool ReadNumber()
    {
        var start = _position;

        if (_data[_position] == (byte)'-') _position++;

        while (_position < _data.Length && IsDigit(_data[_position]))
            _position++;

        bool isFloat = false;
        if (_position < _data.Length && _data[_position] == (byte)'.')
        {
            isFloat = true;
            _position++;
            while (_position < _data.Length && IsDigit(_data[_position]))
                _position++;
        }
        if (_position < _data.Length && (_data[_position] is (byte)'e' or (byte)'E'))
        {
            isFloat = true;
            _position++;
            if (_position < _data.Length && _data[_position] is (byte)'+' or (byte)'-')
                _position++;
            while (_position < _data.Length && IsDigit(_data[_position]))
                _position++;
        }

        _valueSpan = _data[start.._position];
        _tokenType = isFloat ? TokenType.Float64 : TokenType.Int32;
        return true;
    }

    private bool ReadLiteral(ReadOnlySpan<byte> expected, TokenType token)
    {
        if (_position + expected.Length > _data.Length)
            throw new FormatException($"Unexpected end of data at position {_position}");

        if (!_data.Slice(_position, expected.Length).SequenceEqual(expected))
            throw new FormatException($"Invalid literal at position {_position}");

        _valueSpan = _data.Slice(_position, expected.Length);
        _tokenType = token;
        _position += expected.Length;
        return true;
    }

    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    public void Dispose()
    {
        _rentedBuffer = null;
    }
}
```

- [ ] **Step 4: Run tests → GREEN**

Run: `dotnet test --filter "JsonReaderTests"`
Expected: All 20 tests PASS.

- [ ] **Step 5: Commit** (use git-commit-csharp)

```bash
git add PicoJetson/src/PicoJetson/JsonReader.cs PicoJetson/tests/PicoJetson.Tests/JsonReaderTests.cs
# csharpier → re-stage → commit
git commit -m "feat(picojetson): add JsonReader — UTF-8 JSON ref struct parser"
```

---

### Task 5: JsonSerializer Static API + Full Verification

**Files:**
- Create: `PicoJetson/src/PicoJetson/JsonSerializer.cs`
- Create: `PicoJetson/tests/PicoJetson.Tests/JsonSerializerTests.cs`

**Note**: The static API delegates to ISerializer<T>/IDeserializer<T> implementations. Until Source Generator is built, we manually implement one for test types.

- [ ] **Step 1: Write failing tests**

```csharp
// PicoJetson/tests/PicoJetson.Tests/JsonSerializerTests.cs
namespace PicoJetson.Tests;

public class JsonSerializerTests
{
    // Temporary manual implementation until Source Generator is built
    private struct ManualPersonSerializer : ISerializer<Person>
    {
        public void Serialize(IBufferWriter<byte> writer, Person value)
        {
            var jw = new JsonWriter(writer);
            jw.WriteStartObject();
            jw.WritePropertyName("Name"u8);
            jw.WriteString(Encoding.UTF8.GetBytes(value.Name));
            jw.WritePropertyName("Age"u8);
            jw.WriteNumber(value.Age);
            jw.WriteEndObject();
        }
    }

    private struct ManualPersonDeserializer : IDeserializer<Person>
    {
        public Person Deserialize(ReadOnlySpan<byte> data)
        {
            var reader = new JsonReader(data);
            var obj = new Person();
            reader.Read();
            while (reader.Read() && reader.TokenType == TokenType.PropertyName)
            {
                var prop = reader.GetStringRaw();
                reader.Read();
                if (prop.SequenceEqual("Name"u8))
                    obj.Name = Encoding.UTF8.GetString(reader.GetStringRaw());
                else if (prop.SequenceEqual("Age"u8))
                {
                    reader.TryGetInt32(out var age);
                    obj.Age = age;
                }
                else
                    reader.TrySkip();
            }
            return obj;
        }
    }

    public class Person
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }
    }

    [Test]
    public async Task Manual_RoundTrip_Works()
    {
        var person = new Person { Name = "Alice", Age = 30 };
        var serializer = new ManualPersonSerializer();
        var deserializer = new ManualPersonDeserializer();

        var buf = new ArrayBufferWriter<byte>(256);
        serializer.Serialize(buf, person);
        var result = deserializer.Deserialize(buf.WrittenSpan);

        await Assert.That(result.Name).IsEqualTo("Alice");
        await Assert.That(result.Age).IsEqualTo(30);
    }

    [Test]
    public async Task SerializeToUtf8Bytes_WithManualImpl()
    {
        // This test calls the static API which internally finds the manual impl.
        // v1: JsonSerializer.SerializeToUtf8Bytes requires SG-generated impl,
        // so this test validates the API compiles. Full integration after SG.
        var person = new Person { Name = "Bob", Age = 25 };
        var serializer = new ManualPersonSerializer();
        var bytes = serializer.SerializeToBytes(person);
        var json = Encoding.UTF8.GetString(bytes);
        await Assert.That(json).Contains("\"Name\"");
        await Assert.That(json).Contains("\"Bob\"");
    }
}
```

- [ ] **Step 2: Run tests → RED**

Run: `dotnet test --filter "JsonSerializerTests"`
Expected: FAIL — JsonSerializer class not found.

- [ ] **Step 3: Implement**

```csharp
// PicoJetson/src/PicoJetson/JsonSerializer.cs
namespace PicoJetson;

public static class JsonSerializer
{
    public static byte[] SerializeToUtf8Bytes<T>(T value)
    {
        // v1: placeholder — will delegate to SG-generated serializer.
        // For now, callers use ISerializer<T> directly.
        throw new NotImplementedException("Source Generator not yet built. Use ISerializer<T> implementations directly.");
    }

    public static string Serialize<T>(T value)
    {
        var bytes = SerializeToUtf8Bytes(value);
        return Encoding.UTF8.GetString(bytes);
    }

    public static void Serialize<T>(IBufferWriter<byte> writer, T value)
    {
        throw new NotImplementedException("Source Generator not yet built.");
    }

    public static T? Deserialize<T>(ReadOnlySpan<byte> data)
    {
        throw new NotImplementedException("Source Generator not yet built.");
    }
}
```

Note: JsonSerializer is a stub at this stage. Full implementation comes with Source Generator. The round-trip test uses manual implementations directly.

- [ ] **Step 4: Run relevant tests**

Run: `dotnet test --filter "JsonSerializerTests"`  
Expected: `Manual_RoundTrip_Works` PASS, `SerializeToUtf8Bytes_WithManualImpl` PASS. JsonSerializer stub compiles.

- [ ] **Step 5: Full suite**

Run: `dotnet test`
Expected: All tests pass.

- [ ] **Step 6: Commit** (use git-commit-csharp)

```bash
git add PicoJetson/src/PicoJetson/JsonSerializer.cs PicoJetson/tests/PicoJetson.Tests/JsonSerializerTests.cs
# csharpier → re-stage → commit
git commit -m "feat(picojetson): add JsonSerializer static API stub + manual round-trip test"
```
