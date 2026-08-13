namespace PicoJetson.Tests;

/// <summary>
/// H1 regression tests: IsValid / Parse must reject structurally invalid JSON
/// (mismatched brackets, missing property values, bare values in objects,
/// multiple root values, trailing content).
/// </summary>
public class PicoDocumentValidationTests
{
    [Test]
    public async Task IsValid_MismatchedBrackets_ReturnsFalse()
    {
        await Assert.That(PicoDocument.IsValid("[}"u8)).IsFalse();
    }

    [Test]
    public async Task IsValid_ArrayClosedByObjectBrace_ReturnsFalse()
    {
        await Assert.That(PicoDocument.IsValid("{\"a\":[1}}"u8)).IsFalse();
    }

    [Test]
    public async Task IsValid_MissingPropertyValue_ReturnsFalse()
    {
        await Assert.That(PicoDocument.IsValid("{\"a\":}"u8)).IsFalse();
    }

    [Test]
    public async Task IsValid_BareValueInObject_ReturnsFalse()
    {
        await Assert.That(PicoDocument.IsValid("{5}"u8)).IsFalse();
    }

    [Test]
    public async Task IsValid_MultipleRootValues_ReturnsFalse()
    {
        await Assert.That(PicoDocument.IsValid("5 5"u8)).IsFalse();
        await Assert.That(PicoDocument.IsValid("{} {}"u8)).IsFalse();
    }

    [Test]
    public async Task IsValid_ValueAfterPropertyWithoutColonStructure_ReturnsFalse()
    {
        await Assert.That(PicoDocument.IsValid("{\"a\" \"b\"}"u8)).IsFalse();
    }

    [Test]
    public async Task IsValid_ValidDocuments_ReturnTrue()
    {
        await Assert.That(PicoDocument.IsValid("{}"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("[]"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("5"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("null"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("\"str\""u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("{\"a\":1,\"b\":[true,null,\"x\"]}"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("[{\"k\":[]}]"u8)).IsTrue();
    }

    [Test]
    public async Task Parse_MismatchedBrackets_Throws()
    {
        var threw = false;
        try
        {
            PicoDocument.Parse("[}"u8.ToArray());
        }
        catch (FormatException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task Parse_MissingPropertyValue_Throws()
    {
        var threw = false;
        try
        {
            PicoDocument.Parse("{\"a\":}"u8.ToArray());
        }
        catch (FormatException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task Parse_BareValueInObject_Throws()
    {
        var threw = false;
        try
        {
            PicoDocument.Parse("{5}"u8.ToArray());
        }
        catch (FormatException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task Parse_MultipleRootValues_Throws()
    {
        var threw = false;
        try
        {
            PicoDocument.Parse("5 5"u8.ToArray());
        }
        catch (FormatException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task Parse_TrailingValidToken_Throws()
    {
        var threw = false;
        try
        {
            PicoDocument.Parse("{} {}"u8.ToArray());
        }
        catch (FormatException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task Parse_ValidDocument_StillWorks()
    {
        var doc = PicoDocument.Parse("{\"name\":\"Alice\",\"age\":30}"u8.ToArray());
        await Assert.That(doc.RootElement["name"].GetString()).IsEqualTo("Alice");
        await Assert.That(doc.RootElement["age"].GetInt32()).IsEqualTo(30);
    }
}
