# PicoIni Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement a high-performance, AOT-first INI serialization library (PicoIni) following PicoJetson patterns with TDD.

**Architecture:** Two-layer: `PicoIni` runtime (net10.0, `ref struct` IniReader/IniWriter, static IniSerializer facade) + `PicoIni.Gen` source generator (netstandard2.0, IIncrementalGenerator). Uses `PicoSerDe.Abs` ISerializer<T>/IDeserializer<T> abstractions. Section-per-property mapping model.

**Tech Stack:** C# 14, .NET 10, Roslyn IIncrementalGenerator, TUnit, PicoBench

---

## File Structure

```
PicoIni/
├── src/PicoIni/
│   ├── PicoIni.csproj              # net10.0, packable, IsAotCompatible
│   ├── GlobalUsings.cs             # global using System.Buffers, System.Text
│   ├── IniTokenType.cs             # enum: None, SectionStart, Key, Value, Comment, Blank
│   ├── IniReader.cs                # ref struct INI pull parser
│   ├── IniWriter.cs                # ref struct INI push writer
│   ├── IniSerializer.cs            # static partial class facade
│   ├── IIniConverter.cs            # interface for custom converters
│   ├── IniSectionAttribute.cs      # [IniSection("name")] class/property
│   ├── IniKeyAttribute.cs          # [IniKey("name")] property
│   ├── IniIgnoreAttribute.cs       # [IniIgnore] property
│   ├── IniCommentAttribute.cs      # [IniComment("text")] class/property
│   └── IniConverterAttribute.cs    # [IniConverter(typeof(T))] property
├── src/PicoIni.Gen/
│   ├── PicoIni.Gen.csproj          # netstandard2.0, Roslyn component, packable
│   ├── GlobalUsings.cs
│   ├── IsExternalInit.cs           # polyfill for record types
│   ├── TypeKindResolver.cs         # Roslyn type → kind string
│   └── IniSerializerGenerator.cs   # IIncrementalGenerator
├── tests/PicoIni.Tests/
│   ├── PicoIni.Tests.csproj        # TUnit, refs PicoIni + PicoIni.Gen
│   ├── GlobalUsings.cs
│   ├── IniTokenTypeTests.cs
│   ├── IniReaderTests.cs
│   ├── IniWriterTests.cs
│   ├── IniSerializerTests.cs
│   ├── GeneratorTests.cs
│   ├── IniIgnoreTests.cs
│   ├── IniCommentTests.cs
│   ├── IniConverterTests.cs
│   ├── CollectionTests.cs
│   └── FuzzingTests.cs
├── benchmarks/PicoIni.Benchmarks/
│   ├── PicoIni.Benchmarks.csproj
│   └── Program.cs
└── samples/PicoIni.Sample/
    ├── PicoIni.Sample.csproj
    └── Program.cs
```

**Changes to existing files:**
- `PicoSerDe.slnx` — add 5 new projects (runtime, gen, tests, benchmarks, sample)
- `.github/workflows/ci.yml` — add PicoIni build/test steps
- `.github/workflows/release.yml` — add PicoIni.Gen, PicoIni pack steps

---

## Phase 0: Project Scaffolding

### Task 0.1: Create PicoIni directory structure and csproj files

**Files:**
- Create: `PicoIni/src/PicoIni/PicoIni.csproj`
- Create: `PicoIni/src/PicoIni/GlobalUsings.cs`
- Create: `PicoIni/src/PicoIni.Gen/PicoIni.Gen.csproj`
- Create: `PicoIni/src/PicoIni.Gen/GlobalUsings.cs`
- Create: `PicoIni/src/PicoIni.Gen/IsExternalInit.cs`
- Create: `PicoIni/tests/PicoIni.Tests/PicoIni.Tests.csproj`
- Create: `PicoIni/tests/PicoIni.Tests/GlobalUsings.cs`
- Create: `PicoIni/benchmarks/PicoIni.Benchmarks/PicoIni.Benchmarks.csproj`
- Create: `PicoIni/samples/PicoIni.Sample/PicoIni.Sample.csproj`
- Modify: `PicoSerDe.slnx` — add 5 project references

