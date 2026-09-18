# PicoSerDe 代码库 Bug 审核报告

- **审核日期**: 2026-08-16
- **审核基线**: commit `74a91fd` (main, working tree clean)
- **审核范围**: PicoSerDe.Core / PicoJetson / PicoIni / PicoToml / PicoYaml / PicoMsgPack 的 reader / writer / serializer / source generator
- **测试基线**: 9 个测试项目共 **1110 个测试全部通过**（PicoJetson.Unit 213、PicoJetson.Integration 203、PicoMsgPack 145、PicoYaml 130、PicoIni 125、PicoToml 120、Integration 71、PicoJetson.Functional 61、Core 42）

> 说明：测试全绿不意味着没有 bug。本报告所有结论均通过可复现的输入在**全新编译的二进制**上验证（早期几轮探测曾链接到陈旧二进制，已通过 `rm -rf bin/obj` 全量重建排除干扰）。

---

## 结论摘要

| 严重度 | 数量 | 性质 |
|---|---|---|
| P0（崩溃/未处理异常） | 3 | YamlReader、IniReader、TomlReader 在合法/畸形输入上抛 `ArgumentOutOfRangeException` |
| P1（功能正确性） | 1 | `JsonOptions.Current` 为 `[ThreadStatic]` 但被 async 流式路径跨 `await` 使用 → 选项丢失 + 请求间串扰（5 种格式均受影响） |
| P2（数据完整性/严格性） | 6 | 数字 fast-path 溢出回绕、`int.MinValue` 被拒、非法 JSON 转义/前导逗号/缺失值被静默接受、ArrayPool 双重归还 |
| P3（健壮性/性能/卫生） | 4 | 固定 32 字节数字缓冲、O(n²) 流式重解析、构建告警、代码卫生 |

**最值得警惕**：YAML/INI/TOML 三个文本解析器存在**同一类越界 bug**——key 扫描不按行边界停、扫描后无条件 `_position++`，导致 `_position` 越过缓冲区末尾后做 `_data[start.._position]` 切片直接抛 `ArgumentOutOfRangeException`（而非 `FormatException`）。这是系统性的解析器健壮性缺陷。

---

## P0 — 崩溃类（未处理异常，合法输入可触发）

### BUG-01 [P0] YamlReader：无冒号的行使 `_position` 越过缓冲区末尾 → ArgumentOutOfRangeException

- **文件**: `PicoYaml/src/PicoYaml/YamlReader.cs`
- **复现**（全部为合法 YAML 或至少应为 `FormatException` 的输入，实际抛 `ArgumentOutOfRangeException`）:
  - `hello`、`12345`、`null`、`true`、`~`、`[1, 2]`、`[2147483648]`（顶层标量/流序列）
  - `key: value\norphan\n`（映射文档中夹一条无冒号的行）
- **根因**（用插桩副本验证）:
  ```csharp
  // 行 597-601
  int ks = _position;
  while (_position < _data.Length && _data[_position] != (byte)':')   // ← 不按 \n/\r 停
      _position++;
  _keySpan = TrimEnd(_data[ks.._position]);
  _position++;                                                          // ← 无条件 +1，越界
  ```
  对输入 `12345\n`（len=6）：key 扫描一路走到 `_position=6`（`\n` 不是 `:`，被吞掉），`_position++` → **7 > 6**；随后第 849 行 `_valueSpan = Trim(_data[vs2.._position])` 以 `_position > _data.Length` 做切片 → `ArgumentOutOfRangeException`。
- **影响**: 任何以纯标量/流序列为顶层或文档内出现无冒号行的 YAML 都会崩溃；异常类型错误（应为 `FormatException`），且属于**未处理异常**，用户无法按解析错误捕获。测试未覆盖此路径（YAML 序列化器只产出 `key: value` 映射形态）。

### BUG-02 [P0] IniReader：无 `=` 的行 → ArgumentOutOfRangeException

- **文件**: `PicoIni/src/PicoIni/IniReader.cs`
- **复现**: `x`、`]`、`{`、`}`、`"unterminated` 等
- **根因**: 与 BUG-01 完全同型。行 352-357：
  ```csharp
  while (_position < _data.Length && _data[_position] != (byte)'=') _position++;
  _currentValue = TrimEnd(_data[keyStart.._position]);
  _position++;                    // ← 无 '=' 时越界
  ```
  随后行 402 `var raw = _data[valStart.._position]` 因 `_position > _data.Length` 抛 `ArgumentOutOfRangeException`（实测栈：`ReadKeyValueSpan` → line 402）。

### BUG-03 [P0] TomlReader：控制字符/未闭合引号 → ArgumentOutOfRangeException

