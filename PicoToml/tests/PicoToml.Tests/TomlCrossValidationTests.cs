using Tomlyn;
using Tomlyn.Model;

namespace PicoToml.Tests;

public class TomlSub
{
    public string Name { get; set; } = "";
    public int Value { get; set; }
}

public class TomlModel
{
    public bool Bool { get; set; }
    public int Int { get; set; }
    public long Long { get; set; }
    public float Float { get; set; }
    public double Double { get; set; }
    public decimal Decimal { get; set; }
    public string String { get; set; } = "";
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
    public List<TomlSub>? NestedList { get; set; }
}

public class TomlCrossValidationTests
{
    private static TomlModel Model =>
        new()
        {
            Bool = true,
            Int = 42,
            Long = 9_876_543_210L,
            Float = 3.14f,
            Double = 2.71828,
            Decimal = 123.45m,
            String = "Hello, TOML!",
            DateTime = new DateTime(2026, 6, 4, 12, 30, 0, DateTimeKind.Utc),
            TimeSpan = new TimeSpan(10, 30, 0),
            DateOnly = new DateOnly(2026, 6, 4),
            TimeOnly = new TimeOnly(15, 45, 30),
            Guid = Guid.Parse("A1B2C3D4-E5F6-7890-ABCD-EF1234567890"),
            Enum = DayOfWeek.Wednesday,
            NullableInt = 77,
            IntList = [10, 20, 30],
            StringList = ["foo", "bar"],
            IntArray = [100, 200],
            StringDict = new() { ["k1"] = "v1" },
            NestedList = [new() { Name = "a", Value = 1 }, new() { Name = "b", Value = 2 }],
        };

    [Test]
    public async Task Sg_Trigger()
    {
        var bytes = TomlSerializer.SerializeToUtf8Bytes(Model);
        var back = TomlSerializer.Deserialize<TomlModel>(bytes);
        await Assert.That(back).IsNotNull();
    }

    [Test]
    public async Task PicoRoundTrip()
    {
        var bytes = TomlSerializer.SerializeToUtf8Bytes(Model);
        var back = TomlSerializer.Deserialize<TomlModel>(bytes);
        await AssertTomlEqual(Model, back!);
    }

