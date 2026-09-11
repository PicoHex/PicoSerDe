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

    [Test]
    public async Task Audit_ExtendedScalarDictKey_DropsWithoutCompileBreak()
    {
        // Dictionary<DateTimeOffset, int> keys are not supported yet — the property
        // must be dropped (no non-compiling key parsing) and other members survive.
        var dto = new ExtKeyDictDto
        {
            Map = new Dictionary<DateTimeOffset, int> { [DateTimeOffset.UtcNow] = 1 },
            Plain = 6,
        };
        var json = JsonSerializer.Serialize(dto);
        await Assert.That(json).Contains("\"Plain\":6");
        await Assert.That(json.Contains("\"Map\"")).IsFalse();
    }

    [Test]
    public async Task Audit_NestedExtendedCollection_DropsWithoutCompileBreak()
    {
        // List<HashSet<int>> elements are not supported yet — dropped, no cascade.
        var dto = new NestedCollectionDto
        {
            L = new List<HashSet<int>> { new HashSet<int> { 1 } },
            Plain = 7,
        };
        var json = JsonSerializer.Serialize(dto);
        await Assert.That(json).Contains("\"Plain\":7");
        await Assert.That(json.Contains("\"L\"")).IsFalse();
    }

    [Test]
    public async Task Audit_NestedExtendedCollectionAsDictValue_DropsWithoutCompileBreak()
    {
        // Dictionary<string, HashSet<int>> values are not supported yet — dropped.
        var dto = new NestedValueDictDto
        {
            M = new Dictionary<string, HashSet<int>> { ["a"] = new HashSet<int> { 1 } },
            Plain = 8,
        };
        var json = JsonSerializer.Serialize(dto);
        await Assert.That(json).Contains("\"Plain\":8");
        await Assert.That(json.Contains("\"M\"")).IsFalse();
    }

    [Test]
    public async Task Audit_Boundaries_EmptyCollectionsAndExtremeScalars()
    {
        var dto = new CollectionCoverageDto
        {
            Set = new HashSet<int>(),
            Q = new Queue<int>(),
            S = new Stack<int>(),
            L = new LinkedList<int>(),
            Sorted = new SortedDictionary<string, int>(),
            Concurrent = new ConcurrentDictionary<string, int>(),
            Imm = ImmutableArray<int>.Empty,
            Mem = new Memory<int>(Array.Empty<int>()),
            RMem = new ReadOnlyMemory<int>(Array.Empty<int>()),
        };
        var back = JsonSerializer.Deserialize<CollectionCoverageDto>(
            JsonSerializer.SerializeToUtf8Bytes(dto)
        );
        await Assert.That(back!.Set.Count).IsEqualTo(0);
        await Assert.That(back.Q.Count).IsEqualTo(0);
        await Assert.That(back.S.Count).IsEqualTo(0);
        await Assert.That(back.L.Count).IsEqualTo(0);
        await Assert.That(back.Sorted.Count).IsEqualTo(0);
        await Assert.That(back.Imm.Length).IsEqualTo(0);
        await Assert.That(back.Mem.Length).IsEqualTo(0);

        var extreme = new ScalarCoverageDto
        {
            When = new DateTimeOffset(
                new DateTime(1, 1, 15, 0, 0, 0, DateTimeKind.Unspecified),
                TimeSpan.FromHours(-14)
            ),
            Ch = '\0',
            I128 = Int128.MinValue,
            U128 = UInt128.MaxValue,
            Big = BigInteger.MinusOne,
        };
        var extBack = JsonSerializer.Deserialize<ScalarCoverageDto>(
            JsonSerializer.SerializeToUtf8Bytes(extreme)
        );
        await Assert.That(extBack!.When).IsEqualTo(extreme.When);
        await Assert.That(extBack.When.Offset).IsEqualTo(TimeSpan.FromHours(-14));
        await Assert.That(extBack.Ch).IsEqualTo('\0');
        await Assert.That(extBack.I128).IsEqualTo(Int128.MinValue);
        await Assert.That(extBack.U128).IsEqualTo(UInt128.MaxValue);
        await Assert.That(extBack.Big).IsEqualTo(BigInteger.MinusOne);
    }

    [Test]
    public async Task Audit_HugeBigInteger_RoundTripsAsNumber()
    {
        // Beyond the 64-byte stack buffer: must stay a JSON number (never a quoted
        // string) and parse back without a length cap.
        var big = BigInteger.Pow(10, 99);
        var dto = new HugeBigDto { B = big };
        var json = JsonSerializer.Serialize(dto);
        await Assert.That(json).Contains("\"B\":1");
        await Assert.That(json.Contains("\"B\":\"")).IsFalse();
        var back = JsonSerializer.Deserialize<HugeBigDto>(JsonSerializer.SerializeToUtf8Bytes(dto));
        await Assert.That(back!.B).IsEqualTo(big);
    }

    [Test]
    public async Task Audit_LargeUri_RoundTrips()
    {
        // Longer than the 512-char stack buffer → heap path must handle it.
        var longUri = new Uri("https://example.com/" + new string('a', 1200));
        var dto = new LargeUriDto { Link = longUri };
        var back = JsonSerializer.Deserialize<LargeUriDto>(
            JsonSerializer.SerializeToUtf8Bytes(dto)
        );
        await Assert.That(back!.Link!.AbsoluteUri.Length).IsEqualTo(longUri.AbsoluteUri.Length);
    }
}

public class ExtKeyDictDto
{
    public Dictionary<DateTimeOffset, int> Map { get; set; } = new();
    public int Plain { get; set; }
}

public class NestedCollectionDto
{
    public List<HashSet<int>> L { get; set; } = new();
    public int Plain { get; set; }
}

public class HugeBigDto
{
    public BigInteger B { get; set; }
}

public class LargeUriDto
{
    public Uri? Link { get; set; }
}

public class NestedValueDictDto
{
    public Dictionary<string, HashSet<int>> M { get; set; } = new();
    public int Plain { get; set; }
}

/// <summary>
/// Regression model for the v2026.5.2 consuming-build break: the generated dict
/// init emitted `??= new Dictionary&lt;string, object&gt;()` for a
/// <c>Dictionary&lt;string, object?&gt;</c> property (nullable annotation lost by
/// TypeFullName) → CS8619 in nullable-enabled consumers (PicoAgent.Domain's
/// ContentBlock.Arguments). The init must be target-typed.
/// </summary>
public class AnyValueDictDto
{
    public string Name { get; set; } = "";
    public Dictionary<string, object?> Arguments { get; set; } = new();
}

public class AnyValueDictInitRegressionTests
{
    [Test]
    public async Task Missing_Property_Keeps_Target_Typed_Initialized_Dictionary()
    {
        var back = JsonSerializer.Deserialize<AnyValueDictDto>("""{"Name":"y"}"""u8);
        await Assert.That(back!.Arguments).IsNotNull();
        await Assert.That(back.Arguments.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AnyValue_Dictionary_RoundTrips_Mixed_Values()
    {
        var dto = new AnyValueDictDto
        {
            Name = "x",
            Arguments =
            {
                ["s"] = "text",
                ["b"] = true,
                ["nil"] = null,
            },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(dto);
        var back = JsonSerializer.Deserialize<AnyValueDictDto>(bytes);
        await Assert.That(back!.Arguments["s"]).IsEqualTo("text");
        await Assert.That(back.Arguments["b"]).IsEqualTo(true);
        await Assert.That(back.Arguments["nil"]).IsNull();
    }
}
