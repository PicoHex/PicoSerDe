namespace PicoSerDe.Integration.Tests;

// ── The `object?` MEMBER: the shape five formats, one test file ──────────────────────────
//
// A member declared `object?` is TypeKind "any" (shared/Gen/TypeKindResolver.cs:186). Every
// format already carried that kind in TWO positions — a collection ELEMENT and a dictionary
// VALUE — but not in the MEMBER position, and the member position failed in two different
// ways depending on the generator:
//
//   * the READ emitted nothing at all (MsgPack and INI had no `case "any"` in the member-read
//     switch, so the generated code was an empty block), i.e. a value that had just been
//     written came back null — silently, with no diagnostic;
//   * the WRITE fell through to a `ToString()` scalar fallback, so a non-string value lost its
//     type on the wire (MsgPack) or was written as text by design (TOML/YAML/INI, whose own
//     dict-VALUE arms do the same).
//
// These fixtures are the DTO from the defect report, character for character:
//
//     [PicoSerializable] public sealed class ObjMember { public string Name { get; set; } = "n";
//                                                        public object? Result { get; set; } }
//
// ⚠ JSON'S ROW IS COVERED BY THE SIBLING LANE, NOT HERE. On the commit this file was written
// against, `PicoJetson`'s member write emits an EMPTY `if (value.Result != null) { }` block
// (FormatException "Cannot close object: property name was written with no value") and its
// member read emits an empty `else { }` block. That is the same defect and it is being fixed in
// branch `fix/any-member-object` (PicoJetson/src/PicoJetson.Gen), so this file does not assert
// the json row: an assertion pinned to today's behaviour would have to demand either the bug or
// the fix, and both are wrong once the two branches meet. When that lane lands, the 5×3 audit
// table in `.superpowers/sdd/any-cross/report.md` is the record of what was measured here.
//
// The DTOs are namespace-level because the generators skip types with a containing type.

/// <summary>The defect-report DTO: a <see cref="string"/> and one <c>object?</c> member.</summary>
[PicoSerializable]
public sealed class AnyMemberDoc
{
    public string Name { get; set; } = "n";

    public object? Result { get; set; }
}

/// <summary>
/// TWO <c>object?</c> members on one type. Not decoration: both fixed generators emit their
/// any-read as an if/else chain of locals (<c>__i</c>, <c>__l</c>, <c>__d</c>, <c>__b</c>) into
/// the same <c>switch (__k)</c> scope, which is one scope in C# — a second member would be
/// CS0128 ("a local variable named '__i' is already declared") if the arms were not self-scoped.
/// This fixture is what keeps that true.
/// </summary>
[PicoSerializable]
public sealed class TwoAnyMemberDoc
{
    public object? First { get; set; }

    public object? Second { get; set; }
}

/// <summary>
/// A NON-nullable <c>object</c> member. It has no null literal to read, so the two fixed
/// generators must not write <c>null</c> into it (CS8625 inside generated code the consumer
/// cannot edit): MsgPack refuses a nil the way it refuses one for every other non-nullable
/// member, and INI keeps its <c>null!</c> contract (<c>shared/Gen/NullGuardEmit.cs</c>).
/// </summary>
[PicoSerializable]
public sealed class NonNullableAnyMemberDoc
{
    public object Value { get; set; } = "seed";
}

/// <summary>
/// The <c>object?</c> MEMBER across the four formats this branch owns (see the file header for
/// the json row): write a value, write null, read the value back — plus the type-fidelity and
/// null-target edges the fix had to decide.
/// </summary>
public class AnyMemberCrossFormatTests
{
    private const string Value = "boom";

    // ── MsgPack: the read side emitted nothing at all ─────────────────────────────────────

    /// <summary>
    /// The defect, exactly: this is cell W1 + cell R of the audit table for msgpack. The write
    /// already produced `boom` (as the text of a ToString), but the read emitted NOTHING — no
    /// case, no assignment — so the member came back null. A body the caller had just serialized
    /// was gone after one round trip, silently.
    /// </summary>
    [Test]
    public async Task MsgPack_any_member_round_trips_the_value_instead_of_dropping_it()
    {
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(
            new AnyMemberDoc { Name = "n", Result = Value }
        );

        await Assert
            .That(Encoding.UTF8.GetString(bytes))
            .Contains(Value)
            .Because("the value reaches the wire (this half always worked)");

        var back = MsgPackSerializer.Deserialize<AnyMemberDoc>(bytes);

        await Assert.That(back).IsNotNull();
        await Assert
            .That(back!.Result)
            .IsEqualTo(Value)
            .Because(
                "the member read used to emit an empty `case 1: break;`, so the value came back "
                    + "null with no diagnostic — the silent drop this test exists for"
            );
    }

