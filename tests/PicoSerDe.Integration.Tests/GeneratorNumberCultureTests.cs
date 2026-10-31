using System.Globalization;

namespace PicoSerDe.Integration.Tests;

// ── The GENERATED code may not borrow the machine's number spelling either ───────────
//
// The writers already pin their numbers to the invariant culture (`91437c1`). What that
// commit did not cover is the text the GENERATORS hand to those writers: an `int` that
// reaches the wire as `x.ToString()` (`StringBuilder.Append(object)`, `string.Join(",", …)`
// with no provider, `Enum.ToString()`) is formatted by the ambient culture before the writer
// ever sees it. `sv-SE` spells the minus sign U+2212, so `-42` leaves as five bytes that no
// other machine reads back as a negative number.
//
// Four entries were still open after `91437c1`, all four of them `main`-era (independent
// review C7 §2.5):
//   ① INI `List<int>` / `List<long>`      → `string.Join(",", value.Ints)`
//   ② INI `Dictionary<string,int>` value  → `__kvp.Value.ToString()`
//   ③ INI undefined enum value            → `Encoding.UTF8.GetBytes(value.Undef.ToString())`
//   ④ MsgPack `DateOnly` / `Guid` / undefined enum → `mw.WriteString(value.X.ToString())`
//     (paired with the reader's `DateOnly.TryParse` — which is culture-sensitive too)
//
// [NotInParallel] + restore-in-`finally`: this class moves process-wide culture state, the
// precedent is `WriterNumberCultureTests` / `tests/PicoBench.Protocol.Tests/ProtocolCultureTests.cs`.

/// <summary>An enum with a NEGATIVE member — so a defined value has a name, an undefined one does not.</summary>
public enum SignedLevel
{
    Zero = 0,
    Neg = -1,
}

/// <summary>The collection / dictionary / enum members the four residues live behind.</summary>
[PicoSerializable]
public class GeneratorCollectionCultureModel
{
    public List<int> Ints { get; set; } = [];
    public List<long> Longs { get; set; } = [];
    public Dictionary<string, int> Map { get; set; } = new();
    public SignedLevel Defined { get; set; }
    public SignedLevel Undefined { get; set; }
}

/// <summary>The scalar members MessagePack writes as TEXT (the format is binary; these are not).</summary>
[PicoSerializable]
public class GeneratorScalarCultureModel
{
    public DateOnly Day { get; set; }
    public Guid Id { get; set; }
    public SignedLevel Defined { get; set; }
    public SignedLevel Undefined { get; set; }
}

/// <summary>
/// The dictionary value kinds <see cref="IniWriter"/> has NO <c>(string, T)</c> overload for.
/// <para>
/// <c>IniWriter</c> carries a <c>ulong</c> arm only under a <c>ReadOnlySpan&lt;byte&gt;</c> KEY
/// (<c>WriteKeyValue(ReadOnlySpan&lt;byte&gt;, ulong)</c>). The dictionary emitter passes the
/// dictionary's own key, which is a <c>string</c>, so the string-key overload set decides — and
/// it has <c>int</c>/<c>long</c>/<c>bool</c>/<c>double</c>/<c>decimal</c> but no <c>ulong</c>,
/// <c>uint</c>, <c>short</c>, <c>byte</c> or <c>float</c>. A bare <c>__kvp.Value</c> for a
/// <c>ulong</c> therefore ranks <c>ulong → double</c> against <c>ulong → decimal</c>, neither of
/// which is better, and the consumer's generated file does not compile at all
/// (<c>error CS0121</c>). These two members are what makes that shape permanent coverage.
/// </para>
/// </summary>
[PicoSerializable]
public class GeneratorUnsignedDictionaryCultureModel
{
    public Dictionary<string, ulong> ULongs { get; set; } = new();
    public Dictionary<string, uint> UInts { get; set; } = new();
}

/// <summary>Culture-independence of the GE generated text paths listed above.</summary>
[NotInParallel]
public class GeneratorNumberCultureTests
{
    /// <summary>U+2212 MINUS SIGN — what <c>sv-SE</c> uses where the rest of the world writes 0x2D.</summary>
    private const string NonAsciiMinusSign = "\u2212";

