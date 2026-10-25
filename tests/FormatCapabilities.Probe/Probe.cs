// The 70-cell probe (spec §2 R1′ + R5): for every shape in
// FormatCapabilities.AllShapeIds and every format in FormatCapabilities.AllFormatTags it
// declares one model (Models.cs), instantiates three values (empty / single / boundary),
// round-trips each one through the format's own serializer and prints ONE line:
//
//   CELL|<format>|<shape>|<verdict>|<wire>|<expected>|<actual>[|reason=<code>]
//
// <verdict> is OK only when all three values survive the round trip under the shape's
// OWN equivalence relation (spec R1′), otherwise MISMATCH. MISMATCH is a MEASUREMENT,
// not a test failure: this file records today's behaviour (Task 3's before-picture);
// what must round-trip is R2's business.
//
// <verdict> has four values, so that "this format hangs" is countable instead of hiding
// inside the MISMATCH pile:
//   OK / MISMATCH           measured — see above
//   TIMEOUT                 a value never returned: the watchdog fired
//   SKIPPED_AFTER_TIMEOUT   NOT measured: this format already hung twice in this run
// The two unmeasured verdicts append `|reason=<code>` as an 8th column
// (`reason=timeout;value=empty`, `reason=skipped-after-timeout;first-timeout=toml/S9`), so
// `grep -c '|TIMEOUT|'` answers "how many cells hang". Per-verdict totals go to stderr.
//
// EQUIVALENCE (R1′) is implemented as one canonical text per value, compared by string
// equality — so every family's rule is visible in exactly one place:
//   sequence   order + length + elementwise        → Fmt.Seq
//   set        set equality (elements sorted)      → Fmt.Set
//   map        key/value pairs (keys sorted)       → Fmt.Map
//   double     shortest round-trip text ("R")      → Fmt.Dbl
//   decimal    invariant culture                   → Fmt.Dec
//   DateTime   value + DateTime.Kind               → Fmt.When
//   string     decoded code points (repr. free)    → Fmt.Str / Fmt.Seq
//   recursion  tree isomorphism                    → Text functions in the builders
//   null       null ≠ "" ≠ [] ≠ missing member     → each has its own literal below
//
// The wire column renders bytes as text (UTF-8) or, for msgpack, as hex.

using System.Globalization;
using System.Text;
using PicoIni;
using PicoJetson;
using PicoMsgPack;
using PicoToml;
using PicoYaml;
// The project's own namespace is FormatCapabilities.Probe, so the capability model's
// type of the same name needs an alias (it is the one source of truth for shapes and
// formats — do NOT restate those lists here).
using Caps = PicoSerDe.Gen.FormatCapabilities;

namespace FormatCapabilities.Probe;

/// <summary>One cell of the matrix: 3 values, one model type, one format.</summary>
internal sealed class Cell
{
    internal required string Shape { get; init; }

    internal required string Format { get; init; }

    internal required Type ModelType { get; init; }

    internal required Func<object> Empty { get; init; }

    internal required Func<object> Single { get; init; }

    internal required Func<object> Boundary { get; init; }

    internal required Func<object, string> Text { get; init; }

    internal required Func<object, byte[]> Serialize { get; init; }

    internal required Func<byte[], object?> Deserialize { get; init; }
}

/// <summary>The three R5 values of a payload plus its R1′ canonical text.</summary>
internal sealed class Payload<T>
    where T : class
{
    private readonly T _empty;
    private readonly T _single;
    private readonly T _boundary;
    private readonly Func<T?, string> _text;

    internal Payload(T empty, T single, T boundary, Func<T?, string> text)
    {
        _empty = empty;
        _single = single;
        _boundary = boundary;
        _text = text;
    }

    internal T Empty() => _empty;

    internal T Single() => _single;

    internal T Boundary() => _boundary;

    internal string Text(T? value) => _text(value);
}

/// <summary>What the probe decided about one cell. Two of the four are not measurements.</summary>
internal enum Verdict
{
    /// <summary>All three values round-tripped under the shape's own equivalence relation.</summary>
    Ok,

    /// <summary>A measured, explainable difference between what went in and what came back.</summary>
    Mismatch,

    /// <summary>A value never returned: the watchdog fired, so nothing was measured.</summary>
    Timeout,

    /// <summary>Not measured: this cell's format already hung twice in this process.</summary>
    SkippedAfterTimeout,
}

/// <summary>One printed cell line plus the verdict that line carries.</summary>
internal readonly record struct CellResult(string Line, Verdict Verdict);

internal static class Program
{
    private const int MaxColumnChars = 180;

    private static int Main()
    {
        var cells = Registry.Cells();

        if (Registry.Validate(cells, out var error) is false)
        {
            Console.WriteLine("CHECK|FAIL|" + Escape(error));
            return 1;
        }

        var byKey = cells.ToDictionary(c => (c.Shape, c.Format));
        var counts = new Dictionary<Verdict, int>
        {
            [Verdict.Ok] = 0,
            [Verdict.Mismatch] = 0,
            [Verdict.Timeout] = 0,
            [Verdict.SkippedAfterTimeout] = 0,
        };
        var state = new RunState();

        // Canonical order: spec §3 row order × spec §3 column order, both taken from the
        // capability model, so a new shape/format cannot be silently left out.
        foreach (var shape in Caps.AllShapeIds)
        foreach (var format in Caps.AllFormatTags)
        {
            var cell = byKey[(shape, format)];

            // A format that hung twice is no longer measurable in this process (an abandoned
            // thread is still spinning and stealing CPU); say so instead of manufacturing a
            // false timeout for every remaining cell of that format.
            var result = state.IsClosed(format, out var closedReason)
                ? Skipped(cell, closedReason)
                : Run(cell, state);

            if (result.Line.StartsWith("CELL|", StringComparison.Ordinal) is false)
                throw new InvalidOperationException("malformed cell line: " + result.Line);

            counts[result.Verdict]++;
            Console.WriteLine(result.Line);
        }

        // stderr, so `dotnet run | sort > baseline-cells.txt` stays exactly the 70 cells.
        Console.Error.WriteLine(
            $"# cells: {counts[Verdict.Ok]} OK, {counts[Verdict.Mismatch]} MISMATCH, "
                + $"{counts[Verdict.Timeout]} TIMEOUT, "
                + $"{counts[Verdict.SkippedAfterTimeout]} SKIPPED_AFTER_TIMEOUT, "
                + $"{cells.Count} total"
        );
        Console.Error.WriteLine(
            $"# abandoned worker threads never returned: {state.AbandonedCount}"
        );
        return 0;
    }

