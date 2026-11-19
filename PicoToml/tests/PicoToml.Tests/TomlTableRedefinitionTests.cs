namespace PicoToml.Tests;

// ── TOML 1.0 §Table / §Array of Tables / §Inline Table: WHAT A TABLE IS decides whether a
//    header may name it again. ─────────────────────────────────────────────────────────
//
// The matrix below was measured line by line against the reference implementation
// (`python -c "import tomllib; tomllib.loads(...)"`), so every row is a fact about the format
// rather than a preference. The rule the matrix states is not "a header repeats" — it is the
// table's ORIGIN:
//
//   • a table a header declared        → a second [header] is invalid   (§Table)
//   • an array of tables              → [[T]] appends, [T] is invalid  (§Array of Tables)
//   • a table a dotted key created    → a [header] may not open it      (§Table)
//   • an inline table / a plain value → closed, no header may open it   (§Inline Table)
//   • a super-table a CHILD header created → declaring it afterwards is ok (§Table)
//
// That last line is why "reject every repeated path" is the wrong fix, and why five of the
// eleven rows are legal documents that must keep reading.

/// <summary>Root-level scalars, so the serializer paths below have a real target.</summary>
public class TableMatrixModel
{
    public int A { get; set; }
    public int B { get; set; }
    public int C { get; set; }
}

/// <summary>A member named like the tables in the matrix: the sub-table rows reach it.</summary>
public class TableMatrixOuter
{
    public TableMatrixInner T { get; set; } = new();
}

/// <summary>
/// <c>T</c>: a sub-table (<c>T.S</c>) and its own key (<c>B</c>), so the row that declares the
/// sub-table FIRST and its table afterwards has somewhere real to land.
/// </summary>
public class TableMatrixInner
{
    public TableMatrixSub S { get; set; } = new();
    public int B { get; set; }
}

/// <summary><c>T.S</c>.</summary>
public class TableMatrixSub
{
    public int A { get; set; }
}

public class TomlTableRedefinitionTests
{
    /// <summary>
    /// One comparable line: the tokens the walk produced, or the <see cref="FormatException"/>
    /// that ended it. Keeping both halves visible is the point — a refusal must not also have
    /// delivered the definition it refused.
    /// </summary>
    private static string Describe(string toml)
    {
        var seen = new List<string>();
        try
        {
            var reader = new TomlReader(Encoding.UTF8.GetBytes(toml));
            while (reader.Read())
            {
                if (reader.TokenType is TokenType.ObjectStart or TokenType.ArrayStart)
                    seen.Add(
                        reader.TokenType + "(" + Encoding.UTF8.GetString(reader.TablePath) + ")"
                    );
                else if (reader.TokenType == TokenType.PropertyName)
                    seen.Add("PropertyName(" + Encoding.UTF8.GetString(reader.KeySpan) + ")");
            }
        }
        catch (FormatException ex)
        {
            seen.Add("FormatException: " + ex.Message);
        }
        return string.Join(" | ", seen);
    }

    /// <summary>The same question through the public entry point, so the reader is not the only witness.</summary>
    private static string DeserializeVerdict(string toml)
    {
        try
        {
            TomlSerializer.Deserialize<TableMatrixModel>(Encoding.UTF8.GetBytes(toml));
            return "OK";
        }
        catch (FormatException ex)
        {
            return "FormatException: " + ex.Message;
        }
    }

    /// <summary>Asserts the walk was refused, and that the refusal names the clause and the table.</summary>
    private static async Task AssertRefused(string toml, string table, string clause)
    {
        var verdict = Describe(toml);

        await Assert.That(verdict).Contains("FormatException").Because(verdict);
        await Assert.That(verdict).Contains("'" + table + "'").Because(verdict);
        await Assert.That(verdict).Contains(clause).Because(verdict);
        await Assert.That(DeserializeVerdict(toml)).Contains("FormatException");
    }

    // ── row 1: [T] … [T] → tomllib: "Cannot declare ('T',) twice" ──

