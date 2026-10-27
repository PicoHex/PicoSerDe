using MpLib = MessagePack.MessagePackSerializer;

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("PicoMsgPack vs MessagePack-CSharp — Performance Comparison");
Console.WriteLine($"Runtime: {Environment.Version} | OS: {Environment.OSVersion}");
Console.WriteLine(new string('=', 60));

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
    IsActive = true,
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
        Zip = "94105",
    },
    Tags = new List<string> { "dev", "bench" },
};
var nestedPico = MsgPackSerializer.SerializeToUtf8Bytes(nested);
var nestedMp = MpLib.Serialize(nested);

var collection = new MsgCollection
{
    Scores = Enumerable.Range(0, 100).ToList(),
    Metadata = new() { ["source"] = "benchmark" },
};
var colPico = MsgPackSerializer.SerializeToUtf8Bytes(collection);
var colMp = MpLib.Serialize(collection);

// Roles are positional: ours = PicoMsgPack, theirs = MessagePack-CSharp. The table
// runs the pair through BenchRoleGuard (candidate must be a Pico* name), so the two
// columns cannot be swapped without an exception. Ratio = ours / theirs, so >1 means
// PicoMsgPack is slower — the README's direction, so a printed figure is quoted as it stands.
var table = new ProtocolTable("PicoMsgPack", "MessagePack-CSharp");

table.Compare(
    "Simple — Serialize",
    () => MsgPackSerializer.SerializeToUtf8Bytes(simple),
    () => MpLib.Serialize(simple)
);
table.Compare(
    "Simple — Deserialize",
    () => MsgPackSerializer.Deserialize<MsgSimple>(simplePico),
    () => MpLib.Deserialize<MsgSimple>(simpleMp)
);
table.Compare(
    "Complex — Serialize",
    () => MsgPackSerializer.SerializeToUtf8Bytes(complex),
    () => MpLib.Serialize(complex)
);
table.Compare(
    "Complex — Deserialize",
    () => MsgPackSerializer.Deserialize<MsgComplex>(complexPico),
    () => MpLib.Deserialize<MsgComplex>(complexMp)
);
table.Compare(
    "Nested — Serialize",
    () => MsgPackSerializer.SerializeToUtf8Bytes(nested),
    () => MpLib.Serialize(nested)
);
table.Compare(
    "Nested — Deserialize",
    () => MsgPackSerializer.Deserialize<MsgNested>(nestedPico),
    () => MpLib.Deserialize<MsgNested>(nestedMp)
);
table.Compare(
    "Collection — Serialize",
    () => MsgPackSerializer.SerializeToUtf8Bytes(collection),
    () => MpLib.Serialize(collection)
);
table.Compare(
    "Collection — Deserialize",
    () => MsgPackSerializer.Deserialize<MsgCollection>(colPico),
    () => MpLib.Deserialize<MsgCollection>(colMp)
);

table.Print();
Console.WriteLine(new string('=', 60));

Environment.ExitCode = BenchProtocol.ExitCode;

// Dual-attributed models: [MsgPackKey] for PicoMsgPack, [MessagePack.Key] for MPC
[MessagePack.MessagePackObject]
public class MsgSimple
{
    [MsgPackKey(0)]
    [MessagePack.Key(0)]
    public string Name { get; set; } = string.Empty;

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
    public string Title { get; set; } = string.Empty;

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
    public string Name { get; set; } = string.Empty;

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
    public string Street { get; set; } = string.Empty;

    [MsgPackKey(1)]
    [MessagePack.Key(1)]
    public string City { get; set; } = string.Empty;

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
