// YAML's LAST zero-member debt: a `[PicoSerializable]` type that declares ZERO members is
// the EMPTY OBJECT, and YAML is the fifth format that must carry it — the other four already
// do (json `{}`, msgpack an empty map, toml `[Mark]`, ini `[Mark]`; `ZeroMemberObjectShapeTests`
// pins them). Until now YAML DROPPED the member with PICOSERDE001 ⏸, so a parent that held a
// marker type serialized as if the member did not exist — a silently wrong document.
//
// The two halves of the debt were measured separately and both said "the format can hold it":
// PyYAML reads `Mark: {}` as `{'Mark': {}}` AND our own `YamlReader` tokenizes it as
// `PropertyName,ObjectStart,ObjectEnd` (`ZeroMemberObjectShapeTests
// .Yaml_Reader_HoldsTheEmptyFlowMapping`). What was missing was the WRITE form, and the one
// form that must never be used is the tempting one: `Mark:` alone is YAML for NULL, not for an
// empty mapping.
//
// WHAT THIS FILE PINS, for both positions a member can take:
//
//   member `Mark`    ->  `Mark:` + an indented `{}` line      (the reader's own
//                        "flow collection on its own indented line" branch, already pinned by
//                        `EmptyFlowMappingTests.EmptyFlowMappingOnItsOwnIndentedLine_...`)
//   element `Marks`  ->  one `-` line with an indented `{}`   (`Marks: [{}]` semantically)
//
// and, for both, that the value comes BACK: a `{}` must deserialize into an INSTANCE, not into
// null — "it reaches the wire" and "it reads back" are two different claims.
//
// The fixtures are `[PicoYamlSerializable]` rather than the cross-format `[PicoSerializable]`
// on purpose: the attribute form is what makes each generator emit a serializer for the
// MARKER TYPE ITSELF, and this project builds with PICOSERDE001 UNSILENCED (see the csproj's
// NoWarn comment) — every format must therefore be able to carry the fixture, or the build
// reports the drop as a warning. INI cannot hold an object LIST (§3 S2, a format fact) and
// json/msgpack/toml would emit three more serializers that no test here is about. The
// cross-format attribute pipeline the bug report used is driven in-proc by
// `ZeroMemberObjectShapeTests.ZeroMemberObjectMember_IsNoLongerDroppedByTheFormatsThatHoldIt`,
// whose yaml row this fix flips.

using System.Text;

namespace PicoSerDe.Integration.Tests;

/// <summary>BU-4's YAML half: a type with ZERO members. Its shape is the empty mapping.</summary>
[PicoYamlSerializable]
public sealed class YamlMark { }

/// <summary>
/// The parent, holding the marker in both reported positions: a plain nullable member and an
/// object-list element.
/// </summary>
[PicoYamlSerializable]
public sealed class YamlMarkSession
{
    public string Name { get; set; } = "";
    public YamlMark? Mark { get; set; }
    public List<YamlMark> Marks { get; set; } = new();
}

/// <summary>A non-empty nested object whose only member is the marker (two levels deep).</summary>
[PicoYamlSerializable]
public sealed class YamlMarkHolder
{
    public YamlMark? Mark { get; set; }
}

/// <summary>The root of the two-level case: <c>Inner.Mark</c> must stay under <c>Inner</c>.</summary>
[PicoYamlSerializable]
public sealed class YamlMarkOuter
{
    public YamlMarkHolder? Inner { get; set; }
}

/// <summary>
/// A type whose ONLY member is ignored: nothing is written, which is the same wire shape as
/// declaring no member at all (and is what an emitter must not mistake for "unresolvable").
/// </summary>
[PicoYamlSerializable]
public sealed class YamlMuted
{
    [PicoIgnore]
    public int Shape { get; set; }
}

/// <summary>The parent that holds <see cref="YamlMuted"/>.</summary>
[PicoYamlSerializable]
public sealed class YamlMutedHolder
{
    public YamlMuted? Muted { get; set; }
}

/// <summary>
/// The write form, the read-back, and the round trip of an empty object in YAML — one
/// assertion set per position, plus the negative that keeps the form honest.
/// </summary>
public class YamlZeroMemberObjectTests
{
    // ── member position ───────────────────────────────────────────────────────────

