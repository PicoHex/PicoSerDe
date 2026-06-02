Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("PicoIni vs ini-parser — Performance Comparison");
Console.WriteLine($"Runtime: {Environment.Version} | OS: {Environment.OSVersion}");
Console.WriteLine("NOTE: ini-parser uses runtime reflection — incompatible with NativeAOT.");
Console.WriteLine(new string('=', 60));
Console.WriteLine();

var parser = new FileIniDataParser();

var simple = new SimplePoco { Name = "Hello World", Age = 42 };
var simpleBytes = IniSerializer.SerializeToUtf8Bytes(simple);
var simpleStr = IniSerializer.Serialize(simple);

var simpleIni = new IniParser.Model.IniData();
simpleIni.Global["Name"] = "Hello World";
simpleIni.Global["Age"] = "42";

var complex = new ComplexPoco
{
    Id = Guid.NewGuid(),
    Title = "Benchmark Suite",
    Price = 149.99m,
    CreatedAt = DateTime.UtcNow,
    Day = DayOfWeek.Friday,
    Rating = 4.5,
    IsActive = true
};
var complexBytes = IniSerializer.SerializeToUtf8Bytes(complex);
var complexStr = IniSerializer.Serialize(complex);

var complexIni = new IniParser.Model.IniData();
complexIni.Global["Id"] = complex.Id.ToString();
complexIni.Global["Title"] = complex.Title;
complexIni.Global["Price"] = complex
    .Price
    .ToString(System.Globalization.CultureInfo.InvariantCulture);
complexIni.Global["CreatedAt"] = complex.CreatedAt.ToString("O");
complexIni.Global["Day"] = complex.Day.ToString();
complexIni.Global["Rating"] = complex
    .Rating
    .ToString(System.Globalization.CultureInfo.InvariantCulture);
complexIni.Global["IsActive"] = complex.IsActive.ToString();

// Pre-compute outside benchmark loops
var simpleStrBytes = Encoding.UTF8.GetBytes(simpleStr);
var complexStrBytes = Encoding.UTF8.GetBytes(complexStr);
var simpleIniBytes = SerIni(simpleIni);
var complexIniBytes = SerIni(complexIni);

var results = new List<ComparisonResult>
{
    Benchmark.Compare(
        "Simple — Serialize (bytes)",
        "PicoIni",
        () => IniSerializer.SerializeToUtf8Bytes(simple),
        "ini-parser¹",
        () => SerIni(simpleIni)
    ),
    Benchmark.Compare(
        "Simple — Deserialize (bytes)",
        "PicoIni",
        () => IniSerializer.Deserialize<SimplePoco>(simpleBytes),
        "ini-parser¹",
        () => DeserSimple(simpleStr)
    ),
    Benchmark.Compare(
        "Complex — Serialize (bytes)",
        "PicoIni",
        () => IniSerializer.SerializeToUtf8Bytes(complex),
        "ini-parser¹",
        () => SerIni(complexIni)
    ),
    // 文本格式真实场景: string 往返
    Benchmark.Compare(
        "Simple — SerializeToString",
        "PicoIni",
        () => IniSerializer.Serialize(simple),
        "ini-parser¹",
        () => SerIni(simpleIni)
    ),
    Benchmark.Compare(
        "Simple — Deserialize←string",
        "PicoIni",
        () => IniSerializer.Deserialize<SimplePoco>(simpleStrBytes),
        "ini-parser¹",
        () => DeserSimple(simpleStr)
    ),
};

foreach (var c in results)
{
    var icon = c.IsFaster ? "✓" : "✗";
    Console.WriteLine(
        $"{icon} {c.Name, -38} Pico={c.Candidate.Statistics.Avg / 1000:F1}μs  IFP={c.Baseline.Statistics.Avg / 1000:F1}μs  x{c.Speedup:F2}"
    );
}

Console.WriteLine();
Console.WriteLine("¹ ini-parser uses runtime reflection — incompatible with NativeAOT/trimming.");
Console.WriteLine(new string('=', 60));
Console.WriteLine(SummaryFormatter.Format(results));

return;

// ── Helper functions ──

IniParser.Model.IniData ParseIni(string s) => new IniParser.Parser.IniDataParser().Parse(s);

byte[] SerIni(IniParser.Model.IniData d)
{
    using var ms = new MemoryStream();
    using var sw = new StreamWriter(ms);
    parser.WriteData(sw, d);
    sw.Flush();
    return ms.ToArray();
}

SimplePoco DeserSimple(string s)
{
    var d = ParseIni(s);
    return new SimplePoco { Name = d.Global["Name"], Age = int.Parse(d.Global["Age"]) };
}