    /// <summary>
    /// The type-fidelity half of the same fix. The member write fell through to
    /// <c>mw.WriteString(value.Result.ToString())</c>, so <c>object? = 42L</c> was written as the
    /// TEXT "42" and read back as a string — while the SAME value inside a
    /// <c>Dictionary&lt;string, object?&gt;</c> kept its long, because the dict-VALUE position
    /// already used <c>EmitMsgPackAnySerialize</c>. The member position reuses that pair now.
    /// </summary>
    [Test]
    public async Task MsgPack_any_member_keeps_the_value_type_across_the_round_trip()
    {
        var back = MsgPackSerializer.Deserialize<AnyMemberDoc>(
            MsgPackSerializer.SerializeToUtf8Bytes(new AnyMemberDoc { Result = 42L })
        );

        await Assert
            .That(back!.Result)
            .IsTypeOf<long>()
            .Because("the any EMITTER writes WriteInt64 for a long; the ToString fallback did not");
        await Assert.That(back.Result).IsEqualTo(42L);
    }

    /// <summary>
    /// Cell W2: MessagePack has a nil, and the member write already used it (the comment in the
    /// generated code reads <c>else mw.WriteNull()</c> — the same guard that closes the any arm).
    /// This pins that the fix did not trade it for an omission.
    /// </summary>
    [Test]
    public async Task MsgPack_any_member_writes_an_explicit_nil_for_null()
    {
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(new AnyMemberDoc { Result = null });

        // 0xC0 is MessagePack's nil. The map is {0: "n", 1: nil}, so the LAST byte is the value
        // of key 1 — "omitted" would leave the map ending after "n" instead.
        await Assert
            .That(bytes[^1])
            .IsEqualTo((byte)0xC0)
            .Because(
                $"a null object? member must be the nil literal, not a shorter map: {Show(bytes)}"
            );

        await Assert
            .That(MsgPackSerializer.Deserialize<AnyMemberDoc>(bytes)!.Result)
            .IsNull()
            .Because("nil reads back as null");
    }

    /// <summary>
    /// The nil contract for a member that cannot hold it. MsgPack's README: "a nil maps to null
    /// for a nullable member and throws FormatException for a non-nullable one". The any arm used
    /// to assign <c>null</c> unconditionally, which would have been CS8625 inside generated code
    /// for a plain <c>object</c> member — the same rule as every other kind, spelled here.
    /// </summary>
    [Test]
    public async Task MsgPack_any_member_refuses_a_nil_for_a_non_nullable_target()
    {
        // {0: nil} — a non-nullable object member must refuse it, not store null in it.
        byte[] payload = [0x81, 0x00, 0xC0];

        await Assert
            .That(() => MsgPackSerializer.Deserialize<NonNullableAnyMemberDoc>(payload))
            .Throws<FormatException>()
            .Because("a nil into a non-nullable object member is a wire error, not a null");
    }

    /// <summary>Two <c>object?</c> members: the any-read arms must not collide (CS0128).</summary>
    [Test]
    public async Task MsgPack_two_any_members_both_round_trip()
    {
        var back = MsgPackSerializer.Deserialize<TwoAnyMemberDoc>(
            MsgPackSerializer.SerializeToUtf8Bytes(new TwoAnyMemberDoc { First = "a", Second = 7L })
        );

        await Assert.That(back!.First).IsEqualTo("a");
        await Assert.That(back.Second).IsEqualTo(7L);
    }

    // ── INI: the read side emitted an empty else-block ────────────────────────────────────

    /// <summary>
    /// The defect, exactly: cell W1 + cell R for ini. <c>Result = boom</c> reached the wire (INI
    /// writes every value as text), but the reader had no case for the kind, so the generated
    /// <c>else { }</c> was EMPTY and the member kept its initializer. INI's own rule for a value
    /// is "the text between '=' and the end of the line" (its scalar <c>default:</c> arm), and an
    /// <c>object?</c> member is that same rule.
    /// </summary>
    [Test]
    public async Task Ini_any_member_reads_its_text_back_instead_of_dropping_it()
    {
        var bytes = IniSerializer.SerializeToUtf8Bytes(
            new AnyMemberDoc { Name = "n", Result = Value }
        );

        await Assert.That(Encoding.UTF8.GetString(bytes)).Contains(Value);

        var back = IniSerializer.Deserialize<AnyMemberDoc>(bytes);

        await Assert.That(back).IsNotNull();
        await Assert
            .That(back!.Result)
            .IsEqualTo(Value)
            .Because("the empty else-block is what dropped it; INI's value rule is its text");
    }

