using System.Buffers;

namespace PicoJetson.Tests;

/// <summary>
/// Regression tests for reader/writer robustness issues found in code review.
/// Note: JsonReader is a ref struct — all reader interaction happens before
/// the first await; values are captured into locals.
/// </summary>
public class ReaderRobustnessTests
{
    // ── C3: AllowTrailingCommas must not return a stale token ──

    [Test]
    public async Task AllowTrailingCommas_Array_EmitsArrayEndToken()
    {
        var opts = new JsonOptions { AllowTrailingCommas = true };
        {
            var reader = new JsonReader("[1,2,]"u8, options: opts);
            var ok1 = reader.Read();
            var t1 = reader.TokenType;
            var ok2 = reader.Read();
            var ok3 = reader.Read();
            var ok4 = reader.Read();
            var t4 = reader.TokenType;
            var ok5 = reader.Read();

            await Assert.That(ok1).IsTrue();
            await Assert.That(t1).IsEqualTo(TokenType.ArrayStart);
            await Assert.That(ok2).IsTrue(); // 1
            await Assert.That(ok3).IsTrue(); // 2
            await Assert.That(ok4).IsTrue(); // ,] → ArrayEnd
            await Assert.That(t4).IsEqualTo(TokenType.ArrayEnd);
            await Assert.That(ok5).IsFalse(); // EOF
        }
    }

    [Test]
    public async Task AllowTrailingCommas_Object_EmitsObjectEndToken()
    {
        var opts = new JsonOptions { AllowTrailingCommas = true };
        {
            var reader = new JsonReader("{\"a\":1,}"u8, options: opts);
            var ok1 = reader.Read();
            var t1 = reader.TokenType;
            var ok2 = reader.Read();
            var ok3 = reader.Read();
            var ok4 = reader.Read();
            var t4 = reader.TokenType;
            var ok5 = reader.Read();

            await Assert.That(ok1).IsTrue();
            await Assert.That(t1).IsEqualTo(TokenType.ObjectStart);
            await Assert.That(ok2).IsTrue(); // PropertyName
            await Assert.That(ok3).IsTrue(); // 1
            await Assert.That(ok4).IsTrue(); // ,} → ObjectEnd
            await Assert.That(t4).IsEqualTo(TokenType.ObjectEnd);
            await Assert.That(ok5).IsFalse(); // EOF
        }
    }

    // ── H5: comment handling strictness ──

    [Test]
    public async Task SkipComments_BareSlash_Throws()
    {
        var opts = new JsonOptions { ReadCommentHandling = JsonCommentHandling.Skip };
        {
            var reader = new JsonReader("{\"a\":1} /5"u8, options: opts);
            var ok1 = reader.Read();
            var ok2 = reader.Read();
            var ok3 = reader.Read();
            var ok4 = reader.Read();
            var threw = false;
            try
            {
                reader.Read();
            }
            catch (FormatException)
            {
                threw = true;
            }

            await Assert.That(ok1).IsTrue(); // {
            await Assert.That(ok2).IsTrue(); // PropertyName
            await Assert.That(ok3).IsTrue(); // 1
            await Assert.That(ok4).IsTrue(); // }
            await Assert.That(threw).IsTrue();
        }
    }

    [Test]
    public async Task SkipComments_UnterminatedBlockCommentInFinalBlock_Throws()
    {
        var opts = new JsonOptions { ReadCommentHandling = JsonCommentHandling.Skip };
        {
            var reader = new JsonReader(
                "{\"a\":1} /* never closed"u8,
                maxDepth: 64,
                isFinalBlock: true,
                options: opts
            );
            var threw = false;
            try
            {
                while (reader.Read()) { }
            }
            catch (FormatException)
            {
                threw = true;
            }

            await Assert.That(threw).IsTrue();
        }
    }

    // ── C4: seq-mode unicode escape near rented buffer end ──