- [ ] **Step 0.1.1: Create PicoIni.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>true</IsPackable>
    <PackageId>PicoIni</PackageId>
    <Description>PicoIni — high-performance, AOT-first INI serializer with zero-reflection source generation for the PicoHex ecosystem</Description>
    <PackageTags>PicoHex;INI;AOT;Serialization;Configuration</PackageTags>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsAotCompatible>true</IsAotCompatible>
    <IsTrimmable>true</IsTrimmable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\..\PicoSerDe.Abs\src\PicoSerDe.Abs.csproj" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="PicoIni.Tests" />
  </ItemGroup>
</Project>
```

- [ ] **Step 0.1.2: Create PicoIni GlobalUsings.cs**

```csharp
global using System.Buffers;
global using System.Text;
global using PicoSerDe.Abs;
```

- [ ] **Step 0.1.3: Create PicoIni.Gen.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk" TreatAsLocalProperty="PublishAot">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <PublishAot>false</PublishAot>
    <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
    <IsRoslynComponent>true</IsRoslynComponent>
    <IsPackable>true</IsPackable>
    <DevelopmentDependency>false</DevelopmentDependency>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <PackageId>PicoIni.Gen</PackageId>
    <Description>PicoIni source generator — AOT-compatible INI serialization code generation for PicoHex</Description>
    <PackageTags>PicoHex;INI;AOT;Source Generator;Roslyn;Analyzer</PackageTags>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <None Include="$(OutputPath)/$(AssemblyName).dll" Pack="true"
          PackagePath="analyzers/dotnet/cs" Visible="false" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.*" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

- [ ] **Step 0.1.4: Create PicoIni.Gen GlobalUsings.cs**

```csharp
global using System.Collections.Immutable;
global using System.Text;
global using Microsoft.CodeAnalysis;
global using Microsoft.CodeAnalysis.CSharp;
global using Microsoft.CodeAnalysis.CSharp.Syntax;
global using Microsoft.CodeAnalysis.Text;
```

- [ ] **Step 0.1.5: Create IsExternalInit.cs (same as PicoJetson.Gen's)**

```csharp
namespace System.Runtime.CompilerServices;
internal static class IsExternalInit { }
```

- [ ] **Step 0.1.6: Create PicoIni.Tests.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="TUnit" Version="1.*" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\PicoIni\PicoIni.csproj" />
    <ProjectReference Include="..\..\src\PicoIni.Gen\PicoIni.Gen.csproj"
                      OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
    <ProjectReference Include="..\..\..\PicoSerDe.Abs\src\PicoSerDe.Abs.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 0.1.7: Create PicoIni.Tests GlobalUsings.cs**

```csharp
global using System.Buffers;
global using System.Text;
global using PicoIni;
global using PicoSerDe.Abs;
global using TUnit.Core;
```

- [ ] **Step 0.1.8: Create benchmark and sample csproj files** (PicoIni.Benchmarks.csproj and PicoIni.Sample.csproj following PicoJetson patterns with PicoBench, PublishAot=true, TrimMode=full)

- [ ] **Step 0.1.9: Update PicoSerDe.slnx**

Add these 5 entries inside `<Solution>`:
```xml
<Project Path="PicoIni/src/PicoIni/PicoIni.csproj" />
<Project Path="PicoIni/src/PicoIni.Gen/PicoIni.Gen.csproj" />
<Project Path="PicoIni/tests/PicoIni.Tests/PicoIni.Tests.csproj" />
<Project Path="PicoIni/benchmarks/PicoIni.Benchmarks/PicoIni.Benchmarks.csproj" />
<Project Path="PicoIni/samples/PicoIni.Sample/PicoIni.Sample.csproj" />
```

- [ ] **Step 0.1.10: Verify build**

Run: `dotnet build PicoIni/src/PicoIni/PicoIni.csproj --no-restore`
Expected: SUCCESS (empty project)

- [ ] **Step 0.1.11: Commit**

---

## Phase 1: IniTokenType + Attributes (TDD)

### Task 1.1: IniTokenType enum

**Files:** Create: `PicoIni/src/PicoIni/IniTokenType.cs`, `PicoIni/tests/PicoIni.Tests/IniTokenTypeTests.cs`

- [ ] **Step 1.1.1: Write failing test**

```csharp
namespace PicoIni.Tests;
public class IniTokenTypeTests
{
    [Test]
    public async Task IniTokenType_HasExpectedValues()
    {
        var values = Enum.GetValues<IniTokenType>();
        await Assert.That(values).HasCount(6);
        await Assert.That(IniTokenType.None).IsEqualTo((IniTokenType)0);
        await Assert.That(IniTokenType.SectionStart).IsNotEqualTo(IniTokenType.None);
        await Assert.That(IniTokenType.Key).IsNotEqualTo(IniTokenType.None);
        await Assert.That(IniTokenType.Value).IsNotEqualTo(IniTokenType.None);
        await Assert.That(IniTokenType.Comment).IsNotEqualTo(IniTokenType.None);
        await Assert.That(IniTokenType.Blank).IsNotEqualTo(IniTokenType.None);
    }
}
```

- [ ] **Step 1.1.2: Run test — expect FAIL** (IniTokenType not defined)

- [ ] **Step 1.1.3: Write minimal implementation**

```csharp
namespace PicoIni;
public enum IniTokenType
{
    None = 0,
    SectionStart,
    Key,
    Value,
    Comment,
    Blank,
}
```

- [ ] **Step 1.1.4: Run test — expect PASS. Commit.**

### Task 1.2: Attribute classes

**Files:** Create all attribute .cs files, test in attribute tests per PicoJetson pattern

- [ ] **Step 1.2.1: IniSectionAttribute**

```csharp
// PicoIni/src/PicoIni/IniSectionAttribute.cs
namespace PicoIni;
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class)]
public sealed class IniSectionAttribute : Attribute
{
    public string Name { get; }
    public IniSectionAttribute(string name) => Name = name;
}
```

- [ ] **Step 1.2.2: IniKeyAttribute**

```csharp
namespace PicoIni;
[AttributeUsage(AttributeTargets.Property)]
public sealed class IniKeyAttribute : Attribute
{
    public string Name { get; }
    public IniKeyAttribute(string name) => Name = name;
}
```

- [ ] **Step 1.2.3: IniIgnoreAttribute**

```csharp
namespace PicoIni;
[AttributeUsage(AttributeTargets.Property)]
public sealed class IniIgnoreAttribute : Attribute { }
```

- [ ] **Step 1.2.4: IniCommentAttribute**

```csharp
namespace PicoIni;
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class)]
public sealed class IniCommentAttribute : Attribute
{
    public string Text { get; }
    public IniCommentAttribute(string text) => Text = text;
}
```

- [ ] **Step 1.2.5: IniConverterAttribute**

```csharp
namespace PicoIni;
[AttributeUsage(AttributeTargets.Property)]
public sealed class IniConverterAttribute : Attribute
{
    public Type ConverterType { get; }
    public IniConverterAttribute(Type converterType) => ConverterType = converterType;
}
```

- [ ] **Step 1.2.6: IIniConverter interface**

```csharp
namespace PicoIni;
public interface IIniConverter<T>
{
    void Write(IBufferWriter<byte> writer, T value);
    T Read(ReadOnlySpan<byte> value);
}
```

- [ ] **Step 1.2.7: Write attribute tests (one per attribute) — verify construction, property access, AttributeUsage targets. Commit after each.**

---

## Phase 2: IniWriter (TDD)

### Task 2.1: IniWriter basic operations

**Files:** Create: `PicoIni/src/PicoIni/IniWriter.cs`, `PicoIni/tests/PicoIni.Tests/IniWriterTests.cs`

- [ ] **Step 2.1.1: Write failing test — WriteComment**

```csharp
namespace PicoIni.Tests;
public class IniWriterTests
{
    [Test]
    public async Task WriteComment_WritesSemicolonPrefix()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var writer = new IniWriter(buf);
        writer.WriteComment("hello world");
        var output = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(output).IsEqualTo("; hello world" + Environment.NewLine);
    }
}
```

- [ ] **Step 2.1.2: Run — expect FAIL** (IniWriter not defined)

- [ ] **Step 2.1.3: Implement minimal IniWriter**

```csharp
namespace PicoIni;
public ref struct IniWriter
{
    private IBufferWriter<byte> _buffer;
    private long _bytesWritten;

    public IniWriter(IBufferWriter<byte> buffer)
    {
        _buffer = buffer;
        _bytesWritten = 0;
    }

    public long BytesWritten => _bytesWritten;

    public void WriteComment(ReadOnlySpan<char> text)
    {
        WriteRaw("; "u8);
        WriteRaw(Encoding.UTF8.GetBytes(text));
        WriteNewLine();
    }

    private void WriteRaw(ReadOnlySpan<byte> utf8)
    {
        _buffer.Write(utf8);
        _bytesWritten += utf8.Length;
    }

    private void WriteNewLine()
    {
        var s = _buffer.GetSpan(2);
        s[0] = (byte)'\r';
        s[1] = (byte)'\n';
        _buffer.Advance(2);
        _bytesWritten += 2;
    }
}
```

- [ ] **Step 2.1.4: Run — expect PASS**

- [ ] **Step 2.1.5: Add test — WriteCommentWithHash**

```csharp
[Test]
public async Task WriteComment_HashStyle_WritesHashPrefix()
{
    var buf = new ArrayBufferWriter<byte>(256);
    var writer = new IniWriter(buf);
    writer.WriteComment("#", "hash comment");
    var output = Encoding.UTF8.GetString(buf.WrittenSpan);
    await Assert.That(output).IsEqualTo("# hash comment" + Environment.NewLine);
}
```

- [ ] **Step 2.1.6: Implement, run, commit**

### Task 2.2: WriteSection and WriteKeyValue

- [ ] **Step 2.2.1: Write tests for WriteSection, WriteKeyValue**

```csharp
[Test]
public async Task WriteSection_WritesBracketedName()
{
    var buf = new ArrayBufferWriter<byte>(256);
    var writer = new IniWriter(buf);
    writer.WriteSection("Server");
    var output = Encoding.UTF8.GetString(buf.WrittenSpan);
    await Assert.That(output).IsEqualTo("[Server]" + Environment.NewLine);
}

