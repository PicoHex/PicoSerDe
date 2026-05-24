using System.Buffers;
using System.Text;
using PicoJson;
using PicoSerDe.Abs;

public class Person
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
    public List<string> Tags { get; set; } = new();
}

file readonly struct PersonSerializer : ISerializer<Person>
{
    public void Serialize(IBufferWriter<byte> writer, Person value)
    {
        var jw = new JsonWriter(writer);
        jw.WriteStartObject();
        jw.WritePropertyName("Name"u8);
        jw.WriteString(Encoding.UTF8.GetBytes(value.Name));
        jw.WritePropertyName("Age"u8);
        jw.WriteNumber(value.Age);
        jw.WriteEndObject();
    }
}

file readonly struct PersonDeserializer : IDeserializer<Person>
{
    public Person Deserialize(ReadOnlySpan<byte> data)
    {
        var reader = new JsonReader(data);
        var obj = new Person();
        reader.Read();
        while (reader.Read() && reader.TokenType == TokenType.PropertyName)
        {
            var prop = reader.GetStringRaw();
            reader.Read();
            if (prop.SequenceEqual("Name"u8))
                obj.Name = Encoding.UTF8.GetString(reader.GetStringRaw());
            else if (prop.SequenceEqual("Age"u8))
            {
                reader.TryGetInt32(out var age);
                obj.Age = age;
            }
            else
                reader.TrySkip();
        }
        return obj;
    }
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
        var s = new PersonSerializer();
        var d = new PersonDeserializer();

        var bytes = s.SerializeToBytes(person);
        Console.WriteLine("=== Serialized ===");
        Console.WriteLine(Encoding.UTF8.GetString(bytes));

        var restored = d.Deserialize(bytes);
        Console.WriteLine($"\n=== Deserialized ===\nName: {restored.Name}, Age: {restored.Age}");

        var buf = new ArrayBufferWriter<byte>(128);
        var jw = new JsonWriter(buf);
        jw.WriteStartObject();
        jw.WritePropertyName("greeting"u8);
        jw.WriteString("Hello from PicoJson!"u8);
        jw.WriteEndObject();
        Console.WriteLine($"\n=== Raw Writer ===\n{Encoding.UTF8.GetString(buf.WrittenSpan)}");
    }
}
