public class Person
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
    public List<string> Tags { get; set; } = new();
}

class Program
{
    static void Main()
    {
        var person = new Person
        {
            Name = "Alice",
            Age = 30,
            Tags =  ["developer", "runner"]
        };

        // One line: Source Generator produces serializers at compile time
        var bytes = JsonSerializer.SerializeToUtf8Bytes(person);
        Console.WriteLine("=== Serialized ===");
        Console.WriteLine(Encoding.UTF8.GetString(bytes));

        // One line: deserialize back
        var restored = JsonSerializer.Deserialize<Person>(bytes);
        Console.WriteLine($"\n=== Deserialized ===\nName: {restored?.Name}, Age: {restored?.Age}");

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
