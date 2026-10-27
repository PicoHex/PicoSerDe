Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("PicoYaml — Self Benchmark (AOT)");
Console.WriteLine($"Runtime: {Environment.Version} | OS: {Environment.OSVersion}");
Console.WriteLine(
    "No competitor here: YamlDotNet parses a DOM under NativeAOT but its reflection-based"
        + " typed POCO mapping fails there, so no like-for-like AOT pair exists to measure."
);
Console.WriteLine("Absolute timings only — this file reports no ratio and claims no speedup.");
Console.WriteLine(new string('=', 60));

var simple = new SimplePoco { Name = "Hello World", Age = 42 };
var simpleBytes = YamlSerializer.SerializeToUtf8Bytes(simple);

var nested = new NestedPoco
{
    Id = 1,
    Name = "Alice",
    Address = new NestedAddress
    {
        Street = "123 Main",
        City = "SF",
        Zip = "94105",
    },
    Tags = new List<string> { "dev", "bench" },
};
var nestedBytes = YamlSerializer.SerializeToUtf8Bytes(nested);

var collection = new CollectionPoco
{
    Scores = Enumerable.Range(0, 100).ToList(),
    Metadata = new() { ["source"] = "benchmark" },
};
var collectionBytes = YamlSerializer.SerializeToUtf8Bytes(collection);

// Self-benchmark: one arm per operation, measured under the shared protocol.
// Deliberately no competitor arm — a comparison needs one, and constructing one from
// our own two operations is what produced the meaningless "Ser vs Deser ratio" this
// file used to print (on top of measuring Serialize twice for the "Deserialize" row).
var table = new ProtocolTable("PicoYaml", theirsLabel: null);

table.Measure("Simple — Serialize", () => YamlSerializer.SerializeToUtf8Bytes(simple));
table.Measure("Simple — Deserialize", () => YamlSerializer.Deserialize<SimplePoco>(simpleBytes));
table.Measure("Nested — Serialize", () => YamlSerializer.SerializeToUtf8Bytes(nested));
table.Measure("Nested — Deserialize", () => YamlSerializer.Deserialize<NestedPoco>(nestedBytes));
table.Measure("Collection — Serialize", () => YamlSerializer.SerializeToUtf8Bytes(collection));
table.Measure(
    "Collection — Deserialize",
    () => YamlSerializer.Deserialize<CollectionPoco>(collectionBytes)
);

table.Print();
Console.WriteLine(new string('=', 60));

Environment.ExitCode = BenchProtocol.ExitCode;
