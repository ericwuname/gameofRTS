# 04 设计冻结与需求追溯

## 设计冻结
- M1 开工前，从 docs/design/ 抽取冻结清单：每条带编号（VIS-xxx/LOOP-xxx/
  ECON-xxx/UNIT-xxx/MAP-xxx/META-xxx/UI-xxx/DATA-xxx/SCOPE-xxx）
- frozen 状态由 Orchestrator 置位；此后需求变更必须：改设计文档 → 改冻结清单
  → 递增版本号 → 通知所有活跃窗口（写入其 handoff 的 blockers 段）

## 追溯标记
```csharp
// @trace ECON-001 维护费公式 2+1.5*舰数+0.5*连线数
```
- 每个实现文件必须带至少一个 @trace；无编号文件只能是 tools/ 与 Editor/
- `tools/trace_check.py`（M1 交付物）：统计覆盖率 = 有标记文件/应实现条目，
  输出缺失编号列表。**覆盖率 <0.9 不允许进入验收**

## 双向检查
- 正向：冻结条目 → 代码标记（防漏做）
- 反向：代码标记 → 冻结条目合法（防私加需求——标记了不存在的编号即违规）
