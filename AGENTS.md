# AGENTS.md — AI 协作入口（所有 AI 必读，保持 <100 行）

## 项目
破潮 TideBreaker（分支 prototype/tidebreaker）：攻方海战棋。
Unity 6000.6.0f1 / C# / 程序化渲染零美术资产。设计事实源：docs/design/00-08。

## 构建 / 测试（改动后必须跑，跑不了要说清缺什么）
- 构建：Unity batchmode -executeMethod TideShot.EditorTools.ShotTools.BuildWindows64
- 设计追溯自检：python tools/trace_check.py（未实现先跳过并在 PR 注明）
- AI 验证门：ControlServer 7799 + tools/agnes_player.py（M4 起强制）

## 铁律（违反即拒绝合并）
1. 改设计先改 docs/design/，再改代码；每个实现文件带 @trace 编号标记
2. 不信报告信源码——验收必须源码直读或截图证据，不接受"我改好了"
3. 数值只改 docs/design/02 与 03 的调参表来源处；冻结清单条目变更必须递增版本
4. 禁止修改：MEMORY.md（单 owner）、他人会话的 .handoff/ 记录、frozen 清单（只能顶层改）
5. 失败必须如实披露，禁止静默抑制；不确定就停下来问，不要推进
6. 每次会话结束必须写 handoff（见 docs/engineering/03-handoff.md）

## 目录
- Assets/Scripts/ 源码（Gameplay/Core/UI/Control/Boot/Editor）
- docs/design/ 设计模块（事实源）  docs/engineering/ 本规范细节
- .handoff/ 会话交接记录   tools/ AI 玩家与自检脚本（含密钥，禁止入库已有 key 的文件）

## 详细规范
角色与能力分级 → docs/engineering/02-roles.md；交接协议 → 03；
追溯 → 04；验证门禁 → 05；多 AI 风险 → 06；分支与门禁清单 → 07。
