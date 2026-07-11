namespace PicoIni.Tests;

public class IniSerializerUnifiedTests
{
    public class SimpleConfig
    {
        public string Title { get; set; } = string.Empty;
        public int Port { get; set; }
    }

    public class ServerSection
    {
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
    }

    public class AppConfig
    {
        public string Title { get; set; } = string.Empty;
        public ServerSection Server { get; set; } = new();
    }

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
                    reader.Read();
                    if (key.SequenceEqual("Title"u8) && reader.TokenType == TokenType.String)
                        obj.Title = Encoding.UTF8.GetString(reader.GetStringRaw());
                    else if (key.SequenceEqual("Port"u8))
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
                    reader.Read();
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
                                reader.Read();
                                if (
                                    sk.SequenceEqual("Host"u8)
                                    && reader.TokenType == TokenType.String
                                )
                                    config.Server.Host = Encoding.UTF8.GetString(
                                        reader.GetStringRaw()
                                    );
                                else if (sk.SequenceEqual("Port"u8))
                                {
                                    reader.TryGetInt32(out var sp);
                                    config.Server.Port = sp;
                                }
                            }
                            else if (
                                reader.TokenType is TokenType.ObjectStart or TokenType.ObjectEnd
                            )
                                break;
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
        var config = new AppConfig
        {
            Title = "MyApp",
            Server = new ServerSection { Host = "localhost", Port = 8080 },
        };
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
    public async Task BoolValue_ParsesCorrectly()
    {
        var data = "Enabled = true"u8.ToArray();
        TokenType tt;
        bool b;
        using (var reader = new IniReader(data))
        {
            reader.Read();
            tt = reader.TokenType;
            reader.Read();
            reader.TryGetBool(out b);
        }
        await Assert.That(tt).IsEqualTo(TokenType.PropertyName);
        await Assert.That(b).IsTrue();
    }

    [Test]
    public async Task ManyScalarConfig_RoundTrip_UsesGeneratedDispatch()
    {
        var original = new ManyScalarConfig
        {
            Alpha = "a",
            Beta = 2,
            Gamma = true,
            Delta = 4,
            Epsilon = 5.5,
            Zeta = "z",
        };

        var bytes = IniSerializer.SerializeToUtf8Bytes(original);
        var result = IniSerializer.Deserialize<ManyScalarConfig>(bytes);

        await Assert.That(result!.Alpha).IsEqualTo("a");
        await Assert.That(result.Beta).IsEqualTo(2);
        await Assert.That(result.Gamma).IsTrue();
        await Assert.That(result.Delta).IsEqualTo(4);
        await Assert.That(result.Epsilon).IsEqualTo(5.5);
        await Assert.That(result.Zeta).IsEqualTo("z");
    }
}

public class ManyScalarConfig
{
    public string Alpha { get; set; } = string.Empty;
    public int Beta { get; set; }
    public bool Gamma { get; set; }
    public long Delta { get; set; }
    public double Epsilon { get; set; }
    public string Zeta { get; set; } = string.Empty;
}

public class IniPoco
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
}

public class IniRichPoco
{
    public string Label { get; set; } = string.Empty;
    public int Count { get; set; }
    public long Big { get; set; }
    public double Score { get; set; }
    public bool Flag { get; set; }
}

public class IniFullPoco
{
    public string Label { get; set; } = string.Empty;
    public DateTime Created { get; set; }
    public Guid Id { get; set; }
    public DayOfWeek Day { get; set; }
    public decimal Price { get; set; }
}

// ── Top-level List<T> serialization (regression: CS0305 with generic type args) ──

public class IniSerializerTopLevelListTests
{
    [Test]
    public async Task SerializeDeserialize_TopLevelList_Int_Roundtrips()
    {
        var list = new List<int> { 1, 42, -7 };
        var bytes = IniSerializer.SerializeToUtf8Bytes(list);
        var result = IniSerializer.Deserialize<List<int>>(bytes);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!).HasCount().EqualTo(3);
        await Assert.That(result[0]).IsEqualTo(1);
        await Assert.That(result[2]).IsEqualTo(-7);
    }

    [Test]
    public async Task SerializeDeserialize_TopLevelList_ObjectElement_Roundtrips()
    {
        var list = new List<IniPoco>
        {
            new() { Name = "Alice", Age = 30 },
            new() { Name = "Bob", Age = 25 },
        };
        var bytes = IniSerializer.SerializeToUtf8Bytes(list);
        var result = IniSerializer.Deserialize<List<IniPoco>>(bytes);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!).HasCount().EqualTo(2);
        await Assert.That(result[0].Name).IsEqualTo("Alice");
        await Assert.That(result[0].Age).IsEqualTo(30);
        await Assert.That(result[1].Name).IsEqualTo("Bob");
        await Assert.That(result[1].Age).IsEqualTo(25);
    }

    [Test]
    public async Task SerializeDeserialize_TopLevelList_MultiTypeElement_Roundtrips()
    {
        var list = new List<IniRichPoco>
        {
            new()
            {
                Label = "A",
                Count = 1,
                Big = 100L,
                Score = 3.14,
                Flag = true,
            },
            new()
            {
                Label = "B",
                Count = 2,
                Big = 200L,
                Score = 2.71,
                Flag = false,
            },
        };
        var bytes = IniSerializer.SerializeToUtf8Bytes(list);
        var result = IniSerializer.Deserialize<List<IniRichPoco>>(bytes);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!).HasCount().EqualTo(2);
        await Assert.That(result[0].Label).IsEqualTo("A");
        await Assert.That(result[0].Count).IsEqualTo(1);
        await Assert.That(result[0].Big).IsEqualTo(100L);
        await Assert.That(result[0].Score).IsEqualTo(3.14);
        await Assert.That(result[0].Flag).IsEqualTo(true);
        await Assert.That(result[1].Flag).IsEqualTo(false);
    }

    [Test]
    public async Task SerializeDeserialize_TopLevelList_FullTypeElement_Roundtrips()
    {
        var dt = new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Utc);
        var g = Guid.NewGuid();
        var list = new List<IniFullPoco>
        {
            new()
            {
                Label = "X",
                Created = dt,
                Id = g,
                Day = DayOfWeek.Friday,
                Price = 99.99m,
            },
        };
        var bytes = IniSerializer.SerializeToUtf8Bytes(list);
        var result = IniSerializer.Deserialize<List<IniFullPoco>>(bytes);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!).HasCount().EqualTo(1);
        await Assert.That(result[0].Label).IsEqualTo("X");
        await Assert.That(result[0].Created.Year).IsEqualTo(2024);
        await Assert.That(result[0].Id).IsEqualTo(g);
        await Assert.That(result[0].Day).IsEqualTo(DayOfWeek.Friday);
        await Assert.That(result[0].Price).IsEqualTo(99.99m);
    }
}