[Test]
public async Task WriteKeyValue_WritesKeyEqualsValue()
{
    var buf = new ArrayBufferWriter<byte>(256);
    var writer = new IniWriter(buf);
    writer.WriteKeyValue("Host", "localhost");
    var output = Encoding.UTF8.GetString(buf.WrittenSpan);
    await Assert.That(output).IsEqualTo("Host = localhost" + Environment.NewLine);
}

[Test]
public async Task WriteKeyValue_IntValue_WritesWithoutQuotes()
{
    var buf = new ArrayBufferWriter<byte>(256);
    var writer = new IniWriter(buf);
    writer.WriteKeyValue("Port", 8080);
    var output = Encoding.UTF8.GetString(buf.WrittenSpan);
    await Assert.That(output).IsEqualTo("Port = 8080" + Environment.NewLine);
}

[Test]
public async Task WriteKeyValue_BoolValue_WritesTrueFalse()
{
    var buf = new ArrayBufferWriter<byte>(256);
    var writer = new IniWriter(buf);
    writer.WriteKeyValue("Enabled", true);
    var output = Encoding.UTF8.GetString(buf.WrittenSpan);
    await Assert.That(output).IsEqualTo("Enabled = true" + Environment.NewLine);
}
```

- [ ] **Step 2.2.2: Implement WriteSection, WriteKeyValue overloads. Run green. Commit.**

### Task 2.3: Value quoting and escaping

- [ ] **Step 2.3.1: Write tests**

```csharp
[Test]
public async Task WriteKeyValue_ValueWithSpaces_QuotesValue()
{
    var buf = new ArrayBufferWriter<byte>(256);
    var writer = new IniWriter(buf);
    writer.WriteKeyValue("Name", "hello world");
    var output = Encoding.UTF8.GetString(buf.WrittenSpan);
    await Assert.That(output).IsEqualTo("Name = \"hello world\"" + Environment.NewLine);
}

