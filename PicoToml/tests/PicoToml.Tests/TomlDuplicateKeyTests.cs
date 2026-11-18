namespace PicoToml.Tests;

// ── TOML 1.0 §Keys: "Defining a key multiple times is invalid." ──────────────────────────
//
// The reference implementation refuses the document — `tomllib` reports "Cannot overwrite a
// value" and Tomlyn agrees — so a reader that hands both definitions to the caller is
// reporting a document the format does not have, and the second value silently wins. Which
// keys are "already defined" is per TABLE: `[T] A` and `[U] A` are two different keys, and
// `[[Rows]]` opens a NEW table each time (§Array of Tables), so the same key is legal there
// again. Both halves are pinned below — a fix that dedupes globally would break the format.

/// <summary>Two members, so a duplicate at the document root is expressible.</summary>
public class DuplicateKeyTomlModel
{
    public int A { get; set; }
    public int B { get; set; }
}

/// <summary>Array-of-tables fixture: the writer emits one <c>[[Rows]]</c> block per element.</summary>
public class DuplicateKeyTomlRow
{
    public int A { get; set; }
}

/// <summary>Holds the array of tables.</summary>
public class DuplicateKeyTomlRows
{
    public List<DuplicateKeyTomlRow> Rows { get; set; } = new();
}

public class TomlDuplicateKeyTests
{
    /// <summary>
    /// One comparable line describing what the reader did: the PropertyName tokens it produced,
    /// or the FormatException that ended the walk. That is the question the spec answers — "is
    /// this document valid?" — and it keeps "silently accepted" and "refused" visible as two
    /// different answers, instead of asking which of the two values the reader happened to keep.
    /// </summary>
    private static string Describe(string toml)
    {
        var seen = new List<string>();
        try
        {
            var reader = new TomlReader(Encoding.UTF8.GetBytes(toml));
            while (reader.Read())
            {
                if (reader.TokenType == TokenType.PropertyName)
                    seen.Add("PropertyName(" + Encoding.UTF8.GetString(reader.KeySpan) + ")");
            }
        }
        catch (FormatException)
        {
            return seen.Count == 0
                ? "FormatException"
                : "FormatException after " + string.Join(", ", seen);
        }
        return string.Join(", ", seen);
    }

    // ── the bug: a duplicate key is delivered twice, no exception ──

    [Test]
    public async Task DuplicateKey_EndsTheWalkWithFormatException()
    {
        await Assert
            .That(Describe("A = 1\nA = 2\n"))
            .IsEqualTo("FormatException after PropertyName(A)")
            .Because("TOML 1.0 forbids defining a key twice; the second A = ... is not a value");
    }

    [Test]
    public async Task DuplicateKey_ThrowsOnDeserialize()
    {
        await Assert
            .That(() =>
                TomlSerializer.Deserialize<DuplicateKeyTomlModel>("A = 1\nA = 2\n"u8.ToArray())
            )
            .Throws<FormatException>();
    }

    [Test]
    public async Task DuplicateKey_InsideATable_Throws()
    {
        await Assert
            .That(Describe("[T]\nA = 1\nA = 2\n"))
            .IsEqualTo("FormatException after PropertyName(A)");
    }

    [Test]
    public async Task DuplicateDottedKey_Throws()
    {
        await Assert
            .That(Describe("a.b = 1\na.b = 2\n"))
            .IsEqualTo("FormatException after PropertyName(b)");
    }

    // ── the other half: the same key is legal in a different table ──

    [Test]
    public async Task SameKey_InTwoDifferentTables_IsLegal()
    {
        await Assert
            .That(Describe("[T]\nA = 1\n[U]\nA = 2\n"))
            .IsEqualTo("PropertyName(A), PropertyName(A)");
    }

    [Test]
    public async Task RepeatedArrayOfTables_RepeatsItsKeys_IsLegal()
    {
        // [[Rows]] is a NEW table element, so A is defined for the first time in each block.
        await Assert
            .That(Describe("[[Rows]]\nA = 1\n[[Rows]]\nA = 2\n"))
            .IsEqualTo("PropertyName(A), PropertyName(A)");
    }

    [Test]
    public async Task RepeatedArrayOfTables_RoundTrips()
    {
        var doc = new DuplicateKeyTomlRows
        {
            Rows = [new DuplicateKeyTomlRow { A = 1 }, new DuplicateKeyTomlRow { A = 2 }],
        };

        var toml = TomlSerializer.Serialize(doc);
        var back = TomlSerializer.Deserialize<DuplicateKeyTomlRows>(Encoding.UTF8.GetBytes(toml));

        await Assert.That(back!.Rows.Count).IsEqualTo(2);
        await Assert.That(back.Rows[0].A).IsEqualTo(1);
        await Assert.That(back.Rows[1].A).IsEqualTo(2);
    }

    // ── legal documents keep reading ──

    [Test]
    public async Task DistinctKeys_RoundTrip()
    {
        var doc = new DuplicateKeyTomlModel { A = 1, B = 2 };
        var toml = TomlSerializer.Serialize(doc);
        var back = TomlSerializer.Deserialize<DuplicateKeyTomlModel>(Encoding.UTF8.GetBytes(toml));

        await Assert.That(back!.A).IsEqualTo(1);
        await Assert.That(back.B).IsEqualTo(2);
    }

    [Test]
    public async Task DistinctDottedKeys_AreLegal()
    {
        await Assert
            .That(Describe("a.b = 1\na.c = 2\n"))
            .IsEqualTo("PropertyName(b), PropertyName(c)");
    }

    /// <summary>
    /// The streaming path builds a NEW reader per chunk (<c>TomlSerializer.Step</c>), so the
    /// definition record has to travel in <c>TomlReaderState</c> — otherwise the same document
    /// is strict when buffered and lax when streamed.
    /// </summary>
    [Test]
    public async Task DuplicateKey_Streaming_Throws()
    {
        using var stream = new MemoryStream("A = 1\nA = 2\n"u8.ToArray());

        var threw = false;
        try
        {
            await TomlSerializer.DeserializeFromStreamAsync<DuplicateKeyTomlModel>(stream);
        }
        catch (FormatException)
        {
            threw = true;
        }

        await Assert
            .That(threw)
            .IsTrue()
            .Because("the chunked path must reach the same verdict as the buffered one");
    }
}
