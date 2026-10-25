// The probe corpus: one model per cell of the 14 × 5 matrix (spec §3 row order ×
// DecisionTables.FormatTags order) — 70 root models, plus ten OBSERVATION models at the
// end of this file (S14, S15 × 5 formats). Those two shapes have no decision path in the
// capability model: `AllShapeIds` stops at S13, so `DecideShape` does not know their ids
// and must NOT learn them in R1 (the capability table stays frozen while R1 only records
// behaviour). No `CELL|` line can exist for them, so Probe.cs measures them anyway and
// prints `CELL-OBS|…` lines, which are deliberately OUTSIDE the 70-cell statistics (they
// do not start with `CELL|` and they are not registered as cells).
//
// WHY ONE MODEL PER (shape, format) instead of one per shape: every cell gets its own
// type, so a cell's generated serializer is attributable to exactly one cell, and the
// generated-baseline/ snapshot (Task 3's before-picture) has one file per cell rather
// than one file exercising five formats at once.
//
// The models are deliberately plain POCOs — no [PicoSerializable], no base class, no
// interface. The generators' usage-driven pipeline (interceptor at the
// `X.Serializer.Serialize/Deserialize` call site) is what emits them, so a model only
// pulls in the ONE format the probe actually calls. Adding an attribute here would
// multiply the emitted code by five and desynchronise the baseline from the cells.
//
// PAYLOAD MEMBER: every container shape carries the payload in a single member `V`
// (the spec's own notation, e.g. `S1 List<int>` → `V : List<int>`). The two recursive
// shapes are the exception: their payload IS the object graph, so they carry the
// shape's own members (`Value`/`Child`, `Name`/`B`).
//
// The shape ids, the formats and the 70-cell cross product are NOT restated here as
// data — Probe.cs derives them from FormatCapabilities.AllShapeIds / AllFormatTags and
// asserts the registry covers exactly that cross product (see Probe.Validate).

namespace FormatCapabilities.Probe;

/// <summary>
/// The DTO payload of the object-valued shapes (S2, S4). Its members are chosen to
/// cover the R5 value-level items a container shape can carry: a string with
/// separator/quote/newline/unicode, <see cref="int.MaxValue"/>, a <see cref="double"/>
/// (shortest round-trip text), a <see cref="decimal"/> (invariant culture) and a
/// <see cref="DateTime"/> (value + <see cref="DateTime.Kind"/>).
/// </summary>
public sealed class ProbeDto
{
    public string Name { get; set; } = "";
    public int N { get; set; }
    public double F { get; set; }
    public decimal D { get; set; }
    public DateTime When { get; set; }
}

// ── S1  List<int> — flat scalar list ──────────────────────────────────────────────
public sealed class S1Json
{
    public List<int> V { get; set; } = [];
}

public sealed class S1Msgpack
{
    public List<int> V { get; set; } = [];
}

public sealed class S1Toml
{
    public List<int> V { get; set; } = [];
}

public sealed class S1Yaml
{
    public List<int> V { get; set; } = [];
}

public sealed class S1Ini
{
    public List<int> V { get; set; } = [];
}

// ── S2  List<Dto> — object list ───────────────────────────────────────────────────
public sealed class S2Json
{
    public List<ProbeDto> V { get; set; } = [];
}

public sealed class S2Msgpack
{
    public List<ProbeDto> V { get; set; } = [];
}

public sealed class S2Toml
{
    public List<ProbeDto> V { get; set; } = [];
}

public sealed class S2Yaml
{
    public List<ProbeDto> V { get; set; } = [];
}

public sealed class S2Ini
{
    public List<ProbeDto> V { get; set; } = [];
}

// ── S3  List<List<int>> — nested scalar list ──────────────────────────────────────
public sealed class S3Json
{
    public List<List<int>> V { get; set; } = [];
}

public sealed class S3Msgpack
{
    public List<List<int>> V { get; set; } = [];
}

public sealed class S3Toml
{
    public List<List<int>> V { get; set; } = [];
}

public sealed class S3Yaml
{
    public List<List<int>> V { get; set; } = [];
}

public sealed class S3Ini
{
    public List<List<int>> V { get; set; } = [];
}

// ── S4  List<List<Dto>> — nested object list ──────────────────────────────────────
public sealed class S4Json
{
    public List<List<ProbeDto>> V { get; set; } = [];
}

public sealed class S4Msgpack
{
    public List<List<ProbeDto>> V { get; set; } = [];
}

public sealed class S4Toml
{
    public List<List<ProbeDto>> V { get; set; } = [];
}