- **文件**: `PicoToml/src/PicoToml/TomlReader.cs`（`ReadValueSpan` 行 759，调用方 `ReadKeyValueSpan` 行 665）
- **复现**: `\x00`、`\x01\x02`、`"a\x01b"`、`"unterminated`（未闭合引号）
- **根因**: 同一越界模式——前序解析把 `_position` 推进到 `_data.Length` 之外，行 759 `_valueSpan = _data[vs.._position]` 抛异常。
- **影响**: 合法 TOML 中**禁止**控制字符，因此这属于畸形输入应当抛 `FormatException` 却抛内部异常的问题；`"unterminated`（未闭合字符串）更接近真实误用场景。

---

## P1 — 功能正确性

### BUG-04 [P1] `JsonOptions.Current`（[ThreadStatic]）在 async 流式反序列化中丢失，并造成请求间串扰

- **文件**: `PicoJetson/src/PicoJetson/JsonOptions.cs:190`（`[ThreadStatic] public static JsonOptions? Current`）；`JsonSerializer.DeserializeFromStreamAsync`（行 ~206-260）与 `DeserializeAsyncEnumerableImpl`（行 ~360+）在设置 `Current` 后存在 `await`。
- **根因**: `[ThreadStatic]` 不随 `ExecutionContext`/async 延续流动。设置 `Current` 后第一个 `await` 把延续调度到另一线程，该线程的 slot 是 `null` 或**上一次请求遗留的旧值**。
- **复现**（实测）: 传 `AllowTrailingCommas = true` 经 `DeserializeFromStreamAsync`，用强制换线程的流（`await Task.Delay`）——调用线程 2 设置选项，延续在线程 5 执行，流式委托内观察到 `JsonOptions.Current?.AllowTrailingCommas == false`。
- **影响**:
  1. async 流式路径上 `AllowTrailingCommas` / `UnmappedMemberHandling` / `PropertyNameCaseInsensitive` / `NumberHandling` / `ReadCommentHandling` / `MaxDepth` / `Indented` 等选项**静默失效**（生成代码和 reader 均读 `JsonOptions.Current`）。
  2. `finally { JsonOptions.Current = prev; }` 在延续线程上写回，可能**覆盖同线程另一个请求的选项** → 跨请求数据污染（例如 A 请求的严格反序列化选项被 B 请求遗留的宽松选项替换）。
- **范围**: 5 种格式全部同构——`PicoIni.IniOptions.cs:22`、`PicoMsgPack.MsgPackOptions.cs:14`、`PicoToml.TomlOptions.cs:14`、`PicoYaml.YamlOptions.cs:14`，且均有 `DeserializeFromStreamAsync`。
- **修复建议**: 改用 `AsyncLocal<JsonOptions>`（携带选项对象而非依赖堆栈恢复），或显式把选项经参数/上下文传入生成代码与 reader；流式路径绝不能在 `await` 之后依赖线程局部状态。

---

## P2 — 数据完整性 / 严格性

### BUG-05 [P2] TomlReader.TryReadNextInt32Span 整数溢出**静默回绕**返回错误值

- **文件**: `PicoToml/src/PicoToml/TomlReader.cs` 行 256-297
- **复现**（实测）: 输入 `99999999999` → `ok=True, v=1215752191`（真实值超出 int32，应返回 false / 抛错，却返回回绕值并报告成功）。
- **根因**: `result = result * 10 + digit` 无溢出检查（默认 unchecked 回绕）。与 JsonReader 同功能路径（有 `> int.MaxValue` 检查、安全返回 false）不一致。
- **影响**: 手工 converter / 公共 API 使用者会拿到**静默损坏的整数**。`TryReadNextInt32Seq` 路径经 `Utf8Parser` 相对安全，仅 Span 路径受影响。

### BUG-06 [P2] YamlReader.TryReadInt32ArrayFast / TryReadInt64ArrayFast 无溢出检查（静默回绕）

- **文件**: `PicoYaml/src/PicoYaml/YamlReader.cs` 行 2145-2215
- **根因**: `v = v * 10 + (b - '0')` 无溢出防护，`[2147483648]` 之类会回绕写入 `dest`。JsonReader 的同名 fast-path 有 `(int.MaxValue - digit)/10` 检查并返回 0 走回退，YAML 版缺失。
- **说明**: 因 BUG-01 的存在，该 fast-path 对含大数数组的输入在 `Read()` 阶段就崩溃，回绕属于被掩盖的潜在缺陷；修复 BUG-01 后此路径可达，需同步加检查。

### BUG-07 [P2] JsonReader.TryReadNextInt32Span 拒绝合法值 `-2147483648`（int.MinValue）

- **文件**: `PicoJetson/src/PicoJetson/JsonReader.cs` 行 480-525
- **复现**（实测）: `[ -2147483648 ]` → `TryReadNextInt32` 返回 `false`。因溢出检查 `result > int.MaxValue` 在符号位取反**之前**生效，`2147483648` 先被拒绝。
- **影响**: 公共 API 边界值 bug（int.MinValue 是合法 int32）。`TryReadInt32ArrayFast`（行 1379 起）同理——对 `-2147483648` 返回 0 走回退，功能尚可但性能异常；若调用方未实现回退则数据丢失。

