namespace PicoIni.Tests;

public class IniSerializerUnifiedTests
{
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
                                    config.Server.Host = Encoding
                                        .UTF8
                                        .GetString(reader.GetStringRaw());
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
            Server = new ServerSection { Host = "localhost", Port = 8080 }
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
            Zeta = "z"
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
    public string Alpha { get; set; } = "";
    public int Beta { get; set; }
    public bool Gamma { get; set; }
    public long Delta { get; set; }
    public double Epsilon { get; set; }
    public string Zeta { get; set; } = "";
}
