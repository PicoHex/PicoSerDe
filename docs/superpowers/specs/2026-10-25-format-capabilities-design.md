# PicoSerDe Format Capabilities Design Spec

**Status**: Draft v4 — D1/D2/D3 resolved by the architecture principle (§1.1); one release-policy ruling open (§5.5)
**Date**: 2026-10-25
**Baseline**: `84a9560` (`main`, clean tree); §3 现状表 measured there
**Topic**: which shapes each of the five wire formats can represent, and how that
becomes one reviewable, testable table instead of 25 scattered format checks
**Contract**: `picohex-picoserde` (AOT / zero reflection / fail-loud) + `picohex-self-review`

---

## 0. 修订记录

| 版本 | 变更 |
|---|---|
| v1 | 初稿：R1–R3、实测矩阵、D-1…D-4、D1/D2/D3 |
| v2 | 按两份独立复核（`design-review.md` / `evidence-review.md`）重写规则层与 §4 |
| **v3** | 按第三轮复核（`review-v2.md`，15 FIXED / 2 PARTIAL / 8 新问题）修订；**并用 R1 指定的独立合规实现（Python `tomllib`、PyYAML）重新判定整张表示性表** |
| **v4** | 采纳维护者架构原则（§1.1）。该原则**直接裁定**了 D1/D2/D3，并**反转阶段顺序**：先建共用管线，再补能力 —— 否则 7 个扩展集合 kind × 5 格式会在各生成器里手写一遍再被 Step 3 推翻 |

**v3 的关键突破**：v1/v2 一直把「格式事实」与「我们的欠账」混在一列符号里，导致「规则说 `⏸`、矩阵写 `⛔`」这类自相矛盾反复出现（v2 的 N1/N2）。v3 把它们**拆成两张表**：§3.2 表示性（R1，独立实现实测）与 §3.1 现状（我们的生成器实际行为）。拆开后，唯一真正的 `⛔` 只剩 **INI 的结构性限制**与 **TOML 缺 null**；其余全部是 `⏸`。

| v3 修订项（第三轮） | 修法 |
|---|---|
| **N1** R4 说「D3 只能进 `⏸`」，但 9 格写着 `⛔003` | 递归在 TOML/YAML 经独立实现验证**可表示** → 改 `⏸003`；INI 保持 `⛔003`（扁平，真不可表示） |
| **N2** `S13×YAML ⛔` 却排期 Step 2 转 ✅；`🐞` 与 `⏸` 混在一列 | 拆表（§3.1 现状 / §3.2 表示性）；`S13×YAML → ⏸004` |
| **N3** I-5（消息须含格式+形状）与 Step 0 验收③（诊断逐行一致）互斥 | I-5 生效阶段改 **Step 1**；Step 0 验收③注明「除 I-5 计划内的消息改动」 |
| **N4** Step 1 验收「矩阵无 🐞」不可达（D-7b 排到 Step 2） | 改为「🐞 只剩 §6 明确排到 Step 2 的格」 |
| **N5** §8 规则句推不出自己的表；「两条政策」列了三条 | §8 改为**三轴映射**；计数改正 |
| **N6** D-5 标 `y+1` 与 D-7a 标 `x+1` 不一致；「不再丢失」措辞错 | D-5 独立一行 `x+1`；措辞改「不再静默」 |
| **N7** D-6 只引 1 个发射点（实为 3 个，其中 2 个静默） | D-6 改为「3 处发射点，`:1133`（公开字段）与 `:1188`（属性）静默」 |
| **N8** 公共诊断消息把 `⏸`（可表示）说成「cannot be represented」 | D-4 扩容为「诊断消息改造」，要求 `⏸`/`⛔` 可区分 |
| **M1/M12 残留** | R1′ 补「null ≠ 缺失 ≠ 空」等价关系族；§3/§6 补 scalar 行 |
| **O2** `List<List<Dto>> × TOML` 疑为可表示 | **已证**：`V = [[{a = 1}], [{b = 2}]]` 被 `tomllib` 接受 → `⏸004` 而非 `⛔004` |

---

## 1. 目的与非目标

