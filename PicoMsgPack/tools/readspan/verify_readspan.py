#!/usr/bin/env python3
"""Check that `ReadSpan` is still the mechanical derivation of `ReadSequential`.

`ReadSpan` in ``MsgPackReader.cs`` is generated from the ``ReadSequential`` body
by ``derive_readspan.py`` (see that file for the rule list). This checker proves
the claim still holds, and optionally that the input side of it -- i.e. that
``ReadSequential`` really is the pre-Task-4e ``Read()`` body, moved and not
rewritten -- has not been edited either.

Two checks:

``derivation`` (always)
    ``derive(ReadSequential body) == ReadSpan body``. The derivation rules are
    *imported* from ``derive_readspan.py``, so generator and checker cannot
    drift apart; the provenance comment is excluded on both sides, so rewording
    it never counts as drift.

``history`` (``--base <rev>``, needs git)
    ``ReadSequential body == Read() body at <rev>``, byte for byte apart from
    the signature line. This is the auditable form of "the sequence path was
    moved, not touched". ``d45edca^`` is the commit Task 4e was built on
    (``7faeed4``), i.e. the last revision whose ``Read()`` was the original
    state machine; any older revision works the same way.

The same derivation is re-implemented in C# and re-run on every ``dotnet test``
by ``ReadSpan_IsDerivedFromReadSequential_andHasNotDrifted``, so CI catches
drift without Python being installed. This script is the printable, git-aware
version of that check, and does not take part in the build.

Usage (any working directory; paths are resolved relative to the repository)

    python PicoMsgPack/tools/readspan/verify_readspan.py
    python PicoMsgPack/tools/readspan/verify_readspan.py --base d45edca^

Exit code 0 = no drift, 1 = drift (a diff is printed).
Requires Python 3.8+ (stdlib only); ``--base`` additionally needs ``git``.
"""

import difflib
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from derive_readspan import (  # noqa: E402  (path set up above)
    REPO_ROOT,
    SEQUENTIAL_SIGNATURE,
    SOURCE,
    SPAN_SIGNATURE,
    derive,
    method_body,
    read_source,
    strip_provenance,
)

#: The last revision whose `Read()` was the original state machine.
BASE = "d45edca^"


def source_at(rev):
    """`MsgPackReader.cs` as committed at `rev` (LF line endings)."""
    result = subprocess.run(
        ["git", "-C", str(REPO_ROOT), "show", rev + ":" + SOURCE.relative_to(REPO_ROOT).as_posix()],
        capture_output=True,
    )
    if result.returncode != 0:
        sys.exit(
            "git could not read %s at %r:\n%s"
            % (SOURCE.relative_to(REPO_ROOT), rev, result.stderr.decode("utf-8", "replace"))
        )
    return result.stdout.decode("utf-8-sig").replace("\r\n", "\n")


def diff(expected, actual, expected_label, actual_label):
    return "\n".join(
        difflib.unified_diff(
            expected.split("\n"), actual.split("\n"), expected_label, actual_label, lineterm=""
        )
    )


def main(argv):
    args = argv[1:]
    base = None
    while args:
        arg = args.pop(0)
        if arg in {"--help", "-h"}:
            print(__doc__)
            return 0
        if arg == "--base" and args:
            base = args.pop(0)
            continue
        if arg.startswith("--base="):
            base = arg.split("=", 1)[1]
            continue
        sys.exit("unknown argument: %r" % arg)

    text, _ = read_source()
    sequential = method_body(text, SEQUENTIAL_SIGNATURE)
    span_body, _ = strip_provenance(method_body(text, SPAN_SIGNATURE))
    derived_body, _ = strip_provenance(derive(sequential))

    failures = 0

    print("derivation: ", end="")
    if derived_body == span_body:
        print("OK -- ReadSpan is exactly derive(ReadSequential)")
    else:
        failures += 1
        print("DRIFT")
        print(diff(span_body, derived_body, "MsgPackReader.cs:ReadSpan", "derive(ReadSequential)"))

    if base is not None:
        print("history:    ", end="")
        original = method_body(source_at(base), "public bool Read()")
        moved = sequential.replace("    " + SEQUENTIAL_SIGNATURE, "    public bool Read()", 1)
        if moved == original:
            print("OK -- ReadSequential is the %s Read() body, verbatim (signature aside)" % base)
        else:
            failures += 1
            print("DRIFT")
            print(diff(original, moved, "%s:Read()" % base, "MsgPackReader.cs:ReadSequential"))

    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
