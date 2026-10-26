namespace PicoSerDe.Integration.Tests;

// ── I-6 white areas (spec §7 I-6, R2 Task 8) ────────────────────────────────────────
//
// I-6 says "every cell marked SUPPORTED has a round-trip test CONTAINING BOUNDARY VALUES".
// R1's spec review found eight areas where that was not true: values nothing ever drove, so
// the cell was backed by a promise instead of by a measurement. Each group below is one of
// those eight, and each one is written as the assertion I-6 asks for — not as a snapshot of
// whatever the code did the day it was written. Where the honest target is not reached, the
// test says so in one sentence ("known limitation", with the reason and the reopen condition)
// instead of quietly pinning today's behaviour as if it were the goal.

/// <summary>S15's scalar family: the members whose canonical TEXT contains separators.</summary>
[PicoSerializable]
public class ScalarBoundaryModel
{
    public float F { get; set; }
    public decimal D { get; set; }
    public DateTime When { get; set; }
    public TimeSpan Span { get; set; }
    public Guid Id { get; set; }
    public DayOfWeek? Day { get; set; }
}

/// <summary>Special floating-point values: no format literal in JSON, literals in TOML/YAML.</summary>
[PicoSerializable]
public class SpecialFloatModel
{
    public double Dbl { get; set; }
    public float Flt { get; set; }
}

/// <summary>A null collection against an empty one — different values, and a different wire.</summary>
[PicoSerializable]
public class NullableCollectionModel
{
    public List<int>? Ints { get; set; }
    public Dictionary<string, int>? Map { get; set; }
    public string? Text { get; set; }
}

/// <summary>An object list with a null element (spec's S14): <c>List&lt;Dto?&gt;</c>.</summary>
[PicoSerializable]
public class ObjectNullElementModel
{
    public List<BoundaryDto?>? V { get; set; }
}

[PicoSerializable]
public class BoundaryDto
{
    public string Name { get; set; } = "";
    public int N { get; set; }
}

public class BoundaryValueWhiteAreaTests
{
    private static readonly string[] AllFormats = ["json", "msgpack", "toml", "yaml", "ini"];

    // ── item 1: ±Infinity, in every format ──────────────────────────────────────────

    /// <summary>
    /// I-6 white area 1: no test anywhere drove <c>±Infinity</c> through any format. Two
    /// formats can hold it (msgpack's float64 encoding, INI's IEEE text) and three refuse it
    /// (spec §3 footnotes ²/³): JSON has no infinity literal at all — a format FACT — while
    /// TOML and YAML DO have one (<c>inf</c>/<c>-inf</c>), so their refusal is OUR debt and the
    /// reopen condition is written down rather than implied.
    /// </summary>
    [Test]
    [Arguments("msgpack")]
    [Arguments("ini")]
    public async Task Infinity_round_trips_where_the_format_has_a_form_for_it(string tag)
    {
        var back = RoundTripFloat(tag, new SpecialFloatModel { Dbl = double.NegativeInfinity });
        await Assert.That(double.IsNegativeInfinity(back.Dbl)).IsTrue();

        back = RoundTripFloat(tag, new SpecialFloatModel { Dbl = double.PositiveInfinity });
        await Assert.That(double.IsPositiveInfinity(back.Dbl)).IsTrue();

        back = RoundTripFloat(tag, new SpecialFloatModel { Flt = float.NegativeInfinity });
        await Assert.That(float.IsNegativeInfinity(back.Flt)).IsTrue();

        await Assert
            .That(SafeRoundTripFloat(tag, new SpecialFloatModel { Dbl = double.PositiveInfinity }))
            .IsNotNull()
            .Because("a format that has a form for infinity must not throw on it either");
    }