### BUG-08 [P2] JsonReader 严格模式接受前导逗号

- **文件**: `PicoJetson/src/PicoJetson/JsonReader.cs` `Read()` 中逗号分支
- **复现**（实测）: `,5` 被解析为 `5`；`[,1]` 被解析为 `[1]`（`{,}` 则被 "trailing comma before closing bracket" 检查拒绝，不受影响）。
- **根因**: 逗号分支无条件吞掉当前位置的 `,`，不校验"此处是否应当出现逗号"。`[1,,2]` 会抛错（第二个逗号落入 switch default），但**前导逗号**被静默接受。
- **影响**: 严格模式（RFC 8259）下应拒绝的输入被接受，与 `AllowTrailingCommas=false` 的语义不一致。

### BUG-09 [P2] JsonReader 接受属性缺值 `{"a"}` 与非法转义 `"\x41"`

- **文件**: `PicoJetson/src/PicoJetson/JsonReader.cs` `ReadStringOrPropertySpan/Seq` 与 `Read()` 结构
- **复现**（实测）:
  - `{"a"}` → 正常读完，无任何错误（严格 JSON 应在属性名后期待值）。
  - `"\x41"` → 解码为 `x41` 静默通过（RFC 8259 只允许 `"\/bfnrtu`，`\x` 必须报错）。
- **影响**: 前者是解析器结构不跟踪"属性名后必须跟值"；后者会导致**互操作数据损坏**（例如 MSJSON 的 `\x41` 语义是 `A`，本库读出 `x41`）——读写的语义不对称。

### BUG-10 [P2] JsonReader.ReadStringOrPropertySeq 增长时对 ArrayPool 双重归还（潜在内存破坏）

- **文件**: `PicoJetson/src/PicoJetson/JsonReader.cs` 行 741-743、776-778、789-791
- **根因**: 字符串超过 256 字节时：
  ```csharp
  var newBuf = ArrayPool<byte>.Shared.Rent(buf.Length * 2);
  buf.AsSpan(0, di).CopyTo(newBuf);
  ArrayPool<byte>.Shared.Return(buf);   // ← 直接归还
  buf = newBuf;
  TrackBuffer(buf);                     // ← 旧 buf 仍登记在 _rb* 槽中
  ```
  旧缓冲仍留在跟踪槽里，`Dispose()` 时 `ReturnBuf` 会**再次归还同一数组** → 双重归还。池可能把同一数组同时租给两个并发 reader → 数据竞争/内存破坏。
- **实测**: 并发压测 16000 次未复现损坏（池桶饱和时第二次归还被丢弃，掩盖了问题）；属静态可证、触发条件依赖池压力的**潜伏缺陷**。内部 `LeakedBufferCount` 计数因归还/登记计数对称，Dispose 后归零，无法暴露该问题。

---

## P3 — 健壮性 / 性能 / 卫生

### BUG-11 [P3] JsonReader.ReadNumberSeq 固定 32 字节缓冲 → 长数字抛 IndexOutOfRangeException

- **文件**: `PicoJetson/src/PicoJetson/JsonReader.cs` 行 1086（`ArrayPool.Rent(32)`）
- **复现**（实测）: seq 模式下 60 位数字 → `IndexOutOfRangeException`（应为 `FormatException` 或正常解析）。JSON 数字无长度上限，`0.000…0001` 类输入可轻易触发。
- **影响**: 异常类型错误 + 潜在 DoS 向量（异常而非优雅失败）；Span 路径无此问题（直接切片）。

### BUG-12 [P3] JsonSerializer.DeserializeFromStreamAsync 每个 chunk 从头重解析累计缓冲 → O(n²)

- **文件**: `PicoJetson/src/PicoJetson/JsonSerializer.cs`（注释亦自述 "re-parse from the beginning on every attempt"）
- **影响**: 大流（MB 级）性能退化；TOML/YAML/INI/MsgPack 的流式路径（`TomlReaderState` 暂停/续传）则无此问题——JSON 是唯一重复解析的实现，设计不一致。

### BUG-13 [P3] 构建告警（全量重建 `--no-incremental` 实测 35 条，Debug 与 `PublishAot=true` 结果相同）

