# 调查工具：apiprobe / ildump

两个**只读**工具，用来在不加载、不运行游戏程序集的前提下，直接读 `sts2.dll` 的元数据和 IL。

这个项目里几乎所有"为什么卡住"的答案都是它们挖出来的：

- `RunManager.EnterMapCoord` 和 `EnterMapPointInternal` 到底差哪一步
- `PlayerCmd.EndTurn` 里的 `LocalContext.IsMe` 分支
- `MapSelectionSynchronizer.PlayerVotedForMapCoord` 为什么静默丢票
- `MonsterModel.GetIntents()` 返回的是"所有招式"而不是当前招式
- `CardEnergyCost` 根本没有 `Amount`/`Cost` 属性

> 工具不会 `Assembly.Load`，不会触发静态构造函数，不会碰游戏进程。
> 游戏开着也能读，但读的是磁盘上那一刻的 DLL。

---

## 1. 环境

| 项目 | 要求 |
|---|---|
| .NET SDK | 10.x（本机只装了 .NET 10 运行时/ SDK，工具目标框架固定 `net10.0`） |
| 网络 | 不需要。`tools\NuGet.config` 已 `<clear />` 所有包源 |
| 输出 | `UseAppHost=false`，产物是 `bin\Release\net10.0\*.dll`，用 `dotnet xxx.dll` 跑 |

> 不要给这两个 csproj 加 NuGet 包。离线环境下 restore 会直接失败。
> `UseAppHost` 也不能开：那会去要 `Microsoft.NETCore.App.Host` 包，而本机没有缓存。

---

## 2. 构建

在**工程根目录**（`Sts2Companion\`，也就是本 `tools\` 的上一级）执行：

```powershell
dotnet build .\tools\apiprobe\apiprobe.csproj -c Release
dotnet build .\tools\ildump\ildump.csproj   -c Release
```

产物：

```
tools\apiprobe\bin\Release\net10.0\apiprobe.dll
tools\ildump\bin\Release\net10.0\ildump.dll
```

> 旧布局 `D:\sts2_companion\tools\` 里也有一份同样的源码；两边构建方式一样。
> 改工具时优先改本目录，再同步过去。

---

## 3. apiprobe：读类型/成员/枚举值

```powershell
$STS2 = 'D:\steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll'
$PROBE = '.\tools\apiprobe\bin\Release\net10.0\apiprobe.dll'

dotnet $PROBE $STS2 .\out.txt MapSelectionSynchronizer EventSynchronizer
```

参数：

```
apiprobe <sts2.dll> <输出文件> [类型名包含的子串...]
```

- 过滤是**整条完整类型名**的大小写不敏感子串匹配；不传过滤就 dump 全部 9700+ 个类型（很大）。
- 可以传多个过滤词，结果取并集。
- 输出按类型分段：

```
=== class MegaCrit.Sts2.Core.Multiplayer.Game.MapSelectionSynchronizer  [Public, BeforeFieldInit]
    field  System.Collections.Generic.List`1<System.Nullable`1<...>> _votes   [Private, InitOnly]
    prop   int MapGenerationCount { get;set; }
    method public System.Nullable`1<...> GetVote(Player p0)
    nested <SomeNestedType>
```

| 行 | 含义 |
|---|---|
| `field` | 字段，含 `Private/InitOnly/Static/Literal` 等标记 |
| `prop` | 属性，`{ get;set; }` 表示可读写 |
| `method` | 方法；`public/private/protected/internal` 来自元数据 |
| 枚举字段 | 会额外打印常量值，例如 `PlayerTurnPhase Play = 3` |
| `nested` | 嵌套类型名 |

**最常用**：确认某个"看起来有"的属性到底存不存在、是不是 private、参数类型是什么。
比如 `CardEnergyCost` 只有 `Canonical`，没有 `Amount`/`Cost`，一看就知道旧代码反射找错了名字。

---

## 4. ildump：反汇编方法体

```powershell
$DUMP = '.\tools\ildump\bin\Release\net10.0\ildump.dll'

# 某个类型的所有方法
dotnet $DUMP $STS2 .\il.txt Runs.RunManager

# 只看某个方法（方法名子串）
dotnet $DUMP $STS2 .\il.txt Runs.RunManager EnterMapCoord
```

参数：

```
ildump <sts2.dll> <输出文件> <类型名包含的子串> [方法名包含的子串]
```

- 类型过滤是完整类型名的子串；方法过滤是方法名的子串（不是精确匹配）。
- 重载会全部输出，靠参数/IL 区分。
- `call/callvirt/newobj/ldfld/stfld/ldstr` 的操作数会解析成可读名字，直接能看调用链。
- 分支/switch 会打印目标偏移（`brtrue.s IL_000A`、`switch IL_0075, IL_019D`），方便判断 `IsMe` 这类分支走哪边。
- 输出示例：

```
===== MegaCrit.Sts2.Core.Runs.RunManager
-- EnterMapCoord
   IL_0000: ldarg.0
   IL_0015: callvirt RunState::AddVisitedMapCoord
   IL_0026: call RunManager::EnterMapCoordInternal
```

### 已知限制

- 不解析异常处理表、局部变量名、泛型实例的完整参数（会显示 `<...>` / `<spec>`）。
- `InlineSwitch` 只跳过操作数，不展开每个 case。
- abstract / extern（`RelativeVirtualAddress == 0`）的方法会被直接跳过，不打印。
- 工具只输出 IL，不做控制流图；复杂方法建议配合方法名过滤分段看。