    [Test]
    public async Task PicoSerialize_TomlynDeserialize()
    {
        var picoBytes = TomlSerializer.SerializeToUtf8Bytes(Model);
        var tomlText = Encoding.UTF8.GetString(picoBytes);
        var table = Toml.ToModel(tomlText);

        // Primitives that Tomlyn preserves as native types
        await Assert.That((bool)table["Bool"]).IsTrue();
        await Assert.That((long)table["Int"]).IsEqualTo(42L);
        await Assert.That((long)table["Long"]).IsEqualTo(9_876_543_210L);
        await Assert.That((double)table["Float"]).IsGreaterThan(3.13);
        await Assert.That((double)table["Double"]).IsEqualTo(2.71828);
        await Assert.That((long)table["NullableInt"]).IsEqualTo(77L);

        // Types PicoToml serializes as quoted strings
        await Assert.That((string)table["Decimal"]).IsEqualTo("123.45");
        await Assert.That((string)table["String"]).IsEqualTo("Hello, TOML!");
        await Assert.That((string)table["DateTime"]).IsEqualTo("2026-06-04T12:30:00.0000000Z");
        await Assert.That((string)table["TimeSpan"]).IsEqualTo("10:30:00");
        await Assert.That((string)table["DateOnly"]).IsEqualTo("2026-06-04");
        await Assert.That((string)table["TimeOnly"]).IsEqualTo("15:45:30.0000000");
        await Assert.That((string)table["Guid"]).IsEqualTo("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        await Assert.That((string)table["Enum"]).IsEqualTo("Wednesday");

        await Assert.That(table.ContainsKey("NullableString")).IsFalse();

        // Collections
        var intList = (TomlArray)table["IntList"];
        await Assert.That(intList.Count).IsEqualTo(3);
        await Assert.That((long)intList[0]).IsEqualTo(10L);
        await Assert.That((long)intList[1]).IsEqualTo(20L);
        await Assert.That((long)intList[2]).IsEqualTo(30L);

        var strList = (TomlArray)table["StringList"];
        await Assert.That(strList.Count).IsEqualTo(2);
        await Assert.That((string)strList[0]).IsEqualTo("foo");
        await Assert.That((string)strList[1]).IsEqualTo("bar");

        var intArr = (TomlArray)table["IntArray"];
        await Assert.That(intArr.Count).IsEqualTo(2);
        await Assert.That((long)intArr[0]).IsEqualTo(100L);
        await Assert.That((long)intArr[1]).IsEqualTo(200L);

        var dict = (TomlTable)table["StringDict"];
        await Assert.That(dict.Count).IsEqualTo(1);
        await Assert.That((string)dict["k1"]).IsEqualTo("v1");
    }

    [Test]
    public async Task TomlynSerialize_PicoDeserialize()
    {
        var tomlTable = new TomlTable
        {
            ["Bool"] = true,
            ["Int"] = 42L,
            ["Long"] = 9_876_543_210L,
            ["Float"] = 3.14,
            ["Double"] = 2.71828,
            ["Decimal"] = "123.45",
            ["String"] = "Hello from Tomlyn!",
            ["DateTime"] = "2026-06-04T12:30:00.0000000Z",
            ["TimeSpan"] = "10:30:00",
            ["DateOnly"] = "2026-06-04",
            ["TimeOnly"] = "15:45:30",
            ["Guid"] = "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
            ["Enum"] = "Wednesday",
            ["NullableString"] = "not null",
            ["NullableInt"] = 77L,
        };
        tomlTable["IntList"] = new TomlArray { 10L, 20L, 30L };
        tomlTable["StringList"] = new TomlArray { "foo", "bar" };
        tomlTable["IntArray"] = new TomlArray { 100L, 200L };
        tomlTable["StringDict"] = new TomlTable { ["k1"] = "v1" };
        tomlTable["NestedList"] = new TomlArray
        {
            new TomlTable { ["Name"] = "a", ["Value"] = 1L },
            new TomlTable { ["Name"] = "b", ["Value"] = 2L },
        };

        var tomlText = Toml.FromModel(tomlTable);
        var bytes = Encoding.UTF8.GetBytes(tomlText);
        var model = TomlSerializer.Deserialize<TomlModel>(bytes);

        await Assert.That(model).IsNotNull();
        await Assert.That(model!.Bool).IsTrue();
        await Assert.That(model.Int).IsEqualTo(42);
        await Assert.That(model.Long).IsEqualTo(9_876_543_210L);
        await Assert.That(Math.Abs(model.Float - 3.14f) < 0.001f).IsTrue();
        await Assert.That(model.Double).IsEqualTo(2.71828);
        await Assert.That(model.Decimal).IsEqualTo(123.45m);
        await Assert.That(model.String).IsEqualTo("Hello from Tomlyn!");
        await Assert.That(model.NullableString).IsEqualTo("not null");
        await Assert.That(model.NullableInt).IsEqualTo(77);
        await Assert
            .That(model.DateTime.ToUniversalTime())
            .IsEqualTo(new DateTime(2026, 6, 4, 12, 30, 0, DateTimeKind.Utc));
        await Assert.That(model.TimeSpan).IsEqualTo(new TimeSpan(10, 30, 0));
        await Assert.That(model.DateOnly).IsEqualTo(new DateOnly(2026, 6, 4));
        await Assert.That(model.TimeOnly).IsEqualTo(new TimeOnly(15, 45, 30));
        await Assert.That(model.Guid).IsEqualTo(Guid.Parse("A1B2C3D4-E5F6-7890-ABCD-EF1234567890"));
        await Assert.That(model.Enum).IsEqualTo(DayOfWeek.Wednesday);
        await Assert.That(model.IntList).IsEquivalentTo(new List<int> { 10, 20, 30 });
        await Assert.That(model.StringList).IsEquivalentTo(new List<string> { "foo", "bar" });
        await Assert.That(model.IntArray).IsEquivalentTo(new[] { 100, 200 });
        await Assert.That(model.StringDict.Count).IsEqualTo(1);
        await Assert.That(model.StringDict["k1"]).IsEqualTo("v1");
        await Assert.That(model.NestedList).IsNotNull();
        await Assert.That(model.NestedList!.Count).IsEqualTo(2);
        await Assert.That(model.NestedList[0].Name).IsEqualTo("a");
        await Assert.That(model.NestedList[0].Value).IsEqualTo(1);
        await Assert.That(model.NestedList[1].Name).IsEqualTo("b");
        await Assert.That(model.NestedList[1].Value).IsEqualTo(2);
    }

    private static async Task AssertTomlEqual(TomlModel expected, TomlModel actual)
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
        await Assert.That(actual.Guid).IsEqualTo(expected.Guid);
        await Assert.That(actual.IntList).IsEquivalentTo(expected.IntList);
        await Assert.That(actual.StringList).IsEquivalentTo(expected.StringList);
        await Assert.That(actual.IntArray).IsEquivalentTo(expected.IntArray);
        if (expected.NestedList is null)
        {
            await Assert.That(actual.NestedList).IsNull();
        }
        else
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
    }
}