- `PICO*004` × 20（5 种格式各 4 条：src / benchmarks / samples / tests 各 1 条）：匿名类型序列化需 `AllowUnsafeBlocks`。benchmark/sample/test 属卫生问题；src 项目自身也会发出（src 源码并无匿名类型序列化，疑似生成器对宿主编译的误触发诊断），值得在生成器侧排查。
- `IL2026` × 8 + `IL3050` × 2（TomlCrossValidationTests）：测试用 Tomlyn 反射序列化与 TUnit 结构比较，与 AOT 目标冲突（测试专用，可接受但建议注明）。
- `CS8602` × 2（StrictPolyAndOptionsTests.cs:194,200）：测试代码可能空引用解引用。
- `CS8619` × 2（生成的 `PicoJetson_Tests_StrictModel_JsonSerializer.g.cs:241,425`）：生成代码 `List<string>` 与目标 `List<string?>` 空性不匹配——**生成器对可空元素类型的空性标注有缺陷**，值得单独排查。
- `CS0162` × 1（MsgPackReader.cs:347）：`PeekTokenLength` 外层 switch 覆盖全部字节值，default 分支不可达。

### BUG-14 [P3] 同步 API 的 ThreadStatic 共享 writer 不可重入（已文档化，但易踩坑）

- `SerializerExtensions.RentWriter()`：在 `Serialize<T>` 回调里嵌套 `Serialize<U>` 会清空共享缓冲导致损坏。注释已声明，属已知限制；建议 API 文档/XML 注释再加粗提示。

---

## 未发现问题（抽查通过）

- **SimdHelpers**（SkipWhitespace / SkipSpacesAndTabs）：SIMD 位掩码 + `TrailingZeroCount(~bits)` 逻辑正确，含 512/256/128 三级回退。
- **JsonWriter** 逗号逻辑（`_needsComma` 位掩码 + `_afterPropertyName`）：近期 long-string 逗号 bug 的修复正确；`WriteQuotedString` 的转义预分配上限（+5/字符）充足。
- **MsgPackReader**：`PeekTokenLength`/元素计数/深度（固定 MaxDepth=64）结构合理，`RentBuf` 单缓冲跟踪无泄漏路径。
- **Naming policies**（Camel/Snake/Kebab）：边界处理（全大写、单字母）逻辑正确。
- **NumberHandling**（NaN/Infinity）：写侧拒绝、读侧按选项放行，行为一致。

---

## 修复优先级建议

1. **立即**（P0，一行级修复）：为 YAML/INI/TOML 三个 reader 的 key 扫描补 `\n`/`\r` 停靠 + 对"无分隔符行"抛 `FormatException`（而非越界）。
2. **本周**（P1）：`JsonOptions.Current` 改为 `AsyncLocal` 或为流式路径显式传递选项；同步补 async 选项传递的测试。
3. **本月**（P2）：TOML/YAML fast-path 溢出检查；`int.MinValue` 边界；JSON 严格模式（前导逗号/缺值/非法转义）；seq 字符串双归还；`ReadNumberSeq` 加缓冲增长（P3，与 P2 一并处理）。
4. **持续**（P3）：CS8619 生成器空性、流式 O(n²)、MsgPackReader 不可达代码（CS0162）、测试告警清理。

## 验证方式说明

- 所有"实测"结论均通过 `scratch/` 下的临时控制台程序（已清理）在 `dotnet run`（Debug，`PublishAot=false`）全新建产物上验证。
- 测试基线：`dotnet run --project <TestProject>.csproj -p:PublishAot=false` 逐个项目运行（`dotnet test` 在该仓库存在 TUnit 测试发现漂移问题，不可靠路径，详见 tunit-runner skill）。

---

## 修复状态更新（2026-09-14，发布 v2026.9.14）

### 本报告遗留条目

| 条目 | 状态 | 说明 |
|---|---|---|
| BUG-01 / 02 / 03（P0 越界） | ✅ 已修 | YAML/INI/TOML 的 key 扫描已按行边界停靠（在本次审核基线之前完成） |
| BUG-04（P1 `ThreadStatic` options） | ✅ 已修 | 环境态 options 已移除，options 显式贯穿 reader/writer 与生成代码 |
| BUG-05（P2 TOML 溢出回绕） | ⚠️ 部分修复 | fast array 路径已加溢出守卫（`ed0c0fc`）；`TryReadNextInt32Span` 仍未加守卫（未找到经公共 API 可达的调用形态） |
| BUG-06（P2 YAML fast array 溢出） | ✅ 已修 | `7c31df5`，并复活了被禁用的 fast-path 测试（`SetRawPos`） |
| BUG-07（P2 int.MinValue 被拒） | ✅ 已修 | `ac81dca`；`-2147483648` 现在被接受 |
| BUG-08（P2 前导逗号） | ✅ 已修 | `388e1d7`；`,5` / `[,1]` 抛 `FormatException` |
| BUG-09（P2 非法转义/缺值） | ✅ 已修 | `ca8d28a`；未知转义（如 `\q`）抛错，`\b`/`\f` 正确解码 |
| BUG-10（P2 ArrayPool 双重归还） | ✅ 已修 | `76c0964`；扩容不再提前归还，`TotalPoolReturns == TotalTrackedBuffers` 测试锁定 |
| BUG-11（P3 seq 长数字 32 字节缓冲） | ❌ 仍开放 | `JsonReader.ReadNumberSeq` 仍固定 `Rent(32)` 且无增长检查，超长数字在 sequence 模式抛 `IndexOutOfRangeException` |
| BUG-12（P3 JSON 流式 O(n²)） | ⚠️ 部分缓解 | 流式已改为 reader 状态续传 + `partial` 结果保留（`StreamingFunc`/状态导出）；O(n²) 性能未重新测量 |
| BUG-13（P3 构建告警） | ✅ 已修 | `76d29f8`；全量构建 **0 warning / 0 error** |
| BUG-14（P3 ThreadStatic writer 不可重入） | 📝 已文档化 | `RentWriter` 注释保留；未改行为 |

