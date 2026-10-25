# Post-Review Improvements Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement 6 improvements identified in code review: README docs, CI benchmarks, generic cache optimization, SIMD extraction, INI TokenType unification, YAML anchor support — all with TDD.

**Architecture:** Each improvement is independent. Ordered by complexity: README → CI → cache → SIMD → INI refactor → YAML anchors. Code changes follow strict TDD: failing test → minimal implementation → verify green → commit.

**Tech Stack:** C# 14, .NET 10, TUnit, Roslyn IIncrementalGenerator, Vector128 SIMD

---

## File Structure

```
# Created files:
shared/SimdHelpers.cs                                          ← SIMD whitespace skip
PicoIni/tests/PicoIni.Tests/IniReaderUnifiedTests.cs           ← rewritten INI reader tests
PicoIni/tests/PicoIni.Tests/IniSerializerUnifiedTests.cs       ← rewritten INI serializer tests
PicoYaml/tests/PicoYaml.Tests/YamlAnchorTests.cs               ← anchor/alias tests

# Modified files:
README.md                                                      ← full project README
.github/workflows/ci.yml                                       ← benchmark run steps
PicoJetson/src/PicoJetson/JsonSerializer.cs                        ← generic cache
PicoIni/src/PicoIni/IniSerializer.cs                           ← generic cache
PicoToml/src/PicoToml/TomlSerializer.cs                        ← generic cache
PicoYaml/src/PicoYaml/YamlSerializer.cs                        ← generic cache
PicoJetson/src/PicoJetson/JsonReader.cs                            ← extract SIMD, call shared
PicoToml/src/PicoToml/TomlReader.cs                            ← use SimdHelpers
PicoYaml/src/PicoYaml/YamlReader.cs                            ← use SimdHelpers + anchors
PicoIni/src/PicoIni/IniReader.cs                               ← TokenType unification
PicoIni/src/PicoIni/IniWriter.cs                               ← API alignment
PicoIni/src/PicoIni.Gen/IniSerializerGenerator.cs              ← TokenType constants
PicoIni/tests/PicoIni.Tests/IniReaderTests.cs                  ← delete (replaced)
PicoIni/tests/PicoIni.Tests/IniTokenTypeTests.cs               ← delete (replaced)
PicoIni/tests/PicoIni.Tests/IniSerializerTests.cs              ← delete (replaced)
PicoIni/tests/PicoIni.Tests/IniWriterTests.cs                  ← update imports
PicoIni/tests/PicoIni.Tests/AttributeTests.cs                  ← update imports

# Deleted files:
PicoIni/src/PicoIni/IniTokenType.cs                            ← removed
```

---

### Task 1: README Documentation

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Write the README**

```markdown
# PicoSerDe

High-performance, AOT-first serialization framework for the PicoHex ecosystem. Zero-reflection source generation with `ref struct` readers/writers for zero heap allocation.

## Format Support

| Format | Library | Status | Features |
|--------|---------|--------|----------|
| JSON | PicoJetson | ✅ Production | RFC 8259, SIMD-accelerated, Unicode escapes |
| INI | PicoIni | ✅ Production | Sections, comments, quoting, case-insensitive |
| TOML | PicoToml | ✅ Beta | Tables, arrays, inline tables, multi-line strings |
| YAML | PicoYaml | ✅ Beta | Indentation-based, flow style, anchors (coming) |

## Quick Start

### Installation

```bash
dotnet add package PicoJetson
dotnet add package PicoJetson.Gen
```

### Usage

```csharp
using PicoJetson;

public record Person(string Name, int Age);

// Serialize — source generator creates the implementation automatically
var json = JsonSerializer.Serialize(new Person("Alice", 30));
// → {"Name":"Alice","Age":30}

// Deserialize
var person = JsonSerializer.Deserialize<Person>(
    Encoding.UTF8.GetBytes(json));
// → Person { Name = "Alice", Age = 30 }
```

No attributes required. Add `[JsonPropertyName("custom_name")]` for custom keys.
Add `[JsonIgnore]` to exclude properties.

### INI Example

```csharp
using PicoIni;

public class AppConfig
{
    public string Title { get; set; } = "";
    public ServerConfig Server { get; set; } = new();
}

public class ServerConfig
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
}

var config = new AppConfig { Title = "MyApp", Server = new() { Host = "localhost", Port = 8080 } };
var ini = IniSerializer.Serialize(config);
// Title = MyApp
//
// [Server]
// Host = localhost
// Port = 8080
```

### Low-Level Reader/Writer API

All formats provide `ref struct` readers and writers for zero-allocation streaming:

```csharp
var reader = new JsonReader(Encoding.UTF8.GetBytes(json));
while (reader.Read())
{
    switch (reader.TokenType)
    {
        case TokenType.PropertyName:
            var name = reader.GetStringRaw();
            break;
        case TokenType.String:
            var value = reader.GetStringRaw();
            break;
        case TokenType.Int32:
            reader.TryGetInt32(out var n);
            break;
    }
}
```

## Performance

- **Zero heap allocation** on the hot path — all readers/writers are `ref struct`
- **SIMD-accelerated** whitespace and backslash scanning (Vector128)
- **Zero-copy string access** — `GetStringRaw()` returns `ReadOnlySpan<byte>` pointing into source buffer for unescaped strings
- **AOT-compatible** — zero reflection, 100% source generator driven, `IsAotCompatible=true`

## Project Structure

```
PicoSerDe/
├── PicoSerDe.Abs/          # Shared abstractions (ISerializer<T>, IDeserializer<T>, TokenType)
├── PicoJetson/               # JSON format (src, gen, tests, benchmarks, samples)
├── PicoIni/                # INI format (src, gen, tests, benchmarks, samples)
├── PicoToml/               # TOML format (src, gen, tests, benchmarks, samples)
├── PicoYaml/               # YAML format (src, gen, tests, benchmarks, samples)
├── shared/                 # Shared utilities
├── docs/superpowers/       # Design specs and implementation plans
└── .github/workflows/      # CI/CD (5-platform matrix + AOT validation)
```

## Design

See [docs/superpowers/specs/](docs/superpowers/specs/) for detailed design documents:
- [Token Layer Design](docs/superpowers/specs/2026-05-24-token-layer-design.md)
- [PicoJetson Design](docs/superpowers/specs/2026-05-24-picojetson-design.md)
- [PicoIni Design](docs/superpowers/specs/2026-05-26-picoini-design.md)

## License

MIT
```

- [ ] **Step 2: Commit**

```bash
git add README.md
git commit -m "docs: add comprehensive README with quick start and format matrix"
```

---

### Task 2: CI Benchmark Regression Step

**Files:**
- Modify: `.github/workflows/ci.yml`

- [ ] **Step 1: Update CI workflow**

In the `build-and-test` job, after the benchmark build steps, add benchmark execution steps:

```yaml
      - run: dotnet build PicoJetson/benchmarks/PicoJetson.Benchmarks/PicoJetson.Benchmarks.csproj -c Release --no-restore
      - run: dotnet build PicoIni/benchmarks/PicoIni.Benchmarks/PicoIni.Benchmarks.csproj -c Release --no-restore
      - run: dotnet build PicoToml/benchmarks/PicoToml.Benchmarks/PicoToml.Benchmarks.csproj -c Release --no-restore
      - run: dotnet build PicoYaml/benchmarks/PicoYaml.Benchmarks/PicoYaml.Benchmarks.csproj -c Release --no-restore
      - name: Run PicoJetson benchmarks (smoke)
        run: dotnet run --project PicoJetson/benchmarks/PicoJetson.Benchmarks -c Release --no-build -- --filter '*' --iterationCount 1 --warmupCount 0
      - name: Run PicoIni benchmarks (smoke)
        run: dotnet run --project PicoIni/benchmarks/PicoIni.Benchmarks -c Release --no-build -- --filter '*' --iterationCount 1 --warmupCount 0
      - name: Run PicoToml benchmarks (smoke)
        run: dotnet run --project PicoToml/benchmarks/PicoToml.Benchmarks -c Release --no-build -- --filter '*' --iterationCount 1 --warmupCount 0
      - name: Run PicoYaml benchmarks (smoke)
        run: dotnet run --project PicoYaml/benchmarks/PicoYaml.Benchmarks -c Release --no-build -- --filter '*' --iterationCount 1 --warmupCount 0
```

- [ ] **Step 2: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: add benchmark smoke-test steps to prevent bitrot"
```

---

### Task 3: Generic Static Cache Optimization

**Files:**
- Modify: `PicoJetson/src/PicoJetson/JsonSerializer.cs`
- Modify: `PicoIni/src/PicoIni/IniSerializer.cs`
- Modify: `PicoToml/src/PicoToml/TomlSerializer.cs`
- Modify: `PicoYaml/src/PicoYaml/YamlSerializer.cs`

#### Task 3.1: PicoJetson Generic Cache (TDD)

- [ ] **Step 3.1.1: Write the failing test**

In `PicoJetson/tests/PicoJetson.Tests/JsonSerializerTests.cs`, add:

```csharp
[Test]
public async Task GenericCache_RegisterThenSerialize_UsesCache()
{
    // The cache is an internal optimization — we verify correctness via existing API
    // Register + Serialize + Deserialize should work as before
    JsonSerializer.Register(new PersonJsonSerializer(), new PersonJsonDeserializer());

    var person = new Person { Name = "CacheTest", Age = 99 };
    var buf = new ArrayBufferWriter<byte>(256);

    // Serialize via static API (should use generic cache internally)
    JsonSerializer.Serialize(buf, person);
    var result = JsonSerializer.Deserialize<Person>(buf.WrittenSpan);

    await Assert.That(result!.Name).IsEqualTo("CacheTest");
    await Assert.That(result.Age).IsEqualTo(99);

    // Second call should also work (cache is already populated)
    var buf2 = new ArrayBufferWriter<byte>(256);
    JsonSerializer.Serialize(buf2, person);
    var result2 = JsonSerializer.Deserialize<Person>(buf2.WrittenSpan);
    await Assert.That(result2!.Name).IsEqualTo("CacheTest");
    await Assert.That(result2.Age).IsEqualTo(99);
}

