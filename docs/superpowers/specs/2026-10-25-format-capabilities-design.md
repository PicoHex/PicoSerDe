# PicoSerDe Format Capabilities Design Spec

**Status**: Draft v2 — pending maintainer rulings on D1/D2/D3 (§5)
**Date**: 2026-10-25
**Baseline**: `84a9560` (`main`, clean tree); matrix measured there
**Topic**: which shapes each of the five wire formats can represent, and how that
becomes one reviewable, testable table instead of 25 scattered format checks
**Contract**: `picohex-picoserde` (AOT / zero reflection / fail-loud) + `picohex-self-review`

---

## 0. 修订记录

| 版本 | 变更 | 依据 |
|---|---|---|
| v1 | 初稿：规则 R1–R3、实测矩阵、缺陷 D-1…D-4、决策 D1/D2/D3 | 55 格探针 |
| **v2** | 见下 | 两份独立复核 |

**v2 的修订来自两份独立复核**（报告：`.superpowers/sdd/spec/design-review.md`、`evidence-review.md`）：

| 修订 | v1 的错误 | v2 的结论 |
|---|---|---|
| R1 拆成 R1/R1′/R4 | R1 不可判定，且「禁止以未实现为理由」的推论**直接否定自己的 D3-A**（TOML/YAML 的表示性其实够，只有 INI 是扁平） | 表示性判据钉死为**独立合规实现**；代价问题单列为 R4 的 `⏸ deferred` |
| §4 根因重写 | 我写「`HashSet<T>` 落到 `"object"` 兜底分支」—— **实测是 `null`**（`Count`/`Comparer` 都是 get-only，被 `:189` 拒绝） | 真根因：`Resolve` 返回 `null` → **`PICOSERDE001` 被收集后从未上报**（D-6） |
| D-4 重写 | 我写「YAML 独有的递归不一致」—— 实测 **TOML/YAML/INI 三者行为一致**；且 TOML 还有更严重的根覆盖 | 拆为 D-4（诊断命名）+ D-5（TOML 根成员被覆盖） |
| D-3 拆分 | 五格式打包成一条缺陷 | JSON/MsgPack/TOML/YAML = 实现缺口；**INI = 格式边界**（扁平逗号串） |
| §6 修正 | `S8 × INI` 标 ✅ —— 与同表 `S3 × INI ⛔004` **数学矛盾** | 改为 ⛔，并加「达成阶段」列 |
| §5 补选项 | 决策表漏了元素级范围、读侧优先、统一降级、`N` 的具体值 | 补 D1-A′/D1-C、D2-C/D2-D、D3-C 与 N 提案 |
| §8 重写 | 用了仓库**不存在**的 patch/minor/major；且把最破坏的 D3-A 标成最轻 | 改为 `year.x.y` 两轴（API 变/不变 × 行为 无/收紧/放开） |
| §7 重写 | I-3 用 `grep` 防回归（可被无限绕过）；I-4 手抄 52 格必漂移 | I-3 改为**类型级禁止**；I-4 改为**生成式**；新增 I-6 值级保真 |
| §9 重写 | Step 0 验收「测试数不变」与「新增测试」自相矛盾；缺逐字节验证 | 四件套验收；D2-A 拆为独立 Step 2b |
| §10 补强 | 探针**未入库**（证据不可复现） | 探针必须入库 |
| §3 新增 S12/S13 | v1 完全没有**值级**行（成员在、值丢的情况不可见） | 新增两行并实测；S12 在 TOML 是 🐞（`V = ["a", "c"]`，null 消失），在 YAML 是 🐞（`V:~- a~- c`） |

---

## 1. 目的与非目标

### 目的
把「**哪种形状在哪种格式可表示**」从散落在 25 处 `if (formatTag == ...)` 的**隐式共识**，变成**一张可审阅、可测试、唯一的表**。

实测计数（两方独立复核一致）：`shared/Gen` 里含格式字面量的特判 **25 处** = `TypeKindResolver.cs` 12 + `GenInfrastructure.cs` 13；5 个生成器文件合计 **20,687 行**（含 `GlobalUsings.cs` 为 20,730）；同一 kind 分派重复 `case "int32"` **43** 次、`case "string"` **47** 次、`case "dict"` **30** 次。