### 目的
把「**哪种形状在哪种格式可表示**」从散落在 25 处 `if (formatTag == ...)` 的隐式共识，变成**一张可审阅、可测试、唯一的表**。

实测计数（三轮独立复核一致）：`shared/Gen` 格式特判 **25 处** = `TypeKindResolver.cs` 12 + `GenInfrastructure.cs` 13；5 个生成器文件合计 **20,687 行**；同一 kind 分派重复 `case "int32"` **43** 次、`case "string"` **47** 次、`case "dict"` **30** 次。

### 非目标
- 不追求「所有格式支持所有形状」。
- 不解决 reader 的流式/resume 状态机差异（Step 3 的独立规格）。
- 不改变「写侧省略 null 成员」的既有设计（`D1b` 另行签字）。

## 1.1 架构原则（规范性，R0）

> **R0 — 三条**：
> 1. **形状与遍历共用**：类型形状的遍历（属性、集合、嵌套、可空、字典、递归）只有**一份**实现，位于 shared/core；不得在每个格式生成器里各写一遍。
> 2. **wire 表达由格式决定**：同一形状在各格式的**编码**不同，这是格式的职责，由格式原语承担。
> 3. **格式明确不支持的类型则不做支持**：只有格式**无法表达**的形状才不实现；其余一律实现。

**R0 的推论（这才是本规格真正的骨架）**：

| 推论 | 内容 |
|---|---|
| C1 | 能力表只记录**格式事实**（wire 能表达什么），**不记录我们的实现进度**。实现进度由探针测试跟踪，不进表。 |
| C2 | 目标态**不存在 `⏸`**：每格只能是 ✅（格式能表达 → 必须支持）或 ⛔（格式不能表达）。`⏸` 是**过渡态欠账**，必须带 reopen 条件，且不得出现在 §6。 |
| C3 | 顺序：**先建共用管线，再补能力**。若先按格式逐一手写能力（7 个扩展集合 kind × 5 格式 = 28 条读侧构造 × 6 条发射路径），会立刻被共用管线推翻 —— 那是**重复劳动**。 |
| C4 | 诊断是 C2 的**唯一**出口：凡是 ⛔，必须有诊断；凡是有诊断，必须是格式事实（不得用诊断掩盖未实现）。 |

## 2. 规范性规则

### R1 — 表示性（判据在格式，不在我们的实现）
> 一种形状在某格式**不可表示**，当且仅当**不存在**一种该格式的表示，能被该格式的**规范合规的独立实现**往返成等价的值。

- 判定必须用**独立实现**（本规格用 Python `tomllib` 与 PyYAML，见 §10）。
- 「我方 reader 尚未实现」「实现代价过高」**不是**不可表示的理由（→ `⏸`，见 R4）。

### R1′ — 等价关系（每形状一个）
「忠实」不是一句话，而是**每形状一个等价关系**。缺这张表会让 R1 自相矛盾（`HashSet` 无序，按「顺序保持」的字面定义反而应判不可表示）。

| 形状族 | 等价关系 | 备注 |
|---|---|---|
| sequence（`List`/`T[]`） | **有序**、等长、逐元素等价 | |
| set（`HashSet`/`ISet`） | **集合相等**（不看顺序） | 写侧顺序不保证；读侧不得依赖 |
| map（`Dictionary`） | **键值多重集相等**（键序不保证） | |
| scalar 数值 | `(值, 文本精度)`：数值相等 **且**写侧为该值的最短可往返文本 | `float` 不得经 `double` 中转；`decimal` 必须 invariant |
| scalar 时间 | `(值, Kind/offset, 精度)` | `DateTime.Kind` 必须保持 |
| scalar 字符串 | 解码后码点序列相等（转义形式不限） | |
| 特殊浮点 | `NaN`/`±Inf` 仅在格式有字面量时可表示 | **R1 的标准示例** |
| **null 三态** | `null` 值 / **缺失键（或缺失成员）** / **空集合** 三者**互不等价** | `{"a":null}` ≠ `{}` ≠ `{"a":[]}`；`Dictionary` 的 null 值 ≠ 缺键 |

