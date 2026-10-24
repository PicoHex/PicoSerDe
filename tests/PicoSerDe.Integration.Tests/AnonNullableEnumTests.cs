// F4 — an anonymous type's Nullable<enum> member must keep enum semantics.
//
// Before this fix AnonTypeHandler.NullableKind() mapped every enum to its
// underlying *integer* kind ("nullable_int32"/"nullable_byte"/"nullable_int64"),
// so the member was written as a number ("L":2) instead of the enum name the
// named-DTO path uses ("L":"High"), in every format.
//
// Anonymous types have no deserialize entry point (the type cannot be named at a
// call site), so "round-trip" here means: serialize the anonymous value, then
// deserialize the produced document into the equivalent *named* holder — the
// same text the named DTO path writes and its Enum.TryParse reader accepts.

namespace PicoSerDe.Integration.Tests;

public enum ByteLevel : byte
{
    Low = 7,
    High = 200,
}

public enum LongLevel : long
{
    Low = 1,
    High = 0x1_0000_0002,
}

public enum ShortLevel : short
{
    Low = 3,
    High = 0x1234,
}

public class AnonLevelHolder
{
    public Level? L { get; set; }
}

public class AnonByteLevelHolder
{
    public ByteLevel? L { get; set; }
}

public class AnonLongLevelHolder
{
    public LongLevel? L { get; set; }
}

public class AnonNullableEnumTests
{
    // ── JSON: name, not number ──

    [Test]
    public async Task Json_NullableEnum_WritesEnumName()
    {
        await Assert
            .That(JsonSerializer.Serialize(new { L = (Level?)Level.High }))
            .IsEqualTo("{\"L\":\"High\"}");
    }

    [Test]
    public async Task Json_NullableEnum_RoundTripsThroughNamedHolder()
    {
        var json = JsonSerializer.Serialize(new { L = (Level?)Level.High });
        var back = JsonSerializer.Deserialize<AnonLevelHolder>(Encoding.UTF8.GetBytes(json))!;
        await Assert.That(back.L).IsEqualTo(Level.High);
    }

    [Test]
    public async Task Json_NullableEnum_NullStaysNull()
    {
        var json = JsonSerializer.Serialize(new { L = (Level?)null });
        await Assert.That(json).IsEqualTo("{\"L\":null}");
        var back = JsonSerializer.Deserialize<AnonLevelHolder>(Encoding.UTF8.GetBytes(json))!;
        await Assert.That(back.L).IsNull();
    }

    // ── Underlying-type width must survive: byte- and long-backed enums ──

    [Test]
    public async Task Json_ByteBackedNullableEnum_UsesName_NotUnderlyingNumber()
    {
        var json = JsonSerializer.Serialize(new { L = (ByteLevel?)ByteLevel.High });
        await Assert.That(json).IsEqualTo("{\"L\":\"High\"}");

        var back = JsonSerializer.Deserialize<AnonByteLevelHolder>(Encoding.UTF8.GetBytes(json))!;
        await Assert.That(back.L).IsEqualTo(ByteLevel.High);
    }

    [Test]
    public async Task Json_LongBackedNullableEnum_UsesName_NotUnderlyingNumber()
    {
        // LongLevel.High has bits above 32: a reader that collapsed the member to
        // int would print "2" (the truncated value), not "High".
        var json = JsonSerializer.Serialize(new { L = (LongLevel?)LongLevel.High });
        await Assert.That(json).IsEqualTo("{\"L\":\"High\"}");

        var back = JsonSerializer.Deserialize<AnonLongLevelHolder>(Encoding.UTF8.GetBytes(json))!;
        await Assert.That(back.L).IsEqualTo(LongLevel.High);
    }

    // ── Other formats: the enum name is what the named reader parses ──

    [Test]
    public async Task Toml_NullableEnum_RoundTrips()
    {
        var toml = TomlSerializer.Serialize(new { L = (Level?)Level.High });
        await Assert.That(toml).Contains("High");
        await Assert.That(toml).DoesNotContain("2");
        var back = TomlSerializer.Deserialize<AnonLevelHolder>(Encoding.UTF8.GetBytes(toml))!;
        await Assert.That(back.L).IsEqualTo(Level.High);
    }

    [Test]
    public async Task Yaml_NullableEnum_RoundTrips()
    {
        var yaml = YamlSerializer.Serialize(new { L = (Level?)Level.High });
        await Assert.That(yaml).Contains("High");
        await Assert.That(yaml).DoesNotContain("2");
        var back = YamlSerializer.Deserialize<AnonLevelHolder>(Encoding.UTF8.GetBytes(yaml))!;
        await Assert.That(back.L).IsEqualTo(Level.High);
    }

