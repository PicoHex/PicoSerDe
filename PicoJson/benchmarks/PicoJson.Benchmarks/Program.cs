using System.Text;
using PicoBench;
using PicoBench.Formatters;
using PicoJson;

using StjJson = System.Text.Json.JsonSerializer;
using StjOptions = System.Text.Json.JsonSerializerOptions;

// ═══ S.T.J Serializer Options ═══

var stjOptions = new StjOptions { IncludeFields = false };

// ═══ Test Data ═══

var simple = new SimplePoco { Name = "Hello World", Age = 42 };
var simpleBytesPico = JsonSerializer.SerializeToUtf8Bytes(simple);
var simpleBytesStj = StjJson.SerializeToUtf8Bytes(simple, stjOptions);

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
var complexBytesStj = StjJson.SerializeToUtf8Bytes(complex, stjOptions);

var nested = new NestedPoco
{
    Id = 1,
    Name = "Alice",
    Address = new NestedAddress { Street = "123 Main St", City = "SF", Zip = "94105" },
    Tags = ["dev", "runner", "benchmark"]
};
var nestedBytesPico = JsonSerializer.SerializeToUtf8Bytes(nested);
var nestedBytesStj = StjJson.SerializeToUtf8Bytes(nested, stjOptions);

var collection = new CollectionPoco
{
    Scores = Enumerable.Range(0, 100).ToList(),
    Metadata = new() { ["source"] = "benchmark", ["mode"] = "comparison" }
};
var collectionBytesPico = JsonSerializer.SerializeToUtf8Bytes(collection);
var collectionBytesStj = StjJson.SerializeToUtf8Bytes(collection, stjOptions);

// ═══ Benchmark Suite ═══

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("PicoJson vs System.Text.Json — Performance Comparison");
Console.WriteLine($"Runtime: {Environment.Version} | OS: {Environment.OSVersion}");
Console.WriteLine(new string('=', 60));
Console.WriteLine();

// --- 1. Serialize Simple POCO ---
var serSimple = Benchmark.Compare(
    "Simple POCO — Serialize",
    "PicoJson", () => JsonSerializer.SerializeToUtf8Bytes(simple),
    "System.Text.Json", () => StjJson.SerializeToUtf8Bytes(simple, stjOptions)
);
PrintComparison(serSimple);

// --- 2. Deserialize Simple POCO ---
var deserSimple = Benchmark.Compare(
    "Simple POCO — Deserialize",
    "PicoJson", () => JsonSerializer.Deserialize<SimplePoco>(simpleBytesPico),
    "System.Text.Json", () => StjJson.Deserialize<SimplePoco>(simpleBytesStj, stjOptions)
);
PrintComparison(deserSimple);

// --- 3. Serialize Complex POCO ---
var serComplex = Benchmark.Compare(
    "Complex POCO — Serialize",
    "PicoJson", () => JsonSerializer.SerializeToUtf8Bytes(complex),
    "System.Text.Json", () => StjJson.SerializeToUtf8Bytes(complex, stjOptions)
);
PrintComparison(serComplex);

// --- 4. Deserialize Complex POCO ---
var deserComplex = Benchmark.Compare(
    "Complex POCO — Deserialize",
    "PicoJson", () => JsonSerializer.Deserialize<ComplexPoco>(complexBytesPico),
    "System.Text.Json", () => StjJson.Deserialize<ComplexPoco>(complexBytesStj, stjOptions)
);
PrintComparison(deserComplex);

// --- 5. Serialize Nested POCO ---
var serNested = Benchmark.Compare(
    "Nested POCO — Serialize",
    "PicoJson", () => JsonSerializer.SerializeToUtf8Bytes(nested),
    "System.Text.Json", () => StjJson.SerializeToUtf8Bytes(nested, stjOptions)
);
PrintComparison(serNested);

// --- 6. Deserialize Nested POCO ---
var deserNested = Benchmark.Compare(
    "Nested POCO — Deserialize",
    "PicoJson", () => JsonSerializer.Deserialize<NestedPoco>(nestedBytesPico),
    "System.Text.Json", () => StjJson.Deserialize<NestedPoco>(nestedBytesStj, stjOptions)
);
PrintComparison(deserNested);

// --- 7. Serialize Collection POCO ---
var serCol = Benchmark.Compare(
    "Collection POCO — Serialize",
    "PicoJson", () => JsonSerializer.SerializeToUtf8Bytes(collection),
    "System.Text.Json", () => StjJson.SerializeToUtf8Bytes(collection, stjOptions)
);
PrintComparison(serCol);

// --- 8. Deserialize Collection POCO ---
var deserCol = Benchmark.Compare(
    "Collection POCO — Deserialize",
    "PicoJson", () => JsonSerializer.Deserialize<CollectionPoco>(collectionBytesPico),
    "System.Text.Json", () => StjJson.Deserialize<CollectionPoco>(collectionBytesStj, stjOptions)
);
PrintComparison(deserCol);

// ═══ Summary ═══

Console.WriteLine();
Console.WriteLine(new string('=', 60));
Console.WriteLine(SummaryFormatter.Format(new[]
{
    serSimple, deserSimple,
    serComplex, deserComplex,
    serNested, deserNested,
    serCol, deserCol
}));

static void PrintComparison(ComparisonResult c)
{
    var icon = c.IsFaster ? "✓" : "✗";
    Console.WriteLine(
        $"{icon} {c.Name,-38} PicoJson={c.Candidate.Statistics.Avg / 1000:F1}μs  " +
        $"STJ={c.Baseline.Statistics.Avg / 1000:F1}μs  " +
        $"Speedup={c.Speedup:F2}x ({(c.ImprovementPercent >= 0 ? "+" : "")}{c.ImprovementPercent:F1}%)"
    );
}

// ═══ Model Definitions ═══

public class SimplePoco
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
}

public class ComplexPoco
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public decimal Price { get; set; }
    public DateTime CreatedAt { get; set; }
    public DayOfWeek Day { get; set; }
    public double Rating { get; set; }
    public bool IsActive { get; set; }
}

public class NestedPoco
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public NestedAddress? Address { get; set; }
    public List<string> Tags { get; set; } = new();
}

public class NestedAddress
{
    public string Street { get; set; } = "";
    public string City { get; set; } = "";
    public string? Zip { get; set; }
}

public class CollectionPoco
{
    public List<int> Scores { get; set; } = new();
    public Dictionary<string, string> Metadata { get; set; } = new();
}
