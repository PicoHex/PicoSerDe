// ═══ Demo: YAML config serialization (V1: simple types) ═══
var config = new AppConfig
{
    Title = "MyApp",
    Version = 1,
    Debug = true,
    Port = 8080,
    Host = "localhost"
};

var bytes = YamlSerializer.SerializeToUtf8Bytes(config);
Console.WriteLine("=== Serialized YAML ===");
Console.WriteLine(Encoding.UTF8.GetString(bytes));

var restored = YamlSerializer.Deserialize<AppConfig>(bytes);
Console.WriteLine($"\n=== Round-trip OK ===");
Console.WriteLine($"  Title:   {restored?.Title}");
Console.WriteLine($"  Version: {restored?.Version}");
Console.WriteLine($"  Debug:   {restored?.Debug}");
Console.WriteLine($"  Port:    {restored?.Port}");
Console.WriteLine($"  Host:    {restored?.Host}");

// ═══ Low-level Reader ═══
Console.WriteLine("\n=== Reader token stream ===");
var raw = "server:\n  host: localhost\n  port: 8080\n"u8;
var r = new YamlReader(raw);
while (r.Read())
    Console.WriteLine(
        $"  {r.TokenType, -14} {Encoding.UTF8.GetString(r.KeySpan), -10} {Encoding.UTF8.GetString(r.ValueSpan)}"
    );

public class AppConfig
{
    public string Title { get; set; } = "";
    public int Version { get; set; }
    public bool Debug { get; set; }
    public int Port { get; set; }
    public string Host { get; set; } = "";
}
