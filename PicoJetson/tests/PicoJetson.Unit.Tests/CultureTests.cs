using System.Globalization;

namespace PicoJetson.Tests;

/// <summary>
/// Review C-14 / I-24: number formatting must not depend on the ambient culture.
/// <para>
/// A generated JSON serializer writes numbers through
/// <see cref="JsonWriter.WriteNumber(decimal)"/>, so that is where the culture
/// contract can be checked without a source-generated type. (The end-to-end
/// de-DE DTO round-trip lives in <c>PicoJetson.Integration.Tests</c>, where the
/// generator actually runs — the unit-test project has no generator reference,
/// so hand-writing the read side here would only assert a copy of the generated
/// parsing logic against itself.)
/// </para>
/// <para>
/// Culture is process-wide mutable state and TUnit runs tests in parallel, so
/// every culture-mutating test opts out with <see cref="NotInParallelAttribute"/>.
/// </para>
/// </summary>
[NotInParallel]
public class CultureTests
{
    [Test]
    public async Task Decimal_WriterIsInvariantUnderDeDe()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            var buffer = new ArrayBufferWriter<byte>();
            var writer = new JsonWriter(buffer);
            writer.WriteNumber(1.5m);

            var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
            await Assert.That(json).IsEqualTo("1.5");
            await Assert.That(json).DoesNotContain("1,5");
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }

    [Test]
    public async Task Double_WriterIsInvariantUnderDeDe()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            var buffer = new ArrayBufferWriter<byte>();
            var writer = new JsonWriter(buffer);
            writer.WriteNumber(2.25);

            await Assert.That(Encoding.UTF8.GetString(buffer.WrittenSpan)).IsEqualTo("2.25");
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }

    [Test]
    public async Task InvariantDecimalText_ReadsAsNumberTokenUnderDeDe()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            // JsonReader is a ref struct, so read synchronously and assert afterwards.
            var (read, tokenType, text) = ReadFirstToken("1.5");
            await Assert.That(read).IsTrue();
            await Assert.That(tokenType).IsEqualTo(TokenType.Float64);
            await Assert.That(text).IsEqualTo("1.5");
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }

    private static (bool Read, TokenType TokenType, string Text) ReadFirstToken(string json)
    {
        var reader = new JsonReader(Encoding.UTF8.GetBytes(json));
        var read = reader.Read();
        return (read, reader.TokenType, Encoding.UTF8.GetString(reader.GetStringRaw()));
    }
}
