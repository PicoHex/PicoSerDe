// TDD: Reproduce the YAML reader infinite loop with default-value model.
namespace PicoYaml.Tests;

/// <summary>Model matching the integration test's AllFormatsModel.</summary>
[PicoSerializable]
public class ExactModel
{
    public bool Bool { get; set; }
    public int Int { get; set; }
    public long Long { get; set; }
    public float Float { get; set; }
    public double Double { get; set; }
    public decimal Decimal { get; set; }
    public string String { get; set; } = "";
    public string? NullableString { get; set; }
    public int? NullableInt { get; set; }
    public DateTime DateTime { get; set; }
    public TimeSpan TimeSpan { get; set; }
    public DateOnly DateOnly { get; set; }
    public TimeOnly TimeOnly { get; set; }
    public Guid Guid { get; set; }
    public DayOfWeek Enum { get; set; }
    public int Count { get; set; }
    public List<string> StringList { get; set; } = [];
    public ExactSub? Nested { get; set; }
}

/// <summary>Nested model.</summary>
[PicoSerializable]
public class ExactSub
{
    public string Name { get; set; } = "";
    public int Value { get; set; }
}

public class YamlReaderLoopTests
{
    /// <summary>
    /// RED: YAML serialized from an ExactModel with null/default values
    /// causes infinite loop in the generated deserializer.
    /// </summary>
    [Test]
    public async Task GeneratedDeserializer_DefaultValues_DoesNotLoop()
    {
        var model = new ExactModel
        {
            Bool = false,
            Int = 0,
            Long = 0,
            Float = 0,
            Double = 0,
            Decimal = 0,
            String = "empty",
            NullableString = null,
            NullableInt = null,
            DateTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            TimeSpan = TimeSpan.FromHours(1),
            DateOnly = DateOnly.MinValue,
            TimeOnly = TimeOnly.MinValue,
            Guid = Guid.Empty,
            Enum = DayOfWeek.Sunday,
            Count = 0,
            StringList = [],
            Nested = null,
        };

        var yBytes = YamlSerializer.SerializeToUtf8Bytes(model);
        var yamlText = Encoding.UTF8.GetString(yBytes);

        // Step 1: raw reader
        var rawReader = new YamlReader(yBytes);
        int rawReads = 0;
        while (rawReader.Read())
        {
            rawReads++;
            if (rawReads > 500)
                break;
        }
        await Assert.That(rawReads).IsLessThan(100, "Raw reader should not loop");

        // Step 2: full deserialization via SG-generated code
        var back = YamlSerializer.Deserialize<ExactModel>(yBytes);
        await Assert.That(back.NullableString).IsNull();
        await Assert.That(back.NullableInt).IsNull();
        await Assert.That(back.Nested).IsNull();
    }
}
