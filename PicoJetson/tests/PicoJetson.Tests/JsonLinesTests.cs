namespace PicoJetson.Tests;

public class JsonLinesTests
{
    public class Person
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }
    }

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

    private readonly struct PersonDeserializer : IDeserializer<Person>
    {
        public Person Deserialize(ReadOnlySpan<byte> data)
        {
            var reader = new JsonReader(data);
            reader.Read();
            if (reader.TokenType == TokenType.Null)
                return null!;
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

        var data = "null\n"u8.ToArray();
        var result = JsonSerializer.DeserializeLines<Person>(data);

        await Assert.That(result.Length).IsEqualTo(1);
        await Assert.That(result[0]).IsNull();
    }

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
}
