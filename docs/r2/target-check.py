import re

SPEC = "docs/superpowers/specs/2026-10-25-format-capabilities-design.md"
CELLS = "tests/FormatCapabilities.Probe/baseline-cells.txt"

# ── the promise: spec §6 (only ✅ / ❌; "待测" is not a promise) ─────────────────
text = open(SPEC, encoding="utf-8").read()
sec6 = text.split("## 6. 目标矩阵", 1)[1].split("## 7.", 1)[0]
FORMATS = ["json", "msgpack", "toml", "yaml", "ini"]


def expand(label):
    """The §6 row labels, expanded to the 14 shape ids the probe scores."""
    if label == "S1-S4":
        return ["S1", "S2", "S3", "S4"]
    if label == "S5/S6":
        return ["S5", "S6"]
    if label == "S11a/S11b":
        return ["S11a", "S11b"]
    return [label]


expected = {}  # (format, shape) -> "ok" | "no" | "skip"
rows = [l for l in sec6.splitlines() if l.startswith("|") and not l.startswith("|---")]
for row in rows[1:]:  # rows[0] is the header
    cols = [c.strip() for c in row.strip("|").split("|")]
    label = cols[0].replace("\u2013", "-").split("（")[0].strip()
    if label.startswith("S14/S15"):
        continue  # "待测，Step 0 探针补测后并入" — not a promise, not scored
    shapes = expand(label)
    for fmt, raw in zip(FORMATS, cols[1:]):
        if "待测" in raw:
            for s in shapes:
                expected[(fmt, s)] = "skip"
        elif "/" in raw:  # "S1 ✅ / 其余 ❌"
            yes = re.findall(r"(S\d+[ab]?)\s*✅", raw)
            for s in shapes:
                expected[(fmt, s)] = "ok" if s in yes else "no"
        elif "✅" in raw:
            for s in shapes:
                expected[(fmt, s)] = "ok"
        elif "❌" in raw:
            for s in shapes:
                expected[(fmt, s)] = "no"
        else:
            raise SystemExit("unparsed cell: %s / %s: %r" % (fmt, label, raw))

# ── the measurement: the probe's 70 cells ─────────────────────────────────────
observed = {}
for line in open(CELLS, encoding="utf-8"):
    if line.startswith("CELL|"):
        _, fmt, shape, verdict = line.split("|")[:4]
        observed[(fmt.upper(), shape)] = verdict

bad = []
for (fmt, shape), want in sorted(expected.items()):
    got = observed[(fmt.upper(), shape)]
    ok = got == "OK"
    if want == "ok" and not ok:
        bad.append((fmt, shape, "spec ✅, probe " + got))
    if want == "no" and ok:
        bad.append((fmt, shape, "spec ❌, probe " + got))

print("expected cells from spec §6 :", len(expected), " (skip:", sum(1 for v in expected.values() if v == "skip"), ")")
print("expected ✅                  :", sum(1 for v in expected.values() if v == "ok"))
print("expected ❌                  :", sum(1 for v in expected.values() if v == "no"))
print("probe OK                     :", sum(1 for v in observed.values() if v == "OK"), "/", len(observed))
print("inconsistencies              :", len(bad))
for fmt, shape, why in bad:
    print("  MISMATCH %-7s %-5s %s" % (fmt, shape, why))
