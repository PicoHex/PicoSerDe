namespace PicoSerDe.Integration.Tests;

/// <summary>
/// C1: the fast-path array readers (<c>TryReadInt32ArrayFast</c> / <c>TryReadInt64ArrayFast</c> /
/// <c>TryReadBoolArrayFast</c>) were three near-verbatim copies in JSON, TOML and YAML — the same
/// <c>[a, b, c]</c> element grammar, reached differently per format. The scan now exists once, in
/// <c>PicoSerDe.Core</c>'s <c>FastArrayReaders</c>, and each reader forwards to it.
/// <para>
/// A file-by-file diff cannot prove that collapse is behaviour-preserving, because the methods
/// return values, not bytes. So the proof is differential: the same array literal, handed to all
/// three readers at the same grammatical position (just past the opening <c>[</c>), must produce
/// the same count, the same values <i>and</i> the same position advance — for every corner of the
/// grammar: empty, single element, negatives, whitespace everywhere, a missing separator, the
/// int32 and int64 bounds, overflow, a quoted element, a malformed element, a truncated literal
/// and a nested array — and for all three methods, including the bail-outs where a method does
/// not handle the element type at all.
/// </para>
/// <para>
/// Agreement alone would only prove the three agree, so <see cref="ExpectedCountAndValuesMatch"/> also
/// pins the JSON reader against a hand-written expectation table: a shared bug in one AND three
/// copies still fails there.
/// </para>
/// </summary>
public class CrossFormatFastArrayReaderTests
{
    enum Format
    {
        Json,
        Toml,
        Yaml,
    }

    /// <summary>
    /// 8 slots: every case below has at most 4 elements, so a case that fills the buffer would be
    /// a different (deliberately separate) scenario — see
    /// <see cref="A_buffer_that_fills_stops_the_scan_identically_in_every_format"/>.
    /// </summary>
    const int DestLength = 8;

    /// <param name="Array">The literal payload, identical in all three formats.</param>
    /// <param name="Int32">Expected <c>TryReadInt32ArrayFast</c> result; <c>null</c> = bails (0).</param>
    /// <param name="Int64">Expected <c>TryReadInt64ArrayFast</c> result; <c>null</c> = bails (0).</param>
    /// <param name="Bool">Expected <c>TryReadBoolArrayFast</c> result; <c>null</c> = bails (0).</param>
    sealed record Case(string Array, int[]? Int32, long[]? Int64, bool[]? Bool);

    static readonly Dictionary<string, Case> Cases = new(StringComparer.Ordinal)
    {
        // An empty collection is a successful scan of zero elements: the ']' IS consumed.
        ["Empty"] = new("[]", [], [], []),
        ["Single_element"] = new("[7]", [7], [7], null),
        ["Basic"] = new("[1, 42, -7, 0]", [1, 42, -7, 0], [1, 42, -7, 0], null),
        ["Whitespace_everywhere"] = new("[ 1 ,\t-42 ,\n7 ]", [1, -42, 7], [1, -42, 7], null),
        // The scan is a run of scalars, not a validating parser: a missing comma between two
        // number literals reads two elements in every format. Locked so a "fix" is deliberate.
        ["Missing_separator"] = new("[1 2]", [1, 2], [1, 2], null),
        ["Int32_bounds"] = new(
            "[-2147483648, 2147483647]",
            [int.MinValue, int.MaxValue],
            [-2147483648L, 2147483647L],
            null
        ),
        ["Int32_overflow_still_fits_int64"] = new("[99999999999]", null, [99999999999L], null),
        ["Int64_bounds"] = new("[9223372036854775807, -1]", null, [long.MaxValue, -1L], null),
        ["Int64_overflow"] = new("[99999999999999999999]", null, null, null),
        ["Bool"] = new("[true, false, true]", null, null, [true, false, true]),
        ["Bool_spaced"] = new("[ true ,\tfalse ]", null, null, [true, false]),
        // Quoted elements are not on the fast path: the validated reader keeps them (and its
        // own diagnostics), so every method bails instead of half-reading the array.
        ["Quoted_element"] = new("[\"a\"]", null, null, null),
        ["Malformed_element"] = new("[1, x]", null, null, null),
        ["Truncated_bool"] = new("[tru]", null, null, null),
        ["Capitalised_bool"] = new("[True]", null, null, null),
        ["Nested_array"] = new("[[1], 2]", null, null, null),
    };

