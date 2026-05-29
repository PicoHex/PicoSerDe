using System.Text.Json;

Console.WriteLine("=== PicoMsgPack Sample ===\n");

// ═══ 1. Zero-Attribute Model (auto-numbered by declaration order) ═══
Console.WriteLine("─── 1. Zero-Attribute Model ───");
var user = new User { Name = "Alice", Age = 30, Active = true };
var bytes = MsgPackSerializer.SerializeToUtf8Bytes(user);
var result = MsgPackSerializer.Deserialize<User>(bytes);
var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(user);

Console.WriteLine($"  Round-trip: {result!.Name}, {result.Age}, {result.Active}");
Console.WriteLine($"  MsgPack: {bytes.Length} bytes");
Console.WriteLine($"  JSON:    {jsonBytes.Length} bytes");
Console.WriteLine($"  Saving:  {jsonBytes.Length - bytes.Length} bytes ({(double)(jsonBytes.Length - bytes.Length) / jsonBytes.Length * 100:F0}%)");

// ═══ 2. Model with explicit keys ═══
Console.WriteLine("\n─── 2. Explicit Keys ───");
var product = new Product { Title = "Widget", Price = 9.99, Tags = ["sale", "new"] };
var pBytes = MsgPackSerializer.SerializeToUtf8Bytes(product);
var pResult = MsgPackSerializer.Deserialize<Product>(pBytes);
Console.WriteLine($"  Round-trip: {pResult!.Title}, ${pResult.Price}, [{string.Join(", ", pResult.Tags)}]");
Console.WriteLine($"  Size: {pBytes.Length} bytes");

// ═══ 3. Nested model ═══
Console.WriteLine("\n─── 3. Nested Model ───");
var order = new Order
{
    Id = 42,
    Customer = new Customer { Name = "Bob" }
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
    Console.Write($"  {reader.TokenType,-14}");
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
w.WriteInt32(0); w.WriteString("hello"u8);
w.WriteInt32(1); w.WriteInt32(42);
w.WriteEndObject();
Console.WriteLine($"  Written: {buf.WrittenSpan.Length} bytes");

Console.WriteLine("\nDone.");

// ═══ Models (zero-attribute by default) ═══

public class User       // keys auto-assigned: Name=0, Age=1, Active=2
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
    public bool Active { get; set; }
}

public class Product    // explicit keys for stable wire format
{
    [MsgPackKey(0)] public string Title { get; set; } = "";
    [MsgPackKey(1)] public double Price { get; set; }
    [MsgPackKey(2)] public List<string> Tags { get; set; } = new();
}

public class Order
{
    [MsgPackKey(0)] public int Id { get; set; }
    [MsgPackKey(1)] public Customer? Customer { get; set; }
}

public class Customer
{
    [MsgPackKey(0)] public string Name { get; set; } = "";
}
