using System.Globalization;

namespace PicoJetson.Tests;

/// <summary>A temporal member (ISO "O") next to a GUID member ("D").</summary>
[PicoSerializable]
public class TemporalMemberModel
{
    public DateTime At { get; set; }
    public DateTimeOffset OffsetAt { get; set; }
    public DateOnly Day { get; set; }
    public TimeOnly Clock { get; set; }
    public TimeSpan Elapsed { get; set; }
    public Guid Id { get; set; }
}

/// <summary>A DateTime member carrying an explicit format.</summary>
[PicoSerializable]
public class CustomDateTimeFormatModel
{
    [DateTimeFormat("yyyy-MM-dd")]
    public DateTime Date { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// The generated write path for the temporal family and Guid, asserted byte for byte.
/// </summary>
/// <remarks>
/// The generator now emits <c>TypeIo.WriteFormattable(ref jw, value, "O")</c> instead of
/// <c>jw.WriteString(value.ToString("O"))</c>. The value of that swap is measured
/// (.superpowers/sdd/json-elemwrite/report.md); what this file pins is that the document did
/// not move: every member is compared against its own <c>ToString</c> text, which is what the
/// previous emission produced by construction, and the kind/offset spectrum is covered because
/// "O" is the round-trip pattern and its suffix depends on <c>DateTimeKind</c>.
/// </remarks>
public class TemporalWriteTests
{
    private static string Serialize(TemporalMemberModel model) =>
        Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(model));

    private static string Expected(TemporalMemberModel m) =>
        "{\"At\":\""
        + m.At.ToString("O", CultureInfo.InvariantCulture)
        + "\",\"OffsetAt\":\""
        + m.OffsetAt.ToString("O", CultureInfo.InvariantCulture)
        + "\",\"Day\":\""
        + m.Day.ToString("O", CultureInfo.InvariantCulture)
        + "\",\"Clock\":\""
        + m.Clock.ToString("O", CultureInfo.InvariantCulture)
        + "\",\"Elapsed\":\""
        + m.Elapsed.ToString("c", CultureInfo.InvariantCulture)
        + "\",\"Id\":\""
        + m.Id.ToString("D", CultureInfo.InvariantCulture)
        + "\"}";

    public static IEnumerable<(string Name, DateTimeKind Kind, int OffsetHours)> Kinds() =>
        [
            ("utc", DateTimeKind.Utc, 0),
            ("unspecified", DateTimeKind.Unspecified, 0),
            ("local", DateTimeKind.Local, 0),
        ];

    [Test]
    [MethodDataSource(nameof(Kinds))]
    public async Task EveryKindSuffixIsWrittenAsToStringWouldWriteIt(
        string name,
        DateTimeKind kind,
        int offsetHours
    )
    {
        var model = new TemporalMemberModel
        {
            At = new DateTime(2026, 10, 27, 12, 0, 0, kind).AddTicks(1234567),
            OffsetAt = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.FromHours(offsetHours)),
            Day = new DateOnly(2024, 2, 29),
            Clock = new TimeOnly(13, 14, 15).Add(TimeSpan.FromTicks(1234567)),
            Elapsed = TimeSpan.FromSeconds(-90.5),
            Id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"),
        };
        await Assert.That(Serialize(model)).IsEqualTo(Expected(model)).Because(name);
    }

    [Test]
    public async Task ExtremeValuesAreWrittenAsToStringWouldWriteThem()
    {
        var model = new TemporalMemberModel
        {
            At = DateTime.MaxValue,
            OffsetAt = DateTimeOffset.MinValue,
            Day = DateOnly.MinValue,
            Clock = TimeOnly.MaxValue,
            Elapsed = TimeSpan.MinValue,
            Id = Guid.Empty,
        };
        await Assert.That(Serialize(model)).IsEqualTo(Expected(model));
    }

    [Test]
    public async Task AnExplicitDateTimeFormatKeepsItsOwnText()
    {
        var model = new CustomDateTimeFormatModel
        {
            Date = new DateTime(2026, 10, 27, 12, 34, 56, DateTimeKind.Utc),
            CreatedAt = new DateTime(2026, 10, 27, 12, 34, 56, DateTimeKind.Utc),
        };
        var json = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(model));
        await Assert
            .That(json)
            .IsEqualTo(
                $$"""{"Date":"{{model.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}}","CreatedAt":"{{model.CreatedAt.ToString("O", CultureInfo.InvariantCulture)}}"}"""
            );
    }

    [Test]
    public async Task TheMembersRoundTrip()
    {
        var model = new TemporalMemberModel
        {
            At = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234567),
            OffsetAt = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.FromHours(5.5)),
            Day = new DateOnly(2024, 2, 29),
            Clock = new TimeOnly(13, 14, 15).Add(TimeSpan.FromTicks(1234567)),
            Elapsed = TimeSpan.FromSeconds(-90.5),
            Id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"),
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(model);
        var back = JsonSerializer.Deserialize<TemporalMemberModel>(json);
        await Assert.That(back!.At).IsEqualTo(model.At);
        await Assert.That(back.At.Kind).IsEqualTo(model.At.Kind);
        await Assert.That(back.OffsetAt).IsEqualTo(model.OffsetAt);
        await Assert.That(back.Day).IsEqualTo(model.Day);
        await Assert.That(back.Clock).IsEqualTo(model.Clock);
        await Assert.That(back.Elapsed).IsEqualTo(model.Elapsed);
        await Assert.That(back.Id).IsEqualTo(model.Id);
    }

    [Test]
    public async Task ADictionaryWithStringKeysKeepsItsCommas()
    {
        // The dictionary key used to be handed over as Encoding.UTF8.GetBytes(key) — a byte[]
        // per key. The char overload the generator now picks must place the separator exactly
        // where the byte one did, including for a key that needs escaping.
        var model = new StringKeyDictionaryModel
        {
            Values = new()
            {
                ["plain"] = 1,
                ["a\"quote"] = 2,
                ["line\nbreak"] = 3,
                ["unicode-ü"] = 4,
            },
        };
        var json = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(model));
        await Assert
            .That(json)
            .IsEqualTo(
                "{\"Values\":{\"plain\":1,\"a\\\"quote\":2,\"line\\nbreak\":3,\"unicode-ü\":4}}"
            );
    }
}

/// <summary>A string-keyed dictionary, for the key-write assertion.</summary>
[PicoSerializable]
public class StringKeyDictionaryModel
{
    public Dictionary<string, int> Values { get; set; } = new();
}
