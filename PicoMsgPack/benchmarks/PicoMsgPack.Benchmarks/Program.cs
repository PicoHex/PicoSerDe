using System.Diagnostics;
using MessagePack;

var person = new PersonBench { Name = "Alice", Age = 30 };
var sw = Stopwatch.StartNew();
const int Iters = 100_000;

Console.WriteLine("=== PicoMsgPack vs MessagePack-CSharp ===\n");

// --- Serialize ---
sw.Restart(); for (int i = 0; i < Iters; i++) MsgPackSerializer.SerializeToUtf8Bytes(person); sw.Stop();
var picoSer = sw.Elapsed.TotalMilliseconds / Iters * 1_000_000;
sw.Restart(); for (int i = 0; i < Iters; i++) MessagePackSerializer.Serialize(person); sw.Stop();
var mpSer = sw.Elapsed.TotalMilliseconds / Iters * 1_000_000;
Console.WriteLine($"Serialize:   Pico={picoSer:F0}ns  MPC={mpSer:F0}ns  ({picoSer/mpSer:F2}x)");

// --- Deserialize ---
var picoBytes = MsgPackSerializer.SerializeToUtf8Bytes(person);
var mpBytes = MessagePackSerializer.Serialize(person);
sw.Restart(); for (int i = 0; i < Iters; i++) MsgPackSerializer.Deserialize<PersonBench>(picoBytes); sw.Stop();
var picoDeser = sw.Elapsed.TotalMilliseconds / Iters * 1_000_000;
sw.Restart(); for (int i = 0; i < Iters; i++) MessagePackSerializer.Deserialize<PersonBench>(mpBytes); sw.Stop();
var mpDeser = sw.Elapsed.TotalMilliseconds / Iters * 1_000_000;
Console.WriteLine($"Deserialize: Pico={picoDeser:F0}ns  MPC={mpDeser:F0}ns  ({picoDeser/mpDeser:F2}x)");

// --- Size ---
Console.WriteLine($"\nSize: Pico={picoBytes.Length}B  MPC={mpBytes.Length}B");
Console.WriteLine("Done.");

[MessagePackObject] public class PersonBench { [Key(0)] public string Name { get; set; } = ""; [Key(1)] public int Age { get; set; } }