    /// <summary>
    /// The three formats that refuse <c>±Infinity</c>, each in its OWN class:
    /// JSON cannot (no literal — spec §3 footnote ²), TOML/YAML can and we do not (footnote ³
    /// — <c>inf</c>/<c>-inf</c> parse in `tomllib` and PyYAML). Pinning the refusal is the
    /// point: the failure mode is an exception at the call site, and a silent rewrite to
    /// <c>double.MaxValue</c> would be data corruption nobody would notice.
    /// </summary>
    [Test]
    [Arguments("json")]
    [Arguments("toml")]
    [Arguments("yaml")]
    public async Task Infinity_is_refused_loudly_by_the_formats_that_have_no_form_for_it(string tag)
    {
        var thrown = Assert.Throws<ArgumentException>(() =>
            SerializeFloat(tag, new SpecialFloatModel { Dbl = double.PositiveInfinity })
        );
        await Assert
            .That(thrown.Message)
            .Contains("Infinity")
            .Because(
                "the refusal has to name what was refused, otherwise the reader is left with "
                    + "an unexplained ArgumentException: "
                    + thrown.Message
            );
    }

    // ── item 2: null collection vs empty collection ─────────────────────────────────

    /// <summary>
    /// I-6 white area 2: "null" and "empty" are two values, not one, and nothing tested it.
    /// The assertion is double-barrelled on purpose: the ROUND TRIP must keep them apart, and
    /// so must the WIRE — a format whose null and empty serialize to the same bytes cannot
    /// preserve the distinction no matter what its reader does.
    /// </summary>
    [Test]
    public async Task Null_and_empty_collections_stay_different_values()
    {
        foreach (var tag in AllFormats)
        {
            var nullWire = SerializeNullable(tag, new NullableCollectionModel());
            var emptyWire = SerializeNullable(
                tag,
                new NullableCollectionModel { Ints = [], Map = [] }
            );

            await Assert
                .That(nullWire.SequenceEqual(emptyWire))
                .IsFalse()
                .Because(
                    $"{tag}: a null collection and an empty one must not serialize to the same "
                        + "wire, or no reader can tell them apart"
                );

            var backNull = DeserializeNullable(tag, nullWire);
            var backEmpty = DeserializeNullable(tag, emptyWire);

            await Assert.That(backNull!.Ints).IsNull().Because($"{tag}: null must stay null");
            await Assert
                .That(backEmpty!.Ints)
                .IsNotNull()
                .Because($"{tag}: an empty collection must not come back as null");
            await Assert.That(backEmpty.Ints!.Count).IsEqualTo(0);
            await Assert.That(backNull.Map).IsNull().Because($"{tag}: null must stay null");
            await Assert
                .That(backEmpty.Map)
                .IsNotNull()
                .Because($"{tag}: an empty map must not come back as null");
        }
    }

    /// <summary>
    /// I-6 white area 2, the string half: <c>null</c> and <c>""</c> are likewise two values.
    /// <b>Known limitation (INI)</b>: INI's line convention has one form for "key with an empty
    /// value" and none for "key with a null value", so <c>""</c> comes back as <c>null</c> —
    /// the same conflation spec §3 records for INI's other null shapes ("empty value and empty
    /// string are indistinguishable"). Reopen condition: an explicit null token in the INI
    /// value convention, which is a wire change and belongs to R4/R5, not to a test task.
    /// </summary>
    [Test]
    public async Task Null_and_empty_strings_stay_different_values_outside_ini()
    {
        foreach (var tag in AllFormats)
        {
            var back = DeserializeNullable(
                tag,
                SerializeNullable(tag, new NullableCollectionModel { Text = "" })
            );
            var backNull = DeserializeNullable(
                tag,
                SerializeNullable(tag, new NullableCollectionModel { Text = null })
            );

            await Assert
                .That(backNull!.Text)
                .IsNull()
                .Because($"{tag}: a null string must stay null");

            if (tag == "ini")
            {
                await Assert
                    .That(back!.Text)
                    .IsNull()
                    .Because(
                        "KNOWN LIMITATION (spec §3, INI): `Key = ` is one form for both \"\" and "
                            + "a null value, so INI cannot round-trip an empty string. This "
                            + "assertion pins the documented limit; the day the INI value "
                            + "convention grows a null token, it flips to IsEmpty"
                    );
                continue;
            }

            await Assert
                .That(back!.Text)
                .IsNotNull()
                .Because($"{tag}: an empty string must not come back as null");
            await Assert.That(back.Text).IsEmpty();
        }
    }

