using System.Diagnostics;
using System.Text;
using PicoIni;

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("=== PicoIni Performance Benchmarks ===");
Console.WriteLine($"Runtime: {Environment.Version} | OS: {Environment.OSVersion}");
Console.WriteLine(new string('-', 60));

// ── Test data ──
var simple = new BmSimple { Title = "MyApp", Version = 2 };
var complex = new BmComplex
{
    Title = "Benchmark Suite",
    Version = 1,
    Server = new BmServer
    {
        Host = "prod.example.com",
        Port = 443,
        Enabled = true
    },
    Database = new BmDb { ConnectionString = "server=.;db=test", MaxPool = 50 },
    Tags =  ["prod", "api", "v2"]
};

// ── Benchmark helper ──
static void Run(string name, Action action, int iterations = 100_000)
{
    action(); // warmup
    var sw = Stopwatch.StartNew();
    for (int i = 0; i < iterations; i++)
        action();
    sw.Stop();
    var perOp = sw.Elapsed.TotalMicroseconds / iterations;
    Console.WriteLine($"  {name, -30} {perOp, 8:F2} μs/op  ({iterations:N0} iterations)");
}

Console.WriteLine("\n--- Serialize ---");
Run("Simple → bytes", () => IniSerializer.SerializeToUtf8Bytes(simple));
Run("Simple → string", () => IniSerializer.Serialize(simple));
Run("Complex → bytes", () => IniSerializer.SerializeToUtf8Bytes(complex));

Console.WriteLine("\n--- Deserialize ---");
var simpleBytes = IniSerializer.SerializeToUtf8Bytes(simple);
var complexBytes = IniSerializer.SerializeToUtf8Bytes(complex);
Run("Simple ← bytes", () => IniSerializer.Deserialize<BmSimple>(simpleBytes));
Run("Complex ← bytes", () => IniSerializer.Deserialize<BmComplex>(complexBytes));

Console.WriteLine("\n--- Round-trip ---");
Run(
    "Simple  S→D",
    () =>
    {
        var b = IniSerializer.SerializeToUtf8Bytes(simple);
        IniSerializer.Deserialize<BmSimple>(b);
    }
);
Run(
    "Complex S→D",
    () =>
    {
        var b = IniSerializer.SerializeToUtf8Bytes(complex);
        IniSerializer.Deserialize<BmComplex>(b);
    }
);

Console.WriteLine($"\nDone. ({DateTime.Now:T})");

// ── Model types ──
public class BmSimple
{
    public string Title { get; set; } = "";
    public int Version { get; set; }
}

public class BmServer
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public bool Enabled { get; set; }
}

public class BmDb
{
    public string ConnectionString { get; set; } = "";
    public int MaxPool { get; set; }
}

public class BmComplex
{
    public string Title { get; set; } = "";
    public int Version { get; set; }
    public BmServer Server { get; set; } = new();
    public BmDb Database { get; set; } = new();
    public List<string> Tags { get; set; } = [];
}
