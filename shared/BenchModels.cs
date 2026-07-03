// Shared benchmark model types — used by all format benchmark projects

namespace PicoBench;

public class SimplePoco
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
}

public class ComplexPoco
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public DateTime CreatedAt { get; set; }
    public DayOfWeek Day { get; set; }
    public double Rating { get; set; }
    public bool IsActive { get; set; }
}

public class NestedPoco
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public NestedAddress? Address { get; set; }
    public List<string> Tags { get; set; } = new();
}

public class NestedAddress
{
    public string Street { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string? Zip { get; set; }
}

public class CollectionPoco
{
    public List<int> Scores { get; set; } = new();
    public Dictionary<string, string> Metadata { get; set; } = new();
}

/// <summary>Large model with 50 string properties — exercises whitespace-heavy parsing.</summary>
public class LargeFlatPoco
{
    public string F01 { get; set; } = string.Empty;
    public string F02 { get; set; } = string.Empty;
    public string F03 { get; set; } = string.Empty;
    public string F04 { get; set; } = string.Empty;
    public string F05 { get; set; } = string.Empty;
    public string F06 { get; set; } = string.Empty;
    public string F07 { get; set; } = string.Empty;
    public string F08 { get; set; } = string.Empty;
    public string F09 { get; set; } = string.Empty;
    public string F10 { get; set; } = string.Empty;
    public int N01 { get; set; }
    public int N02 { get; set; }
    public int N03 { get; set; }
    public int N04 { get; set; }
    public int N05 { get; set; }
    public int N06 { get; set; }
    public int N07 { get; set; }
    public int N08 { get; set; }
    public int N09 { get; set; }
    public int N10 { get; set; }
    public bool B01 { get; set; }
    public bool B02 { get; set; }
    public bool B03 { get; set; }
    public bool B04 { get; set; }
    public bool B05 { get; set; }
    public double D01 { get; set; }
    public double D02 { get; set; }
    public double D03 { get; set; }
    public double D04 { get; set; }
    public double D05 { get; set; }
}

/// <summary>Large string content — exercises ContainsBackslash SIMD via escaped strings.</summary>
public class LargeStringPoco
{
    public string Body { get; set; } = string.Empty;
}

/// <summary>Medium model with 6 mixed scalar types — exercises key dispatch optimization.</summary>
public class MediumScalarPoco
{
    public string Alpha { get; set; } = string.Empty;
    public int Beta { get; set; }
    public bool Gamma { get; set; }
    public long Delta { get; set; }
    public double Epsilon { get; set; }
    public string Zeta { get; set; } = string.Empty;
}