    // ── item 3: NaN throws but nothing diagnoses it ─────────────────────────────────

    /// <summary>
    /// I-6 white area 3: the JSON/TOML/YAML writers throw on <c>NaN</c> and no diagnostic is
    /// involved anywhere — which is correct-by-construction (the offending VALUE only exists
    /// at run time; a source generator cannot see it) and is why this test pins the two
    /// halves separately: the throw is the signal, and quiet generation is not a bug.
    /// <para>
    /// The three formats are in two classes: JSON has no NaN literal (a FACT, spec §3
    /// footnote ²), TOML/YAML do (footnote ³) so their throw is OUR debt — recorded, with the
    /// reopen condition (write the native <c>nan</c> literal) rather than hidden.
    /// </para>
    /// </summary>
    [Test]
    public async Task NaN_refuses_to_serialize_and_no_generator_diagnostic_could_have_said_so()
    {
        foreach (var tag in new[] { "json", "toml", "yaml" })
        {
            var thrown = Assert.Throws<ArgumentException>(() =>
                SerializeFloat(tag, new SpecialFloatModel { Dbl = double.NaN })
            );
            await Assert.That(thrown.Message).Contains("NaN");
        }

        foreach (var tag in new[] { "msgpack", "ini" })
        {
            var back = RoundTripFloat(tag, new SpecialFloatModel { Dbl = double.NaN });
            await Assert
                .That(double.IsNaN(back.Dbl))
                .IsTrue()
                .Because($"{tag} has a NaN form, so NaN must survive the round trip");
        }

        // The "only throws, never diagnoses" half: generation is silent about it, and that is
        // the correct answer — no source generator can see a value that exists at run time.
        var diagnostics = SilentDropDiagnosticTests.RunJson(
            """
            using PicoJetson;

            [PicoJsonSerializable]
            public partial class Doc
            {
                public double Dbl { get; set; }
            }
            """
        );

        await Assert
            .That(diagnostics.Messages.Where(m => m.Contains("NaN", StringComparison.Ordinal)))
            .IsEmpty()
            .Because(
                "a NaN cannot be diagnosed at generation time; pretending otherwise would be a "
                    + "diagnostic that never fires"
            );
    }

    // ── item 4: S14 — the whole member is dropped (and must say so) ─────────────────