[Test]
public async Task WriteKeyValue_ValueWithEquals_QuotesValue()
{
    var buf = new ArrayBufferWriter<byte>(256);
    var writer = new IniWriter(buf);
    writer.WriteKeyValue("Conn", "server=.;db=mydb");
    var output = Encoding.UTF8.GetString(buf.WrittenSpan);
    await Assert.That(output).IsEqualTo("Conn = \"server=.;db=mydb\"" + Environment.NewLine);
}

[Test]
public async Task WriteKeyValue_ValueWithSemicolon_QuotesValue()
{
    var buf = new ArrayBufferWriter<byte>(256);
    var writer = new IniWriter(buf);
    writer.WriteKeyValue("Path", "C:\\dir;D:\\dir");
    var output = Encoding.UTF8.GetString(buf.WrittenSpan);
    await Assert.That(output).IsEqualTo("Path = \"C:\\dir;D:\\dir\"" + Environment.NewLine);
}

[Test]
public async Task WriteKeyValue_ValueWithQuote_EscapesQuote()
{
    var buf = new ArrayBufferWriter<byte>(256);
    var writer = new IniWriter(buf);
    writer.WriteKeyValue("Text", "say \"hello\"");
    var output = Encoding.UTF8.GetString(buf.WrittenSpan);
    await Assert.That(output).IsEqualTo("Text = \"say \\\"hello\\\"\"" + Environment.NewLine);
}
```

- [ ] **Step 2.3.2: Implement quoting logic — value is quoted if it contains `=`, `;`, `#`, leading/trailing whitespace, or double quote. MUST NOT quote values that don't need it. Run green. Commit.**