    private static CellResult Run(Cell cell, RunState state)
    {
        var wire = new List<string>(3);
        var expected = new List<string>(3);
        var actual = new List<string>(3);
        var allEquivalent = true;
        var abandoned = false;
        var timedOutValue = "";

        foreach (var kind in Kinds)
        {
            if (abandoned)
            {
                // An earlier value of this cell never returned: do not spawn more stuck
                // threads for the remaining kinds, just say so.
                const string skipped = "<skipped: previous value timed out>";
                wire.Add(kind + "=" + skipped);
                expected.Add(kind + "=" + skipped);
                actual.Add(kind + "=" + skipped);
                allEquivalent = false;
                continue;
            }

            object value;
            try
            {
                value = kind switch
                {
                    "empty" => cell.Empty(),
                    "single" => cell.Single(),
                    _ => cell.Boundary(),
                };
            }
            catch (Exception e)
            {
                wire.Add(kind + "=<construct threw " + Describe(e) + ">");
                expected.Add(kind + "=<construct threw>");
                actual.Add(kind + "=<construct threw>");
                allEquivalent = false;
                continue;
            }

            var expectedText = cell.Text(value);
            expected.Add(kind + "=" + Column(expectedText));

            // A probe that can be hung by the code under measurement is worthless: every
            // value runs on its own watchdog thread. A timeout is a finding, not a crash.
            if (TryRun(() => Attempt(cell, value), out var attempt, out var failure) is false)
            {
                wire.Add(kind + "=" + failure);
                actual.Add(kind + "=" + failure);
                allEquivalent = false;
                abandoned = failure.Contains("timed out", StringComparison.Ordinal);
                if (abandoned)
                {
                    // The worker for this value is still running and there is no safe way to
                    // stop it: record it as abandoned and never read its result back. A second
                    // timeout inside the same format closes that format for the rest of the run
                    // (see RunState).
                    timedOutValue = kind;
                    state.RecordTimeout(cell.Format, cell.Shape, kind);
                }

                continue;
            }

            wire.Add(kind + "=" + Column(attempt.Wire));
            actual.Add(kind + "=" + Column(attempt.Actual));

            // Compare the raw texts, not the escaped/truncated columns.
            if (attempt.Actual != expectedText)
                allEquivalent = false;
        }

        // A hang is not a mismatch: the cell was not measured at all, so it gets its own
        // verdict (and a machine-readable reason) instead of polluting the mismatch count.
        var verdict =
            allEquivalent ? Verdict.Ok
            : abandoned ? Verdict.Timeout
            : Verdict.Mismatch;

        var reason = verdict is Verdict.Timeout ? "|reason=timeout;value=" + timedOutValue : "";

        return new CellResult(
            $"CELL|{cell.Format}|{cell.Shape}|{VerdictText(verdict)}|"
                + Escape(string.Join(" ; ", wire))
                + "|"
                + Escape(string.Join(" ; ", expected))
                + "|"
                + Escape(string.Join(" ; ", actual))
                + reason,
            verdict
        );
    }

    /// <summary>
    /// A cell that was deliberately NOT measured because its format already hung twice in
    /// this process. Its verdict says exactly that, all three value columns still carry all
    /// three kinds (so "three values per cell" stays true) and the reason column names the
    /// culprit cells.
    /// </summary>
    private static CellResult Skipped(Cell cell, string reason)
    {
        const string skipped = "<skipped: format short-circuited after a repeated timeout>";
        var column = Escape(string.Join(" ; ", Kinds.Select(kind => kind + "=" + skipped)));
        return new CellResult(
            $"CELL|{cell.Format}|{cell.Shape}|{VerdictText(Verdict.SkippedAfterTimeout)}|"
                + column
                + "|"
                + column
                + "|"
                + column
                + "|"
                + Escape(reason),
            Verdict.SkippedAfterTimeout
        );
    }

    private static string VerdictText(Verdict verdict) =>
        verdict switch
        {
            Verdict.Ok => "OK",
            Verdict.Mismatch => "MISMATCH",
            Verdict.Timeout => "TIMEOUT",
            _ => "SKIPPED_AFTER_TIMEOUT",
        };

    /// <summary>
    /// Cross-cell bookkeeping for the watchdog. Two rules, both about one hang not being
    /// allowed to corrupt the other 69 cells (a thread cannot be stopped, so an abandoned
    /// worker keeps burning a core for the rest of the run):
    /// <list type="number">
    /// <item>A value that never returns is recorded as an abandoned worker; its result is
    /// never read back, and the cell's verdict is TIMEOUT.</item>
    /// <item>A timeout in a format is a warning; a SECOND timeout in the SAME format means
    /// that format can no longer be timed honestly in this process (the abandoned thread is
    /// still running) — so every remaining cell of that format is reported as
    /// SKIPPED_AFTER_TIMEOUT with the culprit cells in its reason column, instead of being
    /// measured under CPU contention and recorded as a false permanent MISMATCH.</item>
    /// </list>
    /// </summary>
    private sealed class RunState
    {
        // format → the first cell of that format that timed out ("toml/S9").
        private readonly Dictionary<string, string> _firstTimeout = new(StringComparer.Ordinal);

        // format → the second cell of that format that timed out; only this closes the format.
        private readonly Dictionary<string, string> _repeatedTimeout = new(StringComparer.Ordinal);

        private int _abandoned;

        internal int AbandonedCount => _abandoned;

        /// <summary>True when <paramref name="format"/> hung twice; then no cell of it is measured.</summary>
        internal bool IsClosed(string format, out string reason)
        {
            if (_repeatedTimeout.TryGetValue(format, out var repeated) is false)
            {
                reason = "";
                return false;
            }

            reason =
                "reason=skipped-after-timeout;first-timeout="
                + _firstTimeout[format]
                + ";repeated-timeout="
                + repeated;
            return true;
        }

        internal void RecordTimeout(string format, string shape, string value)
        {
            _abandoned++;
            var culprit = format + "/" + shape;

            if (_firstTimeout.TryAdd(format, culprit))
            {
                // stderr only: stdout stays exactly one CELL line per cell.
                Console.Error.WriteLine(
                    $"HANG|{format}|{shape}|value={value}|first-timeout-in-format"
                );
                return;
            }

            if (_repeatedTimeout.TryAdd(format, culprit))
            {
                Console.Error.WriteLine(
                    $"SHORT-CIRCUIT|{format}|first={_firstTimeout[format]}|repeated={culprit}"
                );
            }
        }
    }

    /// <summary>Serialize + deserialize one value, reporting failures as text.</summary>
    private static (string Wire, string Actual) Attempt(Cell cell, object value)
    {
        byte[] bytes;
        try
        {
            bytes = cell.Serialize(value);
        }
        catch (Exception e)
        {
            return ("<serialize threw>", "<serialize threw " + Describe(e) + ">");
        }

        var wire = WireText(cell.Format, bytes);
        try
        {
            var back = cell.Deserialize(bytes);
            return (wire, back is null ? "null" : cell.Text(back));
        }
        catch (Exception e)
        {
            return (wire, "<deserialize threw " + Describe(e) + ">");
        }
    }