### 2026-09-14 代码审核新增修复（同批发布）

- **P1**：顶层 `Nullable<T>`/标量目标不再生成不可编译代码（改为快速失败 `PICO…` 语义）；匿名类型整数族（`uint`/`byte`/`char`…）不再使生成器崩溃（原 CS8785 会毁掉整个编译的生成器输出）；INI/TOML/YAML 数值 I/O 全链路 culture-invariant。
- **P2**：INI 列表按元素类型严格解析（int/bool/date/time/double…，非法元素抛 `FormatException`）；生成代码空性告警清零并纳入 `WarningsAsErrors=CS8619;CS8620;CS8625`；`ScalarCodec` 超长输入统一抛 `FormatException`；MsgPack 拒绝空载荷/尾随字节；JSON 非法转义与前导逗号已拒绝；TOML fast array 溢出守卫。
- **P3**：`AssemblyPrefix` 改为 `AsyncLocal`（生成器并发隔离）；`PICO*004` 仅在真实存在匿名序列化用法时报告。
- **新发现并修复**：MsgPack 3+ 层嵌套生成不可编译代码（唯一局部变量 + 正确接收者）；TOML/INI 3+ 层嵌套静默丢值（改为抛 `NotSupportedException`）；INI 对象元素列表不再产出不可编译代码（按文档化约定忽略）。
- **`Indented` 选项补齐**：INI 缩进 section 内容、TOML 缩进 table 内容、YAML 缩进序列项（默认紧凑输出不变）。

### 仍开放的已知限制

- 顶层标量 / `Nullable<T>` 目标不支持（响亮失败）。
- INI/TOML 仅支持一层嵌套对象（更深抛 `NotSupportedException`）；INI 嵌套对象列表被忽略。
- YAML flow 序列（`[a, b]`）尚未支持；flow 映射（`{k: v}`）部分支持。
- BUG-11（sequence 模式超长数字）与 BUG-12（流式性能）见上表。

## 修复状态更新（2026-09-17，流式根因批 + code review 遗留项）

### 本轮已修复

- **递归 DTO**：环形安全提取 + 环目标 helper 播种（JSON/MsgPack 完整支持；TOML/YAML/INI 提取期跳过成员并报 `PICOSERDE003`）。
- **类型级递归引用的环目标**：容器成员（`List<T>`/`Dictionary<K,V>`/数组）的环目标由容器类型修正为**元素/值类型**（`PropertyInfo.ElementTypeFullName`），避免 helper 以容器 FQN 播种而丢失成员。
- **生成器文件名冲突**：`GenInfrastructure.UniqueName`（`SafeName` + FNV-1a 稳定 16 hex 后缀）用于全部 helper/主 hint 名；主 hint 冲突仍报 `PICOSERDE002`。
- **TOML 内层 helper 边界**：section 制且无 `ObjectEnd`：内层 helper 跨同 section 的点号键 `ObjectStart`（`TablePath` 相同）继续、遇其他 section 停止；修复"兄弟 section 被吞"与"点号键丢值"两类缺陷。
- **多态继承（共享修复）**：`GenInfrastructure.ChainKeyword(ref bool first)` 取代 `pi == 0 ? "if" : "else if"`（跳过复杂成员时会产出悬空 `else if`，编译失败）；poly 合并继承属性后按合并顺序重编 `IntKey`（MsgPack 字段 id 去重，原先基类/派生类各自从 0 开始导致重复 `case 0`）。
- **空流语义**：TOML/YAML/INI 空流返回空对象（与同步路径一致）；JSON 空流抛 `FormatException`（"the document contains no value"），`StreamingRunner` 对 `EndOfInput` 给出明确消息。
- **契约/诊断**：`ITransactionalTokenReader` + `IncompleteInputException` 提炼至 Core（IVT 移除无使用的 PicoMsgPack）；BOM 与分块边界统一；`PICOSERDE003` 由 TOML/YAML/INI 生成器在 `GenerateAll` 报告。

### 本轮新发现（开放）