    [Test]
    public async Task ReadString_SeqMode_UnicodeEscapeNearBufferEnd_DoesNotOverflow()
    {
        // 254 ASCII bytes + a literal 4-byte surrogate-pair escape: the escape
        // write starts at index 254 of a 256-byte rented buffer.
        var content = new string('a', 254) + "\uD83D\uDE00";
        var json = "\"" + new string('a', 254) + "\\uD83D\\uDE00" + "\"";
        var bytes = Encoding.UTF8.GetBytes(json);
        var seq = new ReadOnlySequence<byte>(bytes);
        var reader = new JsonReader(seq);
        var ok = reader.Read();
        var tt = reader.TokenType;
        var rawLen = reader.GetStringRaw().Length;
        var decoded = Encoding.UTF8.GetString(reader.GetStringRaw());

        await Assert.That(ok).IsTrue();
        await Assert.That(tt).IsEqualTo(TokenType.String);
        await Assert.That(rawLen).IsEqualTo(258);
        await Assert.That(decoded).IsEqualTo(content);
    }

    // ── M4: TryReadNextInt32 overflow handling ──

    [Test]
    public async Task TryReadNextInt32Span_Overflow_ReturnsFalse()
    {
        var reader = new JsonReader("99999999999"u8);
        var result = reader.TryReadNextInt32(out _);
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task TryReadNextInt32Seq_LongDigitRun_ReturnsFalseWithoutThrowing()
    {
        var bytes = "12345678901234567"u8.ToArray();
        var seq = new ReadOnlySequence<byte>(bytes);
        var reader = new JsonReader(seq);
        var result = reader.TryReadNextInt32(out _);
        await Assert.That(result).IsFalse();
    }

    // ── M1: JsonWriter maxDepth above bitmask capacity ──

    [Test]
    public async Task JsonWriter_MaxDepthAbove63_ThrowsAtConstruction()
    {
        var writer = new ArrayBufferWriter<byte>();
        var threw = false;
        try
        {
            var jw = new JsonWriter(writer, maxDepth: 100);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task TrySkip_OnScalarToken_ConsumesNothing()
    {
        var reader = new JsonReader("{\"a\":1,\"b\":2}"u8);
        var t0 = reader.Read(); // {
        var t1 = reader.Read(); // PN a
        var t2 = reader.Read(); // 1 (the value)
        var skipped = reader.TrySkip();
        // After TrySkip on the scalar value, the next token must be "b",
        // not its value.
        var t3 = reader.Read();
        var nextTok = reader.TokenType;
        var nextRaw = Encoding.UTF8.GetString(reader.GetStringRaw());

        await Assert.That(t0).IsTrue();
        await Assert.That(t1).IsTrue();
        await Assert.That(t2).IsTrue();
        await Assert.That(skipped).IsTrue();
        await Assert.That(t3).IsTrue();
        await Assert.That(nextTok).IsEqualTo(TokenType.PropertyName);
        await Assert.That(nextRaw).IsEqualTo("b");
    }

    [Test]
    public async Task TrySkip_OnContainerToken_SkipsContentsOnly()
    {
        var reader = new JsonReader("{\"a\":{\"x\":1},\"b\":2}"u8);
        reader.Read(); // {
        reader.Read(); // PN a
        reader.Read(); // {
        var skipped = reader.TrySkip();
        var t = reader.Read();
        var nextTok = reader.TokenType;
        var nextRaw = Encoding.UTF8.GetString(reader.GetStringRaw());

        await Assert.That(skipped).IsTrue();
        await Assert.That(t).IsTrue();
        await Assert.That(nextTok).IsEqualTo(TokenType.PropertyName);
        await Assert.That(nextRaw).IsEqualTo("b");
    }

    // ── C5-json: fast-path array readers bail on overflow instead of wrapping ──

    [Test]
    public async Task TryReadInt32ArrayFast_Overflow_BailsOut()
    {
        var reader = new JsonReader("[99999999999]"u8);
        var ok = reader.Read();
        var buf = new int[1];
        var n = reader.TryReadInt32ArrayFast(buf);
        await Assert.That(ok).IsTrue();
        await Assert.That(n).IsEqualTo(0);
    }

    [Test]
    public async Task TryReadInt64ArrayFast_Overflow_BailsOut()
    {
        var reader = new JsonReader("[99999999999999999999]"u8);
        var ok = reader.Read();
        var buf = new long[1];
        var n = reader.TryReadInt64ArrayFast(buf);
        await Assert.That(ok).IsTrue();
        await Assert.That(n).IsEqualTo(0);
    }
}
