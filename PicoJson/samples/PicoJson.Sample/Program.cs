public class Person
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<string> Tags { get; set; } = new();
}

public class DemoModel
{
    public Guid Id { get; set; }
    public decimal Price { get; set; }
    public DayOfWeek Day { get; set; }
    public int? OptionalScore { get; set; }
    public Dictionary<string, int> Counts { get; set; } = new();
    public DateOnly StartDate { get; set; }
    public TimeSpan Duration { get; set; }
}

class Program
{
    static void Main()
    {
        var person = new Person
        {
            Name = "Alice",
            Age = 30,
            CreatedAt = new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Utc),
            Tags =  ["developer", "runner"]
        };

        // One line: Source Generator produces serializers at compile time
        var bytes = JsonSerializer.SerializeToUtf8Bytes(person);
        Console.WriteLine("=== Serialized Person ===");
        Console.WriteLine(Encoding.UTF8.GetString(bytes));

        // One line: deserialize back
        var restored = JsonSerializer.Deserialize<Person>(bytes);
        Console.WriteLine(
            $"\n=== Deserialized Person ===\nName: {restored?.Name}, Age: {restored?.Age}"
        );

        // New type support demo
        var demo = new DemoModel
        {
            Id = Guid.NewGuid(),
            Price = 99.99m,
            Day = DayOfWeek.Friday,
            OptionalScore = 42,
            Counts = new() { ["a"] = 1, ["b"] = 2 },
            StartDate = new DateOnly(2026, 5, 25),
            Duration = TimeSpan.FromHours(1.5),
        };
        var demoBytes = JsonSerializer.SerializeToUtf8Bytes(demo);
        Console.WriteLine($"\n=== Serialized DemoModel ===\n{Encoding.UTF8.GetString(demoBytes)}");
        var demoRestored = JsonSerializer.Deserialize<DemoModel>(demoBytes);
        Console.WriteLine(
            $"\n=== Deserialized DemoModel ===\nId: {demoRestored?.Id}\nPrice: {demoRestored?.Price}\nDay: {demoRestored?.Day}\nOptionalScore: {demoRestored?.OptionalScore}\nCounts: {demoRestored?.Counts?.Count} entries\nStartDate: {demoRestored?.StartDate}\nDuration: {demoRestored?.Duration}"
        );

        // Raw JsonWriter
        var buf = new ArrayBufferWriter<byte>(128);
        var jw = new JsonWriter(buf);
        jw.WriteStartObject();
        jw.WritePropertyName("greeting"u8);
        jw.WriteString("Hello from PicoJson!"u8);
        jw.WriteEndObject();
        Console.WriteLine($"\n=== Raw Writer ===\n{Encoding.UTF8.GetString(buf.WrittenSpan)}");
    }
}