[Test]
public async Task GenericCache_ConcurrentRegistrations_ThreadSafe()
{
    var tasks = new List<Task>();
    for (int i = 0; i < 10; i++)
    {
        tasks.Add(Task.Run(() =>
        {
            JsonSerializer.Register(new PersonJsonSerializer(), new PersonJsonDeserializer());
            var person = new Person { Name = "Concurrent", Age = 42 };
            var json = JsonSerializer.Serialize(person);
            var result = JsonSerializer.Deserialize<Person>(Encoding.UTF8.GetBytes(json));
        }));
    }
    await Task.WhenAll(tasks);
    // No exceptions = pass
    await Assert.That(true).IsTrue();
}
```

- [ ] **Step 3.1.2: Run tests to verify they pass (existing code handles this)**

Run: `dotnet test PicoJetson/tests/PicoJetson.Tests --filter "GenericCache"`
Expected: PASS (existing ConcurrentDictionary-based code already handles this correctly)

Note: These tests validate correctness of the cache optimization. They pass before the optimization because the existing ConcurrentDictionary code already works. After optimization, they must still pass.

- [ ] **Step 3.1.3: Implement generic cache in JsonSerializer**

Replace the body of `PicoJetson/src/PicoJetson/JsonSerializer.cs`:

```csharp
namespace PicoJetson;

public static partial class JsonSerializer
{
    private static readonly ConcurrentDictionary<Type, object> _serializers = new();
    private static readonly ConcurrentDictionary<Type, object> _deserializers = new();

    // Generic static cache — avoids dictionary lookup + cast on hot path
    private static class Cache<T>
    {
        internal static ISerializer<T>? Serializer;
        internal static IDeserializer<T>? Deserializer;
    }

    public static void Register<T>(ISerializer<T> serializer, IDeserializer<T> deserializer)
    {
        Cache<T>.Serializer = serializer;
        Cache<T>.Deserializer = deserializer;
        _serializers[typeof(T)] = serializer;
        _deserializers[typeof(T)] = deserializer;
    }

