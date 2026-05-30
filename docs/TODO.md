# PicoSerDe TODO List

> 364 tests ✅ | 0 build errors | 2026-05-30 (updated)

---

## ✅ 已完成 (本轮)

- [x] **Phase 1: 全局代码去重** — Trim/TrimEnd/IsDigit 统一到 TextHelpers; 5个Gen GlobalUsings 格式统一
- [x] **Phase 2: MsgPackReader 构造器堆分配** — InlineArray 替代 `new int[64]`/`new bool[64]`
- [x] **Phase 2: JSON Reader 多缓冲区追踪** — `_rentedBuffers` + `TrackBuffer` (8-slot tracking)
- [x] **Phase 3: INI Sequence 模式** — TDD: +2 tests; `TryReadTo` 重写 `ReadKeyValueSeq`; 支持引号值+转义+注释
- [x] **Phase 4: TOML goto 重构** — `SkipToMeaningfulLine` / `ReadSeq` goto→结构化 while+flag
- [x] **Phase 5: YAML Sequence 模式 Tag 跳过** — ReadSeq 添加 `!tag`/`&anchor` 跳过
- [x] **Phase 6: MsgPack SG `_varCounter`** — static→`ref int` 参数传递
- [x] **Phase 7: TokenType 注释** — `Int8`/`Int16`/`Float16` 添加 "Reserved" XML doc
- [x] **CI: 0 failures, 364 tests ✅**

---

## 🟠 P1 — 功能缺口 (剩余)

- [ ] **YAML: 多文档 `---` Sequence 模式** — ReadSpan 已支持，ReadSeq 未实现
- [ ] **YAML: 复杂键 `? key` Sequence 模式** — ReadSpan 已支持，ReadSeq 未实现
- [ ] **MsgPack SG: ext 扩展类型** — Reader 可读 ext 但 SG 不处理

## 🟡 P2 — 代码质量 (剩余)

- [ ] **MsgPack Writer: count-upfront API** — 强制预计算
- [ ] **YAML Reader: 锚点 `StoredAnchor` 内 byte[]** — 功能必需
- [ ] **全局: YamlReader Trim (spaces-only)** — YAML 规格要求 (有意保留)

## 🟢 P3 — 清理 (已完成)

- [x] **Abs: TokenType cleanup** — 添加 XML doc 注释
- [x] **MsgPack SG: `static _varCounter`** → `ref int` 局部变量
- [x] **JSON Reader: 多缓冲区追踪** — 8-slot `_rentedBuffers`
