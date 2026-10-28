using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PicoJetson.Gen;

namespace PicoJetson.Tests;

// ── Fixtures ─────────────────────────────────────────────────────────────────────────────────
//
// One of each shape the emitted read has to answer: a plain class (the streaming path every class
// deserializer runs), a struct (the direct path, and the only way to observe "zero allocations"
// without the result object's own allocation in the way), and enums whose underlying type is wide
// enough that a truncating cast would be visible.

public class DateTimeEnumReadModel
{
    public DateTime CreatedAt { get; set; }
    public DayOfWeek Day { get; set; }
}

public struct DateTimeStructModel
{
    public DateTime CreatedAt { get; set; }
}

public struct EnumStructModel
{
    public DayOfWeek Day { get; set; }
}

public enum WideULongDay : ulong
{
    Zero = 0,
    Big = 18446744073709551615,
}

public enum NarrowByteDay : byte
{
    Zero = 0,
    TwoHundred = 200,
}

public class WideEnumReadModel
{
    public WideULongDay Wide { get; set; }
    public NarrowByteDay Narrow { get; set; }
}

/// <summary>
/// The <c>DateTime</c> / <c>enum</c> read path holds no string round trip: the payload our own writer
/// produces (ISO-8601 <c>O</c>, optionally with a trailing <c>Z</c>) is answered by
/// <see cref="System.Buffers.Text.Utf8Parser"/> in one span parse, and anything else falls back to the
/// same parser the string overload runs, on a <c>char</c> span.
/// <para>
/// <b>Why the behaviour locks cannot be red on their own.</b> The task changes no behaviour by design,
/// so every assertion below is green before the emitter is touched. The red has to come from the new
/// path itself — make it answer a wrong value, drop the time-zone handling, or widen the guard past the
/// shapes it is equivalent for, and these go red. That negative control is what
/// <see cref="DateTime_Payloads_MatchTheReferenceParser_ValueAndKind"/>,
/// <see cref="Enum_NumericGuard_RejectsWhatTheReferenceRejects"/> and
/// <see cref="DateTime_MaxValue_WithAnOffset_IsStillAnError"/> exist for; the emission guard
/// (<see cref="EmittedDateTimeRead_UsesTheSpanParser_AndAllocatesNoString"/>) is the one that pins the
/// shape itself on the checked-in artefact.
/// </para>
/// </summary>
public class JsonScalarReadFastPathTests
{
    // ── DateTime behaviour ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every payload is answered exactly as <c>DateTime.TryParse(text, InvariantCulture,
    /// RoundtripKind)</c> answers it: same <c>Ticks</c>, same <c>Kind</c>, and on failure the same
    /// exception type with the same message (offset included).
    /// </summary>
    public static IEnumerable<string> DateTimePayloads() =>
        [
            // our own writer's output, all three Kinds
            "2026-10-27T12:00:00.0000000Z",
            "2026-10-27T12:00:00.1234567Z",
            "2026-10-27T12:00:00.9999999Z",
            "2026-10-27T12:00:00.0000000",
            "2026-10-27T12:00:00.1234567",
            "2026-10-27T12:00:00.0000000+08:00",
            "2026-10-27T12:00:00.0000000-05:30",
            "0001-01-01T00:00:00.0000000",
            "9999-12-31T23:59:59.9999999Z",
            // the 's' shape and other fraction widths the fast path must decline
            "2026-10-27T12:00:00Z",
            "2026-10-27T12:00:00",
            "2026-10-27T12:00:00.123",
            "2026-10-27T12:00:00.123456789Z",
            "2026-10-27T12:00:00.1234567 ",
            " 2026-10-27T12:00:00.1234567Z",
            // shapes only the reference parser accepts
            "2026-10-27",
            "2026-10-27 12:00:00",
            "10/27/2026",
            "Tue, 27 Oct 2026 12:00:00 GMT",
            // errors, including the two edges where Utf8Parser clamps and the reference fails
            "",
            " ",
            "not a date",
            "2026-13-01T00:00:00.0000000Z",
            "2026-02-30T00:00:00.0000000Z",
            "9999-12-31T12:00:00.0000000-05:30",
            "9999-12-31T23:59:59.9999999+02:00",
        ];

