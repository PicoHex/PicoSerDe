# Pending public API additions

**Status: empty as of `v2026.11.0`.** That release read all seven entries this table carried into the
committed baselines (`e19304b`, `chore(release): v2026.11.0`: `+57`, six files, `-0`), so nothing is
waiting for the next release. Where each one landed is recorded under "Released by `v2026.11.0`" below,
pinned to the baseline line that carries it, so the handover can be checked without trusting this file.

`api/*.public.txt` records **the last released surface**. It is deliberately not edited between
releases: the release script diffs the freshly dumped surface against it and reports the delta
(`+N / -M`), which is what drives the version bump. Editing a baseline by hand to match new code
would forge that diff, so a change that adds public surface leaves `api/` untouched and is listed
here instead.

Every entry is reviewed and ruled on before it ships, and the release that ships it reads the table as
additions. The table is therefore the queue between "the code has the member" and "the baseline has
the member"; an empty table means the two agree again.

| # | Package | Addition | Ruled | Why it is worth public surface |
|---|---|---|---|---|
| - | - | - | - | *(no pending entries: `v2026.11.0` released all seven - see below)* |

## Released by `v2026.11.0` (commit `e19304b`)

| Released entry | Addition | The baseline line that now carries it |
|---|---|---|
| one (R-1) | `M:MsgPackWriter.WriteString(System.ReadOnlySpan\`1<char>)` | `api/PicoMsgPack.public.txt:83` |
| two (R-2) | `M:MsgPackWriter.WriteDateTime(System.DateTime)` | `api/PicoMsgPack.public.txt:70` |
| three (R-2) | `T:PicoMsgPack.MsgPackDateTimeFormat` (+ `F:TimestampExt`, `F:IsoString`, `F:value__`) | `api/PicoMsgPack.public.txt:110`, `:8`, `:9`, `:10` |
| four (R-2) | `P:PicoMsgPack.MsgPackOptions.DateTimeFormat` | `api/PicoMsgPack.public.txt:89` |
| five (R-3) | `T:IFusedTokenReader` (+ `P:HasCountedCollections` and five `M:TryGet*`) | `api/PicoSerDe.Core.public.txt:192`, `:44`, `:149`, `:57-61` |
| six (R-3) | `T:TokenReaderFused` (+ four static `M:TryRead*`) | `api/PicoSerDe.Core.public.txt:182`, `:144-147` |
| seven (R-4) | `M:MsgPackWriter.WriteInt32Array(System.ReadOnlySpan\`1<int> values)` | `api/PicoMsgPack.public.txt:76` |

Those seven are the `+57` the release notes count, and the columns above add up to it: 8 lines for the
five `PicoMsgPack` entries (the `MsgPackDateTimeFormat` enum on four of them, its option on the fifth),
13 for `IFusedTokenReader` / `TokenReaderFused` in `PicoSerDe.Core`, 35 for the five readers listed
below, and 1 for `PicoJetson.TypeIo.WriteFormattable` - 8 + 13 + 35 + 1 = 57.

**What the `+57` does not cover.** The tool diffs the code against the baseline, so it can only see what
the baseline is still missing. Seventeen further lines entered the baselines between `v2026.10.0` and
this release *before* this ledger existed (`713c48a`, 2026-10-27) - `TextHelpers.EqLastSegment`,
`TextHelpers.ReadUnicodeEscape`, `IniReader.IsNullLiteral`, and the TOML/YAML round-trip additions of
`9c681f8`, `3870aca`, `f2a539d`, `b00a1dc`, `64f2f76`, `0a44f97`, `6a37ee3`, `e99c28a`, `b2085fd`.
They were in the baseline before the first dry run, so no delta ever named them and no row here ever
queued them. `git diff v2026.10.0 v2026.11.0 --numstat -- 'api/*.public.txt'` totals `74 / 0` against
the `57 / 0` apply committed; the difference is exactly those seventeen. Recorded rather than reverted:
the lines are correct and released, the accounting is what was missing.

## `PicoJetson.TypeIo.WriteFormattable` - ruled, never a pending entry

`M:PicoJetson.TypeIo.WriteFormattable``1(ref JsonWriter, T, ReadOnlySpan<char>)` rode in the
`v2026.11.0` delta with no row in the table above, which `docs/release-notes/v2026.11.0.md` §4 item 5
recorded as an open item. The ruling: **it needs no pending entry, because it shipped in the same
release that would have had to read one.**

- It came in with `b9546d6` (`perf(json): write temporals and numbers through the span, without the
  intermediate string`), an ancestor of the `v2026.11.0` tag.
- `api/PicoJetson.public.txt:169` carries it:
  `M:PicoJetson.TypeIo.WriteFormattable``1(ref PicoJetson.JsonWriter w, T value, System.ReadOnlySpan\`1<char> format) : void static`

