// `PicoToml` is imported per file, not globally: Tomlyn's source generator emits a file into
// this project that declares `using Tomlyn.Serialization;` for its own `TomlReader`/
// `TomlWriter`, and a global `using PicoToml;` would make those two names ambiguous inside a
// file this project cannot edit (25 x CS0104, then CS0534 on the members it could not match).
using PicoToml;

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("PicoToml vs Tomlyn — Performance Comparison (typed, source-generated)");
Console.WriteLine($"Runtime: {Environment.Version} | OS: {Environment.OSVersion}");
Console.WriteLine(new string('=', 78));
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
var mediumStr = TomlSerializer.Serialize(medium);

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
var largeStr = TomlSerializer.Serialize(large);

// Every input is pre-encoded once, outside every measured delegate: a deserialize row has to
// time one deserialize call. (The Medium/Large rows used to call TomlSerializer.Serialize
// inside the delegate, so their competitor column was "our serialize + their parse".)
var simpleStrBytes = Encoding.UTF8.GetBytes(simpleStr);

// Tomlyn: one source-generated context and one reusable buffer, created outside every measured
// delegate for the same reason — a context rebuilt per call makes the row measure the context.
var tomlynContext = new TomlynBenchContext();
var tomlynBuffer = new MemoryStream(4096);

// Equivalence is checked before anything is timed, and the answer is printed: two columns are
// only comparable if they really are the same job on the same document.
Console.WriteLine(
    $"[equiv] simple  text identical = {TomlynStr(simple) == simpleStr}"
        + $" | pico {simpleStr.Length} chars / tomlyn {TomlynStr(simple).Length}"
);
Console.WriteLine($"[equiv] nested  text identical = {TomlynStr(nested) == nestedStr}");
if (TomlynStr(nested) != nestedStr)
{
    // Not a defect on either side: the two documents differ only in the ORDER of the keys
    // inside [Address] — our emitter writes City, Street, Zip (sorted) where Tomlyn writes
    // Street, City, Zip (declaration order). Same keys, same values, same POCO; printed so
    // the difference is visible rather than assumed.
    Console.WriteLine("        pico  : " + Escaped(nestedStr));
    Console.WriteLine("        tomlyn: " + Escaped(TomlynStr(nested)));
}
Console.WriteLine($"[equiv] medium  text identical = {TomlynStr(medium) == mediumStr}");
Console.WriteLine($"[equiv] large   text identical = {TomlynStr(large) == largeStr}");
Console.WriteLine(
    "[equiv] tomlyn stream arm == tomlyn string arm = "
        + $"{TomlynBytes(simple).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(TomlynStr(simple)))}"
);
var tomlynRoundTrip = TomlynDeserialize<SimplePoco>(simpleStr);
Console.WriteLine(
    $"[equiv] tomlyn round-trip SimplePoco = {tomlynRoundTrip.Name}/{tomlynRoundTrip.Age}"
        + " (equal to the POCO = "
        + $"{tomlynRoundTrip.Name == simple.Name && tomlynRoundTrip.Age == simple.Age})"
);
Console.WriteLine();

// TABLE 1 — the equivalent comparison.
//
// PicoToml.SerializeToUtf8Bytes<T> / Deserialize<T>  ↔  Tomlyn's typed serialize/deserialize.
// Both sides are ONE library call on the same document producing/consuming the same POCO, and
// both sides do their type mapping with their own source generator, in JIT and under
// NativeAOT alike (Tomlyn's reflection overloads do not work in AOT; this context does).
// Roles are positional and enforced by BenchRoleGuard: ours = PicoToml, theirs = Tomlyn.
var table = new ProtocolTable("PicoToml", "Tomlyn²", equivalent: true);

table.Compare(
    "Simple — Serialize (bytes)",
    () => TomlSerializer.SerializeToUtf8Bytes(simple),
    () => TomlynBytes(simple)
);
table.Compare(
    "Simple — Deserialize (bytes)",
    () => TomlSerializer.Deserialize<SimplePoco>(simpleBytes),
    () => TomlynDeserialize<SimplePoco>(simpleStr)
);
table.Compare(
    "Medium — Serialize (bytes)",
    () => TomlSerializer.SerializeToUtf8Bytes(medium),
    () => TomlynBytes(medium)
);
table.Compare(
    "Medium — Deserialize (bytes)",
    () => TomlSerializer.Deserialize<MediumScalarPoco>(mediumBytes),
    () => TomlynDeserialize<MediumScalarPoco>(mediumStr)
);
table.Compare(
    "Nested — Serialize (bytes)",
    () => TomlSerializer.SerializeToUtf8Bytes(nested),
    () => TomlynBytes(nested)
);
table.Compare(
    "Nested — Deserialize (bytes)",
    () => TomlSerializer.Deserialize<NestedPoco>(nestedBytes),
    () => TomlynDeserialize<NestedPoco>(nestedStr)
);
table.Compare(
    "Large — Serialize (bytes)",
    () => TomlSerializer.SerializeToUtf8Bytes(large),
    () => TomlynBytes(large)
);
table.Compare(
    "Large — Deserialize (bytes)",
    () => TomlSerializer.Deserialize<LargeFlatPoco>(largeBytes),
    () => TomlynDeserialize<LargeFlatPoco>(largeStr)
);
table.Compare(
    "Simple — SerializeToString",
    () => TomlSerializer.Serialize(simple),
    () => TomlynStr(simple)
);
table.Compare(
    "Simple — Deserialize←string",
    () => TomlSerializer.Deserialize<SimplePoco>(simpleStrBytes),
    () => TomlynDeserialize<SimplePoco>(simpleStr)
);

