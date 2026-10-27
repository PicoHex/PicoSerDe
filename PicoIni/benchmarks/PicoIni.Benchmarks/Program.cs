Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("PicoIni vs ini-parser — Performance Comparison");
Console.WriteLine($"Runtime: {Environment.Version} | OS: {Environment.OSVersion}");
Console.WriteLine(
    "NOTE: ini-parser parses a DOM under NativeAOT; its reflection-based typed mapping does not."
);
Console.WriteLine(new string('=', 60));
Console.WriteLine();

var parser = new FileIniDataParser();

var simple = new SimplePoco { Name = "Hello World", Age = 42 };
var simpleBytes = IniSerializer.SerializeToUtf8Bytes(simple);
var simpleStr = IniSerializer.Serialize(simple);

var complex = new ComplexPoco
{
    Id = Guid.NewGuid(),
    Title = "Benchmark Suite",
    Price = 149.99m,
    CreatedAt = DateTime.UtcNow,
    Day = DayOfWeek.Friday,
    Rating = 4.5,
    IsActive = true,
};
var complexBytes = IniSerializer.SerializeToUtf8Bytes(complex);
var complexStr = IniSerializer.Serialize(complex);

var medium = new MediumScalarPoco
{
    Alpha = "a",
    Beta = 2,
    Gamma = true,
    Delta = 4,
    Epsilon = 5.5,
    Zeta = "z",
};
var mediumBytes = IniSerializer.SerializeToUtf8Bytes(medium);

var large = new LargeFlatPoco
{
    F01 = "a",
    F02 = "b",
    F03 = "c",
    F04 = "d",
    F05 = "e",
    F06 = "f",
    F07 = "g",
    F08 = "h",
    F09 = "i",
    F10 = "j",
    N01 = 1,
    N02 = 2,
    N03 = 3,
    N04 = 4,
    N05 = 5,
    N06 = 6,
    N07 = 7,
    N08 = 8,
    N09 = 9,
    N10 = 10,
    B01 = true,
    B02 = false,
    B03 = true,
    B04 = false,
    B05 = true,
    D01 = 1.1,
    D02 = 2.2,
    D03 = 3.3,
    D04 = 4.4,
    D05 = 5.5,
};
var largeBytes = IniSerializer.SerializeToUtf8Bytes(large);

var simpleStrBytes = Encoding.UTF8.GetBytes(simpleStr);
var complexStrBytes = Encoding.UTF8.GetBytes(complexStr);

// Roles are positional: ours = PicoIni, theirs = ini-parser. The table runs the pair
// through BenchRoleGuard (candidate must be a Pico* name), so the two columns cannot
// be swapped without an exception.
//
// NON-EQUIVALENT — not comparable: the ini-parser arm is a hand-written DOM walk
// (IniDataParser + IniData.WriteTo / Global[...] reads) plus a typed mapping performed
// by this file, not a single library call. The timings are listed for reference and
// the table computes and prints no ratio: a ratio between two different jobs measures
// neither library.
var table = new ProtocolTable("PicoIni", "ini-parser¹", equivalent: false);

table.Compare(
    "Simple — Serialize (bytes)",
    () => IniSerializer.SerializeToUtf8Bytes(simple),
    () => SerIniSimple()
);
table.Compare(
    "Simple — Deserialize (bytes)",
    () => IniSerializer.Deserialize<SimplePoco>(simpleBytes),
    () => DeserSimple(simpleStr)
);
table.Compare(
    "Medium — Serialize (bytes)",
    () => IniSerializer.SerializeToUtf8Bytes(medium),
    () => SerIniMedium(medium)
);
table.Compare(
    "Medium — Deserialize (bytes)",
    () => IniSerializer.Deserialize<MediumScalarPoco>(mediumBytes),
    () => DeserMedium(IniSerializer.Serialize(medium))
);
table.Compare(
    "Complex — Serialize (bytes)",
    () => IniSerializer.SerializeToUtf8Bytes(complex),
    () => SerIniComplex(complex)
);
table.Compare(
    "Large — Serialize (bytes)",
    () => IniSerializer.SerializeToUtf8Bytes(large),
    () => SerIniLarge(large)
);
table.Compare(
    "Large — Deserialize (bytes)",
    () => IniSerializer.Deserialize<LargeFlatPoco>(largeBytes),
    () => DeserLarge(IniSerializer.Serialize(large))
);
table.Compare(
    "Simple — SerializeToString",
    () => IniSerializer.Serialize(simple),
    () => SerIniSimple()
);
table.Compare(
    "Simple — Deserialize←string",
    () => IniSerializer.Deserialize<SimplePoco>(simpleStrBytes),
    () => DeserSimple(simpleStr)
);

table.Print();
Console.WriteLine();
Console.WriteLine(
    "¹ ini-parser: DOM parsing is AOT-safe, POCO mapping needs reflection and is not."
);
Console.WriteLine(new string('=', 60));

Environment.ExitCode = BenchProtocol.ExitCode;

return;

// ── Helpers ──

IniParser.Model.IniData ParseIni(string s) => new IniParser.Parser.IniDataParser().Parse(s);
byte[] SerIni(IniParser.Model.IniData d)
{
    using var ms = new MemoryStream();
    using var sw = new StreamWriter(ms);
    parser.WriteData(sw, d);
    sw.Flush();
    return ms.ToArray();
}

