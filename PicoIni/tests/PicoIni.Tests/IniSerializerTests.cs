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

public class IniSerializerTests
{
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
            var cfg = new AppConfig();
            while (reader.Read())
            {
                if (reader.TokenType == IniTokenType.Key)
                {
                    if (reader.Key.SequenceEqual("Title"u8))
                        cfg.Title = Encoding.UTF8.GetString(reader.ValueSpan);
                }
                else if (
                    reader.TokenType == IniTokenType.SectionStart
                    && reader.SectionNameEquals("Server")
                )
                {
                    cfg.Server = new ServerConfig();
                    while (reader.Read() && reader.TokenType != IniTokenType.SectionStart)
                    {
                        if (reader.TokenType != IniTokenType.Key)
                            continue;
                        if (reader.Key.SequenceEqual("Host"u8))
                            cfg.Server.Host = Encoding.UTF8.GetString(reader.ValueSpan);
                        else if (reader.Key.SequenceEqual("Port"u8))
                        {
                            reader.TryGetInt32(out var p);
                            cfg.Server.Port = p;
                        }
                    }
                }
            }
            return cfg;
        }
    }

    [Test]
    public async Task Manual_RoundTrip_Works()
    {
        var config = new AppConfig
        {
            Title = "MyApp",
            Server = new ServerConfig { Host = "localhost", Port = 8080 }
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
    public async Task SerializeToUtf8Bytes_ProducesValidIni()
    {
        IniSerializer.Register(new AppConfigIniSerializer(), new AppConfigIniDeserializer());
        var config = new AppConfig
        {
            Title = "Test",
            Server = new ServerConfig { Host = "h", Port = 1 }
        };
        var bytes = IniSerializer.SerializeToUtf8Bytes(config);
        var ini = Encoding.UTF8.GetString(bytes);
        await Assert.That(ini).Contains("Title = Test");
        await Assert.That(ini).Contains("[Server]");
        await Assert.That(ini).Contains("Host = h");
    }

    [Test]
    public async Task GeneratedSerializer_RoundTrip()
    {
        var config = new AppConfig
        {
            Title = "MyApp",
            Server = new ServerConfig { Host = "localhost", Port = 8080 }
        };
        var ini = IniSerializer.Serialize(config);
        var bytes = Encoding.UTF8.GetBytes(ini);
        var result = IniSerializer.Deserialize<AppConfig>(bytes);
        await Assert.That(result?.Title).IsEqualTo("MyApp");
        await Assert.That(result?.Server.Host).IsEqualTo("localhost");
        await Assert.That(result?.Server.Port).IsEqualTo(8080);
    }
}
