# INI capability verdicts: how this was settled, and what was wrong on the way

Written 2026-11-06. Settled at `AGENTS.md`'s **arbiter** rule — "What 'the format' MEANS, in order".

## The criterion

> **Would a conforming encoder or decoder of this format have to do the same?**
> Yes ⇒ `⛔`, terminal. No ⇒ `we` did it ⇒ `⏸` (a debt) or an ordinary design choice — never a format fact.

And the arbiter that answers "would a conforming implementation do it":

1. **The specification, when the format has one.** JSON (RFC 8259), MessagePack, TOML 1.0, YAML 1.2 —
   demonstrated through a conforming implementation (`tomllib`, PyYAML).
2. **The de-facto standard, when it has none.** INI has no RFC, no ISO and no owning body, so its arbiter
   is what the widespread implementations do **together**: Windows `kernel32`
   (`GetPrivateProfileString`), Python `configparser`, `git config`.

## The measurement (2026-11-06, all three, run for this)

| construct | `configparser` | `git config` | `kernel32` |
|---|---|---|---|
| `[V]` + `k = v` | key→value map | key→value | key→value |
| `V = 1,2,3` | **one string** | **one string** | **one string** |
| `[Rows.0]`/`[Rows.1]` | two opaque section names | one opaque name | one opaque name |
| value-level null | values are strings | values are strings | values are strings |

**Nobody returns a list for `[Rows.0]`; nobody has a value-level null.** So a member-level object list and
a value-level null are **not carried by the format** — they are ⛔, and emitting a composite section name
would be simulating a construct the format does not have, which the iron rule forbids.

## What was wrong on the way (three attempts, all recorded here so they are not repeated)

1. **"The boundary is this spec's convention."** The spec said INI "has no independent implementation to
   appeal to" and therefore its boundary was our convention. That misreads *no specification* as *no
   arbiter*. There is an arbiter — the de-facto standard — and it answers ⛔.
2. **A three-state capability table** (`Impossible` / `Deferred` / `Representable`) was built on the claim
   that these cells were "the format can, we are missing an agreement". Under the arbiter they are not:
   the format does not carry them at all. `Facts` is a yes/no **about the format**, and it is back to a
   bool.
3. **A fourth verdict** ("the format can, we choose not to support it") was briefly added to the rule. It
   was removed: the criterion's first question is a capability question, and "we could but choose not to"
   needs "we could" established first — which it never was. It also said the same thing as `⛔` about the
   format, differing only in tone.

## What stays true

`⏸` is still the right verdict where the format **does** carry the shape and **we** have not built it —
an anonymous-type collection in MessagePack/TOML/YAML (`List<T>.Count` is known before a byte is written,
so the counted header is not the obstacle), and recursion at the element/dictionary-value positions for
TOML and YAML. Those cells are `Facts == true` with a dropping `Rules` entry, which is exactly what `⏸`
is for.