    /// <summary>
    /// The write form. A block mapping with no members writes NOTHING today, so the member
    /// used to vanish entirely (dropped by the generator) and, if it were merely emitted with
    /// the calls the generator already has, `<c>Mark:</c>` would be YAML for null.
    /// </summary>
    [Test]
    public async Task Yaml_ZeroMemberObjectMember_IsEmittedAsAnEmptyMapping()
    {
        var yaml = YamlSerializer.Serialize(new YamlMarkSession { Name = "s", Mark = new() });

        // The key FOLLOWED BY its value, and nothing else: an empty mapping is exactly
        // ObjectStart,ObjectEnd on the key's own line (`Mark: {}`). No scalar token means no
        // reader can take it for text, and no second ObjectStart means no reader has to
        // reconcile a nested empty mapping this document never said.
        await Assert
            .That(TokenShape(yaml, "Mark"))
            .IsEqualTo("PropertyName,ObjectStart,ObjectEnd")
            .Because(
                "the member must reach the wire as a MAPPING with zero members, not be dropped "
                    + $"and not be left as a bare `Mark:` (which is null): {yaml}"
            );

        await Assert
            .That(yaml)
            .Contains("Mark: {}")
            .Because($"the empty mapping is the flow form on the key's own line: {yaml}");
    }

    [Test]
    public async Task Yaml_ZeroMemberObjectMember_ReadsBackAsAnInstance()
    {
        var bytes = Encoding.UTF8.GetBytes(
            YamlSerializer.Serialize(new YamlMarkSession { Name = "s", Mark = new() })
        );

        var back = YamlSerializer.Deserialize<YamlMarkSession>(bytes);

        await Assert
            .That(back)
            .IsNotNull()
            .Because($"the document must parse: {Encoding.UTF8.GetString(bytes)}");
        await Assert.That(back!.Name).IsEqualTo("s").Because("the sibling member must survive");
        await Assert
            .That(back.Mark)
            .IsNotNull()
            .Because(
                "a `{}` is an empty mapping, not null — reading it back as null is the same "
                    + $"silent loss as dropping it: {Encoding.UTF8.GetString(bytes)}"
            );
    }

    // ── list-element position ─────────────────────────────────────────────────────

    [Test]
    public async Task Yaml_ZeroMemberObjectElementInAList_ReadsBackAsInstances()
    {
        var bytes = Encoding.UTF8.GetBytes(
            YamlSerializer.Serialize(
                new YamlMarkSession
                {
                    Name = "s",
                    Marks = new List<YamlMark> { new(), new() },
                }
            )
        );

        var yaml = Encoding.UTF8.GetString(bytes);
        var back = YamlSerializer.Deserialize<YamlMarkSession>(bytes);

        await Assert.That(back).IsNotNull().Because($"the document must parse: {yaml}");
        await Assert
            .That(back!.Marks.Count)
            .IsEqualTo(2)
            .Because($"one empty mapping per element must read back as two: {yaml}");
        await Assert.That(back.Marks[0]).IsNotNull().Because($"element 0: {yaml}");
        await Assert.That(back.Marks[1]).IsNotNull().Because($"element 1: {yaml}");
    }

    [Test]
    public async Task Yaml_ZeroMemberObjectElementInAList_IsWrittenAsAMappingPerElement()
    {
        var yaml = YamlSerializer.Serialize(
            new YamlMarkSession
            {
                Name = "s",
                Marks = new List<YamlMark> { new(), new() },
            }
        );

        await Assert
            .That(TokenShape(yaml, "Marks"))
            .IsEqualTo(
                "PropertyName,ArrayStart,String,ObjectStart,ObjectEnd,"
                    + "String,ObjectStart,ObjectEnd,ArrayEnd"
            )
            .Because(
                "each element is a `-` item (`String`, a bare dash) whose value is an empty "
                    + $"mapping (`ObjectStart,ObjectEnd`) — PyYAML's `Marks: [{{}}, {{}}]`: {yaml}"
            );
    }

    // ── round trip, both positions at once ────────────────────────────────────────

    [Test]
    public async Task Yaml_ZeroMemberObject_RoundTripsInBothPositions()
    {
        var bytes = Encoding.UTF8.GetBytes(
            YamlSerializer.Serialize(
                new YamlMarkSession
                {
                    Name = "round",
                    Mark = new(),
                    Marks = new List<YamlMark> { new(), new(), new() },
                }
            )
        );

        var back = YamlSerializer.Deserialize<YamlMarkSession>(bytes);

        await Assert.That(back!.Name).IsEqualTo("round");
        await Assert.That(back.Mark).IsNotNull();
        await Assert.That(back.Marks.Count).IsEqualTo(3);
    }

    /// <summary>
    /// Two levels of nesting: the empty mapping must stay under <c>Inner</c> (a wrong depth
    /// would put it at the document root, where it would read as a sibling member).
    /// </summary>
    [Test]
    public async Task Yaml_NestedZeroMemberObject_TwoLevelsDeep_RoundTrips()
    {
        var yaml = YamlSerializer.Serialize(
            new YamlMarkOuter { Inner = new YamlMarkHolder { Mark = new() } }
        );

        var back = YamlSerializer.Deserialize<YamlMarkOuter>(Encoding.UTF8.GetBytes(yaml));

        await Assert.That(back).IsNotNull().Because($"the document must parse: {yaml}");
        await Assert.That(back!.Inner).IsNotNull().Because(yaml);
        await Assert
            .That(back.Inner!.Mark)
            .IsNotNull()
            .Because($"the nested empty mapping must read back as an instance: {yaml}");
    }

