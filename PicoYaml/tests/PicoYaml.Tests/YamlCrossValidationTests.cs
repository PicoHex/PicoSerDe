namespace PicoYaml.Tests;

public class YamlSub
{
    public string Name { get; set; } = string.Empty;
    public int Value { get; set; }
}

public class DateOnlyYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(DateOnly);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var scalar = parser.Consume<Scalar>();
        return DateOnly.ParseExact(scalar.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public void WriteYaml(
        IEmitter emitter,
        object? value,
        Type type,
        ObjectSerializer rootSerializer
    )
    {
        var dt = (DateOnly)value!;
        emitter.Emit(new Scalar(dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
    }
}

public class TimeOnlyYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(TimeOnly);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var scalar = parser.Consume<Scalar>();
        return TimeOnly.ParseExact(scalar.Value, "HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
    }

    public void WriteYaml(
        IEmitter emitter,
        object? value,
        Type type,
        ObjectSerializer rootSerializer
    )
    {
        var tt = (TimeOnly)value!;
        emitter.Emit(new Scalar(tt.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture)));
    }
}

public class YamlModel
{
    public bool Bool { get; set; }
    public int Int { get; set; }
    public long Long { get; set; }
    public float Float { get; set; }
    public double Double { get; set; }
    public decimal Decimal { get; set; }
    public string String { get; set; } = string.Empty;
    public string? NullableString { get; set; }
    public DateTime DateTime { get; set; }
    public TimeSpan TimeSpan { get; set; }
    public DateOnly DateOnly { get; set; }
    public TimeOnly TimeOnly { get; set; }
    public Guid Guid { get; set; }
    public DayOfWeek Enum { get; set; }
    public int? NullableInt { get; set; }
    public List<int> IntList { get; set; } = [];
    public List<string> StringList { get; set; } = [];
    public int[] IntArray { get; set; } = [];
    public Dictionary<string, string> StringDict { get; set; } = [];
    public YamlSub? Nested { get; set; }
    public List<YamlSub>? NestedList { get; set; }
}

public class YamlCrossValidationTests
{
    private static readonly IDeserializer YamlDotNet = new DeserializerBuilder()
        .WithTypeConverter(new DateOnlyYamlConverter())
        .WithTypeConverter(new TimeOnlyYamlConverter())
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer YamlDotNetSer = new SerializerBuilder()
        .WithTypeConverter(new DateOnlyYamlConverter())
        .WithTypeConverter(new TimeOnlyYamlConverter())
        .Build();

    private static YamlModel Model() => MakeModel(includeNestedList: false);

    private static YamlModel MakeModel(bool includeNestedList = true)
    {
        var m = new YamlModel
        {
            Bool = true,
            Int = 42,
            Long = 9_876_543_210L,
            Float = 3.14f,
            Double = 2.71828,
            Decimal = 123.45m,
            String = "Hello YAML!",
            NullableString = "not null",
            DateTime = new DateTime(2026, 6, 4, 12, 30, 0, DateTimeKind.Utc),
            TimeSpan = new TimeSpan(10, 30, 0),
            DateOnly = new DateOnly(2026, 6, 4),
            TimeOnly = new TimeOnly(15, 45, 30),
            Guid = Guid.Parse("A1B2C3D4-E5F6-7890-ABCD-EF1234567890"),
            Enum = DayOfWeek.Monday,
            NullableInt = 77,
            IntList = [10, 20, 30],
            StringList = ["foo", "bar"],
            IntArray = [100, 200],
            StringDict = new() { ["k1"] = "v1" },
            Nested = new() { Name = "sub", Value = 99 },
            NestedList = [new() { Name = "a", Value = 1 }, new() { Name = "b", Value = 2 }],
        };
        if (!includeNestedList)
            m.NestedList = null;
        return m;
    }