> **null 三态**这一族是 v3 补的：`List<string?>` 的 null 元素 vs 跳过该元素、`Dictionary<string,int?>` 的 null 值 vs 缺键、`[]` vs 缺成员，在矩阵里必须能分辨，否则 R1 的判定会随实现漂移（当前 D-7a 正是把 null 元素**静默跳成"缺失"**）。

### R1″ — canonical write / lenient read
写侧每种概念只发**一种**规范形态；读侧必须接受该概念在格式规范内的**全部**合法形态。（YAML null 写 `null`，读 `null`/`~`/`Null`/`NULL`/空。）

### R2 — 无静默
> 任何未进入生成代码的成员，**必须**产生一条**真的会出现在构建输出里**的诊断，id ∈ {`PICOSERDE001`…`005`}。

「收集了但不上报」等同静默（`001` 目前就是这种状态，见 D-6）。**注意**：R2 只管"成员是否进入生成代码"，**值级丢失**由 R5 覆盖。

### R3 — 不可表示 ≠ 不可编译
任何 ⛔/⏸ 形状不得产出不可编译的生成代码。

### R4 — 代价闸门
> 表示**存在**但未实现 ⇒ `⏸`：**必须**有诊断、**必须**写明 reopen 条件、**不得**写成 `⛔`。

`⛔` 是格式事实，`⏸` 是我们的欠账。**每个 `⏸` 格必须能回答"reopen 条件是什么"。**

### R5 — 值级保真
成员进入生成代码**不等于**保真。`✅` 必须由**含边界值**的往返测试支撑（null 元素、空集合、单元素、`NaN`/`±Inf`、`decimal` invariant、`DateTime.Kind`、含分隔符/引号/换行的字符串、`int.MaxValue`）。

## 3. 矩阵

> 两张表缺一不可：**表示性**是格式事实（R1，独立实现实测），**现状**是我们的行为（含缺陷）。v1/v2 把两者混在一列，才反复出现「规则写 ⏸、矩阵写 ⛔」。

### 3.0 证据方法

- **表示性**（§3.2）：Python `tomllib`（TOML 1.0 合规）与 PyYAML，逐形状喂入规范形态并检查解析结果；命令见 §10。
- **现状**（§3.1）：55 格探针（5 格式 × 11 形状）+ 10 格值级探针，采构建期诊断 + 往返实测。**两名独立复核者各自重建探针**，54/55 格与首测一致（唯一差异为 S11 的形状歧义，v2 已拆为 S11a/S11b）。

### 3.1 现状表（我们的生成器实际行为，基线 `84a9560`）

图例（**只描述现状**）：✅ 往返正确 ｜ 🐞 无诊断 + 数据丢失 ｜ ⛔*id* 成员被丢弃且有该诊断 ｜ ⏸*id* 成员被丢弃且有该诊断，但格式**本可表示**

| # | 形状（成员 `V`） | JSON | MsgPack | TOML | YAML | INI |
|---|---|---|:---:|---|---|---|
| S1 | `List<int>` | ✅ | ✅ | ✅ | ✅ | ✅ |
| S2 | `List<Dto>` | ✅ | ✅ | ✅ `[[Table]]` | ✅ | ⛔004 |
| S3 | `List<List<int>>` | ✅ | ✅ | ✅ `[[1,2],[3]]` | ✅ | ⛔004 |
| S4 | `List<List<Dto>>` | ✅ | ✅ | ⏸004 | ✅ | ⛔004 |
| S5 | `List<int?>` | ✅ | ✅ | ⛔004 | ⏸004 | ⛔004 |
| S6 | `List<List<int?>>` | ✅ | ✅ | ⛔004 | ⏸004 | ⛔004 |
| S7 | `HashSet<int>` 作成员 | ✅ | 🐞 **D-1** | 🐞 **D-1** | 🐞 **D-1** | 🐞 **D-1** |
| S8 | `List<HashSet<int>>` 作元素 | ⏸004 | 🐞 **D-2** | 🐞 **D-2** | 🐞 **D-2** | 🐞 **D-2** |
| S9 | `Dictionary<string,int>` | ✅ | ✅ | ✅ | ✅ | ✅ |
| S10 | `Dictionary<string, List<int>>` | ⏸004 **D-3** | ⏸004 **D-3** | ⏸004 **D-3** | ⏸004 **D-3** | ⛔004 |
| S11a | 递归：**自递归** `Node.Child: Node` | ✅ | ✅ | ⏸003 | ⏸003 | ⛔003 |
| S11b | 递归：**互递归** `M1.Child: M2` | ✅ | ✅ | 🐞 **D-5** | ⏸003 | ⏸003 |
| S12 | `List<string?>`（值级 null 元素） | ✅ | ✅ | 🐞 **D-7a** | 🐞 **D-7b** | ⛔004 |
| S13 | `Dictionary<string,int?>`（null 值） | ✅ | ✅ | ⛔004 | ⏸004 | ⛔004 |
| S14 | `List<Dto?>`（对象 null 元素） | **未测** | **未测** | **未测** | **未测** | **未测** |
| S15 | scalar 族（`float`/`double`/`decimal`/`DateTime`/`TimeSpan`/`Guid`/`enum?`） | 见 §3.4 | | | | |

