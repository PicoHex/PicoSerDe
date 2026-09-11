using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Numerics;

namespace PicoJetson.Tests;

// ── Models: full type-coverage matrix ──

public class ScalarCoverageDto
{
    public DateTimeOffset When { get; set; }
    public char Ch { get; set; }
    public Uri? Link { get; set; }
    public Version? Ver { get; set; }
    public Half H { get; set; }
    public BigInteger Big { get; set; }
    public Int128 I128 { get; set; }
    public UInt128 U128 { get; set; }
    public nint NPtr { get; set; }
    public int Plain { get; set; }
}

public class CollectionCoverageDto
{
    public HashSet<int> Set { get; set; } = new();
    public Queue<int> Q { get; set; } = new();
    public Stack<int> S { get; set; } = new();
    public LinkedList<int> L { get; set; } = new();
    public SortedDictionary<string, int> Sorted { get; set; } = new();
    public ConcurrentDictionary<string, int> Concurrent { get; set; } = new();
    public ImmutableArray<int> Imm { get; set; }
    public Memory<int> Mem { get; set; }
    public ReadOnlyMemory<int> RMem { get; set; }
}

public class PairCoverageDto
{
    public KeyValuePair<string, int> Kvp { get; set; }
    public (int, string) VT { get; set; }
    public Tuple<int, string>? Tup { get; set; }
}

/// <summary>
/// Type-coverage matrix: scalar types STJ supports (DateTimeOffset, char, Uri,
/// Version, Half, BigInteger, Int128/UInt128, nint), generalized collections
/// (HashSet/Queue/Stack/LinkedList, Sorted/Concurrent dictionary, ImmutableArray,
/// Memory), and pairs/tuples (KeyValuePair, ValueTuple, Tuple).
/// </summary>
public class TypeCoverageTests
{
    private static bool ThrowsFormat(Action a)
    {
        try
        {
            a();
            return false;
        }
        catch (Exception ex)
            when (ex is FormatException or InvalidOperationException or OverflowException)
        {
            return true;
        }
    }

