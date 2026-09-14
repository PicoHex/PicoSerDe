namespace PicoSerDe.Core.Tests;

/// <summary>
/// ScalarCodec fail-loud contract: malformed extended-scalar text must throw
/// FormatException, including inputs longer than the internal stack buffers
/// (review P2-3: those used to escape as ArgumentException).
/// </summary>
public class ScalarCodecTests
{
    private static readonly byte[] LongText = Encoding.UTF8.GetBytes(new string('a', 100));

    [Test]
    public async Task ParseChar_OverLongText_ThrowsFormatException()
    {
        await Assert.That(() => ScalarCodec.ParseChar(LongText)).Throws<FormatException>();
    }

    [Test]
    public async Task ParseVersion_OverLongText_ThrowsFormatException()
    {
        await Assert.That(() => ScalarCodec.ParseVersion(LongText)).Throws<FormatException>();
    }

    [Test]
    public async Task ParseHalf_OverLongText_ThrowsFormatException()
    {
        await Assert.That(() => ScalarCodec.ParseHalf(LongText)).Throws<FormatException>();
    }

    [Test]
    public async Task ParseInt128_OverLongText_ThrowsFormatException()
    {
        await Assert.That(() => ScalarCodec.ParseInt128(LongText)).Throws<FormatException>();
    }

    [Test]
    public async Task ParseUInt128_OverLongText_ThrowsFormatException()
    {
        await Assert.That(() => ScalarCodec.ParseUInt128(LongText)).Throws<FormatException>();
    }

    [Test]
    public async Task ParseNInt_OverLongText_ThrowsFormatException()
    {
        await Assert.That(() => ScalarCodec.ParseNInt(LongText)).Throws<FormatException>();
    }

    [Test]
    public async Task ParseDateTimeOffset_OverLongText_ThrowsFormatException()
    {
        await Assert
            .That(() => ScalarCodec.ParseDateTimeOffset(LongText))
            .Throws<FormatException>();
    }

    [Test]
    public async Task ParseBigInteger_OverLongInvalidText_ThrowsFormatException()
    {
        await Assert.That(() => ScalarCodec.ParseBigInteger(LongText)).Throws<FormatException>();
    }
}