### Task 2.4: WriteBlankLine and utf8 overloads

- [ ] **Step 2.4.1: Write tests for WriteBlankLine, all utf8 WriteKeyValue overloads. Implement. Commit.**

---

## Phase 3: IniReader (TDD)

### Task 3.1: Basic token reading

**Files:** Create: `PicoIni/src/PicoIni/IniReader.cs`, `PicoIni/tests/PicoIni.Tests/IniReaderTests.cs`

- [ ] **Step 3.1.1: Write failing test — read section**

```csharp
namespace PicoIni.Tests;
public class IniReaderTests
{
    [Test]
    public async Task Read_Section_ReturnsSectionStart()
    {
        var data = "[Server]"u8;
        var reader = new IniReader(data);
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.TokenType).IsEqualTo(IniTokenType.SectionStart);
        await Assert.That(reader.SectionName.SequenceEqual("Server"u8)).IsTrue();
    }

    [Test]
    public async Task Read_KeyValue_ReturnsKeyThenValue()
    {
        var data = "Host = localhost"u8;
        var reader = new IniReader(data);
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.TokenType).IsEqualTo(IniTokenType.Key);
        await Assert.That(reader.Key.SequenceEqual("Host"u8)).IsTrue();
        await Assert.That(reader.ValueSpan.SequenceEqual("localhost"u8)).IsTrue();
    }

    [Test]
    public async Task Read_Comment_ReturnsComment()
    {
        var data = "; this is a comment"u8;
        var reader = new IniReader(data);
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.TokenType).IsEqualTo(IniTokenType.Comment);
        await Assert.That(reader.CommentText.SequenceEqual(" this is a comment"u8)).IsTrue();
    }
}
```

- [ ] **Step 3.1.2: Run — expect FAIL** (IniReader not defined)

- [ ] **Step 3.1.3: Implement IniReader**

```csharp
namespace PicoIni;
public ref struct IniReader
{
    private ReadOnlySpan<byte> _data;
    private int _position;
    private IniTokenType _tokenType;
    private ReadOnlySpan<byte> _sectionName;
    private ReadOnlySpan<byte> _key;
    private ReadOnlySpan<byte> _valueSpan;
    private ReadOnlySpan<byte> _commentText;

    public IniReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
        _tokenType = IniTokenType.None;
        _sectionName = default;
        _key = default;
        _valueSpan = default;
        _commentText = default;
    }

    public IniTokenType TokenType => _tokenType;
    public ReadOnlySpan<byte> SectionName => _sectionName;
    public ReadOnlySpan<byte> Key => _key;
    public ReadOnlySpan<byte> ValueSpan => _valueSpan;
    public ReadOnlySpan<byte> CommentText => _commentText;

    public bool Read()
    {
        SkipWhitespace();
        if (_position >= _data.Length) return false;

        var b = _data[_position];
        switch (b)
        {
            case (byte)'[':
                return ReadSection();
            case (byte)';':
            case (byte)'#':
                return ReadComment();
            default:
                return ReadKeyValue();
        }
    }

    private bool ReadSection()
    {
        _position++; // skip '['
        var start = _position;
        while (_position < _data.Length && _data[_position] != (byte)']') _position++;
        _sectionName = _data[start.._position];
        _position++; // skip ']'
        _tokenType = IniTokenType.SectionStart;
        return true;
    }

    private bool ReadKeyValue()
    {
        // Read key
        var keyStart = _position;
        while (_position < _data.Length && _data[_position] != (byte)'=') _position++;
        _key = _data[keyStart.._position].TrimEnd((byte)' ');
        _position++; // skip '='

        // Skip whitespace after =
        while (_position < _data.Length && _data[_position] == (byte)' ') _position++;

        // Read value
        var valStart = _position;
        while (_position < _data.Length && _data[_position] != (byte)'\n' && _data[_position] != (byte)'\r') _position++;
        _valueSpan = _data[valStart.._position].Trim();
        _tokenType = IniTokenType.Key;
        return true;
    }

    private bool ReadComment()
    {
        _position++; // skip ; or #
        var start = _position;
        while (_position < _data.Length && _data[_position] != (byte)'\n' && _data[_position] != (byte)'\r') _position++;
        _commentText = _data[start.._position];
        _tokenType = IniTokenType.Comment;
        return true;
    }

    private void SkipWhitespace()
    {
        while (_position < _data.Length && (_data[_position] == (byte)' ' || _data[_position] == (byte)'\t')) _position++;
    }
}
```

