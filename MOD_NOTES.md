# STS2 AI 队友 Mod — 卡点清单与风险说明

> 最后更新：2026-10-26
> 适用版本：`Sts2Companion` 0.2.0 / 2026-10-26
> 本轮性能/正确性优化见 `HANDOFF.md` §8；下面保留 2026-10-25 的风险清单与看门狗说明。

---

## 一、现在已经自动处理的

| 场景 | 机制 | 日志关键字 |
|---|---|---|
| 事件选项 | 每翻一页跟选一次，同页最多试 8 次；两人事件都结束才 `NEventRoom.Proceed()` | `coop-event:` |
| 地图选点 | 单人 net service 不走投票；读到本地玩家的票后 `AddVisitedMapCoord` + `EnterMapPointInternal(saveGame:false)` | `coop-map:` |
| 战斗出牌 | `Phase==Play` 才动手；按敌方意图伤害决定斩杀/格挡；换战斗自动重置回合计数 | `coop-combat:`、`AI 回合` |
| 选牌 / 弃牌 / 删牌 / 升级 / 附魔 | `PushSelector(isLocal:false)` 只在 AI 出牌窗口挂；多选按 min/max 取，动作完成前不卸 | `coop-select:` |
| 卡牌奖励里的"选一张" | `CardReward.OnSelect` 对非本地会走 `WaitForRemoteChoice`，所以手动 `CardPileCmd.Add` 进牌组 | `coop-reward: AI 选卡` |
| AI 的奖励集 | 金币/药水/遗物反射 `SelectRewardForPlayer(ai,未选下标)`；卡牌奖励手动领取 | `coop-reward:` |
| 共享事件（投票） | AI 用 `PlayerVotedForSharedOptionIndex` 投同票；票齐后游戏自己结算 | `coop-event:` |
| 角色选择 | 主菜单「和 AI 玩」→ 原版选人界面两段式（先我，再 AI） | `chars: ...` |
| 休息点 | AI 用私有 `ChooseOption(ai,index)` 选治疗/锻造/首个可用项；锻造时临时挂 selector | `coop-rest:` |
| 宝箱 / 遗物 | `TreasureRoomRelicSynchronizer.OnPicked(ai,0)`；地图旅行等双方投票 | `coop-treasure:` |
| 章节转场 | `ActChangeSynchronizer.OnPlayerReady(ai,act)` | `coop-act:` |
| 动作队列被堵 | 牌 2.5 秒没离手 → `Cancel()` 当前动作解锁队列 | `已取消卡住的动作` |

---

## 二、仍然可能卡住的地方（按概率排序）

### 1. 休息点（Rest Site）—— 已处理

AI 通过私有 `RestSiteSynchronizer.ChooseOption(Player, int)` 选治疗/锻造/首个可用项；
锻造/复制/烹饪会临时挂 `AiCardSelector` 处理选牌。
另外 `CoopMapTick` 在 `RestSiteRoom` 里会等双方 `GetChosenOptionIndex` 都有值才允许自动旅行，
避免把休息点跳过导致房间状态为 null、后面进不去。
日志前缀：`coop-rest:` / `coop-map: 休息点未选完`。

### 2. 商店（Shop）—— 中高风险

AI 不买也不离开。若离开商店需要"双方确认"，会卡。

- 看门狗会显示：`房间=MerchantRoom`

### 3. 宝箱 / 遗物选择 —— 已处理

- `TreasureRoomRelicSynchronizer.OnPicked(ai, 0)` 替 AI 选第一个遗物；没有遗物时 `CompleteWithNoRelics()`。
- 双方选完后 `CurrentRelics` 变 null，等 1.2 秒自动调 `RunManager.ProceedFromTerminalRewardsScreen()`，
  不再卡在宝箱房的"继续"按钮。
- `CoopMapTick` 在 `TreasureRoom` 里等双方 `GetPlayerVote` 都有值才允许旅行。
- 日志：`coop-treasure:` / `coop-map: 宝箱遗物未选完`。

### 4. 章节转场 / Boss 遗物 —— 部分处理

- `ActChangeSynchronizer.OnPlayerReady(ai, act)` 会让 AI 在转场时标记就绪，避免等不到人。
- 如果 Boss 遗物本身走 `RelicSelectCmd.FromChooseARelicScreen` 的远程选择，
  单人档仍可能抛 `Cannot wait for remote choice`；需要继续找按玩家的入口。

### 5. 特殊事件小游戏 —— 中风险

水晶球、选卡组（Capstone / Bundle）这类有独立 UI 的事件。它们不一定走 `CardSelectCmd`。

- 看门狗会显示 `事件=[...]` 里有 `(锁)` 或选项数异常。

### 6. 真实"选牌同步"层 —— 中风险，影响面最大

`CardSelectCmd.Selector` 只覆盖走 `CardSelectCmd` 的流程。还有一层是
`PlayerChoiceSynchronizer.WaitForRemoteChoice(player, choiceId)`——它是"等某个玩家的选择"的底层协议。

只要有流程走这层，AI 就永远等不到应答。

