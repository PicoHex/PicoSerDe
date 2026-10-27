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

/// <summary>The one member S15's model never had: a STRING, driven with its own separators.</summary>
[PicoSerializable]
public class ScalarStringModel
{
    public string Text { get; set; } = "";
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

    /// <summary>
    /// The one format whose DateTime encoding cannot carry a <see cref="DateTimeKind"/>:
    /// MessagePack's timestamp extension is seconds + nanoseconds since the epoch.
    /// </summary>
    private const string InstantOnlyFormat = "msgpack";

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
    /// <b>⛔ FORMAT FACT</b>: JSON refuses <c>±Infinity</c> because it has no literal for it
    /// (spec §3 footnote ²; R1′ 「特殊浮点仅在格式有字面量时可表示」). Nothing here is our gap,
    /// so there is nothing to reopen and nothing to schedule.
    /// <para>
    /// Pinning the refusal is the point: the failure mode is an exception at the call site, and
    /// a silent rewrite to <c>double.MaxValue</c> would be data corruption nobody would notice.
    /// </para>
    /// </summary>
    [Test]
    [Arguments("json")]
    public async Task Infinity_is_refused_by_json_because_the_format_has_no_literal(string tag)
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

    /// <summary>
    /// <b>⏸ OUR DEBT, not a format fact</b>: TOML and YAML <i>can</i> hold <c>±Infinity</c>
    /// (<c>inf</c>/<c>-inf</c> parse in `tomllib` and PyYAML — spec §3 footnote ³), so their
    /// refusal today is our writer missing the literal. The reopen condition is exactly that:
    /// emit the native literal, accept it on the read side.
    /// <para>
    /// It is a separate test, with "today" and "our writer" in its name, on purpose: the same
    /// sentence D-10 removed from the generator's diagnostic ("cannot be serialized by
    /// {format}", said of a format that can) had a second home in this file's test NAME. A
    /// reader grepping a screen of green names must not come away believing TOML/YAML have no
    /// infinity concept — so JSON's fact and this debt never share a name or a loop again.
    /// Assertions are byte-for-byte the same two as the ⛔ test: this is a 口径 fix, not a
    /// weaker or a stronger one.
    /// </para>
    /// </summary>
    [Test]
    [Arguments("toml")]
    [Arguments("yaml")]
    public async Task Infinity_is_refused_by_toml_and_yaml_today_because_our_writer_has_no_inf_literal_yet(
        string tag
    )
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
    /// I-6 white area 3, half one: every writer that refuses <c>NaN</c> refuses it LOUDLY
    /// (an <c>ArgumentException</c> naming what it refused) and no diagnostic is involved
    /// anywhere — correct-by-construction, because the offending VALUE only exists at run time
    /// and a source generator cannot see it. That is why the throw and the silence are pinned
    /// separately: the throw is the signal, and quiet generation is not a bug.
    /// <para>
    /// The three refusing formats are in TWO classes and no longer share a loop:
    /// JSON has no NaN literal (<b>⛔ FACT</b>, spec §3 footnote ² — nothing to reopen);
    /// TOML/YAML do (<b>⏸ OUR DEBT</b>, footnote ³ — reopen condition: write the native
    /// <c>nan</c> literal and accept it on the read side). A single loop over
    /// <c>{ "json", "toml", "yaml" }</c> under one "the format refuses NaN" name was exactly
    /// the C1 mirror D-10 was fixed for, so the debt class now lives in its own test whose
    /// name says whose gap it is.
    /// </para>
    /// </summary>
    [Test]
    public async Task NaN_is_refused_by_json_because_the_format_has_no_literal()
    {
        const string tag = "json";
        var thrown = Assert.Throws<ArgumentException>(() =>
            SerializeFloat(tag, new SpecialFloatModel { Dbl = double.NaN })
        );
        await Assert.That(thrown.Message).Contains("NaN");

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

    /// <summary>
    /// I-6 white area 3, half two — <b>⏸ OUR DEBT, not a format fact</b>: TOML and YAML both
    /// have a NaN literal (<c>nan</c> / <c>.nan</c>, spec §3 footnote ³), so refusing it today
    /// is our writer's gap. The assertion is identical to the ⛔ test's (an
    /// <c>ArgumentException</c> whose message names <c>NaN</c>): the refusal is still a loud
    /// failure rather than a silent rewrite, it is only the ownership that differs — and this
    /// name may not be read as "TOML/YAML cannot express NaN".
    /// </summary>
    [Test]
    public async Task NaN_is_refused_by_toml_and_yaml_today_because_our_writer_has_no_nan_literal_yet()
    {
        foreach (var tag in new[] { "toml", "yaml" })
        {
            var thrown = Assert.Throws<ArgumentException>(() =>
                SerializeFloat(tag, new SpecialFloatModel { Dbl = double.NaN })
            );
            await Assert.That(thrown.Message).Contains("NaN");
        }
    }

    /// <summary>
    /// I-6 white area 3, the other side: the two formats that DO carry <c>NaN</c> must keep
    /// carrying it (msgpack's float64 encoding, INI's IEEE text — spec §3 footnote ⁴).
    /// </summary>
    [Test]
    public async Task NaN_round_trips_where_the_format_has_a_nan_form()
    {
        foreach (var tag in new[] { "msgpack", "ini" })
        {
            var back = RoundTripFloat(tag, new SpecialFloatModel { Dbl = double.NaN });
            await Assert
                .That(double.IsNaN(back.Dbl))
                .IsTrue()
                .Because($"{tag} has a NaN form, so NaN must survive the round trip");
        }
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

    /// <summary>
    /// I-6 white area 5, the ROOT CAUSE, pinned so the fallback above cannot be "simplified"
    /// away later: <c>Utf8Parser.TryParse(…, 'O')</c> answers <c>false</c> for
    /// <c>DateTime.MinValue</c>'s own round-trip text while accepting the identical SHAPE for
    /// another value. That is why the INI read path needs
    /// <c>DateTime.Parse(…, RoundtripKind)</c> behind it — not a "missing Kind suffix", which is
    /// what spec §3 footnote ⁴ guessed before it was measured.
    /// </summary>
    [Test]
    public async Task Utf8Parser_round_trip_format_rejects_DateTime_MinValue()
    {
        var min = "0001-01-01T00:00:00.0000000"u8;
        var ordinary = "2020-12-31T23:59:59.0000000"u8;

        var minParsed = System.Buffers.Text.Utf8Parser.TryParse(
            min,
            out DateTime minValue,
            out _,
            'O'
        );
        var ordinaryParsed = System.Buffers.Text.Utf8Parser.TryParse(
            ordinary,
            out DateTime ordinaryValue,
            out _,
            'O'
        );

        await Assert
            .That(minParsed)
            .IsFalse()
            .Because(
                "this false is the whole bug: the text is the writer's own canonical form for "
                    + "default(DateTime)"
            );
        await Assert
            .That(ordinaryParsed)
            .IsTrue()
            .Because(
                "the same SHAPE parses for another value — so it is the value, not the format"
            );
        await Assert.That(minValue).IsEqualTo(default(DateTime));
        await Assert.That(ordinaryValue.Year).IsEqualTo(2020);
    }

    // ── item 6: DateTimeKind.Unspecified ───────────────────────────────────────────

    /// <summary>
    /// I-6 white area 6: only <c>Utc</c> and <c>Local</c> values had ever been round-tripped;
    /// <c>Unspecified</c> — the Kind of every <c>default(DateTime)</c> and of every
    /// <c>DateTime.Parse("2020-12-31")</c> — was never driven. A format that "preserves the
    /// date" while quietly turning <c>Unspecified</c> into <c>Utc</c> changes the value (the
    /// ticks stay, the meaning does not), so all three kinds are asserted per format.
    /// <para>
    /// <b>Msgpack is the documented exception</b>: a MessagePack <c>DateTime</c> is the
    /// timestamp extension (tag <c>0xFF</c>, spec §"Timestamp extension type"), whose payload
    /// is seconds + nanoseconds since the epoch and <em>nothing else</em> — the wire has no
    /// Kind bit to preserve. What it does carry is the instant: <c>Utc</c> keeps its ticks,
    /// <c>Unspecified</c> is taken at face value (see
    /// <see cref="PicoMsgPack.MsgPackWriter.WriteDateTime"/>), <c>Local</c> is converted, and
    /// everything comes back as <c>Utc</c> — the same contract MessagePack-CSharp has.
    /// </para>
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

            if (tag == InstantOnlyFormat)
            {
                // The timestamp extension carries an instant: Local is converted on the way
                // out, Unspecified is taken at face value (as UTC), and the Kind comes back
                // as Utc because the wire cannot spell one.
                var expectedTicks =
                    kind == DateTimeKind.Local ? when.ToUniversalTime().Ticks : when.Ticks;
                await Assert
                    .That(back.When.Ticks)
                    .IsEqualTo(expectedTicks)
                    .Because(
                        $"{tag} writes the timestamp extension: the instant of a {kind} value "
                            + "survives, its Kind does not"
                    );
                await Assert
                    .That(back.When.Kind)
                    .IsEqualTo(DateTimeKind.Utc)
                    .Because(
                        $"{tag}: the timestamp extension has no Kind bit, so it reads back as Utc"
                    );
                continue;
            }

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
            if (tag == InstantOnlyFormat)
            {
                // Msgpack's timestamp extension normalises a Local value to UTC; the other
                // four formats spell the offset out in their text form.
                await Assert
                    .That(back.When.Ticks)
                    .IsEqualTo(model.When.ToUniversalTime().Ticks)
                    .Because($"{tag}: the timestamp extension carries the instant, not the offset");
                await Assert
                    .That(back.When.Kind)
                    .IsEqualTo(DateTimeKind.Utc)
                    .Because($"{tag}: kind");
            }
            else
            {
                await Assert
                    .That(back.When)
                    .IsEqualTo(model.When)
                    .Because($"{tag}: a Local DateTime carries '+08:00' (or the local offset)");
                await Assert.That(back.When.Kind).IsEqualTo(DateTimeKind.Local);
            }
            await Assert.That(back.Id).IsEqualTo(model.Id).Because($"{tag}: all-f Guid");
            await Assert
                .That(back.Day)
                .IsEqualTo(DayOfWeek.Friday)
                .Because($"{tag}: an enum is written by NAME, not by number");
        }
    }

