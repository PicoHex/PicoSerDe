using System.Buffers;
using System.Globalization;
using System.Text;
using PicoJson;

// ═══════════════════════════════════════════════
// PicoJson Sample — complex nested model demo
// ═══════════════════════════════════════════════

// ── Models ──

public enum OrderStatus
{
    Pending,
    Processing,
    Shipped,
    Delivered,
    Cancelled
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
    public DateTime CreatedAt { get; set; }
    public DateOnly? FulfilledDate { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
    public Customer? Customer { get; set; }
    public List<OrderLine> Lines { get; set; } = new();
}

// ── Main ──

class Program
{
    static void Main()
    {
        var order = new Order
        {
            Id = Guid.NewGuid(),
            Status = OrderStatus.Processing,
            Total = 149.97m,
            Discount = 10.0,
            CreatedAt = DateTime.UtcNow,
            FulfilledDate = null,
            Metadata = new() { ["source"] = "web", ["channel"] = "mobile" },
            Customer = new Customer
            {
                Name = "Alice",
                Since = new DateOnly(2024, 1, 15),
                Preferences =  ["email", "sms"],
                Address = new Address
                {
                    Street = "123 Main St",
                    City = "San Francisco",
                    Zip = "94105",
                    Country = "US"
                }
            },
            Lines = new List<OrderLine>
            {
                new()
                {
                    Product = "Widget",
                    Quantity = 2,
                    UnitPrice = 49.99m,
                    PickedAt = null
                },
                new()
                {
                    Product = "Gadget",
                    Quantity = 1,
                    UnitPrice = 49.99m,
                    PickedAt = new TimeOnly(14, 30)
                }
            }
        };

        // ── Serialize (one line, Source Generator at compile time) ──
        var bytes = JsonSerializer.SerializeToUtf8Bytes(order);
        Console.WriteLine("=== Serialized ===");
        Console.WriteLine(PrettyPrint(Encoding.UTF8.GetString(bytes)));

        // ── Deserialize (one line) ──
        var restored = JsonSerializer.Deserialize<Order>(bytes);
        Console.WriteLine("\n=== Deserialized ===");
        Console.WriteLine($"  Id:              {restored?.Id}");
        Console.WriteLine($"  Status:          {restored?.Status}");
        Console.WriteLine($"  Total:           {restored?.Total}");
        Console.WriteLine($"  Discount:        {restored?.Discount}");
        Console.WriteLine($"  CreatedAt:       {restored?.CreatedAt:O}");
        Console.WriteLine($"  FulfilledDate:   {restored?.FulfilledDate?.ToString() ?? "null"}");
        Console.WriteLine(
            $"  Metadata:        [{string.Join(", ", restored?.Metadata?.Select(kv => $"{kv.Key}={kv.Value}") ?? new[] { "" })}]"
        );
        Console.WriteLine($"  Customer.Name:   {restored?.Customer?.Name}");
        Console.WriteLine($"  Customer.Since:  {restored?.Customer?.Since}");
        Console.WriteLine(
            $"  Customer.Prefs:  [{string.Join(", ", restored?.Customer?.Preferences ?? new())}]"
        );
        Console.WriteLine(
            $"  Customer.Addr:   {restored?.Customer?.Address?.Street}, {restored?.Customer?.Address?.City}"
        );
        foreach (var line in restored?.Lines ?? new())
        {
            Console.WriteLine(
                $"  Line:            {line.Product} x{line.Quantity} @ {line.UnitPrice} [{line.PickedAt}]"
            );
        }

        // ── Raw JsonWriter ──
        Console.WriteLine("\n=== Raw Writer ===");
        var buf = new ArrayBufferWriter<byte>(128);
        var jw = new JsonWriter(buf);
        jw.WriteStartObject();
        jw.WritePropertyName("framework"u8);
        jw.WriteString("PicoJson"u8);
        jw.WritePropertyName("message"u8);
        jw.WriteString("Source Generator + AOT + zero reflection"u8);
        jw.WriteEndObject();
        Console.WriteLine(Encoding.UTF8.GetString(buf.WrittenSpan));
    }

    // Quick indented formatter for readability (not part of PicoJson)
    static string PrettyPrint(string compact)
    {
        var sb = new StringBuilder();
        var indent = 0;
        foreach (var c in compact)
        {
            if (c is '{' or '[')
            {
                sb.Append(c);
                sb.Append('\n');
                indent++;
                sb.Append(' ', indent * 2);
            }
            else if (c is '}' or ']')
            {
                sb.Append('\n');
                indent--;
                sb.Append(' ', indent * 2);
                sb.Append(c);
            }
            else if (c == ',')
            {
                sb.Append(c);
                sb.Append('\n');
                sb.Append(' ', indent * 2);
            }
            else
                sb.Append(c);
        }
        return sb.ToString();
    }
}
