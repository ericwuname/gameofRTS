# 07 分支策略 / 任务流转 / PR 门禁清单

## 分支
- `main` = TideShot STG 基线（冻结，只接受 cherry-pick 级修复）
- `prototype/tidebreaker` = 破潮施工主线
- 施工窗口各自开 worktree + 分支 `wb/<窗口名>/<任务>`，完成后 PR 回主线
- 构建产物目录 Build/ 不进 git；可玩版目录用 cp 直复制并校验时间戳

## 任务流转（Taskbook 单）
```
TASK-<n>  capability: basic|standard|advanced|reasoning
设计依据: docs/design/<模块>#<章节>
改动范围: <精确文件列表>
验收标准: <可观察判据>
禁止事项: <本任务的禁区>
```

## PR 门禁清单（有缺项不予 review）
- [ ] 使用角色与 capability 级别
- [ ] 改动文件清单与任务单一致
- [ ] @trace 标记齐全，trace_check 覆盖率 ≥0.9
- [ ] G1 构建证据（日志路径）
- [ ] 测试/验证限制如实声明（跑不了说清缺什么）
- [ ] 已知缺口列明（不装完成）
- [ ] handoff 已写入 .handoff/

## 人审红线（Orchestrator 必须亲自过目）
数值红线（冻结调参表）、存档/序列化结构、删除类操作、密钥与隐私、
发布类操作（push tag / release）。