    /// <summary>
    /// Runs <paramref name="operation"/> on a dedicated background thread and gives up
    /// after <see cref="OperationTimeout"/>. A stuck operation is reported, never waited
    /// on — the abandoned thread is a background thread, so it cannot keep the process
    /// alive.
    /// </summary>
    private static bool TryRun(
        Func<(string Wire, string Actual)> operation,
        out (string Wire, string Actual) result,
        out string failure
    )
    {
        result = ("", "");
        failure = "";
        (string Wire, string Actual) captured = ("", "");
        Exception? error = null;

        // Deliberately NOT `using`: on the timeout path this value is abandoned while its
        // worker thread is still running, and that worker calls done.Set() from its own
        // finally. Disposing here would make that Set() throw ObjectDisposedException on a
        // thread where nothing catches it — and an unhandled exception on ANY thread kills
        // the whole process, i.e. it would silently truncate
        // `dotnet run | sort > baseline-cells.txt` halfway through, corrupting the checked-in
        // baseline instead of reporting a TIMEOUT. ManualResetEventSlim holds no unmanaged
        // resource (its kernel handle is lazy and finalizable), so letting the GC collect it
        // is correct — the abandoned worker dies with the process either way.
        var done = new ManualResetEventSlim(false);

        var worker = new Thread(() =>
        {
            try
            {
                captured = operation();
            }
            catch (Exception e)
            {
                error = e;
            }
            finally
            {
                done.Set();
            }
        })
        {
            IsBackground = true,
            Name = "probe-operation",
        };
        worker.Start();

        if (done.Wait(OperationTimeout) is false)
        {
            failure =
                "<timed out after "
                + OperationTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)
                + "s>";
            return false;
        }

        if (error is not null)
        {
            failure = "<threw " + Describe(error) + ">";
            return false;
        }

        result = captured;
        return true;
    }

    private static readonly string[] Kinds = ["empty", "single", "boundary"];

    /// <summary>How long one serialize+deserialize may take before it counts as hung.</summary>
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(2);

    private static string WireText(string format, byte[] bytes)
    {
        if (bytes.Length == 0)
            return "<empty>";
        return format == "msgpack" ? Convert.ToHexString(bytes) : Encoding.UTF8.GetString(bytes);
    }

    private static string Describe(Exception e) =>
        e.GetType().Name + ": " + (e.Message.Length > 120 ? e.Message[..120] : e.Message);

    private static string Column(string text) =>
        text.Length <= MaxColumnChars ? text : text[..MaxColumnChars] + "~";

    /// <summary>Keeps one cell on one line and keeps <c>|</c> structural.</summary>
    internal static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '|':
                    sb.Append("\\|");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }
}

/// <summary>Canonical text for the R1′ equivalence relations (see the file header).</summary>
internal static class Fmt
{
    internal static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    internal static string NullableInt(int? value) => value is null ? "null" : Int(value.Value);

    /// <summary>Shortest round-trip text — never '<c>float</c> through <c>double</c>'.</summary>
    internal static string Dbl(double value)
    {
        if (double.IsNaN(value))
            return "NaN";
        if (double.IsPositiveInfinity(value))
            return "+Inf";
        if (double.IsNegativeInfinity(value))
            return "-Inf";
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    internal static string Dec(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Value + <see cref="DateTime.Kind"/>: the round trip must keep both.</summary>
    internal static string When(DateTime value) =>
        value.ToString("O", CultureInfo.InvariantCulture) + "#" + value.Kind;

    /// <summary><c>null</c> is its own literal: it is NOT the empty string (R1′ null 三态).</summary>
    internal static string Str(string? value) => value is null ? "null" : "\"" + value + "\"";

    internal static string Dto(ProbeDto? value) =>
        value is null
            ? "null"
            : "{"
                + Str(value.Name)
                + ","
                + Int(value.N)
                + ","
                + Dbl(value.F)
                + ","
                + Dec(value.D)
                + ","
                + When(value.When)
                + "}";

    /// <summary>Sequence: order, length and per-element equivalence all matter.</summary>
    internal static string Seq<T>(IEnumerable<T>? items, Func<T, string> element) =>
        items is null ? "null" : "[" + string.Join(",", items.Select(element)) + "]";

    /// <summary>Set: order is NOT part of the equivalence — sort the element texts.</summary>
    internal static string Set<T>(IEnumerable<T>? items, Func<T, string> element) =>
        items is null
            ? "null"
            : "{"
                + string.Join(",", items.Select(element).OrderBy(s => s, StringComparer.Ordinal))
                + "}";

    /// <summary>Map: key/value pairs compare as a multiset — sort by key.</summary>
    internal static string Map<TV>(Dictionary<string, TV>? map, Func<TV, string> value) =>
        map is null
            ? "null"
            : "{"
                + string.Join(
                    ",",
                    map.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => Str(pair.Key) + ":" + value(pair.Value))
                )
                + "}";
}

/// <summary>
/// The R5 values, one instance per shape. The values are shared by all five formats on
/// purpose: the difference between two cells must come from the format, not from the
/// corpus.
/// </summary>
internal static class Payloads
{
    // ── R5 building blocks ─────────────────────────────────────────────────────────
    private const string Tricky = "p|ipe \"q\" line\nbreak tab\there back\\slash ünï 测试";

    private static readonly DateTime UtcWhen = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime PlainWhen = new(
        2020,
        12,
        31,
        23,
        59,
        59,
        DateTimeKind.Unspecified
    );

    // NOTE on NaN/±Inf: the 14 shapes modelled by R1 are container shapes (S1–S13), so
    // the only place a float can live is inside ProbeDto — and a NaN there would decide
    // the WHOLE cell's verdict (JSON has no NaN literal), hiding whether the shape
    // itself round-trips. The float-literal boundary belongs to the scalar shape (S15,
    // Task 2b), so ProbeDto keeps finite doubles here on purpose.
    internal static readonly ProbeDto DtoTricky = new()
    {
        Name = Tricky,
        N = int.MaxValue,
        F = 1.5,
        D = 123456789.123456789m,
        When = UtcWhen,
    };

    internal static readonly ProbeDto DtoPlain = new()
    {
        Name = "",
        N = 0,
        F = -2.25,
        D = -0.000001m,
        When = PlainWhen,
    };

    // ── S1  List<int> ─────────────────────────────────────────────────────────────
    internal static readonly Payload<List<int>> S1 = new(
        [],
        [7],
        [int.MaxValue, 0, -1],
        value => Fmt.Seq(value, Fmt.Int)
    );

    // ── S2  List<ProbeDto> ────────────────────────────────────────────────────────
    internal static readonly Payload<List<ProbeDto>> S2 = new(
        [],
        [DtoTricky],
        [DtoTricky, DtoPlain],
        value => Fmt.Seq(value, Fmt.Dto)
    );

