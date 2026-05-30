using PicoBench;
using PicoBench.Formatters;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("PicoYaml vs YamlDotNet — Performance Comparison");
Console.WriteLine($"Runtime: {Environment.Version} | OS: {Environment.OSVersion}");
Console.WriteLine(new string('=', 60));
Console.WriteLine();

var yamlSerializer = new SerializerBuilder()
    .WithNamingConvention(NullNamingConvention.Instance)
    .Build();
var yamlDeserializer = new DeserializerBuilder()
    .WithNamingConvention(NullNamingConvention.Instance)
    .Build();

var simple = new SimplePoco { Name = "Hello World", Age = 42 };
var simpleBytes = YamlSerializer.SerializeToUtf8Bytes(simple);

var complex = new ComplexPoco
{
    Id = Guid.NewGuid(),
    Title = "Benchmark",
    Price = 149.99m,
    CreatedAt = DateTime.UtcNow,
    Day = DayOfWeek.Friday,
    Rating = 4.5,
    IsActive = true
};
var complexBytes = YamlSerializer.SerializeToUtf8Bytes(complex);

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
var nestedBytes = YamlSerializer.SerializeToUtf8Bytes(nested);

var collection = new CollectionPoco
{
    Scores = Enumerable.Range(0, 100).ToList(),
    Metadata = new() { ["source"] = "benchmark" }
};
var colBytes = YamlSerializer.SerializeToUtf8Bytes(collection);

var results = new List<ComparisonResult>
{
    Benchmark.Compare(
        "Simple — Serialize",
        "PicoYaml",
        () => YamlSerializer.SerializeToUtf8Bytes(simple),
        "YamlDotNet",
        () => Encoding.UTF8.GetBytes(yamlSerializer.Serialize(simple))
    ),
    Benchmark.Compare(
        "Simple — Deserialize",
        "PicoYaml",
        () => YamlSerializer.Deserialize<SimplePoco>(simpleBytes),
        "YamlDotNet",
        () => yamlDeserializer.Deserialize<SimplePoco>(Encoding.UTF8.GetString(simpleBytes))
    ),
    Benchmark.Compare(
        "Complex — Serialize",
        "PicoYaml",
        () => YamlSerializer.SerializeToUtf8Bytes(complex),
        "YamlDotNet",
        () => Encoding.UTF8.GetBytes(yamlSerializer.Serialize(complex))
    ),
    Benchmark.Compare(
        "Complex — Deserialize",
        "PicoYaml",
        () => YamlSerializer.Deserialize<ComplexPoco>(complexBytes),
        "YamlDotNet",
        () => yamlDeserializer.Deserialize<ComplexPoco>(Encoding.UTF8.GetString(complexBytes))
    ),
    Benchmark.Compare(
        "Nested — Serialize",
        "PicoYaml",
        () => YamlSerializer.SerializeToUtf8Bytes(nested),
        "YamlDotNet",
        () => Encoding.UTF8.GetBytes(yamlSerializer.Serialize(nested))
    ),
    Benchmark.Compare(
        "Nested — Deserialize",
        "PicoYaml",
        () => YamlSerializer.Deserialize<NestedPoco>(nestedBytes),
        "YamlDotNet",
        () => yamlDeserializer.Deserialize<NestedPoco>(Encoding.UTF8.GetString(nestedBytes))
    ),
    Benchmark.Compare(
        "Collection — Serialize",
        "PicoYaml",
        () => YamlSerializer.SerializeToUtf8Bytes(collection),
        "YamlDotNet",
        () => Encoding.UTF8.GetBytes(yamlSerializer.Serialize(collection))
    ),
    Benchmark.Compare(
        "Collection — Deserialize",
        "PicoYaml",
        () => YamlSerializer.Deserialize<CollectionPoco>(colBytes),
        "YamlDotNet",
        () => yamlDeserializer.Deserialize<CollectionPoco>(Encoding.UTF8.GetString(colBytes))
    ),
};

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
