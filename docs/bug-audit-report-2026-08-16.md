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

### BUG-13 [P3] 构建告警（全量重建实测 58 条，按代码分布）

- `PICO*004` × 30（各格式 6 条）：测试/sample/benchmark 项目匿名类型序列化需 `AllowUnsafeBlocks`（src 项目无此告警，属卫生问题）。
- `IL2026` × 16 + `IL3050` × 4（TomlCrossValidationTests）：测试用 Tomlyn 反射序列化，与 AOT 目标冲突（测试专用，可接受但建议注明）。
- `CS8602` × 4（StrictPolyAndOptionsTests.cs:194,200 等）：测试代码可能空引用解引用。
- `CS8619` × 4（生成的 `PicoJetson_Tests_StrictModel_JsonSerializer.g.cs:425,241` 等）：生成代码 `List<string>` 与目标 `List<string?>` 空性不匹配——**生成器对可空元素类型的空性标注有缺陷**，值得单独排查。

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
4. **持续**（P3）：CS8619 生成器空性、流式 O(n²)、测试告警清理。

## 验证方式说明

- 所有"实测"结论均通过 `scratch/` 下的临时控制台程序（已清理）在 `dotnet run`（Debug，`PublishAot=false`）全新建产物上验证。
- 测试基线：`dotnet run --project <TestProject>.csproj -p:PublishAot=false` 逐个项目运行（`dotnet test` 在该仓库存在 TUnit 测试发现漂移问题，不可靠路径，详见 tunit-runner skill）。