    // ── S3  List<List<int>> ───────────────────────────────────────────────────────
    internal static readonly Payload<List<List<int>>> S3 = new(
        [],
        [
            [7],
        ],
        [
            [int.MaxValue, 0],
            [-1],
            [],
        ],
        value => Fmt.Seq(value, inner => Fmt.Seq(inner, Fmt.Int))
    );

    // ── S4  List<List<ProbeDto>> ──────────────────────────────────────────────────
    internal static readonly Payload<List<List<ProbeDto>>> S4 = new(
        [],
        [
            [DtoTricky],
        ],
        [
            [DtoTricky, DtoPlain],
            [],
        ],
        value => Fmt.Seq(value, inner => Fmt.Seq(inner, Fmt.Dto))
    );

    // ── S5  List<int?> ────────────────────────────────────────────────────────────
    internal static readonly Payload<List<int?>> S5 = new(
        [],
        [7],
        [int.MaxValue, null, 0],
        value => Fmt.Seq(value, Fmt.NullableInt)
    );

    // ── S6  List<List<int?>> ──────────────────────────────────────────────────────
    internal static readonly Payload<List<List<int?>>> S6 = new(
        [],
        [
            [7],
        ],
        [
            [null, int.MaxValue],
            [null],
        ],
        value => Fmt.Seq(value, inner => Fmt.Seq(inner, Fmt.NullableInt))
    );

    // ── S7  HashSet<int> ──────────────────────────────────────────────────────────
    internal static readonly Payload<HashSet<int>> S7 = new(
        [],
        [7],
        [int.MaxValue, 0, -1],
        value => Fmt.Set(value, Fmt.Int)
    );

    // ── S8  List<HashSet<int>> ────────────────────────────────────────────────────
    internal static readonly Payload<List<HashSet<int>>> S8 = new(
        [],
        [
            [7],
        ],
        [
            [int.MaxValue],
            [],
            [0, -1],
        ],
        value => Fmt.Seq(value, inner => Fmt.Set(inner, Fmt.Int))
    );

    // ── S9  Dictionary<string,int> ────────────────────────────────────────────────
    internal static readonly Payload<Dictionary<string, int>> S9 = new(
        [],
        new() { ["a"] = 7 },
        new()
        {
            ["a"] = int.MaxValue,
            ["p|ipe"] = 0,
            ["q\"uote"] = -1,
            ["n\nline"] = 2,
        },
        value => Fmt.Map(value, Fmt.Int)
    );

    // ── S10  Dictionary<string,List<int>> ─────────────────────────────────────────
    internal static readonly Payload<Dictionary<string, List<int>>> S10 = new(
        [],
        new() { ["a"] = [7] },
        new()
        {
            ["a"] = [int.MaxValue],
            ["e"] = [],
            ["z"] = [-1, 0],
        },
        value => Fmt.Map(value, inner => Fmt.Seq(inner, Fmt.Int))
    );

    // ── S12  List<string?> ────────────────────────────────────────────────────────
    internal static readonly Payload<List<string?>> S12 = new(
        [],
        ["a"],
        [Tricky, null, "q\"uote", "", "ünï 测试"],
        value => Fmt.Seq(value, Fmt.Str)
    );

    // ── S13  Dictionary<string,int?> ──────────────────────────────────────────────
    internal static readonly Payload<Dictionary<string, int?>> S13 = new(
        [],
        new() { ["a"] = 7 },
        new()
        {
            ["a"] = int.MaxValue,
            ["n"] = null,
            ["e"] = 0,
        },
        value => Fmt.Map(value, Fmt.NullableInt)
    );