    [Test]
    public async Task Row1_TableHeaderDeclaredTwice_IsRefused()
    {
        var verdict = Describe("[T]\nA = 1\n[T]\nB = 2\n");

        await Assert.That(verdict).Contains("FormatException").Because(verdict);
        await Assert.That(verdict).Contains("'T'").Because(verdict);
        await Assert
            .That(verdict)
            .Contains("§Table")
            .Because("TOML 1.0 §Table: \"you cannot define a table more than once\"");
        // The refused header is not delivered: B is never read as a member of anything.
        await Assert.That(verdict).Contains("PropertyName(A)").Because(verdict);
        await Assert.That(verdict).DoesNotContain("PropertyName(B)").Because(verdict);
    }

    // ── row 2: [T] … [T.S] (different key) → OK ──

    [Test]
    public async Task Row2_SubTableAfterItsTable_IsLegal()
    {
        await Assert
            .That(Describe("[T]\nA = 1\n[T.S]\nB = 2\n"))
            .IsEqualTo("ObjectStart(T) | PropertyName(A) | ObjectStart(T.S) | PropertyName(B)");
    }

    // ── row 3: [T.S] … [T] → OK (a super-table may be declared afterwards) ──

    [Test]
    public async Task Row3_TableAfterItsSubTable_IsLegal()
    {
        await Assert
            .That(Describe("[T.S]\nA = 1\n[T]\nB = 2\n"))
            .IsEqualTo("ObjectStart(T.S) | PropertyName(A) | ObjectStart(T) | PropertyName(B)");
    }

    /// <summary>
    /// The same legality through the public entry point, in the order the writer emits (parent, then
    /// sub-table): this is the shape every legal document in this file has to survive.
    /// </summary>
    [Test]
    public async Task Row2_SubTableAfterItsTable_Deserializes()
    {
        var back = TomlSerializer.Deserialize<TableMatrixOuter>(
            Encoding.UTF8.GetBytes("[T]\nB = 2\n[T.S]\nA = 1\n")
        );

        await Assert.That(back!.T.S.A).IsEqualTo(1);
        await Assert.That(back.T.B).IsEqualTo(2);
    }

    /// <summary>
    /// Row 3's document is legal — the reader must deliver it (above). What the GENERATED reader
    /// binds out of it is a different question and not this lane's: on <c>99ad2f1</c> a table
    /// declared <i>after</i> its sub-table already came back empty (<c>T.S.A = 0</c> for
    /// <c>[T.S]</c> … <c>[T]</c>, while the writer's own order round-trips), because the generated
    /// loop can only bind a sub-table it has not walked past yet. Pinned here so the fact is
    /// recorded rather than rediscovered, and so a future fix has a witness.
    /// </summary>
    [Test]
    public async Task Row3_TableAfterItsSubTable_BindsOnlyAfterTheParentIsSeen()
    {
        var outOfOrder = TomlSerializer.Deserialize<TableMatrixOuter>(
            Encoding.UTF8.GetBytes("[T.S]\nA = 1\n[T]\nB = 2\n")
        );
        var inOrder = TomlSerializer.Deserialize<TableMatrixOuter>(
            Encoding.UTF8.GetBytes("[T]\nB = 2\n[T.S]\nA = 1\n")
        );

        await Assert.That(inOrder!.T.S.A).IsEqualTo(1);
        await Assert.That(outOfOrder!.T.B).IsEqualTo(2);
        await Assert
            .That(outOfOrder.T.S.A)
            .IsEqualTo(0)
            .Because("pre-existing generated-reader limit, unchanged by the table registry");
    }

    // ── row 4: [[T]] … [[T]] → OK (appends an element) ──

    [Test]
    public async Task Row4_RepeatedArrayOfTables_IsLegal()
    {
        await Assert
            .That(Describe("[[T]]\nA = 1\n[[T]]\nA = 2\n"))
            .IsEqualTo("ArrayStart(T) | PropertyName(A) | ArrayStart(T) | PropertyName(A)");
    }

    // ── row 5: [T] … [[T]] → invalid (a table cannot become an array) ──

    [Test]
    public async Task Row5_ArrayOfTablesAfterTable_IsRefused()
    {
        await AssertRefused("[T]\nA = 1\n[[T]]\nA = 2\n", "T", "§Array of Tables");
    }

    // ── row 6: [[T]] … [T] → invalid (an array cannot become a table) ──

