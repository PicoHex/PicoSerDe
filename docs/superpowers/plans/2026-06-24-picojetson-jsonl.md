# PicoJetson JSONL Support — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add JSON Lines (JSONL) read/write support to PicoJetson's `JsonSerializer` — sync batch convenience methods + streaming `IAsyncEnumerable<T>` deserializer with JSONL and JSON array modes.

**Architecture:** Three new methods on `JsonSerializer`: `SerializeLines<T>` writes each `T` as a JSON object on its own line; `DeserializeLines<T>` reads a byte span line-by-line; `DeserializeAsyncEnumerable<T>` accumulates bytes from a `Stream`, splits JSONL lines by `\n` (or array elements by bracket counting), and deserializes each value via `Cache<T>.Deserializer` (SG-generated, zero-reflection). No new parser, writer, or SG changes.

**Design Note:** `StreamingFunc<T>` is intentionally not used — the `JsonReader`'s `ref struct` nature and missing public `SkipWhitespace`/`IsAtEnd`/byte-peeking API make it unsuitable for an async enumerator's outer state-machine loop. Instead, value boundaries are identified by line splitting (`\n` for JSONL) or bracket-counting (`{`/`}`/`[`/`]` for array mode), then each complete span is deserialized via `Cache<T>.Deserializer.Deserialize(span)`.

**Tech Stack:** C# 13, .NET 10, TUnit, PicoJetson SG-generated `ISerializer<T>`/`IDeserializer<T>`.

## Global Constraints

- Target framework: net10.0
- No new NuGet dependencies
- No SG changes — reuse existing `Cache<T>.Deserializer` and `Cache<T>.Serializer`
- All JSONL values must be valid JSON per RFC 8259 / jsonlines.org
- Follow existing code patterns in `JsonSerializer.cs`
- All methods added to the existing `JsonSerializer` static class

---

### Task 1: SerializeLines\<T\> — Sync Batch Serialization

**Files:**
- Create: `PicoJetson/tests/PicoJetson.Tests/JsonLinesTests.cs`
- Modify: `PicoJetson/src/PicoJetson/JsonSerializer.cs`

**Interfaces:**
- Produces: `byte[] JsonSerializer.SerializeLines<T>(IEnumerable<T> values, JsonOptions? options = null)`

---

- [ ] **Step 1: Write the failing test for SerializeLines**

Create `PicoJetson/tests/PicoJetson.Tests/JsonLinesTests.cs`:

```csharp
namespace PicoJetson.Tests;

public class JsonLinesTests
{
    public class Person
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }
    }

    // Manual serializer/deserializer for Person (same pattern as JsonSerializerTests)
    private readonly struct PersonSerializer : ISerializer<Person>
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

    private readonly struct PersonDeserializer : IDeserializer<Person?>
    {
        public Person? Deserialize(ReadOnlySpan<byte> data)
        {
            var reader = new JsonReader(data);
            reader.Read();
            if (reader.TokenType == TokenType.Null)
                return null;
            var obj = new Person();
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

    [Test]
    public async Task SerializeLines_EmptyCollection_ReturnsEmptyBytes()
    {
        JsonSerializer.Register<Person>(new PersonSerializer(), new PersonDeserializer());

        var result = JsonSerializer.SerializeLines<Person>(Array.Empty<Person>());

        await Assert.That(result.Length).IsEqualTo(0);
    }

    [Test]
    public async Task SerializeLines_SingleItem_ReturnsOneLine()
    {
        JsonSerializer.Register<Person>(new PersonSerializer(), new PersonDeserializer());

        var people = new[] { new Person { Name = "Alice", Age = 30 } };
        var result = JsonSerializer.SerializeLines(people);

        var text = Encoding.UTF8.GetString(result);
        await Assert.That(text).DoesNotContain("\n\n");
        await Assert.That(text.EndsWith('\n')).IsTrue();
    }

    [Test]
    public async Task SerializeLines_MultipleItems_ReturnsNewlineSeparatedJson()
    {
        JsonSerializer.Register<Person>(new PersonSerializer(), new PersonDeserializer());

        var people = new[]
        {
            new Person { Name = "Alice", Age = 30 },
            new Person { Name = "Bob", Age = 25 },
        };
        var result = JsonSerializer.SerializeLines(people);

        var text = Encoding.UTF8.GetString(result);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines.Length).IsEqualTo(2);
        await Assert.That(lines[0]).Contains("Alice");
        await Assert.That(lines[1]).Contains("Bob");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd D:/MyProjects/PicoHex/PicoSerDe && dotnet test PicoJetson/tests/PicoJetson.Tests/PicoJetson.Tests.csproj --filter "FullyQualifiedName~JsonLinesTests" --no-restore 2>&1 | tail -20
```

