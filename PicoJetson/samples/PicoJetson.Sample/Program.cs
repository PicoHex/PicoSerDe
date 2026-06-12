class Program
{
    static void Main()
    {
        // ═══ 1. Complex Model (all types, nested, collections) ═══
        var order = new Order
        {
            Id = Guid.NewGuid(),
            Status = OrderStatus.Processing,
            Total = 149.97m,
            Discount = 10.0,
            CreatedAt = DateTime.UtcNow,
            Metadata = new() { ["source"] = "web", ["channel"] = "mobile" },
            InternalNote = "this should NOT appear in JSON",
            Customer = new Customer
            {
                Name = "Alice",
                Since = new DateOnly(2024, 1, 15),
                Preferences = ["email", "sms"],
                Address = new Address
                {
                    Street = "123 Main St",
                    City = "SF",
                    Zip = "94105",
                    Country = "US",
                },
            },
            Lines = new List<OrderLine>
            {
                new()
                {
                    Product = "He said \"hello\"",
                    Quantity = 2,
                    UnitPrice = 49.99m,
                },
                new()
                {
                    Product = "Path: C:\\data",
                    Quantity = 1,
                    UnitPrice = 49.99m,
                    PickedAt = new TimeOnly(14, 30),
                },
            },
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(order);
        Console.WriteLine("=== 1. Complex Model ===");
        Console.WriteLine(Encoding.UTF8.GetString(bytes));
        Console.WriteLine(
            $"  (InternalNote [JsonIgnore] excluded: {!Encoding.UTF8.GetString(bytes).Contains("InternalNote")})"
        );
        Console.WriteLine(
            $"  (CreatedAt uses custom format: {Encoding.UTF8.GetString(bytes).Contains("\"CreatedAt\":\"202")})"
        );

        var restored = JsonSerializer.Deserialize<Order>(bytes);
        Console.WriteLine($"  Round-trip OK: {restored?.Id == order.Id}");

        // ═══ 2. Case-Insensitive Deserialization ═══
        Console.WriteLine("\n=== 2. Case-Insensitive ===");
        var camelJson = "{\"name\":\"Bob\",\"age\":25}"u8;
        var person = JsonSerializer.Deserialize<Person>(camelJson);
        Console.WriteLine($"  \"name\"→Name: {person?.Name}");

        // ═══ 3. String Escaping ═══
        Console.WriteLine("\n=== 3. String Escaping ===");
        var productName = order.Lines[0].Product;
        Console.WriteLine($"  Original:  {productName}");
        Console.WriteLine($"  Round-trip: {restored?.Lines.FirstOrDefault()?.Product}");

        // ═══ 4. NaN / Infinity Handling ═══
        Console.WriteLine("\n=== 4. NaN / Infinity → throws ===");
        Console.Write("  NaN: ");
        try
        {
            var nanBuf = new ArrayBufferWriter<byte>(64);
            var nanWriter = new JsonWriter(nanBuf);
            nanWriter.WriteNumber(double.NaN);
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine(ex.Message.Split('.')[0]);
        }
        Console.Write("  Infinity: ");
        try
        {
            var infBuf = new ArrayBufferWriter<byte>(64);
            var infWriter = new JsonWriter(infBuf);
            infWriter.WriteNumber(double.PositiveInfinity);
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine(ex.Message.Split('.')[0]);
        }

        // ═══ 5. Error Messages with Line/Column ═══
        Console.WriteLine("\n=== 5. Error Messages ===");
        try
        {
            var r = new JsonReader("{\n  \"a\": broken\n}"u8);
            while (r.Read()) { }
        }
        catch (FormatException ex)
        {
            Console.WriteLine($"  {ex.Message}");
        }

        // ═══ 6. File I/O — read from data file ═══
        Console.WriteLine("\n=== 6. File I/O ===");
        var dataFile = Path.Combine(AppContext.BaseDirectory, "data", "order.json");
        var jsonBytes = File.ReadAllBytes(dataFile);
        var fromFile = JsonSerializer.Deserialize<Order>(jsonBytes);
        Console.WriteLine($"  Read {dataFile} ({jsonBytes.Length} bytes)");
        Console.WriteLine($"  Order {fromFile?.Id}");
        Console.WriteLine($"  Customer: {fromFile?.Customer?.Name}");
        Console.WriteLine($"  Lines: {fromFile?.Lines?.Count}");

        // Round-trip: serialize back and compare
        var roundBytes = JsonSerializer.SerializeToUtf8Bytes(fromFile!);
        var reRead = JsonSerializer.Deserialize<Order>(roundBytes);
        Console.WriteLine($"  Round-trip OK: {reRead?.Customer?.Name == fromFile?.Customer?.Name}");

        // ═══ 7. JsonOptions ═══
        Console.WriteLine("\n=== 7. JsonOptions ===");
        var optModel = new Person { Name = "Alice", Age = 30 };

        Console.Write("  Indented: ");
        Console.WriteLine(
            Encoding.UTF8.GetString(
                JsonSerializer.SerializeToUtf8Bytes(optModel, new JsonOptions { Indented = true })
            )
        );

        Console.Write("  CamelCase: ");
        var cc = JsonSerializer.SerializeToUtf8Bytes(
            new FullName { FirstName = "Bob", LastName = "Smith" },
            new JsonOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
        );
        Console.WriteLine(Encoding.UTF8.GetString(cc));

        Console.Write("  SnakeCase: ");
        var sc = JsonSerializer.SerializeToUtf8Bytes(
            new FullName { FirstName = "Bob", LastName = "Smith" },
            new JsonOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }
        );
        Console.WriteLine(Encoding.UTF8.GetString(sc));

        Console.Write("  SkipNull: ");
        var sn = JsonSerializer.SerializeToUtf8Bytes(
            new HasNull { Name = "X", Desc = null },
            new JsonOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }
        );
        Console.WriteLine(Encoding.UTF8.GetString(sn));

        Console.Write("  TrailingComma: ");
        var tc = JsonSerializer.Deserialize<Person>(
            "{\"Name\":\"X\",\"Age\":1,}"u8,
            new JsonOptions { AllowTrailingCommas = true }
        );
        Console.WriteLine($"OK: {tc?.Name}");

        Console.Write("  UnknownProp→throw: ");
        try
        {
            JsonSerializer.Deserialize<Person>(
                "{\"Name\":\"X\",\"extra\":1}"u8,
                new JsonOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }
            );
        }
        catch (FormatException ex)
        {
            Console.WriteLine($"Caught: {ex.Message.Split('.')[0]}");
        }

        // ═══ 8. JsonPropertyName ═══
        Console.WriteLine("\n=== 8. [JsonPropertyName] ===");
        var pn = JsonSerializer.SerializeToUtf8Bytes(new Renamed { UserId = 42, Display = "Ada" });
        Console.WriteLine($"  {Encoding.UTF8.GetString(pn)}");

        // ═══ 9. [JsonCamelCase] class-level ═══
        Console.WriteLine("\n=== 9. [JsonCamelCase] ===");
        var cc2 = JsonSerializer.SerializeToUtf8Bytes(
            new CamelClass { ProductName = "Widget", StockCount = 5 }
        );
        Console.WriteLine($"  {Encoding.UTF8.GetString(cc2)}");

        // ═══ 10. [JsonConstructor] + [DateTimeFormat] ═══
        Console.WriteLine("\n=== 10. [JsonConstructor] + [DateTimeFormat] ===");
        var imm = JsonSerializer.SerializeToUtf8Bytes(
            new Immutable("Eve", 99, new DateTime(2024, 1, 1))
        );
        var immJson = Encoding.UTF8.GetString(imm);
        Console.WriteLine($"  {immJson}");
        var immBack = JsonSerializer.Deserialize<Immutable>(imm);
        Console.WriteLine(
            $"  Deserialized: {immBack?.UserName}, age={immBack?.Level}, date={immBack?.Since:yyyy-MM-dd}"
        );
    }
}