| 编号 | 严重度 | 说明 | 证据 |
|---|---|---|---|
| POLY-01 | P1 | **多态派生类型的复杂成员被丢弃**：原为 MsgPack/TOML/YAML/INI 的 poly 派生链排除 `IsComplexMember`。✅ **已修**（`343199c` MsgPack；`e329d63` TOML/YAML）：MsgPack poly 派生 ser 发全成员 + de 用 inner helper 读对象成员；YAML 走共享 `EmitSerialize`/`EmitDeserializeInline`；TOML ser 用 `EmitSerializeProp` 写 `[Section]`、de 改为根循环形状（ObjectStart/TablePath → `TomlInner`/`TomlDictInner`）。INI 保持"忽略（文档化）"。 | `tests/PicoSerDe.Integration.Tests/PolyInheritanceTests.cs`（MsgPack 断言已收窄并注释）；生成代码：`PicoMsgPack.Gen/..._PolyPerson_*_MsgPackSerializer.g.cs` 仅派发 `Id`/`Name` |
| POLY-02 | P1 | ✅ **已修**：多态感知的内层 helper（`GenPolyInner`）——多态类型的生成的 ser/de 改为 `internal` 跨文件可达（`{UniqueName}JsonPolySer/JsonPolyDes`、`{...}MsgPackPolySer/MsgPackPolyDes`），递归 helper 路由到 discriminator 分派；poly core 改为"调用方已定位到对象起始"约定并加 chunk 守卫；JSON poly 流式派生分支加 per-member 快照 + `NeedMoreData` 回退（嵌套多态值跨块安全）。 | 复现：`[PicoDerivedType(typeof(Leaf),"leaf")] abstract class Node { public Node? Next; }` → `_JsonInner.g.cs`/`_MsgPackInner.g.cs` CS0144；具体基类变体：嵌套 `Next` 反序列化后 `IsTypeOf<Leaf>()` 失败 |
| POLY-03 | P2 | ✅ **已修**（`9c29ee3` 之后）：新增共享 `GenInfrastructure.BaseDiscriminator`（具体基类合成 discriminator=类型名，冲突时回退 FQN/hash；抽象基类无实例→不生成），5 个格式的 poly 序列化/反序列化把基类作为一个 case 追加在派生类型之后——基类实例现在输出 `$type = "ConcBasePerson"` 并可往返（原为 `{"$type":}` / `$type = ""`）。测试 `PolyConcreteBaseTests` 6/6（JSON/MsgPack/TOML/YAML/INI + 派生分派回归）。 | `JsonSerializer.Serialize(person)`（静态类型 = 具体基类，实例 = 基类）→ 反序列化报 `Unknown type discriminator: $type` |

### Code review（2026-09-17 提交 `ecaa69d` 复审）

| 编号 | 严重度 | 结论 | 说明 / 证据 |
|---|---|---|---|
| REC-01 | P1（预存在，非原批次引入） | ✅ 已修（`5242089`） | `List<List<TObject>>`（嵌套列表的对象元素）生成不可编译代码：CS0234 缺 `...JsonInner` + CS1503 `List<object>` → `List<T>` 不匹配。已在父提交 `ecaa69d~1` 用 git worktree 复现同一错误，故非本批回归；此外**非递归**变体（`List<List<DeepLeaf>>`）同样失败，说明与递归无关，属嵌套列表的对象元素路径既有缺陷 |
| REC-02 | P3（本次复审已修） | ✅ 已修 | `PICOSERDE003` 对同一类型多用途场景重复报告（driver 计数=2）。`ReportSkippedRecursiveMembers` 增加按成员去重；新增常驻 driver 测试 `RecursiveMember_IsReportedOnceForAllUsages` |
| REC-03 | P3（文档） | ✅ 已修 | README "no whole-document buffering" 措辞收紧为"按 token 释放已消费字节，仅保留当前 token/member 窗口" |
| REC-05 | P1（预存在，复审发现） | ✅ 已修（见下） | **可空元素类型不受支持**：`List<int?>` / `List<string?>` 生成不可编译代码（JSON CS1503/CS0029；MsgPack CS1503/CS0019/CS8619，均在 `WarningsAsErrors` 内）；`List<TObject?>` / `Dictionary<string,TObject?>` JSON 缺 null 检查（CS8604，运行时空元素走 `SerializeCustom`/inner helper）而 MsgPack 为 CS8619。README 声称 "null elements are allowed for reference-type elements" 对带 `?` 注解的元素类型未兑现（`ElementIsNullableReference` 在元素 ser/de 路径未被使用）。证据：复审临时探针生成的 `ScalarNullHolder_*_JsonSerializer.g.cs` / `_MsgPackSerializer.g.cs`（探针已删除，未入库） |
| REC-04 | 无问题 | 通过 | 复审独立验证：可空元素递归（`List<T?>`/`Dictionary<string,T?>`）、TOML/YAML/INI 截断/畸形流的同步-流式一致性（14 个常驻用例，比对字段值而非仅 null 性）、`UniqueName`/`ChainKeyword`/IntKey 重编号回归 |

