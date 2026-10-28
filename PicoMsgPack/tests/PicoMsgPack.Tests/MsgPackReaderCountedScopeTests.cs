namespace PicoMsgPack.Tests;

/// <summary>
/// Task perf/mp-counted-scope: the counted scope's semantic surface.
/// <para>
/// The change moves the innermost open container's element accounting off the level
/// stacks and onto two scalars (<c>_topRemaining</c>/<c>_topIsMap</c>), and replaces the
/// map key/value <i>flip</i> with the parity of that counter. Everything the token stream
/// shows must stay exactly as it was, and the place where that is least obvious is
/// pause/resume: <see cref="MsgPackReader.ExportState"/> no longer carries a key/value flag
/// (the flag is now derived), so a reader resumed in the middle of a map has to classify the
/// next string token as a <see cref="TokenType.PropertyName"/> from the exported count alone.
/// </para>
/// <para>
/// These tests exist to make that derivation <b>falsifiable</b>: they dump tokens through the
/// same fixture the other read-side suites use (<see cref="MsgPackSpanTokenDump"/>) and compare,
/// so they fail on a wrong token type, a wrong end-token position or a lost depth — not merely
/// on "the value came back wrong".
/// </para>
/// </summary>
public class MsgPackReaderCountedScopeTests
{
    /// <summary>
    /// The strongest statement available through the public API: stopping at any token, exporting
    /// the state and resuming on the remaining bytes must produce the same tail a reader that
    /// never stopped would have produced. Walked over the whole shared corpus, at every boundary.
    /// </summary>
    [Test]
    public async Task ResumeAtEveryTokenBoundary_ProducesTheSameTail()
    {
        var failures = new List<string>();
        int boundaries = 0;

        foreach (var (name, payload) in MsgPackSpanTokenDump.SmallPayloads())
        {
            var expected = MsgPackSpanTokenDump.DrainSpan(payload);
            var malformed = Throws(payload);
            if (malformed is not null)
                continue; // a payload that cannot be drained has no boundaries to cut at

            for (int k = 0; k <= expected.Count; k++)
            {
                boundaries++;
                var got = DrainWithAResumeAt(payload, k);
                if (got.Count != expected.Count)
                {
                    failures.Add(
                        $"{name} @{k}: {expected.Count} tokens expected, {got.Count} after the resume"
                    );
                    continue;
                }
                for (int i = 0; i < expected.Count; i++)
                {
                    // BytesConsumed is a position *within this reader*, and the resumed reader
                    // starts at zero, so it is the one field a resume is allowed to move.
                    if (Comparable(got[i]) == Comparable(expected[i]))
                        continue;
                    failures.Add($"{name} @{k}: token {i} <{expected[i]}> vs <{got[i]}>");
                    break;
                }
            }
        }

        await Assert
            .That(failures.Count)
            .IsEqualTo(0)
            .Because(
                "a token boundary is not allowed to change the token stream: "
                    + $"{boundaries} boundaries walked, first failures: {string.Join(" | ", failures.Take(5))}"
            );
        await Assert.That(boundaries).IsGreaterThan(500);
    }

    /// <summary>
    /// The explicit, readable version of the case above: a string token's
    /// <see cref="TokenType.PropertyName"/> classification must survive an export/resume in the
    /// middle of a map, where it is now derived from the parity of the remaining element count
    /// rather than read from a stored flag.
    /// </summary>
    [Test]
    public async Task MapKeyValueClassification_SurvivesExportAndResume()
    {
        // fixmap(2) with string keys: {"a": 1, "b": "two"}
        byte[] payload = [0x82, 0xA1, 0x61, 0x01, 0xA1, 0x62, 0xA3, 0x74, 0x77, 0x6F];
        var fresh = MsgPackSpanTokenDump.DrainSpan(payload);
        await Assert
            .That(string.Join(",", fresh.Select(t => t.Type.ToString())))
            .IsEqualTo("ObjectStart,PropertyName,Int32,PropertyName,String,ObjectEnd");

        // stop after the first value (ObjectStart, PropertyName "a", Int32 1) and resume
        var resumed = DrainWithAResumeAt(payload, 3);
        await Assert
            .That(string.Join(",", resumed.Select(t => t.Type.ToString())))
            .IsEqualTo("ObjectStart,PropertyName,Int32,PropertyName,String,ObjectEnd");
        await Assert
            .That(resumed[3].Raw)
            .IsEqualTo("62")
            .Because("the resumed reader must read the second key, not a value");
    }

    /// <summary>
    /// A container that holds nothing opens and closes immediately: the end token is owed at once
    /// and must not consume a byte from what follows (empty array, empty map, both nested).
    /// </summary>
    [Test]
    public async Task EmptyContainers_YieldTheirEndTokenImmediately()
    {
        // fixarray(3) of { empty array, empty map, fixmap(1) whose value is an empty array }
        byte[] payload = [0x93, 0x90, 0x80, 0x81, 0x01, 0x90];
        var dump = MsgPackSpanTokenDump.DrainSpan(payload);

        await Assert
            .That(string.Join(",", dump.Select(t => $"{t.Type}@{t.Depth}")))
            .IsEqualTo(
                "ArrayStart@1,ArrayStart@2,ArrayEnd@1,ObjectStart@2,ObjectEnd@1,"
                    + "ObjectStart@2,Int32@2,ArrayStart@3,ArrayEnd@2,ObjectEnd@1,ArrayEnd@0"
            );
        await Assert.That(dump[^1].Consumed).IsEqualTo((long)payload.Length);
    }