### 非目标
- 不追求「所有格式支持所有形状」（§2 R1 明确否定）。
- 不解决 reader 的流式/resume 状态机差异（Step 3 的独立规格）。
- 不改变「写侧省略 null 成员」的既有设计（`D1b` 另行签字）。

## 2. 规范性规则

### R1 — 表示性（可判定）
> 一种形状在某格式**不可表示**，当且仅当**不存在**一种该格式的表示，能被该格式的**规范合规的独立实现**（如 PyYAML、`toml` crate、格式规范文本的合规 reader）往返成等价的值。

- 判据在**格式**，不在**我们的生成器**。「我方 reader 尚未实现」「实现代价太高」**不是**不可表示的理由。
- 因此 `HashSet<T>`、`List<List<int?>>`（YAML）、字典值为集合（JSON/MsgPack/YAML）都属**可表示**，它们的缺失是 §4 的缺陷。

### R1′ — 等价关系（每形状各不相同）
「忠实」不是一句话，而是**每形状一个等价关系**。缺失这张表会让 R1 自相矛盾（`HashSet` 无序，按「顺序保持」的字面定义反而应判不可表示）。

| 形状族 | 等价关系 | 备注 |
|---|---|---|
| sequence（`List`/`T[]`） | **有序**、等长、逐元素等价 | |
| set（`HashSet`/`ISet`） | **集合相等**（不看顺序） | 写侧顺序不保证，读侧不得依赖 |
| map（`Dictionary`） | **键值多重集相等**（键序不保证） | 现有实现写序=枚举序，但契约不承诺 |
| scalar（数值） | `(值, 文本精度)`：数值相等 **且** 写侧为该值的最短可往返文本 | `float` 不得经 `double` 中转；`decimal` 必须 invariant culture |
| scalar（时间） | `(值, Kind/offset, 精度)`：`DateTime.Kind` 必须保持；`DateTimeOffset` 保持 offset | |
| scalar（字符串） | 解码后码点序列相等（转义形式不限） | |
| 特殊浮点 | `NaN`/`±Inf`：仅在格式**有字面量**时可表示 | JSON **无** → 语言级 ⛔（见下例） |

> **R1 的标准示例（v1 缺失）**：`double.NaN` — JSON 无 NaN 字面量 ⇒ ⛔（**真正的**语言级不可表示）；TOML `nan`、YAML `.nan` ⇒ ✅。规格必须能用这种**无可争议**的例子自证，而不是用 D3（递归深度）这种可疑例子。

### R1″ — canonical write / lenient read
写侧每种概念只发**一种**规范形态；读侧必须接受该概念在格式规范内的**全部**合法形态。
（例：YAML null 写 `null`，读接受 `null`/`~`/`Null`/`NULL`/空；`PICOSERDE` 的诊断消息模板同样属公共契约。）

### R2 — 无静默
> 任何未进入生成代码的成员，**必须**产生一条**真的会出现在构建输出里**的诊断，id ∈ {`PICOSERDE001`,`002`,`003`,`004`,`005`}。

「收集了但不上报」等同于静默（当前 `001` 就是这种情况，见 D-6）。

### R3 — 不可表示 ≠ 不可编译
任何被判定为不可表示的形状，**不得**产出不可编译的生成代码（F5 review I-3 的教训）。

### R4 — 代价闸门（与 R1 严格分开）
> 表示**存在**、但实现超出当前预算 ⇒ 标 `⏸ deferred`：**必须**有诊断、**必须**写明 reopen 条件、**不得**出现在「不可表示」的 ⛔ 里。

`⏸` 与 `⛔` 的区别是本规格的关键：`⛔` 是格式事实，`⏸` 是我们的欠账。**D3（递归深度）只能进 `⏸`**，因为 TOML 表头 `[a.b.c.d]` 与 YAML 的 block 嵌套在表示性上都没有深度上限；真正的阻塞是**代码生成模型是静态 helper 链**，支持无界深度需要运行时深度循环（架构变更），不是「格式不支持」。

