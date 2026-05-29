# Beta 模块问题清单

> MsgPack 53 tests | TOML 59 tests | YAML 57 tests | 2026-05-29

---

## MsgPack (4 项)

- [ ] **SG: 支持 ext 扩展类型** — Reader 已解析 `Extension` token（tag+data），Writer 缺 ext API，SG 不生成。需在 `WriteSer`/`WriteDeser` 添加 `case "extension"` 分支。
- [ ] **Reader: 构造器堆分配** — 两构造器各 `new int[64]` + 2×`new bool[64]` = 每实例 768B 堆分配。可改为 `stackalloc` 或复用静态数组。
- [ ] **Writer: count-upfront API** — `WriteStartObject(int count)` 强制 SG 预计算集合大小。MsgPack 格式允许用 `0xDE`(map16)/`0xDF`(map32) 后补写长度，可支持流式。
- [ ] **SG: `static _varCounter` 改局部** — 虽单线程安全，改为 `Gen()` 内局部 `int varCounter = 0` 更清晰。

## TOML (3 项)

- [ ] **Reader: `goto StartSpan` 重构** — 主解析循环用 `goto` 跳过空白/注释行。改为 `while (true) { ... continue; }` 结构化循环。
- [ ] **全局: Trim/TrimEnd 去重** — `TomlReader` 的实现与 `IniReader`/`YamlReader` 完全相同。提取到 `shared/` 或使用 `ReadOnlySpan<byte>.Trim()`。
- [ ] **全局: IsDigit 去重** — `private static bool IsDigit(byte b)` 在 ini/toml/yaml reader 中各有一份。提取到 `shared/`。

## YAML (4 项)

- [ ] **Reader: 多文档 `---`** — 无 YAML stream 分隔符解析，仅处理单文档。需在 `ReadSpan()` 顶层添加 `---` / `...` 检测。
- [ ] **Reader: 复杂键 `?`** — 仅解析 `key: value` 单行标量键。需支持 `? complex key\n: value` 多行键语法。
- [ ] **Reader: Tag 指令 `!type` / `%TAG`** — 无类型标签和 TAG 指令处理。需解析 `!` 前缀和 `%TAG !prefix!` 声明。
- [ ] **Reader: 锚点堆分配** — `Dictionary<string, StoredAnchor>` + `List<(byte[],byte[])>` 在启用锚点时破坏零分配承诺。可考虑 `ArrayPool` 或限制锚点数量用栈分配。

---

## 按优先级排序

```
P1 (功能缺口):   YAML 多文档 → YAML 复杂键 → YAML Tag → MsgPack ext SG
P2 (代码质量):   构造器堆分配 → count-upfront → goto → 锚点 → Trim/IsDigit 去重  
P3 (清理):       _varCounter 局部化
```
