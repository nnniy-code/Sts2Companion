# STS2 AI 队友 Mod — 交接文档

> 2026-10-26 · `Sts2Companion` 0.3.0 · StS2 `v0.111.0`（Godot 4.5 + C# / .NET 9）

## 0. 一句话现状

单进程双人档（本地 netId=1 + 模拟 AI netId=2）已跑通：
**事件 → 地图 → 战斗出牌 → 选牌/弃牌 → AI 领奖**。
商店仍会卡；宝箱/章节转场已处理，底层 `PlayerChoiceSynchronizer` 未攻破。
卡住时按 **F10 切视角**人工接管。

2026-10-26 做了一轮性能/正确性优化（§8），构建 0 警告；**尚未实机跑过**，先看 §8 清单。

## 1. 路径：先分清源码和运行态

| 角色 | 路径 |
|---|---|
| **源码真身** | `C:\Users\LENOVO\Documents\deepseek-harness\default-workspace\Sts2Companion` |
| 运行态目录 | `D:\sts2_companion`（`state\` 日志/JSON、`agent.py`、旧 `mod\` 镜像） |
| 游戏根 | `D:\steam\steamapps\common\Slay the Spire 2`（`...\data_sts2_windows_x86_64\sts2.dll`） |
| 部署目标 | `...\mods\Sts2Companion`（DLL + JSON） |
| 一键部署 | `.\deploy.ps1`（自动识别脚本所在目录） |
| mod 日志 | `D:\sts2_companion\state\mod_error.log`（排错第一现场） |

工程直接 `<Reference>` 了 `sts2.dll` / `GodotSharp.dll`（`Private=false`），
公开 API 全编译期检查，只有 private 成员才反射。



## 2. 新对话 5 分钟上手

```powershell
cd C:\Users\LENOVO\Documents\deepseek-harness\default-workspace\Sts2Companion
powershell -NoProfile -ExecutionPolicy Bypass -File .\deploy.ps1 -DryRun
powershell -NoProfile -ExecutionPolicy Bypass -File .\deploy.ps1   # 游戏先退出
Get-Content D:\sts2_companion\state\mod_error.log -Tail 60 -Encoding UTF8
```

正常：有 `PollCoopAI 已启动`/`coop-combat: 新战斗`；AI 会出牌；无 `coop-watch:`；事件能 Proceed。

## 3. `deploy.ps1` 一键部署

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\deploy.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\deploy.ps1 -DryRun
```

| 参数 | 作用 |
|---|---|
| `-Force` | 跳过"游戏仍在运行"检查硬写（文件被锁仍失败） |
| `-Kill` | 先强制结束活进程，等最多 10 秒 |
| `-Launch` | 部署后启动游戏（已在跑则不重复启动） |
| `-DryRun` | 只打印路径、目标状态、哈希是否一致 |

行为：编译 → 比哈希（一致跳过）→ 按需备份 → 区分活进程/残留 → 覆盖重试 → SHA256 校验。

> `-Force` 不能绕过 Windows 文件锁；真正锁文件的是"还在退出中"的游戏本体。

## 4. 代码结构