    /// <summary>What has to be on the wire for a negative number, in every culture.</summary>
    private const string AsciiMinusSign = "-";

    private static readonly CultureInfo Swedish = new("sv-SE");
    private static readonly CultureInfo French = new("fr-FR");

    /// <summary>The four residues, in one model: two integer lists, a dictionary, two enum values.</summary>
    private static GeneratorCollectionCultureModel CreateCollections() =>
        new()
        {
            Ints = [-42, 7],
            Longs = [-9_876_543_210L],
            Map = new Dictionary<string, int> { ["k"] = -42 },
            Defined = SignedLevel.Neg,
            Undefined = (SignedLevel)(-42),
        };

    /// <summary><see cref="DateOnly"/>, <see cref="Guid"/> and both enum shapes for MessagePack.</summary>
    private static GeneratorScalarCultureModel CreateScalars() =>
        new()
        {
            Day = new DateOnly(2026, 6, 4),
            Id = Guid.Parse("6f8fad5b-d9cb-469f-a165-70867728950e"),
            Defined = SignedLevel.Neg,
            Undefined = (SignedLevel)(-42),
        };

    /// <summary>The two values a sign, a culture or a truncation would ruin: the maxima.</summary>
    private static GeneratorUnsignedDictionaryCultureModel CreateUnsignedDictionaries() =>
        new()
        {
            ULongs = new Dictionary<string, ulong> { ["max"] = ulong.MaxValue, ["zero"] = 0UL },
            UInts = new Dictionary<string, uint> { ["umax"] = uint.MaxValue, ["zero"] = 0U },
        };

    private static byte[] IniUnsignedDictionaries() =>
        IniSerializer.SerializeToUtf8Bytes(CreateUnsignedDictionaries());

    private static byte[] IniCollections() =>
        IniSerializer.SerializeToUtf8Bytes(CreateCollections());

    private static byte[] MsgPackScalars() =>
        MsgPackSerializer.SerializeToUtf8Bytes(CreateScalars());

    // ── the oracle itself ────────────────────────────────────────────────────────────

    /// <summary>
    /// The culture under test must really be hostile, or every assertion below passes for the
    /// wrong reason. If a future ICU spells the Swedish minus sign as ASCII this fails loudly
    /// here instead of turning the class into a no-op.
    /// </summary>
    [Test]
    public async Task The_culture_under_test_really_differs_from_invariant()
    {
        await Assert.That(Swedish.NumberFormat.NegativeSign).IsEqualTo(NonAsciiMinusSign);
        await Assert
            .That(CultureInfo.InvariantCulture.NumberFormat.NegativeSign)
            .IsEqualTo(AsciiMinusSign);
    }

    // ── ① INI: List<int> / List<long> ────────────────────────────────────────────────

    /// <summary>
    /// ① <c>string.Join(",", value.Ints)</c> — the join formats each element with
    /// <c>int.ToString()</c>, i.e. under the ambient culture.
    /// </summary>
    [Test]
    public async Task Ini_list_of_integers_is_the_same_in_every_culture()
    {
        var invariant = InCulture(CultureInfo.InvariantCulture, IniCollections);
        var swedish = InCulture(Swedish, IniCollections);

        var text = Encoding.UTF8.GetString(invariant);
        await Assert.That(text).Contains("Ints = " + AsciiMinusSign + "42,7");
        await Assert.That(text).Contains("Longs = " + AsciiMinusSign + "9876543210");
        await Assert.That(text.Contains(NonAsciiMinusSign, StringComparison.Ordinal)).IsFalse();

        await Assert
            .That(Encoding.UTF8.GetString(swedish))
            .IsEqualTo(text)
            .Because("an INI List<int> under sv-SE must be byte-identical to invariant");
    }

    // ── ② INI: Dictionary<string,int> value ──────────────────────────────────────────

