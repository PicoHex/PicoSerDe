Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("PicoYaml — Self Benchmark (AOT)");
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
        "Simple — Ser vs Deser",
        "Serialize",
        () => PicoYaml.YamlSerializer.SerializeToUtf8Bytes(simple),
        "Deserialize",
        () => PicoYaml.YamlSerializer.SerializeToUtf8Bytes(simple)
    ),
    Benchmark.Compare(
        "Nested — Ser vs Deser",
        "Serialize",
        () => PicoYaml.YamlSerializer.SerializeToUtf8Bytes(nested),
        "Deserialize",
        () => PicoYaml.YamlSerializer.SerializeToUtf8Bytes(nested)
    ),
    Benchmark.Compare(
        "Collection — Ser vs Deser",
        "Serialize",
        () => PicoYaml.YamlSerializer.SerializeToUtf8Bytes(collection),
        "Deserialize",
        () => PicoYaml.YamlSerializer.SerializeToUtf8Bytes(collection)
    ),
};

foreach (var c in results)
{
    Console.WriteLine(
        $"   {c.Name, -30} Ser={c.Candidate.Statistics.Avg / 1000:F1}μs  Deser={c.Baseline.Statistics.Avg / 1000:F1}μs  Ratio={c.Candidate.Statistics.Avg / c.Baseline.Statistics.Avg:F2}"
    );
}

Console.WriteLine();
Console.WriteLine(new string('=', 60));
Console.WriteLine(SummaryFormatter.Format(results));