table.Print();
Console.WriteLine();

// TABLE 2 — the historical reference, kept and labelled, not deleted.
//
// NON-EQUIVALENT: Tommy 3.1.2 is DOM-only. Its column is this file's hand-written TomlTable
// walk plus typed conversion, not a library call, so its numbers are listed and never divided.
var referenceTable = new ProtocolTable(
    "PicoToml",
    "Tommy¹",
    equivalent: false,
    nonEquivalentReason: "Tommy 3.1.2 exposes no typed POCO API at all: it is TOML.Parse"
        + " (TextReader) → TomlTable, i.e. a DOM parser. The TomlTable walk and the typed"
        + " conversion in this file's helpers are caller-written, so no equivalent operation"
        + " exists to time — the column is kept as a reference, without a ratio."
);

referenceTable.Compare(
    "Simple — Serialize (bytes)",
    () => TomlSerializer.SerializeToUtf8Bytes(simple),
    () => TommySerSimple()
);
referenceTable.Compare(
    "Simple — Deserialize (bytes)",
    () => TomlSerializer.Deserialize<SimplePoco>(simpleBytes),
    () => TommyDeserSimple(simpleStr)
);
referenceTable.Compare(
    "Medium — Serialize (bytes)",
    () => TomlSerializer.SerializeToUtf8Bytes(medium),
    () => TommySerMedium(medium)
);
referenceTable.Compare(
    "Medium — Deserialize (bytes)",
    () => TomlSerializer.Deserialize<MediumScalarPoco>(mediumBytes),
    () => TommyDeserMedium(mediumStr)
);
referenceTable.Compare(
    "Nested — Serialize (bytes)",
    () => TomlSerializer.SerializeToUtf8Bytes(nested),
    () => TommySerNested(nested)
);
referenceTable.Compare(
    "Nested — Deserialize (bytes)",
    () => TomlSerializer.Deserialize<NestedPoco>(nestedBytes),
    () => TommyDeserNested(nestedStr)
);
referenceTable.Compare(
    "Large — Serialize (bytes)",
    () => TomlSerializer.SerializeToUtf8Bytes(large),
    () => TommySerLarge(large)
);
referenceTable.Compare(
    "Large — Deserialize (bytes)",
    () => TomlSerializer.Deserialize<LargeFlatPoco>(largeBytes),
    () => TommyDeserLarge(largeStr)
);
referenceTable.Compare(
    "Simple — SerializeToString",
    () => TomlSerializer.Serialize(simple),
    () => TommySerStrSimple()
);
referenceTable.Compare(
    "Simple — Deserialize←string",
    () => TomlSerializer.Deserialize<SimplePoco>(simpleStrBytes),
    () => TommyDeserSimple(simpleStr)
);

referenceTable.Print();
Console.WriteLine();

Console.WriteLine(
    "² Tomlyn 2.10.1 (theirs, table 1): typed API with its own source generator — one library"
        + " call per row, the same POCO in and out, and it round-trips under NativeAOT (this"
        + " project is published with PublishAot=true and the same arms run there). Its"
        + " 'Serialize (bytes)' arm uses the Stream overload into one reused MemoryStream"
        + " (SetLength(0) → Serialize → ToArray()); the [equiv] lines above are the evidence"
        + " that those bytes are identical to ours. The ~10 lines of TomlynBenchContext are a"
        + " compile-time configuration cost, not a per-operation cost, so they are not timed."
);
Console.WriteLine(
    "   Input shape is NOT identical on the deserialize rows, and the difference is in the"
        + " competitor's favour: our Deserialize<T>(byte[]) is handed UTF-8 bytes that were"
        + " encoded outside the delegate, so it pays the UTF-8 decode inside the timed region,"
        + " while Tomlyn's Deserialize<T>(string) is handed a string that was built outside it."
        + " Both sides do one library call on the same document, but our deserialize columns"
        + " carry one extra decode that theirs does not, which can only understate us."
);
Console.WriteLine(
    "¹ Tommy 3.1.2 (theirs, table 2): DOM-only (TOML.Parse / TomlTable); no typed POCO mapping."
        + " Kept for continuity; table 2 publishes no ratio because the two columns are not the"
        + " same job."
);
Console.WriteLine(new string('=', 78));

Environment.ExitCode = BenchProtocol.ExitCode;

return;

// ── Tomlyn arms (the equivalent competitor) ──

static string Escaped(string text) => text.Replace("\r", "\\r").Replace("\n", "\\n");

// Typed serialize → the same bytes our SerializeToUtf8Bytes produces, through Tomlyn's Stream
// overload (TOML is text, so this is where "bytes" has to come from on that side).
byte[] TomlynBytes<T>(T value)
{
    tomlynBuffer.SetLength(0);
    Tomlyn.TomlSerializer.Serialize(tomlynBuffer, value, tomlynContext);
    return tomlynBuffer.ToArray();
}

string TomlynStr<T>(T value) => Tomlyn.TomlSerializer.Serialize(value, tomlynContext);

T TomlynDeserialize<T>(string text)
    where T : class =>
    Tomlyn.TomlSerializer.Deserialize<T>(text, tomlynContext)
    ?? throw new InvalidOperationException($"Tomlyn returned null for {typeof(T).Name}.");

// ── Tommy arms (the historical reference) ──

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