| 文件 | 职责 |
|---|---|
| `Mod.cs`（66 KB） | 入口、状态 JSON、UI 面板、角色下拉框、`PollCoopAI` 主循环、`OnCoopPressed` |
| `CoopAI.cs`（31 KB） | 事件跟选/投票/推进、地图跟票/旅行 |
| `CoopCombat.cs`（45 KB） | 战斗决策/出牌/结束回合/选牌应答器/奖励集 |
| `CoopWatch.cs`（11 KB） | 卡死看门狗（15 秒指纹 + 现场 dump） |
| `CoopView.cs`（3.5 KB） | F10 切视角 |
| `deploy.ps1` / `tools\` | 部署 / `apiprobe` + `ildump` 调查工具 |

`CompanionMod` 是 `static partial class`，五个 `.cs` 是同一类型的分部实现。
主循环由 `OnTreeProcessed`、`KeyWatcher._Process`、`CoopLoopNode._Process` 三处驱动，
入口 40ms 节流封顶 25Hz（多驱动点是实测保留，`CoopLoopNode` 单独不可靠）。
`EnsureCoopLoopNode()` 复用同一节点，旧代码每次开局都 `AddChild`，会越堆越多。

## 5. 已实现功能

| 功能 | 触发条件 | 日志 |
|---|---|---|
| 事件跟选 | `room is EventRoom` 且 `NEventRoom.Instance != null` | `coop-event:` |
| 事件推进 | 双方 `IsFinished` + 有 `IsProceed` + `NMapScreen.Instance != null` | `coop-event:` |
| 地图旅行 | 地图屏 `IsOpen` + 本地已投票 + `NetService.Type == Singleplayer` | `coop-map:` |
| 战斗出牌 | `IsInProgress` + `CurrentSide==Player` + AI `Phase==Play` | `coop:` / `AI 回合` |
| AI 选牌应答 | 仅 AI 出牌/领奖瞬间临时挂 `PushSelector(isLocal:false)` | `coop-select:` |
| AI 奖励领取 | 反射 `SelectRewardForPlayer(ai, 未选下标)`，卡牌奖励由临时 selector 应答 | `coop-reward:` |
| 动作解堵 | 出牌 2.5 秒没离手 → `ActionExecutor.Cancel()` | `已取消卡住的动作` |
| 卡死看门狗 | 进度指纹 15 秒不变（1Hz 采样） | `coop-watch:` |
| 切视角 | F10 | `view:` |
| 角色选择 | 主菜单「和 AI 玩」→ 原版选人界面两段式（先我，再 AI，含 mod） | `chars: ...` |
| 休息点 | AI 选治疗/锻造/首个可用项；地图旅行等双方选完才放行 | `coop-rest:` |
| 宝箱遗物 | AI `OnPicked(ai,0)`；双方选完自动点继续 | `coop-treasure:` |
| 章节转场 | AI `OnPlayerReady(ai,act)` | `coop-act:` |

## 6. 挖出来的游戏 API 真相

以下全部用 IL 反汇编 + 元数据 dump 验证，不是猜的。

### 6.1 本地玩家

```csharp
LocalContext.NetId          // public static ulong?，可写；切视角就靠它
LocalContext.GetMe(IRunState) / IsMe(Player) / IsMine(CardModel/RelicModel/…)
```

没有 per-player 的"视角"概念，切视角 = 改这个值。

### 6.2 事件

```csharp
EventSynchronizer.GetLocalEvent() / GetEventForPlayer(Player)
EventSynchronizer.GetPlayerVote(Player)     // uint?，只有共享事件才写！
EventSynchronizer.ChooseLocalOption(int)    // public
EventSynchronizer.ChooseOptionForEvent(Player, int)   // private，AI 用反射
NEventRoom.Proceed()                        // static Task
EventModel.IsFinished / CurrentOptions
EventOption.TextKey / IsLocked / IsProceed / WasChosen
```

坑 1：`ChooseOptionForEvent` **不写** `_playerVotes`，独立事件里 `GetPlayerVote` 永远是 null，
不能拿它判断"选过没有"。改用 AI 自己那份事件的页指纹判断翻页。
`Proceed()` 内部同步解引用 `NMapScreen.Instance`，null 会 NRE。

坑 2：**共享事件（投票制）不能走 `ChooseOptionForEvent`**。本地 `ChooseLocalOption` 只调
`PlayerVotedForSharedOptionIndex` 写票；该方法在**票齐且 Type != Client** 时会自动调
`ChooseSharedEventOption`（单人档也满足），所以旧代码真正的问题是没给 AI 投票、票永远不齐。
现在 AI 用反射投同一票即可，票齐后游戏自己结算，**不要重复手动调**。`_pageIndex` 用私有字段读。

### 6.3 地图

```csharp
MapSelectionSynchronizer.GetVote(Player) -> MapVote?
MapSelectionSynchronizer.PlayerVotedForMapCoord(Player, MapLocation, MapVote?)
MapSelectionSynchronizer.OnLocationChanged(MapLocation)   // 清票 + 重设接收源
MapSelectionSynchronizer.MapGenerationCount               // 每张地图 +1，不是每节点
MapVote { int mapGenerationCount; MapCoord coord; }
MapLocation { int actIndex; MapCoord? coord; }
```

坑 1：`PlayerVotedForMapCoord` 第一句是
`if (_acceptingVotesFromSource != source) { Warn; return; }`，source 不匹配票会被静默丢弃，
且 Warn 只进游戏日志。source 必须用"当前所在位置"。

坑 2：`EnterMapCoord` = `AddVisitedMapCoord` + `EnterMapPointInternal(saveGame:true)`；
只调后者会进房间但不更新坐标。`saveGame:true` 在 `?` 节点有卡住记录。现在手动组合：
`AddVisitedMapCoord(coord)` + `EnterMapPointInternal(floor, type, null, saveGame:false)`。

坑 3：单人 net service 下投票永远不结算（`_votes` 齐了还有 host 判定）。
单人档不要让 AI 投票，自己调旅行。

### 6.4 战斗

```csharp
CombatManager.Instance.IsInProgress / DebugOnlyGetState()
    .IsPlayerReadyToEndTurn(Player) / AllPlayersReadyToEndTurn()
    .UndoReadyToEndTurn(Player)