### R5 — 值级保真
成员进入生成代码**不等于**保真。`✅` 必须由**含边界值**的往返测试支撑：null 元素、空集合、单元素、`NaN`/`±Inf`、`decimal` invariant、`DateTime` 的 `Kind`、含分隔符/引号/换行的字符串、`int.MaxValue`。

## 3. 当前能力矩阵（实测，基线 `84a9560`）

图例：✅ 往返正确 ｜ ⛔ 不可表示（**构建期有诊断**）｜ 🐞 **无诊断 + 数据丢失** ｜ ⏸ 可表示但未实现 ｜ ⚠️ 条件性

> **证据**：55 格探针（每格 1 个样本值）。**独立复核复现了 54/55 格**；唯一不一致是 S11（v1 未定义是自递归还是互递归，见 §3.2）。

| # | 形状（成员 `V`） | JSON | MsgPack | TOML | YAML | INI |
|---|---|---|:---:|---|---|---|
| S1 | `List<int>` 扁平标量列表 | ✅ | ✅ | ✅ | ✅ | ✅ |
| S2 | `List<Dto>` 对象列表 | ✅ | ✅ | ✅ `[[Table]]` | ✅ | ⛔004 |
| S3 | `List<List<int>>` 嵌套标量列表 | ✅ | ✅ | ✅ `[[1,2],[3]]` | ✅ | ⛔004 |
| S4 | `List<List<Dto>>` 嵌套对象列表 | ✅ | ✅ | ⛔004 | ✅ | ⛔004 |
| S5 | `List<int?>` 可空叶子（扁平） | ✅ | ✅ | ⛔004 | ⏸004 | ⛔004 |
| S6 | `List<List<int?>>` 可空叶子（深层） | ✅ | ✅ | ⛔004 | ⏸004 | ⛔004 |
| S7 | `HashSet<int>` 作成员 | ✅ | 🐞 **D-1** | 🐞 **D-1** | 🐞 **D-1** | 🐞 **D-1** |
| S8 | `List<HashSet<int>>` 作元素 | ⛔004 | 🐞 **D-2** | 🐞 **D-2** | 🐞 **D-2** | 🐞 **D-2** |
| S9 | `Dictionary<string,int>` | ✅ | ✅ | ✅ | ✅ | ✅ |
| S10 | `Dictionary<string, List<int>>` | ⏸004 **D-3** | ⏸004 **D-3** | ⏸004 **D-3** | ⏸004 **D-3** | ⛔004（格式边界） |
| S11a | 递归：**自递归** `Node.Child: Node` | ✅ | ✅ | ⛔003 | ⛔003 | ⛔003 |
| S11b | 递归：**双类型互递归** `M1.Child: M2` | ✅ | ✅ | 🐞 **D-5**（根被覆盖） | ⏸003（1 层可用） | ⏸003（1 层可用） |
| S12 | `List<string?>`（**值级** null 元素） | ✅ | ✅ | 🐞 **D-7a** | 🐞 **D-7b** | ⛔004 |
| S13 | `Dictionary<string,int?>`（null 值） | ✅ | ✅ | ⛔004 | ⛔004 | ⛔004 |

### 3.1 S5/S6 × YAML 为什么是 `⏸` 而不是 `⛔`
YAML **有** null（`null`/`~`/空）。当前不支持是**库的选择**（写侧省略 null 成员、`YamlWriter` 缺 `WriteSequenceItem(int?)` 一类重载），符合 R4 的 `⏸`，**不符合** R1 的 `⛔`。

同样适用于 **S12 × YAML（D-7b）**：`List<string?>` 的成员被发射，但 null 元素在写侧被跳过 —— 这是**值级**丢失，R2 抓不到（成员进了生成代码），须由 R5/I-6 的边界值测试覆盖。

TOML 侧相反：TOML 1.0 **没有** null 字面量 ⇒ `List<string?>` 本身应是**类型级 ⛔004**（与 S5/S6 口径一致）；今天是 🐞，即「该响亮丢弃却静默丢值」（D-7a）。