    [Test]
    public async Task Scalars_RoundTrip()
    {
        var when = new DateTimeOffset(2026, 9, 9, 10, 30, 0, TimeSpan.FromHours(8));
        var dto = new ScalarCoverageDto
        {
            When = when,
            Ch = 'x',
            Link = new Uri("https://example.com/a?b=1"),
            Ver = new Version(1, 2, 3),
            H = (Half)1.5,
            Big = BigInteger.Parse("123456789012345678901234567890"),
            I128 = Int128.MaxValue,
            U128 = UInt128.MaxValue,
            NPtr = 42,
            Plain = 7,
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(dto);
        var back = JsonSerializer.Deserialize<ScalarCoverageDto>(bytes);

        await Assert.That(back!.When).IsEqualTo(when);
        await Assert.That(back.When.Offset).IsEqualTo(TimeSpan.FromHours(8));
        await Assert.That(back.Ch).IsEqualTo('x');
        await Assert.That(back.Link!.AbsoluteUri).IsEqualTo("https://example.com/a?b=1");
        await Assert.That(back.Ver!.ToString()).IsEqualTo("1.2.3");
        await Assert.That(back.H).IsEqualTo((Half)1.5);
        await Assert.That(back.Big).IsEqualTo(dto.Big);
        await Assert.That(back.I128).IsEqualTo(Int128.MaxValue);
        await Assert.That(back.U128).IsEqualTo(UInt128.MaxValue);
        await Assert.That(back.NPtr).IsEqualTo((nint)42);
        await Assert.That(back.Plain).IsEqualTo(7);
    }

    [Test]
    public async Task Scalars_UtcOffset_RoundTrips()
    {
        var when = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var back = JsonSerializer.Deserialize<ScalarCoverageDto>(
            JsonSerializer.SerializeToUtf8Bytes(new ScalarCoverageDto { When = when })
        );
        await Assert.That(back!.When).IsEqualTo(when);
        await Assert.That(back.When.Offset).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task Scalars_InvalidValues_Throw()
    {
        await Assert
            .That(
                ThrowsFormat(() =>
                    JsonSerializer.Deserialize<ScalarCoverageDto>("{\"Ch\":\"ab\"}"u8)
                )
            )
            .IsTrue();
        await Assert
            .That(
                ThrowsFormat(() =>
                    JsonSerializer.Deserialize<ScalarCoverageDto>("{\"Link\":\"http://[::1\"}"u8)
                )
            )
            .IsTrue();
        await Assert
            .That(
                ThrowsFormat(() =>
                    JsonSerializer.Deserialize<ScalarCoverageDto>("{\"Ver\":\"not.a.version\"}"u8)
                )
            )
            .IsTrue();
        await Assert
            .That(
                ThrowsFormat(() =>
                    JsonSerializer.Deserialize<ScalarCoverageDto>("{\"I128\":\"abc\"}"u8)
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task Collections_RoundTrip()
    {
        var dto = new CollectionCoverageDto
        {
            Set = new HashSet<int> { 1, 2, 3 },
            Q = new Queue<int>(new[] { 3, 4 }),
            S = new Stack<int>(new[] { 5, 6 }),
            L = new LinkedList<int>(new[] { 7, 8 }),
            Sorted = new SortedDictionary<string, int> { ["b"] = 2, ["a"] = 1 },
            Concurrent = new ConcurrentDictionary<string, int> { ["c"] = 3 },
            Imm = ImmutableArray.Create(9, 10),
            Mem = new Memory<int>(new[] { 11, 12 }),
            RMem = new ReadOnlyMemory<int>(new[] { 13, 14 }),
        };

        var back = JsonSerializer.Deserialize<CollectionCoverageDto>(
            JsonSerializer.SerializeToUtf8Bytes(dto)
        );

        await Assert.That(back!.Set.SetEquals(new[] { 1, 2, 3 })).IsTrue();
        await Assert.That(back.Q.Count).IsEqualTo(2);
        await Assert.That(back.Q.Peek()).IsEqualTo(3);
        await Assert.That(back.S.Count).IsEqualTo(2);
        // Faithful round-trip: enumeration order (top-first) is preserved.
        await Assert.That(back.S.Peek()).IsEqualTo(dto.S.Peek());
        await Assert.That(back.S.SequenceEqual(dto.S)).IsTrue();
        await Assert.That(back.L.Count).IsEqualTo(2);
        await Assert.That(back.L.First!.Value).IsEqualTo(7);
        await Assert.That(back.Sorted.Count).IsEqualTo(2);
        await Assert.That(back.Sorted["a"]).IsEqualTo(1);
        await Assert.That(back.Concurrent["c"]).IsEqualTo(3);
        await Assert.That(back.Imm).Count().IsEqualTo(2);
        await Assert.That(back.Imm[0]).IsEqualTo(9);
        await Assert.That(back.Mem.Length).IsEqualTo(2);
        await Assert.That(back.Mem.Span[0]).IsEqualTo(11);
        await Assert.That(back.RMem.Length).IsEqualTo(2);
        await Assert.That(back.RMem.Span[1]).IsEqualTo(14);
    }

    [Test]
    public async Task Pairs_ValueTuple_RoundTrips()
    {
        var dto = new PairCoverageDto { VT = (2, "v") };
        var back = JsonSerializer.Deserialize<PairCoverageDto>(
            JsonSerializer.SerializeToUtf8Bytes(dto)
        );
        await Assert.That(back!.VT.Item1).IsEqualTo(2);
        await Assert.That(back.VT.Item2).IsEqualTo("v");
    }

    [Test]
    public async Task Pairs_KeyValuePairAndTuple_Pending_NoCascade()
    {
        // PENDING (tracked follow-up): KeyValuePair<,> and Tuple<> members are not
        // implemented yet and are dropped without warning. They must not break the
        // rest of the DTO (no cascade), and this test flips when they land.
        var dto = new PairCoverageDto
        {
            Kvp = new KeyValuePair<string, int>("k", 1),
            VT = (2, "v"),
            Tup = Tuple.Create(3, "t"),
        };
        var json = JsonSerializer.Serialize(dto);
        await Assert.That(json).Contains("\"VT\"");
        await Assert.That(json.Contains("\"Kvp\"")).IsFalse();
        await Assert.That(json.Contains("\"Tup\"")).IsFalse();
    }

    [Test]
    public async Task TopLevel_ListOfChar_DoesNotCrashGenerator_AndRoundTrips()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new List<char> { 'a', 'b' });
        var back = JsonSerializer.Deserialize<List<char>>(bytes);
        await Assert.That(back).Count().IsEqualTo(2);
        await Assert.That(back![0]).IsEqualTo('a');
        await Assert.That(back[1]).IsEqualTo('b');
    }

    [Test]
    public async Task TopLevel_ListOfDateTimeOffset_RoundTrips()
    {
        var when = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(-5));
        var back = JsonSerializer.Deserialize<List<DateTimeOffset>>(
            JsonSerializer.SerializeToUtf8Bytes(new List<DateTimeOffset> { when })
        );
        await Assert.That(back).Count().IsEqualTo(1);
        await Assert.That(back![0]).IsEqualTo(when);
    }
}

public class DegradationDto
{
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;
    public int Plain { get; set; }
}

public class NullableScalarsDto
{
    public DateTimeOffset? When { get; set; }
    public char? Ch { get; set; }
    public int Plain { get; set; }
}

public class TypeCoverageExtraTests
{
    private static bool ThrowsFormat(Action a)
    {
        try
        {
            a();
            return false;
        }
        catch (Exception ex)
            when (ex is FormatException or InvalidOperationException or OverflowException)
        {
            return true;
        }
    }

    [Test]
    public async Task NullableScalars_RoundTrip()
    {
        var dto = new NullableScalarsDto
        {
            When = new DateTimeOffset(2026, 6, 7, 8, 9, 10, TimeSpan.FromHours(2)),
            Ch = 'z',
            Plain = 1,
        };
        var back = JsonSerializer.Deserialize<NullableScalarsDto>(
            JsonSerializer.SerializeToUtf8Bytes(dto)
        );
        await Assert.That(back!.When).IsEqualTo(dto.When);
        await Assert.That(back.Ch).IsEqualTo((char?)'z');
    }

    [Test]
    public async Task UnsupportedProperty_DegradesGracefully_NoCascade()
    {
        // TimeZoneInfo is intentionally unsupported → dropped, but the DTO and its
        // other members must still serialize (generator must not crash the compilation).
        var dto = new DegradationDto { Zone = TimeZoneInfo.Utc, Plain = 5 };
        var json = JsonSerializer.Serialize(dto);
        await Assert.That(json).Contains("\"Plain\":5");
        var back = JsonSerializer.Deserialize<DegradationDto>(
            JsonSerializer.SerializeToUtf8Bytes(dto)
        );
        await Assert.That(back!.Plain).IsEqualTo(5);
    }
}