PlayerTurnPhase { None=0, Start=1, AutoPrePlay=2, Play=3, AutoPostPlay=4, End=5 }
CombatSide { None=0, Player=1, Enemy=2 }
PlayerCombatState.Hand.Cards / Energy / Phase / TurnNumber
CardModel.Type / IsPlayable(protected) / EnergyCost / CanonicalEnergyCost(protected)
    / GainsBlock / TargetType / DynamicVars / Pile
MonsterModel.GetIntents()                    // private，见坑 1
MonsterModel.NextMove.Intents                // 当前招式的意图（正确的）
AttackIntent.GetTotalDamage(IEnumerable<Creature>, Creature)   // public，真实伤害
```

坑 1：`GetIntents()` 的 IL 是把**所有 MoveState** 的 `Intents` 全部 `AddRange` 后返回，
不是当前招式。旧代码拿它算"这回合伤害"，数值严重偏高。当前招式用 `NextMove.Intents`。
2026-10-26 已改。

坑 2：`CardEnergyCost` **没有** `Amount` / `Cost` 属性。旧反射永远失败，退到
`CanonicalEnergyCost`（基础费用），费用增减全被忽略。正确入口：
`CardEnergyCost.GetResolved()` = `max(0, GetWithModifiers(CostModifiers.All))`。

坑 3：`PlayerCmd.EndTurn(p,...)` 里有 `if (LocalContext.IsMe(p)) OnEndedTurnLocally();`，
结束的是**本地玩家**的回合。单人档不要用它，也别替 AI 结束回合（共享回合）。

坑 4：`AllPlayersReadyToEndTurn()` 可能在 `IsPlayerReadyToEndTurn(本地)=False` 时返回 True。

坑 5：`_aiLastTurn` 必须随 `CombatState` 实例变化重置，否则上一场第 5 回合会把新战斗 round=1 挡死。

坑 6：`_aiLastTurn` 要在**真正拿到出牌执行权之后**才记；旧顺序在此前先记，
若上一回合异步出牌没结束，这一回合会被永久跳过。2026-10-26 已调整。

### 6.5 选牌 / 奖励

```csharp
CardSelectCmd.PushSelector(ICardSelector, bool isLocal) -> IDisposable
CardSelectCmd.ShouldSelectLocalCard(Player) = LocalContext.IsMe(p) && NetService.Type != 4
ICardSelector.GetSelectedCards(IEnumerable<CardModel>, int min, int max)
ICardSelector.GetSelectedCardReward(IReadOnlyList<CardCreationResult>, IReadOnlyList<CardRewardAlternative>)
RewardsSetSynchronizer.GetNextRewardIds() / IsRewardsSetCompleted(Player, int)
RewardsSetSynchronizer.SelectRewardForPlayer(Player, int)   // private，AI 领奖
RewardsSetSynchronizer.SkipRewardsSetOnStackTopForPlayer(Player) // private，回退
```

坑 1：`PushSelector(..., false)` 挂到全局 `_selectorStack`；只要它非 null，
`FromSimpleGridForRewards` / `FromHand` 会**优先**用它——本地玩家的卡牌奖励、弃牌、预见
也会被自动应答并“跳过”。所以应答器绝不能整场常驻，只能在 AI 出牌/领奖的瞬间持有。

坑 3：AI 奖励集由 `RewardsSet.Offer` 里的 `BeginRewardsSet` 压栈，但非本地玩家没有 UI。
金币/药水/遗物走私有 `SelectRewardForPlayer(ai, 未选下标)` 即可；**卡牌奖励不能走它**：
`CardReward.OnSelect` 对非本地玩家直接 `WaitForRemoteChoice`，单人档抛
`Cannot wait for remote choice in singleplayer!`。卡牌奖励必须自己
`CardPileCmd.Add(card, PileType.Deck, Bottom, null, false)`，再反射把 `SuccessfullySelected`
置 true，并调 `CompleteRewardsSetIfNecessary`。连续失败才回退跳过。

### 6.6 位置

`RunState.CurrentRoom / CurrentMapCoord / VisitedMapCoords / MapLocation`，
以及 `RunRunLocation { MapLocation mapLocation; int? roomId; }` 都是全局的。
**`Player` 上没有任何地图坐标**，"队友没跟上来"通常不是位置问题。

## 7. 已修复的坑

| 现象 | 根因 |
|---|---|
| 事件结束不推进 | 以为有回调；实际要 `NEventRoom.Proceed()` |
| 进战斗黑屏 | 单人档让 AI 投票，`_votes` 齐了触发半截转场 |
| 打完怪地图不动 | 只调 `EnterMapPointInternal`，没调 `AddVisitedMapCoord` |
| 新战斗不出牌 | `_aiLastTurn` 跨战斗没重置 |
| 浴场无限加最大生命 | 离开事件后 `CurrentRoom` 仍是 EventRoom，AI 每 400ms 重放 |
| 格挡决策偏差 | `GetIntents()` 汇总全部招式伤害，`incoming` 虚高 |
| 本地选牌/奖励被跳过 | selector 整场常驻，全局 `Selector` 非 null，本地 UI 也走自动应答 |
| AI 奖励拿不到 | 只调 `Skip...`；非本地玩家没有 UI，没人选就一直等 |
| 卡牌奖励领不了 | `CardReward.OnSelect` 非本地走 `WaitForRemoteChoice`，单人抛异常 |
| 共享事件我选了他不选 | 单人档 Type!=Host，AI 没投票且没人调 `ChooseSharedEventOption` |

## 8. 2026-10-26 本轮优化

1. **看门狗 25Hz → 1Hz**：指纹里有一次反射 + 两次同步器枚举；`ScreenContext` 的
   Type/Property/Method 全部缓存。
2. **敌方意图伤害改对**：公开 `NextMove.Intents` + `AttackIntent.GetTotalDamage`，只算当前招式；
   不再反射 `GetIntents()`（全部招式的并集）。
3. **卡牌费用改对**：`EnergyCost.GetResolved()`，算上费用修改；X 费按 0。
   旧反射找 `Amount/Cost`（不存在），永远退到基础费用。
4. **卡牌伤害/格挡**：`DynamicVars.Damage/Block.IntValue`，不再每张牌两次 `GetProperty`。
5. **强类型枚举**：`CombatSide` / `PlayerTurnPhase` / `CardType` / `PileType` / `TargetType`，
   替代 `ToString()` 字符串比较。
6. **AI 奖励改为领取**：金币/药水/遗物 `SelectRewardForPlayer`；卡牌奖励手动
   `CardPileCmd.Add` + 标记 `SuccessfullySelected` + 完成奖励集；连续失败才 `Skip...`。
7. **共享事件投票 + selector 窗口**：AI 投同票，票齐后游戏自动结算；selector 只在
   AI 出牌时持有，多选按 min/max 取、动作完成前不卸；本地奖励/弃牌/预见不再被跳过。
验证：mod 重编译 0/0；tools 0/0；`deploy.ps1 -DryRun`（PS 5.1）与临时目录复制/备份/哈希通过。
**未做**：进游戏跑一局（2/3/5/6/7）。

实机：① 意图伤害对得上；② 费用修改生效；③ 本地奖励/弃牌/预见可点、AI 奖励到账；
④ 无重复 `coop loop`；⑤ 真卡仍出 `coop-watch`。若 AI 变笨，优先回退 §8 第 2、3 条；
不想要 `affects_gameplay:true` 就改回 `false`。

## 9. 未解决

1. **商店**：AI 不买，也不自动离开；商店界面开着时地图会暂停自动旅行，需本地点离开。
   Boss 遗物如果走 `RelicSelectCmd.FromChooseARelicScreen` 的远程选择，单人档仍可能卡。
2. **`PlayerChoiceSynchronizer`**：走 `ReserveChoiceId/WaitForRemoteChoice/SyncLocalChoice`
   这层 AI 就永远等不到应答（`待应答选择>0`）。先搞清 `PlayerChoiceResult` 类型，选错更糟。
3. **AI 阵亡后跳过回合**：已加 `UndoReadyToEndTurn` 缓解，未实机验证。
4. **AI 领奖边界**：药水满/特殊奖励可能失败，连续 3 次回退跳过；需按 `RewardType` 细化。

## 10. 日志速查

| 关键字 | 含义 |
|---|---|
| `coop-event:` | 事件跟选/推进/僵尸事件 |
| `coop-map:` | 地图旅行、清票、刷新图标 |
| `coop-combat:` | 新战斗、开局状态快照 |
| `coop-reward:` | 奖励集状态 / AI 领取 / 失败回退 |
| `coop-watch:` | **卡死现场**（15 秒无变化）——排错第一入口 |

看门狗会打 `屏幕 / 房间 / coord / visited / 战斗 / 事件 / 地图 / 奖励 / 选牌器 / 待应答选择`。
`待应答选择>0` 长期不降 → §9.2；`房间=RestSiteRoom/MerchantRoom` → §9.1；
`战斗=进行中` 且某玩家 `phase=None/Start` → 进场/加载层。

## 11. 调查工具

完整用法在 `tools\README.md`；最短路径（`$STS2` = 游戏 `sts2.dll`）：

```powershell
dotnet .\tools\apiprobe\bin\Release\net10.0\apiprobe.dll $STS2 .\out.txt EventSynchronizer
dotnet .\tools\ildump\bin\Release\net10.0\ildump.dll  $STS2 .\il.txt Runs.RunManager EnterMapCoord
```

不 `Assembly.Load`、不执行游戏代码；离线可构建。本轮所有 API 结论都来自它们，
**不确定的 API 先 dump 再写，不要猜。**

## 12. 下一步

1. 实机验证 §8 清单，跑一局确认无 `coop-watch:`。
2. 按 §9 攻：① 商店 → ② `PlayerChoiceSynchronizer` → ④ 奖励边界。

## 13. 禁忌

1. 不要用 `EnterMapCoordDebug` / 手改 `CurrentMapCoord` 跳过卡点；不要替 AI 调 `PlayerCmd.EndTurn`。
2. 不要在非主线程读写游戏对象（`RunOnMain` 就是为此存在）。
3. 不清楚 `PlayerChoiceResult` 类型时不要应答 `SyncLocalChoice`。
4. 不要同时改 `LocalContext.NetId` 和让 AI 自动操作（`_viewSwitched` 是互斥开关）。
5. 单人档不要让 AI 投地图票；不要用 `MonsterModel.GetIntents()` 算当前伤害。
6. 不要给 `tools` 项目加 NuGet 包（离线 restore 会失败）。
