// Shared benchmark model types — used by all format benchmark projects

namespace PicoBench;

public class SimplePoco
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
}

public class ComplexPoco
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public decimal Price { get; set; }
    public DateTime CreatedAt { get; set; }
    public DayOfWeek Day { get; set; }
    public double Rating { get; set; }
    public bool IsActive { get; set; }
}

public class NestedPoco
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public NestedAddress? Address { get; set; }
    public List<string> Tags { get; set; } = new();
}

public class NestedAddress
{
    public string Street { get; set; } = "";
    public string City { get; set; } = "";
    public string? Zip { get; set; }
}

public class CollectionPoco
{
    public List<int> Scores { get; set; } = new();
    public Dictionary<string, string> Metadata { get; set; } = new();
}
