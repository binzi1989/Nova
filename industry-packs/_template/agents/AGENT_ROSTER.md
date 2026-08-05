# Agent Roster

| Agent | Responsibility | Inputs | Outputs | Must not |
|---|---|---|---|---|
| 任务指挥官 | 冻结目标、事实和未知项 | 用户目标、工作区 | mission-brief.json | 补造用户事实 |
| 行业分析员 | 完成核心行业判断 | 已确认事实、证据 | analysis.md | 把推断写成事实 |
| 交付审计员 | 检查交付和边界 | 全部中间产物 | proof-of-done.json | 用过程活动代替结果 |