### 3.2 S11 必须区分两种递归
v1 只写「2 层 `Node.Child.Child`」，导致同一格两种答案：

| 写法 | YAML/TOML/INI 实测 |
|---|---|
| 自递归 | **第 1 层就丢**（输出只有 `K: root`），`PICOSERDE003` 指向该成员 |
| 双类型互递归 | **1 层可用**（写出 `Child:` 子块），更深才丢；TOML 还会**覆盖根成员**（D-5） |

## 4. 缺陷清单

> 分三类：**静默丢失**（违反 R2，最高危）、**数据损坏**（违反 R1′）、**实现缺口**（违反 R1，但有诊断）。

| ID | 类别 | 缺陷 | 证据 |
|---|---|---|---|
| **D-1** | 静默丢失 | `HashSet<T>` 家族作**成员**：MsgPack/TOML/YAML/INI **无任何诊断**且成员消失 | 四格式输出只有 `K`；日志 0 条 001/003/004；JSON 正常（`{"K":"k","V":[1,2,3]}`） |
| **D-2** | 静默丢失 | 同上作**元素**（`List<HashSet<int>>`）：四格式静默；JSON 至少报 004 | 同上 |
| **D-6** | **静默丢失的根因** | `PICOSERDE001`（`Unsupported type skipped during source generation`）**被收集后从未上报**：`GenInfrastructure.cs:1184-1194` 把它 `diagnostics?.Add(...)`，而 **5 个生成器都不传 `diagnostics` 列表**，也没有任何代码把该列表交给 `SourceProductionContext` | 构建日志中 `PICOSERDE001` **0 次**；`grep "diagnostics:" Pico*/src/*.Gen/` **无命中** |
| **D-5** | **数据损坏** | TOML 互递归往返**覆盖根成员**：写 `K = "root"` + `[Child] K = "c1"`，读回 **`K = "c1"`** | 实测 `CELL\|toml\|S11-mutual\|...\|K=c1\|C=c1\|CC=null` |
| **D-7a** | 值级静默丢失 | **TOML**：`List<string?>` 的 null 元素被**静默跳过**（成员在、值丢、无诊断） | 实测 wire：`V = ["a", "c"]`（null 消失）；期望按 S5/S6 的既有口径**类型级 ⛔004** |
| **D-7b** | 值级静默丢失 | **YAML**：同上，`V:` 块序列只剩非 null 项 | 实测 wire：`V:~- a~- c`；YAML 有 null ⇒ 按 R1 应**支持**（并入 D1-A） |
| **D-3** | 实现缺口 | `Dictionary<string, List<T>>` 在 JSON/MsgPack **可表示**却丢弃（有 004，不是静默） | 五格全 004；JSON 的 `{"a":[1,2]}`、MsgPack 的 map→array、TOML/YAML 的表数组均可表达 |
| **D-4** | 诊断措辞 | 递归诊断指向**内层类型的成员**（`Member 'X.Y?.Child'`），而用户写的是 `X.Child`；且「1 层碰巧可用」未在任何文档声明 | 三格式一致；`PICOSERDE003` 有发射 |

**关于 D-3 的 INI 例外**：INI 的列表编码是**扁平逗号串**（`V = 1,2,3`），表达「字典值为列表」需要引入二维扁平约定 —— 是否仍算忠实编码取决于是否允许自定义转义，属可争论的**格式边界**，因此 INI 那一格标 `⛔`，不与其余四格并论。

## 5. 待决事项（需签字）

### D1 — YAML 序列里的 `null`

| 选项 | 范围 | 代价 | 版本影响 |
|---|---|---|---|
| **D1-A** | 全部粒度：`List<int?>`、`List<List<int?>>`、`List<string?>`、`List<Dto?>`、`Dictionary<string,int?>` | 5–6 Task（S6 还需内层序列 + F5 第四态） | `x`（行为放开） |
| **D1-A′** | 仅元素级 nullable **值类型**（S5/S6），字符串/对象 null 另议 | 3 Task | `x` |
| D1-B | 保持现状（⏸ + 004） | 0 | 无 |
| **D1-C** | 在矩阵**理由栏**明确 TOML/INI 的 ⛔ 是**语言级**（TOML 1.0 无 null 字面量、INI 无 null），与 YAML 的 `⏸` 区分 | 0（文档） | 无 |

