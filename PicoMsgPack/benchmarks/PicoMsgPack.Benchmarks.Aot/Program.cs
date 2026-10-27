using MpLib = MessagePack.MessagePackSerializer;

// ─────────────────────────────────────────────────────────────────────────────
// This benchmark exists because the unified MsgPack benchmark CANNOT run under
// NativeAOT, and pretending otherwise is what broke the README's numbers.
//
// MessagePack-CSharp creates formatters for enums and for generic collections at
// run time with MakeGenericType + Activator.CreateInstance. NativeAOT has no
// runtime code generation, so the first such type throws
// "MissingMethodException: No parameterless constructor defined for type ...".
// The unified benchmark's models carry DayOfWeek, Dictionary<string,string> and
// List<int>, so under AOT it dies on the first complex model.
//
// The models below are deliberately restricted to types MessagePack resolves
// statically (string, int, long, bool, double, DateTime). That is what makes an
// honest AOT-vs-AOT comparison possible: both sides are compiled ahead of time and
// both actually finish.
//
// The mode is now printed and asserted rather than assumed: the banner reports
// Aot/Jit from RuntimeFeature.IsDynamicCodeSupported, and a JIT run says out loud
// that it is not the AOT-vs-AOT pair this file claims to be.
// ─────────────────────────────────────────────────────────────────────────────

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("PicoMsgPack vs MessagePack-CSharp — AOT vs AOT");
Console.WriteLine($"Runtime: {Environment.Version} | OS: {Environment.OSVersion}");
Console.WriteLine(
    "Both sides are NativeAOT. Models use only statically resolvable types — the"
        + " unified benchmark's models cannot run under AOT (see the file header)."
);
Console.WriteLine(new string('=', 60));

if (BenchProtocol.Mode != ProtocolMode.Aot)
    Console.WriteLine(
        "⚠ WARNING: this binary is NOT NativeAOT (Mode: Jit), so the table below is a"
            + " JIT-vs-JIT run and must not be published as AOT-vs-AOT."
    );

var person = new AotPerson { Name = "Hello World", Age = 42 };
var metrics = new AotMetrics
{
    Active = true,
    Score = 4.5,
    Ticks = 1234567890123,
    When = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc),
};

var personPico = MsgPackSerializer.SerializeToUtf8Bytes(person);
var personMp = MpLib.Serialize(person);
var metricsPico = MsgPackSerializer.SerializeToUtf8Bytes(metrics);
var metricsMp = MpLib.Serialize(metrics);

// Roles are positional: ours = PicoMsgPack, theirs = MessagePack-CSharp. The table
// runs the pair through BenchRoleGuard (candidate must be a Pico* name), so the two
// columns cannot be swapped without an exception.
var table = new ProtocolTable("PicoMsgPack", "MessagePack-CSharp");

table.Compare(
    "Person — Serialize",
    () => MsgPackSerializer.SerializeToUtf8Bytes(person),
    () => MpLib.Serialize(person)
);
table.Compare(
    "Person — Deserialize",
    () => MsgPackSerializer.Deserialize<AotPerson>(personPico),
    () => MpLib.Deserialize<AotPerson>(personMp)
);
table.Compare(
    "Metrics — Serialize",
    () => MsgPackSerializer.SerializeToUtf8Bytes(metrics),
    () => MpLib.Serialize(metrics)
);
table.Compare(
    "Metrics — Deserialize",
    () => MsgPackSerializer.Deserialize<AotMetrics>(metricsPico),
    () => MpLib.Deserialize<AotMetrics>(metricsMp)
);

table.Print();
Console.WriteLine(
    $"Size: Person Pico={personPico.Length}B MPC={personMp.Length}B  |  Metrics"
        + $" Pico={metricsPico.Length}B MPC={metricsMp.Length}B"
);
Console.WriteLine(new string('=', 60));

Environment.ExitCode = BenchProtocol.ExitCode;

return;

// Statically resolvable only: MessagePack's builtin resolver covers these without
// run-time generic construction, which is what NativeAOT cannot do.
[MessagePack.MessagePackObject]
public class AotPerson
{
    [MsgPackKey(0)]
    [MessagePack.Key(0)]
    public string Name { get; set; } = string.Empty;

    [MsgPackKey(1)]
    [MessagePack.Key(1)]
    public int Age { get; set; }
}

[MessagePack.MessagePackObject]
public class AotMetrics
{
    [MsgPackKey(0)]
    [MessagePack.Key(0)]
    public bool Active { get; set; }

    [MsgPackKey(1)]
    [MessagePack.Key(1)]
    public double Score { get; set; }

    [MsgPackKey(2)]
    [MessagePack.Key(2)]
    public long Ticks { get; set; }

    [MsgPackKey(3)]
    [MessagePack.Key(3)]
    public DateTime When { get; set; }
}
