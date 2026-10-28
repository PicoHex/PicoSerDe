#!/usr/bin/env python3
"""Mechanically derive `ReadSpan` from `ReadSequential` (Task 4e maintenance tool).

`MsgPackReader.Read()` dispatches to one of two tokenizers:

  * ``ReadSpan()``       -- span mode (``byte[]`` / ``ReadOnlySpan<byte>``
    overloads, i.e. every non-streaming deserialize): reads ``_data[_position]``
    directly and advances ``_position`` in place;
  * ``ReadSequential()`` -- sequence mode (streaming): the original state
    machine, moved over verbatim, one helper call per byte access
    (``IsAtEnd`` / ``PeekByte`` / ``AdvanceByte`` / ``ReadBytes`` / ...).

``ReadSpan`` is *not* a hand-written twin. It is ``ReadSequential`` with the
fixed, listed set of textual substitutions below applied -- nothing else. That
is the whole argument for keeping two ~200-line copies of the tokenizer in the
tree: the second copy cannot quietly acquire semantics the first one does not
have, because it is generated.

This file is the **generator**. ``verify_readspan.py`` is the checker, and the
TUnit test ``ReadSpan_IsDerivedFromReadSequential_andHasNotDrifted`` re-runs the
same rules in C# on every ``dotnet test`` (it embeds the substitution list, so
CI catches drift even when nobody runs Python).

Neither script takes part in the build: they are maintenance tools.

Usage (any working directory; paths are resolved relative to the repository)

    python PicoMsgPack/tools/readspan/derive_readspan.py           # print
    python PicoMsgPack/tools/readspan/derive_readspan.py --check   # 1 if drifted
    python PicoMsgPack/tools/readspan/derive_readspan.py --write   # regenerate

Requires Python 3.8+ and nothing outside the standard library.
"""

import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[3]
SOURCE = REPO_ROOT / "PicoMsgPack" / "src" / "PicoMsgPack" / "MsgPackReader.cs"

SEQUENTIAL_SIGNATURE = "private bool ReadSequential()"
SPAN_SIGNATURE = "private bool ReadSpan()"

#: The one line every provenance comment starts with. The checker -- and the
#: C# guard -- treat this line plus the ``//`` lines directly below it as
#: documentation rather than as part of the derived body, so rewording the
#: comment never looks like drift.
PROVENANCE_MARKER = "Mechanically derived from the original Read() body by"

PROVENANCE = [
    "        // " + PROVENANCE_MARKER,
    "        // PicoMsgPack/tools/readspan/derive_readspan.py -- do not edit by",
    "        // hand: regenerate it (same input, same output, `--write` is",
    "        // idempotent). Every byte access is a direct index into _data (no",
    "        // IsAtEnd / PeekByte / AdvanceByte / ReadBytes mode branch). Token",
    "        // types, _valueSpan contents, _tag, _depth accounting, BytesConsumed",
    "        // and every exception (type, order and message) are unchanged.",
]

#: (text in ReadSequential, text in ReadSpan, required occurrence count or None)
#:
#: Order matters only where one pattern is a prefix of another; the sequence
#: below is the order the generator applies.
SUBSTITUTIONS = [
    # 1. The trailing-garbage verdict is span-mode only: the `!_isSequence`
    #    conjunct was how the original body selected span behaviour, and in
    #    span mode it is always false, so it disappears. The throw itself moves
    #    out of line (`TrailingGarbage`) so the hot frame carries no
    #    interpolated-string buffer: same exception type, same message.
    (
        """            if (_depth == 0 && !_isSequence && _position < _data.Length)
                throw new FormatException(
                    $"Unexpected data after the document at offset {_position}"
                );
""",
        """            if (_depth == 0 && _position < _data.Length)
                throw TrailingGarbage(_position);
""",
        1,
    ),
    # 2. End-of-buffer test: `IsAtEnd()` is exactly `_position >= _data.Length`
    #    for a span-mode buffer.
    ("if (IsAtEnd())", "if (_position >= _data.Length)", 1),
    # 3. The streaming partial-token guard is gated on `!_isFinalBlock &&
    #    _isSequence`, i.e. unreachable in span mode.
    (
        """        // Streaming sequence mode: refuse to tokenize a partial value whose
        // payload bytes have not fully arrived yet.
        if (!_isFinalBlock && _isSequence)
        {
            int tokenLength = PeekTokenLength();
            if (tokenLength < 0 || _seqReader.Remaining < tokenLength)
            {
                _needsMoreData = true;
                return false;
            }
        }

""",
        "",
        1,
    ),
    # 4. Tag byte. The buffer was already checked to be non-empty, so a postfix
    #    increment cannot throw before it mutates state: read and advance are
    #    two statements, in that order, exactly like PeekByte()/AdvanceByte().
    (
        """        var b = PeekByte();
        AdvanceByte(); // consume tag byte
""",
        """        var b = _data[_position];
        _position++; // consume tag byte
""",
        1,
    ),
    # 5. Extension tag byte: read, then advance (two statements, so a truncated
    #    fixext still throws from the read *before* `_position` moves).
    #    Count is intentionally not pinned to today's 8 fixext/ext arms: a new
    #    extension arm written with the same two-line shape is covered without
    #    touching this list. The count is still checked to be non-zero.
    (
        """                        _tag = PeekByte();
                        AdvanceByte();
""",
        """                        _tag = _data[_position];
                        _position++;
""",
        None,
    ),
    # 6. Unknown tag byte: the throw moves out of line (`UnknownByte`) for the
    #    same frame-size reason as (1); same exception type, same message.
    (
        """                        throw new FormatException(
                            $"Unknown MsgPack byte 0x{b:X2} at offset {BytesConsumed}"
                        );
""",
        """                        throw UnknownByte(b, _position);
""",
        1,
    ),
]

