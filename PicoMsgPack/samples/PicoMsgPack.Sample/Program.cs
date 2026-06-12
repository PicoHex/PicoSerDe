Console.WriteLine("=== PicoMsgPack Sample ===\n");

// ═══ 1. Zero-Attribute Models (all auto-numbered by declaration order) ═══
Console.WriteLine("─── 1. Zero-Attribute ───");
var user = new User
{
    Name = "Alice",
    Age = 30,
    Active = true,
};
var bytes = MsgPackSerializer.SerializeToUtf8Bytes(user);
var result = MsgPackSerializer.Deserialize<User>(bytes);
var jsonEquivalent = Encoding.UTF8.GetByteCount("{\"Name\":\"Alice\",\"Age\":30,\"Active\":true}");

Console.WriteLine($"  Round-trip: {result!.Name}, {result.Age}, {result.Active}");
Console.WriteLine($"  MsgPack: {bytes.Length} bytes");
Console.WriteLine($"  JSON:    ~{jsonEquivalent} bytes");
Console.WriteLine(
    $"  Saving:  ~{jsonEquivalent - bytes.Length} bytes (~{(double)(jsonEquivalent - bytes.Length) / jsonEquivalent * 100:F0}%)"
);

// ═══ 2. [MsgPackKey] explicit keys ═══
Console.WriteLine("\n─── 2. [MsgPackKey] ───");
var product = new ProductEx
{
    Title = "Widget",
    Price = 9.99,
    Tags = ["sale", "new"],
};
var pBytes = MsgPackSerializer.SerializeToUtf8Bytes(product);
var pResult = MsgPackSerializer.Deserialize<ProductEx>(pBytes);
Console.WriteLine(
    $"  Round-trip: {pResult!.Title}, ${pResult.Price}, [{string.Join(", ", pResult.Tags)}]"
);
Console.WriteLine($"  Size: {pBytes.Length} bytes");

// ═══ 3. Nested model ═══
Console.WriteLine("\n─── 3. Nested Model ───");
var order = new Order
{
    Id = 42,
    Customer = new Customer { Name = "Bob" },
};
var oBytes = MsgPackSerializer.SerializeToUtf8Bytes(order);
var oResult = MsgPackSerializer.Deserialize<Order>(oBytes);
Console.WriteLine($"  Round-trip: #{oResult!.Id}, {oResult.Customer!.Name}");
Console.WriteLine($"  Size: {oBytes.Length} bytes");

// ═══ 4. Low-level reader ═══
Console.WriteLine("\n─── 4. Raw Reader Tokens (User model) ───");
var reader = new MsgPackReader(bytes);
while (reader.Read())
{
    Console.Write($"  {reader.TokenType, -14}");
    if (reader.TokenType is TokenType.String or TokenType.PropertyName)
        Console.Write($" = {Encoding.UTF8.GetString(reader.GetStringRaw())}");
    else if (reader.TokenType == TokenType.Int32)
    {
        reader.TryGetInt32(out var v);
        Console.Write($" = {v}");
    }
    else if (reader.TokenType == TokenType.Bool)
    {
        reader.TryGetBool(out var b);
        Console.Write($" = {b}");
    }
    Console.WriteLine();
}

// ═══ 5. Manual writer ═══
Console.WriteLine("\n─── 5. Manual Writer ───");
var buf = new ArrayBufferWriter<byte>(64);
var w = new MsgPackWriter(buf);
w.WriteStartObject(2);
w.WriteInt32(0);
w.WriteString("hello"u8);
w.WriteInt32(1);
w.WriteInt32(42);
w.WriteEndObject();
Console.WriteLine($"  Written: {buf.WrittenSpan.Length} bytes");

// ═══ 6. [MsgPackIgnore] ═══
Console.WriteLine("\n─── 6. [MsgPackIgnore] ───");
var ignored = MsgPackSerializer.SerializeToUtf8Bytes(
    new WithIgnore { Name = "X", Secret = "s3cret" }
);
var igResult = MsgPackSerializer.Deserialize<WithIgnore>(ignored);
Console.WriteLine($"  Size: {ignored.Length} bytes (Secret excluded)");
Console.WriteLine($"  Secret after deserialize: '{igResult?.Secret}' (default)");

// ═══ 7. [MsgPackConverter] ═══
Console.WriteLine("\n─── 7. [MsgPackConverter] ───");
var enc = MsgPackSerializer.SerializeToUtf8Bytes(new Encoded { Tag = "abc" });
var encBack = MsgPackSerializer.Deserialize<Encoded>(enc);
Console.WriteLine($"  Round-trip: tag='{encBack?.Tag}'");

// ═══ 8. Extension Type (Reader) ═══
Console.WriteLine("\n─── 8. Extension Type ───");

// Write an extension manually: tag=42, data=[0x01,0x02,0x03]
var extBuf = new ArrayBufferWriter<byte>(64);
var ew = new MsgPackWriter(extBuf);
ew.WriteStartObject(1);
ew.WriteInt32(0);
ew.WriteExtension(42, new byte[] { 1, 2, 3 });
ew.WriteEndObject();
var er = new MsgPackReader(extBuf.WrittenSpan);
while (er.Read())
{
    if (er.TokenType == TokenType.Extension)
    {
        er.TryGetExtension(out var tag, out var extData);
        Console.WriteLine($"  Extension tag={tag}, data=[{string.Join(", ", extData.ToArray())}]");
    }
}

// ═══ 9. File I/O ═══
Console.WriteLine("\n─── 9. File I/O ───");
var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDir);
var dataFile = Path.Combine(dataDir, "user.msgpack");
File.WriteAllBytes(dataFile, bytes);
var fileBytes = File.ReadAllBytes(dataFile);
var fromFile = MsgPackSerializer.Deserialize<User>(fileBytes);
Console.WriteLine($"  Wrote → read: {fromFile!.Name}, {fromFile.Age}");
Console.WriteLine($"  Size on disk: {fileBytes.Length} bytes");

Console.WriteLine("\nDone.");

// ═══ Models ═══

public class User // keys auto-assigned: Name=0, Age=1, Active=2
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
    public bool Active { get; set; }
}

public class ProductEx
{
    [MsgPackKey(0)]
    public string Title { get; set; } = "";

    [MsgPackKey(1)]
    public double Price { get; set; }

    [MsgPackKey(2)]
    public List<string> Tags { get; set; } = new();
}

public class Order // auto-numbered: Id=0, Customer=1
{
    public int Id { get; set; }
    public Customer? Customer { get; set; }
}

public class Customer // auto-numbered: Name=0
{
    public string Name { get; set; } = "";
}

public class WithIgnore
{
    [MsgPackKey(0)]
    public string Name { get; set; } = "";

    [MsgPackIgnore]
    public string Secret { get; set; } = "";
}

public class Encoded
{
    [MsgPackKey(0)]
    [MsgPackConverter(typeof(TagEncoder))]
    public string Tag { get; set; } = "";
}

public class TagEncoder : IMsgPackConverter<string>
{
    public void Write(IBufferWriter<byte> writer, string value)
    {
        writer.Write(Encoding.UTF8.GetBytes($"[{value}]"));
    }

    public string Read(ref MsgPackReader reader)
    {
        return Encoding.UTF8.GetString(reader.GetStringRaw()).Trim('[', ']');
    }
}