### REC-01 / REC-05 修复记录（2026-09-18）

- **REC-01**：`BuildNestedListElement` 现在提取最内层对象元素成员并标记环引用；`CollectNestedTypes` 沿嵌套列表链注册最内层对象 helper（`AddNestedTypeFromListChain`）；JSON 内层列表用元素类型声明；MsgPack 新增真正的嵌套 list/array 读写（原来写 `ToString()`、读 `default!`，属静默损坏）。TOML/YAML/INI 在提取阶段以 **PICOSERDE004** 诊断式丢弃嵌套列表成员（原来生成不可编译代码）。
- **REC-05**：`MapTypeNamePreservingNullability` 为 `Nullable<T>` 保留 `?`；新增 `PropertyInfo.ElementIsNullableValue`；JSON/MsgPack 的元素读写对可空元素发 null 检查（值类型解包 `.Value`、引用类型写 null/读 null），TObject 元素同样获得 null 检查（原来 CS8604/运行时 NRE）。TOML/YAML 保留"可空标量元素跳过 null"的既有语义（TOML 数组写出补 null 守卫）；三者对无法表达的形态（`List<int?>`、可空对象/字典元素、嵌套列表）统一 PICOSERDE004 丢弃；INI 丢弃一切可空元素成员。
- 附带修复：YAML 全部成员被丢弃时发射器产生悬挂 `else`（空分发链）——已加 `Properties.Length == 0` 守卫（同步与流式发电器）。
- **POLY-01/POLY-02（2026-09-18 完成）**：poly 派生类型保留嵌套对象成员（MsgPack/TOML/YAML，INI 文档化忽略）；递归 × 多态：抽象基类的递归成员编译通过，嵌套派生值保持运行时类型（JSON 同步+流式跨块、MsgPack），具体基类同样正确。
- **新增修复（同批）**：MsgPack 普通递归对象成员（`TreeNode.Child` 这类 `IsRecursiveRef` 成员）此前序列化为 `null`、反序列化 `default!` —— 现走播种 helper（`RecursiveTypeTests.MsgPack_SelfRecursiveObjectMember_RoundTrips` 锁定）。

### POLY-01 同族补修（poly 派生类型的集合成员，2026-09-18）

- 现象：poly 派生类型的 `List<TObject>` / `Dictionary<string,TObject>` 成员生成不可编译代码（TOML CS1503/CS0234、YAML CS1503/CS8604、MsgPack CS8625）或静默丢值。
- 修复（全走共享发射器，不再手写标量分支）：
  - TOML：poly 序列化改用 `EmitSerializeProp`（全覆盖标量/对象/字典/对象列表/`[[key]]`）；poly 反序列化改为根循环形状——`PropertyName` 分支用 `EmitPropertyDispatch`、`ObjectStart` 分支用 `EmitNestedObjectRead`/`EmitDictRead`、新增 `ArrayStart` 分支用 `EmitPropertyDispatch` 读表元素（reader 变量统一为共享发射器约定的 `r`）。
  - YAML：poly 序列化改用 `EmitSerialize`；poly 反序列化改用 `EmitDeserialize`（含 ctorMap），循环采用普通路径形状（成员发射器自行推进 reader，循环顶部不重复 Read），reader 统一为 `r`。
  - MsgPack：poly 反序列化复杂/集合成员改用 `WriteDeser`。
- 附带修复：`dti.CtorParams` 为 default `ImmutableArray` 时访问 `.Length` 抛 NRE（生成器整体失败 CS8785）——所有新增循环加 `IsDefaultOrEmpty` 守卫。
- 测试：`PolyCollectionMemberTests`（JSON/TOML/YAML/MsgPack × 列表+字典）4/4；全量 1501 tests / 0 failed。

### Code review 复审（2026-09-18，批次 `122143a..c406436`）

对抗性探针（不依赖既有测试）发现并处理：