### 3.2 表示性表（R1，独立实现实测）

✅ 可表示（独立实现接受规范形态）｜ ❌ 不可表示（我给出的形态被独立实现拒绝）

| 形状 | JSON | MsgPack | TOML | YAML | INI | 依据 |
|---|:---:|:---:|:---:|:---:|:---:|---|
| S1–S4（含嵌套对象列表） | ✅ | ✅ | ✅ | ✅ | ❌ | TOML：`V = [[{a = 1}], [{b = 2}]]` 被 `tomllib` 接受；INI 无嵌套 |
| S5/S6/S12/S13（null） | ✅ | ✅ | ❌ | ✅ | ❌ | TOML：`V = ["a", null]` / `{a = 1, b = null}` 均被 `tomllib` **拒绝**；YAML 全部接受 |
| S7/S8（扩展集合） | ✅ | ✅ | ✅ | ✅ | S7 ✅ / S8 ❌ | TOML `V = [1,2,3]`、`V = [[1],[2,3]]` 均被接受 |
| S9/S10（字典） | ✅ | ✅ | ✅ | ✅ | S9 ✅ / S10 ❌ | TOML `V = {a = [1, 2]}` 被接受 |
| S11a/S11b（递归） | ✅ | ✅ | ✅ | ✅ | ❌ | TOML `[Child]` / `[Child.Child]` 被接受；YAML 两层映射被接受 |

**结论（这是 v3 的核心）**：真正的 `⛔` 只有两类 ——
1. **INI 的结构性限制**：任何嵌套（S2–S4、S8、S10、S11）与任何 null（S5、S6、S12、S13）；
2. **TOML 缺 null**：S5、S6、S12、S13。

其余所有「不支持」都是 `⏸`（我们的欠账）。

### 3.3 S11 必须区分两种递归
自递归从第 0 层就丢；互递归在 TOML/YAML/INI 的「1 层可用」是**偶然**（不是契约），且 TOML 会覆盖根成员（D-5）。

### 3.4 S15 scalar 族（v3 新增行，**未测**）
R1′ 花 6 行定义 scalar 等价关系，但目前为止**一格都没实测**。已知行为（来自 `review-report-2026-10-23.md` 的 C-14 与本轮修复）：`decimal` 曾因 culture 把 `1.5m` 往返成 `15`（已修，`WriteNumber(float)` 也已补最短往返）。**Step 0 的探针必须补这一族**：`float`/`double` 最短往返、`decimal` invariant、`DateTime.Kind`、`TimeSpan` 格式、`Guid`、`enum?`。

## 4. 缺陷清单

