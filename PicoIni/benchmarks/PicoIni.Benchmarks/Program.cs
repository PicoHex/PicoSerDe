using IniParser;
using PicoBench;
using PicoBench.Formatters;

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("PicoIni vs ini-parser — Performance Comparison");
Console.WriteLine($"Runtime: {Environment.Version} | OS: {Environment.OSVersion}");
Console.WriteLine(new string('=', 60));
Console.WriteLine();

var parser = new FileIniDataParser();

var simple = new SimplePoco { Name = "Hello World", Age = 42 };
var simpleBytes = IniSerializer.SerializeToUtf8Bytes(simple);
var simpleStr = Encoding.UTF8.GetString(simpleBytes);

// Build equivalent IniData for ini-parser
var simpleIni = new IniParser.Model.IniData();
simpleIni.Global["Name"] = "Hello World";
simpleIni.Global["Age"] = "42";

IniParser.Model.IniData ParseIni(string s)
{
    var p = new IniParser.Parser.IniDataParser();
    return p.Parse(s);
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