    /// <summary>
    /// I-6 white area 4 / spec §3 S14: an object list with a null ELEMENT. Two of the three
    /// section formats still drop the whole member, and the point of this test is that the
    /// drop is DIAGNOSED rather than silent (I-1/I-7: a drop must answer both "did you tell
    /// anyone" and "what granularity").
    /// <para>
    /// YAML is the third case and the reason this test exists in this shape: R2 Task 1's
    /// footnote said YAML lost the member too, and it no longer does (the null literal landed
    /// in Task 4). Pinning the round trip keeps the spec's stale sentence from coming back.
    /// </para>
    /// </summary>
    [Test]
    public async Task S14_drops_are_diagnosed_and_yaml_no_longer_drops_the_member()
    {
        const string source = """
            using PicoToml;
            using PicoYaml;
            using PicoIni;

            public class Row
            {
                public string Name { get; set; } = "";
            }

            [PicoTomlSerializable]
            public partial class DocToml
            {
                public System.Collections.Generic.List<Row?> V { get; set; } = new();
            }

            [PicoYamlSerializable]
            public partial class DocYaml
            {
                public System.Collections.Generic.List<Row?> V { get; set; } = new();
            }

            [PicoIniSerializable]
            public partial class DocIni
            {
                public System.Collections.Generic.List<Row?> V { get; set; } = new();
            }
            """;

        foreach (
            var (tag, run) in new (string, (string[] Ids, string Generated, string[] Messages))[]
            {
                ("toml", SilentDropDiagnosticTests.RunToml(source)),
                ("ini", SilentDropDiagnosticTests.RunIni(source)),
            }
        )
        {
            var drop = run.Messages.FirstOrDefault(m =>
                m.Contains("V", StringComparison.Ordinal)
                && m.Contains(tag, StringComparison.Ordinal)
            );

            await Assert
                .That(drop)
                .IsNotNull()
                .Because(
                    $"{tag} drops the whole S14 member, so it must report it (I-1) — a silent "
                        + "drop is the class R2 exists to remove"
                );
            await Assert
                .That(run.Ids.Contains("PICOSERDE004") || run.Ids.Contains("PICOSERDE001"))
                .IsTrue()
                .Because(
                    $"the S14 drop in {tag} must carry a drop id: {string.Join(",", run.Ids)}"
                );
        }

        // yaml: no drop, and the null ELEMENT survives.
        var yamlRun = SilentDropDiagnosticTests.RunYaml(source);
        await Assert
            .That(yamlRun.Messages.Where(m => m.Contains("DocYaml.V", StringComparison.Ordinal)))
            .IsEmpty()
            .Because(
                "YAML can hold a null element (spec §3 S14 = ✅, and R2 Task 4 taught the writer "
                    + "`- null`), so dropping the member would be a silent loss: "
                    + string.Join(" | ", yamlRun.Messages)
            );

        var back = YamlSerializer.Deserialize<ObjectNullElementModel>(
            YamlSerializer.SerializeToUtf8Bytes(
                new ObjectNullElementModel
                {
                    V =
                    [
                        new BoundaryDto { Name = "a", N = 1 },
                        null,
                        new BoundaryDto { Name = "b" },
                    ],
                }
            )
        );

        await Assert.That(back!.V!.Count).IsEqualTo(3).Because("the null element must survive");
        await Assert.That(back.V[1]).IsNull();
        await Assert.That(back.V[2]!.Name).IsEqualTo("b");
    }

    // ── item 5: INI's default DateTime could not be read back ──────────────────────

    /// <summary>
    /// I-6 white area 5 / spec §3 S15 × ini, the <c>empty</c> value: <c>When</c> is
    /// <c>default(DateTime)</c>, whose canonical text is <c>0001-01-01T00:00:00.0000000</c>.
    /// The writer produced it and the reader REJECTED it, so every INI document with a
    /// default-valued DateTime member was unreadable.
    /// <para>
    /// The root cause is not the "missing Kind suffix" spec footnote ⁴ guessed: the reader
    /// parses with <c>Utf8Parser.TryParse(..., 'O')</c>, and that parser answers <c>false</c>
    /// for <c>DateTime.MinValue</c> in its own round-trip text (measured: <c>false</c> for
    /// <c>0001-01-01T00:00:00.0000000</c>, <c>true</c> for <c>2020-12-31T23:59:59.0000000</c> —
    /// same shape, different value). The read path now falls back to
    /// <c>DateTime.Parse(..., RoundtripKind)</c>, which accepts both.
    /// </para>
    /// </summary>
    [Test]
    public async Task Ini_reads_back_a_default_DateTime_member()
    {
        var model = new ScalarBoundaryModel { When = default };
        var bytes = IniSerializer.SerializeToUtf8Bytes(model);

        await Assert
            .That(Encoding.UTF8.GetString(bytes))
            .Contains("When = 0001-01-01T00:00:00.0000000")
            .Because("the writer's canonical form is the round-trip text, unchanged");

        var back = IniSerializer.Deserialize<ScalarBoundaryModel>(bytes);

        await Assert.That(back!.When).IsEqualTo(default(DateTime));
        await Assert.That(back.When.Kind).IsEqualTo(DateTimeKind.Unspecified);
    }

