# PicoSerDe 全面代码审核报告（P0 规格）

- **审核日期**：2026-10-23
- **审核基线**：`d28e0125f64318327d0c37729ee1f6744e985bd0`（`main`，工作树 clean）
- **方法**：10 个独立只读 reviewer lane（按模块/关注面分区）+ 编排者对每条 Critical 的独立复验（构建、全量测试、AOT 发布、`%TEMP%` 运行时与编译负例探针）。
- **审核契约**：`picohex-picoserde`（AOT/零反射/fail-loud）+ `picohex-self-review`。

## 基线证据

| 项目 | 命令 | 结果 |
|---|---|---|
| Debug 构建 | `dotnet build PicoSerDe.slnx -c Debug` | 0 warning / 0 error |
| Release 构建 | `dotnet build PicoSerDe.slnx -c Release` | 0 warning / 0 error |
| 全量测试 | `dotnet test PicoSerDe.slnx -c Debug --no-build` | 1541 passed / 0 failed / 0 skipped |
| AOT 发布 | `dotnet publish PicoJetson.Sample -c Release -r win-x64 -p:PublishAot=true` | 成功，1.89 MB，exit 0 |
| 格式 | `dotnet csharpier check .` | 39 个文件未格式化 |

> 结论：构建/测试/AOT 均通过，但 1541 个测试未覆盖本报告的全部 P0；多处测试为自证式断言。

## 评审结论

**不建议按现状发布/合并。** 共 31 条 Critical（本规格只覆盖 P0；Important/Minor 见对话中的完整报告）。所有 P0 均带可复现证据或编译负例。

## P0 清单（实施目标）

### 共享层（5 格式共同受影响）

| 编号 | 问题 | 证据/根因 |
|---|---|---|
| C-01 | 非多态继承的基类属性被静默丢弃 | `shared/Gen/GenInfrastructure.cs` `ExtractProperties` 只遍历 `type.GetMembers()`；仅多态路径合并基类。探针：`class D : B` 序列化输出缺 `B` 的成员。 |
| C-02 | 匿名类型 null 集合成员输出裸 `null` | `shared/Gen/AnonTypeHandler.cs` key 写在非 null 分支内。探针：`new { Xs = (List<int>?)null }` → `{null}`。 |
| C-26 | 匿名类型可空**值类型**成员序列化全错 | `AnonFieldInfo` 不记录 `IsNullable`；`Read_*` 用 `Unsafe.Read<int>` 读 `Nullable<int>` 布局。探针：`int?=null→0`、`int?=5→1`、`decimal?=1.5→65536`（5 格式）。 |
| C-25 | `AppendCodepoint` 不校验码点，写出非法 UTF-8 | `shared/TextHelpers.cs`；TOML/YAML 的 `\U` 8 位转义直接传入。探针：`\U00110000` → 4 个 U+FFFD，无异常。 |

### JSON

| 编号 | 问题 | 证据/根因 |
|---|---|---|
| C-03 | 多态序列化器无 `default` → `{"$type":}` | `JsonSerializerGenerator` 的 poly switch 无 default；`JsonWriter.WriteEndObject` 不校验 `_afterPropertyName`。 |
| C-04 | 可空枚举生成 `Enum.TryParse<Priority?>` → CS0453 | JSON/INI/TOML/MsgPack 四个生成器共用 `prop.TypeFullName`。 |
| C-05 | 流式池化缓冲所有权竞态（use-after-return） | `JsonReader.TrackBuffer` 的 `_rb7` 滚动驱逐；生成代码的 `propNameSpan` 跨 `Read()` 持有。单线程未复现，并发下可写坏属性名。 |
| C-06 | 多态流式反序列化的 ctor 临时量在分块恢复时重置 | 非多态路径用 `reader.StreamState`；多态路径是分支内局部量。 |
| C-29 | 字典的 nullable 值元素丢 null | `Dict<string,int?>` 的 `null → 0`、`decimal? → 0`、`Guid? → Guid.Empty`；字典值读路径无 `TokenType.Null` 分支。 |

### MsgPack