    public static IEnumerable<string> CaseNames() => Cases.Keys;

    [Test]
    [MethodDataSource(nameof(CaseNames))]
    public async Task Every_case_reads_identically_in_json_toml_and_yaml(string name)
    {
        var c = Cases[name];

        var j32 = ReadInt32(Format.Json, c.Array);
        var t32 = ReadInt32(Format.Toml, c.Array);
        var y32 = ReadInt32(Format.Yaml, c.Array);
        await Agree(name, "int32", j32, t32, y32);

        var j64 = ReadInt64(Format.Json, c.Array);
        var t64 = ReadInt64(Format.Toml, c.Array);
        var y64 = ReadInt64(Format.Yaml, c.Array);
        await Agree(name, "int64", j64, t64, y64);

        var jb = ReadBool(Format.Json, c.Array);
        var tb = ReadBool(Format.Toml, c.Array);
        var yb = ReadBool(Format.Yaml, c.Array);
        await Agree(name, "bool", jb, tb, yb);
    }

    [Test]
    [MethodDataSource(nameof(CaseNames))]
    public async Task Expected_count_and_values_match(string name)
    {
        var c = Cases[name];

        var i32 = ReadInt32(Format.Json, c.Array);
        await AssertExpected(name, "int32", c.Int32, i32.Count, i32.Values, i32.Delta);
        var i64 = ReadInt64(Format.Json, c.Array);
        await AssertExpected(name, "int64", c.Int64, i64.Count, i64.Values, i64.Delta);
        var b = ReadBool(Format.Json, c.Array);
        await AssertExpected(name, "bool", c.Bool, b.Count, b.Values, b.Delta);
    }

    /// <summary>
    /// A destination buffer that fills up ends the scan successfully and stops between elements —
    /// the contract that lets a caller resume with a bigger buffer. The three formats must stop at
    /// the same byte, or a caller's resume point would depend on the format it picked.
    /// </summary>
    [Test]
    public async Task A_buffer_that_fills_stops_the_scan_identically_in_every_format()
    {
        const string payload = "[1, 2, 3, 4]";

        var j = ReadInt32(Format.Json, payload, destLength: 2);
        var t = ReadInt32(Format.Toml, payload, destLength: 2);
        var y = ReadInt32(Format.Yaml, payload, destLength: 2);
        await Agree("partial", "int32", j, t, y);

        await Assert.That(j.Count).IsEqualTo(2);
        await Assert.That(j.Values.SequenceEqual(new[] { 1, 2 })).IsTrue();
        // Two elements consumed leaves the position on the separator before the third element:
        // from '1' (offset 1) through the " 2" that filled the buffer (offset 5) is four bytes.
        await Assert.That(j.Delta).IsEqualTo(4L);
        await Assert
            .That(j.Delta)
            .IsEqualTo((long)payload.IndexOf(", 3", StringComparison.Ordinal) - 1L);
    }