    [Test]
    public async Task DateTime_Payloads_MatchTheReferenceParser_ValueAndKind()
    {
        foreach (var payload in DateTimePayloads())
        {
            var jsonText = $"{{\"CreatedAt\":\"{payload}\"}}";
            var json = Encoding.UTF8.GetBytes(jsonText);
            var expected = DateTime.TryParse(
                payload,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _
            );

            if (expected)
            {
                var model = JsonSerializer.Deserialize<DateTimeEnumReadModel>(json)!;
                var reference = DateTime.Parse(
                    payload,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind
                );
                await Assert
                    .That(model.CreatedAt)
                    .IsEqualTo(reference)
                    .Because(
                        $"payload '{payload}' must read back as {reference:O} ({reference.Kind})"
                    );
                await Assert.That(model.CreatedAt.Kind).IsEqualTo(reference.Kind);
            }
            else
            {
                var ex = Assert.Throws<FormatException>(() =>
                    JsonSerializer.Deserialize<DateTimeEnumReadModel>(json)
                );
                // The message is built from the property's JSON name and the reader offset, so it is
                // the exact pair the old string path produced — offset included.
                await Assert
                    .That(ex!.Message)
                    .IsEqualTo(
                        "Expected a DateTime value for property \"CreatedAt\" at offset"
                            + $" {Encoding.UTF8.GetByteCount(jsonText) - 1}"
                    )
                    .Because($"payload '{payload}' must fail the way it always did");
            }
        }
    }

    /// <summary>The <c>Kind</c> contract, spelled out: <c>Z</c> → Utc, no designator → Unspecified, offset → Local.</summary>
    [Test]
    public async Task DateTime_Kind_FollowsTheDesignator()
    {
        var utc = JsonSerializer.Deserialize<DateTimeEnumReadModel>(
            """{"CreatedAt":"2026-10-27T12:00:00.1234567Z"}"""u8.ToArray()
        )!;
        await Assert.That(utc.CreatedAt.Kind).IsEqualTo(DateTimeKind.Utc);
        await Assert
            .That(utc.CreatedAt)
            .IsEqualTo(new DateTime(2026, 10, 27, 12, 0, 0, 123, DateTimeKind.Utc).AddTicks(4567));

        var unspecified = JsonSerializer.Deserialize<DateTimeEnumReadModel>(
            """{"CreatedAt":"2026-10-27T12:00:00.1234567"}"""u8.ToArray()
        )!;
        await Assert.That(unspecified.CreatedAt.Kind).IsEqualTo(DateTimeKind.Unspecified);
        await Assert
            .That(unspecified.CreatedAt.Ticks)
            .IsEqualTo(new DateTime(2026, 10, 27, 12, 0, 0).Ticks + 1234567);

        var local = JsonSerializer.Deserialize<DateTimeEnumReadModel>(
            """{"CreatedAt":"2026-10-27T12:00:00.0000000+02:00"}"""u8.ToArray()
        )!;
        await Assert.That(local.CreatedAt.Kind).IsEqualTo(DateTimeKind.Local);
        await Assert
            .That(local.CreatedAt)
            .IsEqualTo(
                DateTime.Parse(
                    "2026-10-27T12:00:00.0000000+02:00",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind
                )
            );
    }

    /// <summary>
    /// The boundary the fast path must <b>decline</b>: with an explicit offset, <c>Utf8Parser</c>
    /// clamps at <c>DateTime.MaxValue</c> and answers <c>true</c>, while the reference parser fails.
    /// A widened guard (no length check) turns this into a silent wrong value.
    /// </summary>
    [Test]
    public async Task DateTime_MaxValue_WithAnOffset_IsStillAnError()
    {
        var ex = Assert.Throws<FormatException>(() =>
            JsonSerializer.Deserialize<DateTimeEnumReadModel>(
                """{"CreatedAt":"9999-12-31T12:00:00.1234567-05:30"}"""u8.ToArray()
            )
        );
        await Assert.That(ex!.Message).Contains("Expected a DateTime value for property");
    }