public sealed class S4Yaml
{
    public List<List<ProbeDto>> V { get; set; } = [];
}

public sealed class S4Ini
{
    public List<List<ProbeDto>> V { get; set; } = [];
}

// ── S5  List<int?> — nullable value leaf ──────────────────────────────────────────
public sealed class S5Json
{
    public List<int?> V { get; set; } = [];
}

public sealed class S5Msgpack
{
    public List<int?> V { get; set; } = [];
}

public sealed class S5Toml
{
    public List<int?> V { get; set; } = [];
}

public sealed class S5Yaml
{
    public List<int?> V { get; set; } = [];
}

public sealed class S5Ini
{
    public List<int?> V { get; set; } = [];
}

// ── S6  List<List<int?>> — deep nullable value leaf ───────────────────────────────
public sealed class S6Json
{
    public List<List<int?>> V { get; set; } = [];
}

public sealed class S6Msgpack
{
    public List<List<int?>> V { get; set; } = [];
}

public sealed class S6Toml
{
    public List<List<int?>> V { get; set; } = [];
}

public sealed class S6Yaml
{
    public List<List<int?>> V { get; set; } = [];
}

public sealed class S6Ini
{
    public List<List<int?>> V { get; set; } = [];
}

// ── S7  HashSet<int> as a member — extended collection ────────────────────────────
public sealed class S7Json
{
    public HashSet<int> V { get; set; } = [];
}

public sealed class S7Msgpack
{
    public HashSet<int> V { get; set; } = [];
}

public sealed class S7Toml
{
    public HashSet<int> V { get; set; } = [];
}

public sealed class S7Yaml
{
    public HashSet<int> V { get; set; } = [];
}

public sealed class S7Ini
{
    public HashSet<int> V { get; set; } = [];
}

// ── S8  List<HashSet<int>> — extended collection as an element ────────────────────
public sealed class S8Json
{
    public List<HashSet<int>> V { get; set; } = [];
}

public sealed class S8Msgpack
{
    public List<HashSet<int>> V { get; set; } = [];
}

public sealed class S8Toml
{
    public List<HashSet<int>> V { get; set; } = [];
}

public sealed class S8Yaml
{
    public List<HashSet<int>> V { get; set; } = [];
}

public sealed class S8Ini
{
    public List<HashSet<int>> V { get; set; } = [];
}

// ── S9  Dictionary<string,int> — flat dictionary ──────────────────────────────────
public sealed class S9Json
{
    public Dictionary<string, int> V { get; set; } = [];
}

public sealed class S9Msgpack
{
    public Dictionary<string, int> V { get; set; } = [];
}

public sealed class S9Toml
{
    public Dictionary<string, int> V { get; set; } = [];
}

public sealed class S9Yaml
{
    public Dictionary<string, int> V { get; set; } = [];
}

public sealed class S9Ini
{
    public Dictionary<string, int> V { get; set; } = [];
}

// ── S10  Dictionary<string,List<int>> — dict value is a collection ────────────────
public sealed class S10Json
{
    public Dictionary<string, List<int>> V { get; set; } = [];
}

public sealed class S10Msgpack
{
    public Dictionary<string, List<int>> V { get; set; } = [];
}

public sealed class S10Toml
{
    public Dictionary<string, List<int>> V { get; set; } = [];
}

public sealed class S10Yaml
{
    public Dictionary<string, List<int>> V { get; set; } = [];
}

public sealed class S10Ini
{
    public Dictionary<string, List<int>> V { get; set; } = [];
}

// ── S11a  self recursion (Node.Child : Node) ──────────────────────────────────────
public sealed class S11aJson
{
    public int Value { get; set; }
    public S11aJson? Child { get; set; }
}

public sealed class S11aMsgpack
{
    public int Value { get; set; }
    public S11aMsgpack? Child { get; set; }
}

public sealed class S11aToml
{
    public int Value { get; set; }
    public S11aToml? Child { get; set; }
}

public sealed class S11aYaml
{
    public int Value { get; set; }
    public S11aYaml? Child { get; set; }
}

public sealed class S11aIni
{
    public int Value { get; set; }
    public S11aIni? Child { get; set; }
}

// ── S11b  mutual recursion (M1.Child : M2, M2.Back : M1) ──────────────────────────
public sealed class S11bJson
{
    public string Name { get; set; } = "";
    public S11bJsonPartner? B { get; set; }
}

public sealed class S11bJsonPartner
{
    public int N { get; set; }
    public S11bJson? A { get; set; }
}

public sealed class S11bMsgpack
{
    public string Name { get; set; } = "";
    public S11bMsgpackPartner? B { get; set; }
}

