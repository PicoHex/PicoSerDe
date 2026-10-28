# `ReadSpan` derivation tools

Maintenance tools for the two tokenizers in
[`MsgPackReader.cs`](../../src/PicoMsgPack/MsgPackReader.cs). **They are not part
of the build and nothing in `src/` depends on them.**

## Why this directory exists

`MsgPackReader.Read()` dispatches to one of two ~200-line state machines:

| method | mode | byte access |
|---|---|---|
| `ReadSpan()` | span (`byte[]` / `ReadOnlySpan<byte>` — every non-streaming deserialize) | `_data[_position]` directly, `_position++` |
| `ReadSequential()` | sequence (`ReadOnlySequence<byte>` — streaming) | `IsAtEnd()` / `PeekByte()` / `AdvanceByte()` / `ReadBytes(...)` |

The span twin exists because the mode branch inside every byte access costs
8–13% of deserialization time in the msgpack benchmarks (see the Task 4e
report), and **`ReadSpan` is not hand-written**: it is `ReadSequential` with a
fixed, listed set of textual substitutions applied. Those tools are the whole
argument for tolerating a second copy of the state machine in the tree.

The rule list lives in [`derive_readspan.py`](derive_readspan.py) as
`SUBSTITUTIONS` + `HELPER_RENAMES`:

1. the trailing-garbage verdict loses its `!_isSequence` conjunct and its throw
   moves out of line (`TrailingGarbage`);
2. `if (IsAtEnd())` → `if (_position >= _data.Length)`;
3. the streaming partial-token guard (`!_isFinalBlock && _isSequence`) is
   dropped — unreachable in span mode;
4. `var b = PeekByte(); AdvanceByte();` → `var b = _data[_position]; _position++;`;
5. the same for the extension tag byte (`_tag = PeekByte(); AdvanceByte();`);
6. `throw new FormatException($"Unknown MsgPack byte …")` → `throw UnknownByte(b, _position)`;
7. `ReadBytes( / ReadByteLen( / ReadString(` → their `…Span(` twins.

Anything else that differs between the two bodies is drift.

## `derive_readspan.py` — the generator

Reads the `ReadSequential` body out of `MsgPackReader.cs`, applies the rules and
prints the `ReadSpan` body it should have.

```console
python PicoMsgPack/tools/readspan/derive_readspan.py            # print to stdout
python PicoMsgPack/tools/readspan/derive_readspan.py --check    # gated check, exit 1 on drift
python PicoMsgPack/tools/readspan/derive_readspan.py --write    # regenerate in place
```

`--check` prints a unified diff (`in-file ReadSpan` → `derived`) and exits 1 when
the file has drifted. `--write` splices the regenerated body in, keeping the
file's own line ending (LF in the index, CRLF in a `core.autocrlf=true` working
tree) and the provenance comment; it is idempotent. Each rule asserts that it
matched something (and, where the count is pinned, how many times), so a
rewritten `ReadSequential` reports *which* rule stopped applying instead of
silently producing a half-derived body.

## `verify_readspan.py` — the checker

```console
python PicoMsgPack/tools/readspan/verify_readspan.py
python PicoMsgPack/tools/readspan/verify_readspan.py --base d45edca^
```

- **derivation** (always): `derive(ReadSequential) == ReadSpan`, provenance
  comment excluded. The rules are *imported* from `derive_readspan.py`, so
  generator and checker cannot drift apart.
- **history** (only with `--base <rev>`, needs `git`): `ReadSequential` equals
  the `Read()` body at `<rev>`, byte for byte apart from the signature line —
  the auditable form of "the sequence path was moved, not rewritten".
  `d45edca^` (= `7faeed4`) is the last revision whose `Read()` was the original
  state machine.

Exit code 0 means no drift; 1 means drift, with a diff.

## How to add a token type

1. Add the `case` arm to **`ReadSequential` only**, using the existing shapes
   (`PeekByte()`/`AdvanceByte()` for the tag byte, `_tag = PeekByte();
   AdvanceByte();` for an extension tag, `ReadBytes(…)`/`ReadByteLen(…)`/
   `ReadString(…)` for payload).
2. `python PicoMsgPack/tools/readspan/derive_readspan.py --write`
3. Add the payload to `MsgPackSpanTokenDump.Payloads()` and the family to the
   coverage checklist at the top of
   `MsgPackReaderSpanSequenceEquivalenceTests.cs`.
4. `dotnet test` — the equivalence matrix and the derivation guard both run.

## Dependencies

- Python **3.8+**, standard library only (`pathlib`, `difflib`, `subprocess`).
- `git` only for `verify_readspan.py --base`.
- Paths resolve from `__file__` (repo root is three levels up), so the scripts
  can be run from any working directory.

## The permanent guard

Python is not assumed to be installed on a contributor's machine or in CI, so
the same derivation rules are re-implemented in C# and asserted on every
`dotnet test` by
`PicoMsgPack.Tests/MsgPackReaderReadSpanDerivationTests.cs`
(`ReadSpan_IsDerivedFromReadSequential_andHasNotDrifted`). If you change the
rule list here, change it there too — that duplication is deliberate: the two
implementations cross-check each other.