The obligation this table exists for is "public surface the code has and the baseline does not yet".
For this member that window closed when apply refreshed the baseline, so the table can never carry it:
what was missing was the *record*, not the line, and this section is the record. The justification is
the one that carried entries 1, 2 and 7: generated serializers live in the consumer assembly, so a
write path the generator calls can only be reached through a public member.

## Registered capability change (not an API addition)

**Task 4d normalises `DateTimeKind` to `Utc` on a msgpack round trip.** The timestamp extension
carries seconds and nanoseconds - an instant - and has no field for a kind, so `Unspecified` and
`Local` values come back as `Utc`; `Local` is converted to UTC first, so no instant moves. Ticks
are preserved exactly.

This is inherent to the encoding, not to our reader: MessagePack-CSharp reads the same bytes the
same way, which the interop tests assert tick for tick. The probe records it honestly - cells
S2 and S4 and observations S14 and S15 move `OK` to `MISMATCH`, with spec section 3 stating why -
rather than papering over a real behaviour change. The alternative (keep writing ISO strings)
would forfeit the 65% deserialize gain and the interoperability, and would leave the format the
only one in the library that cannot talk to its own ecosystem.

A capability change has no member to put in a baseline, so this section is permanent: it is the only
record of the semantics `v2026.11.0` changed, and no later dry run can rediscover it.

## Readers that implement `IFusedTokenReader`

Entry #5 was the interface; it does not say who implements it, and each implementation put its own
lines into the `v2026.11.0` delta. **Every format reader in the tree implements it** - five readers
across four packages, on top of the contract itself in `PicoSerDe.Core`:

| Reader | Declaration | Lines it added to that delta |
|---|---|--:|
| `PicoJetson.JsonReader` | `PicoJetson/src/PicoJetson/JsonReader.cs:27` | 7 (`I:` + `HasCountedCollections` + the five `TryGet*`) |
| `PicoIni.IniReader` | `PicoIni/src/PicoIni/IniReader.cs:15` | 7 |
| `PicoToml.TomlReader` | `PicoToml/src/PicoToml/TomlReader.cs:20` | 7 |
| `PicoYaml.YamlReader` | `PicoYaml/src/PicoYaml/YamlReader.cs:19` | 7 |
| `PicoMsgPack.MsgPackReader` | `PicoMsgPack/src/PicoMsgPack/MsgPackReader.cs:13` | 7 |

What each one does with the members: the four text formats declare
`HasCountedCollections => false` (`IniReader.cs:459`, `TomlReader.cs:603`, `YamlReader.cs:717`,
`JsonReader.cs:758`) and forward every fused read to `TokenReaderFused` whenever their own fast path
does not serve the token - JSON's `TryReadInt32` is the one that carries a real fused body
(`JsonReader.cs:792-797`). `MsgPackReader` is the one answering `HasCountedCollections` with `true`
(`MsgPackReader.cs:1232`), because its wire header carries the element count.

Which of these members the **generator** then calls is a separate question, and not an API question:
it is the table in `docs/fused-emission-registry.md`, enforced by `FusedMemberEmissionGuardTests`.

## Notes

- Additions are additive only. No existing signature changed, so no consumer breaks; the release
  script treats `-0` as the safe case and bumps accordingly.
- The packages affected by that cycle were **six**, not two: `PicoSerDe.Core` and `PicoMsgPack` (the
  two packages with rulings, entries 5-7 and `R-4` below) plus `PicoJetson`, `PicoIni`, `PicoToml` and
  `PicoYaml`, whose readers also implement `IFusedTokenReader` (see "Readers that implement
  `IFusedTokenReader`" above). `-DryRun` on `v2026.10.0 -> v2026.11.0` measured `+57 / -0` across those
  six; the `.Gen` packages are unchanged.
- R-3 is additive in the plain sense: a new interface and a new static class. No existing member
  changed signature, and `ITokenReader` itself is untouched, so readers that do not want the fused
  path never see it.

### R-4: `MsgPackWriter.WriteInt32Array`

Generated serializers live in the consumer assembly, so a bulk write path can only reach them through a
public member - the same reason `WriteString(ReadOnlySpan<char>)` and `WriteDateTime` were accepted.
The alternatives were measured rather than waved away: keeping the per-element call costs 2.1 ns per
element on the Collection row against 0.57 ns for the competitor, and no existing public member writes
a length-prefixed run of values. Output is byte-identical to the element loop because non-fixint values
still go through `WriteInt32`. While it was pending, `api/*.public.txt` stayed untouched, as the note
above requires; `v2026.11.0` then wrote it into the baseline along with the rest of the release.
