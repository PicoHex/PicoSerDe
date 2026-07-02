namespace PicoJetson.Functional.Tests;

// ── Models for functional matrix ──

public class MatrixRegular
{
    public string Name { get; set; } = "";
    public int Value { get; set; }
}

public class MatrixRegularNullable
{
    public string? OptionalName { get; set; }
    public int? OptionalAge { get; set; }
}

[PicoSerializable]
[PicoDerivedType(typeof(MatrixDog), "dog")]
[PicoDerivedType(typeof(MatrixCat), "cat")]
public abstract class MatrixAnimal { }

public class MatrixDog : MatrixAnimal
{
    public string Breed { get; set; } = "";
    public int Age { get; set; }
}

public class MatrixCat : MatrixAnimal
{
    public string Color { get; set; } = "";
    public bool Indoor { get; set; }
}

public class MatrixImmutable
{
    public string Name { get; }
    public int Count { get; }

    [JsonConstructor]
    public MatrixImmutable(string name, int count)
    {
        Name = name;
        Count = count;
    }
}

// ── Functional type-vs-operation matrix ──
// Covers every TypeInfo variant × (Serialize | Deserialize | Streaming | RoundTrip)

public class FunctionalTypeMatrix
{
    // ═══ Regular type ═══

    [Test]
    public async Task Regular_Serialize_ProducesJson()
    {
        var obj = new MatrixRegular { Name = "hello", Value = 42 };
        var json = JsonSerializer.Serialize(obj);
        await Assert.That(json).Contains("\"Name\"");
        await Assert.That(json).Contains("\"hello\"");
        await Assert.That(json).Contains("42");
    }

    [Test]
    public async Task Regular_Deserialize_ReadsJson()
    {
        var data = """{"Name":"world","Value":99}"""u8;
        var result = JsonSerializer.Deserialize<MatrixRegular>(data);
        await Assert.That(result!.Name).IsEqualTo("world");
        await Assert.That(result.Value).IsEqualTo(99);
    }

    [Test]
    public async Task Regular_RoundTrip_Identity()
    {
        var original = new MatrixRegular { Name = "rt", Value = 7 };
        var json = JsonSerializer.SerializeToUtf8Bytes(original);
        var result = JsonSerializer.Deserialize<MatrixRegular>(json);
        await Assert.That(result!.Name).IsEqualTo(original.Name);
        await Assert.That(result.Value).IsEqualTo(original.Value);
    }

    [Test]
    public async Task Regular_HasStreamingDelegate_ReturnsTrue()
    {
        await Assert.That(JsonSerializer.HasStreamingDelegate<MatrixRegular>()).IsTrue();
    }

    [Test]
    public async Task Regular_Streaming_RoundTrips()
    {
        var json = """{"Name":"stream","Value":1}"""u8;
        using var stream = new MemoryStream(json.ToArray());
        var result = await JsonSerializer.DeserializeFromStreamAsync<MatrixRegular>(stream);
        await Assert.That(result.Name).IsEqualTo("stream");
        await Assert.That(result.Value).IsEqualTo(1);
    }

    // ═══ Regular type with nullables ═══

    [Test]
    public async Task RegularNullable_RoundTrip_Identity()
    {
        var original = new MatrixRegularNullable { OptionalName = "present", OptionalAge = 30 };
        var json = JsonSerializer.SerializeToUtf8Bytes(original);
        var result = JsonSerializer.Deserialize<MatrixRegularNullable>(json);

        await Assert.That(result!.OptionalName).IsEqualTo("present");
        await Assert.That(result.OptionalAge).IsEqualTo(30);
    }

    [Test]
    public async Task RegularNullable_Nulls_RoundTrip()
    {
        var original = new MatrixRegularNullable { OptionalName = null, OptionalAge = null };
        var json = JsonSerializer.SerializeToUtf8Bytes(original);
        var result = JsonSerializer.Deserialize<MatrixRegularNullable>(json);

        await Assert.That(result!.OptionalName).IsNull();
        await Assert.That(result.OptionalAge).IsNull();
    }

    // ═══ Polymorphic base ═══

    [Test]
    public async Task Poly_Serialize_WritesDiscriminator()
    {
        MatrixAnimal animal = new MatrixDog { Breed = "Lab", Age = 3 };
        var json = JsonSerializer.Serialize(animal);
        await Assert.That(json).Contains("\"$type\"");
        await Assert.That(json).Contains("\"dog\"");
        await Assert.That(json).Contains("\"Breed\"");
        await Assert.That(json).Contains("\"Lab\"");
    }

    [Test]
    public async Task Poly_Deserialize_ReturnsCorrectDerivedType()
    {
        var data = """{"$type":"cat","Color":"black","Indoor":true}"""u8;
        var result = JsonSerializer.Deserialize<MatrixAnimal>(data);

        await Assert.That(result).IsTypeOf<MatrixCat>();
        var cat = (MatrixCat)result!;
        await Assert.That(cat.Color).IsEqualTo("black");
        await Assert.That(cat.Indoor).IsTrue();
    }