    /// <summary>
    /// INI carries no value TYPES on the wire — a value is text — so a non-string value survives
    /// as its own text. This is the format boundary stated in PicoIni/README.md (Limits), pinned
    /// by value rather than by prose: the value is not lost, only its type is.
    /// </summary>
    [Test]
    public async Task Ini_any_member_round_trips_a_non_string_value_as_its_text()
    {
        var back = IniSerializer.Deserialize<AnyMemberDoc>(
            IniSerializer.SerializeToUtf8Bytes(new AnyMemberDoc { Result = 42L })
        );

        await Assert.That(back!.Result).IsEqualTo("42");
        await Assert
            .That(back.Result)
            .IsTypeOf<string>()
            .Because("ini has no typed value on the wire — its reader yields the text");
    }

    /// <summary>
    /// Cell W2 for ini: a null member is OMITTED, which PicoIni/README.md (§ Null Handling)
    /// documents as the format's own limit — INI has no null value on the write side. Pinned so
    /// the new any arm cannot start writing the four characters "null" (a quoted <c>"null"</c> is
    /// a string in this format, so the ambiguity has to stay on the omission side).
    /// </summary>
    [Test]
    public async Task Ini_any_member_null_is_omitted_not_written_as_text()
    {
        var text = IniSerializer.Serialize(new AnyMemberDoc { Name = "n", Result = null });

        await Assert.That(text).DoesNotContain("Result").Because("⛔ ini has no null value");
        await Assert
            .That(IniSerializer.Deserialize<AnyMemberDoc>(Encoding.UTF8.GetBytes(text))!.Result)
            .IsNull();
    }

    /// <summary>Two <c>object?</c> members, and the non-nullable contract (text is never null).</summary>
    [Test]
    public async Task Ini_any_members_round_trip_independently()
    {
        var back = IniSerializer.Deserialize<TwoAnyMemberDoc>(
            IniSerializer.SerializeToUtf8Bytes(new TwoAnyMemberDoc { First = "a", Second = 7L })
        );

        await Assert.That(back!.First).IsEqualTo("a");
        await Assert.That(back.Second).IsEqualTo("7");
    }

    // ── TOML: the member position already worked — pinned so it cannot regress ───────────

    /// <summary>
    /// TOML was NOT broken by this defect: its member write already had an any arm
    /// (<c>accessor?.ToString() ?? ""</c>) and its member read falls through to the same
    /// <c>GetString(ValueSpan)</c> the dict-VALUE any read uses — the "TOML values lose type
    /// info" rule the format's tests already state. Cell W1 + cell R pass; this pins them.
    /// </summary>
    [Test]
    public async Task Toml_any_member_round_trips_its_text()
    {
        var bytes = TomlSerializer.SerializeToUtf8Bytes(
            new AnyMemberDoc { Name = "n", Result = Value }
        );

        await Assert.That(Encoding.UTF8.GetString(bytes)).Contains(Value);
        await Assert.That(TomlSerializer.Deserialize<AnyMemberDoc>(bytes)!.Result).IsEqualTo(Value);
    }

    /// <summary>
    /// Cell W2 for toml: TOML has no null literal, so a null member is omitted — documented in
    /// PicoToml/README.md (§ Null Handling, ⛔ format fact). The reader maps the unquoted word
    /// <c>null</c> to null for nullable members, but WRITING it would produce a document no other
    /// TOML parser accepts, so omission is the correct side to stay on.
    /// </summary>
    [Test]
    public async Task Toml_any_member_null_is_omitted_because_toml_has_no_null_literal()
    {
        var text = TomlSerializer.Serialize(new AnyMemberDoc { Name = "n", Result = null });

        await Assert.That(text).DoesNotContain("Result").Because("⛔ TOML has no null literal");
        await Assert
            .That(TomlSerializer.Deserialize<AnyMemberDoc>(Encoding.UTF8.GetBytes(text))!.Result)
            .IsNull();
    }

    // ── YAML: W1/R worked; the write needed the null-forgiving `!` its siblings spell ─────

