namespace PicoSerDe.Integration.Tests;

public enum AnonProbeEnum
{
    First,
    Second,
}

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
    public async Task Json_AnonCollection_NullIsNull_NonNullIsArray()
    {
        await Assert
            .That(JsonSerializer.Serialize(new { A = (List<int>?)null }))
            .IsEqualTo("{\"A\":null}");
        // The non-null reverse: a fix must not swallow representable values.
        await Assert
            .That(JsonSerializer.Serialize(new { A = new List<int> { 1, 2 } }))
            .IsEqualTo("{\"A\":[1,2]}");
    }

    [Test]
    public async Task AnonNullableList_NullDoesNotCorruptDocument()
    {
        await Assert
            .That(JsonSerializer.Serialize(new { A = (List<int>?)null }))
            .IsEqualTo("{\"A\":null}");

        // 非 null 的反向：修复不能把正常值也吞掉。
        await Assert.That(JsonSerializer.Serialize(new { A = (int?)5 })).IsEqualTo("{\"A\":5}");
        await Assert.That(TomlSerializer.Serialize(new { A = (int?)5 })).Contains("A = 5");
        await Assert.That(YamlSerializer.Serialize(new { A = (int?)5 })).Contains("A: 5");
    }

    // Review Task 2 (Important 2): TOML/YAML/INI cannot represent an anonymous
    // collection member, so it is dropped — but *loudly* (PICOSERDE005, asserted in
    // AnonCollectionDiagnosticTests) and per member, never silently with the whole
    // document. This case is what separates "member unsupported" from "null member
    // omitted"; the old `.Trim()).IsEmpty()` assertions could not tell them apart.
    [Test]
    public async Task NonJson_AnonCollection_MemberDropped_NeighboursSurvive()
    {
        var toml = TomlSerializer.Serialize(new { Name = "x", Xs = new List<int> { 1, 2 } });
        await Assert.That(toml).Contains("Name");
        await Assert.That(toml).DoesNotContain("Xs");

        var yaml = YamlSerializer.Serialize(new { Name = "x", Xs = new List<int> { 1, 2 } });
        await Assert.That(yaml).Contains("Name");
        await Assert.That(yaml).DoesNotContain("Xs");

        var ini = IniSerializer.Serialize(new { Name = "x", Xs = new List<int> { 1, 2 } });
        await Assert.That(ini).Contains("Name");
        await Assert.That(ini).DoesNotContain("Xs");
    }

    [Test]
    public async Task NonJson_AnonNullCollection_EmitsNothing()
    {
        await Assert.That(TomlSerializer.Serialize(new { A = (List<int>?)null }).Trim()).IsEmpty();
        await Assert.That(YamlSerializer.Serialize(new { A = (List<int>?)null }).Trim()).IsEmpty();
        await Assert.That(IniSerializer.Serialize(new { A = (List<int>?)null }).Trim()).IsEmpty();
    }

    // Review Task 2 (Critical 1): MsgPackWriter only offers WriteStartArray(int
    // count) / WriteStartObject(int count), so a MsgPack anonymous collection member
    // used to generate CS7036 and the format had to be dropped — this test compiling
    // and running is the "no non-compiling code" proof. The map header must also
    // shrink with the dropped member (0x81 = one entry, not 0x82).
    [Test]
    public async Task MsgPack_AnonCollection_DroppedAndMapCountMatchesEmittedMembers()
    {
        var dropped = MsgPackSerializer.SerializeToUtf8Bytes(
            new { A = 1, Xs = new List<int> { 2 } }
        );
        await Assert.That(dropped).IsEquivalentTo(new byte[] { 0x81, 0xA1, (byte)'A', 0x01 });

        var nullCollection = MsgPackSerializer.SerializeToUtf8Bytes(new { A = (List<int>?)null });
        await Assert.That(nullCollection).IsEquivalentTo(new byte[] { 0x80 });
    }

    // Review Task 2 (Minor): the CLR field-reordering assumption behind the nullable
    // sort key must hold for the 8-byte-aligned nullable kinds too — the existing
    // coverage only mixed small kinds (int?/byte?/Guid?) with neighbours.
    [Test]
    public async Task Json_AnonNullableGuidDecimalDoubleMix_ValuesPreserved()
    {
        var probe = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var only = JsonSerializer.Serialize(
            new
            {
                G = (Guid?)probe,
                M = (decimal?)1.5m,
                D = (double?)2.5,
            }
        );
        await Assert.That(only).Contains($"\"G\":\"{probe}\"");
        await Assert.That(only).Contains("\"M\":1.5");
        await Assert.That(only).Contains("\"D\":2.5");

        var mixed = JsonSerializer.Serialize(
            new
            {
                G = (Guid?)probe,
                A = 7,
                M = (decimal?)-1.25m,
                D = (double?)null,
                S = "t",
                L = 9L,
            }
        );
        await Assert.That(mixed).Contains($"\"G\":\"{probe}\"");
        await Assert.That(mixed).Contains("\"A\":7");
        await Assert.That(mixed).Contains("\"M\":-1.25");
        await Assert.That(mixed).Contains("\"D\":null");
        await Assert.That(mixed).Contains("\"S\":\"t\"");
        await Assert.That(mixed).Contains("\"L\":9");
    }

    // Review Task 2 (Minor, documented behaviour): a Nullable<enum> member is read
    // through its underlying integer kind, so JSON writes the number and not the enum
    // name (the non-nullable enum member path is a separate, pre-existing defect).
    [Test]
    public async Task Json_AnonNullableEnum_WritesUnderlyingNumber()
    {
        await Assert
            .That(JsonSerializer.Serialize(new { E = (AnonProbeEnum?)AnonProbeEnum.Second }))
            .IsEqualTo("{\"E\":1}");
        await Assert
            .That(JsonSerializer.Serialize(new { E = (AnonProbeEnum?)null }))
            .IsEqualTo("{\"E\":null}");
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