    /// <summary>Our writer's output for each <c>Kind</c> must survive a round trip, Kind included.</summary>
    [Test]
    [Arguments(DateTimeKind.Utc)]
    [Arguments(DateTimeKind.Local)]
    [Arguments(DateTimeKind.Unspecified)]
    public async Task DateTime_OwnWriterOutput_RoundTrips(DateTimeKind kind)
    {
        var original = new DateTime(2026, 10, 27, 12, 34, 56, 789, kind).AddTicks(1234);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new DateTimeEnumReadModel { CreatedAt = original, Day = DayOfWeek.Friday }
        );
        var model = JsonSerializer.Deserialize<DateTimeEnumReadModel>(bytes)!;
        await Assert.That(model.CreatedAt).IsEqualTo(original);
        await Assert.That(model.CreatedAt.Kind).IsEqualTo(kind);
    }

    /// <summary>A chunk boundary inside the value must not change the answer (the streaming rewind path).</summary>
    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(7)]
    [Arguments(64)]
    public async Task DateTime_AndEnum_AcrossChunkedStreams(int chunkSize)
    {
        var json = Encoding.UTF8.GetBytes(
            """{"CreatedAt":"2026-10-27T12:00:00.1234567Z","Day":5}"""
        );
        using var stream = new ChunkyStream(json, chunkSize);
        var model = await JsonSerializer.DeserializeFromStreamAsync<DateTimeEnumReadModel>(stream);
        await Assert.That(model!.CreatedAt.Kind).IsEqualTo(DateTimeKind.Utc);
        await Assert
            .That(model.CreatedAt.Ticks)
            .IsEqualTo(new DateTime(2026, 10, 27, 12, 0, 0).Ticks + 1234567);
        await Assert.That(model.Day).IsEqualTo(DayOfWeek.Friday);
    }

    // ── enum behaviour ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Raw JSON payloads for <c>Day</c>, both as a number (what we write) and as a string (what an
    /// older or hand-written document may carry). The reference is
    /// <see cref="Enum.TryParse{TEnum}(string, out TEnum)"/> on the same text, so the fallback keeps
    /// every legacy form working.
    /// </summary>
    public static IEnumerable<(string Json, string Text)> DayPayloads() =>
        [
            ("5", "5"),
            ("0", "0"),
            ("9", "9"),
            ("-1", "-1"),
            ("\"007\"", "007"),
            ("2147483647", "2147483647"),
            ("2147483648", "2147483648"),
            ("-2147483648", "-2147483648"),
            ("\"5\"", "5"),
            ("\"Friday\"", "Friday"),
            ("\"friday\"", "friday"),
            ("\"FRIDAY\"", "FRIDAY"),
            ("\" 5\"", " 5"),
            ("\"5 \"", "5 "),
            ("\"Friday,Tuesday\"", "Friday,Tuesday"),
            ("\"Funday\"", "Funday"),
            ("5.0", "5.0"),
            ("\"\"", ""),
        ];

    [Test]
    public async Task Enum_Payloads_MatchEnumTryParse()
    {
        foreach (var (jsonValue, text) in DayPayloads())
        {
            var jsonText = $"{{\"Day\":{jsonValue}}}";
            var json = Encoding.UTF8.GetBytes(jsonText);
            if (Enum.TryParse<DayOfWeek>(text, out var expected))
            {
                var model = JsonSerializer.Deserialize<DateTimeEnumReadModel>(json)!;
                await Assert
                    .That(model.Day)
                    .IsEqualTo(expected)
                    .Because($"payload {jsonValue} must parse as the reference does");
            }
            else
            {
                var ex = Assert.Throws<FormatException>(() =>
                    JsonSerializer.Deserialize<DateTimeEnumReadModel>(json)
                );
                await Assert
                    .That(ex!.Message)
                    .IsEqualTo(
                        "Expected an enum value value for property \"Day\" at offset"
                            + $" {Encoding.UTF8.GetByteCount(jsonText) - 1}"
                    )
                    .Because($"payload {jsonValue} must fail the way it always did");
            }
        }
    }

    /// <summary>
    /// The numeric guard must be exact for every underlying type: a value that does not fit the enum's
    /// own width is rejected by the reference parser, so it must be rejected here too — the round-trip
    /// check plus the no-sign rule are what make that true.
    /// </summary>
    [Test]
    public async Task Enum_NumericGuard_RejectsWhatTheReferenceRejects()
    {
        // ulong-backed: a negative payload must not survive as 18446744073709551615
        Assert.Throws<FormatException>(() =>
            JsonSerializer.Deserialize<WideEnumReadModel>("""{"Wide":"-1"}"""u8.ToArray())
        );
        Assert.Throws<FormatException>(() =>
            JsonSerializer.Deserialize<WideEnumReadModel>("""{"Narrow":300}"""u8.ToArray())
        );
        Assert.Throws<FormatException>(() =>
            JsonSerializer.Deserialize<WideEnumReadModel>("""{"Narrow":"-1"}"""u8.ToArray())
        );
        // > long.MaxValue only reaches the reader as a *string* (a JSON number of that size is a
        // UInt64 token, which the enum token check has never accepted — unchanged by this task).
        var wide = JsonSerializer.Deserialize<WideEnumReadModel>(
            """{"Wide":"18446744073709551615","Narrow":200}"""u8.ToArray()
        )!;
        await Assert.That(wide.Wide).IsEqualTo(WideULongDay.Big);
        await Assert.That(wide.Narrow).IsEqualTo(NarrowByteDay.TwoHundred);
    }

    // ── allocation ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The point of the change: a struct model has no result object to allocate, so anything the read
    /// allocates is the read's own — a materialised string shows up here immediately.
    /// </summary>
    [Test]
    public async Task DateTimeAndEnumRead_AllocatesNothing()
    {
        var dtJson = """{"CreatedAt":"2026-10-27T12:00:00.1234567Z"}"""u8.ToArray();
        var enumJson = """{"Day":5}"""u8.ToArray();

        // warm the static registration + any first-call caches out of the measurement
        _ = JsonSerializer.Deserialize<DateTimeStructModel>(dtJson);
        _ = JsonSerializer.Deserialize<EnumStructModel>(enumJson);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            _ = JsonSerializer.Deserialize<DateTimeStructModel>(dtJson);
            _ = JsonSerializer.Deserialize<EnumStructModel>(enumJson);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert
            .That(allocated)
            .IsEqualTo(0L)
            .Because("100 DateTime + 100 enum reads on struct models are the read path alone");
    }

    // ── emission guards ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The emitted artifact, not the runtime: the class deserializer's <c>DateTime</c> and <c>enum</c>
    /// reads must go through the span parsers and must not call
    /// <c>Encoding.UTF8.GetString(reader.GetStringRaw())</c>.
    /// </summary>
    [Test]
    public async Task EmittedDateTimeRead_UsesTheSpanParser_AndAllocatesNoString()
    {
        var emitted = Emit(
            """
            using PicoJetson;
            namespace FastPathEmit.DateTimeOnly;

            [PicoJsonSerializable]
            public class DateTimeOnlyPoco
            {
                public System.DateTime CreatedAt { get; set; }
            }
            """
        );
        var text = string.Join("\n", emitted);

        await Assert
            .That(text)
            .Contains(
                "System.Buffers.Text.Utf8Parser.TryParse(__rawBytes, out __dt_CreatedAt, out"
            );
        await Assert.That(text).Contains("'O'");
        await Assert.That(text).Contains("stackalloc char[64]");
        await Assert.That(text).Contains("__dtUsed_CreatedAt == __rawBytes.Length");
        // the old unconditional materialisation is gone; the only GetString left is the >64-byte
        // escape hatch inside the fallback, which is not a DateTime shape we write
        await Assert
            .That(text)
            .DoesNotContain("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
        await Assert.That(text).DoesNotContain("__strValue");
    }

    /// <summary>Same for the enum: numeric fast path, span fallback, no string materialisation.</summary>
    [Test]
    public async Task EmittedEnumRead_UsesTheNumericFastPath()
    {
        var emitted = Emit(
            """
            using PicoJetson;
            namespace FastPathEmit.EnumOnly;

            [PicoJsonSerializable]
            public class EnumOnlyPoco
            {
                public System.DayOfWeek Day { get; set; }
            }
            """
        );
        var text = string.Join("\n", emitted);

        await Assert
            .That(text)
            .Contains(
                "System.Buffers.Text.Utf8Parser.TryParse(__rawBytes, out long __enumNumber, out var __enumUsed_Day"
            );
        await Assert.That(text).Contains("unchecked((global::System.DayOfWeek)__enumNumber)");
        await Assert.That(text).Contains("unchecked((long)__enumCast_Day) == __enumNumber");
        await Assert.That(text).Contains("System.Enum.TryParse<global::System.DayOfWeek>");
        await Assert.That(text).Contains("stackalloc char[64]");
        await Assert
            .That(text)
            .DoesNotContain("var __strValue = System.Text.Encoding.UTF8.GetString(__rawBytes);");
        await Assert.That(text).DoesNotContain("__strValue");
    }

    /// <summary>
    /// The <c>[DateTimeFormat]</c> branch keeps <c>TryParseExact</c> and its string: the fast path is
    /// only equivalent for the round-trip shape, and an explicit format is the caller asking for a
    /// different one.
    /// </summary>
    [Test]
    public async Task EmittedDateTimeFormat_KeepsTryParseExact()
    {
        var emitted = Emit(
            """
            using PicoJetson;
            namespace FastPathEmit.DateTimeFormatted;

            [PicoJsonSerializable]
            public class DateTimeFormattedPoco
            {
                [DateTimeFormat("yyyy-MM-dd")]
                public System.DateTime Date { get; set; }
            }
            """
        );
        var text = string.Join("\n", emitted);
        await Assert.That(text).Contains("System.DateTime.TryParseExact(");
        await Assert.That(text).DoesNotContain("Utf8Parser.TryParse(__rawBytes");
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────

    static string[] Emit(string source)
    {
        var compilation = CSharpCompilation.Create(
            "JsonFastPathEmitAsm",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        var driver = CSharpGeneratorDriver.Create(new JsonSerializerGenerator());
        var run = driver.RunGenerators(compilation).GetRunResult();

        var errors = compilation
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToArray();
        if (errors.Length > 0)
            throw new InvalidOperationException(
                "The harness compilation must be clean:"
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, errors)
            );

        return run.GeneratedTrees.Select(tree => tree.GetText().ToString()).ToArray();
    }

    static IEnumerable<MetadataReference> PlatformReferences()
    {
        var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        foreach (var path in tpa.Split(Path.PathSeparator))
            yield return MetadataReference.CreateFromFile(path);
        yield return MetadataReference.CreateFromFile(
            Path.Combine(AppContext.BaseDirectory, "PicoJetson.dll")
        );
        yield return MetadataReference.CreateFromFile(
            Path.Combine(AppContext.BaseDirectory, "PicoSerDe.Core.dll")
        );
    }

    sealed class ChunkyStream(byte[] data, int chunkSize) : Stream
    {
        int _pos;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;

        public override long Position
        {
            get => _pos;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = Math.Min(Math.Min(chunkSize, buffer.Length), data.Length - _pos);
            if (n <= 0)
                return 0;
            data.AsSpan(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(Read(buffer.Span));

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
