namespace PicoSerDe.Abs.Tests;

public class TokenTypeTests
{
    [Test]
    public async Task None_HasValueZero()
    {
        await Assert.That((int)TokenType.None).IsEqualTo(0);
    }

    [Test]
    public async Task EnumContainsAllStructuralTokens()
    {
        var values = Enum.GetValues<TokenType>();
        await Assert.That(values).Contains(TokenType.ObjectStart);
        await Assert.That(values).Contains(TokenType.ObjectEnd);
        await Assert.That(values).Contains(TokenType.ArrayStart);
        await Assert.That(values).Contains(TokenType.ArrayEnd);
        await Assert.That(values).Contains(TokenType.PropertyName);
    }

    [Test]
    public async Task EnumContainsAllScalarTokens()
    {
        var values = Enum.GetValues<TokenType>();
        await Assert.That(values).Contains(TokenType.Null);
        await Assert.That(values).Contains(TokenType.Bool);
        await Assert.That(values).Contains(TokenType.String);
        await Assert.That(values).Contains(TokenType.Bytes);
        await Assert.That(values).Contains(TokenType.Int8);
        await Assert.That(values).Contains(TokenType.Int16);
        await Assert.That(values).Contains(TokenType.Int32);
        await Assert.That(values).Contains(TokenType.Int64);
        await Assert.That(values).Contains(TokenType.UInt8);
        await Assert.That(values).Contains(TokenType.UInt16);
        await Assert.That(values).Contains(TokenType.UInt32);
        await Assert.That(values).Contains(TokenType.UInt64);
        await Assert.That(values).Contains(TokenType.Float16);
        await Assert.That(values).Contains(TokenType.Float32);
        await Assert.That(values).Contains(TokenType.Float64);
    }

    [Test]
    public async Task EnumContainsExtensionToken()
    {
        var values = Enum.GetValues<TokenType>();
        await Assert.That(values).Contains(TokenType.Extension);
    }

    [Test]
    public async Task TotalMemberCount_IsTwentyTwo()
    {
        var count = Enum.GetValues<TokenType>().Length;
        await Assert.That(count).IsEqualTo(22);
    }

    [Test]
    public async Task StructuralTokens_HaveDistinctValues()
    {
        await Assert.That(TokenType.ObjectStart).IsNotEqualTo(TokenType.ObjectEnd);
        await Assert.That(TokenType.ArrayStart).IsNotEqualTo(TokenType.ArrayEnd);
        await Assert.That(TokenType.ObjectStart).IsNotEqualTo(TokenType.ArrayStart);
    }
}