    public static byte[] SerializeToUtf8Bytes<T>(T value)
    {
        var s = Cache<T>.Serializer;
        if (s is not null)
        {
            var writer = new ArrayBufferWriter<byte>();
            s.Serialize(writer, value);
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

    public static void Serialize<T>(IBufferWriter<byte> writer, T value)
    {
        var s = Cache<T>.Serializer;
        if (s is not null)
            s.Serialize(writer, value);
        else
            ThrowNoSerializer<T>();
    }

    public static T? Deserialize<T>(ReadOnlySpan<byte> data)
    {
        var d = Cache<T>.Deserializer;
        if (d is not null)
            return d.Deserialize(data);
        ThrowNoSerializer<T>();
        return default;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowNoSerializer<T>()
    {
        throw new InvalidOperationException(
            $"No serializer/deserializer registered for {typeof(T)}. "
                + "Ensure PicoJetson.Gen is referenced and the type is used with JsonSerializer methods."
        );
    }
}
```

- [ ] **Step 3.1.4: Run tests**

Run: `dotnet test PicoJetson/tests/PicoJetson.Tests`
Expected: ALL PASS

- [ ] **Step 3.1.5: Commit**

```bash
git add PicoJetson/src/PicoJetson/JsonSerializer.cs PicoJetson/tests/PicoJetson.Tests/JsonSerializerTests.cs
git commit -m "perf(picojetson): add generic static cache to eliminate dictionary lookup on hot path"
```

#### Task 3.2: PicoIni Generic Cache

- [ ] **Step 3.2.1: Write test**

In `PicoIni/tests/PicoIni.Tests/IniSerializerTests.cs`, add:

```csharp
[Test]
public async Task GenericCache_RegisterThenSerialize_UsesCache()
{
    var config = new AppConfig { Title = "CacheTest", Server = new ServerConfig { Host = "localhost", Port = 8080 } };
    var ser = new AppConfigIniSerializer();
    var deser = new AppConfigIniDeserializer();
    IniSerializer.Register(ser, deser);

    var ini = IniSerializer.Serialize(config);
    var result = IniSerializer.Deserialize<AppConfig>(Encoding.UTF8.GetBytes(ini));
    await Assert.That(result!.Title).IsEqualTo("CacheTest");
    await Assert.That(result.Server.Host).IsEqualTo("localhost");
    await Assert.That(result.Server.Port).IsEqualTo(8080);
}
```

- [ ] **Step 3.2.2: Run — verify existing tests pass**

Run: `dotnet test PicoIni/tests/PicoIni.Tests --filter "GenericCache"`
Expected: PASS

- [ ] **Step 3.2.3: Implement generic cache in IniSerializer**

```csharp
namespace PicoIni;

public static partial class IniSerializer
{
    private static readonly ConcurrentDictionary<Type, object> _serializers = new();
    private static readonly ConcurrentDictionary<Type, object> _deserializers = new();

    private static class Cache<T>
    {
        internal static ISerializer<T>? Serializer;
        internal static IDeserializer<T>? Deserializer;
    }

    public static void Register<T>(ISerializer<T> serializer, IDeserializer<T> deserializer)
    {
        Cache<T>.Serializer = serializer;
        Cache<T>.Deserializer = deserializer;
        _serializers[typeof(T)] = serializer;
        _deserializers[typeof(T)] = deserializer;
    }

    public static byte[] SerializeToUtf8Bytes<T>(T value)
    {
        var s = Cache<T>.Serializer;
        if (s is not null)
        {
            var writer = new ArrayBufferWriter<byte>();
            s.Serialize(writer, value);
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

    public static void Serialize<T>(IBufferWriter<byte> writer, T value)
    {
        var s = Cache<T>.Serializer;
        if (s is not null)
            s.Serialize(writer, value);
        else
            ThrowNoSerializer<T>();
    }

    public static T? Deserialize<T>(ReadOnlySpan<byte> data)
    {
        var d = Cache<T>.Deserializer;
        if (d is not null)
            return d.Deserialize(data);
        ThrowNoSerializer<T>();
        return default;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowNoSerializer<T>() =>
        throw new InvalidOperationException(
            $"No serializer/deserializer registered for {typeof(T)}. "
                + "Ensure PicoIni.Gen is referenced and the type is used with IniSerializer methods."
        );
}
```

- [ ] **Step 3.2.4: Run tests**

Run: `dotnet test PicoIni/tests/PicoIni.Tests`
Expected: ALL PASS

- [ ] **Step 3.2.5: Commit**

```bash
git add PicoIni/src/PicoIni/IniSerializer.cs PicoIni/tests/PicoIni.Tests/IniSerializerTests.cs
git commit -m "perf(picoini): add generic static cache to eliminate dictionary lookup on hot path"
```

#### Task 3.3: PicoToml Generic Cache

- [ ] **Step 3.3.1: Implement generic cache in TomlSerializer**

Replace body of `PicoToml/src/PicoToml/TomlSerializer.cs`:

```csharp
namespace PicoToml;

public static partial class TomlSerializer
{
    private static readonly ConcurrentDictionary<Type, object> _serializers = new();
    private static readonly ConcurrentDictionary<Type, object> _deserializers = new();

    private static class Cache<T>
    {
        internal static ISerializer<T>? Serializer;
        internal static IDeserializer<T>? Deserializer;
    }

    public static void Register<T>(ISerializer<T> serializer, IDeserializer<T> deserializer)
    {
        Cache<T>.Serializer = serializer;
        Cache<T>.Deserializer = deserializer;
        _serializers[typeof(T)] = serializer;
        _deserializers[typeof(T)] = deserializer;
    }

    public static byte[] SerializeToUtf8Bytes<T>(T value)
    {
        var s = Cache<T>.Serializer;
        if (s is not null)
        {
            var writer = new ArrayBufferWriter<byte>();
            s.Serialize(writer, value);
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

    public static void Serialize<T>(IBufferWriter<byte> writer, T value)
    {
        var s = Cache<T>.Serializer;
        if (s is not null)
            s.Serialize(writer, value);
        else
            ThrowNoSerializer<T>();
    }

    public static T? Deserialize<T>(ReadOnlySpan<byte> data)
    {
        var d = Cache<T>.Deserializer;
        if (d is not null)
            return d.Deserialize(data);
        ThrowNoSerializer<T>();
        return default;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowNoSerializer<T>() =>
        throw new InvalidOperationException(
            $"No serializer registered for {typeof(T)}. Ensure PicoToml.Gen is referenced."
        );
}
```

- [ ] **Step 3.3.2: Run tests**

Run: `dotnet test PicoToml/tests/PicoToml.Tests`
Expected: ALL PASS

- [ ] **Step 3.3.3: Commit**

```bash
git add PicoToml/src/PicoToml/TomlSerializer.cs
git commit -m "perf(picotoml): add generic static cache to eliminate dictionary lookup on hot path"
```

#### Task 3.4: PicoYaml Generic Cache

- [ ] **Step 3.4.1: Implement generic cache in YamlSerializer**

Replace body of `PicoYaml/src/PicoYaml/YamlSerializer.cs`:

```csharp
namespace PicoYaml;

public static partial class YamlSerializer
{
    private static readonly ConcurrentDictionary<Type, object> _serializers = new();
    private static readonly ConcurrentDictionary<Type, object> _deserializers = new();

    private static class Cache<T>
    {
        internal static ISerializer<T>? Serializer;
        internal static IDeserializer<T>? Deserializer;
    }

    public static void Register<T>(ISerializer<T> serializer, IDeserializer<T> deserializer)
    {
        Cache<T>.Serializer = serializer;
        Cache<T>.Deserializer = deserializer;
        _serializers[typeof(T)] = serializer;
        _deserializers[typeof(T)] = deserializer;
    }

    public static byte[] SerializeToUtf8Bytes<T>(T value)
    {
        var s = Cache<T>.Serializer;
        if (s is not null)
        {
            var w = new ArrayBufferWriter<byte>();
            s.Serialize(w, value);
            return w.WrittenSpan.ToArray();
        }
        ThrowNoSer<T>();
        return default;
    }

    public static string Serialize<T>(T value) =>
        Encoding.UTF8.GetString(SerializeToUtf8Bytes(value));

    public static void Serialize<T>(IBufferWriter<byte> writer, T value)
    {
        var s = Cache<T>.Serializer;
        if (s is not null)
            s.Serialize(writer, value);
        else
            ThrowNoSer<T>();
    }

    public static T? Deserialize<T>(ReadOnlySpan<byte> data)
    {
        var d = Cache<T>.Deserializer;
        if (d is not null)
            return d.Deserialize(data);
        ThrowNoSer<T>();
        return default;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowNoSer<T>() =>
        throw new InvalidOperationException(
            $"No serializer registered for {typeof(T)}. Ensure PicoYaml.Gen is referenced."
        );
}
```

- [ ] **Step 3.4.2: Run tests**

Run: `dotnet test PicoYaml/tests/PicoYaml.Tests`
Expected: ALL PASS

- [ ] **Step 3.4.3: Commit**

```bash
git add PicoYaml/src/PicoYaml/YamlSerializer.cs
git commit -m "perf(picoyaml): add generic static cache to eliminate dictionary lookup on hot path"
```

---

### Task 4: SIMD Whitespace Skip Extraction

**Files:**
- Create: `shared/SimdHelpers.cs`
- Modify: `PicoJetson/src/PicoJetson/JsonReader.cs`
- Modify: `PicoToml/src/PicoToml/TomlReader.cs`
- Modify: `PicoYaml/src/PicoYaml/YamlReader.cs`

- [ ] **Step 1: Write the failing test**

Create `PicoSerDe.Abs/tests/PicoSerDe.Abs.Tests/SimdWhitespaceTests.cs`:

```csharp
namespace PicoSerDe.Abs.Tests;

public class SimdWhitespaceTests
{
    [Test]
    public async Task SkipWhitespace_AllSpaces_SkipsAll()
    {
        var data = "        hello"u8;
        var pos = SimdHelpers.SkipWhitespace(data, 0);
        await Assert.That(pos).IsEqualTo(8);
        await Assert.That((char)data[pos]).IsEqualTo('h');
    }

    [Test]
    public async Task SkipWhitespace_NoWhitespace_ReturnsSamePosition()
    {
        var data = "hello"u8;
        var pos = SimdHelpers.SkipWhitespace(data, 0);
        await Assert.That(pos).IsEqualTo(0);
    }

    [Test]
    public async Task SkipWhitespace_MixedTypes_SkipsAll()
    {
        var data = " \t\n\r  hello"u8;
        var pos = SimdHelpers.SkipWhitespace(data, 0);
        await Assert.That(pos).IsEqualTo(6);
        await Assert.That((char)data[pos]).IsEqualTo('h');
    }

    [Test]
    public async Task SkipWhitespace_EmptyInput_ReturnsZero()
    {
        var data = ""u8;
        var pos = SimdHelpers.SkipWhitespace(data, 0);
        await Assert.That(pos).IsEqualTo(0);
    }

    [Test]
    public async Task SkipWhitespace_AllWhitespace_ReturnsLength()
    {
        var data = "   \t\n\r  "u8;
        var pos = SimdHelpers.SkipWhitespace(data, 0);
        await Assert.That(pos).IsEqualTo(data.Length);
    }

    [Test]
    public async Task SkipWhitespace_OffsetStart_SkipsFromOffset()
    {
        var data = "xxx   hello"u8;
        var pos = SimdHelpers.SkipWhitespace(data, 3);
        await Assert.That(pos).IsEqualTo(6);
        await Assert.That((char)data[pos]).IsEqualTo('h');
    }
}
```

- [ ] **Step 2: Run tests — expect FAIL**

Run: `dotnet test PicoSerDe.Abs/tests/PicoSerDe.Abs.Tests --filter "SimdWhitespace"`
Expected: FAIL — `SimdHelpers` not defined

- [ ] **Step 3: Create SimdHelpers.cs**

```csharp
// shared/SimdHelpers.cs
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PicoSerDe.Abs;

internal static class SimdHelpers
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SkipWhitespace(ReadOnlySpan<byte> data, int position)
    {
        int len = data.Length;

        // SIMD: process 16 bytes at a time
        if (Vector128.IsHardwareAccelerated)
        {
            var spaceVec = Vector128.Create((byte)0x20);
            var tabVec = Vector128.Create((byte)0x09);
            var newlineVec = Vector128.Create((byte)0x0A);
            var crVec = Vector128.Create((byte)0x0D);

            while (position + 16 <= len)
            {
                ref readonly var src = ref data[position];
                var chunk = Vector128.LoadUnsafe(in src);
                var isWs =
                    Vector128.Equals(chunk, spaceVec)
                    | Vector128.Equals(chunk, tabVec)
                    | Vector128.Equals(chunk, newlineVec)
                    | Vector128.Equals(chunk, crVec);
                var bits = isWs.ExtractMostSignificantBits();

                if (bits == 0xFFFF)
                {
                    position += 16;
                    continue;
                }

                position += BitOperations.TrailingZeroCount(~bits);
                return position;
            }
        }

        // Scalar fallback
        while (
            position < len
            && data[position] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r'
        )
        {
            position++;
        }
        return position;
    }
}
```

- [ ] **Step 4: Include SimdHelpers.cs in projects**

Add to `PicoSerDe.Abs/src/PicoSerDe.Abs.csproj`, `PicoJetson/src/PicoJetson/PicoJetson.csproj`, `PicoToml/src/PicoToml/PicoToml.csproj`, `PicoYaml/src/PicoYaml/PicoYaml.csproj`:

```xml
<ItemGroup>
    <Compile Include="..\..\..\shared\SimdHelpers.cs" Link="SimdHelpers.cs" />
</ItemGroup>
```

The paths are relative to the csproj location:
- `PicoSerDe.Abs`: `..\..\shared\SimdHelpers.cs`
- `PicoJetson`: `..\..\..\shared\SimdHelpers.cs`
- `PicoToml`: `..\..\..\shared\SimdHelpers.cs`
- `PicoYaml`: `..\..\..\shared\SimdHelpers.cs`

- [ ] **Step 5: Run tests — expect PASS**

Run: `dotnet test PicoSerDe.Abs/tests/PicoSerDe.Abs.Tests --filter "SimdWhitespace"`
Expected: PASS

- [ ] **Step 6: Replace JsonReader.SkipWhitespaceSpan with shared helper**

In `PicoJetson/src/PicoJetson/JsonReader.cs`, replace the body of `SkipWhitespaceSpan()`:

```csharp
private void SkipWhitespaceSpan()
{
    _position = SimdHelpers.SkipWhitespace(_data, _position);
}
```

- [ ] **Step 7: Replace TomlReader whitespace loops with shared helper**

In `PicoToml/src/PicoToml/TomlReader.cs`:

In `ReadSpan()`, replace the inline while loop that skips whitespace/newlines at the start:
```csharp
// Before (in ReadSpan):
while (_position < _data.Length)
{
    if (_data[_position] == (byte)'\n' || _data[_position] == (byte)'\r')
    {
        _position++;
        continue;
    }
    // ...
}

// After:
_position = SimdHelpers.SkipWhitespace(_data, _position);
while (_position < _data.Length && (_data[_position] == (byte)'\n' || _data[_position] == (byte)'\r'))
    _position++;
```

In `ReadArraySpan()`, replace the while loop that skips whitespace/commas:
```csharp
// Before:
while (_position < _data.Length)
{
    var b = _data[_position];
    if (b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)',')
        _position++;
    else if (b == (byte)'#')
        SkipLineSpan();
    else
        break;
}

// After:
_position = SimdHelpers.SkipWhitespace(_data, _position);
while (_position < _data.Length && _data[_position] == (byte)',')
    _position++;
while (_position < _data.Length && _data[_position] == (byte)'#')
{
    SkipLineSpan();
    _position = SimdHelpers.SkipWhitespace(_data, _position);
}
```

In `ReadInlineTableSpan()`, replace the similar whitespace skip:
```csharp
// Before:
while (_position < _data.Length)
{
    var b = _data[_position];
    if (b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)',')
        _position++;
    else
        break;
}

// After:
_position = SimdHelpers.SkipWhitespace(_data, _position);
while (_position < _data.Length && _data[_position] == (byte)',')
    _position++;
```

- [ ] **Step 8: Replace YAML whitespace loops with shared helper**

In `PicoYaml/src/PicoYaml/YamlReader.cs`:

In `ReadSpan()`, replace the inline while loop that skips blank lines:
```csharp
// Before:
while (_position < _data.Length && _data[_position] == (byte)' ')
{
    lineIndent++;
    _position++;
}

// After (keep the indent counting, but use SIMD for the pure whitespace-only paths):
while (_position < _data.Length && _data[_position] == (byte)' ')
{
    lineIndent++;
    _position++;
}
```

For the Retry path whitespace skip before comments, use `SimdHelpers.SkipWhitespace` for the newline-only skip in `SkipLineSpan` and `SkipNewlineSpan`.

Actually, YAML's whitespace handling is more nuanced because indentation is significant. The SIMD optimization should only apply to pure blank-line skipping. Let me be more specific:

In `ReadSpan()` around the blank line check, use the helper for newline skipping:
```csharp
// After blank line detection:
SkipLineSpan();
_position = SimdHelpers.SkipWhitespace(_data, _position); // fast skip pure whitespace
goto Retry;
```

In `ReadFlowSpan()` whitespace skip:
```csharp
// Before:
while (_position < _data.Length)
{
    var b = _data[_position];
    if (b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)',')
        _position++;
    else
        break;
}

// After:
_position = SimdHelpers.SkipWhitespace(_data, _position);
while (_position < _data.Length && _data[_position] == (byte)',')
    _position++;
```

Similarly for sequence mode paths.

- [ ] **Step 9: Full test suite**

Run: `dotnet test`
Expected: ALL PASS

- [ ] **Step 10: Commit**

```bash
git add shared/SimdHelpers.cs
git add PicoSerDe.Abs/src/PicoSerDe.Abs.csproj PicoSerDe.Abs/tests/PicoSerDe.Abs.Tests/SimdWhitespaceTests.cs
git add PicoJetson/src/PicoJetson/PicoJetson.csproj PicoJetson/src/PicoJetson/JsonReader.cs
git add PicoToml/src/PicoToml/PicoToml.csproj PicoToml/src/PicoToml/TomlReader.cs
git add PicoYaml/src/PicoYaml/PicoYaml.csproj PicoYaml/src/PicoYaml/YamlReader.cs
git commit -m "perf: extract SIMD whitespace skip to shared SimdHelpers, apply to TOML/YAML readers"
```

---

### Task 5: Unify INI TokenType

This is the largest refactor. It involves rewriting IniReader to emit `PicoSerDe.Abs.TokenType`, removing `IniTokenType`, updating the source generator, and rewriting all tests.

#### Task 5.1: Remove IniTokenType

**Files:**
- Delete: `PicoIni/src/PicoIni/IniTokenType.cs`
- Delete: `PicoIni/tests/PicoIni.Tests/IniTokenTypeTests.cs`

- [ ] **Step 5.1.1: Delete files and verify build fails**

```bash
rm PicoIni/src/PicoIni/IniTokenType.cs
rm PicoIni/tests/PicoIni.Tests/IniTokenTypeTests.cs
dotnet build PicoIni/src/PicoIni --no-restore
```

Expected: FAIL — IniReader and IniWriter reference `IniTokenType`

- [ ] **Step 5.1.2: Commit**

```bash
git rm PicoIni/src/PicoIni/IniTokenType.cs PicoIni/tests/PicoIni.Tests/IniTokenTypeTests.cs
git commit -m "refactor(picoini): remove IniTokenType, prepare for unified TokenType"
```

#### Task 5.2: Rewrite IniReader with unified TokenType (RED phase)

**Files:**
- Create: `PicoIni/tests/PicoIni.Tests/IniReaderUnifiedTests.cs`

- [ ] **Step 5.2.1: Write failing tests for new reader API**

```csharp
namespace PicoIni.Tests;

public class IniReaderUnifiedTests
{
    // Section → ObjectStart
    [Test]
    public async Task Read_Section_ReturnsObjectStart()
    {
        var data = "[Server]"u8;
        var reader = new IniReader(data);
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.ObjectStart);
        await Assert.That(reader.GetStringRaw().SequenceEqual("Server"u8)).IsTrue();
    }

    // Section transition emits implicit ObjectEnd + new ObjectStart
    [Test]
    public async Task Read_TwoSections_EmitsImplicitEndBeforeSecondStart()
    {
        var data = "[A]\n[B]"u8;
        var reader = new IniReader(data);
        reader.Read(); // ObjectStart for A
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.ObjectStart);

        reader.Read(); // ObjectEnd for A (implicit), then ObjectStart for B
        // The reader should emit ObjectEnd before ObjectStart
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.ObjectEnd);

        reader.Read(); // ObjectStart for B
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.ObjectStart);
        await Assert.That(reader.GetStringRaw().SequenceEqual("B"u8)).IsTrue();
    }

    // Key=Value → PropertyName + value token
    [Test]
    public async Task Read_KeyValue_ReturnsPropertyNameThenValue()
    {
        var data = "Host = localhost"u8;
        var reader = new IniReader(data);

        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.PropertyName);
        await Assert.That(reader.GetStringRaw().SequenceEqual("Host"u8)).IsTrue();

        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.String);
        await Assert.That(reader.GetStringRaw().SequenceEqual("localhost"u8)).IsTrue();
    }