    [Test]
    public async Task Row6_TableAfterArrayOfTables_IsRefused()
    {
        await AssertRefused("[[T]]\nA = 1\n[T]\nA = 2\n", "T", "§Array of Tables");
    }

    // ── row 7: a.b = 1 … [a] → invalid (a dotted key created the table) ──

    [Test]
    public async Task Row7_HeaderOverADottedKeyTable_IsRefused()
    {
        await AssertRefused("a.b = 1\n[a]\nc = 2\n", "a", "§Table");
    }

    // ── row 8: [a] … a.b = 1 → OK (the dotted key is relative to the current table) ──

    [Test]
    public async Task Row8_DottedKeyUnderATable_IsLegal()
    {
        await Assert
            .That(Describe("[a]\nb.c = 1\n"))
            .IsEqualTo("ObjectStart(a) | ObjectStart(b) | PropertyName(c)");
    }

    // ── row 9: a.b = 1 … a.b = 2 → invalid (already refused, and it must stay refused) ──

    [Test]
    public async Task Row9_SameDottedKeyTwice_IsRefused()
    {
        await AssertRefused("a.b = 1\na.b = 2\n", "a.b", "§Keys");
    }

    // ── row 10: T = {A=1} … [T] → invalid (an inline table is closed) ──

    [Test]
    public async Task Row10_HeaderOverAnInlineTable_IsRefused()
    {
        await AssertRefused("T = {A = 1}\n[T]\nB = 2\n", "T", "§Inline Table");
    }

    // ── row 11: [[T]] … [[T.S]] → OK (a sub-array under the most recent element) ──

    [Test]
    public async Task Row11_SubArrayTableUnderAnArrayElement_IsLegal()
    {
        await Assert
            .That(Describe("[[T]]\nA = 1\n[[T.S]]\nB = 2\n"))
            .IsEqualTo("ArrayStart(T) | PropertyName(A) | ArrayStart(T.S) | PropertyName(B)");
    }

    // ── the same origin rule on paths the matrix does not spell out, both directions ──

    /// <summary>
    /// Two ways of writing one table are one table: the quote is syntax (§Table: "naming rules for
    /// tables are the same as for keys") and whitespace around a segment is ignored
    /// ("[ g . h . i ]  # same as [g.h.i]"). Identity is the DECODED segment, or the registry
    /// would let <c>["T"]</c> … <c>[T]</c> declare one table twice.
    /// </summary>
    [Test]
    public async Task QuotedHeader_IsTheSameTable()
    {
        await AssertRefused("[\"T\"]\nA = 1\n[T]\nB = 2\n", "T", "§Table");
    }

    /// <summary>A quoted segment keeps every character it holds, whitespace included.</summary>
    [Test]
    public async Task QuotedHeaderWithSpaces_RepeatsInTheSameForm_IsRefused()
    {
        await AssertRefused("[\"a b\"]\nA = 1\n[\"a b\"]\nB = 2\n", "a b", "§Table");
    }

    /// <summary>Whitespace around a bare segment is not part of the name.</summary>
    [Test]
    public async Task WhitespaceAroundSegments_IsIgnored()
    {
        await AssertRefused("[ g . h ]\nA = 1\n[g.h]\nB = 2\n", "g.h", "§Table");
    }

    /// <summary>
    /// …but <c>'b.c'</c> is ONE segment: <c>[a.'b.c']</c> and <c>[a.b.c]</c> are two different
    /// tables, so neither may refuse the other.
    /// </summary>
    [Test]
    public async Task QuotedSegmentWithADot_IsADifferentTable()
    {
        await Assert
            .That(Describe("[a.'b.c']\nA = 1\n[a.b.c]\nB = 2\n"))
            .IsEqualTo(
                "ObjectStart(a.'b.c') | PropertyName(A) | ObjectStart(a.b.c) | PropertyName(B)"
            );
    }

    // ── a VALUE is in the way: nothing may be declared beneath it ──

    /// <summary>
    /// <c>[T]</c> / <c>A = 1</c> then <c>[T.A.B]</c> only reaches B by walking through the value
    /// A, which §Keys forbids ("Cannot overwrite a value" in the reference implementation).
    /// </summary>
    [Test]
    public async Task HeaderUnderAValue_IsRefused()
    {
        await AssertRefused("[T]\nA = 1\n[T.A.B]\nC = 2\n", "T.A", "§Keys");
    }

