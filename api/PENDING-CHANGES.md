# Pending public API additions

`api/*.public.txt` records **the last released surface**. It is deliberately not edited between
releases: the release script diffs the freshly dumped surface against it and reports the delta
(`+N / -M`), which is what drives the version bump. Editing a baseline by hand to match new code
would forge that diff, so a change that adds public surface leaves `api/` untouched and is listed
here instead.

Every entry below has been reviewed and ruled on. The next release will read them as additions.

| # | Package | Addition | Ruled | Why it is worth public surface |
|---|---|---|---|---|
| 1 | PicoMsgPack | `M:MsgPackWriter.WriteString(System.ReadOnlySpan\`1<char>)` | R-1 | Without it every string property allocates a `byte[]` (`Encoding.UTF8.GetBytes`): 600 B/op for a fifteen-string model. Two alternatives were measured — stack encoding, and handing off through `Buffer` — both zero-gain or slower. The JSON writer has exposed the same overload all along (`PicoJetson.JsonWriter.WriteString(ReadOnlySpan<char>)`), so this removes an asymmetry between formats rather than adding a concept. |
| 2 | PicoMsgPack | `M:MsgPackWriter.WriteDateTime(System.DateTime)` | R-2 | Writing a `DateTime` as the spec's timestamp extension (tag `0xFF`) needs a writer entry point; the generated code calls it. MessagePack-CSharp produces byte-identical output for the same value, so this is the interoperable encoding rather than a private one. |
| 3 | PicoMsgPack | `T:PicoMsgPack.MsgPackDateTimeFormat` (+ `F:TimestampExt`, `F:IsoString`, `F:value__`) | R-2 | The enum makes the wire-format choice explicit instead of silent. `TimestampExt` is the spec-conformant, MessagePack-CSharp-interoperable encoding; `IsoString` keeps the old behaviour selectable for writers that must stay byte-compatible with payloads produced before this release. |
| 4 | PicoMsgPack | `P:PicoMsgPack.MsgPackOptions.DateTimeFormat` | R-2 | The setting that carries #3. Default is `TimestampExt`; a consumer that needs the pre-change encoding can opt in without giving up the rest of the release. |

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

| 5 | PicoSerDe.Core | `T:IFusedTokenReader` (+ `P:HasCountedCollections`, `M:TryGetCollectionCount`, `M:TryReadInt32`, `M:TryReadInt64`, `M:TryReadFloat64`, `M:TryReadBool`) | R-3 | The token contract asks for two steps per value - `Read()` classifies the byte and `TryGetX` decodes it - which is measurable: 13.4 ns per token against the competitor's 6.8 on the same 107-token payload. A derived interface lets a format fuse the two steps where its wire allows it. The members are abstract rather than defaulted because a `ref struct` must implement every interface member itself (CS9245, measured), and because an override is only trustworthy when the thing it must equal is written down somewhere. |
| 6 | PicoSerDe.Core | `T:TokenReaderFused` (+ the same six `M:` methods, static) | R-3 | The reference those overrides must equal: the literal composition of `Read()` plus `TryGetX()`, existing exactly once. Readers that do not optimize a member forward to it, so "the fast path is equivalent to the slow path" is testable against a single source of truth rather than against each implementer's memory of one. |
| 7 | PicoMsgPack | `M:MsgPackWriter.WriteInt32Array(System.ReadOnlySpan\`1<int> values)` | **R-4** | A flat `List<int>` / `int[]` row is the one place where the per-element *call* is the whole cost, not the encoding: measured on the Collection row, 100 elements cost 2.1 ns each through `WriteInt32` (inlined by dynamic PGO, so this is not a call-overhead story any more) against 0.57 ns for MessagePack-CSharp's equivalent loop, and 0.5 ns for the same encoding run as a tight loop inside this type. Writing the run in one call removes the per-element `Ensure` and the per-element method boundary while emitting **byte-identical** output - the non-fixint values are still handed to `WriteInt32` itself, so no value takes an encoding the loop would not have chosen. `api/*.public.txt` is untouched, as the note above requires. |

## Notes

- Additions are additive only. No existing signature changed, so no consumer breaks; the release
  script treats `-0` as the safe case and bumps accordingly.
- `PicoMsgPack` and `PicoSerDe.Core` are the packages affected by this cycle so far.
- R-3 is additive in the plain sense: a new interface and a new static class. No existing member
  changed signature, and `ITokenReader` itself is untouched, so readers that do not want the fused
  path never see it.

### R-4: `MsgPackWriter.WriteInt32Array`

Generated serializers live in the consumer assembly, so a bulk write path can only reach them through a
public member - the same reason `WriteString(ReadOnlySpan<char>)` and `WriteDateTime` were accepted.
The alternatives were measured rather than waved away: keeping the per-element call costs 2.1 ns per
element on the Collection row against 0.57 ns for the competitor, and no existing public member writes
a length-prefixed run of values. Output is byte-identical to the element loop because non-fixint values
still go through `WriteInt32`. `api/*.public.txt` stays untouched, as the note above requires.