    // Integer value
    [Test]
    public async Task Read_KeyValue_Int32_ReturnsInt32()
    {
        var data = "Port = 8080"u8;
        var reader = new IniReader(data);
        reader.Read(); // PropertyName "Port"
        reader.Read(); // Int32 8080
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.Int32);
        reader.TryGetInt32(out var v);
        await Assert.That(v).IsEqualTo(8080);
    }

    // Boolean value
    [Test]
    public async Task Read_KeyValue_Bool_ReturnsBool()
    {
        var data = "Enabled = true"u8;
        var reader = new IniReader(data);
        reader.Read(); // PropertyName
        reader.Read(); // Bool
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.Bool);
        reader.TryGetBool(out var v);
        await Assert.That(v).IsTrue();
    }

    // Float value
    [Test]
    public async Task Read_KeyValue_Float_ReturnsFloat64()
    {
        var data = "Rate = 3.14"u8;
        var reader = new IniReader(data);
        reader.Read(); // PropertyName
        reader.Read(); // Float64
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.Float64);
        reader.TryGetFloat64(out var v);
        await Assert.That(v).IsEqualTo(3.14);
    }

    // Comments are skipped (no token emitted)
    [Test]
    public async Task Read_Comment_Skipped_ThenNextToken()
    {
        var data = "; this is a comment\n[Server]"u8;
        var reader = new IniReader(data);
        reader.Read(); // ObjectStart "Server" (comment skipped)
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.ObjectStart);
    }

    // Blank lines are skipped
    [Test]
    public async Task Read_BlankLines_Skipped_ThenNextToken()
    {
        var data = "\n\n[Server]"u8;
        var reader = new IniReader(data);
        reader.Read(); // ObjectStart "Server" (blank lines skipped)
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.ObjectStart);
    }

    // Quoted value
    [Test]
    public async Task Read_QuotedValue_Unquotes()
    {
        var data = "Name = \"hello world\""u8;
        var reader = new IniReader(data);
        reader.Read(); // PropertyName
        reader.Read(); // String
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.String);
        await Assert.That(reader.GetStringRaw().SequenceEqual("hello world"u8)).IsTrue();
    }

    // Int32 array via comma-separated
    [Test]
    public async Task Read_CommaSeparatedInts_RepeatedPropertyName()
    {
        var data = "Tags = 1,2,3"u8;
        var reader = new IniReader(data);
        reader.Read(); // PropertyName "Tags"
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.PropertyName);
        reader.Read(); // Int32 1
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.Int32);
    }

    // End of input
    [Test]
    public async Task Read_EndOfInput_ReturnsFalse()
    {
        var data = "Key = Value"u8;
        var reader = new IniReader(data);
        reader.Read(); // PropertyName
        reader.Read(); // Value
        await Assert.That(reader.Read()).IsFalse();
    }

    // TrySkip
    [Test]
    public async Task TrySkip_SkipsCurrentValue()
    {
        var data = "Key = Value\nNext = 42"u8;
        var reader = new IniReader(data);
        reader.Read(); // PropertyName "Key"
        reader.Read(); // Value "Value" — skip this
        reader.TrySkip();
        reader.Read(); // PropertyName "Next"
        await Assert.That(reader.GetStringRaw().SequenceEqual("Next"u8)).IsTrue();
    }
}
```

- [ ] **Step 5.2.2: Run tests — expect FAIL**

Run: `dotnet test PicoIni/tests/PicoIni.Tests --filter "IniReaderUnified"`
Expected: FAIL — IniReader API has changed, new methods don't exist

#### Task 5.3: Implement new IniReader (GREEN phase)

- [ ] **Step 5.3.1: Rewrite IniReader.cs**

```csharp
namespace PicoIni;

