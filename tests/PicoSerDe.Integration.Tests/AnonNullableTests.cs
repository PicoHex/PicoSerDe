namespace PicoSerDe.Integration.Tests;

public class AnonNullableTests
{
    [Test]
    public async Task Json_AnonNullableInt_NullIsNull() =>
        await Assert
            .That(JsonSerializer.Serialize(new { A = (int?)null }))
            .IsEqualTo("{\"A\":null}");

    [Test]
    public async Task Json_AnonNullableInt_ValuePreserved() =>
        await Assert.That(JsonSerializer.Serialize(new { A = (int?)5 })).IsEqualTo("{\"A\":5}");

    [Test]
    public async Task Json_AnonNullableDecimal_ValuePreserved() =>
        await Assert
            .That(JsonSerializer.Serialize(new { A = (decimal?)1.5m }))
            .IsEqualTo("{\"A\":1.5}");

    [Test]
    public async Task Json_AnonNullableAllKinds_AreNull()
    {
        // 匿名类型会重排字段，逐成员断言。
        var json = JsonSerializer.Serialize(
            new
            {
                I = (int?)null,
                L = (long?)null,
                S = (short?)null,
                B = (bool?)null,
                D = (double?)null,
                M = (decimal?)null,
                T = (DateTime?)null,
                G = (Guid?)null,
                TS = (TimeSpan?)null,
                DO = (DateOnly?)null,
                TO = (TimeOnly?)null,
            }
        );
        foreach (var n in new[] { "I", "L", "S", "B", "D", "M", "T", "G", "TS", "DO", "TO" })
            await Assert.That(json).Contains($"\"{n}\":null");
    }

    [Test]
    public async Task AnonNullableList_NullDoesNotCorruptDocument()
    {
        await Assert
            .That(JsonSerializer.Serialize(new { A = (List<int>?)null }))
            .IsEqualTo("{\"A\":null}");
        await Assert.That(TomlSerializer.Serialize(new { A = (List<int>?)null }).Trim()).IsEmpty();
        await Assert.That(YamlSerializer.Serialize(new { A = (List<int>?)null }).Trim()).IsEmpty();
        await Assert.That(IniSerializer.Serialize(new { A = (List<int>?)null }).Trim()).IsEmpty();

        // 非 null 的反向：修复不能把正常值也吞掉。
        await Assert.That(JsonSerializer.Serialize(new { A = (int?)5 })).IsEqualTo("{\"A\":5}");
        await Assert.That(TomlSerializer.Serialize(new { A = (int?)5 })).Contains("A = 5");
        await Assert.That(YamlSerializer.Serialize(new { A = (int?)5 })).Contains("A: 5");
    }

    // Added beyond the brief: proves the nullable members sit at the offsets the
    // CLR actually uses (nullable value fields sort after every non-nullable
    // value field and keep declaration order). Member *order* follows the
    // computed layout, so assert per member instead of on the whole document.
    [Test]
    public async Task Json_AnonNullableMixedWithOtherMembers_ValuesPreserved()
    {
        await Assert
            .That(JsonSerializer.Serialize(new { A = (int?)5, L = (long?)9 }))
            .IsEqualTo("{\"A\":5,\"L\":9}");
        await Assert
            .That(JsonSerializer.Serialize(new { A = (long?)9, B = (int?)5 }))
            .IsEqualTo("{\"A\":9,\"B\":5}");

        var mixed = JsonSerializer.Serialize(
            new
            {
                I = (int?)5,
                Name = "x",
                B = (byte?)7,
                G = (Guid?)Guid.Parse("11111111-2222-3333-4444-555555555555"),
            }
        );
        await Assert.That(mixed).Contains("\"I\":5");
        await Assert.That(mixed).Contains("\"Name\":\"x\"");
        await Assert.That(mixed).Contains("\"B\":7");
        await Assert.That(mixed).Contains("\"G\":\"11111111-2222-3333-4444-555555555555\"");

        var plain = JsonSerializer.Serialize(
            new
            {
                P = 42,
                Q = (short?)3,
                R = 2L,
            }
        );
        await Assert.That(plain).Contains("\"P\":42");
        await Assert.That(plain).Contains("\"Q\":3");
        await Assert.That(plain).Contains("\"R\":2");
        await Assert.That(JsonSerializer.Serialize(new { E = (short?)-3 })).IsEqualTo("{\"E\":-3}");
    }
}
