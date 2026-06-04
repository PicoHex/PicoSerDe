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

// ═══ Benchmark Suite ═══

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("PicoJetson vs System.Text.Json — Performance Comparison");
Console.WriteLine($"Runtime: {Environment.Version} | OS: {Environment.OSVersion}");
Console.WriteLine(new string('=', 60));
Console.WriteLine();

// --- 1. Serialize Simple POCO ---
var serSimple = Benchmark.Compare(
    "Simple POCO — Serialize",
    "PicoJetson",
    () => JsonSerializer.SerializeToUtf8Bytes(simple),
    "System.Text.Json",
    () => StjJson.SerializeToUtf8Bytes(simple, ctx.SimplePoco)
);
PrintComparison(serSimple);

// --- 2. Deserialize Simple POCO ---
var deserSimple = Benchmark.Compare(
    "Simple POCO — Deserialize",
    "PicoJetson",
    () => JsonSerializer.Deserialize<SimplePoco>(simpleBytesPico),
    "System.Text.Json",
    () => StjJson.Deserialize(simpleBytesStj, ctx.SimplePoco)
);
PrintComparison(deserSimple);

// --- 3. Serialize Complex POCO ---
var serComplex = Benchmark.Compare(
    "Complex POCO — Serialize",
    "PicoJetson",
    () => JsonSerializer.SerializeToUtf8Bytes(complex),
    "System.Text.Json",
    () => StjJson.SerializeToUtf8Bytes(complex, ctx.ComplexPoco)
);
PrintComparison(serComplex);

// --- 4. Deserialize Complex POCO ---
var deserComplex = Benchmark.Compare(
    "Complex POCO — Deserialize",
    "PicoJetson",
    () => JsonSerializer.Deserialize<ComplexPoco>(complexBytesPico),
    "System.Text.Json",
    () => StjJson.Deserialize(complexBytesStj, ctx.ComplexPoco)
);
PrintComparison(deserComplex);

// --- 5. Serialize Nested POCO ---
var serNested = Benchmark.Compare(
    "Nested POCO — Serialize",
    "PicoJetson",
    () => JsonSerializer.SerializeToUtf8Bytes(nested),
    "System.Text.Json",
    () => StjJson.SerializeToUtf8Bytes(nested, ctx.NestedPoco)
);
PrintComparison(serNested);

// --- 6. Deserialize Nested POCO ---
var deserNested = Benchmark.Compare(
    "Nested POCO — Deserialize",
    "PicoJetson",
    () => JsonSerializer.Deserialize<NestedPoco>(nestedBytesPico),
    "System.Text.Json",
    () => StjJson.Deserialize(nestedBytesStj, ctx.NestedPoco)
);
PrintComparison(deserNested);

// --- 7. Serialize Collection POCO ---
var serCol = Benchmark.Compare(
    "Collection POCO — Serialize",
    "PicoJetson",
    () => JsonSerializer.SerializeToUtf8Bytes(collection),
    "System.Text.Json",
    () => StjJson.SerializeToUtf8Bytes(collection, ctx.CollectionPoco)
);
PrintComparison(serCol);

// --- 8. Deserialize Collection POCO ---
var deserCol = Benchmark.Compare(
    "Collection POCO — Deserialize",
    "PicoJetson",
    () => JsonSerializer.Deserialize<CollectionPoco>(collectionBytesPico),
    "System.Text.Json",
    () => StjJson.Deserialize(collectionBytesStj, ctx.CollectionPoco)
);
PrintComparison(deserCol);

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

var serLargeFlat = Benchmark.Compare(
    "LargeFlat 50-fields — Serialize",
    "PicoJetson",
    () => JsonSerializer.SerializeToUtf8Bytes(largeFlat),
    "System.Text.Json",
    () => StjJson.SerializeToUtf8Bytes(largeFlat, ctx.LargeFlatPoco)
);
PrintComparison(serLargeFlat);

var deserLargeFlat = Benchmark.Compare(
    "LargeFlat 50-fields — Deserialize",
    "PicoJetson",
    () => JsonSerializer.Deserialize<LargeFlatPoco>(largeFlatBytesPico),
    "System.Text.Json",
    () => StjJson.Deserialize(largeFlatBytesStj, ctx.LargeFlatPoco)
);
PrintComparison(deserLargeFlat);

// --- 10. Large string (1KB, no escapes → exercises ContainsBackslash fast-path) ---
var largeString = new LargeStringPoco { Body = new string('x', 1024) };
var largeStringBytesPico = JsonSerializer.SerializeToUtf8Bytes(largeString);
var largeStringBytesStj = StjJson.SerializeToUtf8Bytes(largeString, ctx.LargeStringPoco);

var serLargeStr = Benchmark.Compare(
    "LargeString 1KB — Serialize",
    "PicoJetson",
    () => JsonSerializer.SerializeToUtf8Bytes(largeString),
    "System.Text.Json",
    () => StjJson.SerializeToUtf8Bytes(largeString, ctx.LargeStringPoco)
);
PrintComparison(serLargeStr);

var deserLargeStr = Benchmark.Compare(
    "LargeString 1KB — Deserialize",
    "PicoJetson",
    () => JsonSerializer.Deserialize<LargeStringPoco>(largeStringBytesPico),
    "System.Text.Json",
    () => StjJson.Deserialize(largeStringBytesStj, ctx.LargeStringPoco)
);
PrintComparison(deserLargeStr);

// ═══ Summary ═══

Console.WriteLine();
Console.WriteLine(new string('=', 60));
Console.WriteLine(
    SummaryFormatter.Format(
        new[]
        {
            serSimple,
            deserSimple,
            serComplex,
            deserComplex,
            serNested,
            deserNested,
            serCol,
            deserCol,
            serLargeFlat,
            deserLargeFlat,
            serLargeStr,
            deserLargeStr,
        }
    )
);

static void PrintComparison(ComparisonResult c)
{
    var icon = c.IsFaster ? "✓" : "✗";
    Console.WriteLine(
        $"{icon} {c.Name, -38} PicoJetson={c.Candidate.Statistics.Avg / 1000:F1}μs  "
            + $"STJ={c.Baseline.Statistics.Avg / 1000:F1}μs  "
            + $"Speedup={c.Speedup:F2}x ({(c.ImprovementPercent >= 0 ? "+" : "")}{c.ImprovementPercent:F1}%)"
    );
}

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