    // ── item 6: DateTimeKind.Unspecified ───────────────────────────────────────────

    /// <summary>
    /// I-6 white area 6: only <c>Utc</c> and <c>Local</c> values had ever been round-tripped;
    /// <c>Unspecified</c> — the Kind of every <c>default(DateTime)</c> and of every
    /// <c>DateTime.Parse("2020-12-31")</c> — was never driven. A format that "preserves the
    /// date" while quietly turning <c>Unspecified</c> into <c>Utc</c> changes the value (the
    /// ticks stay, the meaning does not), so all three kinds are asserted per format.
    /// </summary>
    [Test]
    [Arguments(DateTimeKind.Utc)]
    [Arguments(DateTimeKind.Local)]
    [Arguments(DateTimeKind.Unspecified)]
    public async Task Every_datetime_kind_survives_every_format(DateTimeKind kind)
    {
        var when = new DateTime(2026, 7, 8, 9, 10, 11, 123, kind).AddTicks(4567);

        foreach (var tag in AllFormats)
        {
            var back = RoundTripScalar(
                tag,
                new ScalarBoundaryModel
                {
                    When = when,
                    Span = TimeSpan.FromHours(3),
                    D = 1.5m,
                    Id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"),
                    Day = DayOfWeek.Friday,
                }
            );

            await Assert
                .That(back.When)
                .IsEqualTo(when)
                .Because($"{tag} must keep the ticks of a {kind} DateTime");
            await Assert
                .That(back.When.Kind)
                .IsEqualTo(kind)
                .Because(
                    $"{tag} must keep DateTimeKind.{kind}: the ticks alone do not carry the "
                        + "meaning, and a Kind rewrite silently changes the value"
                );
        }
    }

    // ── item 7: S15's scalar text contains separators ──────────────────────────────

    /// <summary>
    /// I-6 white area 7: S15's scalar family was only ever driven with "ordinary" values, so
    /// nothing pinned the members whose canonical text is full of the format's own separators
    /// — <c>decimal.MaxValue</c> (29 digits), <c>TimeSpan.MinValue</c>
    /// (<c>-10675199.02:48:05.4775808</c>: minus, colons, dot), a <c>Local</c> DateTime
    /// (<c>+08:00</c>: plus, colon), an all-<c>f</c> Guid (dashes) and an enum NAME. A format
    /// that splits on its own separator, re-quotes or re-scales any of these loses data
    /// quietly, so each member is asserted individually rather than as one blob.
    /// </summary>
    [Test]
    public async Task Scalar_text_with_separators_survives_every_format()
    {
        var model = new ScalarBoundaryModel
        {
            F = 1.5f,
            D = decimal.MaxValue,
            When = new DateTime(2026, 7, 8, 9, 10, 11, DateTimeKind.Local),
            Span = TimeSpan.MinValue,
            Id = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
            Day = DayOfWeek.Friday,
        };

        foreach (var tag in AllFormats)
        {
            var back = RoundTripScalar(tag, model);

            await Assert.That(back.F).IsEqualTo(model.F).Because($"{tag}: float");
            await Assert
                .That(back.D)
                .IsEqualTo(model.D)
                .Because($"{tag}: decimal.MaxValue must not lose its 29 digits");
            await Assert
                .That(back.Span)
                .IsEqualTo(model.Span)
                .Because($"{tag}: TimeSpan.MinValue is the separator-heavy one");
            await Assert
                .That(back.When)
                .IsEqualTo(model.When)
                .Because($"{tag}: a Local DateTime carries '+08:00' (or the local offset)");
            await Assert.That(back.When.Kind).IsEqualTo(DateTimeKind.Local);
            await Assert.That(back.Id).IsEqualTo(model.Id).Because($"{tag}: all-f Guid");
            await Assert
                .That(back.Day)
                .IsEqualTo(DayOfWeek.Friday)
                .Because($"{tag}: an enum is written by NAME, not by number");
        }
    }

