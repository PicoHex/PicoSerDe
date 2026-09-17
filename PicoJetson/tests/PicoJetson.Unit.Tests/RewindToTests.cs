using System.Buffers;
using System.Text;

namespace PicoJetson.Tests;

/// <summary>
/// Streaming resume primitive: RewindTo lets generated streaming delegates
/// return to the start of a re-readable unit (property name / array element)
/// after an incomplete read.
/// </summary>
public class RewindToTests
{
    [Test]
    public async Task Span_RewindTo_ReReadsEarlierToken()
    {
        var r = new JsonReader("{\"A\":1}"u8);
        bool open = r.Read(); // '{'
        long snap = r.BytesConsumed;
        bool name1 = r.Read(); // property name
        r.RewindTo(snap);
        bool name2 = r.Read(); // property name again
        var type = r.TokenType;
        var name = Encoding.UTF8.GetString(r.GetStringRaw());

        await Assert.That(open && name1 && name2).IsTrue();
        await Assert.That(type).IsEqualTo(TokenType.PropertyName);
        await Assert.That(name).IsEqualTo("A");
    }

    [Test]
    public async Task Sequence_RewindTo_ReReadsEarlierToken()
    {
        var seq = new ReadOnlySequence<byte>("{\"A\":1}"u8.ToArray());
        var r = new JsonReader(seq);
        bool open = r.Read(); // '{'
        long snap = r.BytesConsumed;
        bool name1 = r.Read(); // property name
        r.RewindTo(snap);
        bool name2 = r.Read(); // property name again
        var type = r.TokenType;
        var name = Encoding.UTF8.GetString(r.GetStringRaw());

        await Assert.That(open && name1 && name2).IsTrue();
        await Assert.That(type).IsEqualTo(TokenType.PropertyName);
        await Assert.That(name).IsEqualTo("A");
    }

    [Test]
    public async Task RewindTo_InvalidOffset_Throws()
    {
        var r = new JsonReader("{}"u8);
        Exception? negative = null;
        try
        {
            r.RewindTo(-1);
        }
        catch (Exception ex)
        {
            negative = ex;
        }
        Exception? tooFar = null;
        try
        {
            r.RewindTo(99);
        }
        catch (Exception ex)
        {
            tooFar = ex;
        }

        await Assert.That(negative).IsTypeOf<ArgumentOutOfRangeException>();
        await Assert.That(tooFar).IsTypeOf<ArgumentOutOfRangeException>();
    }
}