> **D1-写侧形态**：`- null`（显式）。**不可**用裸 `-`：它已被 F5 占用为「嵌套序列项」（R1″ canonical write 要求写侧唯一）。

### D2 — 扩展集合（`hashset`/`queue`/`stack`/`linkedlist`/`immutablearray`/`memory`/`readonlymemory`）

| 选项 | 范围 | 代价 | 版本影响 |
|---|---|---|---|
| D2-A | **全格式 × 全形态**（成员 + 元素 + 嵌套元素 + 多态 + 流式 + **匿名类型**） | **10–15 Task**：emit 代码**只存在于 JSON 生成器**，需要 7 kind × 4 格式的容器构造，再乘 6 条发射路径（匿名类型是第 6 个 surface，走 `PICOSERDE005`） | `x` |
| **D2-B（先行止血）** | 非 JSON 格式**报诊断**（接上 D-6 的上报器即可） | 1 Task | `y` + 新增警告的 `NoWarn` 说明 |
| D2-C | 分步：先读侧构造（写侧走现有 list 发射） | 6–8 Task | `x` |
| D2-D | 统一降级：连 JSON 也 ⛔ | 1 Task | `y`（**违反 R1，仅列出**） |

> **D2-A 的顺序陷阱**：`Stack<T>` 枚举 top→bottom，读侧若按 `Push` 逐项构造会**反转顺序** —— 必须逐类型声明读侧语义，否则引入「静默顺序错误」（比丢成员更难发现）。`Memory<T>`/`ReadOnlyMemory<T>` 还涉及 span 生命周期与可写性，建议排除。

### D3 — section 格式的递归深度

| 选项 | 内容 | 代价 | 版本影响 |
|---|---|---|---|
| **D3-A** | 统一丢弃（自递归与互递归**都从第 0 层拦**），修 D-4 的命名，修 D-5 的覆盖 | 2–3 Task | **`x`**（移除 YAML/INI 现有的 1 层能力）+ 迁移说明 |
| D3-B(N) | 支持固定深度：TOML **N=1**、YAML **N=1**（即把"碰巧可用"正式化）、INI **N=0** | 3–4 Task | `x` |
| D3-C | YAML 支持任意深度（表示性够），TOML/INI 受限 | **架构变更**：静态 helper 链 → 运行时深度循环 | `x` + 大版本说明 |

> **v1 的错误**：把 D3-A 的理由写成「格式固定层级、无法忠实编码（R1）」。按 R1 这只对 INI 成立。正确的归类是 **R4 的 `⏸`**（表示存在、实现未做），reopen 条件 = 「Step 3 的 emit 模型支持运行时深度」。

### D4（新增，需签字）— 诊断 id 是否作为公共契约
`PICOSERDE001`…`005` 已被用户用于 `NoWarn`。是否承诺 **id 与消息模板**在 `year` 内稳定？

## 6. 目标矩阵（D1-A + D2-A + D3-A 生效后）

