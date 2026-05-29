# PicoSerDe TODO List

> 358 tests ✅ | 0 build errors | 2026-05-29 (updated)

---

## ✅ 已完成 (本轮)

- [x] **MsgPack SG: Converter 支持** — 添加 `GetConverter()` + `PropInfo.ConverterTypeFullName` + `WriteSer`/`WriteDeser` converter 分支 (TDD: +1 test, 53→53✅)
- [x] **P3: YAML merge key 自引用修复** — `FinalizeMappingAnchor` / `_pendingMappingAnchor` 时序冲突 (7 tests ✅)
- [x] **P7: YAML SG inner helper** — 嵌套类型去重，`CollectNestedTypes` + `GenerateInnerHelper` (57 tests ✅)
- [x] **E1: JSON 集合反序列化性能** — 已确认 int32 fast path 内联正确 (97 tests ✅)
- [x] **ArrayPool 缓冲区泄漏** — 多缓冲区追踪 (Ini/Toml/Yaml)
- [x] **MsgPack 每整数堆分配** — `_singleByteBuffer` 替换 `new[]`
- [x] **JsonWriter depth bitmask** — `long`→`ulong`
- [x] **INI/Toml depth 校验**
- [x] **CamelCase 首字母缩写**
- [x] **ASCII 大小写比较守卫**
- [x] **PipeReader cancel guard**
- [x] **Release CI + README MsgPack**
- [x] **DateOnly/TimeOnly for MsgPack SG**

## 🟠 P1 — 功能缺口

- [ ] **YAML: 多文档 `---`** — Reader 无文档分隔符解析
- [ ] **YAML: 复杂键** — 仅支持单行标量键
- [ ] **YAML: Tag 指令** — 不支持 `!type` / `%TAG`
- [ ] **MsgPack SG: ext 扩展类型** — Reader 可读 ext 但 SG 不处理
- [ ] **INI: Sequence 模式不完整** — `ReadSeq` 缺数组/引号值解析

## 🟡 P2 — 代码质量

- [ ] **MsgPack Reader: 构造器堆分配** — 每次 `new int[64]` + 2×`new bool[64]`
- [ ] **MsgPack Writer: count-upfront API** — 强制预计算
- [ ] **TOML Reader: goto 重构** — `goto StartSpan` → 结构化循环
- [ ] **YAML Reader: 锚点堆分配** — `Dictionary<string, StoredAnchor>`
- [ ] **全局: Trim/TrimEnd 去重** — 3 份独立实现
- [ ] **全局: 生成器 using 格式统一**
- [ ] **全局: IsDigit 去重** — ini/toml/yaml 各有独立实现

## 🟢 P3 — 清理

- [ ] **Abs: TokenType 清理** — `Float16`/`Int8`/`Int16` 仅定义未使用（其余由 MsgPack 使用）
- [ ] **MsgPack SG: `static _varCounter`** → 改为局部变量
- [ ] **JSON Reader: 多缓冲区追踪** — `ReadNumberSeq`/`ReadLiteralSeq` 未追踪全部

---

## 📊 复查结果（原评估中的错误）

| 原评估 | 实际情况 |
|--------|----------|
| ❌ TOML SG 不支持 list/dict/object | ✅ 59 tests 全部支持（`if` 块而非 `case`） |
| ❌ TOML SG 不支持 section 嵌套 | ✅ NestedPoco test 通过 |
| ❌ MsgPack Cvt 已支持 | ❌→✅ 已修复（TDD: converter 测试） |
| ❌ Float16/Int8/Int16 未使用 | ✅ 确认仅枚举定义 + 测试引用，未在 reader/writer 使用 |
