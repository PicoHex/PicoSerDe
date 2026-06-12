Console.OutputEncoding = Encoding.UTF8;

Console.WriteLine("=== PicoSerDe.Core Sample ===\n");

// ═══ 1. Implement ISerializer<T> + IDeserializer<T> ═══
Console.WriteLine("─── 1. Custom Serializer ───");
var serializer = new PointSerializer();
var deserializer = new PointDeserializer();

var pt = new Point { X = 3, Y = 7 };
var bytes = serializer.SerializeToBytes(pt);
Console.WriteLine($"  Point(3,7) → {bytes.Length} bytes: [{string.Join(", ", bytes)}]");

var restored = deserializer.Deserialize(bytes);
Console.WriteLine($"  Deserialized: Point({restored.X}, {restored.Y})");

// ═══ 2. SerializeToString / SerializeToBytes extensions ═══
Console.WriteLine("\n─── 2. Extension Methods ───");
var str = serializer.SerializeToString(pt);
Console.WriteLine($"  SerializeToString: \"{str}\" (length={str.Length})");

var directBytes = serializer.SerializeToBytes(new Point { X = 10, Y = 20 });
Console.WriteLine($"  Direct: Point(10,20) → {directBytes.Length} bytes");

// ═══ 3. RentWriter (ThreadStatic pooled) ═══
Console.WriteLine("\n─── 3. RentWriter ───");
var rented = SerializerExtensions.RentWriter();
rented.Write("hello"u8);
Console.WriteLine(
    $"  Rented writer: {rented.WrittenSpan.Length} bytes → \"{Encoding.UTF8.GetString(rented.WrittenSpan)}\""
);

// ═══ 4. ThrowNoSerializer<T> ═══
Console.WriteLine("\n─── 4. ThrowNoSerializer<T> ───");
try
{
    SerializerExtensions.ThrowNoSerializer<string>("MyFormat.Gen");
}
catch (InvalidOperationException ex)
{
    Console.WriteLine($"  Caught: {ex.Message}");
}

Console.WriteLine("\nAll samples passed.");

// ═══ Models ═══

public struct Point
{
    public int X;
    public int Y;
}

// ═══ Custom serializer/deserializer ═══

public class PointSerializer : ISerializer<Point>
{
    public void Serialize(IBufferWriter<byte> writer, Point value)
    {
        Span<byte> buf = stackalloc byte[8];
        BitConverter.TryWriteBytes(buf[..4], value.X);
        BitConverter.TryWriteBytes(buf[4..], value.Y);
        writer.Write(buf);
    }
}

public class PointDeserializer : IDeserializer<Point>
{
    public Point Deserialize(ReadOnlySpan<byte> data)
    {
        return new Point
        {
            X = BitConverter.ToInt32(data[..4]),
            Y = BitConverter.ToInt32(data[4..8]),
        };
    }
}