    // ── item 7, the STRING half: separators, quotes, newlines ─────────────────────

    /// <summary>
    /// I-6 white area 7 as R1 actually worded it — "含分隔符/引号/换行的字符串未在 S15 覆盖（S15
    /// 六成员无 `string`）". The decimal/TimeSpan/Guid/enum texts above cover the separator family
    /// for S15's own members; a real <c>string</c> member is the other half, and it is the half
    /// where the format's own punctuation (<c>:</c> <c>,</c> <c>=</c> <c>#</c> <c>[</c> <c>]</c>,
    /// quotes, TAB) appears inside the VALUE. Four formats carry all of it; INI does not — see the
    /// three tests below, each of which pins one measured INI limit instead of pretending the
    /// round trip works.
    /// </summary>
    [Test]
    [Arguments("json")]
    [Arguments("msgpack")]
    [Arguments("toml")]
    [Arguments("yaml")]
    public async Task Separator_heavy_strings_round_trip_outside_ini(string tag)
    {
        foreach (var text in DangerousStrings)
        {
            var back = RoundTripString(tag, text);
            await Assert
                .That(back.Text)
                .IsEqualTo(text)
                .Because($"{tag} must carry the string {Escape(text)} byte for byte");
        }
    }

    /// <summary>
    /// <b>Known limitation (INI), value half — spec §8.1 V-2.</b> INI's line convention has no
    /// VALUE escaping, so a value containing <c>\n</c>/<c>\r</c> is written as a cut line and the
    /// reader then rejects the document: <c>FormatException: Invalid INI line: expected '='</c>.
    /// It is loud (the document cannot be read at all — not silent corruption), but it is also a
    /// write/read asymmetry: the writer should not produce a line the reader refuses. Fixing it
    /// means a value-escaping convention — a public wire change, registered in §8.1, not this
    /// task. Reopen condition: INI value escaping lands ⇒ this test flips to "round trip".
    /// </summary>
    [Test]
    [Arguments("line\nbreak")]
    [Arguments("cr\rhere")]
    public async Task Ini_cannot_carry_a_newline_in_a_value(string text)
    {
        var bytes = IniSerializer.SerializeToUtf8Bytes(new ScalarStringModel { Text = text });

        await Assert
            .That(Encoding.UTF8.GetString(bytes))
            .Contains("\n")
            .Because("the writer emits the newline raw — no escaping exists on this side either");

        var thrown = Assert.Throws<FormatException>(() =>
            IniSerializer.Deserialize<ScalarStringModel>(bytes)
        );
        await Assert
            .That(thrown.Message)
            .Contains("Invalid INI line")
            .Because(
                "KNOWN LIMITATION: the failure must at least be loud and name the shape; the day "
                    + "INI grows value escaping this test flips to a round trip"
            );
    }

