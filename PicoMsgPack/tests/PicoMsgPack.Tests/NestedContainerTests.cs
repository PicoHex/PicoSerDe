namespace PicoMsgPack.Tests;

public class NestedContainerTests
{
    [Test]
    public async Task NestedEmptyArray_EmitsOuterArrayEnd()
    {
        // [[ ]] — fixarray(1, fixarray(0))
        var data = new byte[] { 0x91, 0x90 };
        var tokens = new List<TokenType>();

        using (var reader = new MsgPackReader(data))
        {
            while (reader.Read())
                tokens.Add(reader.TokenType);
        }

        // Expected: ArrayStart, ArrayStart, ArrayEnd, ArrayEnd
        // Bug: outer ArrayEnd is missing (reader returns false instead)
        await Assert.That(tokens.Count).IsEqualTo(4);
        await Assert.That(tokens[0]).IsEqualTo(TokenType.ArrayStart);
        await Assert.That(tokens[1]).IsEqualTo(TokenType.ArrayStart);
        await Assert.That(tokens[2]).IsEqualTo(TokenType.ArrayEnd);
        await Assert.That(tokens[3]).IsEqualTo(TokenType.ArrayEnd);
    }

    [Test]
    public async Task MapWithNestedArray_EmitsObjectEnd()
    {
        // {"a": [1, 2]} — fixmap(1) + fixstr("a") + fixarray(2) + 1 + 2
        // 0x81 = fixmap(1 pair), 0xA1 0x61 = str("a"), 0x92 = fixarray(2), 0x01, 0x02
        var data = new byte[] { 0x81, 0xA1, 0x61, 0x92, 0x01, 0x02 };
        var tokens = new List<TokenType>();

        using (var reader = new MsgPackReader(data))
        {
            while (reader.Read())
                tokens.Add(reader.TokenType);
        }

        // Expected: ObjectStart, PropertyName, ArrayStart, Int32, Int32, ArrayEnd, ObjectEnd
        // Bug: ObjectEnd is missing (reader returns false after ArrayEnd due to un-decremented parent count)
        await Assert.That(tokens.Count).IsEqualTo(7);
        await Assert.That(tokens[0]).IsEqualTo(TokenType.ObjectStart);
        await Assert.That(tokens[1]).IsEqualTo(TokenType.PropertyName);
        await Assert.That(tokens[2]).IsEqualTo(TokenType.ArrayStart);
        await Assert.That(tokens[3]).IsEqualTo(TokenType.Int32);
        await Assert.That(tokens[4]).IsEqualTo(TokenType.Int32);
        await Assert.That(tokens[5]).IsEqualTo(TokenType.ArrayEnd);
        await Assert.That(tokens[6]).IsEqualTo(TokenType.ObjectEnd);
    }

    [Test]
    public async Task DeeplyNestedArrays_CorrectTokenSequence()
    {
        // [[1, 2], [3]] — fixarray(2, fixarray(2, 1, 2), fixarray(1, 3))
        // 0x92, 0x92, 0x01, 0x02, 0x91, 0x03
        var data = new byte[] { 0x92, 0x92, 0x01, 0x02, 0x91, 0x03 };
        var tokens = new List<TokenType>();

        using (var reader = new MsgPackReader(data))
        {
            while (reader.Read())
                tokens.Add(reader.TokenType);
        }

        // Expected: ArrayStart, ArrayStart, Int32, Int32, ArrayEnd, ArrayStart, Int32, ArrayEnd, ArrayEnd
        await Assert.That(tokens.Count).IsEqualTo(9);
        await Assert.That(tokens[0]).IsEqualTo(TokenType.ArrayStart); // outer
        await Assert.That(tokens[1]).IsEqualTo(TokenType.ArrayStart); // inner 1
        await Assert.That(tokens[2]).IsEqualTo(TokenType.Int32); // 1
        await Assert.That(tokens[3]).IsEqualTo(TokenType.Int32); // 2
        await Assert.That(tokens[4]).IsEqualTo(TokenType.ArrayEnd); // end inner 1
        await Assert.That(tokens[5]).IsEqualTo(TokenType.ArrayStart); // inner 2
        await Assert.That(tokens[6]).IsEqualTo(TokenType.Int32); // 3
        await Assert.That(tokens[7]).IsEqualTo(TokenType.ArrayEnd); // end inner 2
        await Assert.That(tokens[8]).IsEqualTo(TokenType.ArrayEnd); // end outer
    }
}