| ID | 类别 | 缺陷 | 证据 |
|---|---|---|---|
| **D-1** | 静默丢失 | `HashSet<T>` 家族作**成员**：MsgPack/TOML/YAML/INI 无任何诊断且成员消失 | 四格式输出只有 `K`；日志 0 条 001/003/004 |
| **D-2** | 静默丢失 | 同上作**元素**（`List<HashSet<int>>`）：四格式静默 | 同上 |
| **D-6** | **静默的根因** | `PICOSERDE001` 被收集后从不发射。**3 个发射点**：`GenInfrastructure.cs:1133`（**公开字段**路径）、`:1188`（属性路径）、`:1537`（递归路径）。前两处只 `diagnostics?.Add` + `continue`，且**不进** `skippedUnsupported`；而 5 个生成器**从不传** `diagnostics` 列表 ⇒ 永不上报。仅 `:1537` 另有 `skippedRecursive` 故会产出 003 | `grep -rn "diagnostics:" Pico*/src/*.Gen/` 无命中；构建日志 `PICOSERDE001` 0 次 |
| **D-5** | **数据损坏** | TOML 互递归往返**覆盖根成员**：写 `K = "root"` + `[Child] K = "c1"`，读回 `K = "c1"` | `CELL\|toml\|S11-mutual\|…\|K=c1` |
| **D-7a** | 值级静默丢失 | **TOML**：`List<string?>` 的 null 元素被静默跳过（成员在、值丢）；应改为类型级 `⛔004`（TOML 无 null，§3.2 已证） | `V = ["a", "c"]` |
| **D-7b** | 值级静默丢失 | **YAML**：同上；但 YAML 有 null ⇒ 应**支持**（并入 D1-A） | `V:⏎- a⏎- c` |
| **D-3** | 实现缺口 | `Dictionary<string, List<T>>` 在 JSON/MsgPack/TOML/YAML **可表示**却丢弃（有 004，不违反 R2，违反 R1） | §3.2 已证四格式可表示 |
| **D-4** | 诊断措辞 | ①递归诊断指向内层类型成员；②**`⏸` 格与 `⛔` 格共用同一条消息**「cannot be represented by this format」—— 对 YAML 的 S5/S6/S13 这类**可表示**的格，公共消息在误导用户（N8） | `GenInfrastructure.cs:183/235` 的 `messageFormat` 只含 `{0}` |
| **D-8** | 双口径 | 匿名类型走**另一套** id（`PICOSERDE005`）与另一套消息，能力口径与具名类型可能不一致 | `AnonTypeHandler` + `:276` |

## 5. 裁定记录（由 R0 推导，v4）

下列各项在 v3 中曾作为「需签字选项」列出；**v4 按 R0 直接裁定**，推导一并写出，供复核。

### 5.1 D1 — YAML 序列里的 `null` → **实现**（原 D1-A）

- **推导**：R0 第 3 条 + §3.2：PyYAML 接受 `- null`、`V: {a: 1, b: null}`、`[[1, null]]` ⇒ YAML **能表达** ⇒ 必须实现。
- **范围**：S5/S6/S12/S13 × YAML 全粒度。
- **写侧形态**：`- null`（显式）。**不可**用裸 `-` —— F5 已将其占用为「嵌套序列项」（R1″ 要求写侧唯一）。
- **TOML/INI 的 `⛔` 保持**：`tomllib` 明确拒绝 `null`（§3.2），是格式事实。

### 5.2 D2 — 扩展集合 → **全格式实现**（原 D2-A）

- **推导**：R0 第 3 条：7 个 kind（`hashset`/`queue`/`stack`/`linkedlist`/`immutablearray`/`memory`/`readonlymemory`）在 5 个格式上都能用「数组」表达（§3.2 已证 TOML/YAML 接受嵌套数组），**没有任何格式明确不支持** ⇒ 必须实现。
- **实现位置**：**在共用管线里实现一次**（R0 第 1 条），不是在 5 个生成器里各写一遍。
- **语义注意（R1′）**：`Stack<T>` 枚举 top→bottom，读侧必须按**逆序 `Push`** 构造才能保序；`Memory<T>`/`ReadOnlyMemory<T>` 读侧需 `new Memory<byte>(array)`。这两条写进管线的容器构造策略表。
- **原 D2-B（先加诊断止血）不再必要**：共用管线会在同一步内让它们真正可用；但**若管线分多次落地**，中间态仍须有诊断（R2）。