Expected: 3 FAIL (no `SerializeLines` method exists)

- [ ] **Step 3: Implement `SerializeLines<T>` in JsonSerializer.cs**

Add the following to `PicoJetson/src/PicoJetson/JsonSerializer.cs`, inside the `JsonSerializer` class, after the existing `DeserializeFromStreamAsync<T>` method:

```csharp
/// <summary>
/// Serializes each value in <paramref name="values"/> as a JSON line,
/// separated by <c>'\n'</c>. Returns a JSONL byte array.
/// </summary>
public static byte[] SerializeLines<T>(
    IEnumerable<T> values,
    JsonOptions? options = null
)
{
    var prev = JsonOptions.Current;
    JsonOptions.Current = options;
    try
    {
        var buf = new ArrayBufferWriter<byte>(1024);
        foreach (var v in values)
        {
            if (Cache<T>.Serializer is { } s)
            {
                s.Serialize(buf, v);
                buf.Write("\n"u8);
            }
            else
            {
                SerializerExtensions.ThrowNoSerializer<T>("PicoJetson.Gen");
            }
        }
        return buf.WrittenSpan.ToArray();
    }
    finally
    {
        JsonOptions.Current = prev;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
cd D:/MyProjects/PicoHex/PicoSerDe && dotnet test PicoJetson/tests/PicoJetson.Tests/PicoJetson.Tests.csproj --filter "FullyQualifiedName~JsonLinesTests" --no-restore 2>&1 | tail -20
```

Expected: 3 PASS

- [ ] **Step 5: Commit**

```bash
cd D:/MyProjects/PicoHex/PicoSerDe && git add PicoJetson/src/PicoJetson/JsonSerializer.cs PicoJetson/tests/PicoJetson.Tests/JsonLinesTests.cs && git commit -m "feat(picojetson): add JsonSerializer.SerializeLines<T> for JSONL batch serialization"
```

---

### Task 2: DeserializeLines\<T\> — Sync Batch Deserialization

**Files:**
- Modify: `PicoJetson/tests/PicoJetson.Tests/JsonLinesTests.cs` (add tests)
- Modify: `PicoJetson/src/PicoJetson/JsonSerializer.cs`

**Interfaces:**
- Consumes: `SerializeLines<T>` from Task 1 (for test data generation)
- Produces: `T?[] JsonSerializer.DeserializeLines<T>(ReadOnlySpan<byte> data, JsonOptions? options = null)`

---

- [ ] **Step 1: Write failing tests for DeserializeLines**

Append to `PicoJetson/tests/PicoJetson.Tests/JsonLinesTests.cs`:

