namespace PicoJetson.Tests;

/// <summary>
/// Missing-separator guard: RFC 8259 requires a ',' between object members and
/// between array elements. <see cref="PicoDocument.IsValid"/> is the ruler this
/// repository (and its downstream) uses to answer "is this output correct?", so
/// an unterminated element must be rejected, not silently accepted.
///
/// Before this file, the structure validator only required "a property name may
/// follow a value" and "an array keeps expecting value-or-end", so
/// <c>{"a":1"b":2}</c> and <c>[1 2]</c> were reported as valid JSON.
/// </summary>
public class PicoDocumentSeparatorTests
{
    [Test]
    public async Task IsValid_MissingCommaBetweenObjectProperties_ReturnsFalse()
    {
        await Assert.That(PicoDocument.IsValid("{\"a\":1\"b\":2}"u8)).IsFalse();
        await Assert.That(PicoDocument.IsValid("{\"a\":1 \"b\":2}"u8)).IsFalse();
        await Assert.That(PicoDocument.IsValid("{\"a\":\"x\"\"b\":2}"u8)).IsFalse();
        await Assert.That(PicoDocument.IsValid("{\"a\":true\"b\":2}"u8)).IsFalse();
    }

    [Test]
    public async Task IsValid_MissingCommaBetweenArrayElements_ReturnsFalse()
    {
        await Assert.That(PicoDocument.IsValid("[1 2]"u8)).IsFalse();
        await Assert.That(PicoDocument.IsValid("[1,2 3]"u8)).IsFalse();
        await Assert.That(PicoDocument.IsValid("[1 2,3]"u8)).IsFalse();
        await Assert.That(PicoDocument.IsValid("[true false]"u8)).IsFalse();
        await Assert.That(PicoDocument.IsValid("[\"a\" \"b\"]"u8)).IsFalse();
        await Assert.That(PicoDocument.IsValid("[null null]"u8)).IsFalse();
    }

    [Test]
    public async Task IsValid_MissingCommaBetweenContainerElements_ReturnsFalse()
    {
        // Objects / arrays used as elements need the separator just as scalars do.
        await Assert.That(PicoDocument.IsValid("[{\"a\":1}{\"b\":2}]"u8)).IsFalse();
        await Assert.That(PicoDocument.IsValid("[[1][2]]"u8)).IsFalse();
        await Assert.That(PicoDocument.IsValid("[[1] [2]]"u8)).IsFalse();
        await Assert.That(PicoDocument.IsValid("{\"a\":{}{\"b\":2}}"u8)).IsFalse();
        await Assert.That(PicoDocument.IsValid("{\"a\":[][]}"u8)).IsFalse();
    }

