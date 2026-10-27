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
| 2 | PicoMsgPack | `T:PicoMsgPack.MsgPackDateTimeFormat` | R-2 | The enum makes the wire-format choice explicit instead of silent. `TimestampExt` is the spec-conformant, MessagePack-CSharp-interoperable encoding; `IsoString` keeps the old behaviour selectable for writers that must stay byte-compatible with payloads produced before this release. |
| 3 | PicoMsgPack | `P:PicoMsgPack.MsgPackOptions.DateTimeFormat` | R-2 | The setting that carries #2. Default is `TimestampExt`; a consumer that needs the pre-change encoding can opt in without giving up the rest of the release. |

## Notes

- Additions are additive only. No existing signature changed, so no consumer breaks; the release
  script treats `-0` as the safe case and bumps accordingly.
- `PicoMsgPack` is the only package affected by this cycle so far.