    /// <summary>
    /// The other end of the int64 range, and the one asymmetry in
    /// <c>FastArrayReaders.TryReadInt64Array</c>'s doc comment: <c>-9223372036854775808</c> is a
    /// magnitude of 2^63, one past <see cref="long.MaxValue"/>, and that scan deliberately has no
    /// "a negative sign buys one more step" branch — unlike the int32 scan above it, which does.
    /// So the literal is legal input but never a fast-path element: every format must bail,
    /// consume nothing, and hand the list to the validated reader.
    /// <para>
    /// What is asserted is the bail-out, not <c>long.MinValue</c> coming back from the fast path,
    /// because the hand-off IS the contract: asserting the value here would pin a behaviour the
    /// file explicitly promises not to have (and would pass under a broken overflow guard that
    /// lets the accumulate wrap around). That is also why the second half exists — the three
    /// formats must still agree on the value through the validated path, or the same document
    /// would deserialize differently depending on which format read it.
    /// </para>
    /// <para>
    /// <see cref="Cases"/>'s <c>Int64_bounds</c> pins <see cref="long.MaxValue"/>; nothing in
    /// this file reached the value one past the negative end before.
    /// </para>
    /// </summary>
    [Test]
    public async Task Long_min_value_is_past_the_fast_path_in_every_format_and_still_reads_back()
    {
        const string payload = "[-9223372036854775808]";

        var j = ReadInt64(Format.Json, payload);
        var t = ReadInt64(Format.Toml, payload);
        var y = ReadInt64(Format.Yaml, payload);
        await Agree("long-min", "int64", j, t, y);

        await Assert
            .That(j.Count)
            .IsEqualTo(0)
            .Because("long.MinValue is past the int64 fast path in every format");
        await Assert.That(j.Delta).IsEqualTo(0L).Because("a bail-out consumes nothing");

        // The value is not lost with the fast path: the validated read still answers it, and all
        // three formats answer the same thing.
        await Assert
            .That(ValidatedInt64(Format.Json, payload))
            .IsEqualTo(long.MinValue)
            .Because("JSON validated path");
        await Assert
            .That(ValidatedInt64(Format.Toml, payload))
            .IsEqualTo(long.MinValue)
            .Because("TOML validated path");
        await Assert
            .That(ValidatedInt64(Format.Yaml, payload))
            .IsEqualTo(long.MinValue)
            .Because("YAML validated path");
    }

    // ── the three formats, positioned just past '[' ──

    static byte[] Bytes(Format format, string array) =>
        Encoding.UTF8.GetBytes(format == Format.Toml ? "k = " + array : array);

    static (int Count, int[] Values, long Delta) ReadInt32(
        Format format,
        string array,
        int destLength = DestLength
    )
    {
        var bytes = Bytes(format, array);
        switch (format)
        {
            case Format.Json:
            {
                var reader = new JsonReader(bytes);
                reader.Read();
                long start = reader.BytesConsumed;
                var buf = new int[destLength];
                int n = reader.TryReadInt32ArrayFast(buf);
                return (n, buf[..n], reader.BytesConsumed - start);
            }
            case Format.Toml:
            {
                var reader = new TomlReader(bytes);
                reader.Read(); // property name — advances past '['
                reader.Read(); // array start
                long start = reader.BytesConsumed;
                var buf = new int[destLength];
                int n = reader.TryReadInt32ArrayFast(buf);
                return (n, buf[..n], reader.BytesConsumed - start);
            }
            default:
            {
                var reader = new YamlReader(bytes);
                reader.SetRawPos(bytes.AsSpan().IndexOf((byte)'[') + 1);
                long start = reader.BytesConsumed;
                var buf = new int[destLength];
                int n = reader.TryReadInt32ArrayFast(buf);
                return (n, buf[..n], reader.BytesConsumed - start);
            }
        }
    }

    static (int Count, long[] Values, long Delta) ReadInt64(
        Format format,
        string array,
        int destLength = DestLength
    )
    {
        var bytes = Bytes(format, array);
        switch (format)
        {
            case Format.Json:
            {
                var reader = new JsonReader(bytes);
                reader.Read();
                long start = reader.BytesConsumed;
                var buf = new long[destLength];
                int n = reader.TryReadInt64ArrayFast(buf);
                return (n, buf[..n], reader.BytesConsumed - start);
            }
            case Format.Toml:
            {
                var reader = new TomlReader(bytes);
                reader.Read();
                reader.Read();
                long start = reader.BytesConsumed;
                var buf = new long[destLength];
                int n = reader.TryReadInt64ArrayFast(buf);
                return (n, buf[..n], reader.BytesConsumed - start);
            }
            default:
            {
                var reader = new YamlReader(bytes);
                reader.SetRawPos(bytes.AsSpan().IndexOf((byte)'[') + 1);
                long start = reader.BytesConsumed;
                var buf = new long[destLength];
                int n = reader.TryReadInt64ArrayFast(buf);
                return (n, buf[..n], reader.BytesConsumed - start);
            }
        }
    }