| # | 形状 | JSON | MsgPack | TOML | YAML | INI | 达成阶段 |
|---|---|---|:---:|---|---|---|---|
| S1 | `List<int>` | ✅ | ✅ | ✅ | ✅ | ✅ | 已有 |
| S2 | `List<Dto>` | ✅ | ✅ | ✅ | ✅ | ⛔004 | 已有 / INI 永久 |
| S3 | `List<List<int>>` | ✅ | ✅ | ✅ | ✅ | ⛔004 | 已有 / INI 永久 |
| S4 | `List<List<Dto>>` | ✅ | ✅ | ⛔004 | ✅ | ⛔004 | TOML/INI 永久 |
| S5 | `List<int?>` | ✅ | ✅ | ⛔004 | ✅ | ⛔004 | Step 2 |
| S6 | `List<List<int?>>` | ✅ | ✅ | ⛔004 | ✅ | ⛔004 | Step 2 |
| S7 | `HashSet<int>` 成员 | ✅ | ✅ | ✅ | ✅ | ✅ | Step 2b |
| S8 | `List<HashSet<int>>` 元素 | ✅ | ✅ | ✅ | ✅ | **⛔004** | Step 2b（**INI 不可能**：严格宽于 S3） |
| S9 | `Dictionary<string,int>` | ✅ | ✅ | ✅ | ✅ | ✅ | 已有 |
| S10 | `Dictionary<string,List<int>>` | ✅ | ✅ | ✅ | ✅ | ⛔004 | Step 1（D-3） |
| S11a | 自递归 | ✅ | ✅ | ⛔003 | ⛔003 | ⛔003 | Step 2c |
| S11b | 互递归 | ✅ | ✅ | ⛔003 | ⛔003 | ⛔003 | Step 2c（**不再有 1 层特例**） |
| S12 | `List<string?>` 值级 null | ✅ | ✅ | ⛔004（D-7a：改为类型级丢弃） | ✅（D-7b） | ⛔004 | Step 1（TOML）/ Step 2（YAML） |
| S13 | `Dictionary<string,int?>` | ✅ | ✅ | ⛔004 | ✅ | ⛔004 | Step 2 |

**Step 1 完成后**（仅消静默）的中间态：S7/S8 四格式 = `⏸004`（不再是 🐞）；S10 四格式转 ✅。**验收按阶段判定，不按本节最终态。**

## 7. 不变式（可测试）

| ID | 不变式 | 强制方式 | 生效阶段 |
|---|---|---|---|
| **I-1** | 任何未进入生成代码的成员产生诊断，id ∈ {001…005} | 生成器驱动测试：对每个 ⛔/⏸ 形状断言诊断出现**且真的在 `GetRunResult().Diagnostics` 里**（直接覆死 D-6） | Step 1 |
| **I-2** | 每个 ⛔/⏸ 形状不得产出不可编译代码 | 编译级测试（`CSharpCompilation.Emit` 无 error） | Step 0 |
| **I-3** | 能力表是唯一判据 | **类型级禁止**：生成器 API 不再接收 `string formatTag`，只接收 `FormatCapabilities` 值 —— 拿不到字符串就写不出 `formatTag == "json"`。再加一条 Roslyn 架构测试扫描 `shared/Gen` 语法树，断言除能力文件外不出现格式名字面量比较 | Step 0 |
| **I-4** | 每个 ✅ 有往返测试，逐格映射矩阵 | **生成式**：能力表每格带 `SampleType`，测试与 README 矩阵**都从表生成**（而非手抄 52 格） | Step 0 |
| **I-5** | 诊断消息含成员名 + 格式 + 形状；模板属公共契约 | 断言消息片段 | Step 0 |
| **I-6** | ✅ 必须由**边界值样本**支撑（R5：null 元素、空集合、单元素、NaN/±Inf、decimal invariant、`DateTime.Kind`、含分隔符字符串、`int.MaxValue`） | 数据驱动的多值探针 | Step 2 |

## 8. 兼容性政策

仓库版本是 **`<year>.<x>.<y>`**：**公共 API 变化（含纯新增）→ `x+1`**；**公共 API 不变 → `y+1`**。没有 patch/minor/major。

| 变更 | 公共 API | 行为 | 版本 |
|---|---|---|---|
| D-6（接通 001 上报） | 不变 | 新增诊断（**可让 `TreatWarningsAsErrors` 的下游构建失败**） | `y+1` **+ 强制发布说明** |
| D2-B（非 JSON 报诊断） | 不变 | 同上 | `y+1` + 同上 |
| D-1/D-2/D-5（D2-B 生效后） | 不变 | 成员/值不再丢失 | `y+1`（加性） |
| **D-7a**（TOML 改为类型级丢弃） | 不变 | **收紧**：成员从「在但丢值」变为「不在 + 诊断」 | `x+1` |
| **D-7b**（YAML 支持 null 元素） | 不变 | 放开 | `x+1` |
| **D3-A**（移除 YAML/INI 1 层递归） | 不变 | **收紧（唯一破坏性）** | **`x+1` + 迁移说明** |
| D1-A / D2-A / D-3 / D3-B/C | 不变 | 放开（输出新增键） | `x+1` |
| `FormatCapabilities` 若定为 **public** | **变** | — | `x+1` 且更新 `api/*.public.txt` |

