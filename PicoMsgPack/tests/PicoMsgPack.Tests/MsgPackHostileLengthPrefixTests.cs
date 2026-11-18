namespace PicoMsgPack.Tests;

/// <summary>
/// A hostile length prefix must die as a <see cref="FormatException"/> carrying the reader's own
/// <c>Invalid length prefix</c> diagnostic, on the span path and on the sequence path alike, and
/// it must die <i>before</i> the value reaches Slice/Rent — where a length narrowed carelessly to
/// <see cref="int"/> would surface as ArgumentOutOfRangeException, not as a parse error.
/// <para>
/// The payloads are str32 headers whose 4-byte length does not fit a non-negative int: 0xFFFFFFFF
/// (4 294 967 295) and 0x80000000 (2 147 483 648). The reader accumulates length prefixes in
/// <c>long</c> for exactly this reason and then refuses anything above int.MaxValue, so both are
/// reported as the clamped 2147483647 and never as a negative count.
/// </para>
/// </summary>
public class MsgPackHostileLengthPrefixTests
{
    // No body at all: the length prefix alone is what must be rejected.
    private static readonly (string Name, byte[] Payload)[] HostileStr32Lengths =
    [
        ("str32-length-0xFFFFFFFF", [0xDB, 0xFF, 0xFF, 0xFF, 0xFF]),
        ("str32-length-0x80000000", [0xDB, 0x80, 0x00, 0x00, 0x00]),
    ];

    private const string ExpectedMessage = "Invalid length prefix 2147483647 at offset 5";

    [Test]
    public async Task HostileStr32Length_SpanPath_ThrowsFormatException()
    {
        foreach (var (name, payload) in HostileStr32Lengths)
        {
            var (type, message) = Failure(payload, sequence: false);
            await Assert.That(type).IsEqualTo("FormatException").Because(name);
            await Assert.That(message).IsEqualTo(ExpectedMessage).Because(name);
        }
    }

    [Test]
    public async Task HostileStr32Length_SequencePath_ThrowsFormatException()
    {
        foreach (var (name, payload) in HostileStr32Lengths)
        {
            var (type, message) = Failure(payload, sequence: true);
            await Assert.That(type).IsEqualTo("FormatException").Because(name);
            await Assert.That(message).IsEqualTo(ExpectedMessage).Because(name);
        }
    }

    private static (string Type, string Message) Failure(byte[] payload, bool sequence)
    {
        var reader = sequence
            ? new MsgPackReader(new ReadOnlySequence<byte>(payload))
            : new MsgPackReader(payload);
        try
        {
            while (reader.Read()) { }
        }
        catch (Exception ex)
        {
            return (
                ex.GetType().Name,
                string.IsNullOrEmpty(ex.Message) ? "<no message>" : ex.Message
            );
        }
        finally
        {
            reader.Dispose();
        }
        return ("<no throw>", "<no throw>");
    }
}
