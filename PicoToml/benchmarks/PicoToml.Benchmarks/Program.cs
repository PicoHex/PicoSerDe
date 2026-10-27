Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("PicoToml vs Tommy — Performance Comparison");
Console.WriteLine($"Runtime: {Environment.Version} | OS: {Environment.OSVersion}");
Console.WriteLine(
    "NOTE: Tommy is DOM-only under NativeAOT — it has no typed POCO mapper to run there."
);
Console.WriteLine(new string('=', 60));
Console.WriteLine();

var simple = new SimplePoco { Name = "Hello World", Age = 42 };
var simpleBytes = TomlSerializer.SerializeToUtf8Bytes(simple);
var simpleStr = TomlSerializer.Serialize(simple);

var nested = new NestedPoco
{
    Id = 1,
    Name = "Alice",
    Address = new()
    {
        Street = "123 Main",
        City = "SF",
        Zip = "94105",
    },
    Tags = ["dev", "bench"],
};
var nestedBytes = TomlSerializer.SerializeToUtf8Bytes(nested);
var nestedStr = TomlSerializer.Serialize(nested);

var medium = new MediumScalarPoco
{
    Alpha = "a",
    Beta = 2,
    Gamma = true,
    Delta = 4,
    Epsilon = 5.5,
    Zeta = "z",
};
var mediumBytes = TomlSerializer.SerializeToUtf8Bytes(medium);

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
var largeBytes = TomlSerializer.SerializeToUtf8Bytes(large);

var simpleStrBytes = Encoding.UTF8.GetBytes(simpleStr);
var nestedStrBytes = Encoding.UTF8.GetBytes(nestedStr);

// Roles are positional: ours = PicoToml, theirs = Tommy. The table runs the pair
// through BenchRoleGuard (candidate must be a Pico* name), so the two columns cannot
// be swapped without an exception.
//
// NON-EQUIVALENT — not comparable: the Tommy arm is a hand-written TomlTable DOM walk
// (TOML.Parse + TomlTable writes / TomlTable[...] reads) plus a typed mapping
// performed by this file, not a single library call. The timings are listed for
// reference and the table computes and prints no ratio: a ratio between two different
// jobs measures neither library.
var table = new ProtocolTable("PicoToml", "Tommy¹", equivalent: false);

table.Compare(
    "Simple — Serialize (bytes)",
    () => TomlSerializer.SerializeToUtf8Bytes(simple),
    () => TommySerSimple()
);
table.Compare(
    "Simple — Deserialize (bytes)",
    () => TomlSerializer.Deserialize<SimplePoco>(simpleBytes),
    () => TommyDeserSimple(simpleStr)
);
table.Compare(
    "Medium — Serialize (bytes)",
    () => TomlSerializer.SerializeToUtf8Bytes(medium),
    () => TommySerMedium(medium)
);
table.Compare(
    "Medium — Deserialize (bytes)",
    () => TomlSerializer.Deserialize<MediumScalarPoco>(mediumBytes),
    () => TommyDeserMedium(TomlSerializer.Serialize(medium))
);
table.Compare(
    "Nested — Serialize (bytes)",
    () => TomlSerializer.SerializeToUtf8Bytes(nested),
    () => TommySerNested(nested)
);
table.Compare(
    "Nested — Deserialize (bytes)",
    () => TomlSerializer.Deserialize<NestedPoco>(nestedBytes),
    () => TommyDeserNested(nestedStr)
);
table.Compare(
    "Large — Serialize (bytes)",
    () => TomlSerializer.SerializeToUtf8Bytes(large),
    () => TommySerLarge(large)
);
table.Compare(
    "Large — Deserialize (bytes)",
    () => TomlSerializer.Deserialize<LargeFlatPoco>(largeBytes),
    () => TommyDeserLarge(TomlSerializer.Serialize(large))
);
table.Compare(
    "Simple — SerializeToString",
    () => TomlSerializer.Serialize(simple),
    () => TommySerStrSimple()
);
table.Compare(
    "Simple — Deserialize←string",
    () => TomlSerializer.Deserialize<SimplePoco>(simpleStrBytes),
    () => TommyDeserSimple(simpleStr)
);

table.Print();
Console.WriteLine();
Console.WriteLine("¹ Tommy: DOM-only (TOML.Parse / TomlTable); no typed POCO mapping.");
Console.WriteLine(new string('=', 60));

Environment.ExitCode = BenchProtocol.ExitCode;

return;

// ── Helpers ──

static byte[] TommySer(TomlTable t)
{
    using var sw = new StringWriter();
    t.WriteTo(sw);
    sw.Flush();
    return Encoding.UTF8.GetBytes(sw.ToString());
}
static string TommySerStr(TomlTable t)
{
    using var sw = new StringWriter();
    t.WriteTo(sw);
    sw.Flush();
    return sw.ToString();
}

static byte[] TommySerSimple() =>
    TommySer(new TomlTable { ["Name"] = "Hello World", ["Age"] = 42L });