    /// <summary>
    /// <b>Known limitation (INI), value half — spec §8.1 V-3.</b> The reader trims a value's
    /// surrounding spaces, so <c>" pad "</c> comes back as <c>"pad"</c>: a SILENT value change
    /// (no exception, no diagnostic). Same family as V-2 (no value escaping), same reopen
    /// condition.
    /// </summary>
    [Test]
    public async Task Ini_trims_surrounding_spaces_in_a_value()
    {
        const string text = " pad ";
        var back = IniSerializer.Deserialize<ScalarStringModel>(
            IniSerializer.SerializeToUtf8Bytes(new ScalarStringModel { Text = text })
        );

        await Assert
            .That(back!.Text)
            .IsEqualTo("pad")
            .Because(
                "KNOWN LIMITATION: INI has no value quoting, so surrounding whitespace is lost "
                    + "silently — asserted rather than wished away, per §8.1 V-3"
            );
    }

    /// <summary>
    /// <b>Known limitation (INI), value half — spec §8.1 V-4.</b> A value that STARTS with
    /// <c>#</c> makes the reader treat the whole line as a comment, so the member disappears and
    /// comes back as <c>null</c> — the worst shape in this file, because it is silent and it
    /// loses a value the document plainly contains. A <c>;</c> inside the value truncates it.
    /// (A <c>#</c> in the MIDDLE of a value is fine — measured — so this is a rule about the
    /// comment marker's position, not "any hash".)
    /// </summary>
    [Test]
    [Arguments("#hash", null)]
    [Arguments("a ; b", "a")]
    public async Task Ini_drops_a_value_that_starts_with_a_comment_marker(
        string text,
        string? expected
    )
    {
        var back = IniSerializer.Deserialize<ScalarStringModel>(
            IniSerializer.SerializeToUtf8Bytes(new ScalarStringModel { Text = text })
        );

        await Assert
            .That(back!.Text)
            .IsEqualTo(expected)
            .Because(
                "KNOWN LIMITATION: a leading `#` comments the line out (member lost ⇒ null) and an "
                    + "embedded `;` starts an inline comment (value truncated). Both are silent; "
                    + "§8.1 V-4 records them and the reopen condition"
            );
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

    private static readonly string[] DangerousStrings =
    [
        "a,b=c",
        "q\"uote'",
        "line\nbreak",
        "cr\rhere",
        "tab\there",
        "#hash",
        "[bracket]",
        "x = y",
        "\u00fcn\u00efcod\u00e9",
    ];

    private static ScalarStringModel RoundTripString(string tag, string text)
    {
        var model = new ScalarStringModel { Text = text };
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
            "json" => JsonSerializer.Deserialize<ScalarStringModel>(bytes)!,
            "msgpack" => MsgPackSerializer.Deserialize<ScalarStringModel>(bytes)!,
            "toml" => TomlSerializer.Deserialize<ScalarStringModel>(bytes)!,
            "yaml" => YamlSerializer.Deserialize<ScalarStringModel>(bytes)!,
            _ => IniSerializer.Deserialize<ScalarStringModel>(bytes)!,
        };
    }

    private static string Escape(string s) =>
        s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
}
