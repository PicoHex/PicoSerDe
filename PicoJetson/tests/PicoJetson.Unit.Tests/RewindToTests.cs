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
        var seq = new ReadOnlySequence<byte>("{\"A\":1,\"B\":2"u8.ToArray()); // B's value ends at the boundary
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
    public async Task RewindToMark_ClearsStickyIncomplete()
    {
        // A reader that hit a chunk boundary stays sticky until it is rewound;
        // RewindToMark must restore a readable state.
        var seq = new ReadOnlySequence<byte>("{\"A\":1,\"B\":2"u8.ToArray()); // B's value ends at the boundary
        var r = new JsonReader(seq, isFinalBlock: false);
        bool open = r.Read(); // '{'
        r.Mark();
        bool name = r.Read(); // property name A
        bool value = r.Read(); // value 1
        bool nextName = r.Read(); // property name B
        bool atEnd = r.Read(); // B's value hits the chunk boundary
        bool sticky = r.NeedsMoreData && !r.Read();
        r.RewindToMark();
        bool afterRewind = r.Read(); // readable again
        var type = r.TokenType;

        await Assert.That(open && name && value && nextName).IsTrue();
        await Assert.That(atEnd).IsFalse();
        await Assert.That(sticky).IsTrue();
        await Assert.That(afterRewind).IsTrue();
        await Assert.That(type).IsEqualTo(TokenType.PropertyName);
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
