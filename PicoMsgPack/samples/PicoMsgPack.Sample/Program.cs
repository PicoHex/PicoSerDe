Console.WriteLine("=== PicoMsgPack Sample ===");
Console.WriteLine();

var person = new Person { Name = "Alice", Age = 30 };
var bytes = MsgPackSerializer.SerializeToUtf8Bytes(person);
var result = MsgPackSerializer.Deserialize<Person>(bytes);
Console.WriteLine($"Round-trip: {result!.Name}, {result.Age}");
Console.WriteLine($"  Size: {bytes.Length} bytes");
Console.WriteLine();

Console.WriteLine("Raw reader tokens:");
var reader = new MsgPackReader(bytes);
while (reader.Read())
{
    Console.Write($"  {reader.TokenType,-14}");
    if (reader.TokenType == TokenType.String || reader.TokenType == TokenType.PropertyName)
        Console.Write($" = {Encoding.UTF8.GetString(reader.GetStringRaw())}");
    else if (reader.TokenType == TokenType.Int32)
    {
        reader.TryGetInt32(out var v);
        Console.Write($" = {v}");
    }
    Console.WriteLine();
}

Console.WriteLine();
Console.WriteLine("Manual writer round-trip:");
var buf = new ArrayBufferWriter<byte>(64);
var writer = new MsgPackWriter(buf);
writer.WriteStartObject(2);
writer.WriteInt32(0);
writer.WriteString("Bob"u8);
writer.WriteInt32(1);
writer.WriteInt32(99);
writer.WriteEndObject();
Console.WriteLine($"  Written: {buf.WrittenSpan.Length} bytes");
var r2 = new MsgPackReader(buf.WrittenSpan);
r2.Read();
r2.Read(); r2.TryGetInt32(out var k); r2.Read();
Console.WriteLine($"  Key {k} = {Encoding.UTF8.GetString(r2.GetStringRaw())}");

Console.WriteLine();
Console.WriteLine("Done.");

public class Person { [MsgPackKey(0)] public string Name { get; set; } = ""; [MsgPackKey(1)] public int Age { get; set; } }