    /// <summary>
    /// ② <c>iw.WriteKeyValue(__kvp.Key, __kvp.Value.ToString())</c> — the dictionary's VALUE
    /// type is not known to the emitter, so it fell through to <c>object.ToString()</c>.
    /// </summary>
    [Test]
    public async Task Ini_dictionary_integer_value_is_the_same_in_every_culture()
    {
        var invariant = InCulture(CultureInfo.InvariantCulture, IniCollections);
        var swedish = InCulture(Swedish, IniCollections);

        var text = Encoding.UTF8.GetString(invariant);
        await Assert.That(text).Contains("k = " + AsciiMinusSign + "42");
        await Assert.That(text.Contains(NonAsciiMinusSign, StringComparison.Ordinal)).IsFalse();
        await Assert.That(Encoding.UTF8.GetString(swedish)).IsEqualTo(text);
    }

    // ── ②ᵇ INI: Dictionary<string,ulong> / <uint> — no exact (string, T) overload ──

    /// <summary>
    /// The GENERATED call site has to be resolvable, not merely correct-looking.
    /// <para>
    /// <c>iw.WriteKeyValue(__kvp.Key, __kvp.Value)</c> asks the compiler to rank
    /// <c>ulong → double</c> against <c>ulong → decimal</c>; neither conversion is better than the
    /// other, so <c>1e1945f</c>'s bare-value emission did not compile:
    /// <c>error CS0121: The call is ambiguous between 'IniWriter.WriteKeyValue(string, double)'
    /// and '(string, decimal)'</c> — inside code the consumer cannot edit. The generator now
    /// builds this value's text itself, in the invariant culture, and hands over a
    /// <see cref="string"/>. This test pins the half a compile error cannot: the BYTES.
    /// </para>
    /// </summary>
    [Test]
    public async Task Ini_unsigned_dictionary_is_the_same_in_every_culture()
    {
        var invariant = InCulture(CultureInfo.InvariantCulture, IniUnsignedDictionaries);
        var text = Encoding.UTF8.GetString(invariant);

        await Assert.That(text).Contains("max = 18446744073709551615");
        await Assert.That(text).Contains("umax = 4294967295");
        await Assert.That(text).Contains("zero = 0");
        // No sign of any kind: these are unsigned maxima, and a widened `double`/`decimal`
        // detour is exactly what used to make the number unrepresentable here.
        await Assert.That(text.Contains(NonAsciiMinusSign, StringComparison.Ordinal)).IsFalse();
        await Assert.That(text.Contains(AsciiMinusSign, StringComparison.Ordinal)).IsFalse();

        foreach (var culture in new[] { Swedish, French })
        {
            await Assert
                .That(InCulture(culture, IniUnsignedDictionaries).AsSpan().SequenceEqual(invariant))
                .IsTrue()
                .Because(
                    $"an INI Dictionary<string,ulong> under {culture.Name} must be byte-identical"
                        + " to the invariant serialization"
                );
        }
    }

