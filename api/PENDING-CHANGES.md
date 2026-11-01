# Pending public API additions

**Status: empty as of `v2026.11.0`.** That release read all seven entries this table carried into the
committed baselines (`e19304b`, `chore(release): v2026.11.0`: `+57`, six files, `-0`), so nothing is
waiting for the next release. Where each one landed is recorded under "Released by `v2026.11.0`" below,
pinned to the baseline **entry** that carries it, so the handover can be checked without trusting this file
(grep the entry named in the Addition column of a row inside the archive named in its third column).

**Nothing in this file points at code with a line number.** A pointer into a source file is a member name
(`JsonReader.TryReadInt32`), and a pointer into a baseline is the baseline entry itself. A line number here is
the one kind of evidence that goes stale with nothing turning red: `JsonReader.cs` grew by 33 lines in one
commit (`a5c0dfb`) and invalidated every line reference to it here and in `docs/fused-emission-registry.md` -
seven of them (six spelled `<file>.cs:<line>` and one written as a bare `（:NNN）` shorthand after the file
had already been named), and no test failed. `DocPointerGuardTests` now rejects `<file>.cs:<line>` and
`<file>.public.txt:<line>` in both ledgers.

**Removals are recorded here too, since `v2026.12.0`.** This file was written for additions, and an
addition can wait for a release because nothing breaks while it waits. A **removal** cannot wait quietly: a
consumer's build goes red the moment the type is gone, and no `+N` in a later delta explains it. So a removal
is registered as soon as the code loses the member - see "Pending public API removals" at the end of this
file, which carries the five enums the C9 unification deleted.

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

| Released entry | Addition | The baseline that now carries it |
|---|---|---|
| one (R-1) | `M:MsgPackWriter.WriteString(System.ReadOnlySpan\`1<char>)` | `api/PicoMsgPack.public.txt` |
| two (R-2) | `M:MsgPackWriter.WriteDateTime(System.DateTime)` | `api/PicoMsgPack.public.txt` |
| three (R-2) | `T:PicoMsgPack.MsgPackDateTimeFormat` (+ `F:TimestampExt`, `F:IsoString`, `F:value__`) | `api/PicoMsgPack.public.txt` |
| four (R-2) | `P:PicoMsgPack.MsgPackOptions.DateTimeFormat` | `api/PicoMsgPack.public.txt` |
| five (R-3) | `T:IFusedTokenReader` (+ `P:HasCountedCollections` and five `M:TryGet*`) | `api/PicoSerDe.Core.public.txt` |
| six (R-3) | `T:TokenReaderFused` (+ four static `M:TryRead*`) | `api/PicoSerDe.Core.public.txt` |
| seven (R-4) | `M:MsgPackWriter.WriteInt32Array(System.ReadOnlySpan\`1<int> values)` | `api/PicoMsgPack.public.txt` |

The third column names the archive; what to look for in it is the entry the Addition column spells, so
`grep -n 'M:MsgPackWriter.WriteString' api/PicoMsgPack.public.txt` proves the handover in one command. It
used to carry that archive's line number instead, which the next baseline refresh (`-Push` rewrites all six
archives wholesale) would have moved under it - the same rot that hit the `JsonReader.cs` pointers.

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
- `api/PicoJetson.public.txt` carries it, spelled exactly like this:
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
| `PicoJetson.JsonReader` | `PicoJetson/src/PicoJetson/JsonReader.cs` - `public ref struct JsonReader` | 7 (`I:` + `HasCountedCollections` + the five `TryGet*`) |
| `PicoIni.IniReader` | `PicoIni/src/PicoIni/IniReader.cs` - `public ref struct IniReader` | 7 |
| `PicoToml.TomlReader` | `PicoToml/src/PicoToml/TomlReader.cs` - `public ref struct TomlReader` | 7 |
| `PicoYaml.YamlReader` | `PicoYaml/src/PicoYaml/YamlReader.cs` - `public ref struct YamlReader` | 7 |
| `PicoMsgPack.MsgPackReader` | `PicoMsgPack/src/PicoMsgPack/MsgPackReader.cs` - `public ref struct MsgPackReader` | 7 |