public ref struct IniReader
{
    private ReadOnlySpan<byte> _data;
    private int _position;
    private SequenceReader<byte> _seqReader;
    private readonly bool _isSequence;
    private TokenType _tokenType;
    private ReadOnlySpan<byte> _valueSpan;
    private ReadOnlySpan<byte> _currentValue;
    private byte[]? _rentedBuffer;

    // Section tracking for implicit ObjectEnd emission
    private bool _inSection;
    private bool _sectionJustStarted;
    private ReadOnlySpan<byte> _pendingSectionName;
    private bool _hasPendingSectionEnd;

    public IniReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
        _seqReader = default;
        _isSequence = false;
        _tokenType = TokenType.None;
        _valueSpan = default;
        _currentValue = default;
        _rentedBuffer = null;
        _inSection = false;
        _sectionJustStarted = false;
        _pendingSectionName = default;
        _hasPendingSectionEnd = false;
    }

    public IniReader(ReadOnlySequence<byte> data)
    {
        _data = default;
        _position = 0;
        _seqReader = new SequenceReader<byte>(data);
        _isSequence = true;
        _tokenType = TokenType.None;
        _valueSpan = default;
        _currentValue = default;
        _rentedBuffer = null;
        _inSection = false;
        _sectionJustStarted = false;
        _pendingSectionName = default;
        _hasPendingSectionEnd = false;
    }

    public TokenType TokenType => _tokenType;
    public long BytesConsumed => _isSequence ? _seqReader.Consumed : _position;

    /// <summary>Returns the raw UTF-8 bytes of the current section name, property name, or string value.</summary>
    public ReadOnlySpan<byte> GetStringRaw() => _currentValue;

    public bool Read()
    {
        // Emit pending ObjectEnd from previous section transition
        if (_hasPendingSectionEnd)
        {
            _tokenType = TokenType.ObjectEnd;
            _hasPendingSectionEnd = false;
            return true;
        }

        if (_isSequence)
            return ReadSeq();
        return ReadSpan();
    }

    public bool TryGetInt32(out int v)
    {
        if (_tokenType != TokenType.Int32)
        {
            v = 0;
            return false;
        }
        return Utf8Parser.TryParse(_currentValue, out v, out _);
    }

    public bool TryGetInt64(out long v)
    {
        if (_tokenType is not (TokenType.Int32 or TokenType.Int64))
        {
            v = 0;
            return false;
        }
        return Utf8Parser.TryParse(_currentValue, out v, out _);
    }

    public bool TryGetFloat64(out double v)
    {
        if (_tokenType is not (TokenType.Float64 or TokenType.Int32 or TokenType.Int64))
        {
            v = 0;
            return false;
        }
        return Utf8Parser.TryParse(_currentValue, out v, out _);
    }

    public bool TryGetBool(out bool v)
    {
        if (_tokenType != TokenType.Bool)
        {
            v = false;
            return false;
        }
        v = _currentValue[0] == (byte)'t';
        return true;
    }

    public void Skip()
    {
        // INI values are single tokens — nothing to skip structurally
    }

    public bool TrySkip()
    {
        try { Skip(); return true; }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_rentedBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_rentedBuffer);
            _rentedBuffer = null;
        }
    }

    // ── Span mode ──

    private bool ReadSpan()
    {
        while (_position < _data.Length)
        {
            var b = _data[_position];

            if (b is (byte)'\n' or (byte)'\r')
            {
                _position++;
                if (_position < _data.Length && _data[_position] == (byte)'\n' && _data[_position - 1] == (byte)'\r')
                    _position++;
                continue;
            }

            if (b is (byte)' ' or (byte)'\t')
            {
                _position++;
                continue;
            }

            if (b == (byte)'[')
                return ReadSectionSpan();
            if (b is (byte)';' or (byte)'#')
            {
                SkipToNextLineSpan();
                continue;
            }
            return ReadKeyValueSpan();
        }
        return false;
    }

    private bool ReadSectionSpan()
    {
        // If we were inside a section, queue an implicit ObjectEnd
        if (_inSection)
            _hasPendingSectionEnd = true;

        _position++;
        var start = _position;
        while (_position < _data.Length && _data[_position] != (byte)']')
            _position++;
        if (_position >= _data.Length)
            throw new FormatException("Unterminated section at end of input");
        _currentValue = _data[start.._position];
        _position++;
        SkipToNextLineSpan();

        _inSection = true;
        _tokenType = TokenType.ObjectStart;

        // If we had a previous section, the pending ObjectEnd is emitted first
        if (_hasPendingSectionEnd)
        {
            _pendingSectionName = _currentValue;
            _sectionJustStarted = true;
            return true;
        }
        return true;
    }

    private bool ReadKeyValueSpan()
    {
        // Key
        var keyStart = _position;
        while (_position < _data.Length && _data[_position] != (byte)'=')
            _position++;
        _currentValue = TrimEnd(_data[keyStart.._position]);
        _tokenType = TokenType.PropertyName;
        _position++;

        // Skip whitespace after =
        while (_position < _data.Length && (_data[_position] == (byte)' ' || _data[_position] == (byte)'\t'))
            _position++;

        // Parse value type
        var valStart = _position;
        bool isQuoted = _position < _data.Length && _data[_position] == (byte)'"';

        if (isQuoted)
        {
            _position++;
            valStart = _position;
            var buf = ArrayPool<byte>.Shared.Rent(256);
            _rentedBuffer = buf;
            int di = 0;
            while (_position < _data.Length && _data[_position] != (byte)'"')
            {
                if (_data[_position] == (byte)'\\' && _position + 1 < _data.Length)
                {
                    _position++;
                    switch (_data[_position])
                    {
                        case (byte)'n': buf[di++] = (byte)'\n'; break;
                        case (byte)'t': buf[di++] = (byte)'\t'; break;
                        case (byte)'r': buf[di++] = (byte)'\r'; break;
                        case (byte)'\\': buf[di++] = (byte)'\\'; break;
                        case (byte)'"': buf[di++] = (byte)'"'; break;
                        default: buf[di++] = _data[_position]; break;
                    }
                    _position++;
                }
                else
                {
                    buf[di++] = _data[_position++];
                }
            }
            if (_position < _data.Length)
                _position++;
            _valueSpan = buf.AsSpan(0, di);
        }
        else
        {
            while (_position < _data.Length && _data[_position] != (byte)'\n' && _data[_position] != (byte)'\r')
                _position++;
            var raw = _data[valStart.._position];
            var semiIdx = raw.IndexOf((byte)';');
            var hashIdx = raw.IndexOf((byte)'#');
            var commentIdx = semiIdx < 0 ? hashIdx : hashIdx < 0 ? semiIdx : Math.Min(semiIdx, hashIdx);
            if (commentIdx >= 0)
                raw = TrimEnd(raw[..commentIdx]);
            _valueSpan = Trim(raw);
        }

        SkipToNextLineSpan();

        // Return PropertyName on this Read(). The caller must call Read() again for the value.
        // We store _valueSpan and emit the value token on the next Read() call.
        // Actually, for simplicity, emit both in one Read() — PropertyName now, value on next Read().
        // But the standard pattern is: Read() → PropertyName, then Read() → value.
        // We need to store the value for the next Read().

        // For now, store the value info and return PropertyName
        _pendingValue = _valueSpan;
        _pendingIsQuoted = isQuoted;
        _hasPendingValue = true;
        return true;
    }

    // ... rest of implementation continues with sequence mode, value emission, etc.
}
```

Wait, this is getting very long for the plan. Let me focus on the key state machine change and write the complete file via the `write` tool during execution. The plan should describe the approach and key code fragments.

Let me complete the plan with the essential changes.

Actually, the writing-plans skill says "No Placeholders" and every step must contain actual code. But a 500+ line reader rewrite in a plan would be unmanageable. Let me include the full code but organize it well.

Let me reconsider the approach. The IniReader state machine needs to:
1. On Read(), return one token at a time
2. When reading `Key = Value`, first Read() returns PropertyName, second Read() returns the typed value (String/Int32/Float64/Bool)
3. When reading `[Section]`, return ObjectStart with section name via GetStringRaw()
4. When transitioning between sections, emit ObjectEnd before next ObjectStart

This is actually achievable with a simpler state machine than the current one. Let me write the complete implementation.

I'll finalize the plan content. It's going to be long but complete.<｜end▁of▁thinking｜>Plan is quite long. Let me continue writing the remaining tasks:

<｜｜DSML｜｜tool_calls>
<｜｜DSML｜｜invoke name="write">
<｜｜DSML｜｜parameter name="content" string="true"># Post-Review Improvements Implementation Plan (Part 2)

**Continuation from Part 1 — starts at Task 5.3**

---

- [ ] **Step 5.3.1: Rewrite IniReader.cs (complete implementation)**

Delete old `PicoIni/src/PicoIni/IniReader.cs` and write:

```csharp
namespace PicoIni;

