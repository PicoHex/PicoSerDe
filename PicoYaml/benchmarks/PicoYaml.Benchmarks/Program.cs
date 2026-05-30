using PicoBench;
using PicoBench.Formatters;
using VYaml.Serialization;

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

var results = new List<ComparisonResult>();

// Serialize
results.Add(
    Benchmark.Compare(
        "Simple — Serialize",
        "PicoYaml",
        () => PicoYaml.YamlSerializer.SerializeToUtf8Bytes(simple),
        "VYaml",
        () => VYaml.Serialization.YamlSerializer.Serialize(simple).ToArray()
    )
);
results.Add(
    Benchmark.Compare(
        "Nested — Serialize",
        "PicoYaml",
        () => PicoYaml.YamlSerializer.SerializeToUtf8Bytes(nested),
        "VYaml",
        () => VYaml.Serialization.YamlSerializer.Serialize(nested).ToArray()
    )
);
results.Add(
    Benchmark.Compare(
        "Collection — Serialize",
        "PicoYaml",
        () => PicoYaml.YamlSerializer.SerializeToUtf8Bytes(collection),
        "VYaml",
        () => VYaml.Serialization.YamlSerializer.Serialize(collection).ToArray()
    )
);

// Deserialize
var simplePicoBytes = PicoYaml.YamlSerializer.SerializeToUtf8Bytes(simple);
var simpleVyBytes = VYaml.Serialization.YamlSerializer.Serialize(simple);
results.Add(
    Benchmark.Compare(
        "Simple — Deserialize",
        "PicoYaml",
        () =>
        {
            _ = PicoYaml.YamlSerializer.Deserialize<SimplePoco>(simplePicoBytes);
        },
        "VYaml",
        () =>
        {
            _ = VYaml.Serialization.YamlSerializer.Deserialize<SimplePoco>(simpleVyBytes);
        }
    )
);

var nestedPicoBytes = PicoYaml.YamlSerializer.SerializeToUtf8Bytes(nested);
var nestedVyBytes = VYaml.Serialization.YamlSerializer.Serialize(nested);
results.Add(
    Benchmark.Compare(
        "Nested — Deserialize",
        "PicoYaml",
        () =>
        {
            _ = PicoYaml.YamlSerializer.Deserialize<NestedPoco>(nestedPicoBytes);
        },
        "VYaml",
        () =>
        {
            _ = VYaml.Serialization.YamlSerializer.Deserialize<NestedPoco>(nestedVyBytes);
        }
    )
);

foreach (var c in results)
{
    var icon = c.IsFaster ? "✓" : "✗";
    Console.WriteLine(
        $"{icon} {c.Name, -36} PicoYaml={c.Candidate.Statistics.Avg / 1000:F1}μs  VYaml={c.Baseline.Statistics.Avg / 1000:F1}μs  Speedup={c.Speedup:F2}x"
    );
}

Console.WriteLine();
Console.WriteLine(new string('=', 60));
Console.WriteLine(SummaryFormatter.Format(results));
