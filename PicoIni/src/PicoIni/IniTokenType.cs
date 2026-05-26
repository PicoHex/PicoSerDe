namespace PicoIni;

public enum IniTokenType
{
    None = 0,
    SectionStart,
    Key,
    Value,
    Comment,
    Blank,
}