    // ── the predicate asks what extraction SAW, and one gap this project cannot drive ──

    // A TOP-LEVEL `List<Mark>` is deliberately NOT driven from this project. The element writer
    // and the helper registration are shared with the member position (that is the generator arm
    // this fix touched), but this project references ALL FIVE generators, and the OTHER formats
    // have the same `IsDefaultOrEmpty` skip in their own top-level-list helper registration —
    // JSON then emits a reference to a helper nobody generated (CS0234) and INI a scalar call
    // (CS1503) for the same `List<YamlMark>`. Both are pre-existing, belong to their own formats,
    // and are out of this change's scope; measured on a YAML-only project (probe, this commit):
    // two items, each a dash whose value is the flow pair, and `Deserialize<List<Mark>>`
    // returns 2 instances.
    /// <summary>
    /// The predicate asks what EXTRACTION saw, not what the type declares — the same reasoning
    /// TOML records for its identical predicate (`Toml_TypeWhoseMembersWereAllDropped_...`). A
    /// type whose only member is <c>[PicoIgnore]</c>d has nothing to write, not serializing it
    /// was the instruction, so <c>{}</c> IS its shape. It also pins the shape that used to make
    /// the generated code non-compiling: with no nested properties the member fell through to a
    /// scalar arm (CS0029) instead of the mapping one.
    /// </summary>
    [Test]
    public async Task Yaml_TypeWhoseOnlyMemberIsIgnored_IsStillTheEmptyMapping()
    {
        var yaml = YamlSerializer.Serialize(new YamlMutedHolder { Muted = new() });

        await Assert
            .That(yaml)
            .Contains("Muted: {}")
            .Because(
                $"an all-ignored type has nothing to write, so its shape is the empty mapping: {yaml}"
            );

        var back = YamlSerializer.Deserialize<YamlMutedHolder>(Encoding.UTF8.GetBytes(yaml));

        await Assert
            .That(back!.Muted)
            .IsNotNull()
            .Because($"`Muted: {{}}` must read back as an instance: {yaml}");
    }

    // ── the negative that keeps the form honest ──────────────────────────────────

    /// <summary>
    /// The form this fix must NOT use. A key with no value carries no value token at all — our
    /// reader emits <c>PropertyName</c> and then the next key — and PyYAML resolves the same
    /// line to <c>None</c>. So a mapping written as a bare <c>Mark:</c> would say "a key with
    /// no value" to every reader: the same silent loss the drop was.
    /// </summary>
    [Test]
    public async Task Yaml_BareKeyForm_CarriesNoValue_WhichIsWhyTheWriterOwesTheFlowMapping()
    {
        await Assert
            .That(TokenShape("Name: s\nMark:\n", "Mark"))
            .IsEqualTo("PropertyName")
            .Because(
                "`Mark:` with nothing under it says null (PyYAML: {'Mark': None}) — there is not "
                    + "even a value token to read"
            );

        await Assert
            .That(TokenShape("Name: s\nMark:\n  {}\n", "Mark"))
            .Contains("ObjectStart,ObjectEnd")
            .Because(
                "the form the writer owes: the same line plus the flow pair, which is the empty "
                    + "mapping (PyYAML: {'Mark': {}})"
            );
    }

    /// <summary>
    /// The token stream that follows <paramref name="key"/>: the key's own
    /// <c>PropertyName</c> followed by every token up to the end of its value. Reading OUR OWN
    /// document back through OUR OWN reader is only HALF the proof — the other half is an
    /// external parser (PyYAML: <c>yaml.safe_load</c> of this exact document), which is why
    /// these tests never rely on the round trip alone to say "legal YAML".
    /// </summary>
    private static string TokenShape(string yaml, string key)
    {
        var reader = new YamlReader(Encoding.UTF8.GetBytes(yaml));
        var tokens = new List<string>();
        var collecting = false;
        var depth = 0;

        while (reader.Read())
        {
            if (!collecting)
            {
                if (
                    reader.TokenType == TokenType.PropertyName
                    && Encoding.UTF8.GetString(reader.KeySpan) == key
                )
                {
                    collecting = true;
                    tokens.Add(nameof(TokenType.PropertyName));
                    depth = 0;
                }
                continue;
            }

            tokens.Add(reader.TokenType.ToString());

            // The value ends when the containers it opened are closed again: a mapping's
            // ObjectEnd or a sequence's ArrayEnd, back at the depth the value started at.
            if (reader.TokenType is TokenType.ObjectStart or TokenType.ArrayStart)
                depth++;
            else if (reader.TokenType is TokenType.ObjectEnd or TokenType.ArrayEnd)
            {
                depth--;
                if (depth == 0)
                    break;
            }
        }

        return string.Join(",", tokens);
    }
}
