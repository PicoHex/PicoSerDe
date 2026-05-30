using PicoBench;
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
    IsActive = true
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
        Zip = "94105"
    },
    Tags =  ["dev", "runner", "benchmark"]
};
var nestedBytesPico = JsonSerializer.SerializeToUtf8Bytes(nested);
var nestedBytesStj = StjJson.SerializeToUtf8Bytes(nested, ctx.NestedPoco);

var collection = new CollectionPoco
{
    Scores = Enumerable.Range(0, 100).ToList(),
    Metadata = new() { ["source"] = "benchmark", ["mode"] = "comparison" }
};
var collectionBytesPico = JsonSerializer.SerializeToUtf8Bytes(collection);
var collectionBytesStj = StjJson.SerializeToUtf8Bytes(collection, ctx.CollectionPoco);

// ═══ Benchmark Suite ═══

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("PicoJson vs System.Text.Json — Performance Comparison");
Console.WriteLine($"Runtime: {Environment.Version} | OS: {Environment.OSVersion}");
Console.WriteLine(new string('=', 60));
Console.WriteLine();

// --- 1. Serialize Simple POCO ---
var serSimple = Benchmark.Compare(
    "Simple POCO — Serialize",
    "PicoJson",
    () => JsonSerializer.SerializeToUtf8Bytes(simple),
    "System.Text.Json",
    () => StjJson.SerializeToUtf8Bytes(simple, ctx.SimplePoco)
);
PrintComparison(serSimple);

// --- 2. Deserialize Simple POCO ---
var deserSimple = Benchmark.Compare(
    "Simple POCO — Deserialize",
    "PicoJson",
    () => JsonSerializer.Deserialize<SimplePoco>(simpleBytesPico),
    "System.Text.Json",
    () => StjJson.Deserialize(simpleBytesStj, ctx.SimplePoco)
);
PrintComparison(deserSimple);

// --- 3. Serialize Complex POCO ---
var serComplex = Benchmark.Compare(
    "Complex POCO — Serialize",
    "PicoJson",
    () => JsonSerializer.SerializeToUtf8Bytes(complex),
    "System.Text.Json",
    () => StjJson.SerializeToUtf8Bytes(complex, ctx.ComplexPoco)
);
PrintComparison(serComplex);

// --- 4. Deserialize Complex POCO ---
var deserComplex = Benchmark.Compare(
    "Complex POCO — Deserialize",
    "PicoJson",
    () => JsonSerializer.Deserialize<ComplexPoco>(complexBytesPico),
    "System.Text.Json",
    () => StjJson.Deserialize(complexBytesStj, ctx.ComplexPoco)
);
PrintComparison(deserComplex);

// --- 5. Serialize Nested POCO ---
var serNested = Benchmark.Compare(
    "Nested POCO — Serialize",
    "PicoJson",
    () => JsonSerializer.SerializeToUtf8Bytes(nested),
    "System.Text.Json",
    () => StjJson.SerializeToUtf8Bytes(nested, ctx.NestedPoco)
);
PrintComparison(serNested);

// --- 6. Deserialize Nested POCO ---
var deserNested = Benchmark.Compare(
    "Nested POCO — Deserialize",
    "PicoJson",
    () => JsonSerializer.Deserialize<NestedPoco>(nestedBytesPico),
    "System.Text.Json",
    () => StjJson.Deserialize(nestedBytesStj, ctx.NestedPoco)
);
PrintComparison(deserNested);

// --- 7. Serialize Collection POCO ---
var serCol = Benchmark.Compare(
    "Collection POCO — Serialize",
    "PicoJson",
    () => JsonSerializer.SerializeToUtf8Bytes(collection),
    "System.Text.Json",
    () => StjJson.SerializeToUtf8Bytes(collection, ctx.CollectionPoco)
);
PrintComparison(serCol);

// --- 8. Deserialize Collection POCO ---
var deserCol = Benchmark.Compare(
    "Collection POCO — Deserialize",
    "PicoJson",
    () => JsonSerializer.Deserialize<CollectionPoco>(collectionBytesPico),
    "System.Text.Json",
    () => StjJson.Deserialize(collectionBytesStj, ctx.CollectionPoco)
);
PrintComparison(deserCol);

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
            deserCol
        }
    )
);

static void PrintComparison(ComparisonResult c)
{
    var icon = c.IsFaster ? "✓" : "✗";
    Console.WriteLine(
        $"{icon} {c.Name, -38} PicoJson={c.Candidate.Statistics.Avg / 1000:F1}μs  "
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
internal partial class StjContext : JsonSerializerContext { }