    // ── harness ────────────────────────────────────────────────────────────────────

    private static byte[] SerializeFloat(string tag, SpecialFloatModel model) =>
        tag switch
        {
            "json" => JsonSerializer.SerializeToUtf8Bytes(model),
            "msgpack" => MsgPackSerializer.SerializeToUtf8Bytes(model),
            "toml" => TomlSerializer.SerializeToUtf8Bytes(model),
            "yaml" => YamlSerializer.SerializeToUtf8Bytes(model),
            _ => IniSerializer.SerializeToUtf8Bytes(model),
        };

    private static SpecialFloatModel RoundTripFloat(string tag, SpecialFloatModel model)
    {
        var bytes = SerializeFloat(tag, model);
        return tag switch
        {
            "json" => JsonSerializer.Deserialize<SpecialFloatModel>(bytes)!,
            "msgpack" => MsgPackSerializer.Deserialize<SpecialFloatModel>(bytes)!,
            "toml" => TomlSerializer.Deserialize<SpecialFloatModel>(bytes)!,
            "yaml" => YamlSerializer.Deserialize<SpecialFloatModel>(bytes)!,
            _ => IniSerializer.Deserialize<SpecialFloatModel>(bytes)!,
        };
    }

    private static SpecialFloatModel? SafeRoundTripFloat(string tag, SpecialFloatModel model)
    {
        try
        {
            return RoundTripFloat(tag, model);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static byte[] SerializeNullable(string tag, NullableCollectionModel model) =>
        tag switch
        {
            "json" => JsonSerializer.SerializeToUtf8Bytes(model),
            "msgpack" => MsgPackSerializer.SerializeToUtf8Bytes(model),
            "toml" => TomlSerializer.SerializeToUtf8Bytes(model),
            "yaml" => YamlSerializer.SerializeToUtf8Bytes(model),
            _ => IniSerializer.SerializeToUtf8Bytes(model),
        };

    private static NullableCollectionModel DeserializeNullable(string tag, byte[] bytes) =>
        tag switch
        {
            "json" => JsonSerializer.Deserialize<NullableCollectionModel>(bytes)!,
            "msgpack" => MsgPackSerializer.Deserialize<NullableCollectionModel>(bytes)!,
            "toml" => TomlSerializer.Deserialize<NullableCollectionModel>(bytes)!,
            "yaml" => YamlSerializer.Deserialize<NullableCollectionModel>(bytes)!,
            _ => IniSerializer.Deserialize<NullableCollectionModel>(bytes)!,
        };

    private static ScalarBoundaryModel RoundTripScalar(string tag, ScalarBoundaryModel model)
    {
        var bytes = tag switch
        {
            "json" => JsonSerializer.SerializeToUtf8Bytes(model),
            "msgpack" => MsgPackSerializer.SerializeToUtf8Bytes(model),
            "toml" => TomlSerializer.SerializeToUtf8Bytes(model),
            "yaml" => YamlSerializer.SerializeToUtf8Bytes(model),
            _ => IniSerializer.SerializeToUtf8Bytes(model),
        };
        return tag switch
        {
            "json" => JsonSerializer.Deserialize<ScalarBoundaryModel>(bytes)!,
            "msgpack" => MsgPackSerializer.Deserialize<ScalarBoundaryModel>(bytes)!,
            "toml" => TomlSerializer.Deserialize<ScalarBoundaryModel>(bytes)!,
            "yaml" => YamlSerializer.Deserialize<ScalarBoundaryModel>(bytes)!,
            _ => IniSerializer.Deserialize<ScalarBoundaryModel>(bytes)!,
        };
    }
}
