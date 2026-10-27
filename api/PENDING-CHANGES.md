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

## Notes

- Additions are additive only. No existing signature changed, so no consumer breaks; the release
  script treats `-0` as the safe case and bumps accordingly.
- `PicoMsgPack` is the only package affected by this cycle so far.
