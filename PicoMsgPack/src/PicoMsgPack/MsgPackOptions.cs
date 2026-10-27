namespace PicoMsgPack;

public enum MsgPackIgnoreCondition
{
    Never = 0,
    WhenWritingNull = 1,
}

/// <summary>
/// How a <see cref="System.DateTime"/> is put on the wire.
/// </summary>
public enum MsgPackDateTimeFormat
{
    /// <summary>
    /// The MessagePack timestamp extension (tag <c>0xFF</c>, spec §"Timestamp extension
    /// type"): <em>timestamp 32</em> / <em>64</em> / <em>96</em> depending on the value.
    /// This is what MessagePack-CSharp emits and 4–12 bytes instead of a 28-character
    /// string. <b>Default.</b>
    /// </summary>
    TimestampExt = 0,

    /// <summary>
    /// ISO 8601 round-trip text (<c>DateTime.ToString("O")</c>) as a MessagePack string.
    /// Kept for payloads that must stay byte-compatible with earlier PicoMsgPack builds
    /// and for custom headers that want a human-readable timestamp. The reader accepts
    /// this shape regardless of the option, so switching to
    /// <see cref="TimestampExt"/> never makes old payloads unreadable.
    /// </summary>
    IsoString = 1,
}

public class MsgPackOptions : SerOptions
{
    public MsgPackIgnoreCondition DefaultIgnoreCondition { get; set; } =
        MsgPackIgnoreCondition.Never;

    /// <summary>
    /// Wire representation for <see cref="System.DateTime"/> members. Defaults to
    /// <see cref="MsgPackDateTimeFormat.TimestampExt"/> (interoperable with
    /// MessagePack-CSharp); set to <see cref="MsgPackDateTimeFormat.IsoString"/> to
    /// reproduce the pre-timestamp text form.
    /// </summary>
    /// <remarks>
    /// Only the write side is affected — the generated reader accepts the timestamp
    /// extension <em>and</em> the ISO string, so payloads written under either setting
    /// stay readable.
    /// </remarks>
    public MsgPackDateTimeFormat DateTimeFormat { get; set; } = MsgPackDateTimeFormat.TimestampExt;
}