    // S11a / S11b have no Payload<>: their payload IS the object graph; their values are
    // built by the recursion builders in Registry.
}

/// <summary>The 70 cells, built from the one source of truth for shapes and formats.</summary>
internal static class Registry
{
    internal static List<Cell> Cells() =>
        [
            // ── S1  List<int> — flat scalar list
            Container<S1Json, List<int>>(
                "S1",
                "json",
                Payloads.S1,
                () => new S1Json(),
                m => m.V,
                (m, v) => m.V = v,
                m => JsonSerializer.SerializeToUtf8Bytes(m),
                b => JsonSerializer.Deserialize<S1Json>(b)
            ),
            Container<S1Msgpack, List<int>>(
                "S1",
                "msgpack",
                Payloads.S1,
                () => new S1Msgpack(),
                m => m.V,
                (m, v) => m.V = v,
                m => MsgPackSerializer.SerializeToUtf8Bytes(m),
                b => MsgPackSerializer.Deserialize<S1Msgpack>(b)
            ),
            Container<S1Toml, List<int>>(
                "S1",
                "toml",
                Payloads.S1,
                () => new S1Toml(),
                m => m.V,
                (m, v) => m.V = v,
                m => TomlSerializer.SerializeToUtf8Bytes(m),
                b => TomlSerializer.Deserialize<S1Toml>(b)
            ),
            Container<S1Yaml, List<int>>(
                "S1",
                "yaml",
                Payloads.S1,
                () => new S1Yaml(),
                m => m.V,
                (m, v) => m.V = v,
                m => YamlSerializer.SerializeToUtf8Bytes(m),
                b => YamlSerializer.Deserialize<S1Yaml>(b)
            ),
            Container<S1Ini, List<int>>(
                "S1",
                "ini",
                Payloads.S1,
                () => new S1Ini(),
                m => m.V,
                (m, v) => m.V = v,
                m => IniSerializer.SerializeToUtf8Bytes(m),
                b => IniSerializer.Deserialize<S1Ini>(b)
            ),
            // ── S2  List<ProbeDto> — object list
            Container<S2Json, List<ProbeDto>>(
                "S2",
                "json",
                Payloads.S2,
                () => new S2Json(),
                m => m.V,
                (m, v) => m.V = v,
                m => JsonSerializer.SerializeToUtf8Bytes(m),
                b => JsonSerializer.Deserialize<S2Json>(b)
            ),
            Container<S2Msgpack, List<ProbeDto>>(
                "S2",
                "msgpack",
                Payloads.S2,
                () => new S2Msgpack(),
                m => m.V,
                (m, v) => m.V = v,
                m => MsgPackSerializer.SerializeToUtf8Bytes(m),
                b => MsgPackSerializer.Deserialize<S2Msgpack>(b)
            ),
            Container<S2Toml, List<ProbeDto>>(
                "S2",
                "toml",
                Payloads.S2,
                () => new S2Toml(),
                m => m.V,
                (m, v) => m.V = v,
                m => TomlSerializer.SerializeToUtf8Bytes(m),
                b => TomlSerializer.Deserialize<S2Toml>(b)
            ),
            Container<S2Yaml, List<ProbeDto>>(
                "S2",
                "yaml",
                Payloads.S2,
                () => new S2Yaml(),
                m => m.V,
                (m, v) => m.V = v,
                m => YamlSerializer.SerializeToUtf8Bytes(m),
                b => YamlSerializer.Deserialize<S2Yaml>(b)
            ),
            Container<S2Ini, List<ProbeDto>>(
                "S2",
                "ini",
                Payloads.S2,
                () => new S2Ini(),
                m => m.V,
                (m, v) => m.V = v,
                m => IniSerializer.SerializeToUtf8Bytes(m),
                b => IniSerializer.Deserialize<S2Ini>(b)
            ),
            // ── S3  List<List<int>> — nested scalar list
            Container<S3Json, List<List<int>>>(
                "S3",
                "json",
                Payloads.S3,
                () => new S3Json(),
                m => m.V,
                (m, v) => m.V = v,
                m => JsonSerializer.SerializeToUtf8Bytes(m),
                b => JsonSerializer.Deserialize<S3Json>(b)
            ),
            Container<S3Msgpack, List<List<int>>>(
                "S3",
                "msgpack",
                Payloads.S3,
                () => new S3Msgpack(),
                m => m.V,
                (m, v) => m.V = v,
                m => MsgPackSerializer.SerializeToUtf8Bytes(m),
                b => MsgPackSerializer.Deserialize<S3Msgpack>(b)
            ),
            Container<S3Toml, List<List<int>>>(
                "S3",
                "toml",
                Payloads.S3,
                () => new S3Toml(),
                m => m.V,
                (m, v) => m.V = v,
                m => TomlSerializer.SerializeToUtf8Bytes(m),
                b => TomlSerializer.Deserialize<S3Toml>(b)
            ),
            Container<S3Yaml, List<List<int>>>(
                "S3",
                "yaml",
                Payloads.S3,
                () => new S3Yaml(),
                m => m.V,
                (m, v) => m.V = v,
                m => YamlSerializer.SerializeToUtf8Bytes(m),
                b => YamlSerializer.Deserialize<S3Yaml>(b)
            ),
            Container<S3Ini, List<List<int>>>(
                "S3",
                "ini",
                Payloads.S3,
                () => new S3Ini(),
                m => m.V,
                (m, v) => m.V = v,
                m => IniSerializer.SerializeToUtf8Bytes(m),
                b => IniSerializer.Deserialize<S3Ini>(b)
            ),
            // ── S4  List<List<ProbeDto>> — nested object list
            Container<S4Json, List<List<ProbeDto>>>(
                "S4",
                "json",
                Payloads.S4,
                () => new S4Json(),
                m => m.V,
                (m, v) => m.V = v,
                m => JsonSerializer.SerializeToUtf8Bytes(m),
                b => JsonSerializer.Deserialize<S4Json>(b)
            ),
            Container<S4Msgpack, List<List<ProbeDto>>>(
                "S4",
                "msgpack",
                Payloads.S4,
                () => new S4Msgpack(),
                m => m.V,
                (m, v) => m.V = v,
                m => MsgPackSerializer.SerializeToUtf8Bytes(m),
                b => MsgPackSerializer.Deserialize<S4Msgpack>(b)
            ),
            Container<S4Toml, List<List<ProbeDto>>>(
                "S4",
                "toml",
                Payloads.S4,
                () => new S4Toml(),
                m => m.V,
                (m, v) => m.V = v,
                m => TomlSerializer.SerializeToUtf8Bytes(m),
                b => TomlSerializer.Deserialize<S4Toml>(b)
            ),
            Container<S4Yaml, List<List<ProbeDto>>>(
                "S4",
                "yaml",
                Payloads.S4,
                () => new S4Yaml(),
                m => m.V,
                (m, v) => m.V = v,
                m => YamlSerializer.SerializeToUtf8Bytes(m),
                b => YamlSerializer.Deserialize<S4Yaml>(b)
            ),
            Container<S4Ini, List<List<ProbeDto>>>(
                "S4",
                "ini",
                Payloads.S4,
                () => new S4Ini(),
                m => m.V,
                (m, v) => m.V = v,
                m => IniSerializer.SerializeToUtf8Bytes(m),
                b => IniSerializer.Deserialize<S4Ini>(b)
            ),
            // ── S5  List<int?> — nullable value leaf
            Container<S5Json, List<int?>>(
                "S5",
                "json",
                Payloads.S5,
                () => new S5Json(),
                m => m.V,
                (m, v) => m.V = v,
                m => JsonSerializer.SerializeToUtf8Bytes(m),
                b => JsonSerializer.Deserialize<S5Json>(b)
            ),
            Container<S5Msgpack, List<int?>>(
                "S5",
                "msgpack",
                Payloads.S5,
                () => new S5Msgpack(),
                m => m.V,
                (m, v) => m.V = v,
                m => MsgPackSerializer.SerializeToUtf8Bytes(m),
                b => MsgPackSerializer.Deserialize<S5Msgpack>(b)
            ),
            Container<S5Toml, List<int?>>(
                "S5",
                "toml",
                Payloads.S5,
                () => new S5Toml(),
                m => m.V,
                (m, v) => m.V = v,
                m => TomlSerializer.SerializeToUtf8Bytes(m),
                b => TomlSerializer.Deserialize<S5Toml>(b)
            ),
            Container<S5Yaml, List<int?>>(
                "S5",
                "yaml",
                Payloads.S5,
                () => new S5Yaml(),
                m => m.V,
                (m, v) => m.V = v,
                m => YamlSerializer.SerializeToUtf8Bytes(m),
                b => YamlSerializer.Deserialize<S5Yaml>(b)
            ),
            Container<S5Ini, List<int?>>(
                "S5",
                "ini",
                Payloads.S5,
                () => new S5Ini(),
                m => m.V,
                (m, v) => m.V = v,
                m => IniSerializer.SerializeToUtf8Bytes(m),
                b => IniSerializer.Deserialize<S5Ini>(b)
            ),
            // ── S6  List<List<int?>> — deep nullable value leaf
            Container<S6Json, List<List<int?>>>(
                "S6",
                "json",
                Payloads.S6,
                () => new S6Json(),
                m => m.V,
                (m, v) => m.V = v,
                m => JsonSerializer.SerializeToUtf8Bytes(m),
                b => JsonSerializer.Deserialize<S6Json>(b)
            ),
            Container<S6Msgpack, List<List<int?>>>(
                "S6",
                "msgpack",
                Payloads.S6,
                () => new S6Msgpack(),
                m => m.V,
                (m, v) => m.V = v,
                m => MsgPackSerializer.SerializeToUtf8Bytes(m),
                b => MsgPackSerializer.Deserialize<S6Msgpack>(b)
            ),
            Container<S6Toml, List<List<int?>>>(
                "S6",
                "toml",
                Payloads.S6,
                () => new S6Toml(),
                m => m.V,
                (m, v) => m.V = v,
                m => TomlSerializer.SerializeToUtf8Bytes(m),
                b => TomlSerializer.Deserialize<S6Toml>(b)
            ),
            Container<S6Yaml, List<List<int?>>>(
                "S6",
                "yaml",
                Payloads.S6,
                () => new S6Yaml(),
                m => m.V,
                (m, v) => m.V = v,
                m => YamlSerializer.SerializeToUtf8Bytes(m),
                b => YamlSerializer.Deserialize<S6Yaml>(b)
            ),
            Container<S6Ini, List<List<int?>>>(
                "S6",
                "ini",
                Payloads.S6,
                () => new S6Ini(),
                m => m.V,
                (m, v) => m.V = v,
                m => IniSerializer.SerializeToUtf8Bytes(m),
                b => IniSerializer.Deserialize<S6Ini>(b)
            ),
            // ── S7  HashSet<int> as a member — extended collection
            Container<S7Json, HashSet<int>>(
                "S7",
                "json",
                Payloads.S7,
                () => new S7Json(),
                m => m.V,
                (m, v) => m.V = v,
                m => JsonSerializer.SerializeToUtf8Bytes(m),
                b => JsonSerializer.Deserialize<S7Json>(b)
            ),
            Container<S7Msgpack, HashSet<int>>(
                "S7",
                "msgpack",
                Payloads.S7,
                () => new S7Msgpack(),
                m => m.V,
                (m, v) => m.V = v,
                m => MsgPackSerializer.SerializeToUtf8Bytes(m),
                b => MsgPackSerializer.Deserialize<S7Msgpack>(b)
            ),
            Container<S7Toml, HashSet<int>>(
                "S7",
                "toml",
                Payloads.S7,
                () => new S7Toml(),
                m => m.V,
                (m, v) => m.V = v,
                m => TomlSerializer.SerializeToUtf8Bytes(m),
                b => TomlSerializer.Deserialize<S7Toml>(b)
            ),
            Container<S7Yaml, HashSet<int>>(
                "S7",
                "yaml",
                Payloads.S7,
                () => new S7Yaml(),
                m => m.V,
                (m, v) => m.V = v,
                m => YamlSerializer.SerializeToUtf8Bytes(m),
                b => YamlSerializer.Deserialize<S7Yaml>(b)
            ),
            Container<S7Ini, HashSet<int>>(
                "S7",
                "ini",
                Payloads.S7,
                () => new S7Ini(),
                m => m.V,
                (m, v) => m.V = v,
                m => IniSerializer.SerializeToUtf8Bytes(m),
                b => IniSerializer.Deserialize<S7Ini>(b)
            ),
            // ── S8  List<HashSet<int>> — extended collection as element
            Container<S8Json, List<HashSet<int>>>(
                "S8",
                "json",
                Payloads.S8,
                () => new S8Json(),
                m => m.V,
                (m, v) => m.V = v,
                m => JsonSerializer.SerializeToUtf8Bytes(m),
                b => JsonSerializer.Deserialize<S8Json>(b)
            ),
            Container<S8Msgpack, List<HashSet<int>>>(
                "S8",
                "msgpack",
                Payloads.S8,
                () => new S8Msgpack(),
                m => m.V,
                (m, v) => m.V = v,
                m => MsgPackSerializer.SerializeToUtf8Bytes(m),
                b => MsgPackSerializer.Deserialize<S8Msgpack>(b)
            ),
            Container<S8Toml, List<HashSet<int>>>(
                "S8",
                "toml",
                Payloads.S8,
                () => new S8Toml(),
                m => m.V,
                (m, v) => m.V = v,
                m => TomlSerializer.SerializeToUtf8Bytes(m),
                b => TomlSerializer.Deserialize<S8Toml>(b)
            ),
            Container<S8Yaml, List<HashSet<int>>>(
                "S8",
                "yaml",
                Payloads.S8,
                () => new S8Yaml(),
                m => m.V,
                (m, v) => m.V = v,
                m => YamlSerializer.SerializeToUtf8Bytes(m),
                b => YamlSerializer.Deserialize<S8Yaml>(b)
            ),
            Container<S8Ini, List<HashSet<int>>>(
                "S8",
                "ini",
                Payloads.S8,
                () => new S8Ini(),
                m => m.V,
                (m, v) => m.V = v,
                m => IniSerializer.SerializeToUtf8Bytes(m),
                b => IniSerializer.Deserialize<S8Ini>(b)
            ),
            // ── S9  Dictionary<string,int> — flat dictionary
            Container<S9Json, Dictionary<string, int>>(
                "S9",
                "json",
                Payloads.S9,
                () => new S9Json(),
                m => m.V,
                (m, v) => m.V = v,
                m => JsonSerializer.SerializeToUtf8Bytes(m),
                b => JsonSerializer.Deserialize<S9Json>(b)
            ),
            Container<S9Msgpack, Dictionary<string, int>>(
                "S9",
                "msgpack",
                Payloads.S9,
                () => new S9Msgpack(),
                m => m.V,
                (m, v) => m.V = v,
                m => MsgPackSerializer.SerializeToUtf8Bytes(m),
                b => MsgPackSerializer.Deserialize<S9Msgpack>(b)
            ),
            Container<S9Toml, Dictionary<string, int>>(
                "S9",
                "toml",
                Payloads.S9,
                () => new S9Toml(),
                m => m.V,
                (m, v) => m.V = v,
                m => TomlSerializer.SerializeToUtf8Bytes(m),
                b => TomlSerializer.Deserialize<S9Toml>(b)
            ),
            Container<S9Yaml, Dictionary<string, int>>(
                "S9",
                "yaml",
                Payloads.S9,
                () => new S9Yaml(),
                m => m.V,
                (m, v) => m.V = v,
                m => YamlSerializer.SerializeToUtf8Bytes(m),
                b => YamlSerializer.Deserialize<S9Yaml>(b)
            ),
            Container<S9Ini, Dictionary<string, int>>(
                "S9",
                "ini",
                Payloads.S9,
                () => new S9Ini(),
                m => m.V,
                (m, v) => m.V = v,
                m => IniSerializer.SerializeToUtf8Bytes(m),
                b => IniSerializer.Deserialize<S9Ini>(b)
            ),
            // ── S10  Dictionary<string,List<int>> — dict value is a collection
            Container<S10Json, Dictionary<string, List<int>>>(
                "S10",
                "json",
                Payloads.S10,
                () => new S10Json(),
                m => m.V,
                (m, v) => m.V = v,
                m => JsonSerializer.SerializeToUtf8Bytes(m),
                b => JsonSerializer.Deserialize<S10Json>(b)
            ),
            Container<S10Msgpack, Dictionary<string, List<int>>>(
                "S10",
                "msgpack",
                Payloads.S10,
                () => new S10Msgpack(),
                m => m.V,
                (m, v) => m.V = v,
                m => MsgPackSerializer.SerializeToUtf8Bytes(m),
                b => MsgPackSerializer.Deserialize<S10Msgpack>(b)
            ),
            Container<S10Toml, Dictionary<string, List<int>>>(
                "S10",
                "toml",
                Payloads.S10,
                () => new S10Toml(),
                m => m.V,
                (m, v) => m.V = v,
                m => TomlSerializer.SerializeToUtf8Bytes(m),
                b => TomlSerializer.Deserialize<S10Toml>(b)
            ),
            Container<S10Yaml, Dictionary<string, List<int>>>(
                "S10",
                "yaml",
                Payloads.S10,
                () => new S10Yaml(),
                m => m.V,
                (m, v) => m.V = v,
                m => YamlSerializer.SerializeToUtf8Bytes(m),
                b => YamlSerializer.Deserialize<S10Yaml>(b)
            ),
            Container<S10Ini, Dictionary<string, List<int>>>(
                "S10",
                "ini",
                Payloads.S10,
                () => new S10Ini(),
                m => m.V,
                (m, v) => m.V = v,
                m => IniSerializer.SerializeToUtf8Bytes(m),
                b => IniSerializer.Deserialize<S10Ini>(b)
            ),
            // ── S12  List<string?> — null ELEMENT in a string list
            Container<S12Json, List<string?>>(
                "S12",
                "json",
                Payloads.S12,
                () => new S12Json(),
                m => m.V,
                (m, v) => m.V = v,
                m => JsonSerializer.SerializeToUtf8Bytes(m),
                b => JsonSerializer.Deserialize<S12Json>(b)
            ),
            Container<S12Msgpack, List<string?>>(
                "S12",
                "msgpack",
                Payloads.S12,
                () => new S12Msgpack(),
                m => m.V,
                (m, v) => m.V = v,
                m => MsgPackSerializer.SerializeToUtf8Bytes(m),
                b => MsgPackSerializer.Deserialize<S12Msgpack>(b)
            ),
            Container<S12Toml, List<string?>>(
                "S12",
                "toml",
                Payloads.S12,
                () => new S12Toml(),
                m => m.V,
                (m, v) => m.V = v,
                m => TomlSerializer.SerializeToUtf8Bytes(m),
                b => TomlSerializer.Deserialize<S12Toml>(b)
            ),
            Container<S12Yaml, List<string?>>(
                "S12",
                "yaml",
                Payloads.S12,
                () => new S12Yaml(),
                m => m.V,
                (m, v) => m.V = v,
                m => YamlSerializer.SerializeToUtf8Bytes(m),
                b => YamlSerializer.Deserialize<S12Yaml>(b)
            ),
            Container<S12Ini, List<string?>>(
                "S12",
                "ini",
                Payloads.S12,
                () => new S12Ini(),
                m => m.V,
                (m, v) => m.V = v,
                m => IniSerializer.SerializeToUtf8Bytes(m),
                b => IniSerializer.Deserialize<S12Ini>(b)
            ),
            // ── S13  Dictionary<string,int?> — null VALUE in a dictionary
            Container<S13Json, Dictionary<string, int?>>(
                "S13",
                "json",
                Payloads.S13,
                () => new S13Json(),
                m => m.V,
                (m, v) => m.V = v,
                m => JsonSerializer.SerializeToUtf8Bytes(m),
                b => JsonSerializer.Deserialize<S13Json>(b)
            ),
            Container<S13Msgpack, Dictionary<string, int?>>(
                "S13",
                "msgpack",
                Payloads.S13,
                () => new S13Msgpack(),
                m => m.V,
                (m, v) => m.V = v,
                m => MsgPackSerializer.SerializeToUtf8Bytes(m),
                b => MsgPackSerializer.Deserialize<S13Msgpack>(b)
            ),
            Container<S13Toml, Dictionary<string, int?>>(
                "S13",
                "toml",
                Payloads.S13,
                () => new S13Toml(),
                m => m.V,
                (m, v) => m.V = v,
                m => TomlSerializer.SerializeToUtf8Bytes(m),
                b => TomlSerializer.Deserialize<S13Toml>(b)
            ),
            Container<S13Yaml, Dictionary<string, int?>>(
                "S13",
                "yaml",
                Payloads.S13,
                () => new S13Yaml(),
                m => m.V,
                (m, v) => m.V = v,
                m => YamlSerializer.SerializeToUtf8Bytes(m),
                b => YamlSerializer.Deserialize<S13Yaml>(b)
            ),
            Container<S13Ini, Dictionary<string, int?>>(
                "S13",
                "ini",
                Payloads.S13,
                () => new S13Ini(),
                m => m.V,
                (m, v) => m.V = v,
                m => IniSerializer.SerializeToUtf8Bytes(m),
                b => IniSerializer.Deserialize<S13Ini>(b)
            ),
            // ── S11a  self recursion (Node.Child : Node)
            SelfRecursive<S11aJson>(
                "S11a",
                "json",
                (v, child) => new S11aJson { Value = v, Child = child },
                m => m.Value,
                m => m.Child,
                m => JsonSerializer.SerializeToUtf8Bytes(m),
                b => JsonSerializer.Deserialize<S11aJson>(b)
            ),
            SelfRecursive<S11aMsgpack>(
                "S11a",
                "msgpack",
                (v, child) => new S11aMsgpack { Value = v, Child = child },
                m => m.Value,
                m => m.Child,
                m => MsgPackSerializer.SerializeToUtf8Bytes(m),
                b => MsgPackSerializer.Deserialize<S11aMsgpack>(b)
            ),
            SelfRecursive<S11aToml>(
                "S11a",
                "toml",
                (v, child) => new S11aToml { Value = v, Child = child },
                m => m.Value,
                m => m.Child,
                m => TomlSerializer.SerializeToUtf8Bytes(m),
                b => TomlSerializer.Deserialize<S11aToml>(b)
            ),
            SelfRecursive<S11aYaml>(
                "S11a",
                "yaml",
                (v, child) => new S11aYaml { Value = v, Child = child },
                m => m.Value,
                m => m.Child,
                m => YamlSerializer.SerializeToUtf8Bytes(m),
                b => YamlSerializer.Deserialize<S11aYaml>(b)
            ),
            SelfRecursive<S11aIni>(
                "S11a",
                "ini",
                (v, child) => new S11aIni { Value = v, Child = child },
                m => m.Value,
                m => m.Child,
                m => IniSerializer.SerializeToUtf8Bytes(m),
                b => IniSerializer.Deserialize<S11aIni>(b)
            ),
            // ── S11b  mutual recursion (M1.Child : M2, M2.Back : M1)
            Mutual<S11bJson, S11bJsonPartner>(
                "S11b",
                "json",
                (name, partner) => new S11bJson { Name = name, B = partner },
                a => a.Name,
                a => a.B,
                (n, back) => new S11bJsonPartner { N = n, A = back },
                p => p.N,
                p => p.A,
                a => JsonSerializer.SerializeToUtf8Bytes(a),
                b => JsonSerializer.Deserialize<S11bJson>(b)
            ),
            Mutual<S11bMsgpack, S11bMsgpackPartner>(
                "S11b",
                "msgpack",
                (name, partner) => new S11bMsgpack { Name = name, B = partner },
                a => a.Name,
                a => a.B,
                (n, back) => new S11bMsgpackPartner { N = n, A = back },
                p => p.N,
                p => p.A,
                a => MsgPackSerializer.SerializeToUtf8Bytes(a),
                b => MsgPackSerializer.Deserialize<S11bMsgpack>(b)
            ),
            Mutual<S11bToml, S11bTomlPartner>(
                "S11b",
                "toml",
                (name, partner) => new S11bToml { Name = name, B = partner },
                a => a.Name,
                a => a.B,
                (n, back) => new S11bTomlPartner { N = n, A = back },
                p => p.N,
                p => p.A,
                a => TomlSerializer.SerializeToUtf8Bytes(a),
                b => TomlSerializer.Deserialize<S11bToml>(b)
            ),
            Mutual<S11bYaml, S11bYamlPartner>(
                "S11b",
                "yaml",
                (name, partner) => new S11bYaml { Name = name, B = partner },
                a => a.Name,
                a => a.B,
                (n, back) => new S11bYamlPartner { N = n, A = back },
                p => p.N,
                p => p.A,
                a => YamlSerializer.SerializeToUtf8Bytes(a),
                b => YamlSerializer.Deserialize<S11bYaml>(b)
            ),
            Mutual<S11bIni, S11bIniPartner>(
                "S11b",
                "ini",
                (name, partner) => new S11bIni { Name = name, B = partner },
                a => a.Name,
                a => a.B,
                (n, back) => new S11bIniPartner { N = n, A = back },
                p => p.N,
                p => p.A,
                a => IniSerializer.SerializeToUtf8Bytes(a),
                b => IniSerializer.Deserialize<S11bIni>(b)
            ),
        ];

