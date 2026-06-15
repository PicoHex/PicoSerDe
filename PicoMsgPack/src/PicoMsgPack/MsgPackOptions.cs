namespace PicoMsgPack;

public enum MsgPackIgnoreCondition
{
    Never = 0,
    WhenWritingNull = 1,
}

public class MsgPackOptions
{
    public MsgPackIgnoreCondition DefaultIgnoreCondition { get; set; } = MsgPackIgnoreCondition.Never;

    [ThreadStatic]
    internal static MsgPackOptions? Current;
}