### 5.3 D3 — 递归深度 → **TOML/YAML 实现，INI 不支持**（原 D3-B/D3-C 合并）

- **推导**：R0 第 3 条 + §3.2：`tomllib` 接受 `[Child]` / `[Child.Child]`，PyYAML 接受两层映射 ⇒ **能表达** ⇒ 必须实现；INI 是扁平结构 ⇒ 明确不支持 ⇒ 不做。
- **架构要求**：实现任意深度需要**运行时深度循环**（静态 helper 链无法无界展开）。这正好落在共用管线里（R0 第 1 条），不需要为每种格式单独设计。
- **D-5（TOML 根成员被覆盖）必须在同一步修掉**，否则"实现"等于实现一个有数据损坏的版本。

### 5.4 D4 — 诊断 id 与消息 → **公共契约**

- **推导**：C4：诊断是"不支持"的唯一出口 ⇒ 用户会根据 id 做 `NoWarn` ⇒ id 必须在 `year` 内稳定；消息模板可变，但须保持可 grep 的稳定前缀，且**`⛔`（格式事实）与"未实现"必须可区分**（v3 的 D-4/N8 正是把两者混用）。

### 5.5 D5 — `FormatCapabilities` 的可见性 → **internal**（唯一仍需你确认的产品问题）

- **推导**：R0 第 1/2 条把能力表定位为**共用管线的内部机制**，不是用户 API ⇒ `internal` + `InternalsVisibleTo` 给测试。
- **后果**：`api/*.public.txt` 不变；I-3 只能在仓库内部验证（外部用户无法编程查询能力）。
- **若你想让用户可查**（`public`）：这是一次 `x` bump + 基线更新，规格其余部分不变。

### 5.6 唯一未决：发布策略
能力全部放开会改变生成输出（新增成员/键），属 `x` bump（§8）。**问题：一次性 `x` bump 交付，还是分两次（先修静默 + 共用管线，再放开能力）？** 我建议**分两次**，理由见 §9。

## 6. 目标矩阵（R0 终态：只有 ✅ 与 ⛔，**无 `⏸`**）

按 R0/C2：能表达就必须支持。下表是**终态**，不是某一阶段的中途状态。

| # | 形状 | JSON | MsgPack | TOML | YAML | INI |
|---|---|---|:---:|---|---|---|
| S1 | `List<int>` | ✅ | ✅ | ✅ | ✅ | ✅ |
| S2 | `List<Dto>` | ✅ | ✅ | ✅ | ✅ | ⛔ |
| S3 | `List<List<int>>` | ✅ | ✅ | ✅ | ✅ | ⛔ |
| S4 | `List<List<Dto>>` | ✅ | ✅ | ✅ | ✅ | ⛔ |
| S5 | `List<int?>` | ✅ | ✅ | ⛔ null | ✅ | ⛔ |
| S6 | `List<List<int?>>` | ✅ | ✅ | ⛔ null | ✅ | ⛔ |
| S7 | `HashSet<int>` 成员 | ✅ | ✅ | ✅ | ✅ | ✅ |
| S8 | `List<HashSet<int>>` 元素 | ✅ | ✅ | ✅ | ✅ | ⛔ 嵌套 |
| S9 | `Dictionary<string,int>` | ✅ | ✅ | ✅ | ✅ | ✅ |
| S10 | `Dictionary<string, List<int>>` | ✅ | ✅ | ✅ | ✅ | ⛔ 嵌套 |
| S11a/S11b | 递归（任意深度） | ✅ | ✅ | ✅ | ✅ | ⛔ 扁平 |
| S12 | `List<string?>` | ✅ | ✅ | ⛔ null | ✅ | ⛔ |
| S13 | `Dictionary<string,int?>` | ✅ | ✅ | ⛔ null | ✅ | ⛔ |
| S14 | `List<Dto?>` | 待实测 | 待实测 | ⛔ null | 待实测 | ⛔ |
| S15 | scalar 族（`float`/`decimal`/`DateTime`/`TimeSpan`/`Guid`/`enum?`） | 待实测 | 待实测 | 待实测 | 待实测 | 待实测 |

