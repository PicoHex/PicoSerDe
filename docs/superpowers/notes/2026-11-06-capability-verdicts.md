# Capability verdicts: what is pending, and the criterion

Written 2026-11-06, while the tristate model landed (main = `eb40c08`) **and** the tree is clean at that commit.

## The criterion (the user's, and it is the only one)

> **"Would a conforming encoder or decoder of this format have to do the same?"**
> No ⇒ **we** chose it ⇒ a `Deferred` (⏸) debt or an ordinary design choice. **Never** a format fact.
> Yes ⇒ `Impossible` (⛔), terminal.

## Why this memo exists

The capability table used to answer one boolean, so "the format cannot hold it" and "we have not built
it" were the same bit, the same glyph and the same red cell. `refactor/facts-tristate` split it three
ways (`Representable` / `Deferred` / `Impossible`) and moved **INI x S2** to `Deferred`. Auditing the
remaining `Impossible` cells for the same mistake **has not been done yet**.

## What is pending: audit the 14 remaining ⛔ cells

List them from `shared/Gen/DecisionTables.cs` (`Facts`, `Capability.Impossible`) — do not trust this
list, it is where the audit started, not where it ended:

```
toml x {S5, S6, S12, S13}
ini  x {S3, S4, S5, S6, S8, S10, S11a, S11b, S12, S13}
```

**INI needs the hardest look.** Spec §3 records that INI has **no independent implementation to appeal
to** (`configparser` accepts anything as a string) and that its boundary is **this spec's own
convention**. So for INI the criterion can almost never answer "yes" — and `ini x S12` (`List<string?>`)
is the sharpest case: spec §8.1 already registers an **`x+1`-level** entry for the INI value-level null
token, i.e. the tree already says the way out is *a new agreement*, while the cell says the format
*cannot*.

Evidence rules: an external parser first (`tomllib` for TOML, `configparser` for INI), the code second.
A published form that an external parser also does not turn back into a list is **not** evidence that
the format cannot hold the shape — the top-level numbered section is ✅ and has exactly that property.

## Also unsettled (smaller)

1. `README.md:624` reads as a comment about object lists where the sentence it replaced was about
   **nested members** — a topic slip from the wording round.
2. `docs/superpowers/notes/` — this file is the first one; if it outlives the audit, fold it into the
   spec or the README rather than keeping a stray notes/ directory.

---

## Audit result (2026-11-06, after this note was first written)

The audit ran. Its finding is bigger than "a few cells are mislabelled", and it is the reason this
section exists:

**The three-state model is still one state short.** The criterion allows three answers, not two:
the format requires it (`Impossible`), we have not built it but intend to (`Deferred`), and **we chose
not to support it and are not scheduling it** — a plain design choice. We have no tier for the third,
so five cells sit on `Impossible` while the audit's own reading is *"not required by the format, but
downgrading would promise a `reopen:` that does not exist"*.

**Nine cells should become `Deferred`** (`toml x {S12, S13}`, `ini x {S3, S4, S5, S6, S8, S10, S13}`),
each with its own reopen pointer — the agreement it needs is already registered in spec §8.1 as `x+1`
level in at least three cases, which is a direct contradiction: the tree says the way out is a new
convention while the cell says the format cannot hold the shape.

**Five cells stay, pending a decision that is not ours to make:** `ini x {S11a, S11b, S12}` and
`toml x {S5, S6}`. Two of them (`toml x S5/S6`) the audit judged *not* ⛔ but refused to downgrade,
because today there is no null carrier to borrow, so a ⏸ would point at a mechanism that does not
exist. `ini x S12` is the same shape of problem one level up: the registered `x+1` entry (V-6) settles
`null` vs `""` and does not settle this cell, so a ⏸ would send the reader to a debt that does not
resolve it.

⇒ **The five are left where they are, and inventing a tier for them was a mistake.** An earlier
revision of this note proposed a fourth verdict ("the format can, we choose not to support it") and a
rule for it was briefly written into `AGENTS.md`. It was removed, because the reasoning behind it was
wrong twice over:

1. **There is nothing to choose.** The earliest question the criterion can be asked is "CAN this format
   hold the shape?" — a capability question, answered by the format, not by us. "We could, and we choose
   not to" is only available if "we could" has already been established.
2. **It was never established.** For `ini x {S11a, S11b}` the audit's own verdict was *"cannot be
   decided"*, and for `toml x {S5, S6}` it was *"the null-leaf half has no external evidence"*. Both say
   the same thing: nobody has shown the format can carry these. Without that, "not supported by design"
   is a refusal with no basis — and `Impossible` and the invented tier would make the SAME claim about
   the format ("it cannot"), differing only in tone. **A tier whose definition cannot be applied is a
   tier that must not exist.**

⇒ **What is actually owed here is evidence, not a decision.** For each of the five: find whether a
conforming encoder or decoder of the format could carry the shape. If yes, the cell is a ⏸ with the
agreement named (as the eight reclassified cells now have); if no, it is ⛔ or the spec's own convention,
and the wording must say which. Until that evidence exists, leaving them on `Impossible` is the honest
option, because at least it does not promise anything.
