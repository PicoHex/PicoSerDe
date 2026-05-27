// ═══ Demo: TOML config serialization (V1: simple types) ═══
var config = new AppConfig
{
    Title = "MyApp",
    Version = 1,
    Debug = true,
    Port = 8080,
    Host = "localhost"
};

var bytes = TomlSerializer.SerializeToUtf8Bytes(config);
Console.WriteLine("=== Serialized TOML ===");
Console.WriteLine(Encoding.UTF8.GetString(bytes));

var restored = TomlSerializer.Deserialize<AppConfig>(bytes);
Console.WriteLine($"\n=== Round-trip OK ===");
Console.WriteLine($"  Title:   {restored?.Title}");
Console.WriteLine($"  Version: {restored?.Version}");
Console.WriteLine($"  Debug:   {restored?.Debug}");
Console.WriteLine($"  Port:    {restored?.Port}");
Console.WriteLine($"  Host:    {restored?.Host}");

// ═══ Low-level Reader ═══
Console.WriteLine("\n=== Reader token stream ===");
var raw = "[server]\nhost = \"localhost\"\nport = 8080\n"u8;
var r = new TomlReader(raw);
while (r.Read())
{
    if (r.TokenType == PicoSerDe.Abs.TokenType.ObjectStart)
        Console.WriteLine($"  [{Encoding.UTF8.GetString(r.TablePath)}]");
    else
        Console.WriteLine(
            $"  {Encoding.UTF8.GetString(r.KeySpan)} = {Encoding.UTF8.GetString(r.ValueSpan)}"
        );
}

public class AppConfig
{
    public string Title { get; set; } = "";
    public int Version { get; set; }
    public bool Debug { get; set; }
    public int Port { get; set; }
    public string Host { get; set; } = "";
}