    [Test]
    public async Task Poly_RoundTrip_Identity()
    {
        MatrixAnimal original = new MatrixDog { Breed = "Husky", Age = 5 };
        var json = JsonSerializer.SerializeToUtf8Bytes(original);
        var result = JsonSerializer.Deserialize<MatrixAnimal>(json);

        await Assert.That(result).IsTypeOf<MatrixDog>();
        var dog = (MatrixDog)result!;
        await Assert.That(dog.Breed).IsEqualTo("Husky");
        await Assert.That(dog.Age).IsEqualTo(5);
    }

    [Test]
    public async Task Poly_HasStreamingDelegate_ReturnsTrue()
    {
        await Assert.That(JsonSerializer.HasStreamingDelegate<MatrixAnimal>()).IsTrue();
    }

    [Test]
    public async Task Poly_Streaming_RoundTrips()
    {
        var json = """{"$type":"dog","Breed":"Pug","Age":1}"""u8;
        using var stream = new MemoryStream(json.ToArray());
        var result = await JsonSerializer.DeserializeFromStreamAsync<MatrixAnimal>(stream);

        await Assert.That(result).IsTypeOf<MatrixDog>();
        var dog = (MatrixDog)result;
        await Assert.That(dog.Breed).IsEqualTo("Pug");
        await Assert.That(dog.Age).IsEqualTo(1);
    }

    // ═══ [JsonConstructor] immutable type ═══

    [Test]
    public async Task JsonConstructor_Serialize_ProducesJson()
    {
        var obj = new MatrixImmutable("imm", 100);
        var json = JsonSerializer.Serialize(obj);
        await Assert.That(json).Contains("\"Name\"");
        await Assert.That(json).Contains("\"imm\"");
        await Assert.That(json).Contains("100");
    }

    [Test]
    public async Task JsonConstructor_Deserialize_ConstructsViaCtor()
    {
        var data = """{"Name":"ctor","Count":55}"""u8;
        var result = JsonSerializer.Deserialize<MatrixImmutable>(data);
        await Assert.That(result!.Name).IsEqualTo("ctor");
        await Assert.That(result.Count).IsEqualTo(55);
    }

    [Test]
    public async Task JsonConstructor_RoundTrip_Identity()
    {
        var original = new MatrixImmutable("rt", 42);
        var json = JsonSerializer.SerializeToUtf8Bytes(original);
        var result = JsonSerializer.Deserialize<MatrixImmutable>(json);
        await Assert.That(result!.Name).IsEqualTo(original.Name);
        await Assert.That(result.Count).IsEqualTo(original.Count);
    }

    // ═══ Top-level array ═══

    [Test]
    public async Task Array_TopLevel_String_RoundTrips()
    {
        var arr = new[] { "a", "b", "c" };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(arr);
        var result = JsonSerializer.Deserialize<string[]>(bytes);

        await Assert.That(result!).HasCount().EqualTo(3);
        await Assert.That(result[0]).IsEqualTo("a");
        await Assert.That(result[2]).IsEqualTo("c");
    }

    [Test]
    public async Task Array_TopLevel_Int_RoundTrips()
    {
        var arr = new[] { 10, -5, 0 };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(arr);
        var result = JsonSerializer.Deserialize<int[]>(bytes);

        await Assert.That(result!).HasCount().EqualTo(3);
        await Assert.That(result[0]).IsEqualTo(10);
        await Assert.That(result[2]).IsEqualTo(0);
    }

    [Test]
    public async Task Array_TopLevel_Complex_RoundTrips()
    {
        var arr = new[]
        {
            new MatrixRegular { Name = "x", Value = 1 },
            new MatrixRegular { Name = "y", Value = 2 },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(arr);
        var result = JsonSerializer.Deserialize<MatrixRegular[]>(bytes);

        await Assert.That(result!).HasCount().EqualTo(2);
        await Assert.That(result[0].Name).IsEqualTo("x");
        await Assert.That(result[1].Value).IsEqualTo(2);
    }

    [Test]
    public async Task Array_HasStreamingDelegate_ReturnsTrue()
    {
        await Assert.That(JsonSerializer.HasStreamingDelegate<string[]>()).IsTrue();
        await Assert.That(JsonSerializer.HasStreamingDelegate<int[]>()).IsTrue();
    }

    [Test]
    public async Task Array_Streaming_RoundTrips()
    {
        var json = "[1,2,3]"u8;
        using var stream = new MemoryStream(json.ToArray());
        var result = await JsonSerializer.DeserializeFromStreamAsync<int[]>(stream);

        await Assert.That(result).HasCount().EqualTo(3);
        await Assert.That(result[0]).IsEqualTo(1);
        await Assert.That(result[2]).IsEqualTo(3);
    }
}
