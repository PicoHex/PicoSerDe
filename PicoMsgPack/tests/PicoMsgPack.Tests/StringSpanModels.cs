namespace PicoMsgPack.Tests;

// ── Models for the Task 4c guard-rail suite ────────────────────────────────
// The generated serializer used to hand every string to the writer as a freshly
// allocated UTF-8 byte[]: `mw.WriteString(Encoding.UTF8.GetBytes(value.S00))`. It now
// hands the characters over and the writer encodes them straight into its own span.
// These models pin both halves of that change:
//   * `StrFifteenModel` is the 15-string shape whose 600 B/op opened the task — the
//     allocation test asserts the writer path now allocates nothing at all;
//   * `StrEdgeModel` walks every UTF-8 header regime (fixstr / str8 / str16 / str32)
//     and every awkward character class (empty, NUL, 2/3/4-byte sequences, lone
//     surrogate) because the header length is derived from the *byte* count, not the
//     char count;
//   * `StrCollectionModel` covers the two other emit shapes that encoded strings:
//     sequence elements and dictionary keys/values.

public class StrFifteenModel
{
    public int N00 { get; set; }
    public int N01 { get; set; }
    public int N02 { get; set; }
    public int N03 { get; set; }
    public int N04 { get; set; }
    public int N05 { get; set; }
    public int N06 { get; set; }
    public int N07 { get; set; }
    public int N08 { get; set; }
    public int N09 { get; set; }
    public int N10 { get; set; }
    public int N11 { get; set; }
    public int N12 { get; set; }
    public int N13 { get; set; }
    public int N14 { get; set; }
    public string S00 { get; set; } = "";
    public string S01 { get; set; } = "";
    public string S02 { get; set; } = "";
    public string S03 { get; set; } = "";
    public string S04 { get; set; } = "";
    public string S05 { get; set; } = "";
    public string S06 { get; set; } = "";
    public string S07 { get; set; } = "";
    public string S08 { get; set; } = "";
    public string S09 { get; set; } = "";
    public string S10 { get; set; } = "";
    public string S11 { get; set; } = "";
    public string S12 { get; set; } = "";
    public string S13 { get; set; } = "";
    public string S14 { get; set; } = "";

    internal static StrFifteenModel Create() =>
        new()
        {
            N00 = 1000,
            N01 = 1001,
            N02 = 1002,
            N03 = 1003,
            N04 = 1004,
            N05 = 1005,
            N06 = 1006,
            N07 = 1007,
            N08 = 1008,
            N09 = 1009,
            N10 = 1010,
            N11 = 1011,
            N12 = 1012,
            N13 = 1013,
            N14 = 1014,
            S00 = "value-00-payload",
            S01 = "value-01-payload",
            S02 = "value-02-payload",
            S03 = "value-03-payload",
            S04 = "value-04-payload",
            S05 = "value-05-payload",
            S06 = "value-06-payload",
            S07 = "value-07-payload",
            S08 = "value-08-payload",
            S09 = "value-09-payload",
            S10 = "value-10-payload",
            S11 = "value-11-payload",
            S12 = "value-12-payload",
            S13 = "value-13-payload",
            S14 = "value-14-payload",
        };
}

public class StrEdgeModel
{
    // 0 / 1 / 2 / 3 / 4 bytes per char, a NUL run, every header boundary, and a
    // multi-byte string whose byte count crosses a header boundary its char count
    // does not reach (100 CJK chars = 300 bytes -> str16).
    public string Empty { get; set; } = "";
    public string OneByte { get; set; } = "a";
    public string Latin2 { get; set; } = "héllo";
    public string Cjk3 { get; set; } = "中文测试";
    public string Emoji4 { get; set; } = "😀🎉";
    public string NullChars { get; set; } = "a\0b\0\0";
    public string L31 { get; set; } = new string('x', 31);
    public string L32 { get; set; } = new string('y', 32);
    public string L255 { get; set; } = new string('z', 255);
    public string L256 { get; set; } = new string('w', 256);
    public string Cjk100 { get; set; } = new string('中', 100);
    public string Long600 { get; set; } = new string('q', 600);
    public string Mixed { get; set; } = "abc中文😀\0";
}

public class StrCollectionModel
{
    // Elements and dictionary keys/values are emitted by different generator paths
    // (WriteSerElemCore / the dict emitters), all of which encoded a byte[] before.
    public List<string> Items { get; set; } = null!;
    public Dictionary<string, string> Map { get; set; } = null!;
    public List<string?> Nullables { get; set; } = null!;

    internal static StrCollectionModel Create() =>
        new()
        {
            Items = ["", "a", "héllo", "中文", "😀", new string('m', 40)],
            Map = new()
            {
                ["k1"] = "",
                ["k2"] = "中文值",
                ["kéy"] = "value",
            },
            Nullables = ["a", null!, "中文"],
        };
}
