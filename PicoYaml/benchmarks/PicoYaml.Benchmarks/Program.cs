// ═══ Benchmark: PicoYaml Serialize/Deserialize ═══
var config = new BenchModel
{
    Name = "BenchApp",
    Count = 42,
    Enabled = true
};

var bytes = YamlSerializer.SerializeToUtf8Bytes(config);
var restored = YamlSerializer.Deserialize<BenchModel>(bytes);

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("=== PicoYaml Benchmark ===");
Console.WriteLine($"Output size: {bytes.Length} bytes");

var sw = System.Diagnostics.Stopwatch.StartNew();
const int N = 10_000;
for (int i = 0; i < N; i++)
    YamlSerializer.SerializeToUtf8Bytes(config);
sw.Stop();
Console.WriteLine(
    $"Serialize:   {sw.ElapsedMilliseconds}ms ({sw.ElapsedMilliseconds * 1000.0 / N:F2}us/op)"
);

sw.Restart();
for (int i = 0; i < N; i++)
    YamlSerializer.Deserialize<BenchModel>(bytes);
sw.Stop();
Console.WriteLine(
    $"Deserialize: {sw.ElapsedMilliseconds}ms ({sw.ElapsedMilliseconds * 1000.0 / N:F2}us/op)"
);
Console.WriteLine($"Round-trip:  {(restored?.Name == config.Name ? "OK" : "FAIL")}");

public class BenchModel
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
    public bool Enabled { get; set; }
}