    /// <summary>The dotted-key spelling of the same walk: <c>a.b = 1</c> then <c>a.b.c = 2</c>.</summary>
    [Test]
    public async Task DottedKeyThroughAValue_IsRefused()
    {
        await AssertRefused("a.b = 1\na.b.c = 2\n", "a.b", "§Keys");
    }

    /// <summary>
    /// §Array of Tables: "attempting to append to a statically defined array, even if that array is
    /// empty, must produce an error at parse time".
    /// </summary>
    [Test]
    public async Task ArrayOfTablesOverAStaticalArray_IsRefused()
    {
        await AssertRefused("fruits = []\n[[fruits]]\n", "fruits", "§Array of Tables");
    }

    /// <summary>
    /// §Array of Tables: "attempting to redefine a normal table as an array must likewise produce a
    /// parse-time error" — and a table a CHILD header created is a normal table for that purpose.
    /// </summary>
    [Test]
    public async Task ArrayOfTablesOverASuperTable_IsRefused()
    {
        await AssertRefused("[T.U]\nA = 1\n[[T]]\nB = 2\n", "T", "§Array of Tables");
    }

    /// <summary>While a sub-array declared first leaves its parent a legal super-table.</summary>
    [Test]
    public async Task TableAfterAnArrayTablesSubArray_IsLegal()
    {
        await Assert
            .That(Describe("[[T.U]]\nA = 1\n[T]\nB = 2\n"))
            .IsEqualTo("ArrayStart(T.U) | PropertyName(A) | ObjectStart(T) | PropertyName(B)");
    }

    /// <summary>
    /// An element of an array of tables is a fresh namespace (§Array of Tables: "each subsequent
    /// instance creates and defines a new table element"), so the sub-table repeats legally —
    /// a registry that keyed on the path alone would refuse this.
    /// </summary>
    [Test]
    public async Task SubTable_RepeatsPerArrayElement()
    {
        await Assert
            .That(Describe("[[Rows]]\n[Rows.S]\nA = 1\n[[Rows]]\n[Rows.S]\nA = 2\n"))
            .IsEqualTo(
                "ArrayStart(Rows) | ObjectStart(Rows.S) | PropertyName(A)"
                    + " | ArrayStart(Rows) | ObjectStart(Rows.S) | PropertyName(A)"
            );
    }

    /// <summary>…while twice inside ONE element is still a redefinition.</summary>
    [Test]
    public async Task SubTable_TwiceInTheSameArrayElement_IsRefused()
    {
        await AssertRefused("[[Rows]]\n[Rows.S]\nA = 1\n[Rows.S]\nA = 2\n", "Rows.S", "§Table");
    }

    /// <summary>A sub-table may still be declared twice — once per element — after a later element opened.</summary>
    [Test]
    public async Task SubSubTable_OfASubTable_IsLegal()
    {
        await Assert
            .That(Describe("[T]\n[T.S]\n[T.S.T]\nA = 1\n"))
            .IsEqualTo("ObjectStart(T) | ObjectStart(T.S) | ObjectStart(T.S.T) | PropertyName(A)");
    }

    /// <summary>A scalar key is a value, not a table: a header may not open it (§Table "cannot define more than once" / overwrite).</summary>
    [Test]
    public async Task HeaderOverAScalarKey_IsRefused()
    {
        await AssertRefused("[T]\nS = 1\n[T.S]\nA = 2\n", "T.S", "§Keys");
    }

    /// <summary>
    /// The streaming path builds a NEW reader per chunk (<c>TomlSerializer.Step</c>), so the table
    /// record has to travel in <c>TomlReaderState</c> — otherwise the same document is strict when
    /// buffered and lax when streamed. 1-, 2- and 3-byte chunks put a boundary inside nearly every
    /// header and dotted key in this matrix.
    /// </summary>
    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task EveryMatrixRow_GetsTheSameVerdictFromTheChunkedPath(int chunk)
    {
        foreach (var (toml, label) in Matrix())
        {
            // The offset inside these messages is chunk-relative on the resumed path (that reader
            // only sees the bytes it was handed), so the VERDICT and the clause are what must
            // agree: both paths must refuse the same documents, for the same reason.
            var buffered = WithoutOffset(DeserializeVerdict(toml));
            var streamed = WithoutOffset(await StreamedVerdict(toml, chunk));

            await Assert
                .That(streamed)
                .IsEqualTo(buffered)
                .Because($"chunk={chunk}, {label}: the two paths must reach one verdict ({toml})");
        }
    }

