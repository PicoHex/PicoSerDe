namespace PicoSerDe.Gen;

internal readonly record struct AnonFormatConfig(
    bool HasNullLiteral,
    bool EmbedsKeyInValue,
    string? ObjectStartMethod,
    string? ObjectEndMethod,
    bool ObjectStartNeedsCount,
    bool HasIndentedMaxDepth,
    bool KeyIsEncodedString,
    bool HasOptionsParam
);