- [ ] **Step 3.1.4: Run — expect all 3 tests PASS. Commit.**

### Task 3.2: Quoted values, escape sequences, blank lines

- [ ] **Step 3.2.1: Write tests for quoted values**

```csharp
[Test]
public async Task Read_QuotedValue_Unquotes()
{
    var data = "Name = \"hello world\""u8;
    var reader = new IniReader(data);
    await Assert.That(reader.Read()).IsTrue();
    await Assert.That(reader.ValueSpan.SequenceEqual("hello world"u8)).IsTrue();
}

[Test]
public async Task Read_QuotedValue_UnescapesBackslashN()
{
    var data = "Text = \"line1\\nline2\""u8;
    var reader = new IniReader(data);
    await Assert.That(reader.Read()).IsTrue();
    await Assert.That(reader.ValueSpan.SequenceEqual("line1\nline2"u8)).IsTrue();
}

[Test]
public async Task Read_BlankLine_ReturnsBlank()
{
    var data = "\n\n[Section]"u8;
    var reader = new IniReader(data);
    await Assert.That(reader.Read()).IsTrue();
    await Assert.That(reader.TokenType).IsEqualTo(IniTokenType.Blank);
}
```

- [ ] **Step 3.2.2: Implement quoted value parsing with escape support. Add mult-line continuation `\` handling. Run green. Commit.**

### Task 3.3: TryGet methods, case-insensitive matching

- [ ] **Step 3.3.1: Write tests for TryGetInt32, TryGetFloat64, TryGetBool, SectionNameEquals**

```csharp
[Test]
public async Task TryGetInt32_ParsesInteger()
{
    var data = "Count = 42"u8;
    var reader = new IniReader(data);
    reader.Read();
    await Assert.That(reader.TryGetInt32(out var v)).IsTrue();
    await Assert.That(v).IsEqualTo(42);
}

[Test]
public async Task SectionNameEquals_CaseInsensitive()
{
    var data = "[server]"u8;
    var reader = new IniReader(data);
    reader.Read();
    await Assert.That(reader.SectionNameEquals("SERVER")).IsTrue();
    await Assert.That(reader.SectionNameEquals("Server")).IsTrue();
}
```

- [ ] **Step 3.3.2: Implement TryGetInt32/Int64/Float64/Bool and SectionNameEquals. Run green. Commit.**

### Task 3.4: Sequence mode, multi-section, edge cases

- [ ] **Step 3.4.1: Add ReadOnlySequence constructor test**

```csharp
[Test]
public async Task SequenceReader_ParsesMultiSegment()
{
    var seg1 = "[Sect"u8.ToArray();
    var seg2 = "ion]\nKey = Val"u8.ToArray();
    var seq = new ReadOnlySequence<byte>(seg1, seg2);
    var reader = new IniReader(seq);
    await Assert.That(reader.Read()).IsTrue();
    await Assert.That(reader.SectionName.SequenceEqual("Section"u8)).IsTrue();
}
```

- [ ] **Step 3.4.2: Implement sequence mode path. Handle multi-segment boundaries in reader. Commit.**

- [ ] **Step 3.4.3: Write fuzzing tests — random valid INI input, verify no crash. Commit.**

---

## Phase 4: IniSerializer + IIniConverter (TDD)

### Task 4.1: Static facade

**Files:** Create: `PicoIni/src/PicoIni/IniSerializer.cs`, `PicoIni/tests/PicoIni.Tests/IniSerializerTests.cs`

- [ ] **Step 4.1.1: Define test model**

```csharp
namespace PicoIni.Tests;
public class ServerConfig
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
}

public class AppConfig
{
    public string Title { get; set; } = "";
    public ServerConfig Server { get; set; } = new();
}
```

- [ ] **Step 4.1.2: Write manual serializer/deserializer structs (golden reference)**

```csharp
private readonly struct AppConfigIniSerializer : ISerializer<AppConfig>
{
    public void Serialize(IBufferWriter<byte> writer, AppConfig value)
    {
        var iw = new IniWriter(writer);
        iw.WriteKeyValue("Title", value.Title);
        iw.WriteBlankLine();
        iw.WriteSection("Server");
        iw.WriteKeyValue("Host", value.Server.Host);
        iw.WriteKeyValue("Port", value.Server.Port);
    }
}

