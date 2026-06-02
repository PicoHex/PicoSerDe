Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("PicoToml vs Tommy — Performance Comparison");
Console.WriteLine($"Runtime: {Environment.Version} | OS: {Environment.OSVersion}");
Console.WriteLine("NOTE: Tommy uses runtime reflection — incompatible with NativeAOT.");
Console.WriteLine(new string('=', 60));
Console.WriteLine();

var simple = new SimplePoco { Name = "Hello World", Age = 42 };
var simpleBytes = TomlSerializer.SerializeToUtf8Bytes(simple);
var simpleStr = TomlSerializer.Serialize(simple);

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
var nestedStr = TomlSerializer.Serialize(nested);

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

// Pre-compute competitor outputs outside benchmark loops
var simpleTomlBytes = TommySer(simpleToml);
var simpleTomlStr = TommySerStr(simpleToml);
var nestedTomlBytes = TommySer(nestedToml);
var nestedTomlStr = TommySerStr(nestedToml);
var simpleStrBytes = Encoding.UTF8.GetBytes(simpleStr);
var nestedStrBytes = Encoding.UTF8.GetBytes(nestedStr);

var results = new List<ComparisonResult>
{
    // bytes 场景
    Benchmark.Compare(
        "Simple — Serialize (bytes)",
        "PicoToml",
        () => TomlSerializer.SerializeToUtf8Bytes(simple),
        "Tommy¹",
        () => TommySer(simpleToml)
    ),
    Benchmark.Compare(
        "Simple — Deserialize (bytes)",
        "PicoToml",
        () => TomlSerializer.Deserialize<SimplePoco>(simpleBytes),
        "Tommy¹",
        () => TommyDeser(simpleStr)
    ),
    Benchmark.Compare(
        "Nested — Serialize (bytes)",
        "PicoToml",
        () => TomlSerializer.SerializeToUtf8Bytes(nested),
        "Tommy¹",
        () => TommySer(nestedToml)
    ),
    Benchmark.Compare(
        "Nested — Deserialize (bytes)",
        "PicoToml",
        () => TomlSerializer.Deserialize<NestedPoco>(nestedBytes),
        "Tommy¹",
        () => TommyDeserNested(nestedStr)
    ),
    // string 往返（文本格式自然输出）
    Benchmark.Compare(
        "Simple — SerializeToString",
        "PicoToml",
        () => TomlSerializer.Serialize(simple),
        "Tommy¹",
        () => TommySerStr(simpleToml)
    ),
    Benchmark.Compare(
        "Simple — Deserialize←string",
        "PicoToml",
        () => TomlSerializer.Deserialize<SimplePoco>(simpleStrBytes),
        "Tommy¹",
        () => TommyDeser(simpleStr)
    ),
};

foreach (var c in results)
{
    var icon = c.IsFaster ? "✓" : "✗";
    Console.WriteLine(
        $"{icon} {c.Name, -38} Pico={c.Candidate.Statistics.Avg / 1000:F1}μs  Tommy={c.Baseline.Statistics.Avg / 1000:F1}μs  x{c.Speedup:F2}"
    );
}

Console.WriteLine();
Console.WriteLine("¹ Tommy uses runtime reflection — incompatible with NativeAOT/trimming.");
Console.WriteLine(new string('=', 60));
Console.WriteLine(SummaryFormatter.Format(results));

return;

// ── Helper functions ──

byte[] TommySer(TomlTable t)
{
    using var sw = new StringWriter();
    t.WriteTo(sw);
    sw.Flush();
    return Encoding.UTF8.GetBytes(sw.ToString());
}

string TommySerStr(TomlTable t)
{
    using var sw = new StringWriter();
    t.WriteTo(sw);
    sw.Flush();
    return sw.ToString();
}

SimplePoco TommyDeser(string s)
{
    using var sr = new StringReader(s);
    var t = TOML.Parse(sr);
    return new SimplePoco { Name = t["Name"].AsString, Age = (int)t["Age"].AsInteger };
}

NestedPoco TommyDeserNested(string s)
{
    using var sr = new StringReader(s);
    var t = TOML.Parse(sr);
    var tags = new List<string>();
    if (t["Tags"].IsArray)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                var v = t["Tags"][i];
                tags.Add(v.AsString.Value);
            }
            catch
            {
                break;
            }
        }
    }
    return new NestedPoco
    {
        Id = (int)t["Id"].AsInteger,
        Name = t["Name"].AsString,
        Address = new NestedAddress
        {
            Street = t["Address"]["Street"].AsString,
            City = t["Address"]["City"].AsString,
            Zip = t["Address"]["Zip"].AsString
        },
        Tags = tags
    };
}
