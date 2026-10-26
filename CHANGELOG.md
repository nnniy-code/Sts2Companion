# Changelog

## [0.4.0] - 2026-10-26

### Added

- 2–4 player party (default 3), with `＋` / `－` controls in character select
- Per-teammate character selection: self → teammate 1 → teammate 2 → teammate 3
- AI action order follows netId: 2 → 3 → 4
- Rewards, rest sites, treasure, events, and combat process AIs sequentially
- Chat box via OpenAI-compatible API/local model, with custom memory and persona
- Clear "no model connected" message when chat is unavailable

### Fixed

- In singleplayer, AI teammates no longer call `SetReadyToEndTurn`; that API always reports "all players ready" and was ending the local turn.

### Notes

- 3-player mode is the tested configuration; 4-player mode is experimental.
- Extra-AI event choices may still fail in 4-player runs.

## [0.3.0] - 2026-10-26

### Added

- AI source switch: local rules or OpenAI-compatible API
- API settings in the in-game panel (`T`): Base URL, API key, model
- API combat card selection with automatic fallback to local rules
- Language selector: follow game language / Chinese / English
- API and language settings persisted to `companion_ai.json`

### Fixed

- Shop: remove auto-leave and shop map guard; AI stands by until the local player leaves, then map travel resumes.

### Notes

- API mode currently controls combat card choice only; other flows stay local.
- API key is stored in plain text in the local state directory.

## [0.2.0] - 2026-10-26

第一个可玩的本地 AI 队友版本。

### Added

- 主菜单「和 AI 玩」入口，集成到原版/RitsuLib 主菜单按钮列
- 两段式原版选人界面：先选自己，再选 AI 队友
- 支持原版角色和已注册的 Mod 角色
- 战斗 AI：出牌、目标选择、多段攻击
- 战斗奖励：金币、药水、卡牌奖励
- 事件：普通事件、共享事件投票、多页事件推进
- 地图路线自动跟随
- 篝火：AI 自动选择治疗/锻造/首个可用项
- 宝箱：AI 自动选择遗物，双方选完后自动继续
- 需要选牌/弃牌/预见的流程
- 章节转场 AI ready
- F10 切视角人工接管
- `mod_error.log` 卡住诊断

### Fixed

- 篝火后地图提前旅行导致房间状态 `null`
- 宝箱双方选完遗物后卡在继续按钮
- 选人界面直接 `Push` 导致 RitsuLib/原版 UI 空引用
- 多选卡牌只选一张

### Known Issues

- 商店 AI 不购买，也不会自动离开
- Boss 遗物远程选择仍可能卡住
- 部分特殊事件/小游戏未完全覆盖
- AI 决策较简单，不是最优解
- 个别 Mod 角色可能创建失败
- 仅支持单机本地模拟双人，不支持网络联机