public sealed class S11bMsgpackPartner
{
    public int N { get; set; }
    public S11bMsgpack? A { get; set; }
}

public sealed class S11bToml
{
    public string Name { get; set; } = "";
    public S11bTomlPartner? B { get; set; }
}

public sealed class S11bTomlPartner
{
    public int N { get; set; }
    public S11bToml? A { get; set; }
}

public sealed class S11bYaml
{
    public string Name { get; set; } = "";
    public S11bYamlPartner? B { get; set; }
}

public sealed class S11bYamlPartner
{
    public int N { get; set; }
    public S11bYaml? A { get; set; }
}

public sealed class S11bIni
{
    public string Name { get; set; } = "";
    public S11bIniPartner? B { get; set; }
}

public sealed class S11bIniPartner
{
    public int N { get; set; }
    public S11bIni? A { get; set; }
}

// ── S12  List<string?> — null ELEMENT in a string list ────────────────────────────
public sealed class S12Json
{
    public List<string?> V { get; set; } = [];
}

public sealed class S12Msgpack
{
    public List<string?> V { get; set; } = [];
}

public sealed class S12Toml
{
    public List<string?> V { get; set; } = [];
}

public sealed class S12Yaml
{
    public List<string?> V { get; set; } = [];
}

public sealed class S12Ini
{
    public List<string?> V { get; set; } = [];
}

// ── S13  Dictionary<string,int?> — null VALUE in a dictionary ─────────────────────
public sealed class S13Json
{
    public Dictionary<string, int?> V { get; set; } = [];
}

public sealed class S13Msgpack
{
    public Dictionary<string, int?> V { get; set; } = [];
}

public sealed class S13Toml
{
    public Dictionary<string, int?> V { get; set; } = [];
}

public sealed class S13Yaml
{
    public Dictionary<string, int?> V { get; set; } = [];
}

public sealed class S13Ini
{
    public Dictionary<string, int?> V { get; set; } = [];
}

// ══ OBSERVATION-ONLY MODELS (spec §3's two「待测」rows; NOT cells) ══════════════════════
// Same construction rules as above (plain POCO, one type per (shape, format)), but these
// types are never handed to Registry.Cells(): they exist so Probe.cs can MEASURE what
// today's generators do with a shape that has no capability path yet. Nothing here
// reaches DecideShape, DecisionTables or any generator's capability decision.

// ── S14  List<Dto?> — null ELEMENT in an OBJECT list ──────────────────────────────
public sealed class S14Json
{
    public List<ProbeDto?> V { get; set; } = [];
}

public sealed class S14Msgpack
{
    public List<ProbeDto?> V { get; set; } = [];
}

public sealed class S14Toml
{
    public List<ProbeDto?> V { get; set; } = [];
}

public sealed class S14Yaml
{
    public List<ProbeDto?> V { get; set; } = [];
}

public sealed class S14Ini
{
    public List<ProbeDto?> V { get; set; } = [];
}

// ── S15  scalar family: float / decimal / DateTime / TimeSpan / Guid / enum? ──────
// One member per scalar the spec row names, because that row's question is PER SCALAR:
// a format can keep `decimal` and lose `TimeSpan`. Six members keep that visible in one
// observation line — Probe.cs compares member-by-member and names the losers.
public sealed class S15Json
{
    public float F { get; set; }
    public decimal D { get; set; }
    public DateTime When { get; set; }
    public TimeSpan Span { get; set; }
    public Guid Id { get; set; }
    public DayOfWeek? Day { get; set; }
}

public sealed class S15Msgpack
{
    public float F { get; set; }
    public decimal D { get; set; }
    public DateTime When { get; set; }
    public TimeSpan Span { get; set; }
    public Guid Id { get; set; }
    public DayOfWeek? Day { get; set; }
}

public sealed class S15Toml
{
    public float F { get; set; }
    public decimal D { get; set; }
    public DateTime When { get; set; }
    public TimeSpan Span { get; set; }
    public Guid Id { get; set; }
    public DayOfWeek? Day { get; set; }
}

public sealed class S15Yaml
{
    public float F { get; set; }
    public decimal D { get; set; }
    public DateTime When { get; set; }
    public TimeSpan Span { get; set; }
    public Guid Id { get; set; }
    public DayOfWeek? Day { get; set; }
}

public sealed class S15Ini
{
    public float F { get; set; }
    public decimal D { get; set; }
    public DateTime When { get; set; }
    public TimeSpan Span { get; set; }
    public Guid Id { get; set; }
    public DayOfWeek? Day { get; set; }
}
