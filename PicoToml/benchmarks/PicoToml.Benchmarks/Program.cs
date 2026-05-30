
Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("PicoToml vs Tommy — Performance Comparison");
Console.WriteLine($"Runtime: {Environment.Version} | OS: {Environment.OSVersion}");
Console.WriteLine(new string('=', 60));
Console.WriteLine();

var simple = new SimplePoco { Name = "Hello World", Age = 42 };
var simpleBytes = TomlSerializer.SerializeToUtf8Bytes(simple);
var simpleStr = Encoding.UTF8.GetString(simpleBytes);

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
var nestedBytes = TomlSerializer.SerializeToUtf8Bytes(nested);
var nestedStr = Encoding.UTF8.GetString(nestedBytes);

// Tommy: build equivalent tables
var simpleToml = new TomlTable { ["Name"] = "Hello World", ["Age"] = 42L };
var nestedToml = new TomlTable
{
    ["Id"] = 1L,
    ["Name"] = "Alice",
    ["Address"] = new TomlTable
    {
        ["Street"] = "123 Main",
        ["City"] = "SF",
        ["Zip"] = "94105"
    },
    ["Tags"] = new TomlArray { "dev", "bench" }
};

byte[] TommySer(TomlTable t)
{
    using var sw = new StringWriter();
    t.WriteTo(sw);
    sw.Flush();
    return Encoding.UTF8.GetBytes(sw.ToString());
}
SimplePoco TommyDeserSimple(string s)
{
    using var sr = new StringReader(s);
    var t = TOML.Parse(sr);
    return new SimplePoco { Name = t["Name"].AsString, Age = (int)t["Age"].AsInteger };
}
NestedPoco TommyDeserNested(string s)
{
    using var sr = new StringReader(s);
    var t = TOML.Parse(sr);
    return new NestedPoco { Name = t["Name"].AsString };
}

var results = new List<ComparisonResult>
{
    Benchmark.Compare(
        "Simple — Serialize",
        "PicoToml",
        () => TomlSerializer.SerializeToUtf8Bytes(simple),
        "Tommy",
        () => TommySer(simpleToml)
    ),
    Benchmark.Compare(
        "Simple — Deserialize",
        "PicoToml",
        () => TomlSerializer.Deserialize<SimplePoco>(simpleBytes),
        "Tommy",
        () => TommyDeserSimple(simpleStr)
    ),
    Benchmark.Compare(
        "Nested — Serialize",
        "PicoToml",
        () => TomlSerializer.SerializeToUtf8Bytes(nested),
        "Tommy",
        () => TommySer(nestedToml)
    ),
    Benchmark.Compare(
        "Nested — Deserialize",
        "PicoToml",
        () => TomlSerializer.Deserialize<NestedPoco>(nestedBytes),
        "Tommy",
        () => TommyDeserNested(nestedStr)
    ),
};

foreach (var c in results)
{
    var icon = c.IsFaster ? "✓" : "✗";
    Console.WriteLine(
        $"{icon} {c.Name, -36} PicoToml={c.Candidate.Statistics.Avg / 1000:F1}μs  Tommy={c.Baseline.Statistics.Avg / 1000:F1}μs  Speedup={c.Speedup:F2}x"
    );
}

Console.WriteLine();
Console.WriteLine(new string('=', 60));
Console.WriteLine(SummaryFormatter.Format(results));