- 看门狗会显示：`待应答选择=N 个 ids=[...]`
- 只要这个数字长期 > 0，就是这个原因。
- 处理方向：对 AI 调 `SyncLocalChoice(ai, choiceId, PlayerChoiceResult.…)`，但要先确定 choice 的类型（选牌 / 选目标 / 选项下标），选错的后果比卡住更糟。

### 7. 阵亡后的回合判定 —— 低-中风险

AI 阵亡后，这套单人 net service 的 `AllPlayersReadyToEndTurn()` 会变成 true，导致你的回合被跳过。

- 已加缓解：你还在 `Play` 阶段时撤销你的 ready 标记（`UndoReadyToEndTurn`），500ms 一次。
- 日志：`coop-death:`。**未在实机验证过**。

### 8. AI 奖励领取的边界 —— 常见奖励已能领，特殊奖励仍可能回退

旧版只调 `SkipRewardsSetOnStackTopForPlayer`，AI 拿不到金币/卡牌/遗物。
现在金币/药水/遗物反射 `SelectRewardForPlayer(ai, 未选下标)` 领取。
卡牌奖励不能走它：`CardReward.OnSelect` 对非本地玩家直接 `WaitForRemoteChoice`，
单人档抛 `Cannot wait for remote choice in singleplayer!`；改为自己
`CardPileCmd.Add(card, Deck, Bottom, null, false)`，再标记 `SuccessfullySelected`
并调 `CompleteRewardsSetIfNecessary`。药水满/特殊奖励连续 3 次失败回退跳过。

### 9. 结构性风险：单人 net service 本身

这套档是用 `SetUpNewSingleplayer` 建的，但塞了两个玩家。游戏内部假设"玩家 == 网络对端"，
所以我们不断遇到：

- `AllPlayersReadyToEndTurn()` 不含本地玩家却返回 true
- 投票系统收票但永远不结算
- 房间进入的 await 可能极晚才完成（实测拖过 60 秒）

**这类问题没法根治**，只能逐个接口绕开。每绕开一处就可能冒出新的一处。

---

## 三、看门狗怎么读

状态指纹 15 秒不变就会打一条：

```
coop-watch: ⚠ 状态已 15 秒无变化（第 1 次）—— 现场如下
coop-watch:   屏幕=MapScreen 房间=null coord=(2,4) visited=5
coop-watch:   战斗=未进行
coop-watch:   事件=无 地图=gen=0 local=无 ai=无 奖励=无待领
coop-watch:   选牌器=已挂上 待应答选择=0
```

字段含义：

| 字段 | 怎么看 |
|---|---|
| `屏幕` | 当前 UI 屏幕类名，`null` 说明没有活动屏幕 |
| `房间` | `null` = 处在"房间之间"（地图/转场） |
| `战斗` | `round/side/allReady` + 每个玩家的 `phase/hp/ready` |
| `事件` | 选项列表，`(锁)`=不可选，`(推进)`=Proceed 占位 |
| `地图` | 双方投票的坐标 |
| `奖励` | 每个奖励集本地/AI 是否完成 |
| `待应答选择` | **> 0 且不降 = 卡在 ChoiceSynchronizer 那一层** |

---

## 四、出事时的处理顺序

1. **别关游戏**，先复制 `D:\sts2_companion\state\mod_error.log` 的尾部（最后 30 行）。
2. 关游戏。
3. 需要回滚时，把 `mods\Sts2Companion\Sts2Companion.dll.bak_<时间戳>` 改名回 `Sts2Companion.dll`。
4. 不要用 `EnterMapCoordDebug` / `EnterMapPointInternal` 之类的暴力手段手动推进——会把 `CurrentMapCoord`
   和实际房间弄不一致，之后每一处都会错。

---

## 五、已知已修复的历史问题（避免重复踩）

| 现象 | 根因 |
|---|---|
| 事件房间不推进 | 以为有 `OnEventFinished` 回调；实际要 `NEventRoom.Proceed()` |
| 进战斗黑屏 | 单人档让 AI 投了票，`_votes` 集齐触发半截转场 |
| 打完怪地图不动 | 只调了 `EnterMapPointInternal`，没调 `AddVisitedMapCoord`，坐标不前进 |
| 每回合只出一张牌 | 固定 `Delay(200)` 判离手太短，第二张还没走完就被判失败 |
| 新战斗 AI 不出牌 | `_aiLastTurn` 跨战斗没重置，上一场第 5 回合把新战斗的 round=1 挡死 |
| 游戏无响应 | `System.Threading.Timer` 在**线程池**上调 `Tick()` 读游戏对象 |
| 弃牌卡住 | 非本地玩家的选择请求没人应答（`CardSelectCmd.Selector` 栈为空） |
| 浴场事件无限加最大生命 | 离开事件房间后 `CurrentRoom` 仍停在 `EventRoom`，AI 每 400ms 重放 `LINGER` |

---

## 六、2026-10-25 12:40 这一版的加固

针对"离开界面后台还在动"这一类问题，统一改成**看界面而不是看房间类型**：