// ═══ Models ═══

public class Person
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
}

public enum OrderStatus
{
    Pending,
    Processing,
    Shipped,
    Delivered,
    Cancelled,
}

public class Address
{
    public string Street { get; set; } = "";
    public string City { get; set; } = "";
    public string? Zip { get; set; }
    public string Country { get; set; } = "";
}

public class Customer
{
    public string Name { get; set; } = "";
    public DateOnly Since { get; set; }
    public List<string> Preferences { get; set; } = new();
    public Address? Address { get; set; }
}

public class OrderLine
{
    public string Product { get; set; } = "";
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public TimeOnly? PickedAt { get; set; }
}

public class Order
{
    public Guid Id { get; set; }
    public OrderStatus Status { get; set; }
    public decimal Total { get; set; }
    public double? Discount { get; set; }

    [JsonConverter(typeof(ShortDateConverter))]
    public DateTime CreatedAt { get; set; }

    public DateOnly? FulfilledDate { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
    public Customer? Customer { get; set; }
    public List<OrderLine> Lines { get; set; } = new();

    [JsonIgnore]
    public string InternalNote { get; set; } = "";
}

public class ShortDateConverter : IJsonConverter<DateTime>
{
    public void Write(IBufferWriter<byte> writer, DateTime value)
    {
        var jw = new JsonWriter(writer);
        jw.WriteString(Encoding.UTF8.GetBytes(value.ToString("yyyy-MM-dd")));
    }

    public DateTime Read(ref JsonReader reader)
    {
        DateTime.TryParse(Encoding.UTF8.GetString(reader.GetStringRaw()), null, out var dt);
        return dt;
    }
}

// ═══ Models for Options demos ═══

public class FullName
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
}

public class HasNull
{
    public string Name { get; set; } = "";
    public string? Desc { get; set; }
}

// ═══ Models for Attribute demos ═══

public class Renamed
{
    [JsonPropertyName("id")]
    public int UserId { get; set; }

    [JsonPropertyName("name")]
    public string Display { get; set; } = "";
}

[JsonCamelCase]
public class CamelClass
{
    public string ProductName { get; set; } = "";
    public int StockCount { get; set; }
}

public class Immutable
{
    public string UserName { get; }
    public int Level { get; }

    [DateTimeFormat("yyyy-MM-dd")]
    public DateTime Since { get; }

    [JsonConstructor]
    public Immutable(string userName, int level, DateTime since)
    {
        UserName = userName;
        Level = level;
        Since = since;
    }
}