static string TommySerStrSimple() =>
    TommySerStr(new TomlTable { ["Name"] = "Hello World", ["Age"] = 42L });

static SimplePoco TommyDeserSimple(string s)
{
    using var sr = new StringReader(s);
    var t = TOML.Parse(sr);
    return new SimplePoco { Name = t["Name"].AsString, Age = (int)t["Age"].AsInteger };
}

static byte[] TommySerNested(NestedPoco p) =>
    TommySer(
        new TomlTable
        {
            ["Id"] = p.Id,
            ["Name"] = p.Name,
            ["Address"] = new TomlTable
            {
                ["Street"] = p.Address!.Street,
                ["City"] = p.Address.City,
                ["Zip"] = p.Address.Zip!,
            },
            ["Tags"] = new TomlArray { "dev", "bench" },
        }
    );
static NestedPoco TommyDeserNested(string s)
{
    using var sr = new StringReader(s);
    var t = TOML.Parse(sr);
    var tags = new List<string>();
    if (t["Tags"].IsArray)
        for (int i = 0; ; i++)
        {
            try
            {
                tags.Add(t["Tags"][i].AsString.Value);
            }
            catch
            {
                break;
            }
        }
    return new NestedPoco
    {
        Id = (int)t["Id"].AsInteger,
        Name = t["Name"].AsString,
        Address = new()
        {
            Street = t["Address"]["Street"].AsString,
            City = t["Address"]["City"].AsString,
            Zip = t["Address"]["Zip"].AsString,
        },
        Tags = tags,
    };
}

static byte[] TommySerMedium(MediumScalarPoco p) =>
    TommySer(
        new TomlTable
        {
            ["Alpha"] = p.Alpha,
            ["Beta"] = p.Beta,
            ["Gamma"] = p.Gamma,
            ["Delta"] = p.Delta,
            ["Epsilon"] = p.Epsilon,
            ["Zeta"] = p.Zeta,
        }
    );
static MediumScalarPoco TommyDeserMedium(string s)
{
    using var sr = new StringReader(s);
    var t = TOML.Parse(sr);
    return new MediumScalarPoco
    {
        Alpha = t["Alpha"].AsString,
        Beta = (int)t["Beta"].AsInteger,
        Gamma = t["Gamma"].AsBoolean,
        Delta = t["Delta"].AsInteger,
        Epsilon = t["Epsilon"].AsFloat,
        Zeta = t["Zeta"].AsString,
    };
}

static byte[] TommySerLarge(LargeFlatPoco p) =>
    TommySer(
        new TomlTable
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
            ["N01"] = p.N01,
            ["N02"] = p.N02,
            ["N03"] = p.N03,
            ["N04"] = p.N04,
            ["N05"] = p.N05,
            ["N06"] = p.N06,
            ["N07"] = p.N07,
            ["N08"] = p.N08,
            ["N09"] = p.N09,
            ["N10"] = p.N10,
            ["B01"] = p.B01,
            ["B02"] = p.B02,
            ["B03"] = p.B03,
            ["B04"] = p.B04,
            ["B05"] = p.B05,
            ["D01"] = p.D01,
            ["D02"] = p.D02,
            ["D03"] = p.D03,
            ["D04"] = p.D04,
            ["D05"] = p.D05,
        }
    );
static LargeFlatPoco TommyDeserLarge(string s)
{
    using var sr = new StringReader(s);
    var t = TOML.Parse(sr);
    return new LargeFlatPoco
    {
        F01 = t["F01"].AsString,
        F02 = t["F02"].AsString,
        F03 = t["F03"].AsString,
        F04 = t["F04"].AsString,
        F05 = t["F05"].AsString,
        F06 = t["F06"].AsString,
        F07 = t["F07"].AsString,
        F08 = t["F08"].AsString,
        F09 = t["F09"].AsString,
        F10 = t["F10"].AsString,
        N01 = (int)t["N01"].AsInteger,
        N02 = (int)t["N02"].AsInteger,
        N03 = (int)t["N03"].AsInteger,
        N04 = (int)t["N04"].AsInteger,
        N05 = (int)t["N05"].AsInteger,
        N06 = (int)t["N06"].AsInteger,
        N07 = (int)t["N07"].AsInteger,
        N08 = (int)t["N08"].AsInteger,
        N09 = (int)t["N09"].AsInteger,
        N10 = (int)t["N10"].AsInteger,
        B01 = t["B01"].AsBoolean,
        B02 = t["B02"].AsBoolean,
        B03 = t["B03"].AsBoolean,
        B04 = t["B04"].AsBoolean,
        B05 = t["B05"].AsBoolean,
        D01 = t["D01"].AsFloat,
        D02 = t["D02"].AsFloat,
        D03 = t["D03"].AsFloat,
        D04 = t["D04"].AsFloat,
        D05 = t["D05"].AsFloat,
    };
}
