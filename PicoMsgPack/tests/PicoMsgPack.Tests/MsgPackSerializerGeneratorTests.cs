namespace PicoMsgPack.Tests;

// Test models with integer keys
public class PersonMsgPack
{
    [MsgPackKey(0)] public string Name { get; set; } = "";
    [MsgPackKey(1)] public int Age { get; set; }
}

public class BookMsgPack
{
    [MsgPackKey(0)] public string Title { get; set; } = "";
    [MsgPackKey(1)] public int Pages { get; set; }
    [MsgPackKey(2)] public List<string> Tags { get; set; } = new();
}

public class MsgPackSerializerGeneratorTests
{
    [Test]
    public async Task Generated_Person_RoundTrip()
    {
        var person = new PersonMsgPack { Name = "Alice", Age = 30 };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(person);
        var result = MsgPackSerializer.Deserialize<PersonMsgPack>(bytes);
        await Assert.That(result!.Name).IsEqualTo("Alice");
        await Assert.That(result.Age).IsEqualTo(30);
    }

    [Test]
    public async Task Generated_Book_RoundTrip()
    {
        var book = new BookMsgPack { Title = "Dune", Pages = 412, Tags = ["sci-fi", "classic"] };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(book);
        var result = MsgPackSerializer.Deserialize<BookMsgPack>(bytes);
        await Assert.That(result!.Title).IsEqualTo("Dune");
        await Assert.That(result.Pages).IsEqualTo(412);
        await Assert.That(result.Tags).HasCount(2);
    }

    [Test]
    public async Task Generated_Person_ProducesValidMsgPack()
    {
        var person = new PersonMsgPack { Name = "Bob", Age = 25 };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(person);

        // Verify structure manually
        TokenType t1; int k0, k1, age; string v1;
        using (var reader = new MsgPackReader(bytes))
        {
            reader.Read(); t1 = reader.TokenType;
            reader.Read(); reader.TryGetInt32(out k0);
            reader.Read(); v1 = Encoding.UTF8.GetString(reader.GetStringRaw());
            reader.Read(); reader.TryGetInt32(out k1);
            reader.Read(); reader.TryGetInt32(out age);
        }
        await Assert.That(t1).IsEqualTo(TokenType.ObjectStart);
        await Assert.That(k0).IsEqualTo(0);
        await Assert.That(v1).IsEqualTo("Bob");
        await Assert.That(k1).IsEqualTo(1);
        await Assert.That(age).IsEqualTo(25);
    }
}