---

## 5. 常用查询清单

先 `cd` 到工程根目录，并设好：

```powershell
$STS2 = 'D:\steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll'
$PROBE = '.\tools\apiprobe\bin\Release\net10.0\apiprobe.dll'
$DUMP  = '.\tools\ildump\bin\Release\net10.0\ildump.dll'
```

| 想确认 | 命令 |
|---|---|
| 事件同步器全部成员 | `dotnet $PROBE $STS2 .\o.txt EventSynchronizer` |
| 地图投票怎么校验 | `dotnet $DUMP $STS2 .\o.txt MapSelectionSynchronizer PlayerVotedForMapCoord` |
| 结束回合到底结束谁 | `dotnet $DUMP $STS2 .\o.txt Commands.PlayerCmd EndTurn` |
| 进地图节点做了哪几步 | `dotnet $DUMP $STS2 .\o.txt Runs.RunManager EnterMapCoord` |
| 奖励跳过作用于谁 | `dotnet $DUMP $STS2 .\o.txt RewardsSetSynchronizer SkipLocalRewardsSet` |
| 回合阶段枚举值 | `dotnet $PROBE $STS2 .\o.txt PlayerTurnPhase` |
| 卡牌有哪些属性 | `dotnet $PROBE $STS2 .\o.txt Models.CardModel` |
| 怪物当前意图怎么取 | `dotnet $PROBE $STS2 .\o.txt Models.MonsterModel MonsterMove` |
| 怪物"所有意图"的真相 | `dotnet $DUMP $STS2 .\o.txt Models.MonsterModel GetIntents` |
| 卡牌费用怎么算 | `dotnet $DUMP $STS2 .\o.txt Entities.Cards.CardEnergyCost GetResolved` |
| 生物/玩家属性 | `dotnet $PROBE $STS2 .\o.txt Entities.Creatures.Creature Entities.Players.Player` |

---

## 6. 三个真实例子（照着看）

### 6.1 为什么进房间要"手动标位置 + EnterMapPointInternal"

```powershell
dotnet $DUMP $STS2 .\il.txt Runs.RunManager EnterMapCoord
dotnet $DUMP $STS2 .\il.txt Runs.RunManager EnterMapPointInternal
```

`EnterMapCoord` 里能看到：

```
callvirt RunState::AddVisitedMapCoord
call     RunManager::EnterMapCoordInternal
```

而 `EnterMapPointInternal` 只建房间，不动 `CurrentMapCoord`。所以旧实现"房间进了、地图不动"。
现在用 `AddVisitedMapCoord(coord)` + `EnterMapPointInternal(floor, type, null, saveGame:false)` 的组合。

### 6.2 为什么 `GetIntents()` 不能用

```powershell
dotnet $DUMP $STS2 .\il.txt Models.MonsterModel GetIntents
```

IL 里是：

```
GenerateMoveStateMachine
  .States.Values
  foreach MoveState: List.AddRange(MoveState.Intents)
```

它把所有招式的意图全部拼起来，不是"当前招式"。当前招式在公开的 `MonsterModel.NextMove.Intents`。
旧代码拿它算"敌方这回合伤害"，数值会高得离谱；已改为 `NextMove.Intents` + `AttackIntent.GetTotalDamage`。

### 6.3 为什么投票会被静默丢弃

```powershell
dotnet $DUMP $STS2 .\il.txt MapSelectionSynchronizer PlayerVotedForMapCoord
```

开头就是：

```
if (_acceptingVotesFromSource != source) { Warn; return; }
```

`source` 必须是"当前所在位置"，否则票投进去但永远不结算。Warn 还打在游戏自己的日志里，
mod 日志看不到，所以表现为"没有任何反应"。

---

## 7. 排错

| 现象 | 处理 |
|---|---|
| `NETSDK1045` / 找不到 `net10.0` | 装 .NET 10 SDK；本机当前是 `10.0.300` |
| `NU1101` / restore 去联网 | 确认在 `tools\` 下有 `NuGet.config`（内容 `<clear />`），且没给 csproj 加包 |
| `找不到 sts2.dll` | 用完整路径；注意是 `data_sts2_windows_x86_64\sts2.dll`，不是游戏根目录 |
| 输出 0 个类型 | 过滤词拼错；先用 `Player`/`Card` 这种宽词试 |
| 输出太大 | 加过滤词，或先 `apiprobe` 找到完整类型名再 `ildump` |
| `ildump` 某个方法没输出 | 方法名子串不对，或该方法 `RelativeVirtualAddress == 0`（abstract/extern） |
| 中文乱码 | 输出文件是 UTF-8 no BOM；用支持 UTF-8 的编辑器打开 |
| 工具版本不匹配游戏更新 | 重新构建即可；工具不依赖游戏版本，只读元数据格式 |

---

## 8. 维护约定

1. 工具只读，**永远不要**为了"方便"去 `Assembly.Load` 或反射执行游戏代码。
2. 保持 `net10.0` + `UseAppHost=false` + 空 NuGet 源，保证离线可构建。
3. 新增查询示例时，把命令和"该看哪一行"一起写进本文件，别只贴命令。
4. 改完工具后至少跑一次 `dotnet build -c Release`，确认 0 error。
5. 调查结论（尤其是 private 字段/分支）优先写进 `HANDOFF.md` 的 API 真相一节，避免下个对话重新挖。