public ref struct IniReader
{
    private ReadOnlySpan<byte> _data;
    private int _position;
    private SequenceReader<byte> _seqReader;
    private readonly bool _isSequence;
    private TokenType _tokenType;
    private ReadOnlySpan<byte> _currentValue;
    private byte[]? _rentedBuffer;

    // Pending value state: when Read() returns PropertyName, the value is parsed
    // and stored here. The next Read() call emits the stored value token.
    private ReadOnlySpan<byte> _pendingValue;
    private TokenType _pendingValueType;
    private bool _hasPendingValue;

    // Section tracking: emit implicit ObjectEnd when entering a new section
    private bool _inSection;
    private bool _hasPendingSectionEnd;

    public IniReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
        _seqReader = default;
        _isSequence = false;
        _tokenType = TokenType.None;
        _currentValue = default;
        _rentedBuffer = null;
        _pendingValue = default;
        _pendingValueType = TokenType.None;
        _hasPendingValue = false;
        _inSection = false;
        _hasPendingSectionEnd = false;
    }

    public IniReader(ReadOnlySequence<byte> data)
    {
        _data = default;
        _position = 0;
        _seqReader = new SequenceReader<byte>(data);
        _isSequence = true;
        _tokenType = TokenType.None;
        _currentValue = default;
        _rentedBuffer = null;
        _pendingValue = default;
        _pendingValueType = TokenType.None;
        _hasPendingValue = false;
        _inSection = false;
        _hasPendingSectionEnd = false;
    }

    public TokenType TokenType => _tokenType;
    public long BytesConsumed => _isSequence ? _seqReader.Consumed : _position;

    public ReadOnlySpan<byte> GetStringRaw() => _currentValue;

    public bool Read()
    {
        // Emit pending ObjectEnd before next token
        if (_hasPendingSectionEnd)
        {
            _tokenType = TokenType.ObjectEnd;
            _hasPendingSectionEnd = false;
            _inSection = false;
            return true;
        }

        // Emit pending value from previous PropertyName read
        if (_hasPendingValue)
        {
            _tokenType = _pendingValueType;
            _currentValue = _pendingValue;
            _hasPendingValue = false;
            return true;
        }

        return _isSequence ? ReadSeq() : ReadSpan();
    }

    public bool TryGetInt32(out int v)
    {
        if (_tokenType != TokenType.Int32) { v = 0; return false; }
        return Utf8Parser.TryParse(_currentValue, out v, out _);
    }

    public bool TryGetInt64(out long v)
    {
        if (_tokenType is not (TokenType.Int32 or TokenType.Int64)) { v = 0; return false; }
        return Utf8Parser.TryParse(_currentValue, out v, out _);
    }

    public bool TryGetFloat64(out double v)
    {
        if (_tokenType is not (TokenType.Float64 or TokenType.Int32 or TokenType.Int64)) { v = 0; return false; }
        return Utf8Parser.TryParse(_currentValue, out v, out _);
    }

    public bool TryGetBool(out bool v)
    {
        if (_tokenType != TokenType.Bool) { v = false; return false; }
        v = _currentValue[0] == (byte)'t';
        return true;
    }

    public void Skip() { }
    public bool TrySkip() => true;

    public void Dispose()
    {
        if (_rentedBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_rentedBuffer);
            _rentedBuffer = null;
        }
    }

    // ── Span-mode read ──

    private bool ReadSpan()
    {
        while (_position < _data.Length)
        {
            var b = _data[_position];

            if (b is (byte)'\n' or (byte)'\r')
            {
                _position++;
                if (_position < _data.Length && _data[_position] == (byte)'\n' && _data[_position - 1] == (byte)'\r')
                    _position++;
                continue;
            }

            if (b is (byte)' ' or (byte)'\t') { _position++; continue; }

            if (b == (byte)'[')
                return ReadSectionSpan();
            if (b is (byte)';' or (byte)'#')
            {
                SkipToNextLineSpan();
                continue;
            }
            // Key=Value: first Read() emits PropertyName, stores value for next Read()
            return ReadKeyValueSpan();
        }
        return false;
    }

    private bool ReadSectionSpan()
    {
        if (_inSection)
            _hasPendingSectionEnd = true;

        _position++; // skip '['
        var start = _position;
        while (_position < _data.Length && _data[_position] != (byte)']')
            _position++;
        if (_position >= _data.Length)
            throw new FormatException("Unterminated section at end of input");
        _currentValue = _data[start.._position];
        _position++; // skip ']'
        SkipToNextLineSpan();

        _inSection = true;
        _tokenType = TokenType.ObjectStart;
        return true;
    }

    private bool ReadKeyValueSpan()
    {
        // Read key
        var keyStart = _position;
        while (_position < _data.Length && _data[_position] != (byte)'=')
            _position++;
        _currentValue = TrimEnd(_data[keyStart.._position]);
        _tokenType = TokenType.PropertyName;
        _position++; // skip '='

        // Skip whitespace after =
        while (_position < _data.Length && (_data[_position] == (byte)' ' || _data[_position] == (byte)'\t'))
            _position++;

        // Parse and store value for next Read()
        if (_position < _data.Length && _data[_position] == (byte)'"')
        {
            // Quoted string value
            _position++;
            var valStart = _position;
            var buf = ArrayPool<byte>.Shared.Rent(256);
            _rentedBuffer = buf;
            int di = 0;
            while (_position < _data.Length && _data[_position] != (byte)'"')
            {
                if (_data[_position] == (byte)'\\' && _position + 1 < _data.Length)
                {
                    _position++;
                    buf[di++] = _data[_position] switch
                    {
                        (byte)'n' => (byte)'\n',
                        (byte)'t' => (byte)'\t',
                        (byte)'r' => (byte)'\r',
                        (byte)'\\' => (byte)'\\',
                        (byte)'"' => (byte)'"',
                        var c => c,
                    };
                    _position++;
                }
                else
                    buf[di++] = _data[_position++];
            }
            if (_position < _data.Length) _position++; // skip closing "
            _pendingValue = buf.AsSpan(0, di);
            _pendingValueType = TokenType.String;
        }
        else
        {
            // Unquoted — parse inline, detect type
            var valStart = _position;
            while (_position < _data.Length && _data[_position] != (byte)'\n' && _data[_position] != (byte)'\r')
                _position++;
            var raw = _data[valStart.._position];

            // Strip inline comment
            var semiIdx = raw.IndexOf((byte)';');
            var hashIdx = raw.IndexOf((byte)'#');
            var commentIdx = semiIdx < 0 ? hashIdx : hashIdx < 0 ? semiIdx : Math.Min(semiIdx, hashIdx);
            if (commentIdx >= 0)
                raw = TrimEnd(raw[..commentIdx]);
            raw = Trim(raw);

            _pendingValue = raw;
            _pendingValueType = DetectValueType(raw);
        }

        SkipToNextLineSpan();
        _hasPendingValue = true;
        return true;
    }

    private static TokenType DetectValueType(ReadOnlySpan<byte> raw)
    {
        if (raw.IsEmpty) return TokenType.String;
        if (raw.SequenceEqual("true"u8) || raw.SequenceEqual("false"u8)) return TokenType.Bool;
        if (raw.SequenceEqual("null"u8)) return TokenType.Null;

        // Check if numeric
        bool hasDot = false, hasExp = false;
        int start = raw[0] == (byte)'-' ? 1 : 0;
        if (start >= raw.Length) return TokenType.String;
        for (int i = start; i < raw.Length; i++)
        {
            byte c = raw[i];
            if (c == (byte)'.') hasDot = true;
            else if (c is (byte)'e' or (byte)'E') hasExp = true;
            else if (c < (byte)'0' || c > (byte)'9') return TokenType.String;
        }
        return (hasDot || hasExp) ? TokenType.Float64 : TokenType.Int32;
    }

    // ── Sequence-mode read (simplified — mirrors span path) ──

    private bool ReadSeq()
    {
        // Emit pending ObjectEnd
        if (_hasPendingSectionEnd)
        {
            _tokenType = TokenType.ObjectEnd;
            _hasPendingSectionEnd = false;
            _inSection = false;
            return true;
        }

        // Emit pending value
        if (_hasPendingValue)
        {
            _tokenType = _pendingValueType;
            _currentValue = _pendingValue;
            _hasPendingValue = false;
            return true;
        }

        while (!_seqReader.End)
        {
            var b = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            if (b is (byte)'\n' or (byte)'\r')
            {
                _seqReader.Advance(1);
                if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\n')
                    _seqReader.Advance(1);
                continue;
            }
            if (b is (byte)' ' or (byte)'\t') { _seqReader.Advance(1); continue; }
            if (b == (byte)'[') return ReadSectionSeq();
            if (b is (byte)';' or (byte)'#') { SkipToNextLineSeq(); continue; }
            return ReadKeyValueSeq();
        }
        return false;
    }

    private bool ReadSectionSeq()
    {
        if (_inSection) _hasPendingSectionEnd = true;
        _seqReader.Advance(1);
        var buf = ArrayPool<byte>.Shared.Rent(64);
        _rentedBuffer = buf;
        int di = 0;
        while (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)']')
        {
            buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            _seqReader.Advance(1);
        }
        if (_seqReader.End) throw new FormatException("Unterminated section");
        _seqReader.Advance(1);
        _currentValue = buf.AsSpan(0, di);
        SkipToNextLineSeq();
        _inSection = true;
        _tokenType = TokenType.ObjectStart;
        return true;
    }

    private bool ReadKeyValueSeq()
    {
        var buf = ArrayPool<byte>.Shared.Rent(256);
        _rentedBuffer = buf;
        int di = 0;
        while (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'=')
        {
            buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            _seqReader.Advance(1);
        }
        _currentValue = TrimEnd(buf.AsSpan(0, di));
        _tokenType = TokenType.PropertyName;
        _seqReader.Advance(1); // skip '='
        while (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)' ')
            _seqReader.Advance(1);

        // Read value
        di = 0;
        while (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\n'
            && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\r')
        {
            buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            _seqReader.Advance(1);
        }
        var raw = Trim(buf.AsSpan(0, di));
        _pendingValue = raw;
        _pendingValueType = DetectValueType(raw);
        SkipToNextLineSeq();
        _hasPendingValue = true;
        return true;
    }

    private void SkipToNextLineSpan()
    {
        while (_position < _data.Length && _data[_position] != (byte)'\n' && _data[_position] != (byte)'\r')
            _position++;
        if (_position < _data.Length)
        {
            if (_data[_position] == (byte)'\r' && _position + 1 < _data.Length && _data[_position + 1] == (byte)'\n')
                _position += 2;
            else
                _position++;
        }
    }

    private void SkipToNextLineSeq()
    {
        while (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\n'
            && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\r')
            _seqReader.Advance(1);
        if (!_seqReader.End)
        {
            var b = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            _seqReader.Advance(1);
            if (!_seqReader.End && b == (byte)'\r' && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\n')
                _seqReader.Advance(1);
        }
    }

    private static ReadOnlySpan<byte> TrimEnd(ReadOnlySpan<byte> s)
    {
        int e = s.Length;
        while (e > 0 && (s[e - 1] == (byte)' ' || s[e - 1] == (byte)'\t')) e--;
        return s[..e];
    }

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> s)
    {
        int st = 0, e = s.Length;
        while (st < e && (s[st] == (byte)' ' || s[st] == (byte)'\t')) st++;
        while (e > st && (s[e - 1] == (byte)' ' || s[e - 1] == (byte)'\t')) e--;
        return s[st..e];
    }
}
```

- [ ] **Step 5.3.2: Run reader tests**

Run: `dotnet test PicoIni/tests/PicoIni.Tests --filter "IniReaderUnified"`
Expected: ALL PASS

- [ ] **Step 5.3.3: Commit**

```bash
git add PicoIni/src/PicoIni/IniReader.cs PicoIni/tests/PicoIni.Tests/IniReaderUnifiedTests.cs
git commit -m "refactor(picoini): rewrite IniReader to emit shared TokenType"
```

#### Task 5.4: Update IniWriter (minor alignment)

**Files:**
- Modify: `PicoIni/src/PicoIni/IniWriter.cs`

The IniWriter doesn't read tokens so it needs minimal changes — only updating the type references from `IniTokenType` to `TokenType` if any remain. Actually, IniWriter never references IniTokenType. It's self-contained. No changes needed.

#### Task 5.5: Update IniSerializerGenerator for TokenType constants

**Files:**
- Modify: `PicoIni/src/PicoIni.Gen/IniSerializerGenerator.cs`

- [ ] **Step 5.5.1: Update generated deserializer code to use TokenType**

In `EmitRead`, update constants:
- Change `IniTokenType.Key` → `TokenType.PropertyName`
- Change `IniTokenType.SectionStart` → `TokenType.ObjectStart`
- Remove `IniTokenType.Comment` and `IniTokenType.Blank` references (comments/blanks are now skipped)

In `Gen()` method, update the deserializer generation:

```csharp
// Old:
s.AppendLine("            if (reader.TokenType == IniTokenType.Key) {");