private readonly struct AppConfigIniDeserializer : IDeserializer<AppConfig>
{
    public AppConfig Deserialize(ReadOnlySpan<byte> data)
    {
        var reader = new IniReader(data);
        var config = new AppConfig();
        while (reader.Read())
        {
            if (reader.TokenType == IniTokenType.Key && reader.Key.SequenceEqual("Title"u8))
                config.Title = Encoding.UTF8.GetString(reader.ValueSpan);
            else if (reader.TokenType == IniTokenType.SectionStart && reader.SectionNameEquals("Server"))
            {
                config.Server = new ServerConfig();
                while (reader.Read() && reader.TokenType != IniTokenType.SectionStart)
                {
                    if (reader.TokenType != IniTokenType.Key) continue;
                    if (reader.Key.SequenceEqual("Host"u8))
                        config.Server.Host = Encoding.UTF8.GetString(reader.ValueSpan);
                    else if (reader.Key.SequenceEqual("Port"u8))
                    { reader.TryGetInt32(out var p); config.Server.Port = p; }
                }
            }
        }
        return config;
    }
}
```

- [ ] **Step 4.1.3: Test round-trip with manual impls**

```csharp
[Test]
public async Task Manual_RoundTrip_Works()
{
    var config = new AppConfig { Title = "MyApp", Server = new ServerConfig { Host = "localhost", Port = 8080 } };
    var ser = new AppConfigIniSerializer();
    var deser = new AppConfigIniDeserializer();
    var buf = new ArrayBufferWriter<byte>(256);
    ser.Serialize(buf, config);
    var result = deser.Deserialize(buf.WrittenSpan);
    await Assert.That(result.Title).IsEqualTo("MyApp");
    await Assert.That(result.Server.Host).IsEqualTo("localhost");
    await Assert.That(result.Server.Port).IsEqualTo(8080);
}
```

- [ ] **Step 4.1.4: Implement IniSerializer static facade**

```csharp
namespace PicoIni;
public static partial class IniSerializer
{
    private static readonly ConcurrentDictionary<Type, object> _serializers = new();
    private static readonly ConcurrentDictionary<Type, object> _deserializers = new();

    public static void Register<T>(ISerializer<T> serializer, IDeserializer<T> deserializer)
    {
        _serializers[typeof(T)] = serializer;
        _deserializers[typeof(T)] = deserializer;
    }

    public static byte[] SerializeToUtf8Bytes<T>(T value)
    {
        if (_serializers.TryGetValue(typeof(T), out var s))
        {
            var writer = new ArrayBufferWriter<byte>();
            ((ISerializer<T>)s).Serialize(writer, value);
            return writer.WrittenSpan.ToArray();
        }
        ThrowNoSerializer<T>();
        return default;
    }

    public static string Serialize<T>(T value)
    {
        var bytes = SerializeToUtf8Bytes(value);
        return Encoding.UTF8.GetString(bytes);
    }