    /// <summary>
    /// The value survives the round trip across cultures: written under <c>sv-SE</c>, read under
    /// <c>fr-FR</c>. <c>ulong.MaxValue</c> is the case a <c>double</c>/<c>decimal</c>/<c>long</c>
    /// detour would silently corrupt — 18446744073709551615 does not survive any of them.
    /// </summary>
    [Test]
    public async Task Ini_unsigned_dictionary_round_trips_its_extremes_across_cultures()
    {
        var written = InCulture(Swedish, IniUnsignedDictionaries);
        var back = InCulture(
            French,
            () => IniSerializer.Deserialize<GeneratorUnsignedDictionaryCultureModel>(written)
        );

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.ULongs["max"]).IsEqualTo(ulong.MaxValue);
        await Assert.That(back.ULongs["zero"]).IsEqualTo(0UL);
        await Assert.That(back.UInts["umax"]).IsEqualTo(uint.MaxValue);
        await Assert.That(back.UInts["zero"]).IsEqualTo(0U);
    }

    // ── ③ INI: an enum value that has no name ───────────────────────────────────────

    /// <summary>
    /// ③ <c>Encoding.UTF8.GetBytes(value.Undefined.ToString())</c> — a DEFINED enum value is a
    /// name and stays culture-free; an UNDEFINED one is the underlying number formatted by
    /// <c>Enum.ToString()</c>, which follows the ambient culture.
    /// </summary>
    [Test]
    public async Task Ini_undefined_enum_value_is_the_same_in_every_culture()
    {
        var invariant = InCulture(CultureInfo.InvariantCulture, IniCollections);
        var swedish = InCulture(Swedish, IniCollections);

        var text = Encoding.UTF8.GetString(invariant);
        await Assert.That(text).Contains("Undefined = " + AsciiMinusSign + "42");
        // ... while the DEFINED value keeps its name — this fix must not turn enums into numbers.
        await Assert.That(text).Contains("Defined = Neg");
        await Assert.That(text.Contains(NonAsciiMinusSign, StringComparison.Ordinal)).IsFalse();
        await Assert.That(Encoding.UTF8.GetString(swedish)).IsEqualTo(text);
    }

    // ── ④ MsgPack: DateOnly / Guid / undefined enum ─────────────────────────────────

    /// <summary>
    /// ④ <c>mw.WriteString(value.Day.ToString())</c> and its two neighbours in the same
    /// <c>switch</c> group. MessagePack is a binary format, but <c>DateOnly</c>, <c>Guid</c> and
    /// an undefined enum are TEXT on its wire (`0xAA` string header).
    /// </summary>
    [Test]
    public async Task MsgPack_scalars_are_the_same_in_every_culture()
    {
        var invariant = InCulture(CultureInfo.InvariantCulture, MsgPackScalars);

        // The DateOnly payload is ISO 8601 (`2026-06-04`), ASCII, and the same everywhere.
        var text = Encoding.UTF8.GetString(invariant);
        await Assert.That(text).Contains("2026-06-04");
        await Assert.That(text.Contains(NonAsciiMinusSign, StringComparison.Ordinal)).IsFalse();

        foreach (var culture in new[] { Swedish, French })
        {
            var actual = InCulture(culture, MsgPackScalars);
            await Assert
                .That(actual.AsSpan().SequenceEqual(invariant))
                .IsTrue()
                .Because($"MessagePack under {culture.Name} must be byte-identical to invariant");
        }
    }

    /// <summary>
    /// The reader half: a payload written under one culture has to come back with the SAME
    /// value under another. This is what makes ④ a wire bug and not a cosmetic one —
    /// <c>DateOnly.TryParse</c> without a provider reads the ambient culture's spelling.
    /// </summary>
    [Test]
    public async Task MsgPack_dateonly_round_trips_across_cultures()
    {
        var written = InCulture(Swedish, MsgPackScalars);
        var readBack = InCulture(
            French,
            () => MsgPackSerializer.Deserialize<GeneratorScalarCultureModel>(written)
        );

        await Assert.That(readBack).IsNotNull();
        await Assert.That(readBack!.Day).IsEqualTo(new DateOnly(2026, 6, 4));
        await Assert.That(readBack.Undefined).IsEqualTo((SignedLevel)(-42));
        await Assert.That(readBack.Defined).IsEqualTo(SignedLevel.Neg);
    }

    /// <summary>
    /// The value survives a write/read pair in ONE culture too — the fix must not break the
    /// single-culture path it was already serving.
    /// </summary>
    [Test]
    public async Task Ini_collections_and_enums_round_trip_under_one_culture()
    {
        var bytes = InCulture(Swedish, IniCollections);
        var readBack = InCulture(
            Swedish,
            () => IniSerializer.Deserialize<GeneratorCollectionCultureModel>(bytes)
        );

        await Assert.That(readBack).IsNotNull();
        await Assert.That(readBack!.Ints).IsEquivalentTo(new List<int> { -42, 7 });
        await Assert.That(readBack.Longs).IsEquivalentTo(new List<long> { -9_876_543_210L });
        await Assert.That(readBack.Map["k"]).IsEqualTo(-42);
        await Assert.That(readBack.Undefined).IsEqualTo((SignedLevel)(-42));
        await Assert.That(readBack.Defined).IsEqualTo(SignedLevel.Neg);
    }

    // ── culture funnel ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <paramref name="action"/> with <see cref="CultureInfo.CurrentCulture"/> and
    /// <see cref="CultureInfo.CurrentUICulture"/> set to <paramref name="culture"/>, and puts
    /// both back afterwards — including when the action throws, so a red test cannot leave the
    /// rest of the assembly formatting under <c>sv-SE</c>.
    /// </summary>
    private static T InCulture<T>(CultureInfo culture, Func<T> action)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }
}
