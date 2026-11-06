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