    public static T? Deserialize<T>(ReadOnlySpan<byte> data)
    {
        if (_deserializers.TryGetValue(typeof(T), out var d))
            return ((IDeserializer<T>)d).Deserialize(data);
        ThrowNoSerializer<T>();
        return default;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowNoSerializer<T>() =>
        throw new InvalidOperationException(
            $"No serializer/deserializer registered for {typeof(T)}. " +
            "Ensure PicoIni.Gen is referenced and the type is used with IniSerializer methods.");
}
```

- [ ] **Step 4.1.5: Test registration and static API. Run green. Commit.**

### Task 4.2: IniConverter tests

- [ ] **Step 4.2.1: Write custom converter test**

```csharp
public class UpperCaseConverter : IIniConverter<string>
{
    public void Write(IBufferWriter<byte> writer, string value)
    {
        var iw = new IniWriter(writer);
        iw.WriteKeyValueRaw(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
    }
    public string Read(ReadOnlySpan<byte> value) =>
        Encoding.UTF8.GetString(value).ToUpperInvariant();
}
```

- [ ] **Step 4.2.2: Test converter round-trip. Implement missing WriteKeyValueRaw in IniWriter. Run green. Commit.**

---

## Phase 5: PicoIni.Gen Source Generator (TDD)

### Task 5.1: TypeKindResolver

**Files:** Create: `PicoIni/src/PicoIni.Gen/TypeKindResolver.cs`

- Copy from `PicoJetson.Gen/TypeKindResolver.cs` with namespace change to `PicoIni.Gen`. Same logic — INI supports the same types.
- Test by verifying generated code produces correct type kinds.

- [ ] **Step 5.1.1: Create TypeKindResolver. Commit.**

### Task 5.2: Generator structure

**Files:** Create: `PicoIni/src/PicoIni.Gen/IniSerializerGenerator.cs`

- Follow `PicoJetson.Gen/JsonSerializerGenerator.cs` pattern exactly:
  - `IIncrementalGenerator`
  - Detect `IniSerializer.Serialize<T>()` / `SerializeToUtf8Bytes<T>()` / `Deserialize<T>()` calls
  - Extract public readable+writable properties
  - Generate `file readonly struct` serializers + deserializers
  - Auto-registration via `[ModuleInitializer]`

**Key difference from PicoJetson:** Section-per-property mapping:
- Object-typed properties → `writer.WriteSection(name)` before their properties
- Simple properties → `writer.WriteKeyValue(name, value)`
- Top-level simple properties → header-level `key = value`
- Collections → comma-separated `key = value1, value2, value3`

- [ ] **Step 5.2.1: Write golden-reference tests for generator**

Create model types and manual serializer/deserializer in test file `GeneratorTests.cs`. These serve as the expected output reference.

```csharp
// Test models
public class SimpleModel { public string Name { get; set; } = ""; public int Age { get; set; } }
public class SectionedModel { public string Title { get; set; } = ""; public SubConfig Sub { get; set; } = new(); }
public class SubConfig { public string Host { get; set; } = ""; public int Port { get; set; } }
public class NullableModel { public int? Optional { get; set; } }
public class ListModel { public List<string> Tags { get; set; } = new(); }
public class ArrayModel { public int[] Scores { get; set; } = []; }
public class DictModel { public Dictionary<string, string> Meta { get; set; } = new(); }
```

- [ ] **Step 5.2.2: Test — manual impl round-trip for all model types. These pass before generator exists. Run green. Commit.**

- [ ] **Step 5.2.3: Test — generated round-trip (these FAIL until generator exists)**

```csharp
[Test]
public async Task Generated_SimpleModel_RoundTrip()
{
    var model = new SimpleModel { Name = "Test", Age = 25 };
    var ini = IniSerializer.Serialize(model);
    var result = IniSerializer.Deserialize<SimpleModel>(Encoding.UTF8.GetBytes(ini));
    await Assert.That(result?.Name).IsEqualTo("Test");
    await Assert.That(result?.Age).IsEqualTo(25);
}
```

- [ ] **Step 5.2.4: Implement generator — detect calls, generate code. Run green. Commit.**

### Task 5.3: Generator — section mapping, collections, edge cases

- [ ] **Step 5.3.1: Tests for nested sections, collections, nullables, enums, DateTime, comment attributes. Each: write test → verify red → implement → verify green → commit.**

---

## Phase 6: Benchmarks, Sample, CI Integration

### Task 6.1: Benchmarks

**Files:** Create: `PicoIni/benchmarks/PicoIni.Benchmarks/Program.cs`

- [ ] **Step 6.1.1: Create benchmark comparing serialization/deserialization with comparable approaches. Commit.**

### Task 6.2: Sample

**Files:** Create: `PicoIni/samples/PicoIni.Sample/Program.cs`

- [ ] **Step 6.2.1: Create sample demonstrating all features. Commit.**

### Task 6.3: CI Integration

**Files:** Modify: `.github/workflows/ci.yml`, `.github/workflows/release.yml`

- [ ] **Step 6.3.1: Add PicoIni to CI build/test matrix. Commit.**
- [ ] **Step 6.3.2: Add PicoIni.Gen and PicoIni to release pipeline. Commit.**

---

## Phase 7: Final Verification

- [ ] **Step 7.1: Full solution build** — `dotnet build` the entire solution
- [ ] **Step 7.2: Run all tests** — `dotnet test --verbosity normal`
- [ ] **Step 7.3: LSP diagnostics** — check all new files for errors/warnings
- [ ] **Step 7.4: AOT publish** — `dotnet publish PicoIni/samples/PicoIni.Sample -r win-x64 --self-contained`
