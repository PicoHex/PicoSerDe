using System.Text;
using PicoIni;

public class Program
{
    static void Main()
    {
        var config = new AppConfig
        {
            Title = "My Application",
            Version = 2,
            Server = new ServerConfig
            {
                Host = "prod.example.com",
                Port = 443,
                Enabled = true
            },
            Database = new DatabaseConfig
            {
                ConnectionString = "server=db.internal;database=app",
                MaxPoolSize = 100,
                Timeout = TimeSpan.FromSeconds(30)
            },
            Tags = new List<string> { "web", "api", "production" }
        };

        var ini = IniSerializer.Serialize(config);
        Console.WriteLine("=== Serialized INI ===");
        Console.WriteLine(ini);

        var bytes = Encoding.UTF8.GetBytes(ini);
        var restored = IniSerializer.Deserialize<AppConfig>(bytes);
        Console.WriteLine($"\n=== Round-trip ===");
        Console.WriteLine($"Title: {restored?.Title}");
        Console.WriteLine($"Version: {restored?.Version}");
        Console.WriteLine(
            $"Server: {restored?.Server.Host}:{restored?.Server.Port} (enabled={restored?.Server.Enabled})"
        );
        Console.WriteLine($"DB: {restored?.Database.ConnectionString}");
        Console.WriteLine($"MaxPool: {restored?.Database.MaxPoolSize}");
        Console.WriteLine($"Timeout: {restored?.Database.Timeout}");
        Console.WriteLine($"Tags: [{string.Join(", ", restored?.Tags ?? new())}]");

        // Debug: read with raw reader
        Console.WriteLine("\n=== Raw Reader Debug ===");
        var r = new IniReader(bytes);
        while (r.Read())
        {
            if (r.TokenType == IniTokenType.Key)
                Console.WriteLine(
                    $"  KEY: {Encoding.UTF8.GetString(r.Key)} = {Encoding.UTF8.GetString(r.ValueSpan)}"
                );
            else if (r.TokenType == IniTokenType.SectionStart)
                Console.WriteLine($"  SECTION: {Encoding.UTF8.GetString(r.SectionName)}");
            else if (r.TokenType == IniTokenType.Comment)
                Console.WriteLine($"  COMMENT: {Encoding.UTF8.GetString(r.CommentText)}");
            else if (r.TokenType == IniTokenType.Blank)
                Console.WriteLine($"  BLANK");
        }

        var ok =
            restored?.Title == config.Title
            && restored?.Server.Host == config.Server.Host
            && restored?.Server.Port == config.Server.Port
            && restored?.Tags?.Count == 3;
        Console.WriteLine($"\nRound-trip OK: {ok}");
    }
}

public class ServerConfig
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public bool Enabled { get; set; }
}

public class DatabaseConfig
{
    public string ConnectionString { get; set; } = "";
    public int MaxPoolSize { get; set; }
    public TimeSpan Timeout { get; set; }
}

public class AppConfig
{
    public string Title { get; set; } = "";
    public int Version { get; set; }
    public ServerConfig Server { get; set; } = new();
    public DatabaseConfig Database { get; set; } = new();
    public List<string> Tags { get; set; } = new();
}