    [Test]
    public async Task Ini_NullableEnum_RoundTrips()
    {
        var ini = IniSerializer.Serialize(new { L = (Level?)Level.High });
        await Assert.That(ini).Contains("High");
        await Assert.That(ini).DoesNotContain("2");
        var back = IniSerializer.Deserialize<AnonLevelHolder>(Encoding.UTF8.GetBytes(ini))!;
        await Assert.That(back.L).IsEqualTo(Level.High);
    }

    // MsgPack anonymous maps use *string* keys (0xA1 'L'), while a named MsgPack
    // DTO reads integer keys — so an anonymous MsgPack document has never been
    // readable by a named deserializer, for any member type (see
    // AnonTypeSerializationTests.MsgPack_AnonType_Serializes, which can only assert
    // bytes). What F4 owns here is the member *value*: the enum name string, not
    // the integer 2.
    [Test]
    public async Task MsgPack_NullableEnum_WritesEnumNameString()
    {
        var payload = MsgPackSerializer.SerializeToUtf8Bytes(new { L = (Level?)Level.High });
        await Assert
            .That(payload)
            .IsEquivalentTo(
                new byte[]
                {
                    0x81,
                    0xA1,
                    (byte)'L',
                    0xA4,
                    (byte)'H',
                    (byte)'i',
                    (byte)'g',
                    (byte)'h',
                }
            );

        var nullPayload = MsgPackSerializer.SerializeToUtf8Bytes(new { L = (Level?)null });
        await Assert.That(nullPayload).IsEquivalentTo(new byte[] { 0x81, 0xA1, (byte)'L', 0xC0 });
    }

    // ── Layout must not be corrupted ──

    [Test]
    public async Task Json_NullableEnumWithNeighbours_AllValuesPreserved()
    {
        var json = JsonSerializer.Serialize(
            new
            {
                A = (Level?)Level.High,
                B = 42L,
                C = "x",
            }
        );
        await Assert.That(json).Contains("\"A\":\"High\"");
        await Assert.That(json).Contains("\"B\":42");
        await Assert.That(json).Contains("\"C\":\"x\"");
    }

    // A byte-backed Nullable<T> is 2 bytes/align 1 (measured), an int? is 8/4, so
    // a nullable-enum layout that assumed a fixed pointer-sized slot would put the
    // last member at the wrong offset and read garbage.
    [Test]
    public async Task Json_MixedNullableWidths_OffsetsStayExact()
    {
        var json = JsonSerializer.Serialize(
            new
            {
                A = (ByteLevel?)ByteLevel.High,
                B = (int?)0x11223344,
                C = (ByteLevel?)ByteLevel.Low,
            }
        );
        await Assert.That(json).Contains("\"A\":\"High\"");
        await Assert.That(json).Contains($"\"B\":{0x11223344}");
        await Assert.That(json).Contains("\"C\":\"Low\"");

        var longMix = JsonSerializer.Serialize(
            new
            {
                A = (ByteLevel?)ByteLevel.Low,
                B = (long?)0x1122334455667788,
                C = (int?)0x11223344,
            }
        );
        await Assert.That(longMix).Contains("\"A\":\"Low\"");
        await Assert.That(longMix).Contains($"\"B\":{0x1122334455667788}");
        await Assert.That(longMix).Contains($"\"C\":{0x11223344}");
    }

    // Null semantics stay per format: JSON/MsgPack write an explicit null, the
    // section formats have no null value and omit the member (README "Null
    // Semantics"); a null enum must not swallow its neighbours either.
    [Test]
    public async Task NonJson_NullableEnumNull_OmitsMember()
    {
        await Assert.That(TomlSerializer.Serialize(new { L = (Level?)null }).Trim()).IsEmpty();
        await Assert.That(YamlSerializer.Serialize(new { L = (Level?)null }).Trim()).IsEmpty();
        await Assert.That(IniSerializer.Serialize(new { L = (Level?)null }).Trim()).IsEmpty();

        var json = JsonSerializer.Serialize(new { A = (Level?)null, B = 42 });
        await Assert.That(json).Contains("\"A\":null");
        await Assert.That(json).Contains("\"B\":42");
    }

    // ── Fix round 1: the slot is per underlying width, never a fixed one ──