```csharp
    [Test]
    public async Task DeserializeLines_EmptyData_ReturnsEmptyArray()
    {
        JsonSerializer.Register<Person>(new PersonSerializer(), new PersonDeserializer());

        var result = JsonSerializer.DeserializeLines<Person>(ReadOnlySpan<byte>.Empty);

        await Assert.That(result.Length).IsEqualTo(0);
    }

    [Test]
    public async Task DeserializeLines_SingleLine_ReturnsOneItem()
    {
        JsonSerializer.Register<Person>(new PersonSerializer(), new PersonDeserializer());

        var jsonl = JsonSerializer.SerializeLines(
            new[] { new Person { Name = "Alice", Age = 30 } });
        var result = JsonSerializer.DeserializeLines<Person>(jsonl);

        await Assert.That(result.Length).IsEqualTo(1);
        await Assert.That(result[0]!.Name).IsEqualTo("Alice");
        await Assert.That(result[0]!.Age).IsEqualTo(30);
    }

    [Test]
    public async Task DeserializeLines_MultipleLines_ReturnsAllItems()
    {
        JsonSerializer.Register<Person>(new PersonSerializer(), new PersonDeserializer());

        var people = new[]
        {
            new Person { Name = "Alice", Age = 30 },
            new Person { Name = "Bob", Age = 25 },
            new Person { Name = "Charlie", Age = 35 },
        };
        var jsonl = JsonSerializer.SerializeLines(people);
        var result = JsonSerializer.DeserializeLines<Person>(jsonl);

        await Assert.That(result.Length).IsEqualTo(3);
        await Assert.That(result[0]!.Name).IsEqualTo("Alice");
        await Assert.That(result[1]!.Name).IsEqualTo("Bob");
        await Assert.That(result[2]!.Name).IsEqualTo("Charlie");
    }

    [Test]
    public async Task DeserializeLines_NullValuesInStream_ReturnsNullableNulls()
    {
        JsonSerializer.Register<Person>(new PersonSerializer(), new PersonDeserializer());

        // "null" is a valid JSON value in JSONL
        var data = "null\n"u8.ToArray();
        var result = JsonSerializer.DeserializeLines<Person>(data);

        await Assert.That(result.Length).IsEqualTo(1);
        await Assert.That(result[0]).IsNull();
    }
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd D:/MyProjects/PicoHex/PicoSerDe && dotnet test PicoJetson/tests/PicoJetson.Tests/PicoJetson.Tests.csproj --filter "FullyQualifiedName~JsonLinesTests" --no-restore 2>&1 | tail -20
```

Expected: 4 new tests FAIL (no `DeserializeLines` method)

- [ ] **Step 3: Implement `DeserializeLines<T>` in JsonSerializer.cs**

Add this method to `JsonSerializer.cs`, right after `SerializeLines<T>`:

```csharp
/// <summary>
/// Deserializes each line of a JSONL byte span into an array of <typeparamref name="T"/>.
/// Empty lines are skipped. Each line must be a complete, valid JSON value.
/// </summary>
public static T?[] DeserializeLines<T>(
    ReadOnlySpan<byte> data,
    JsonOptions? options = null
)
{
    if (data.IsEmpty)
        return [];

    var prev = JsonOptions.Current;
    JsonOptions.Current = options;
    try
    {
        var results = new List<T?>();
        var remaining = data;

        while (remaining.Length > 0)
        {
            int newlineIdx = remaining.IndexOf((byte)'\n');
            ReadOnlySpan<byte> line;
            if (newlineIdx >= 0)
            {
                line = remaining[..newlineIdx];
                remaining = remaining[(newlineIdx + 1)..];
            }
            else
            {
                line = remaining;
                remaining = default;
            }

            if (line.IsEmpty)
                continue;

            if (Cache<T>.Deserializer is { } d)
            {
                results.Add(d.Deserialize(line));
            }
            else
            {
                SerializerExtensions.ThrowNoSerializer<T>("PicoJetson.Gen");
            }
        }

        return results.ToArray();
    }
    finally
    {
        JsonOptions.Current = prev;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
cd D:/MyProjects/PicoHex/PicoSerDe && dotnet test PicoJetson/tests/PicoJetson.Tests/PicoJetson.Tests.csproj --filter "FullyQualifiedName~JsonLinesTests" --no-restore 2>&1 | tail -20
```

Expected: all 7 tests PASS

- [ ] **Step 5: Commit**

```bash
cd D:/MyProjects/PicoHex/PicoSerDe && git add PicoJetson/src/PicoJetson/JsonSerializer.cs PicoJetson/tests/PicoJetson.Tests/JsonLinesTests.cs && git commit -m "feat(picojetson): add JsonSerializer.DeserializeLines<T> for JSONL batch deserialization"
```

---

### Task 3: DeserializeAsyncEnumerable\<T\> — Streaming JSONL Mode

**Files:**
- Modify: `PicoJetson/tests/PicoJetson.Tests/JsonLinesTests.cs` (add tests)
- Modify: `PicoJetson/src/PicoJetson/JsonSerializer.cs`