    // ── builders ──────────────────────────────────────────────────────────────────

    /// <summary>One cell whose payload is a single member <c>V</c> of the model.</summary>
    private static Cell Container<M, T>(
        string shape,
        string format,
        Payload<T> payload,
        Func<M> create,
        Func<M, T> read,
        Action<M, T> write,
        Func<M, byte[]> serialize,
        Func<byte[], M?> deserialize
    )
        where M : class
        where T : class
    {
        M Filled(T value)
        {
            var model = create();
            write(model, value);
            return model;
        }

        return Raw(
            shape,
            format,
            () => Filled(payload.Empty()),
            () => Filled(payload.Single()),
            () => Filled(payload.Boundary()),
            model => payload.Text(read(model)),
            serialize,
            deserialize
        );
    }

    /// <summary>One cell whose payload IS the model (S11a: <c>Node.Child : Node</c>).</summary>
    private static Cell SelfRecursive<M>(
        string shape,
        string format,
        Func<int, M?, M> node,
        Func<M, int> valueOf,
        Func<M, M?> childOf,
        Func<M, byte[]> serialize,
        Func<byte[], M?> deserialize
    )
        where M : class
    {
        // Tree isomorphism: two graphs are equivalent when the value/child spine matches,
        // independently of any depth limit the wire format applies.
        string Text(M? node1) =>
            node1 is null
                ? "null"
                : "(" + Fmt.Int(valueOf(node1)) + "→" + Text(childOf(node1)) + ")";

        return Raw(
            shape,
            format,
            () => node(0, null),
            () => node(7, null),
            () => node(1, node(2, node(3, null))),
            model => Text(model),
            serialize,
            deserialize
        );
    }