    /// <summary>The matrix as data, so the chunked-parity test cannot drift from the rows above.</summary>
    private static IEnumerable<(string Toml, string Label)> Matrix()
    {
        yield return ("[T]\nA = 1\n[T]\nB = 2\n", "row 1: [T] twice");
        yield return ("[T]\nA = 1\n[T.S]\nB = 2\n", "row 2: [T] then [T.S]");
        yield return ("[T.S]\nA = 1\n[T]\nB = 2\n", "row 3: [T.S] then [T]");
        yield return ("[[T]]\nA = 1\n[[T]]\nA = 2\n", "row 4: [[T]] twice");
        yield return ("[T]\nA = 1\n[[T]]\nA = 2\n", "row 5: [T] then [[T]]");
        yield return ("[[T]]\nA = 1\n[T]\nA = 2\n", "row 6: [[T]] then [T]");
        yield return ("a.b = 1\n[a]\nc = 2\n", "row 7: dotted then [a]");
        yield return ("[a]\nb.c = 1\n", "row 8: [a] then dotted");
        yield return ("a.b = 1\na.b = 2\n", "row 9: dotted twice");
        yield return ("T = {A = 1}\n[T]\nB = 2\n", "row 10: inline table then [T]");
        yield return ("[[T]]\nA = 1\n[[T.S]]\nB = 2\n", "row 11: [[T]] then [[T.S]]");
        yield return (
            "[[Rows]]\n[Rows.S]\nA = 1\n[[Rows]]\n[Rows.S]\nA = 2\n",
            "per-element sub-table"
        );
        yield return (
            "[[Rows]]\n[Rows.S]\nA = 1\n[Rows.S]\nA = 2\n",
            "sub-table twice in one element"
        );
        yield return ("[\"T\"]\nA = 1\n[T]\nB = 2\n", "quoted header, then the bare one");
        yield return ("[ g . h ]\nA = 1\n[g.h]\nB = 2\n", "spacing around segments");
        yield return ("[a.'b.c']\nA = 1\n[a.b.c]\nB = 2\n", "quoted segment with a dot");
        yield return ("[T]\nA = 1\n[T.A.B]\nC = 2\n", "header under a value");
        yield return ("a.b = 1\na.b.c = 2\n", "dotted key through a value");
        yield return ("fruits = []\n[[fruits]]\n", "array of tables over an array value");
        yield return ("[T.U]\nA = 1\n[[T]]\nB = 2\n", "array of tables over a super-table");
        yield return ("[[T.U]]\nA = 1\n[T]\nB = 2\n", "table after a sub-array");
        yield return ("[a]\nb.c = 1\nb.d = 2\n", "two dotted keys through one created table");
    }

    private static string WithoutOffset(string verdict) =>
        Regex.Replace(verdict, @"offset \d+", "offset N");

    private static async Task<string> StreamedVerdict(string toml, int chunk)
    {
        using var stream = new TinyChunkStream(Encoding.UTF8.GetBytes(toml), chunk);
        try
        {
            var value = await TomlSerializer.DeserializeFromStreamAsync<TableMatrixModel>(stream);
            return value is null ? "OK(null)" : "OK";
        }
        catch (FormatException ex)
        {
            return "FormatException: " + ex.Message;
        }
    }
}

/// <summary>Hands out at most <c>chunk</c> bytes per read, so a chunk boundary lands inside tokens.</summary>
public sealed class TinyChunkStream(byte[] data, int chunk) : Stream
{
    private int _pos;

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

    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = Math.Min(Math.Min(count, chunk), data.Length - _pos);
        Array.Copy(data, _pos, buffer, offset, n);
        _pos += n;
        return n;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        await Task.Yield();
        int n = Math.Min(Math.Min(buffer.Length, chunk), data.Length - _pos);
        data.AsSpan(_pos, n).CopyTo(buffer.Span);
        _pos += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
}
