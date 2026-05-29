using System.Diagnostics;

// ═══ Test Data ═══
var person = new PersonBench { Name = "Alice", Age = 30 };
var tags = new TagList { Items = ["dev", "bench", "msgpack"] };

// ═══ Benchmarks ═══
var sw = Stopwatch.StartNew();
const int Iters = 100_000;

// Serialize Person
sw.Restart();
for (int i = 0; i < Iters; i++)
    MsgPackSerializer.SerializeToUtf8Bytes(person);
sw.Stop();
Console.WriteLine($"Person Serialize:   {sw.Elapsed.TotalMilliseconds / Iters * 1_000_000:F2} ns/op");

// Deserialize Person
var personBytes = MsgPackSerializer.SerializeToUtf8Bytes(person);
sw.Restart();
for (int i = 0; i < Iters; i++)
    MsgPackSerializer.Deserialize<PersonBench>(personBytes);
sw.Stop();
Console.WriteLine($"Person Deserialize: {sw.Elapsed.TotalMilliseconds / Iters * 1_000_000:F2} ns/op");

// Serialize TagList
sw.Restart();
for (int i = 0; i < Iters; i++)
    MsgPackSerializer.SerializeToUtf8Bytes(tags);
sw.Stop();
Console.WriteLine($"TagList Serialize:  {sw.Elapsed.TotalMilliseconds / Iters * 1_000_000:F2} ns/op");

// Deserialize TagList
var tagBytes = MsgPackSerializer.SerializeToUtf8Bytes(tags);
sw.Restart();
for (int i = 0; i < Iters; i++)
    MsgPackSerializer.Deserialize<TagList>(tagBytes);
sw.Stop();
Console.WriteLine($"TagList Deserialize:{sw.Elapsed.TotalMilliseconds / Iters * 1_000_000:F2} ns/op");

Console.WriteLine($"\nPerson size: {personBytes.Length} bytes");
Console.WriteLine($"TagList size: {tagBytes.Length} bytes");

public class PersonBench { [MsgPackKey(0)] public string Name { get; set; } = ""; [MsgPackKey(1)] public int Age { get; set; } }
public class TagList { [MsgPackKey(0)] public List<string> Items { get; set; } = new(); }