    /// <summary>One cell whose payload IS the model (S11b: <c>M1.Child : M2</c>, mutual).</summary>
    private static Cell Mutual<A, B>(
        string shape,
        string format,
        Func<string, B?, A> makeA,
        Func<A, string> nameOf,
        Func<A, B?> bOf,
        Func<int, A?, B> makeB,
        Func<B, int> nOf,
        Func<B, A?> aOf,
        Func<A, byte[]> serialize,
        Func<byte[], A?> deserialize
    )
        where A : class
        where B : class
    {
        string TextA(A? a) =>
            a is null ? "null" : "A{" + Fmt.Str(nameOf(a)) + "→" + TextB(bOf(a)) + "}";

        string TextB(B? b) =>
            b is null ? "null" : "B{" + Fmt.Int(nOf(b)) + "→" + TextA(aOf(b)) + "}";

        return Raw(
            shape,
            format,
            () => makeA("", null),
            () => makeA("a", makeB(7, null)),
            () => makeA("root", makeB(int.MaxValue, makeA("mid", makeB(-1, makeA("leaf", null))))),
            model => TextA(model),
            serialize,
            deserialize
        );
    }

    private static Cell Raw<M>(
        string shape,
        string format,
        Func<M> empty,
        Func<M> single,
        Func<M> boundary,
        Func<M, string> text,
        Func<M, byte[]> serialize,
        Func<byte[], M?> deserialize
    )
        where M : class =>
        new()
        {
            Shape = shape,
            Format = format,
            ModelType = typeof(M),
            Empty = () => empty(),
            Single = () => single(),
            Boundary = () => boundary(),
            Text = value => text((M)value),
            Serialize = value => serialize((M)value),
            Deserialize = bytes => deserialize(bytes),
        };