The `Declaration` column is a member/type declaration, not a line: `grep -n 'public ref struct JsonReader'
PicoJetson/src/PicoJetson/JsonReader.cs` finds it. These five rows were still on the right line when the
review measured them (the five declarations sit above the insert); what `a5c0dfb` moved by 33 lines are the
two `JsonReader.cs` references in the paragraph below. A line is the one part of this table that can go
stale while the table still reads right, so the locator is the declaration itself.

What each one does with the members: the four text formats declare `HasCountedCollections => false` (each
reader's own member of that name: `IniReader.HasCountedCollections`, `TomlReader.HasCountedCollections`,
`YamlReader.HasCountedCollections`, `JsonReader.HasCountedCollections`) and forward every fused read to
`TokenReaderFused` whenever their own fast path does not serve the token - JSON's `TryReadInt32` is the one
that carries a real fused body (`JsonReader.TryReadInt32`, plus its fused half `TryReadInt32Span`).
`MsgPackReader` is the one answering `HasCountedCollections` with `true`
(`MsgPackReader.HasCountedCollections`), because its wire header carries the element count.

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

## Pending public API removals - `v2026.12.0`

**One change, five types, source-breaking.** The C9 unification deleted the per-format ignore-condition
enums and pointed every `DefaultIgnoreCondition` property at the suite-wide
`PicoSerDe.Core.PicoIgnoreCondition` that already existed for `[PicoIgnore(Condition = ...)]`. Five public
types disappear and five properties change type in the same release, so this is an **x bump**
(`v2026.11.x` -> `v2026.12.0`), not a `y`.

### What goes, entry by entry

| # | Package | Removed baseline entries | The one that replaces them |
|---|---|---|---|
| R-1 | `PicoJetson` | `T:enum PicoJetson.JsonIgnoreCondition` + its four `F:` rows (`Never`, `WhenWritingNull`, `WhenWritingDefault`, `value__`) | `T:enum PicoSerDe.Core.PicoIgnoreCondition` |
| R-2 | `PicoMsgPack` | `T:enum PicoMsgPack.MsgPackIgnoreCondition` + three `F:` rows (`Never`, `WhenWritingNull`, `value__`) | same |
| R-3 | `PicoIni` | `T:enum PicoIni.IniIgnoreCondition` + three `F:` rows (`Never`, `WhenWritingNull`, `value__`) | same |
| R-4 | `PicoToml` | `T:enum PicoToml.TomlIgnoreCondition` + three `F:` rows (`Never`, `WhenWritingNull`, `value__`) | same |
| R-5 | `PicoYaml` | `T:enum PicoYaml.YamlIgnoreCondition` + three `F:` rows (`Never`, `WhenWritingNull`, `value__`) | same |

Five properties change type rather than disappear, so each shows up in the delta as one removed line and one
added line:

| Package | The property |
|---|---|
| `PicoJetson` | `JsonOptions.DefaultIgnoreCondition`, `JsonIgnoreCondition` -> `PicoIgnoreCondition` |
| `PicoMsgPack` | `MsgPackOptions.DefaultIgnoreCondition`, `MsgPackIgnoreCondition` -> `PicoIgnoreCondition` |
| `PicoIni` | `IniOptions.DefaultIgnoreCondition`, `IniIgnoreCondition` -> `PicoIgnoreCondition` |
| `PicoToml` | `TomlOptions.DefaultIgnoreCondition`, `TomlIgnoreCondition` -> `PicoIgnoreCondition` |
| `PicoYaml` | `YamlOptions.DefaultIgnoreCondition`, `YamlIgnoreCondition` -> `PicoIgnoreCondition` |

Expected delta: **`+5 / -26`** over the five format archives (`-21` enum rows, `-5`/`+5` for the retyped
properties); `api/PicoSerDe.Core.public.txt` is untouched.

### What the rename costs, beyond the rename

Every row above is "old name -> new name", and that is only half of the migration. Two things a consumer
hits the moment the package is upgraded, neither of which a rename table can show:

| # | The trap | The fix |
|---|---|---|
| M-1 | **`PicoIgnoreCondition` lives in `PicoSerDe.Core`, not in the format package.** The old name came into scope with the `using PicoJetson;` (and friends) every consumer already had; the new one does not, so a file that only swapped the type name fails with `CS0103: The name 'PicoIgnoreCondition' does not exist in the current context`. PicoSerDe's own sample is the proof rather than the anecdote: this change added `global using PicoSerDe.Core;` next to the type swap in the sample's global usings file. | Add `using PicoSerDe.Core;` - every consumer already references that package, which is where the enum has lived since `v2026.11.0` - or spell it out as `PicoSerDe.Core.PicoIgnoreCondition`. The `using` is the option that survives copy-paste from the old code. |
| M-2 | **The integer values moved under unchanged names.** Anything that stored the enum as a *number* - a config file, a database column, a `(int)` cast, a numeric comparison - flips meaning silently instead of failing to compile. The bullet below proves the library never reads these numbers; that is exactly why nothing will tell you the stored value now means something else. | Re-map the stored numbers (table below) or, better, stop storing numbers: persist the member *name* and read it back with `Enum.Parse<PicoIgnoreCondition>("WhenWritingNull")`. No member was renamed, so every stored name still parses. |

What a stored number now means:

| Stored | `v2026.11.0` | `v2026.12.0` |
|--:|---|---|
| `0` | `Never` (write everything) | `Always` - at options level still writes everything, but as an attribute condition it now **strips** the member, because that is what `Always` has always meant there |
| `1` | `WhenWritingNull` | `Never` - null members are written again |
| `2` | `WhenWritingDefault` (JSON only; the other four had no such member) | `WhenWritingNull` - null is skipped, `default(T)` is written |
| `3` | did not exist | `WhenWritingDefault` |

### Why it is safe to merge into one enum

- **Same member names, same meanings.** `Never` = never skip, `WhenWritingNull` = skip when null,
  `WhenWritingDefault` = skip when null or `default(T)`. Only the integer values move (`Never` 0 -> 1,
  `WhenWritingNull` 1 -> 2, `WhenWritingDefault` 2 -> 3, and `Always = 0` is new to these five classes).
  Nothing in the runtime or the generators compares the numeric value: every generated guard compares
  **named constants**, and no library code reads `DefaultIgnoreCondition` at all - it is consumed by emitted
  code only.
- **Wire output is unchanged.** A harness ran the same model (null member, default-value member, normal
  member, null nullable, nested object, nullable list) through all five formats at every setting, before and
  after: the ten pre-existing settings produce byte-identical payloads, and the settings the unification
  newly exposes (`WhenWritingDefault` on INI/TOML/YAML/MsgPack) produce exactly the `Never` bytes, because
  those four formats never consulted the value. JSON is the only format that honors
  `WhenWritingDefault`, and it honored it before too.
- **Two values are accepted-but-inert as a global default**, and that is the one behavioral sharp edge of
  the unification, recorded here instead of discovered later: `PicoIgnoreCondition.Always` means "strip this
  property everywhere" *at attribute level* and has no global meaning, so setting it as a
  `DefaultIgnoreCondition` behaves like `Never` (write everything); `WhenWritingNull`/`WhenWritingDefault`
  remain inert in TOML/INI/YAML, and `WhenWritingDefault` inert in MsgPack, exactly as their per-format
  enums never offered it.

### Checking this without trusting this file

Each removed entry is still in the last released baseline, so one `grep` per row proves the removal is real
and has not been applied yet:

```
grep -c 'IgnoreCondition' api/PicoJetson.public.txt api/PicoMsgPack.public.txt api/PicoIni.public.txt api/PicoToml.public.txt api/PicoYaml.public.txt
```

Before the release that answers `6 5 5 5 5` (Jetson carries one extra `F:` row: it also had
`WhenWritingDefault`); the release commit rewrites the five archives and the same
`grep` answers `1 1 1 1 1` - the surviving count is the retyped `P:` row, which now spells
`PicoSerDe.Core.PicoIgnoreCondition`. The handover is complete when every `T:enum` row for one of the five
names is gone from `api/`, and `T:enum PicoSerDe.Core.PicoIgnoreCondition` is still there.
