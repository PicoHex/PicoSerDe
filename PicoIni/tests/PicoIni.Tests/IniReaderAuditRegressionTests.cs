namespace PicoIni.Tests;

/// <summary>Audit BUG-02 regression tests: malformed lines must throw
/// FormatException, never ArgumentOutOfRangeException.</summary>
public class IniReaderAuditRegressionTests
{
    [Test]
    [Arguments("x")]
    [Arguments("]")]
    [Arguments("{")]
    [Arguments("}")]
    [Arguments("\"unterminated")]
    public async Task MalformedLine_ThrowsFormatException_NotArgumentOutOfRange(string line)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(line);
        var threw = false;
        try
        {
            var reader = new IniReader(data);
            while (reader.Read()) { }
        }
        catch (FormatException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task ValidLine_StillParses()
    {
        var reader = new IniReader("k=v\n"u8);
        await Assert.That(reader.Read()).IsTrue();
    }

    [Test]
    public async Task UnterminatedSection_AtEndOfInput_ThrowsFormatException()
    {
        // The value side of the reader already has this pin (an unterminated quoted value);
        // the section side did not. "[sec" with no ']' anywhere in the document is a
        // truncated document, not an empty section: the reader must say so instead of
        // taking the rest of the file — "sec\nK = 1\n" — as a section name.
        string? message = null;
        try
        {
            var reader = new IniReader("[sec\nK = 1\n"u8);
            while (reader.Read()) { }
        }
        catch (FormatException ex)
        {
            message = ex.Message;
        }

        await Assert.That(message).IsEqualTo("Unterminated section at end of input");
    }
}