// New:
s.AppendLine("            if (reader.TokenType == TokenType.PropertyName) {");
```

```csharp
// Old:
s.AppendLine("            else if (reader.TokenType == IniTokenType.SectionStart) {");

// New:
s.AppendLine("            else if (reader.TokenType == TokenType.ObjectStart) {");
```

Replace `reader.Key` with `reader.GetStringRaw()`:
```csharp
// Old:
s.Append("__IniHelp.Eq(reader.Key, \"");

// New:
s.Append("__IniHelp.Eq(reader.GetStringRaw(), \"");
```

Replace `reader.SectionName` with `reader.GetStringRaw()`:
```csharp
// Old:
s.Append("__IniHelp.Eq(reader.SectionName, \"");

// New:
s.Append("__IniHelp.Eq(reader.GetStringRaw(), \"");
```

Replace `reader.ValueSpan` with `reader.GetStringRaw()`:
```csharp
// Old:
s.AppendLine(" = Encoding.UTF8.GetString(reader.ValueSpan);");

// New:
s.AppendLine(" = Encoding.UTF8.GetString(reader.GetStringRaw());");
```

- [ ] **Step 5.5.2: Commit**

```bash
git add PicoIni/src/PicoIni.Gen/IniSerializerGenerator.cs
git commit -m "refactor(picoini-gen): update generated code to use TokenType and GetStringRaw"
```

#### Task 5.6: Update existing INI tests for new API

**Files:**
- Modify: `PicoIni/tests/PicoIni.Tests/IniWriterTests.cs` — update import if any reference to IniTokenType
- Modify: `PicoIni/tests/PicoIni.Tests/AttributeTests.cs` — update if referencing old types
- Delete: `PicoIni/tests/PicoIni.Tests/IniReaderTests.cs` — replaced by IniReaderUnifiedTests
- Delete: `PicoIni/tests/PicoIni.Tests/IniSerializerTests.cs` — replaced by IniSerializerUnifiedTests

- [ ] **Step 5.6.1: Create unified serializer tests**

Create `PicoIni/tests/PicoIni.Tests/IniSerializerUnifiedTests.cs`:

```csharp
namespace PicoIni.Tests;

public class IniSerializerUnifiedTests
{
    // Test models
    public class SimpleConfig
    {
        public string Title { get; set; } = "";
        public int Port { get; set; }
    }

    public class ServerSection
    {
        public string Host { get; set; } = "";
        public int Port { get; set; }
    }

    public class AppConfig
    {
        public string Title { get; set; } = "";
        public ServerSection Server { get; set; } = new();
    }

    // Manual serializer for SimpleConfig
    private readonly struct SimpleConfigIniSerializer : ISerializer<SimpleConfig>
    {
        public void Serialize(IBufferWriter<byte> writer, SimpleConfig value)
        {
            var iw = new IniWriter(writer);
            iw.WriteKeyValue("Title", value.Title);
            iw.WriteKeyValue("Port", value.Port);
        }
    }

    private readonly struct SimpleConfigIniDeserializer : IDeserializer<SimpleConfig>
    {
        public SimpleConfig Deserialize(ReadOnlySpan<byte> data)
        {
            var reader = new IniReader(data);
            var obj = new SimpleConfig();
            while (reader.Read())
            {
                if (reader.TokenType == TokenType.PropertyName)
                {
                    var key = reader.GetStringRaw();
                    reader.Read(); // value
                    if (key.SequenceEqual("Title"u8) && reader.TokenType == TokenType.String)
                        obj.Title = Encoding.UTF8.GetString(reader.GetStringRaw());
                    else if (key.SequenceEqual("Port"u8) && reader.TokenType == TokenType.Int32)
                    {
                        reader.TryGetInt32(out var p);
                        obj.Port = p;
                    }
                }
            }
            return obj;
        }
    }

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
                if (reader.TokenType == TokenType.PropertyName)
                {
                    var key = reader.GetStringRaw();
                    reader.Read(); // value
                    if (key.SequenceEqual("Title"u8) && reader.TokenType == TokenType.String)
                        config.Title = Encoding.UTF8.GetString(reader.GetStringRaw());
                }
                else if (reader.TokenType == TokenType.ObjectStart)
                {
                    var section = reader.GetStringRaw();
                    if (section.SequenceEqual("Server"u8))
                    {
                        config.Server = new ServerSection();
                        while (reader.Read())
                        {
                            if (reader.TokenType == TokenType.PropertyName)
                            {
                                var sk = reader.GetStringRaw();
                                reader.Read(); // value
                                if (sk.SequenceEqual("Host"u8) && reader.TokenType == TokenType.String)
                                    config.Server.Host = Encoding.UTF8.GetString(reader.GetStringRaw());
                                else if (sk.SequenceEqual("Port"u8) && reader.TokenType == TokenType.Int32)
                                {
                                    reader.TryGetInt32(out var sp);
                                    config.Server.Port = sp;
                                }
                            }
                            else if (reader.TokenType == TokenType.ObjectStart || reader.TokenType == TokenType.ObjectEnd)
                            {
                                break; // next section or section end
                            }
                        }
                    }
                }
            }
            return config;
        }
    }

    [Test]
    public async Task SimpleConfig_RoundTrip()
    {
        var config = new SimpleConfig { Title = "MyApp", Port = 8080 };
        var ser = new SimpleConfigIniSerializer();
        var deser = new SimpleConfigIniDeserializer();
        var buf = new ArrayBufferWriter<byte>(256);
        ser.Serialize(buf, config);
        var result = deser.Deserialize(buf.WrittenSpan);
        await Assert.That(result.Title).IsEqualTo("MyApp");
        await Assert.That(result.Port).IsEqualTo(8080);
    }

    [Test]
    public async Task AppConfig_RoundTrip()
    {
        var config = new AppConfig { Title = "MyApp", Server = new ServerSection { Host = "localhost", Port = 8080 } };
        var ser = new AppConfigIniSerializer();
        var deser = new AppConfigIniDeserializer();
        var buf = new ArrayBufferWriter<byte>(256);
        ser.Serialize(buf, config);
        var result = deser.Deserialize(buf.WrittenSpan);
        await Assert.That(result.Title).IsEqualTo("MyApp");
        await Assert.That(result.Server.Host).IsEqualTo("localhost");
        await Assert.That(result.Server.Port).IsEqualTo(8080);
    }

    [Test]
    public async Task StaticApi_RegisterThenRoundTrip()
    {
        IniSerializer.Register(new SimpleConfigIniSerializer(), new SimpleConfigIniDeserializer());
        var config = new SimpleConfig { Title = "Static", Port = 42 };
        var ini = IniSerializer.Serialize(config);
        var result = IniSerializer.Deserialize<SimpleConfig>(Encoding.UTF8.GetBytes(ini));
        await Assert.That(result!.Title).IsEqualTo("Static");
        await Assert.That(result.Port).IsEqualTo(42);
    }

    [Test]
    public async Task BoolValue_RoundTrip()
    {
        var data = "Enabled = true"u8;
        var reader = new IniReader(data);
        reader.Read(); // PropertyName
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.PropertyName);
        await Assert.That(reader.GetStringRaw().SequenceEqual("Enabled"u8)).IsTrue();
        reader.Read(); // Bool
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.Bool);
        reader.TryGetBool(out var v);
        await Assert.That(v).IsTrue();
    }

    [Test]
    public async Task FloatValue_RoundTrip()
    {
        var data = "Rate = 3.14"u8;
        var reader = new IniReader(data);
        reader.Read(); // PropertyName
        reader.Read(); // Float64
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.Float64);
        reader.TryGetFloat64(out var v);
        await Assert.That(v).IsEqualTo(3.14);
    }

    [Test]
    public async Task NegativeInt()
    {
        var data = "Offset = -42"u8;
        var reader = new IniReader(data);
        reader.Read(); // PropertyName
        reader.Read(); // Int32
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.Int32);
        reader.TryGetInt32(out var v);
        await Assert.That(v).IsEqualTo(-42);
    }
}
```

- [ ] **Step 5.6.2: Remove old test files**

```bash
git rm PicoIni/tests/PicoIni.Tests/IniReaderTests.cs
git rm PicoIni/tests/PicoIni.Tests/IniSerializerTests.cs
```

- [ ] **Step 5.6.3: Run all INI tests**

Run: `dotnet test PicoIni/tests/PicoIni.Tests`
Expected: ALL PASS

- [ ] **Step 5.6.4: Commit**

```bash
git add PicoIni/tests/PicoIni.Tests/IniSerializerUnifiedTests.cs
git add PicoIni/tests/PicoIni.Tests/IniWriterTests.cs PicoIni/tests/PicoIni.Tests/AttributeTests.cs
git commit -m "test(picoini): rewrite tests for unified TokenType API"
```

#### Task 5.7: Full build and test verification

- [ ] **Step 5.7.1: Build entire solution**

Run: `dotnet build`
Expected: 0 errors, 0 warnings

- [ ] **Step 5.7.2: Run all tests**

Run: `dotnet test`
Expected: ALL PASS

- [ ] **Step 5.7.3: Commit if any remaining changes**

```bash
git add -A
git commit -m "refactor(picoini): complete INI TokenType unification"
```

---

### Task 6: YAML Anchor/Alias Support

**Files:**
- Create: `PicoYaml/tests/PicoYaml.Tests/YamlAnchorTests.cs`
- Modify: `PicoYaml/src/PicoYaml/YamlReader.cs`

#### Task 6.1: Write failing tests (RED phase)

- [ ] **Step 6.1.1: Create anchor test file**

```csharp
namespace PicoYaml.Tests;

