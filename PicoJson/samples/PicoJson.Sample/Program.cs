using System.Buffers;
using System.Text;
using PicoJson;

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
            Metadata = new() { ["source"] = "web", ["channel"] = "mobile" },
            Customer = new Customer
            {
                Name = "Alice",
                Since = new DateOnly(2024, 1, 15),
                Preferences =  ["email", "sms"],
                Address = new Address
                {
                    Street = "123 Main St",
                    City = "SF",
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
                    UnitPrice = 49.99m
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

        var bytes = JsonSerializer.SerializeToUtf8Bytes(order);
        Console.WriteLine("=== Serialized Order ===");
        Console.WriteLine(Encoding.UTF8.GetString(bytes));

        var restored = JsonSerializer.Deserialize<Order>(bytes);
        Console.WriteLine("\n=== Deserialized Order ===");
        Console.WriteLine($"  Id:        {restored?.Id}");
        Console.WriteLine($"  Status:    {restored?.Status}");
        Console.WriteLine($"  Total:     {restored?.Total}");
        Console.WriteLine($"  Discount:  {restored?.Discount}");
        Console.WriteLine(
            $"  Metadata:  [{string.Join(",", restored?.Metadata?.Select(kv => $"{kv.Key}={kv.Value}") ?? new[] { "" })}]"
        );
        Console.WriteLine(
            $"  Customer:  {restored?.Customer?.Name} (since {restored?.Customer?.Since})"
        );
        Console.WriteLine(
            $"  Address:   {restored?.Customer?.Address?.Street}, {restored?.Customer?.Address?.City}"
        );
        foreach (var line in restored?.Lines ?? new())
            Console.WriteLine(
                $"  Line:      {line.Product} x{line.Quantity} @ {line.UnitPrice} [{line.PickedAt}]"
            );
    }
}