**Interfaces:**
- Consumes: `Cache<T>.Deserializer` (already SG-generated + manually registered in tests)
- Produces: `IAsyncEnumerable<T?> JsonSerializer.DeserializeAsyncEnumerable<T>(Stream stream, bool topLevelValues = true, JsonOptions? options = null, CancellationToken ct = default)`

**Internal approach:** Accumulate bytes from stream into a growable buffer. For JSONL mode (`topLevelValues: true`): find `\n` boundaries, extract complete lines, deserialize each via `IDeserializer<T>.Deserialize(span)`. Track consumed bytes and trim buffer. For array mode: handled in Task 4.

---

- [ ] **Step 1: Write failing tests for streaming JSONL deserialization**

Append to `PicoJetson/tests/PicoJetson.Tests/JsonLinesTests.cs`:

```csharp
    [Test]
    public async Task DeserializeAsyncEnumerable_JsonlMode_StreamsAllItems()
    {
        JsonSerializer.Register<Person>(new PersonSerializer(), new PersonDeserializer());

        var jsonl = JsonSerializer.SerializeLines(new[]
        {
            new Person { Name = "Alice", Age = 30 },
            new Person { Name = "Bob", Age = 25 },
        });
        using var stream = new MemoryStream(jsonl);

        var results = new List<Person?>();
        await foreach (var person in JsonSerializer.DeserializeAsyncEnumerable<Person>(stream))
        {
            results.Add(person);
        }

        await Assert.That(results.Count).IsEqualTo(2);
        await Assert.That(results[0]!.Name).IsEqualTo("Alice");
        await Assert.That(results[1]!.Name).IsEqualTo("Bob");
    }

    [Test]
    public async Task DeserializeAsyncEnumerable_JsonlMode_EmptyStream_ReturnsNothing()
    {
        JsonSerializer.Register<Person>(new PersonSerializer(), new PersonDeserializer());

        using var stream = new MemoryStream(Array.Empty<byte>());

        var results = new List<Person?>();
        await foreach (var person in JsonSerializer.DeserializeAsyncEnumerable<Person>(stream))
        {
            results.Add(person);
        }

        await Assert.That(results.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DeserializeAsyncEnumerable_JsonlMode_SingleItem_Works()
    {
        JsonSerializer.Register<Person>(new PersonSerializer(), new PersonDeserializer());

        var jsonl = JsonSerializer.SerializeLines(
            new[] { new Person { Name = "Alice", Age = 30 } });
        using var stream = new MemoryStream(jsonl);

        var results = new List<Person?>();
        await foreach (var person in JsonSerializer.DeserializeAsyncEnumerable<Person>(stream))
        {
            results.Add(person);
        }

        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0]!.Name).IsEqualTo("Alice");
    }
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd D:/MyProjects/PicoHex/PicoSerDe && dotnet test PicoJetson/tests/PicoJetson.Tests/PicoJetson.Tests.csproj --filter "FullyQualifiedName~JsonLinesTests" --no-restore 2>&1 | tail -20
```

Expected: 3 new tests FAIL (no `DeserializeAsyncEnumerable` method)

- [ ] **Step 3: Implement `DeserializeAsyncEnumerable<T>` in JsonSerializer.cs**

Add the public entry point and private async enumerator to `JsonSerializer.cs`, after `DeserializeLines<T>`:

```csharp
/// <summary>
/// Wraps a UTF-8 stream into an <see cref="IAsyncEnumerable{T}"/> that yields one
/// <typeparamref name="T"/> per top-level JSON value (JSONL mode, <c>topLevelValues = true</c>)
/// or per root-level array element (JSON array mode, <c>topLevelValues = false</c>).
/// Requires a deserializer to be registered for <typeparamref name="T"/>
/// (auto-registered by the SG via <c>ModuleInitializer</c>).
/// </summary>
public static IAsyncEnumerable<T?> DeserializeAsyncEnumerable<T>(
    Stream stream,
    bool topLevelValues = true,
    JsonOptions? options = null,
    CancellationToken ct = default
)
{
    return DeserializeAsyncEnumerableImpl<T>(stream, topLevelValues, options, ct);
}

private static async IAsyncEnumerable<T?> DeserializeAsyncEnumerableImpl<T>(
    Stream stream,
    bool topLevelValues,
    JsonOptions? options,
    [EnumeratorCancellation] CancellationToken ct
)
{
    // Verify deserializer is registered before entering the loop
    if (Cache<T>.Deserializer is not { } deserializer)
    {
        SerializerExtensions.ThrowNoSerializer<T>("PicoJetson.Gen");
        yield break;
    }

    var prev = JsonOptions.Current;
    JsonOptions.Current = options;
    try
    {
        if (topLevelValues)
        {
            // ── JSONL mode: split by '\n' ──
            var readBuf = new byte[4096];
            var accum = new List<byte>(4096);

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int bytesRead = await stream.ReadAsync(readBuf, ct);
                if (bytesRead == 0)
                {
                    // Stream ended — process any remaining partial line
                    if (accum.Count > 0)
                    {
                        yield return deserializer.Deserialize(
                            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(accum));
                    }
                    yield break;
                }

                accum.AddRange(readBuf.AsSpan(0, bytesRead));
                var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(accum);

                // Process all complete lines
                int consumed = 0;
                while (consumed < span.Length)
                {
                    int nlPos = span[consumed..].IndexOf((byte)'\n');
                    if (nlPos < 0)
                        break;

                    int lineStart = consumed;
                    int lineEnd = consumed + nlPos;
                    consumed = lineEnd + 1; // skip the \n

                    var line = span[lineStart..lineEnd];
                    if (!line.IsEmpty)
                    {
                        yield return deserializer.Deserialize(line);
                    }
                }

                // Keep the unconsumed remainder
                if (consumed > 0)
                {
                    if (consumed < span.Length)
                    {
                        var rest = span[consumed..].ToArray();
                        accum.Clear();
                        accum.AddRange(rest);
                    }
                    else
                    {
                        accum.Clear();
                    }
                }
            }
        }
        else
        {
            // ── Array mode (implemented in Task 4) ──
            // Placeholder: handle [] array mode here
            yield break;
        }
    }
    finally
    {
        JsonOptions.Current = prev;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
cd D:/MyProjects/PicoHex/PicoSerDe && dotnet test PicoJetson/tests/PicoJetson.Tests/PicoJetson.Tests.csproj --filter "FullyQualifiedName~JsonLinesTests" --no-restore 2>&1 | tail -20
```

Expected: all 10 tests PASS (3 serialize + 4 deserialize + 3 streaming)

- [ ] **Step 5: Commit**

```bash
cd D:/MyProjects/PicoHex/PicoSerDe && git add PicoJetson/src/PicoJetson/JsonSerializer.cs PicoJetson/tests/PicoJetson.Tests/JsonLinesTests.cs && git commit -m "feat(picojetson): add JsonSerializer.DeserializeAsyncEnumerable<T> for streaming JSONL"
```

---

### Task 4: DeserializeAsyncEnumerable\<T\> — Streaming JSON Array Mode

**Files:**
- Modify: `PicoJetson/tests/PicoJetson.Tests/JsonLinesTests.cs` (add array-mode tests)
- Modify: `PicoJetson/src/PicoJetson/JsonSerializer.cs` (extend `else` branch from Task 3)

**Interfaces:**
- Verifies: `DeserializeAsyncEnumerable<T>` with `topLevelValues = false`

---

- [ ] **Step 1: Write failing tests for array mode**

Append to `PicoJetson/tests/PicoJetson.Tests/JsonLinesTests.cs`:

```csharp
    [Test]
    public async Task DeserializeAsyncEnumerable_ArrayMode_SingleElement_Works()
    {
        JsonSerializer.Register<Person>(new PersonSerializer(), new PersonDeserializer());

        var json = "[\n  {\"Name\":\"Alice\",\"Age\":30}\n]"u8.ToArray();
        using var stream = new MemoryStream(json);

        var results = new List<Person?>();
        await foreach (var person in JsonSerializer.DeserializeAsyncEnumerable<Person>(
            stream, topLevelValues: false))
        {
            results.Add(person);
        }

        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0]!.Name).IsEqualTo("Alice");
    }

    [Test]
    public async Task DeserializeAsyncEnumerable_ArrayMode_MultipleElements_Works()
    {
        JsonSerializer.Register<Person>(new PersonSerializer(), new PersonDeserializer());

        var json = "[\n  {\"Name\":\"Alice\",\"Age\":30},\n  {\"Name\":\"Bob\",\"Age\":25}\n]"u8.ToArray();
        using var stream = new MemoryStream(json);

        var results = new List<Person?>();
        await foreach (var person in JsonSerializer.DeserializeAsyncEnumerable<Person>(
            stream, topLevelValues: false))
        {
            results.Add(person);
        }

        await Assert.That(results.Count).IsEqualTo(2);
        await Assert.That(results[0]!.Name).IsEqualTo("Alice");
        await Assert.That(results[1]!.Name).IsEqualTo("Bob");
    }

    [Test]
    public async Task DeserializeAsyncEnumerable_ArrayMode_EmptyArray_ReturnsNothing()
    {
        JsonSerializer.Register<Person>(new PersonSerializer(), new PersonDeserializer());

        var json = "[]"u8.ToArray();
        using var stream = new MemoryStream(json);

        var results = new List<Person?>();
        await foreach (var person in JsonSerializer.DeserializeAsyncEnumerable<Person>(
            stream, topLevelValues: false))
        {
            results.Add(person);
        }

        await Assert.That(results.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DeserializeAsyncEnumerable_ArrayMode_MissingOpenBracket_Throws()
    {
        JsonSerializer.Register<Person>(new PersonSerializer(), new PersonDeserializer());

        var json = "{\"Name\":\"Alice\"}"u8.ToArray();
        using var stream = new MemoryStream(json);

        await Assert.ThrowsAsync<FormatException>(async () =>
        {
            await foreach (var _ in JsonSerializer.DeserializeAsyncEnumerable<Person>(
                stream, topLevelValues: false))
            {
            }
        });
    }
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd D:/MyProjects/PicoHex/PicoSerDe && dotnet test PicoJetson/tests/PicoJetson.Tests/PicoJetson.Tests.csproj --filter "FullyQualifiedName~JsonLinesTests" --no-restore 2>&1 | tail -20
```

Expected: 4 new array-mode tests FAIL (the `else` branch in `DeserializeAsyncEnumerableImpl` is a stub)

- [ ] **Step 3: Implement array mode in the `else` branch**

Replace the entire `else` block (the `// ── Array mode ──` section) in `DeserializeAsyncEnumerableImpl`:

```csharp
        else
        {
            // ── Array mode: bracket-counting value extraction ──
            var readBuf = new byte[4096];
            var accum = new List<byte>(4096);
            bool sawOpeningBracket = false;
            // Track nested bracket depth; starts at 0. For top-level array,
            // '[' increments to 1, then each '{'/'[' inside elements adds more.
            // When depth returns to 0 (after ']'), we're done.
            int depth = 0;
            int valueStart = -1; // index in accum where current value started

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int bytesRead = await stream.ReadAsync(readBuf, ct);
                if (bytesRead == 0)
                {
                    if (!sawOpeningBracket)
                        throw new FormatException("Expected '[' at start of JSON array stream.");
                    // Handle trailing partial value (unexpected EOF inside array)
                    if (depth > 0)
                        throw new FormatException("Unexpected end of stream inside JSON array.");
                    yield break;
                }

                accum.AddRange(readBuf.AsSpan(0, bytesRead));
                var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(accum);

                int i = 0;
                while (i < span.Length)
                {
                    byte b = span[i];

                    if (!sawOpeningBracket)
                    {
                        // Skip leading whitespace
                        if (b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r')
                        {
                            i++;
                            continue;
                        }
                        if (b == (byte)'[')
                        {
                            sawOpeningBracket = true;
                            depth = 1;
                            i++;
                            continue;
                        }
                        throw new FormatException(
                            "Expected '[' at start of JSON array stream.");
                    }

                    // ── Track brackets to find value boundaries ──
                    if (b == (byte)'"')
                    {
                        // Skip string contents (including escaped quotes)
                        i++;
                        while (i < span.Length)
                        {
                            if (span[i] == (byte)'\\')
                            {
                                i += 2; // skip escape sequence
                                continue;
                            }
                            if (span[i] == (byte)'"')
                            {
                                i++; // past closing quote
                                break;
                            }
                            i++;
                        }
                        continue;
                    }

                    if (b is (byte)'{' or (byte)'[')
                    {
                        if (depth == 1)
                        {
                            // Start of a top-level value in the array
                            valueStart = i;
                        }
                        depth++;
                        i++;
                        continue;
                    }

                    if (b is (byte)'}' or (byte)']')
                    {
                        depth--;
                        if (depth == 0 && b == (byte)']')
                        {
                            // Array closing bracket: any trailing value?
                            if (valueStart >= 0)
                            {
                                var valSpan = span[valueStart..i];
                                // Trim trailing whitespace
                                while (valSpan.Length > 0 && valSpan[^1] <= 32)
                                    valSpan = valSpan[..^1];
                                if (!valSpan.IsEmpty)
                                {
                                    yield return deserializer.Deserialize(valSpan);
                                }
                            }
                            // Discard processed data
                            accum.RemoveRange(0, i + 1);
                            yield break; // array complete
                        }
                        if (depth == 1 && b == (byte)'}')
                        {
                            // End of a top-level value
                            var valSpan = span[valueStart..(i + 1)];
                            yield return deserializer.Deserialize(valSpan);
                            valueStart = -1;
                            // Consume up to after this value (including trailing comma)
                            int consumed = i + 1;
                            // skip whitespace and optional comma
                            while (consumed < span.Length && span[consumed] <= 32)
                                consumed++;
                            if (consumed < span.Length && span[consumed] == (byte)',')
                                consumed++;
                            accum.RemoveRange(0, consumed);
                            span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(accum);
                            i = 0;
                            continue;
                        }
                        i++;
                        continue;
                    }

                    i++;
                }

                // No complete value found in this chunk.
                // Trim processed bytes if we yielded something.
                if (valueStart < 0 && depth == 1)
                {
                    // No value started yet — discard whitespace/comma prefix
                    int trim = 0;
                    while (trim < accum.Count && accum[trim] <= 32)
                        trim++;
                    if (trim < accum.Count && accum[trim] == (byte)',')
                        trim++;
                    if (trim > 0)
                        accum.RemoveRange(0, trim);
                }
                // Otherwise keep accum for next read cycle
            }
        }
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
cd D:/MyProjects/PicoHex/PicoSerDe && dotnet test PicoJetson/tests/PicoJetson.Tests/PicoJetson.Tests.csproj --filter "FullyQualifiedName~JsonLinesTests" --no-restore 2>&1 | tail -30
```

Expected: all 14 tests PASS

- [ ] **Step 5: Commit**

```bash
cd D:/MyProjects/PicoHex/PicoSerDe && git add PicoJetson/src/PicoJetson/JsonSerializer.cs PicoJetson/tests/PicoJetson.Tests/JsonLinesTests.cs && git commit -m "feat(picojetson): add array mode to DeserializeAsyncEnumerable<T>"
```

---

### Task 5: Edge Cases — Cancellation, \r\n, Options Propagation

**Files:**
- Modify: `PicoJetson/tests/PicoJetson.Tests/JsonLinesTests.cs`
- Modify: `PicoJetson/src/PicoJetson/JsonSerializer.cs` (only if bugs found)

---

- [ ] **Step 1: Write edge case tests**

Append to `PicoJetson/tests/PicoJetson.Tests/JsonLinesTests.cs`:

