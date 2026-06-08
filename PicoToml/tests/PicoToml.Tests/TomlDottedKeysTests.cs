namespace PicoToml.Tests;

// Models at namespace level (SG skips nested types)
public class TomlDottedServerConfig
{
    public TomlDottedServerInfo? Server { get; set; }
}

public class TomlDottedServerInfo
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
}

public class TomlDottedKeysTests
{
    [Test]
    public async Task Reader_DottedKey_SingleLevel_EmitsNestedTokens()
    {
        var toml = "server.host = \"localhost\"\nserver.port = 8080\n"u8;
        List<(bool ok, TokenType tt, string key, string? val, int? port)> results = new();
        {
            var r = new TomlReader(toml);
            // server.host: ObjectStart("server") then PropertyName("host")
            results.Add((r.Read(), r.TokenType, Encoding.UTF8.GetString(r.KeySpan), null, null));
            results.Add(
                (
                    r.Read(),
                    r.TokenType,
                    Encoding.UTF8.GetString(r.KeySpan),
                    Encoding.UTF8.GetString(r.ValueSpan),
                    null
                )
            );
            // server.port: ObjectStart("server") then PropertyName("port")
            results.Add((r.Read(), r.TokenType, Encoding.UTF8.GetString(r.KeySpan), null, null));
            var ok4 = r.Read();
            r.TryGetInt32(out var port);
            results.Add((ok4, r.TokenType, Encoding.UTF8.GetString(r.KeySpan), null, port));
            // EOF
            results.Add((r.Read(), TokenType.None, "", null, null));
        }

        await Assert.That(results[0].ok).IsTrue();
        await Assert.That(results[0].tt).IsEqualTo(TokenType.ObjectStart);
        await Assert.That(results[0].key).IsEqualTo("server");
        await Assert.That(results[1].ok).IsTrue();
        await Assert.That(results[1].tt).IsEqualTo(TokenType.PropertyName);
        await Assert.That(results[1].key).IsEqualTo("host");
        await Assert.That(results[1].val).IsEqualTo("localhost");
        await Assert.That(results[2].ok).IsTrue();
        await Assert.That(results[2].tt).IsEqualTo(TokenType.ObjectStart);
        await Assert.That(results[2].key).IsEqualTo("server");
        await Assert.That(results[3].ok).IsTrue();
        await Assert.That(results[3].tt).IsEqualTo(TokenType.PropertyName);
        await Assert.That(results[3].key).IsEqualTo("port");
        await Assert.That(results[3].port).IsEqualTo(8080);
        await Assert.That(results[4].ok).IsFalse();
    }

    [Test]
    public async Task Deserialize_DottedKey_SingleLevel_NestedObject()
    {
        var toml = """
        server.host = "localhost"
        server.port = 8080
        """u8;
        var result = TomlSerializer.Deserialize<TomlDottedServerConfig>(toml);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Server).IsNotNull();
        await Assert.That(result.Server!.Host).IsEqualTo("localhost");
        await Assert.That(result.Server!.Port).IsEqualTo(8080);
    }

    [Test]
    public async Task Serialize_DottedKey_RoundTrips()
    {
        var config = new TomlDottedServerConfig
        {
            Server = new TomlDottedServerInfo { Host = "example.com", Port = 443 },
        };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(config);
        var result = TomlSerializer.Deserialize<TomlDottedServerConfig>(bytes);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Server).IsNotNull();
        await Assert.That(result.Server!.Host).IsEqualTo("example.com");
        await Assert.That(result.Server!.Port).IsEqualTo(443);
    }
}