| 编号 | 问题 | 证据 |
|---|---|---|
| C-07 | string 成员不校验 token 类型 | `{"Name":42}` → `"*"`，无异常。 |
| C-08 | 元素类型缺失 + `Try*` 返回值丢弃 | `List<Guid>` → `Guid.Empty`；`[1,"x"]` → `[1,0]`；`List<DateTime>`/枚举同理。 |
| C-09 | 截断容器静默返回部分对象 | map 声明 2 对只给 1 对 → 返回对象，无异常。 |
| C-10 | map 字符串键被忽略；UInt64 变负数；负长度抛 `ArgumentOutOfRangeException` | 字符串键值写入 `IntKey==0` 成员；`0xCF FF..FF → -1`；`0xC6 FF FF FF FF → 内部异常`。 |
| C-27 | nullable 的 decimal/DateTime/Guid/TimeSpan/DateOnly/TimeOnly 写 null 后读不回 | 写 `C0`，读侧这些分支无 `TokenType.Null` 守卫；6/6 `FormatException`。 |
| C-28 | nullable 集合被读成空集合 | `??= new List/...` 先执行；`null` 与 `[]` 合并；3/3 复现。 |

### TOML

| 编号 | 问题 | 证据 |
|---|---|---|
| C-11 | section 之后的标量被读侧吞掉 | 声明顺序发射 `[Address]` 后写 `Name`；inner helper 消费后续 `PropertyName`。探针往返丢 `Name`。 |
| C-12 | 裸值前缀解析 + 行尾注释 | `0x1F→0`、`1_000→1`；`true # c` 抛 `FormatException`。 |
| C-13 | 带引号的点号键拆分错位 | `"a.b" = 1` → 键 `"` 与 `.`。 |
| C-14 | `decimal` 写读 culture 不对称（TOML/YAML/MsgPack） | de-DE `1.5m` → `"1,5"` → 回读 `15`。 |

### INI

| 编号 | 问题 | 证据 |
|---|---|---|
| C-15 | `[IniConstructor]`/record 的非 string/int 成员生成 `__cp_N.Prop` → CS1061 | `record IniRec(string Name, long Age)` 编译失败。 |
| C-16 | 非 string 字典键生成 CS1503 | `Dictionary<int,string>` 编译失败。 |
| C-17 | `[IniComment]`/`[IniSection]` 未转义 → 语法错误 | `[IniComment("say \"hi\"")]` → CS1003。 |
| C-18 | Guid/DateTime/TimeSpan/enum 解析失败静默默认 | `Id = not-a-guid` → `Guid.Empty`。 |
| C-19 | `[IniConverter]` 只读不写 | 写侧输出原始值，读侧喂给 converter → 往返损坏。 |
| C-20 | 未闭合引号/`#` 截断 | `A = "oops` → `oops`；`Url = http://x#frag` → `http://x`。 |
| C-31 | nullable 字典为 null → NRE | INI dict section 缺 `EmitNullGuardOpen`。 |

### 跨格式（TOML / YAML / INI）

| 编号 | 问题 | 证据 |
|---|---|---|
| C-30 | null 字面量不被接受：`V = null` / `V:` / `V: ~` 抛 `FormatException`；`string?` 成员把 `null`/`~` 当字符串（`'null'`/`'~'`）。 | 探针复现；`YamlReader`/`TomlReader`/`IniReader` 无 null 字面量识别，生成器无条件走标量解析。 |

### YAML

| 编号 | 问题 | 证据 |
|---|---|---|
| C-21 | 空对象列表吞掉下一个属性 | `Items:` + `Count: 7` → `Count=0`。 |
| C-22 | `List<double|bool|decimal|Guid|DateTime|TimeSpan|enum>` 不可编译 | `List<double>` → CS1503。 |
| C-23 | 行内注释不剥离 | `Name: v # note` → `"v # note"`。 |
| C-24 | 文档末尾空嵌套对象触发伪“死循环” | `Nested:` → `MaxTokens` `FormatException`。 |

## 非 P0 的已知项（后续计划）

- **Important**：MaxDepth 三套默认值；读路径不应用命名策略；`required`/ctor 未全覆盖；Strict 接受 `1e400→∞`；流式 O(n²)（JSON/MsgPack）；`StreamingRunner` 不 `Complete()`；YAML block scalar/锚点/重复键；INI poly 宽松解析；`[PicoIgnore]` 条件在 3 格式失效；`DefaultIgnoreCondition` 在 INI/TOML/YAML 无效；测试假绿；发布链路无门禁；包缺 license/repository；浮动依赖；文档数字与事实错误。
- **Minor**：CSharpier 39 文件、文件级 using、死代码、重复实现、API 命名漂移。

## 变更约束

- 修复必须保持 AOT/零反射：不得引入 `Activator`/`MakeGenericType`/`Enum.Parse(Type)`/`dynamic`。
- 生成代码必须零 warning（`CS8619/CS8620/CS8625` 已设为 error）。
- 公开 API 变化需重新生成 `api/*.public.txt`。
- 行为变化（如 MsgPack nullable 集合不再变空集合）必须在 README 的严格性矩阵中声明。