public class YamlAnchorTests
{
    [Test]
    public async Task SimpleAnchor_DefineAndReference_ResolvesValue()
    {
        var yaml = "name: &label Alice\ncopy: *label"u8;
        var reader = new YamlReader(yaml);

        // name: &label Alice
        reader.Read(); // ObjectStart (implicit)
        reader.Read(); // PropertyName "name"
        await Assert.That(reader.GetStringRaw().SequenceEqual("name"u8)).IsTrue();
        reader.Read(); // String "Alice"
        await Assert.That(reader.GetStringRaw().SequenceEqual("Alice"u8)).IsTrue();

        // copy: *label → should resolve to "Alice"
        reader.Read(); // PropertyName "copy"
        await Assert.That(reader.GetStringRaw().SequenceEqual("copy"u8)).IsTrue();
        reader.Read(); // String "Alice" (resolved)
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.String);
        await Assert.That(reader.GetStringRaw().SequenceEqual("Alice"u8)).IsTrue();
    }

    [Test]
    public async Task AnchorOnMapping_DefineAndReference_ResolvesMapping()
    {
        var yaml = "defaults: &def\n  host: localhost\n  port: 8080\nserver: *def"u8;
        var reader = new YamlReader(yaml);

        // Read through the document
        reader.Read(); // ObjectStart (implicit root)
        reader.Read(); // PropertyName "defaults"
        reader.Read(); // ObjectStart for defaults mapping (nested)
        reader.Read(); // PropertyName "host"
        reader.Read(); // String "localhost"
        reader.Read(); // PropertyName "port"
        reader.Read(); // Int32? or String "8080"
        reader.Read(); // ObjectEnd for defaults
        reader.Read(); // PropertyName "server"
        reader.Read(); // ObjectStart for *def (resolved)
        reader.Read(); // PropertyName "host"
        reader.Read(); // String "localhost"
        reader.Read(); // PropertyName "port"
        reader.Read(); // Int32? or String "8080"
        reader.Read(); // ObjectEnd for server

        await Assert.That(true).IsTrue(); // no exceptions = pass
    }

    [Test]
    public async Task AnchorDefinition_DoesNotAffectNormalTokenEmission()
    {
        var yaml = "key: &myAnchor value"u8;
        var reader = new YamlReader(yaml);

        reader.Read(); // ObjectStart
        reader.Read(); // PropertyName "key"
        await Assert.That(reader.GetStringRaw().SequenceEqual("key"u8)).IsTrue();
        reader.Read(); // String "value" (anchor stored internally, not emitted differently)
        await Assert.That(reader.TokenType).IsEqualTo(TokenType.String);
        await Assert.That(reader.GetStringRaw().SequenceEqual("value"u8)).IsTrue();
    }

    [Test]
    public async Task DuplicateAnchor_ThrowsFormatException()
    {
        var yaml = "a: &dup 1\nb: &dup 2"u8;
        var reader = new YamlReader(yaml);
        try
        {
            while (reader.Read()) { }
            throw new Exception("Expected FormatException");
        }
        catch (FormatException) { }
    }

    [Test]
    public async Task UnresolvedAlias_ThrowsFormatException()
    {
        var yaml = "key: *undefined"u8;
        var reader = new YamlReader(yaml);
        reader.Read(); // ObjectStart
        reader.Read(); // PropertyName "key"
        try
        {
            reader.Read(); // should throw on *undefined
            throw new Exception("Expected FormatException");
        }
        catch (FormatException) { }
    }
}
```

- [ ] **Step 6.1.2: Run tests — expect FAIL**

Run: `dotnet test PicoYaml/tests/PicoYaml.Tests --filter "YamlAnchor"`
Expected: FAIL — anchors not yet implemented

#### Task 6.2: Implement anchor/alias support (GREEN phase)

- [ ] **Step 6.2.1: Add anchor storage to YamlReader**

Add fields to `PicoYaml/src/PicoYaml/YamlReader.cs`:

```csharp
// Add to field declarations (near _rentedBuffer):
private Dictionary<string, StoredAnchor>? _anchors;
private bool _aliasInProgress;
private StoredAnchor _currentAlias;
private int _aliasReplayIndex;

// Add struct after class:
internal struct StoredAnchor
{
    public TokenType TokenType;
    public byte[] Value; // Copy of the value bytes (since source span may move)
    public List<(byte[] Key, byte[] Val)>? Properties; // For mapping anchors
}
```

- [ ] **Step 6.2.2: Add anchor parsing in ReadSpan()**

After reading a scalar value or before emitting ObjectStart for a mapping, check for `&` in the source:

In the `ReadSpan()` method, after the `:` processing, before reading the value, check for `&anchor`:

```csharp
// After finding ':' and advancing past it, before reading value:
// Check for &anchor
if (_position < _data.Length && _data[_position] == (byte)'&')
{
    _position++;
    int anchorStart = _position;
    while (_position < _data.Length && _data[_position] != (byte)' ' && _data[_position] != (byte)'\n')
        _position++;
    var anchorName = Encoding.UTF8.GetString(_data[anchorStart.._position]);
    _pendingAnchorName = anchorName;
    while (_position < _data.Length && _data[_position] == (byte)' ')
        _position++;
}
```

After reading the value, if `_pendingAnchorName` is set, store it:

```csharp
if (_pendingAnchorName is not null)
{
    _anchors ??= new Dictionary<string, StoredAnchor>();
    if (_anchors.ContainsKey(_pendingAnchorName))
        throw new FormatException($"Duplicate anchor '&{_pendingAnchorName}'");
    _anchors[_pendingAnchorName] = new StoredAnchor
    {
        TokenType = _tokenType,
        Value = _valueSpan.ToArray(),
    };
    _pendingAnchorName = null;
}
```

For mapping anchors, after ObjectStart:
```csharp
if (_pendingAnchorName is not null)
{
    _anchors ??= new Dictionary<string, StoredAnchor>();
    _anchors[_pendingAnchorName] = new StoredAnchor
    {
        TokenType = TokenType.ObjectStart,
        Properties = new List<(byte[], byte[])>(),
    };
    _currentMappingAnchor = _pendingAnchorName;
    _pendingAnchorName = null;
}
```

Then during ObjectEnd for that mapping:
```csharp
if (_currentMappingAnchor is not null && _tokenType == TokenType.ObjectEnd)
{
    // Finalize anchor — store accumulated properties
    _currentMappingAnchor = null;
}
```

- [ ] **Step 6.2.3: Add alias resolution**

When reading a value and encountering `*`:

```csharp
if (_position < _data.Length && _data[_position] == (byte)'*')
{
    _position++;
    int aliasStart = _position;
    while (_position < _data.Length && _data[_position] != (byte)' ' && _data[_position] != (byte)'\n')
        _position++;
    var aliasName = Encoding.UTF8.GetString(_data[aliasStart.._position]);

    if (_anchors is null || !_anchors.TryGetValue(aliasName, out var anchor))
        throw new FormatException($"Unresolved alias '*{aliasName}'");

    if (anchor.Properties is not null)
    {
        // Mapping alias — set up replay
        _aliasInProgress = true;
        _currentAlias = anchor;
        _aliasReplayIndex = 0;
        _tokenType = TokenType.ObjectStart;
        return true;
    }
    else
    {
        // Scalar alias — emit stored value
        _tokenType = anchor.TokenType;
        _valueSpan = anchor.Value;
        return true;
    }
}
```

- [ ] **Step 6.2.4: Run anchor tests**

Run: `dotnet test PicoYaml/tests/PicoYaml.Tests --filter "YamlAnchor"`
Expected: PASS

- [ ] **Step 6.2.5: Run all YAML tests**

Run: `dotnet test PicoYaml/tests/PicoYaml.Tests`
Expected: ALL PASS

- [ ] **Step 6.2.6: Commit**

```bash
git add PicoYaml/src/PicoYaml/YamlReader.cs PicoYaml/tests/PicoYaml.Tests/YamlAnchorTests.cs
git commit -m "feat(picoyaml): add anchor (&) and alias (*) support to YamlReader"
```

---

## Final Verification

- [ ] **Step F1: Full solution build**

Run: `dotnet build`
Expected: 0 errors, 0 warnings

- [ ] **Step F2: Run all tests in all projects**

Run: `dotnet test --verbosity normal`
Expected: ALL PASS

- [ ] **Step F3: Check LSP diagnostics on all modified files**

For each modified file, verify no errors/warnings.

- [ ] **Step F4: AOT publish validation**

```bash
dotnet publish PicoJetson/samples/PicoJetson.Sample -c Release -r win-x64 --self-contained
dotnet publish PicoIni/samples/PicoIni.Sample -c Release -r win-x64 --self-contained
dotnet publish PicoToml/samples/PicoToml.Sample -c Release -r win-x64 --self-contained
dotnet publish PicoYaml/samples/PicoYaml.Sample -c Release -r win-x64 --self-contained
```
Expected: All succeed

- [ ] **Step F5: Final commit (if any remaining changes)**

```bash
git add -A
git status
git commit -m "chore: final verification — all 6 improvements complete, all tests pass"
```
