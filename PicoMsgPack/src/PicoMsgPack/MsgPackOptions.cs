namespace PicoMsgPack;

public enum MsgPackIgnoreCondition
{
    Never = 0,
    WhenWritingNull = 1,
}

public class MsgPackOptions : SerOptions
{
    public MsgPackIgnoreCondition DefaultIgnoreCondition { get; set; } =
        MsgPackIgnoreCondition.Never;
}