| 位置 | 守卫 |
|---|---|
| `CoopEventTick` | `NEventRoom.Instance == null` → 直接返回（核心修复） |
| `CoopEventTick` | 本地事件 `IsFinished` 且当前页 `options == 0` → 判定"僵尸事件"，停止跟选 |
| `CoopEventTick` | 每个事件房间最多跟选 6 次；每页最多试 8 次 |
| `CoopProceed` | 推进后 8 秒仍是同一房间 → 重试 Proceed（最多 3 次） |
| `CoopMapTick` | 地图屏幕必须 `IsOpen`；进房间 12 秒未生效 → 重试（最多 2 次） |
| `CoopCombatTick` | `IsInProgress` 才处理；换 `CombatState` 实例即重置回合计数 |
| `PlayAiCardsAsync` | `_aiPlaying` 重入锁，防止并发触发导致重复出牌 |

新增日志前缀：`coop-combat-idle:`、`coop-watch:`、`coop-death:`。

---

## 七、人工兜底：切视角（F10）

卡住时可以**临时把"本地玩家"切到 AI 身上**，用你的界面替它做决策。

- 快捷键：**F10**
- 实现：`LocalContext.NetId`（公开可写静态属性）切到 AI 的 netId

切过去之后：

| 你的操作 | 作用对象 |
|---|---|
| 点事件选项 | AI 的事件 |
| 选牌 / 弃牌 / 附魔 | AI 的牌 |
| 结束回合 | AI 的回合 |
| 地图选点 | AI 的投票 |

同时 **AI 自动操作全部暂停**（`CoopEventTick` / `CoopMapTick` / `CoopCombatTick` 直接 return），
避免它跟你抢同一个决策。再按一次 F10 切回，会重置 AI 的状态缓存
（回合计数、事件页、地图旅行锁），避免它拿旧缓存做错判断。

### 已知限制

1. **已经构建好的界面不会重绘**。切过去之后，屏幕上的手牌/HUD 还是原来那个玩家的。
   但"点下去时现查 `LocalContext`"的动作（事件选项、结束回合）会正确作用到 AI。
2. **游戏可能在房间切换时把 `LocalContext.NetId` 改回默认值**。如果发现切过去又不管用了，
   再按一次 F10 即可。
3. 这是兜底手段，不是修复。切过去之后 AI 不会自己再动，需要你手动切回来。

---

## 八、2026-10-26 这一版的优化（详见 HANDOFF §8）

- 看门狗从 25Hz 降到 1Hz，并缓存 `ScreenContext` 的反射结果。
- 敌方意图伤害改用 `NextMove.Intents`，不再用 `GetIntents()`（它是全部招式的并集）。
- 卡牌费用改用 `EnergyCost.GetResolved()`；卡牌伤害/格挡改用 `DynamicVars` 公开属性。
- 战斗判断改用 `CombatSide` / `PlayerTurnPhase` / `CardType` / `PileType` / `TargetType` 强类型。
- AI 奖励改为逐个领取；卡牌奖励手动进牌组，金币/药水/遗物走 `SelectRewardForPlayer`。
- 共享事件改为 AI 投同票；票齐后 `PlayerVotedForSharedOptionIndex` 自动结算，不再用 `ChooseOptionForEvent`。
- 选牌应答器只在 AI 出牌窗口临时挂；本地卡牌奖励/弃牌/预见不会被跳过。
- 多选按 `min/max` 取（min=0、max=2~3 时取满），AI 动作完成前不卸 selector。
- 休息点用私有 `ChooseOption(ai,index)`；锻造时临时挂 selector，地图旅行等双方选完才放行。
- 宝箱用 `TreasureRoomRelicSynchronizer.OnPicked(ai,0)`；章节转场用 `ActChangeSynchronizer.OnPlayerReady`。
- 商店界面开着时暂停自动旅行，避免跳过商店。
- `CoopLoopNode` 复用，不再每次开局新增。
- `deploy.ps1` 重写：路径自适应、哈希幂等、按需备份、`-DryRun` / `-Launch`。

构建结果：0 警告 / 0 错误；尚未进游戏实跑，第一次请按 `HANDOFF.md` §8 的清单验收。

---

## 九、角色选择（2026-10-26 改版）

主菜单「和 AI 玩」按钮（插在「单人游戏」同一列）→ 原版选人界面两段式：

1. 第一步选你玩的角色，点「下一步：选队友（点这里）」
2. 第二步选 AI 队友，点「开始双人（点这里）」

- 角色列表来自原版 `NCharacterSelectScreen`，所以**原版 + 已注册 mod 角色**都会出现。
- 原版「启程」按钮被隐藏，避免误开单人对局；两步都走完才 `StartCoopRun(local, ai)`。
- 日志：`chars: 第一步 我=...` / `chars: 第二步 AI=...` / `coop: 角色 ...`。
- 如果某个 mod 角色的 `CharacterModel` 不能用于 `Player.CreateForNewRun`，会在
  `coop 崩溃:` 里看到异常；把日志发回来即可针对该 mod 加兼容。


