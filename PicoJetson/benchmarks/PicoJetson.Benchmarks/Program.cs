using StjJson = System.Text.Json.JsonSerializer;

// ═══ Test Data ═══

var ctx = StjContext.Default;

var simple = new SimplePoco { Name = "Hello World", Age = 42 };
var simpleBytesPico = JsonSerializer.SerializeToUtf8Bytes(simple);
var simpleBytesStj = StjJson.SerializeToUtf8Bytes(simple, ctx.SimplePoco);

var complex = new ComplexPoco
{
    Id = Guid.NewGuid(),
    Title = "Benchmark Test Suite",
    Price = 149.99m,
    CreatedAt = DateTime.UtcNow,
    Day = DayOfWeek.Friday,
    Rating = 4.5,
    IsActive = true,
};
var complexBytesPico = JsonSerializer.SerializeToUtf8Bytes(complex);
var complexBytesStj = StjJson.SerializeToUtf8Bytes(complex, ctx.ComplexPoco);

var nested = new NestedPoco
{
    Id = 1,
    Name = "Alice",
    Address = new NestedAddress
    {
        Street = "123 Main St",
        City = "SF",
        Zip = "94105",
    },
    Tags = ["dev", "runner", "benchmark"],
};
var nestedBytesPico = JsonSerializer.SerializeToUtf8Bytes(nested);
var nestedBytesStj = StjJson.SerializeToUtf8Bytes(nested, ctx.NestedPoco);

var collection = new CollectionPoco
{
    Scores = Enumerable.Range(0, 100).ToList(),
    Metadata = new() { ["source"] = "benchmark", ["mode"] = "comparison" },
};
var collectionBytesPico = JsonSerializer.SerializeToUtf8Bytes(collection);
var collectionBytesStj = StjJson.SerializeToUtf8Bytes(collection, ctx.CollectionPoco);

// --- 9. Large flat POCO (50 fields, whitespace-heavy) ---
var largeFlat = new LargeFlatPoco
{
    F01 = "value-01",
    F02 = "value-02",
    F03 = "value-03",
    F04 = "value-04",
    F05 = "value-05",
    F06 = "value-06",
    F07 = "value-07",
    F08 = "value-08",
    F09 = "value-09",
    F10 = "value-10",
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
var largeFlatBytesPico = JsonSerializer.SerializeToUtf8Bytes(largeFlat);
var largeFlatBytesStj = StjJson.SerializeToUtf8Bytes(largeFlat, ctx.LargeFlatPoco);

// --- 10. Large string (1KB, no escapes → exercises ContainsBackslash fast-path) ---
var largeString = new LargeStringPoco { Body = new string('x', 1024) };
var largeStringBytesPico = JsonSerializer.SerializeToUtf8Bytes(largeString);
var largeStringBytesStj = StjJson.SerializeToUtf8Bytes(largeString, ctx.LargeStringPoco);

// ═══ Benchmark Suite ═══

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("PicoJetson vs System.Text.Json — Performance Comparison");
Console.WriteLine($"Runtime: {Environment.Version} | OS: {Environment.OSVersion}");
Console.WriteLine(new string('=', 60));

// Roles are positional: ours = PicoJetson, theirs = System.Text.Json. The table
// runs the pair through BenchRoleGuard (candidate must be a Pico* name), so the two
// columns cannot be swapped without an exception. Ratio = theirs / ours, so >1 means
// PicoJetson is faster.
var table = new ProtocolTable("PicoJetson", "System.Text.Json");

table.Compare(
    "Simple POCO — Serialize",
    () => JsonSerializer.SerializeToUtf8Bytes(simple),
    () => StjJson.SerializeToUtf8Bytes(simple, ctx.SimplePoco)
);
table.Compare(
    "Simple POCO — Deserialize",
    () => JsonSerializer.Deserialize<SimplePoco>(simpleBytesPico),
    () => StjJson.Deserialize(simpleBytesStj, ctx.SimplePoco)
);
table.Compare(
    "Complex POCO — Serialize",
    () => JsonSerializer.SerializeToUtf8Bytes(complex),
    () => StjJson.SerializeToUtf8Bytes(complex, ctx.ComplexPoco)
);
table.Compare(
    "Complex POCO — Deserialize",
    () => JsonSerializer.Deserialize<ComplexPoco>(complexBytesPico),
    () => StjJson.Deserialize(complexBytesStj, ctx.ComplexPoco)
);
table.Compare(
    "Nested POCO — Serialize",
    () => JsonSerializer.SerializeToUtf8Bytes(nested),
    () => StjJson.SerializeToUtf8Bytes(nested, ctx.NestedPoco)
);
table.Compare(
    "Nested POCO — Deserialize",
    () => JsonSerializer.Deserialize<NestedPoco>(nestedBytesPico),
    () => StjJson.Deserialize(nestedBytesStj, ctx.NestedPoco)
);
table.Compare(
    "Collection POCO — Serialize",
    () => JsonSerializer.SerializeToUtf8Bytes(collection),
    () => StjJson.SerializeToUtf8Bytes(collection, ctx.CollectionPoco)
);
table.Compare(
    "Collection POCO — Deserialize",
    () => JsonSerializer.Deserialize<CollectionPoco>(collectionBytesPico),
    () => StjJson.Deserialize(collectionBytesStj, ctx.CollectionPoco)
);
table.Compare(
    "LargeFlat 50-fields — Serialize",
    () => JsonSerializer.SerializeToUtf8Bytes(largeFlat),
    () => StjJson.SerializeToUtf8Bytes(largeFlat, ctx.LargeFlatPoco)
);
table.Compare(
    "LargeFlat 50-fields — Deserialize",
    () => JsonSerializer.Deserialize<LargeFlatPoco>(largeFlatBytesPico),
    () => StjJson.Deserialize(largeFlatBytesStj, ctx.LargeFlatPoco)
);
table.Compare(
    "LargeString 1KB — Serialize",
    () => JsonSerializer.SerializeToUtf8Bytes(largeString),
    () => StjJson.SerializeToUtf8Bytes(largeString, ctx.LargeStringPoco)
);
table.Compare(
    "LargeString 1KB — Deserialize",
    () => JsonSerializer.Deserialize<LargeStringPoco>(largeStringBytesPico),
    () => StjJson.Deserialize(largeStringBytesStj, ctx.LargeStringPoco)
);

table.Print();
Console.WriteLine(new string('=', 60));

Environment.ExitCode = BenchProtocol.ExitCode;

// ═══ S.T.J AOT Context ═══

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(SimplePoco))]
[JsonSerializable(typeof(ComplexPoco))]
[JsonSerializable(typeof(NestedPoco))]
[JsonSerializable(typeof(NestedAddress))]
[JsonSerializable(typeof(CollectionPoco))]
[JsonSerializable(typeof(LargeFlatPoco))]
[JsonSerializable(typeof(LargeStringPoco))]
internal partial class StjContext : JsonSerializerContext { }