    // ── anti-drift check ──────────────────────────────────────────────────────────

    /// <summary>
    /// The registry must cover the 14 × 5 cross product of the capability model exactly
    /// once, and every cell must own a distinct model type. This is what stops the
    /// corpus from silently drifting away from <c>FormatCapabilities.AllShapeIds</c>.
    /// </summary>
    internal static bool Validate(List<Cell> cells, out string error)
    {
        var shapes = Caps.AllShapeIds;
        var formats = Caps.AllFormatTags;

        if (cells.Count != shapes.Count * formats.Count)
        {
            error = $"expected {shapes.Count * formats.Count} cells, found {cells.Count}";
            return false;
        }

        if (cells.Count != 70)
        {
            error = $"the 14x5 matrix is 70 cells, found {cells.Count}";
            return false;
        }

        if (cells.Select(c => c.ModelType).Distinct().Count() != cells.Count)
        {
            error = "two cells share a model type; each cell must have its own model";
            return false;
        }

        var seen = new HashSet<(string, string)>();
        foreach (var cell in cells)
        {
            if (shapes.Contains(cell.Shape, StringComparer.Ordinal) is false)
            {
                error = $"cell {cell.Format}/{cell.Shape}: unknown shape id";
                return false;
            }

            if (formats.Contains(cell.Format, StringComparer.Ordinal) is false)
            {
                error = $"cell {cell.Format}/{cell.Shape}: unknown format tag";
                return false;
            }

            if (seen.Add((cell.Shape, cell.Format)) is false)
            {
                error = $"cell {cell.Format}/{cell.Shape}: declared twice";
                return false;
            }
        }

        foreach (var shape in shapes)
        foreach (var format in formats)
        {
            if (seen.Contains((shape, format)))
                continue;

            error = $"cell {format}/{shape}: missing";
            return false;
        }

        error = "";
        return true;
    }
}