**唯一的 `⛔` 来源**（与 §3.2 一致）：
1. **INI 结构性限制**：任何嵌套、任何 null、任何对象列表；
2. **TOML 缺 null**：S5/S6/S12/S13（以及 S14 的对象 null）。

### 6.1 中间态允许 `⏸`，但必须收敛
落地过程中允许出现 `⏸`（R4），但：
- 每个 `⏸` 必须写明 **reopen 条件**；
- §9 的**每个阶段验收**都要能回答"还剩哪些 `⏸`、为什么"；
- 任何 `⏸` 不得进入发布说明的"已知限制"（那是 `⛔` 的用途），只能进"路线图"。

## 7. 不变式

| ID | 不变式 | 强制方式 | 生效 |
|---|---|---|---|
| **I-1** | 未进入生成代码的成员产生诊断（id ∈ 001…005），且**真的在 `GetRunResult().Diagnostics` 里** | 生成器驱动测试，逐形状断言（直接覆死 D-6） | Step 1 |
| **I-2** | ⛔/⏸ 形状不得产出不可编译代码 | `CSharpCompilation.Emit` 无 error | Step 0 |
| **I-3** | 能力表是唯一判据 | **类型级禁止**：生成器 API 只收 `FormatCapabilities` 值（拿不到字符串就写不出 `formatTag == "json"`）+ Roslyn 架构测试扫描 `shared/Gen` | Step 0 |
| **I-4** | 每个 ✅ 有往返测试并与矩阵同源 | **生成式**：表每格带 `SampleType`，测试与 README 矩阵都从表生成 | Step 0 |
| **I-5** | 诊断含成员名 + 格式 + 形状；`⏸` 与 `⛔` 的消息**可区分** | 断言消息片段 | **Step 1**（因需改 `messageFormat`：003/004 现在只含 `{0}`，见 N3） |
| **I-6** | ✅ 由边界值样本支撑（R5 + §3.4 的 scalar 族） | 数据驱动多值探针 | Step 2 |

## 8. 兼容性政策

仓库版本是 **`<year>.<x>.<y>`**，**三根轴**：

| 轴 | 规则 |
|---|---|
| 公共 API 变化（含纯新增） | `x+1` |
| 公共 API 不变，**行为收紧或放开** | `x+1` |
| 公共 API 不变，仅**新增诊断**（wire 输出不变） | `y+1` |

| 变更 | API | 行为 | 版本 |
|---|---|---|---|
| D-6（接通 001）、D2-B | 不变 | 新增诊断 | `y+1` + **必须附 `NoWarn` opt-out 说明** |
| D-1/D-2（D2-A 生效后） | 不变 | 成员不再丢失 | `x+1`（放开） |
| **D-5**（根成员不再被覆盖） | 不变 | **读回结果改变** | `x+1` |
| D-7a（TOML 改类型级 ⛔） | 不变 | 收紧（成员消失 + 诊断） | `x+1` |
| D-7b / D1-A（YAML 支持 null） | 不变 | 放开 | `x+1` |
| D-3 / S10 | 不变 | 放开 | `x+1` |
| **D3-A / D3-B(N)** | 不变 | 收紧 | `x+1` + 迁移说明 |
| `FormatCapabilities` 若 public（D5） | **变** | — | `x+1` + 更新 `api/*.public.txt` |

**三条政策**（v3 修正了 v2 的「两条」笔误）：
1. **新增诊断必须附 `NoWarn` opt-out 说明**（否则 `warnaserror` 用户升级即挂）。
2. **诊断 id + 消息模板是公共契约**，变更同样计版本。
3. 除 `FormatCapabilities`（若 public）外，本批**不改** `api/*.public.txt`；禁止「每项都更新基线」。

## 9. 实施阶段（按 R0/C3 排序：**先共用管线，再补能力**）