    /// <summary>
    /// YAML's member read already returned the scalar's text (its member switch falls through to
    /// <c>GetString(ValueSpan)</c>, the dict-VALUE any rule), so W1 and R passed. What the fix
    /// touched is the write: <c>object? x</c> was written through <c>x.ToString()</c>, which is
    /// declared to return <c>string?</c> — so the generated code carried
    /// <c>Encoding.GetBytes(string s)</c> a possibly-null argument, and every consumer with
    /// nullable enabled got CS8604 from a file they cannot edit. The arm now spells the <c>!</c>
    /// that every other any-emitting site in that generator already spelled.
    /// <para>
    /// The `!` cannot be asserted from here (a warning is not a value) — the build is the gate
    /// for that. This test pins the value path around it.
    /// </para>
    /// </summary>
    [Test]
    public async Task Yaml_any_member_round_trips_its_text()
    {
        var bytes = YamlSerializer.SerializeToUtf8Bytes(
            new AnyMemberDoc { Name = "n", Result = Value }
        );

        await Assert.That(Encoding.UTF8.GetString(bytes)).Contains(Value);
        await Assert.That(YamlSerializer.Deserialize<AnyMemberDoc>(bytes)!.Result).IsEqualTo(Value);

        // A non-string value survives as its text, exactly like TOML/INI: this format's any arms
        // write `value.ToString()` by design (its dict-VALUE arm does the same).
        await Assert
            .That(
                YamlSerializer
                    .Deserialize<AnyMemberDoc>(
                        YamlSerializer.SerializeToUtf8Bytes(new AnyMemberDoc { Result = 42L })
                    )!
                    .Result
            )
            .IsEqualTo("42");
    }

    /// <summary>
    /// Cell W2 for yaml: a null MEMBER is omitted (PicoYaml/README.md § Null Handling draws the
    /// line explicitly — a null *element* and a null *dictionary value* are written as the null
    /// literal, a null *member* is not). The reader can take `Result: null`, but this branch does
    /// not change that per-format convention: it is uniform for every nullable member, not a
    /// property of `object?`.
    /// </summary>
    [Test]
    public async Task Yaml_any_member_null_is_omitted_like_every_other_nullable_member()
    {
        var text = YamlSerializer.Serialize(new AnyMemberDoc { Name = "n", Result = null });

        await Assert.That(text).DoesNotContain("Result");
        await Assert
            .That(YamlSerializer.Deserialize<AnyMemberDoc>(Encoding.UTF8.GetBytes(text))!.Result)
            .IsNull();
    }

    // ── The generator side: no format may DROP the member, and none may claim it is a drop ──

    /// <summary>
    /// The member must reach the generated code in every format this branch owns — the empty
    /// read block was the whole bug for two of them — and no generator may report a drop for it,
    /// because every one of these four formats CAN carry an <c>object?</c> member. That pair is
    /// the negative control for the ⛔ machinery in
    /// <see cref="SilentDropDiagnosticTests"/>: if a later change decides one of these formats
    /// cannot hold the shape, this test has to be the one that changes, together with a
    /// PICOSERDE0xx diagnostic and a README Limits line.
    /// </summary>
    [Test]
    public async Task Every_owned_format_emits_the_any_member_and_reports_no_drop()
    {
        const string shape = """

            [PicoSerDe.Core.PicoSerializable]
            public sealed class ProbeDoc
            {
                public string Name { get; set; } = "n";
                public object? Result { get; set; }
            }
            """;

        // ReadAssignment is the emitted READ arm for `Result`, spelled with each format's own
        // reader. It is the half that was MISSING (an empty block leaves no such text at all), so
        // this table would have been red for msgpack and ini before the fix — a "the member is
        // mentioned somewhere" assertion would not have been.
        var formats = new (
            string Tag,
            string ReadAssignment,
            Func<string, (string[] Ids, string Generated, string[] _)> Run
        )[]
        {
            (
                "msgpack",
                "obj.Result = Encoding.UTF8.GetString(reader.GetStringRaw());",
                s => SilentDropDiagnosticTests.RunMsgPack("using PicoMsgPack;" + s)
            ),
            (
                "toml",
                "o.Result = Encoding.UTF8.GetString(r.ValueSpan);",
                s => SilentDropDiagnosticTests.RunToml("using PicoToml;" + s)
            ),
            (
                "yaml",
                "o.Result = Encoding.UTF8.GetString(r.ValueSpan);",
                s => SilentDropDiagnosticTests.RunYaml("using PicoYaml;" + s)
            ),
            (
                "ini",
                "obj.Result = Encoding.UTF8.GetString(reader.GetStringRaw());",
                s => SilentDropDiagnosticTests.RunIni("using PicoIni;" + s)
            ),
        };

        foreach (var (tag, readAssignment, run) in formats)
        {
            var (ids, generated, messages) = run(shape);

            await Assert
                .That(ids)
                .IsEmpty()
                .Because(
                    $"{tag} can hold an object? member, so it must not report a drop: "
                        + string.Join(" | ", messages)
                );
            await Assert
                .That(generated)
                .Contains(readAssignment)
                .Because(
                    $"{tag}: the member's READ arm is what came out empty, so the generated "
                        + $"reader has to contain '{readAssignment}'"
                );
        }
    }

    private static string Show(byte[] bytes)
    {
        var sb = new StringBuilder();
        foreach (var b in bytes)
            sb.Append(b >= 0x20 && b < 0x7f ? ((char)b).ToString() : "\\x" + b.ToString("X2"));
        return sb.ToString();
    }
}