byte[] SerIniSimple() => SerIni(IS(new() { ["Name"] = "Hello World", ["Age"] = "42" }));
byte[] SerIniMedium(MediumScalarPoco p) =>
    SerIni(
        IS(
            new()
            {
                ["Alpha"] = p.Alpha,
                ["Beta"] = p.Beta.ToString(),
                ["Gamma"] = p.Gamma.ToString(),
                ["Delta"] = p.Delta.ToString(),
                ["Epsilon"] = p.Epsilon.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Zeta"] = p.Zeta,
            }
        )
    );
byte[] SerIniComplex(ComplexPoco p) =>
    SerIni(
        IS(
            new()
            {
                ["Id"] = p.Id.ToString(),
                ["Title"] = p.Title,
                ["Price"] = p.Price.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["CreatedAt"] = p.CreatedAt.ToString("O"),
                ["Day"] = p.Day.ToString(),
                ["Rating"] = p.Rating.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["IsActive"] = p.IsActive.ToString(),
            }
        )
    );
byte[] SerIniLarge(LargeFlatPoco p) =>
    SerIni(
        IS(
            new()
            {
                ["F01"] = p.F01,
                ["F02"] = p.F02,
                ["F03"] = p.F03,
                ["F04"] = p.F04,
                ["F05"] = p.F05,
                ["F06"] = p.F06,
                ["F07"] = p.F07,
                ["F08"] = p.F08,
                ["F09"] = p.F09,
                ["F10"] = p.F10,
                ["N01"] = p.N01.ToString(),
                ["N02"] = p.N02.ToString(),
                ["N03"] = p.N03.ToString(),
                ["N04"] = p.N04.ToString(),
                ["N05"] = p.N05.ToString(),
                ["N06"] = p.N06.ToString(),
                ["N07"] = p.N07.ToString(),
                ["N08"] = p.N08.ToString(),
                ["N09"] = p.N09.ToString(),
                ["N10"] = p.N10.ToString(),
                ["B01"] = p.B01.ToString(),
                ["B02"] = p.B02.ToString(),
                ["B03"] = p.B03.ToString(),
                ["B04"] = p.B04.ToString(),
                ["B05"] = p.B05.ToString(),
                ["D01"] = p.D01.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["D02"] = p.D02.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["D03"] = p.D03.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["D04"] = p.D04.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["D05"] = p.D05.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }
        )
    );

static IniParser.Model.IniData IS(Dictionary<string, string> d)
{
    var ini = new IniParser.Model.IniData();
    foreach (var kv in d)
        ini.Global[kv.Key] = kv.Value;
    return ini;
}

SimplePoco DeserSimple(string s)
{
    var d = ParseIni(s);
    return new SimplePoco { Name = d.Global["Name"], Age = int.Parse(d.Global["Age"]) };
}
MediumScalarPoco DeserMedium(string s)
{
    var d = ParseIni(s);
    return new MediumScalarPoco
    {
        Alpha = d.Global["Alpha"],
        Beta = int.Parse(d.Global["Beta"]),
        Gamma = bool.Parse(d.Global["Gamma"]),
        Delta = long.Parse(d.Global["Delta"]),
        Epsilon = double.Parse(
            d.Global["Epsilon"],
            System.Globalization.CultureInfo.InvariantCulture
        ),
        Zeta = d.Global["Zeta"],
    };
}
LargeFlatPoco DeserLarge(string s)
{
    var d = ParseIni(s);
    return new LargeFlatPoco
    {
        F01 = d.Global["F01"],
        F02 = d.Global["F02"],
        F03 = d.Global["F03"],
        F04 = d.Global["F04"],
        F05 = d.Global["F05"],
        F06 = d.Global["F06"],
        F07 = d.Global["F07"],
        F08 = d.Global["F08"],
        F09 = d.Global["F09"],
        F10 = d.Global["F10"],
        N01 = int.Parse(d.Global["N01"]),
        N02 = int.Parse(d.Global["N02"]),
        N03 = int.Parse(d.Global["N03"]),
        N04 = int.Parse(d.Global["N04"]),
        N05 = int.Parse(d.Global["N05"]),
        N06 = int.Parse(d.Global["N06"]),
        N07 = int.Parse(d.Global["N07"]),
        N08 = int.Parse(d.Global["N08"]),
        N09 = int.Parse(d.Global["N09"]),
        N10 = int.Parse(d.Global["N10"]),
        B01 = bool.Parse(d.Global["B01"]),
        B02 = bool.Parse(d.Global["B02"]),
        B03 = bool.Parse(d.Global["B03"]),
        B04 = bool.Parse(d.Global["B04"]),
        B05 = bool.Parse(d.Global["B05"]),
        D01 = double.Parse(d.Global["D01"], System.Globalization.CultureInfo.InvariantCulture),
        D02 = double.Parse(d.Global["D02"], System.Globalization.CultureInfo.InvariantCulture),
        D03 = double.Parse(d.Global["D03"], System.Globalization.CultureInfo.InvariantCulture),
        D04 = double.Parse(d.Global["D04"], System.Globalization.CultureInfo.InvariantCulture),
        D05 = double.Parse(d.Global["D05"], System.Globalization.CultureInfo.InvariantCulture),
    };
}