    [Test]
    public async Task Sg_Trigger()
    {
        var bytes = YamlSerializer.SerializeToUtf8Bytes(Model());
        var back = YamlSerializer.Deserialize<YamlModel>(bytes);
        await Assert.That(back).IsNotNull();
    }

    [Test]
    public async Task PicoRoundTrip()
    {
        var m = MakeModel(includeNestedList: true);
        var bytes = YamlSerializer.SerializeToUtf8Bytes(m);
        var back = YamlSerializer.Deserialize<YamlModel>(bytes);
        await AssertYamlEqual(m, back!);
    }

    [Test]
    public async Task PicoSerialize_YamlDotNetDeserialize()
    {
        var picoBytes = YamlSerializer.SerializeToUtf8Bytes(Model());
        var yamlText = Encoding.UTF8.GetString(picoBytes);
        var yaml = YamlDotNet.Deserialize<YamlModel>(yamlText);
        await AssertYamlEqual(Model(), yaml!);
    }

    [Test]
    public async Task YamlDotNetSerialize_PicoDeserialize()
    {
        var modelNoNl = MakeModel(false);
        var yamlText = YamlDotNetSer.Serialize(modelNoNl);
        var bytes = Encoding.UTF8.GetBytes(yamlText);
        var pico = YamlSerializer.Deserialize<YamlModel>(bytes);
        await AssertYamlEqual(modelNoNl, pico!);
    }

    private static async Task AssertYamlEqual(YamlModel expected, YamlModel actual)
    {
        await Assert.That(actual.Bool).IsEqualTo(expected.Bool);
        await Assert.That(actual.Int).IsEqualTo(expected.Int);
        await Assert.That(actual.Long).IsEqualTo(expected.Long);
        await Assert.That(Math.Abs(actual.Float - expected.Float) < 0.001f).IsTrue();
        await Assert.That(actual.Double).IsEqualTo(expected.Double);
        await Assert.That(actual.String).IsEqualTo(expected.String);
        await Assert.That(actual.Enum).IsEqualTo(expected.Enum);
        await Assert.That(actual.NullableInt).IsEqualTo(expected.NullableInt);
        await Assert
            .That(actual.DateTime.ToUniversalTime())
            .IsEqualTo(expected.DateTime.ToUniversalTime());
        await Assert.That(actual.TimeSpan).IsEqualTo(expected.TimeSpan);
        await Assert.That(actual.DateOnly).IsEqualTo(expected.DateOnly);
        await Assert.That(actual.TimeOnly).IsEqualTo(expected.TimeOnly);

        await Assert.That(actual.Decimal).IsEqualTo(expected.Decimal);
        await Assert.That(actual.Guid).IsEqualTo(expected.Guid);
        await Assert.That(actual.NullableString).IsEqualTo(expected.NullableString);
        await Assert.That(actual.IntList).IsEquivalentTo(expected.IntList);
        await Assert.That(actual.StringList).IsEquivalentTo(expected.StringList);
        await Assert.That(actual.IntArray).IsEquivalentTo(expected.IntArray);
        await Assert.That(actual.StringDict).IsEquivalentTo(expected.StringDict);
        if (expected.NestedList is not null)
        {
            await Assert.That(actual.NestedList).IsNotNull();
            await Assert.That(actual.NestedList!.Count).IsEqualTo(expected.NestedList.Count);
            for (int i = 0; i < expected.NestedList.Count; i++)
            {
                await Assert.That(actual.NestedList[i].Name).IsEqualTo(expected.NestedList[i].Name);
                await Assert
                    .That(actual.NestedList[i].Value)
                    .IsEqualTo(expected.NestedList[i].Value);
            }
        }
        if (expected.Nested is not null)
        {
            await Assert.That(actual.Nested).IsNotNull();
            await Assert.That(actual.Nested!.Name).IsEqualTo(expected.Nested.Name);
            await Assert.That(actual.Nested.Value).IsEqualTo(expected.Nested.Value);
        }
    }
}
