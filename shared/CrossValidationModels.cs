// Shared complex model for cross-validation tests across all PicoSerDe formats.

namespace PicoCrossValidation;

public class ComplexModel
{
    // ── Primitives (all SGs support bool/int/long/double/decimal) ──
    public bool Bool { get; set; }
    public int Int { get; set; }
    public long Long { get; set; }
    public float Float { get; set; }
    public double Double { get; set; }
    public decimal Decimal { get; set; }

    // ── String ──
    public string String { get; set; } = "";
    public string? NullableString { get; set; }

    // ── Date/Time (all SGs support DateTime/TimeSpan/DateOnly/TimeOnly/Guid) ──
    public DateTime DateTime { get; set; }
    public TimeSpan TimeSpan { get; set; }
    public DateOnly DateOnly { get; set; }
    public TimeOnly TimeOnly { get; set; }
    public Guid Guid { get; set; }

    // ── Enum (SGs handle via ToString/Enum.Parse) ──
    public DayOfWeek Enum { get; set; }

    // ── Nullable (SGs handle Nullable<T> unwrapping) ──
    public int? NullableInt { get; set; }

    // ── Collections (all SGs support List<T>/T[]/Dictionary<string,string>) ──
    public List<int> IntList { get; set; } = [];
    public List<string> StringList { get; set; } = [];
    public int[] IntArray { get; set; } = [];
    public Dictionary<string, string> StringDict { get; set; } = [];

    // ── Nested object (all SGs support nested POCOs) ──
    public SubModel? Nested { get; set; }
    public List<SubModel>? NestedList { get; set; }
}

public class SubModel
{
    public string Name { get; set; } = "";
    public int Value { get; set; }
}

/// <summary>Factory providing a rich instance with values for every property.</summary>
public static class ComplexModelFactory
{
    public static ComplexModel Create() => new()
    {
        Bool = true,
        Int = 42,
        Long = 9_876_543_210L,
        Double = 2.718281828459045,
        Float = 3.14f,
        Decimal = 123456.789m,
        String = "Hello, PicoSerDe! 特殊字符 ñ 测试",
        NullableString = null,
        DateTime = new DateTime(2026, 6, 4, 12, 30, 0, DateTimeKind.Utc),
        TimeSpan = new TimeSpan(10, 30, 0),
        DateOnly = new DateOnly(2026, 6, 4),
        TimeOnly = new TimeOnly(15, 45, 30, 123),
        Guid = Guid.Parse("A1B2C3D4-E5F6-7890-ABCD-EF1234567890"),
        Enum = DayOfWeek.Wednesday,
        NullableInt = null,
        IntList = [10, 20, 30],
        StringList = ["foo", "bar", "baz"],
        IntArray = [100, 200],
        StringDict = new() { ["key1"] = "val1", ["key2"] = "val2" },
        Nested = new SubModel { Name = "nested", Value = 99 },
        NestedList = [new() { Name = "a", Value = 1 }, new() { Name = "b", Value = 2 }],
    };
}