**必须写进 §8 的两条政策**：
1. **新增诊断必须附 `NoWarn` opt-out 说明**（否则 `warnaserror` 用户升级即挂）。
2. **诊断 id + 消息模板是公共契约**（用户 `NoWarn` 依赖它），变更同样计版本。
3. 除 `FormatCapabilities` 外，本批**不改** `api/*.public.txt`；禁止「每项都更新基线」（会让发布脚本误判为 `x` bump）。

## 9. 实施阶段

| 阶段 | 内容 | 风险 | 验收 |
|---|---|---|---|
| **Step 0** | 建 `FormatCapabilities`（含每格 `SampleType`）；25 处特判改查表；生成器 API 改收能力值（I-3）；测试/README 矩阵改为生成式（I-4） | ≈0（零行为变化） | **四件套**（见下） |
| **Step 1** | 消静默：D-6（接通 `PICOSERDE001` 上报）、D-1/D-2、D-5、D-7、D-4 措辞；**D-3 也在此**（它只是 dict-value 发射） | 低 | 矩阵无 🐞；每个 ⏸/⛔ 有诊断断言 |
| **Step 2** | 能力放开（YAML/Toml 系）：D1-A、S12/S13 | 中 | §6 的 Step 2 行转 ✅ |
| **Step 2b** | D2-A 扩展集合（**10–15 Task，独立可裁**） | 中高 | S7/S8 全格式 ✅（除 INI 的 S8） |
| **Step 2c** | D3 决策落地（A/B/C） | 中（A 为破坏性） | S11a/S11b 行为一致 |
| **Step 3（独立 spec）** | emit 骨架上提 core：统一 `IEmitPrimitives`，5 个生成器只留 wire 原语；reader 侧**不在此列**（resume 状态机差异大） | 高 | 独立规格与计划 |

### Step 0 验收（四件套，全部必须过）
1. **既有测试逐条不变绿** + 新增 I-3/I-4 测试（**不是**「测试数不变」—— v1 此处自相矛盾）
2. **生成代码逐字节 diff 为空**：对入库探针语料（§10）在 Step 0 前后各生成一次并 `git diff`
3. **诊断集合逐行一致**（id + 消息 + 位置）
4. **AOT publish 通过** + `dotnet csharpier check` 通过

基线命令与数字（本机实测，须在 PR 里复现）：`dotnet test PicoSerDe.slnx -c Debug` → **1826 passed / 0 failed**（`README` 记的 1698 已过期，需同步）。

## 10. 附录：探针语料（**必须入库**）

v1 把探针删了，只留「重建约 30 分钟」—— 违反 fail-loud 精神：**证据必须可复现**。v2 要求入库：

```
tests/FormatCapabilities.Probe/          # 55+ 类型，每格 ≥3 个样本值（空/单值/边界）
  ├─ Models.cs            # [Pico*Serializable] partial class，按 (format, shapeId) 命名
  ├─ Probe.cs             # 输出 CELL|format|shape|verdict|wire|expected|actual
  └─ MatrixProbe.csproj   # 5 运行时 + 5 生成器（OutputItemType=Analyzer），不 NoWarn 诊断
```

复现命令：

```bash
cd tests/FormatCapabilities.Probe
dotnet build -m:1 -p:EmitCompilerGeneratedFiles=true -p:CompilerGeneratedFilesOutputPath=generated
./bin/Debug/net10.0/FormatCapabilities.Probe.exe          # CELL| 行
grep -oE "warning PICOSERDE[0-9]+: Member '[^']*'" build.log | sort -u
```

**独立复核结论**（`evidence-review.md`）：54/55 格复现一致；唯一差异为 S11 的形状歧义（§3.2），已在 v2 中拆分。