    static (int Count, bool[] Values, long Delta) ReadBool(
        Format format,
        string array,
        int destLength = DestLength
    )
    {
        var bytes = Bytes(format, array);
        switch (format)
        {
            case Format.Json:
            {
                var reader = new JsonReader(bytes);
                reader.Read();
                long start = reader.BytesConsumed;
                var buf = new bool[destLength];
                int n = reader.TryReadBoolArrayFast(buf);
                return (n, buf[..n], reader.BytesConsumed - start);
            }
            case Format.Toml:
            {
                var reader = new TomlReader(bytes);
                reader.Read();
                reader.Read();
                long start = reader.BytesConsumed;
                var buf = new bool[destLength];
                int n = reader.TryReadBoolArrayFast(buf);
                return (n, buf[..n], reader.BytesConsumed - start);
            }
            default:
            {
                var reader = new YamlReader(bytes);
                reader.SetRawPos(bytes.AsSpan().IndexOf((byte)'[') + 1);
                long start = reader.BytesConsumed;
                var buf = new bool[destLength];
                int n = reader.TryReadBoolArrayFast(buf);
                return (n, buf[..n], reader.BytesConsumed - start);
            }
        }
    }

    /// <summary>
    /// The validated (non-fast) read of this literal's first element — the path a caller falls
    /// back to when <c>TryReadInt64ArrayFast</c> answers 0. Positioned exactly like the fast-path
    /// helpers above, so the two ask the same question of the same bytes.
    /// </summary>
    static long ValidatedInt64(Format format, string array)
    {
        var bytes = Bytes(format, array);
        switch (format)
        {
            case Format.Json:
            {
                var reader = new JsonReader(bytes);
                reader.Read(); // array start
                if (!reader.Read() || !reader.TryGetInt64(out var v))
                    throw new InvalidOperationException(
                        "JSON validated read of the element failed"
                    );
                return v;
            }
            case Format.Toml:
            {
                var reader = new TomlReader(bytes);
                reader.Read(); // property name — advances past '['
                reader.Read(); // array start
                if (!reader.Read() || !reader.TryGetInt64(out var v))
                    throw new InvalidOperationException(
                        "TOML validated read of the element failed"
                    );
                return v;
            }
            default:
            {
                var reader = new YamlReader(bytes);
                reader.Read(); // flow-sequence start (YAML has no ArrayStart token)
                if (!reader.Read() || !reader.TryGetInt64(out var v))
                    throw new InvalidOperationException(
                        "YAML validated read of the element failed"
                    );
                return v;
            }
        }
    }

    // ── assertions ──

    static async Task Agree<T>(
        string name,
        string method,
        (int Count, T[] Values, long Delta) json,
        (int Count, T[] Values, long Delta) toml,
        (int Count, T[] Values, long Delta) yaml
    )
    {
        var expected = $"{name}/{method}";
        await Assert
            .That(toml.Count)
            .IsEqualTo(json.Count)
            .Because($"TOML count vs JSON, {expected}");
        await Assert
            .That(yaml.Count)
            .IsEqualTo(json.Count)
            .Because($"YAML count vs JSON, {expected}");
        await Assert
            .That(toml.Values.SequenceEqual(json.Values))
            .IsTrue()
            .Because($"TOML values vs JSON, {expected}");
        await Assert
            .That(yaml.Values.SequenceEqual(json.Values))
            .IsTrue()
            .Because($"YAML values vs JSON, {expected}");
        await Assert
            .That(toml.Delta)
            .IsEqualTo(json.Delta)
            .Because($"TOML position vs JSON, {expected}");
        await Assert
            .That(yaml.Delta)
            .IsEqualTo(json.Delta)
            .Because($"YAML position vs JSON, {expected}");
    }

    static async Task AssertExpected<T>(
        string name,
        string method,
        T[]? expected,
        int count,
        T[] values,
        long delta
    )
    {
        var label = $"{name}/{method}";
        if (expected is null)
        {
            // A bail-out reports 0 and must not have consumed a byte.
            await Assert.That(count).IsEqualTo(0).Because(label);
            await Assert.That(delta).IsEqualTo(0L).Because($"{label}: a bail-out rewinds");
            return;
        }
        await Assert.That(count).IsEqualTo(expected.Length).Because(label);
        await Assert.That(values.SequenceEqual(expected)).IsTrue().Because($"{label}: values");
        // A scan that runs to the terminator consumes every byte up to and including ']'.
        await Assert.That(delta > 0).IsTrue().Because($"{label}: a completed scan consumes bytes");
    }
}
