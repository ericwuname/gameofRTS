# 03 交接协议（Handoff）

## 会话生命周期（POS 三步制）
1. **注册**：会话开始，创建 `.handoff/sessions/<tool>-<yyyyMMdd-HHmm>.md`
   声明角色、capability、当前任务
2. **工作**：边做边更新 touched files 与 current_task（会话崩溃也有据可查）
3. **关闭**：写 handoff 记录到 `.handoff/handoffs/<date>-<tool>.md`

## Handoff 记录格式（六段，缺一不可）
```markdown
# handoff <date> <tool> <role>
summary:     # 3-5 行做了什么
completed:   # 逐条列出，每条带 @trace 编号或"无编号原因"
pending:     # 未做项 + 为什么
blockers:    # 卡在哪，需要谁/什么才能继续
resume_point:# 精确到 file:line 的续作起点
evidence:    # 构建日志/截图/自检输出路径
```

## 纪律
- resume_point 必须精确锚点（file:line），禁止"继续做 XXX"这种模糊交接
- 接手窗口先读 handoff 再动手；发现 handoff 与源码矛盾时**以源码为准并报告**
- handoff 是 append-only，禁止修改他人记录