| 阶段 | 内容 | 为什么在这个位置 |
|---|---|---|
| **Step 0** | 建 `FormatCapabilities`（**只记格式事实**，C1）；25 处特判改查表；生成器 API 改收能力值（I-3）；补 S14/S15 探针；探针入库 | 零行为变化的地基；表 ↔ README ↔ 测试同源由 I-4 生成 |
| **Step 1** | 消静默 + 诊断改造：D-6（3 处发射点全接）、D-1/D-2（先让丢失**可见**，`⏸`）、D-5、D-7a、D-3、D-4（消息区分 `⛔`/未实现，I-5） | 便宜、独立、立刻止住"静默丢数据"，且不依赖管线 |
| **Step 2** | **共用 emit 管线进 core**（R0 第 1 条）：形状遍历一份实现 + `IEmitPrimitives`；各生成器只留 wire 原语 | **这是 R0 的结构体现**。能力在此"一次实现、五格式受益" |
| **Step 3** | 能力落地（在管线上）：扩展集合 7 kind、YAML null（`- null`）、TOML/YAML 任意深度递归、S4×TOML、S10 全格式 | 全部由 Step 2 的管线承担，故必须在 Step 2 之后 |
| **Step 4** | 矩阵收敛：把 §6 每一格跑成 ✅/⛔，README 与 spec 同源更新 | 终态验收 |

> **v3 的错误顺序**：把「扩展集合全格式（10–15 Task）」放在管线之前，等于在 5 个生成器里各写一遍 28 条读侧构造，然后被 Step 2 的管线整体推翻（C3）。

### Step 0 验收（四件套）
1. **既有测试逐条不变绿** + 新增 I-2/I-3/I-4 测试（**不是**「测试数不变」）
2. **生成代码逐字节 diff 为空**（对入库探针语料前后各生成一次并 `git diff`）
3. **诊断集合逐行一致**（id + 消息 + 位置）—— **例外**：I-5 计划内的消息改动不算（见 N3）
4. **AOT publish 通过** + `dotnet csharpier check` 通过

基线命令：`dotnet test PicoSerDe.slnx -c Debug`。**规格与 README 都不写具体测试数字** —— 数字每次 PR 都变，过期数字比没有更糟；只记录命令与「0 failed」这一判据。

## 10. 附录：探针语料与表示性判定（**必须入库**）

### 10.1 现状探针
```
tests/FormatCapabilities.Probe/
  ├─ Models.cs            # 每格 ≥3 个样本（空/单值/边界）
  ├─ Probe.cs             # CELL|format|shape|verdict|wire|expected|actual
  └─ FormatCapabilities.Probe.csproj   # 5 运行时 + 5 生成器（Analyzer），不 NoWarn 诊断
```
```bash
cd tests/FormatCapabilities.Probe
dotnet build -m:1 -p:EmitCompilerGeneratedFiles=true -p:CompilerGeneratedFilesOutputPath=generated
./bin/Debug/net10.0/FormatCapabilities.Probe.exe
grep -oE "warning PICOSERDE[0-9]+: Member '[^']*'" build.log | sort -u
```

### 10.2 表示性判定（R1 的独立实现）
```bash
python - <<'PY'
import tomllib, yaml, json
toml_cases = {
  "S4 nested-object-array": 'V = [[{a = 1}], [{b = 2}]]',
  "S7 set":                 'V = [1, 2, 3]',
  "S8 set-as-element":      'V = [[1], [2, 3]]',
  "S10 dict-value-list":    'V = {a = [1, 2]}',
  "S11 recursion":          '[Child]\nName = "c"\n[Child.Child]\nName = "d"\n',
  "S12 null-in-array":      'V = ["a", null, "c"]',
  "S13 null-dict-value":    'V = {a = 1, b = null}',
}
for n, t in toml_cases.items():
    try: print(f"TOML {n:26s} OK   {json.dumps(tomllib.loads(t))}")
    except Exception: print(f"TOML {n:26s} REJECTED")
PY
```
**v3 的实测结论**：TOML 接受 S4/S7/S8/S10/S11，**拒绝** S12/S13（null）；PyYAML 接受全部含 null 的用例。

**独立复核**：三轮复核报告见 `.superpowers/sdd/spec/`（`design-review.md` / `evidence-review.md` / `review-v2.md`），均未提交入库（过程产物）。
