using PicoBench;
using PicoBench.Formatters;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("PicoYaml vs YamlDotNet — Performance Comparison");
Console.WriteLine($"Runtime: {Environment.Version} | OS: {Environment.OSVersion}");
Console.WriteLine(new string('=', 60));
Console.WriteLine();

var yamlSer = new SerializerBuilder().WithNamingConvention(NullNamingConvention.Instance).Build();

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
results.Add(
    Benchmark.Compare(
        "Simple — Serialize",
        "PicoYaml",
        () => YamlSerializer.SerializeToUtf8Bytes(simple),
        "YamlDotNet",
        () => Encoding.UTF8.GetBytes(yamlSer.Serialize(simple))
    )
);
results.Add(
    Benchmark.Compare(
        "Nested — Serialize",
        "PicoYaml",
        () => YamlSerializer.SerializeToUtf8Bytes(nested),
        "YamlDotNet",
        () => Encoding.UTF8.GetBytes(yamlSer.Serialize(nested))
    )
);
results.Add(
    Benchmark.Compare(
        "Collection — Serialize",
        "PicoYaml",
        () => YamlSerializer.SerializeToUtf8Bytes(collection),
        "YamlDotNet",
        () => Encoding.UTF8.GetBytes(yamlSer.Serialize(collection))
    )
);

// Deserialize: round-trip
var spb = YamlSerializer.SerializeToUtf8Bytes(simple);
results.Add(
    Benchmark.Compare(
        "Simple — Deserialize",
        "PicoYaml",
        () => YamlSerializer.Deserialize<SimplePoco>(spb)!,
        "YamlDotNet — Serialize",
        () => Encoding.UTF8.GetBytes(yamlSer.Serialize(simple))
    )
);

foreach (var c in results)
{
    var icon = c.IsFaster ? "✓" : "✗";
    Console.WriteLine(
        $"{icon} {c.Name, -36} PicoYaml={c.Candidate.Statistics.Avg / 1000:F1}μs  YDN={c.Baseline.Statistics.Avg / 1000:F1}μs  Speedup={c.Speedup:F2}x"
    );
}

Console.WriteLine();
Console.WriteLine(new string('=', 60));
Console.WriteLine(SummaryFormatter.Format(results));