    /// <summary>
    /// Two levels of maps: keys stay <see cref="TokenType.PropertyName"/> and values stay
    /// <see cref="TokenType.String"/> at <i>both</i> depths, i.e. the parity rule follows the
    /// innermost open container instead of leaking a parent's phase into a child.
    /// </summary>
    [Test]
    public async Task NestedMapKeys_ArePropertyNamesAtEveryLevel()
    {
        // fixmap(1) { "outer": fixmap(2) { "k1": "v1", "k2": "v2" } }
        byte[] payload =
        [
            0x81,
            0xA5,
            0x6F,
            0x75,
            0x74,
            0x65,
            0x72, // "outer"
            0x82,
            0xA2,
            0x6B,
            0x31,
            0xA2,
            0x76,
            0x31,
            0xA2,
            0x6B,
            0x32,
            0xA2,
            0x76,
            0x32,
        ];
        var dump = MsgPackSpanTokenDump.DrainSpan(payload);

        await Assert
            .That(
                string.Join(
                    ",",
                    dump.Select(t =>
                        t.Type == TokenType.String || t.Type == TokenType.PropertyName
                            ? $"{t.Type}:{HexText(t.Raw)}"
                            : t.Type.ToString()
                    )
                )
            )
            .IsEqualTo(
                "ObjectStart,PropertyName:6f75746572,ObjectStart,"
                    + "PropertyName:6b31,String:7631,PropertyName:6b32,String:7632,"
                    + "ObjectEnd,ObjectEnd"
            );
    }

    /// <summary>
    /// A count query on the innermost collection must answer for that collection, and the
    /// enclosing level must keep its own count afterwards -- the scope's counter is what both
    /// the end-token decision and this query read.
    /// </summary>
    [Test]
    public async Task CountQuery_FollowsTheInnermostCollection_AndTheOuterOneSurvives()
    {
        // fixmap(2) { 0: fixarray(3) [1,2,3], 1: fixarray(1) [4] }
        byte[] payload = [0x82, 0x00, 0x93, 0x01, 0x02, 0x03, 0x01, 0x91, 0x04];
        var answers = new List<string>();
        {
            var reader = new MsgPackReader(payload);
            reader.Read(); // ObjectStart (2 pairs)
            answers.Add($"outer-start:{Query(ref reader)}");
            reader.Read(); // key 0
            reader.Read(); // inner ArrayStart (3)
            answers.Add($"inner-start:{Query(ref reader)}");
            reader.Read(); // 1
            answers.Add($"inner-after-1:{Query(ref reader)}");
            reader.Read(); // 2
            reader.Read(); // 3
            reader.Read(); // inner ArrayEnd
            answers.Add($"after-inner:{Query(ref reader)}");
            reader.Read(); // key 1
            reader.Read(); // second ArrayStart (1)
            answers.Add($"second-start:{Query(ref reader)}");
            reader.Read(); // 4
            reader.Read(); // ArrayEnd
            reader.Read(); // ObjectEnd
            answers.Add($"closed:{Query(ref reader)}");
        }

        await Assert
            .That(string.Join(",", answers))
            .IsEqualTo(
                "outer-start:2,inner-start:3,inner-after-1:false,after-inner:false,"
                    + "second-start:1,closed:false"
            );
    }

    // ── helpers ──

    static string Query(ref MsgPackReader reader) =>
        reader.TryGetCollectionCount(out var count)
            ? count.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "false";

    /// <summary>
    /// Drain <paramref name="payload"/> but interrupt it after <paramref name="boundary"/> tokens:
    /// export the state, hand the remaining bytes to a resumed reader, and return the whole walk.
    /// </summary>
    static List<MsgPackTokenSnapshot> DrainWithAResumeAt(byte[] payload, int boundary)
    {
        var tokens = new List<MsgPackTokenSnapshot>(payload.Length);
        var head = new MsgPackReader(payload.AsSpan());
        try
        {
            while (tokens.Count < boundary && head.Read())
                tokens.Add(MsgPackSpanTokenDump.Capture(tokens.Count, ref head));

            var state = head.ExportState();
            var remaining = payload.AsSpan((int)head.BytesConsumed).ToArray();
            var tail = new MsgPackReader(new ReadOnlySequence<byte>(remaining), true, state);
            try
            {
                while (tail.Read())
                    tokens.Add(MsgPackSpanTokenDump.Capture(tokens.Count, ref tail));
            }
            finally
            {
                tail.Dispose();
            }
        }
        finally
        {
            head.Dispose();
        }
        return tokens;
    }

    /// <summary>Null when the payload drains cleanly, otherwise the exception it raises.</summary>
    static Exception? Throws(byte[] payload)
    {
        try
        {
            MsgPackSpanTokenDump.DrainSpan(payload);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>
    /// What a resume must preserve, token for token: type, depth, the getter answers, and the
    /// value span <i>of the tokens that carry one</i>.
    /// <para>
    /// Two snapshot fields are deliberately left out, and neither is about element accounting:
    /// <c>BytesConsumed</c> is a position inside the current reader (a resumed reader starts at
    /// zero), and the value span of a token that carries no payload — an end token or nil — is
    /// whatever the previously read token left behind, which a resumed reader has never read.
    /// Both were true before this task's change and are pinned here as a property of resume, so
    /// this test fails on an accounting slip rather than on a known artifact.
    /// </para>
    /// </summary>
    static string Comparable(MsgPackTokenSnapshot t) =>
        string.Join(
            "|",
            t.Index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            t.Type.ToString(),
            t.Depth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            CarriesValue(t.Type) ? t.Raw : "-",
            t.Scalars
        );

    static bool CarriesValue(TokenType type) =>
        type
            is TokenType.String
                or TokenType.PropertyName
                or TokenType.Bytes
                or TokenType.Extension;

    static string HexText(string hex) => hex.ToLowerInvariant();
}
