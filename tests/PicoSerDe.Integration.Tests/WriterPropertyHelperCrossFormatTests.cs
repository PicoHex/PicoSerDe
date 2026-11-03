namespace PicoSerDe.Integration.Tests;

/// <summary>
/// The cross-format half of the <c>Write*Property</c> family: the same logical model, written with
/// the <em>same seven member-named calls</em> through both writers, has to come back out of each
/// format's own reader as the same five facts.
/// </summary>
/// <remarks>
/// <para>
/// "One meaning, per-family spelling" is the whole rule: a member is <c>name: value</c> in YAML and
/// <c>"name": value</c> in JSON, so the entry points share names (<c>WriteStringProperty</c>,
/// <c>WriteNumberProperty</c>, …) and the bytes differ exactly as much as the grammars differ. What
/// must <b>not</b> differ is the meaning, so the proof is a round trip through two independent
/// readers — <c>System.Text.Json</c> for the JSON document, PicoYaml's own
/// <see cref="YamlReader"/> for the YAML one — compared as one record. An assertion on the two
/// documents' text alone would only prove that two hard-coded strings are hard-coded.
/// </para>
/// <para>
/// The only call that is not in both arms is the container pair <c>WriteStartObject</c> /
/// <c>WriteEndObject</c>: JSON has to bracket its members (<c>{…}</c>) and YAML's document root is
/// already a mapping, so there is nothing to open. That asymmetry is the format's, not the
/// family's — the member calls themselves are identical in both arms, in the same order.
/// </para>
/// </remarks>
public class WriterPropertyHelperCrossFormatTests
{
    /// <summary>The logical model both arms write, in the order they write it.</summary>
    private sealed record Model(string? Name, int Count, double Ratio, bool Active, string? Note);

    private static readonly Model Expected = new("Alice", 3, 0.5, true, null);

    // ── the two documents ──────────────────────────────────────────────────

    private static string JsonDocument()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new JsonWriter(buf);
        w.WriteStartObject();
        w.WriteStringProperty("name"u8, "Alice");
        w.WriteNumberProperty("count"u8, 3);
        w.WriteNumberProperty("ratio"u8, 0.5);
        w.WriteBooleanProperty("active"u8, true);
        w.WriteNullProperty("note"u8);
        w.WriteEndObject();
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    private static string YamlDocument()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new YamlWriter(buf);
        w.WriteStringProperty("name"u8, "Alice");
        w.WriteNumberProperty("count"u8, 3);
        w.WriteNumberProperty("ratio"u8, 0.5);
        w.WriteBooleanProperty("active"u8, true);
        w.WriteNullProperty("note"u8);
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    // ── the claim ──────────────────────────────────────────────────────────

    [Test]
    public async Task TheSameMemberCalls_produceTheSameFacts_inTwoLegalDocuments()
    {
        var json = JsonDocument();
        var yaml = YamlDocument();

        // Each arm is the document its own grammar requires, and nothing else.
        await Assert
            .That(json)
            .IsEqualTo("""{"name":"Alice","count":3,"ratio":0.5,"active":true,"note":null}""");
        await Assert
            .That(yaml)
            .IsEqualTo("name: Alice\ncount: 3\nratio: 0.5\nactive: true\nnote: null\n");

        // … and each reader gets the same five facts back out of it, which is what "same meaning"
        // has to mean: not "the strings look alike", but "the model survives both".
        var fromJson = ReadJson(json);
        var fromYaml = ReadYaml(yaml);

        await Assert.That(fromJson).IsEqualTo(Expected);
        await Assert.That(fromYaml).IsEqualTo(Expected);
        await Assert.That(fromJson).IsEqualTo(fromYaml);
    }

    /// <summary>
    /// The number formatting is one answer, not two: both writers print the double through
    /// <c>InvariantCulture</c>, so the same value cannot be <c>0,5</c> in one document and
    /// <c>0.5</c> in the other under a comma-decimal culture.
    /// </summary>
    [Test]
    public async Task BothWriters_printNumbers_theSameWayUnderACommaDecimalCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(
                "de-DE"
            );
            var json = JsonDocument();
            var yaml = YamlDocument();

            await Assert.That(ReadJson(json).Ratio).IsEqualTo(0.5);
            await Assert.That(ReadYaml(yaml).Ratio).IsEqualTo(0.5);
            await Assert.That(json).Contains("\"ratio\":0.5");
            await Assert.That(yaml).Contains("ratio: 0.5\n");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    // ── two independent readers, one record ────────────────────────────────

    private static Model ReadJson(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new(
            root.GetProperty("name").GetString(),
            root.GetProperty("count").GetInt32(),
            root.GetProperty("ratio").GetDouble(),
            root.GetProperty("active").GetBoolean(),
            root.GetProperty("note").ValueKind == System.Text.Json.JsonValueKind.Null
                ? null
                : root.GetProperty("note").GetString()
        );
    }

    private static Model ReadYaml(string yaml)
    {
        var reader = new YamlReader(Encoding.UTF8.GetBytes(yaml));
        string? name = null;
        string? note = null;
        var count = 0;
        var ratio = 0d;
        var active = false;
        while (reader.Read())
        {
            switch (Encoding.UTF8.GetString(reader.KeySpan))
            {
                case "name":
                    name = Encoding.UTF8.GetString(reader.ValueSpan);
                    break;
                case "count":
                    if (!reader.TryGetInt32(out count))
                        throw new InvalidOperationException("count is not an int");
                    break;
                case "ratio":
                    if (!reader.TryGetFloat64(out ratio))
                        throw new InvalidOperationException("ratio is not a double");
                    break;
                case "active":
                    if (!reader.TryGetBool(out active))
                        throw new InvalidOperationException("active is not a bool");
                    break;
                case "note":
                    note = reader.IsNullLiteral ? null : Encoding.UTF8.GetString(reader.ValueSpan);
                    break;
                default:
                    throw new InvalidOperationException("unexpected key");
            }
        }
        return new(name, count, ratio, active, note);
    }
}
