using MpLib = MessagePack.MessagePackSerializer;

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("PicoMsgPack vs MessagePack-CSharp — Performance Comparison");
Console.WriteLine($"Runtime: {Environment.Version} | OS: {Environment.OSVersion}");
Console.WriteLine(new string('=', 60));
Console.WriteLine();

var simple = new MsgSimple { Name = "Hello World", Age = 42 };
var simplePico = MsgPackSerializer.SerializeToUtf8Bytes(simple);
var simpleMp = MpLib.Serialize(simple);

var complex = new MsgComplex
{
    Id = Guid.NewGuid(),
    Title = "Benchmark",
    Price = 149.99m,
    CreatedAt = DateTime.UtcNow,
    Day = DayOfWeek.Friday,
    Rating = 4.5,
    IsActive = true
};
var complexPico = MsgPackSerializer.SerializeToUtf8Bytes(complex);
var complexMp = MpLib.Serialize(complex);

var nested = new MsgNested
{
    Id = 1,
    Name = "Alice",
    Address = new MsgAddress
    {
        Street = "123 Main",
        City = "SF",
        Zip = "94105"
    },
    Tags = new List<string> { "dev", "bench" }
};
var nestedPico = MsgPackSerializer.SerializeToUtf8Bytes(nested);
var nestedMp = MpLib.Serialize(nested);

var collection = new MsgCollection
{
    Scores = Enumerable.Range(0, 100).ToList(),
    Metadata = new() { ["source"] = "benchmark" }
};
var colPico = MsgPackSerializer.SerializeToUtf8Bytes(collection);
var colMp = MpLib.Serialize(collection);

var results = new List<ComparisonResult>
{
    Benchmark.Compare(
        "Simple — Serialize",
        "PicoMsgPack",
        () => MsgPackSerializer.SerializeToUtf8Bytes(simple),
        "MessagePack-CSharp",
        () => MpLib.Serialize(simple)
    ),
    Benchmark.Compare(
        "Simple — Deserialize",
        "PicoMsgPack",
        () => MsgPackSerializer.Deserialize<MsgSimple>(simplePico),
        "MessagePack-CSharp",
        () => MpLib.Deserialize<MsgSimple>(simpleMp)
    ),
    Benchmark.Compare(
        "Complex — Serialize",
        "PicoMsgPack",
        () => MsgPackSerializer.SerializeToUtf8Bytes(complex),
        "MessagePack-CSharp",
        () => MpLib.Serialize(complex)
    ),
    Benchmark.Compare(
        "Complex — Deserialize",
        "PicoMsgPack",
        () => MsgPackSerializer.Deserialize<MsgComplex>(complexPico),
        "MessagePack-CSharp",
        () => MpLib.Deserialize<MsgComplex>(complexMp)
    ),
    Benchmark.Compare(
        "Nested — Serialize",
        "PicoMsgPack",
        () => MsgPackSerializer.SerializeToUtf8Bytes(nested),
        "MessagePack-CSharp",
        () => MpLib.Serialize(nested)
    ),
    Benchmark.Compare(
        "Nested — Deserialize",
        "PicoMsgPack",
        () => MsgPackSerializer.Deserialize<MsgNested>(nestedPico),
        "MessagePack-CSharp",
        () => MpLib.Deserialize<MsgNested>(nestedMp)
    ),
    Benchmark.Compare(
        "Collection — Serialize",
        "PicoMsgPack",
        () => MsgPackSerializer.SerializeToUtf8Bytes(collection),
        "MessagePack-CSharp",
        () => MpLib.Serialize(collection)
    ),
    Benchmark.Compare(
        "Collection — Deserialize",
        "PicoMsgPack",
        () => MsgPackSerializer.Deserialize<MsgCollection>(colPico),
        "MessagePack-CSharp",
        () => MpLib.Deserialize<MsgCollection>(colMp)
    ),
};

foreach (var c in results)
{
    var icon = c.IsFaster ? "✓" : "✗";
    Console.WriteLine(
        $"{icon} {c.Name, -36} Pico={c.Candidate.Statistics.Avg / 1000:F1}μs  MPC={c.Baseline.Statistics.Avg / 1000:F1}μs  Speedup={c.Speedup:F2}x"
    );
}

Console.WriteLine();
Console.WriteLine(new string('=', 60));
Console.WriteLine(SummaryFormatter.Format(results));

// Dual-attributed models: [MsgPackKey] for PicoMsgPack, [MessagePack.Key] for MPC
[MessagePack.MessagePackObject]
public class MsgSimple
{
    [MsgPackKey(0)]
    [MessagePack.Key(0)]
    public string Name { get; set; } = "";

    [MsgPackKey(1)]
    [MessagePack.Key(1)]
    public int Age { get; set; }
}

[MessagePack.MessagePackObject]
public class MsgComplex
{
    [MsgPackKey(0)]
    [MessagePack.Key(0)]
    public Guid Id { get; set; }

    [MsgPackKey(1)]
    [MessagePack.Key(1)]
    public string Title { get; set; } = "";

    [MsgPackKey(2)]
    [MessagePack.Key(2)]
    public decimal Price { get; set; }

    [MsgPackKey(3)]
    [MessagePack.Key(3)]
    public DateTime CreatedAt { get; set; }

    [MsgPackKey(4)]
    [MessagePack.Key(4)]
    public DayOfWeek Day { get; set; }

    [MsgPackKey(5)]
    [MessagePack.Key(5)]
    public double Rating { get; set; }

    [MsgPackKey(6)]
    [MessagePack.Key(6)]
    public bool IsActive { get; set; }
}

[MessagePack.MessagePackObject]
public class MsgNested
{
    [MsgPackKey(0)]
    [MessagePack.Key(0)]
    public int Id { get; set; }

    [MsgPackKey(1)]
    [MessagePack.Key(1)]
    public string Name { get; set; } = "";

    [MsgPackKey(2)]
    [MessagePack.Key(2)]
    public MsgAddress? Address { get; set; }

    [MsgPackKey(3)]
    [MessagePack.Key(3)]
    public List<string> Tags { get; set; } = new();
}

[MessagePack.MessagePackObject]
public class MsgAddress
{
    [MsgPackKey(0)]
    [MessagePack.Key(0)]
    public string Street { get; set; } = "";

    [MsgPackKey(1)]
    [MessagePack.Key(1)]
    public string City { get; set; } = "";

    [MsgPackKey(2)]
    [MessagePack.Key(2)]
    public string? Zip { get; set; }
}

[MessagePack.MessagePackObject]
public class MsgCollection
{
    [MsgPackKey(0)]
    [MessagePack.Key(0)]
    public List<int> Scores { get; set; } = new();

    [MsgPackKey(1)]
    [MessagePack.Key(1)]
    public Dictionary<string, string> Metadata { get; set; } = new();
}
