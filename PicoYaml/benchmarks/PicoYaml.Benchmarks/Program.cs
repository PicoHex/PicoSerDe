using PicoBench;
using PicoBench.Formatters;

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("PicoYaml vs VYaml — Performance Comparison");
Console.WriteLine($"Runtime: {Environment.Version} | OS: {Environment.OSVersion}");
Console.WriteLine(new string('=', 60));
Console.WriteLine();

var simple = new SimplePoco { Name = "Hello World", Age = 42 };
var nested = new NestedPoco
{
    Id = 1,
    Name = "Alice",
    Address = new NestedAddress
    {
        Street = "123 Main",
        City = "SF",
        Zip = "94105"
    },
    Tags = new List<string> { "dev", "bench" }
};
var collection = new CollectionPoco
{
    Scores = Enumerable.Range(0, 100).ToList(),
    Metadata = new() { ["source"] = "benchmark" }
};

var results = new List<ComparisonResult>
{
    Benchmark.Compare(
        "Simple — Serialize (bytes)",
        "PicoYaml",
        () => PicoYaml.YamlSerializer.SerializeToUtf8Bytes(simple),
        "VYaml",
        () => VYaml.Serialization.YamlSerializer.Serialize(simple).ToArray()
    ),
    Benchmark.Compare(
        "Nested — Serialize (bytes)",
        "PicoYaml",
        () => PicoYaml.YamlSerializer.SerializeToUtf8Bytes(nested),
        "VYaml",
        () => VYaml.Serialization.YamlSerializer.Serialize(nested).ToArray()
    ),
    Benchmark.Compare(
        "Collection — Serialize (bytes)",
        "PicoYaml",
        () => PicoYaml.YamlSerializer.SerializeToUtf8Bytes(collection),
        "VYaml",
        () => VYaml.Serialization.YamlSerializer.Serialize(collection).ToArray()
    ),
    // string 场景
    Benchmark.Compare(
        "Simple — SerializeToString",
        "PicoYaml",
        () => PicoYaml.YamlSerializer.Serialize(simple),
        "VYaml",
        () => VYaml.Serialization.YamlSerializer.Serialize(simple).ToArray()
    ),
    Benchmark.Compare(
        "Nested — SerializeToString",
        "PicoYaml",
        () => PicoYaml.YamlSerializer.Serialize(nested),
        "VYaml",
        () => VYaml.Serialization.YamlSerializer.Serialize(nested).ToArray()
    ),
};

foreach (var c in results)
{
    var icon = c.IsFaster ? "✓" : "✗";
    Console.WriteLine(
        $"{icon} {c.Name, -38} Pico={c.Candidate.Statistics.Avg / 1000:F1}μs  VYaml={c.Baseline.Statistics.Avg / 1000:F1}μs  x{c.Speedup:F2}"
    );
}

Console.WriteLine();
Console.WriteLine(new string('=', 60));
Console.WriteLine(SummaryFormatter.Format(results));
