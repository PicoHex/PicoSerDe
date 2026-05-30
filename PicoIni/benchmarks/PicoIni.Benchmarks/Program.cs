
Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("PicoIni vs ini-parser — Performance Comparison");
Console.WriteLine($"Runtime: {Environment.Version} | OS: {Environment.OSVersion}");
Console.WriteLine(new string('=', 60));
Console.WriteLine();

var parser = new FileIniDataParser();

// ── Simple POCO ──
var simple = new SimplePoco { Name = "Hello World", Age = 42 };
var simpleBytes = IniSerializer.SerializeToUtf8Bytes(simple);
var simpleStr = Encoding.UTF8.GetString(simpleBytes);

var simpleIni = new IniParser.Model.IniData();
simpleIni.Global["Name"] = "Hello World";
simpleIni.Global["Age"] = "42";

// ── Complex POCO ──
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
var complexStr = Encoding.UTF8.GetString(complexBytes);

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

// ── Helpers ──
IniParser.Model.IniData ParseIni(string s)
{
    return new IniParser.Parser.IniDataParser().Parse(s);
}
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
ComplexPoco DeserComplex(string s)
{
    var d = ParseIni(s);
    return new ComplexPoco
    {
        Id = Guid.Parse(d.Global["Id"]!),
        Title = d.Global["Title"] ?? "",
        Price = decimal.Parse(
            d.Global["Price"]!,
            System.Globalization.CultureInfo.InvariantCulture
        ),
        CreatedAt = DateTime.Parse(d.Global["CreatedAt"]!),
        Day = Enum.Parse<DayOfWeek>(d.Global["Day"]!),
        Rating = double.Parse(d.Global["Rating"]!),
        IsActive = bool.Parse(d.Global["IsActive"]!)
    };
}

// ── Benchmarks ──
var results = new List<ComparisonResult>
{
    Benchmark.Compare(
        "Simple — Serialize",
        "PicoIni",
        () => IniSerializer.SerializeToUtf8Bytes(simple),
        "ini-parser",
        () => SerIni(simpleIni)
    ),
    Benchmark.Compare(
        "Simple — Deserialize",
        "PicoIni",
        () => IniSerializer.Deserialize<SimplePoco>(simpleBytes),
        "ini-parser",
        () => DeserSimple(simpleStr)
    ),
    Benchmark.Compare(
        "Complex — Serialize",
        "PicoIni",
        () => IniSerializer.SerializeToUtf8Bytes(complex),
        "ini-parser",
        () => SerIni(complexIni)
    ),
    Benchmark.Compare(
        "Complex — Deserialize",
        "PicoIni",
        () => IniSerializer.Deserialize<ComplexPoco>(complexBytes),
        "ini-parser",
        () => DeserComplex(complexStr)
    ),
};

foreach (var c in results)
{
    var icon = c.IsFaster ? "✓" : "✗";
    Console.WriteLine(
        $"{icon} {c.Name, -36} PicoIni={c.Candidate.Statistics.Avg / 1000:F1}μs  IFP={c.Baseline.Statistics.Avg / 1000:F1}μs  Speedup={c.Speedup:F2}x"
    );
}

Console.WriteLine();
Console.WriteLine(new string('=', 60));
Console.WriteLine(SummaryFormatter.Format(results));