    // An int-backed (8/4) and a byte-backed (2/1) Nullable<enum> in one anonymous
    // type. A single conservative slot for "nullable enum" (pointer-sized, or the
    // int32 width) puts at least one of them, or the sentinel that follows, at the
    // wrong offset — and a wrong offset reads garbage that cannot print as the
    // expected enum name.
    [Test]
    public async Task Json_IntAndByteBackedNullableEnums_Mixed_AllValuesExact()
    {
        var json = JsonSerializer.Serialize(
            new
            {
                A = (Level?)Level.High,
                B = (ByteLevel?)ByteLevel.High,
                C = 0x11223344,
                D = (ByteLevel?)ByteLevel.Low,
                E = 0x1122334455667788L,
            }
        );
        await Assert.That(json).Contains("\"A\":\"High\"");
        await Assert.That(json).Contains("\"B\":\"High\"");
        await Assert.That(json).Contains($"\"C\":{0x11223344}");
        await Assert.That(json).Contains("\"D\":\"Low\"");
        await Assert.That(json).Contains($"\"E\":{0x1122334455667788}");
    }

    // All four enum underlying widths of the CLR (byte/short/int/long) in one
    // layout: 2/1, 4/2, 8/4 and 16/8. Each one has to land on its own width — no
    // fixed slot can be right for more than one of them.
    [Test]
    public async Task Json_FourUnderlyingWidthsMixed_AllValuesExact()
    {
        var json = JsonSerializer.Serialize(
            new
            {
                A = (ByteLevel?)ByteLevel.High,
                B = (ShortLevel?)ShortLevel.High,
                C = (Level?)Level.High,
                D = (LongLevel?)LongLevel.High,
                E = (int?)0x55667788,
            }
        );
        await Assert.That(json).Contains("\"A\":\"High\"");
        await Assert.That(json).Contains("\"B\":\"High\"");
        await Assert.That(json).Contains("\"C\":\"High\"");
        await Assert.That(json).Contains("\"D\":\"High\"");
        await Assert.That(json).Contains($"\"E\":{0x55667788}");
    }

    // The mixed-width case in the section formats too: the layout is computed once
    // in shared/Gen and consumed by all five generators, so a wrong slot corrupts
    // every format equally.
    [Test]
    public async Task NonJson_IntAndByteBackedNullableEnums_Mixed_AllValuesExact()
    {
        var toml = TomlSerializer.Serialize(
            new
            {
                A = (Level?)Level.High,
                B = (ByteLevel?)ByteLevel.High,
                C = 0x11223344,
            }
        );
        await Assert.That(toml).Contains("A = \"High\"");
        await Assert.That(toml).Contains("B = \"High\"");
        await Assert.That(toml).Contains($"C = {0x11223344}");

        var yaml = YamlSerializer.Serialize(
            new
            {
                A = (Level?)Level.Low,
                B = (ByteLevel?)ByteLevel.High,
                C = 0x11223344,
            }
        );
        await Assert.That(yaml).Contains("A: Low");
        await Assert.That(yaml).Contains("B: High");
        await Assert.That(yaml).Contains($"C: {0x11223344}");
    }

    // Level? and int? have the *same* physical slot (8/4) but different semantics, so
    // this is the pair most likely to poison each other: the enum must still be
    // written as an enum (its name) and the int as an int, alternating, nulls
    // included.
    [Test]
    public async Task Json_NullableEnumAndNullableIntInterleaved_NoPollution()
    {
        var json = JsonSerializer.Serialize(
            new
            {
                A = (Level?)Level.High,
                B = (int?)11,
                C = (Level?)Level.Low,
                D = (int?)22,
                E = (Level?)null,
                F = (int?)null,
            }
        );
        await Assert.That(json).Contains("\"A\":\"High\"");
        await Assert.That(json).Contains("\"B\":11");
        await Assert.That(json).Contains("\"C\":\"Low\"");
        await Assert.That(json).Contains("\"D\":22");
        await Assert.That(json).Contains("\"E\":null");
        await Assert.That(json).Contains("\"F\":null");
        // A name must never leak into an int slot (or the int into a name slot).
        await Assert.That(json).DoesNotContain("\"B\":\"");
        await Assert.That(json).DoesNotContain("\"D\":\"");
    }

    // ── Non-enum nullable value types are untouched by this fix ──

    [Test]
    public async Task Json_NonEnumNullableMembers_Unchanged()
    {
        await Assert.That(JsonSerializer.Serialize(new { A = (int?)5 })).IsEqualTo("{\"A\":5}");
        await Assert
            .That(JsonSerializer.Serialize(new { A = (int?)null }))
            .IsEqualTo("{\"A\":null}");
        await Assert
            .That(JsonSerializer.Serialize(new { A = (decimal?)1.5m }))
            .IsEqualTo("{\"A\":1.5}");
        await Assert
            .That(
                JsonSerializer.Serialize(
                    new { A = (Guid?)Guid.Parse("11111111-2222-3333-4444-555555555555") }
                )
            )
            .Contains("\"11111111-2222-3333-4444-555555555555\"");
    }
}