| 编号 | 严重度 | 结论 | 说明 / 证据 |
|---|---|---|---|
| RV-01 | P1 | ✅ 已修 | REC-05 只覆盖了单层可空元素：`List<List<int?>>` 的内层声明为 `List<int>`（CS1503）且 null 分支写入非可空 int（CS1503）。修复：`BuildNestedListElement` 的包装对象补齐 `ElementTypeNameAnnotated` 与 `ElementIsNullableReference/Value`；JSON `EmitNestedListDeserialize` 最内层改用注解名。回归：`EdgeCaseRegressionTests.Json_NullableScalarAndValueElements_AllKinds`（含 `List<Guid?>`/`DateTime?`/`bool?`/`HashSet<string?>`） |
| RV-02 | P1 | ✅ 已修 | `List<List<List<T>>>` 三层嵌套时 JSON 序列化器每层复用 `__inner` → CS0136（构建失败）。修复：`EmitNestedListSerialize` 增加 nestLevel 后缀。回归：`Json_ThreeLevelNestedListOfObjects`、`MsgPack_ThreeLevelNestedListOfObjects` |
| RV-03 | P1 | ✅ **已修**（TDD） | **字段级对象数组不受支持**：`TObject[]` 成员在 TOML（CS0019/CS8619/CS1061/CS8978）与 YAML（CS0019）生成不可编译代码；`List<TObject[]>` 在 JSON CS1503。已在基线 `ecaa69d` worktree 复现同一错误（`PicoToml.Gen/..._BaseArrPlain_..._TomlSerializer.g.cs(45,25)` 等），与本次改动无关 |
| RV-04 | P1 | ✅ **已修**（诊断式，TDD） | `Dictionary<string, List<T>>` 生成不可编译代码（JSON CS0029/CS8625：把 list 值按 string 读取）。基线 `ecaa69d` 同样失败（`..._BaseDictList_..._JsonSerializer.g.cs(97/101)`） |

RV-03/RV-04 建议单开修复（形态族：字段级数组、字典值为集合），修复策略与 REC-01 相同（提取期支持或 `PICOSERDE004` 诊断式丢弃）。

### RV-03 / RV-04 修复记录（2026-09-18）

- **RV-03 字段级对象数组（已支持）**：提取期 `BuildNestedListElement` 泛化为可接收数组元素（`T[]`/`T[][]`），JSON 嵌套列表反序列化在"父集合是数组"时 `.ToArray()` 物化；YAML 序列化器对数组用 `Array.Empty<T>()` 兜底（原来用 `List<T>` → CS0019）；TOML 同步路径用累加列表 + `ToArray()` 物化，并跳过该类型的流式委托（`TomlSerializer` 有缓冲回退，语义仍正确，仅不再增量）。INI 仍按文档丢弃对象集合。
  - 测试：`ArraySupportTests` 4/4（JSON/MsgPack/TOML/YAML 数组字段往返、JSON `List<T[]>`+`T[][]`、TOML 流式回退、INI 丢弃）。
- **RV-04 字典值集合（诊断式跳过）**：`Dictionary<string, List<T>>` 在 JSON/TOML/YAML 生成不可编译代码、MsgPack 静默丢值；现于提取期以 **PICOSERDE004** 丢弃（嵌套字典 `Dictionary<string, Dictionary<...>>` 仍走原 helper 路径保持支持）。
  - 测试：`DictValueCollectionTests` 5/5（五格式成员被丢弃且不产出 key）+ TOML driver `DictValueListMember_IsReported`。

### Code review 第三轮（2026-09-18，批次 `fa5dfef..b295115`）

对抗性探针（batch A/B/C，共 20+ 形态）发现：

| 编号 | 严重度 | 结论 | 说明 |
|---|---|---|---|
| RV-05 | P1（本批引入） | ✅ 已修 | `List<List<T?>>`（嵌套可空**引用**元素）不可编译：集合名走 `FullyQualifiedFormat`（丢注解），且 JSON 最内层对象名未用注解名。修复：`MapTypeNamePreservingNullability` 对符号派生 kind 使用注解保留显示；JSON 最内层名优先 `ElementTypeNameAnnotated`。回归：`EdgeCaseRegressionTests`（`Grid`/`Names` 含 JSON 流式分块）|
| RV-06 | P1（**预存在**） | ✅ 已修 | **record（主构造器）在 MsgPack/TOML/YAML/INI 全部不可编译**（CS7036/CS8852）——共享 `DetectConstructor` 只认显式 ctor 属性。修复：共享 `DetectConstructor` 回退到 record 主构造器（`BuildCtorParams` 抽公共），并补 TOML ctor-集合成员发射（列表/数组赋值走 `EmitAssign`/`__cp_i`）、YAML inner helper 的对象列表读取。回归：`RecordSupportTests` 4/4（五格式 plain record、含 List/数组成员、流式）|
| RV-07 | P2（预存在，静默丢值） | ✅ 已修（诊断式） | YAML：**嵌套对象成员内含对象序列**时 inner helper 静默返回空列表；现于提取期以 `PICOSERDE004` 丢弃外层成员（TOML 该形态保持既有"深层嵌套"响亮异常）。回归：`EdgeCaseCollectionRegressionTests.ObjectArrayInsideNestedMember_*` |

同轮确认无问题：字段级数组四格式往返、`List<T[]>`/`T[][]`/`List<string[]>`、JSON/MsgPack 流式数组元素、MsgPack 数组元素、TOML record+数组（`[[Items]]` + ctor）、`Dictionary<string,Obj[]>` 丢弃、嵌套成员内 `List<List<int?>>`、`HashSet<int?>`/`Queue<string?>`、TOML/YAML 流式（含缓冲回退）。