```csharp
    [Test]
    public async Task DeserializeAsyncEnumerable_CancellationToken_ThrowsOperationCanceled()
    {
        JsonSerializer.Register<Person>(new PersonSerializer(), new PersonDeserializer());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var jsonl = "{\"Name\":\"Alice\",\"Age\":30}\n"u8.ToArray();
        using var stream = new MemoryStream(jsonl);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in JsonSerializer.DeserializeAsyncEnumerable<Person>(
                stream, ct: cts.Token))
            {
            }
        });
    }

    [Test]
    public async Task DeserializeLines_HandlesCrlfLineEndings()
    {
        JsonSerializer.Register<Person>(new PersonSerializer(), new PersonDeserializer());

        var data = "{\"Name\":\"Alice\",\"Age\":30}\r\n"u8.ToArray();
        var result = JsonSerializer.DeserializeLines<Person>(data);

        await Assert.That(result.Length).IsEqualTo(1);
        await Assert.That(result[0]!.Name).IsEqualTo("Alice");
    }

    [Test]
    public async Task DeserializeLines_LastLineWithoutNewline_Works()
    {
        JsonSerializer.Register<Person>(new PersonSerializer(), new PersonDeserializer());

        var data = "{\"Name\":\"Alice\",\"Age\":30}\n{\"Name\":\"Bob\",\"Age\":25}"u8.ToArray();
        var result = JsonSerializer.DeserializeLines<Person>(data);

        await Assert.That(result.Length).IsEqualTo(2);
        await Assert.That(result[0]!.Name).IsEqualTo("Alice");
        await Assert.That(result[1]!.Name).IsEqualTo("Bob");
    }

    [Test]
    public async Task DeserializeLines_EmptyLines_Skipped()
    {
        JsonSerializer.Register<Person>(new PersonSerializer(), new PersonDeserializer());

        var data = "\n\n{\"Name\":\"Alice\",\"Age\":30}\n\n{\"Name\":\"Bob\",\"Age\":25}\n\n"u8.ToArray();
        var result = JsonSerializer.DeserializeLines<Person>(data);

        await Assert.That(result.Length).IsEqualTo(2);
    }

    [Test]
    public async Task SerializeLines_RoundTrip_PreservesData()
    {
        JsonSerializer.Register<Person>(new PersonSerializer(), new PersonDeserializer());

        var original = new[]
        {
            new Person { Name = "Alice", Age = 30 },
            new Person { Name = "Bob", Age = 25 },
            new Person { Name = "Charlie", Age = 35 },
        };
        var jsonl = JsonSerializer.SerializeLines(original);
        var restored = JsonSerializer.DeserializeLines<Person>(jsonl);

        await Assert.That(restored.Length).IsEqualTo(original.Length);
        for (int i = 0; i < original.Length; i++)
        {
            await Assert.That(restored[i]!.Name).IsEqualTo(original[i].Name);
            await Assert.That(restored[i]!.Age).IsEqualTo(original[i].Age);
        }
    }
```

- [ ] **Step 2: Run tests and fix any failures**

```bash
cd D:/MyProjects/PicoHex/PicoSerDe && dotnet test PicoJetson/tests/PicoJetson.Tests/PicoJetson.Tests.csproj --filter "FullyQualifiedName~JsonLinesTests" --no-restore 2>&1 | tail -30
```

Evaluate failures and fix implementation if needed. Expected: all tests PASS.

- [ ] **Step 3: Commit**

```bash
cd D:/MyProjects/PicoHex/PicoSerDe && git add PicoJetson/tests/PicoJetson.Tests/JsonLinesTests.cs && git commit -m "test(picojetson): add JSONL edge-case tests — cancellation, CRLF, empty lines, round-trip"
```

---

### Task 6: Final Verification — Full Test Suite

**Files:**
- All touched files from Tasks 1-5

---

- [ ] **Step 1: Run full PicoJetson test suite**

```bash
cd D:/MyProjects/PicoHex/PicoSerDe && dotnet test PicoJetson/tests/PicoJetson.Tests/PicoJetson.Tests.csproj --no-restore 2>&1 | tail -20
```

Expected: ALL tests pass (existing + new JSONL tests)

- [ ] **Step 2: Run full solution build to verify no regressions**

```bash
cd D:/MyProjects/PicoHex/PicoSerDe && dotnet build --no-restore 2>&1 | tail -20
```

Expected: Build succeeded with 0 errors

- [ ] **Step 3: Verify AOT compatibility**

```bash
cd D:/MyProjects/PicoHex/PicoSerDe && dotnet publish PicoJetson/tests/PicoJetson.Tests/PicoJetson.Tests.csproj -c Release --no-restore 2>&1 | tail -10
```

Expected: Publish succeeded (verifies no reflection or AOT-unfriendly patterns were introduced)

- [ ] **Step 4: Commit**

```bash
cd D:/MyProjects/PicoHex/PicoSerDe && git status
```
