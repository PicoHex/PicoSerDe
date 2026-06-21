namespace PicoJetson.Tests;

public class LargeIntegerModel
{
    public int IntField { get; set; }
    public long LongField { get; set; }
}

public class LargeIntegerTests
{
    private static (
        TokenType TokenType,
        bool TryInt32Ok,
        int Int32Val,
        bool TryInt64Ok,
        long Int64Val
    ) ReadNumber(ReadOnlySpan<byte> json)
    {
        var reader = new JsonReader(json);
        reader.Read();
        var tt = reader.TokenType;
        var tryInt32Ok = reader.TryGetInt32(out var i32);
        var tryInt64Ok = reader.TryGetInt64(out var i64);
        return (tt, tryInt32Ok, i32, tryInt64Ok, i64);
    }

    [Test]
    public async Task Reader_Int32Max_ReturnsInt32()
    {
        var (tt, ok32, v32, ok64, v64) = ReadNumber("2147483647"u8);
        await Assert.That(tt).IsEqualTo(TokenType.Int32);
        await Assert.That(ok32).IsTrue();
        await Assert.That(v32).IsEqualTo(2147483647);
        await Assert.That(ok64).IsTrue();
        await Assert.That(v64).IsEqualTo(2147483647L);
    }

    [Test]
    public async Task Reader_Int32MaxPlus1_ReturnsInt64()
    {
        var (tt, ok32, v32, ok64, v64) = ReadNumber("2147483648"u8);
        await Assert.That(tt).IsEqualTo(TokenType.Int64);
        await Assert.That(ok32).IsFalse();
        await Assert.That(ok64).IsTrue();
        await Assert.That(v64).IsEqualTo(2147483648L);
    }

    [Test]
    public async Task Reader_LongMax_ReturnsInt64()
    {
        var (tt, ok32, v32, ok64, v64) = ReadNumber("9223372036854775807"u8);
        await Assert.That(tt).IsEqualTo(TokenType.Int64);
        await Assert.That(ok64).IsTrue();
        await Assert.That(v64).IsEqualTo(long.MaxValue);
    }

    [Test]
    public async Task Reader_NegativeInt32_ReturnsInt32()
    {
        var (tt, ok32, v32, ok64, v64) = ReadNumber("-2147483648"u8);
        await Assert.That(tt).IsEqualTo(TokenType.Int32);
        await Assert.That(ok32).IsTrue();
        await Assert.That(v32).IsEqualTo(-2147483648);
    }

    [Test]
    public async Task Reader_NegativeInt32Minus1_ReturnsInt64()
    {
        var (tt, ok32, v32, ok64, v64) = ReadNumber("-2147483649"u8);
        await Assert.That(tt).IsEqualTo(TokenType.Int64);
        await Assert.That(ok32).IsFalse();
        await Assert.That(ok64).IsTrue();
        await Assert.That(v64).IsEqualTo(-2147483649L);
    }

    [Test]
    public async Task SG_LongField_LargeValue_RoundTrips()
    {
        var model = new LargeIntegerModel { IntField = 100, LongField = 2147483648L };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var result = JsonSerializer.Deserialize<LargeIntegerModel>(bytes);

        await Assert.That(result!.LongField).IsEqualTo(2147483648L);
        await Assert.That(result.IntField).IsEqualTo(100);
    }
}