#: Payload helpers -> their span-mode twins (applied to every occurrence).
HELPER_RENAMES = [
    ("ReadBytes(", "ReadBytesSpan("),
    ("ReadByteLen(", "ReadByteLenSpan("),
    ("ReadString(", "ReadStringSpan("),
]


def read_source(path=SOURCE):
    """Return (text with '\\n' line endings, the file's own line ending)."""
    raw = Path(path).read_bytes()
    newline = "\r\n" if b"\r\n" in raw else "\n"
    return raw.decode("utf-8-sig").replace("\r\n", "\n"), newline


def write_source(text, newline, path=SOURCE):
    """Write `text` back with the file's own line ending (LF or CRLF)."""
    data = text.replace("\n", newline).encode("utf-8")
    Path(path).write_bytes(data)


def method_body(text, signature):
    """The `signature` line through its closing `    }` line, inclusive."""
    lines = text.split("\n")
    starts = [i for i, line in enumerate(lines) if line.strip() == signature]
    if len(starts) != 1:
        sys.exit("expected exactly one %r line, found %d" % (signature, len(starts)))
    end = starts[0] + 1
    while end < len(lines) and lines[end] != "    }":
        end += 1
    if end == len(lines):
        sys.exit("no closing '    }' for %r" % signature)
    return "\n".join(lines[starts[0] : end + 1])


def substitute(text, old, new, count, label):
    found = text.count(old)
    if found == 0:
        sys.exit("derivation rule %s matched nothing; it is %r" % (label, old[:70]))
    if count is not None and found != count:
        sys.exit(
            "derivation rule %s expected %d occurrence(s), found %d: %r"
            % (label, count, found, old[:70])
        )
    return text.replace(old, new)


def strip_provenance(body):
    """Drop the provenance comment block, leaving the derived body."""
    lines = body.split("\n")
    for i, line in enumerate(lines):
        if PROVENANCE_MARKER in line:
            end = i + 1
            while end < len(lines) and lines[end].lstrip().startswith("//"):
                end += 1
            return "\n".join(lines[:i] + lines[end:]), True
    return body, False


def derive(sequential_body, with_provenance=True):
    """Apply the listed substitutions to a `ReadSequential` body."""
    body = sequential_body
    if not body.startswith("    " + SEQUENTIAL_SIGNATURE):
        sys.exit("body does not start with the ReadSequential signature")

    body = body.replace(
        "    " + SEQUENTIAL_SIGNATURE, "    " + SPAN_SIGNATURE, 1
    )
    for index, (old, new, count) in enumerate(SUBSTITUTIONS, start=1):
        body = substitute(body, old, new, count, "#%d" % index)
    for old, new in HELPER_RENAMES:
        body = body.replace(old, new)

    if with_provenance:
        anchor = "    " + SPAN_SIGNATURE + "\n    {\n"
        body = substitute(body, anchor, anchor + "\n".join(PROVENANCE) + "\n", 1, "provenance")
    return body


def main(argv):
    args = set(argv[1:])
    unknown = args - {"--check", "--write", "--help", "-h"}
    if unknown:
        sys.exit("unknown argument(s): %s" % " ".join(sorted(unknown)))
    if args & {"--help", "-h"}:
        print(__doc__)
        return 0

    text, newline = read_source()
    sequential = method_body(text, SEQUENTIAL_SIGNATURE)
    current = method_body(text, SPAN_SIGNATURE)
    current_body, has_provenance = strip_provenance(current)
    derived_body, _ = strip_provenance(derive(sequential))
    drifted = derived_body != current_body

    if "--check" in args:
        if drifted:
            import difflib

            print(
                "\n".join(
                    difflib.unified_diff(
                        current_body.split("\n"),
                        derived_body.split("\n"),
                        "MsgPackReader.cs:ReadSpan",
                        "derived from ReadSequential",
                        lineterm="",
                    )
                )
            )
            print("\nReadSpan has drifted from its derivation (see diff above).")
            return 1
        print("ReadSpan is exactly the mechanical derivation of ReadSequential.")
        return 0

    if "--write" in args:
        if not drifted:
            print("nothing to do: ReadSpan already matches the derivation.")
            return 0
        if not has_provenance:
            sys.exit("the in-file ReadSpan has no provenance comment; refusing to rewrite")
        rewritten = text.replace(current, derive(sequential), 1)
        if rewritten == text:
            sys.exit("could not splice the regenerated body back into the file")
        write_source(rewritten, newline)
        print("rewrote " + str(SOURCE.relative_to(REPO_ROOT)))
        return 0

    print(derive(sequential, with_provenance=has_provenance))
    if drifted:
        print("\n# NOTE: the in-file ReadSpan differs from this derivation.", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