    [Test]
    public async Task IsValid_ScalarAfterContainerWithoutComma_ReturnsFalse()
    {
        // A closed container used as a member/element is a consumed value too: the
        // parent owes a separator before the next token, scalar or not.
        await Assert.That(PicoDocument.IsValid("[{\"a\":1}2]"u8)).IsFalse();
        await Assert.That(PicoDocument.IsValid("{\"a\":{}5}"u8)).IsFalse();
        await Assert.That(PicoDocument.IsValid("[[1]2]"u8)).IsFalse();
        await Assert.That(PicoDocument.IsValid("{\"a\":{\"b\":1}\"c\":2}"u8)).IsFalse();
        // Separated forms stay valid; a comma still does not let a bare scalar be
        // an object member (that guard is unchanged).
        await Assert.That(PicoDocument.IsValid("[{\"a\":1},2]"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("[{\"a\":1},{\"b\":2}]"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("{\"a\":{},5}"u8)).IsFalse();
    }

    [Test]
    public async Task IsValid_MissingCommaInNestedContainers_ReturnsFalse()
    {
        await Assert.That(PicoDocument.IsValid("{\"a\":[1 2]}"u8)).IsFalse();
        await Assert.That(PicoDocument.IsValid("[{\"a\":1\"b\":2}]"u8)).IsFalse();
        await Assert.That(PicoDocument.IsValid("{\"o\":{\"a\":1\"b\":2}}"u8)).IsFalse();
        await Assert.That(PicoDocument.IsValid("{\"a\":[{\"k\":1},{\"k\":2 3}]}"u8)).IsFalse();
    }

    [Test]
    public async Task IsValid_SeparatedDocuments_ReturnTrue()
    {
        await Assert.That(PicoDocument.IsValid("{\"a\":1,\"b\":2}"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("[1,2]"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("[{\"a\":1},{\"b\":2}]"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("{}"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("[]"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("[{},{}]"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("{\"a\":[],\"b\":{}}"u8)).IsTrue();
        await Assert
            .That(PicoDocument.IsValid("{\"a\":[1,{\"k\":[2,3]},\"x\"],\"b\":{}}"u8))
            .IsTrue();
    }

    [Test]
    public async Task IsValid_SingleElementContainers_ReturnTrue()
    {
        // A container that never holds a second member needs no separator.
        await Assert.That(PicoDocument.IsValid("[1]"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("[\"a\"]"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("[[1]]"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("{\"a\":1}"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("{ \"a\" : 1 }"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("{\"a\":{}}"u8)).IsTrue();
    }

    [Test]
    public async Task IsValid_CommaMisuse_ReturnsFalse()
    {
        // The reader owns these (leading / doubled / trailing commas); pinned here
        // because the separator state machine reads the same byte gap.
        await Assert.That(PicoDocument.IsValid("[,1]"u8)).IsFalse();
        await Assert.That(PicoDocument.IsValid("[1,,2]"u8)).IsFalse();
        await Assert.That(PicoDocument.IsValid("[1,]"u8)).IsFalse();
        await Assert.That(PicoDocument.IsValid("{\"a\":1,}"u8)).IsFalse();
        await Assert.That(PicoDocument.IsValid("{\"a\":1,,\"b\":2}"u8)).IsFalse();
        await Assert.That(PicoDocument.IsValid("[1, 2, ]"u8)).IsFalse();
    }

    [Test]
    public async Task IsValid_BomPrefixedDocuments_StillDetectMissingComma()
    {
        // The reader strips a leading BOM from its raw buffer; the separators are
        // located through that same buffer, so a BOM must not shift the scan.
        await Assert.That(PicoDocument.IsValid("\uFEFF{\"a\":1,\"b\":2}"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("\uFEFF[1,2]"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("\uFEFF{\"a\":1\"b\":2}"u8)).IsFalse();
        await Assert.That(PicoDocument.IsValid("\uFEFF[1 2]"u8)).IsFalse();
    }

    [Test]
    public async Task IsValid_DeeplyNestedSeparators_BehaveConsistently()
    {
        await Assert.That(PicoDocument.IsValid("{\"a\":[{\"b\":[{\"c\":[1,2]},3]},4]}"u8)).IsTrue();
        await Assert
            .That(PicoDocument.IsValid("{\"a\":[{\"b\":[{\"c\":[1 2]},3]},4]}"u8))
            .IsFalse();
    }

    [Test]
    public async Task IsValid_SeparatorsWithWhitespace_ReturnTrue()
    {
        // Any RFC 8259 whitespace may surround the comma.
        await Assert.That(PicoDocument.IsValid("{ \"a\" : 1 , \"b\" : 2 }"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("[\r\n1\t,\n2\r\n]"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("{\n  \"a\": 1,\n  \"b\": [ 1, 2 ]\n}"u8)).IsTrue();
    }

    [Test]
    public async Task IsValid_EscapesAndUnicodeInStrings_ReturnTrue()
    {
        await Assert.That(PicoDocument.IsValid("[\"a,b\",\"c\\\"d\"]"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("[\"\\u00e9\",\"\\ud83d\\ude00\"]"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("[\"中文\",\"日本語\"]"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("{\"a,b\":\"[1 2]\"}"u8)).IsTrue();
    }

    [Test]
    public async Task IsValid_ScalarRoots_ReturnTrue()
    {
        await Assert.That(PicoDocument.IsValid("0"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("-1.5e10"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("true"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("false"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("null"u8)).IsTrue();
    }

    [Test]
    public async Task Parse_MissingComma_Throws()
    {
        foreach (
            var json in new[]
            {
                "{\"a\":1\"b\":2}"u8.ToArray(),
                "[1 2]"u8.ToArray(),
                "{\"a\":[1 2]}"u8.ToArray(),
                "[{\"a\":1\"b\":2}]"u8.ToArray(),
            }
        )
        {
            var threw = false;
            try
            {
                PicoDocument.Parse(json);
            }
            catch (FormatException)
            {
                threw = true;
            }
            await Assert.That(threw).IsTrue();
        }
    }

    [Test]
    public async Task Parse_BomPrefixedDocument_StillRejectsMissingComma()
    {
        // The separator scan reads the reader's raw buffer, which a leading BOM
        // shifts; the verdict must not move with it. (Value slicing for
        // BOM-prefixed input is broken independently of this gate — pre-existing
        // on 64780b7 — so only the verdict is asserted here.)
        await Assert.That(PicoDocument.IsValid("\uFEFF{\"a\":1,\"b\":2}"u8)).IsTrue();

        var threw = false;
        try
        {
            PicoDocument.Parse("\uFEFF{\"a\":1\"b\":2}"u8.ToArray());
        }
        catch (FormatException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
    }
}
