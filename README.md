# STS2 Companion

![version](https://img.shields.io/badge/version-0.2.0-blue)
![license](https://img.shields.io/badge/license-MIT-green)
![game](https://img.shields.io/badge/Slay%20the%20Spire%202-v0.111.0-orange)

给《Slay the Spire 2》加一个**本地 AI 队友**的单机 Mod。

不是联机、不需要第二个玩家、不需要外部 AI 服务。游戏里会出现一个由 AI 控制的第二名角色，和你一起走地图、打牌、选事件、领奖励。

> 当前是 **0.2.0 可玩测试版**，不是 1.0 正式版。  
> 已知问题见下方 [当前已知问题](#当前已知问题)。

---

## 这是什么 Mod

《Slay the Spire 2》原本是单主角爬塔。这个 Mod 会在一局游戏里创建：

- **玩家 1**：你自己
- **玩家 2**：本地 AI 队友

AI 队友会自动处理：

- 战斗出牌（攻击、防御、能力牌，目标选择）
- 战斗后的金币、药水、卡牌奖励
- 事件选项、共享事件投票
- 地图路线
- 篝火（治疗 / 锻造 / 其它选项）
- 宝箱遗物选择
- 需要选牌、弃牌、预见的流程
- 章节转场就绪

主菜单会新增 **「和 AI 玩」** 入口，流程和原版单人游戏一样：

1. 选你自己玩的角色
2. 选 AI 队友的角色
3. 开始双人档

角色列表来自原版角色选择界面，所以**原版角色和已注册的 Mod 角色都会出现**。

---

## 支持的游戏版本

| 项目 | 值 |
|---|---|
| 开发 / 实测版本 | **Slay the Spire 2 v0.111.0** |
| Manifest 声明最低版本 | `0.107.1` |
| 游戏引擎 | Godot 4.5 / .NET 9 |
| Mod 类型 | 纯 DLL Mod（`has_dll: true`） |

说明：

- 我主要在 **v0.111.0** 上开发和实机测试。
- `Sts2Companion.json` 里写的最低版本是 `0.107.1`，但**旧版本没有逐一实机验证**。
- 游戏更新后如果内部 API 改名，Mod 可能需要更新。遇到问题请带日志提 Issue。

---

## 安装

### 1. 下载

到 Releases 下载最新压缩包：

**https://github.com/nnniy-code/Sts2Companion/releases/latest**

压缩包里应该只有两个文件：

```text
Sts2Companion.dll
Sts2Companion.json
```

### 2. 解压到 Mods 目录

把这两个文件放进：

```text
<你的游戏目录>\Slay the Spire 2\mods\Sts2Companion\
```

例如 Steam 默认安装：

```text
D:\steam\steamapps\common\Slay the Spire 2\mods\Sts2Companion\
```

正确结构：

```text
Slay the Spire 2\
└── mods\
    └── Sts2Companion\
        ├── Sts2Companion.dll
        └── Sts2Companion.json
```

不要多套一层文件夹，也不要改文件名。

### 3. 启动

- 如果游戏有 Mod 管理界面，确认 `STS2 Companion` 已启用。
- 完全重启游戏（不是只回主菜单）。
- 主菜单会出现 **「和 AI 玩」**。

如果安装了 **RitsuLib**，按钮会出现在它的主菜单滚动列里；没有安装 RitsuLib 时使用原版主菜单布局。

### 卸载

删除整个文件夹即可：

```text
Slay the Spire 2\mods\Sts2Companion\
```

---

## 怎么开始

1. 主菜单点击 **「和 AI 玩」**
2. 第一步：选你自己玩的角色，点 **「下一步：选队友（点这里）」**
3. 第二步：选 AI 队友，点 **「开始双人（点这里）」**
4. 进入游戏

AI 会自动行动，你正常玩自己的角色即可。

### 操作

| 按键 | 作用 |
|---|---|
| `F10` | 切换视角：临时接管 AI / 切回自己 |
| `T` | 打开聊天/指令输入框（如果当前版本启用） |

### 卡住时

先按 **F10** 切到 AI 视角，手动把当前选择点掉，再按 F10 切回。

日志位置：

```text
D:\sts2_companion\state\mod_error.log
```

如果没有 D 盘，会回退到：

```text
%APPDATA%\SlayTheSpire2\sts2_companion\mod_error.log
```

---

## 当前已知问题

这是 **0.2.0 测试版**，以下问题已知：

### 1. 商店

AI 不会主动买东西，也不会自动离开商店。

- 商店界面开着时，地图自动旅行会暂停，避免跳过商店。
- 需要你自己点商店的离开按钮。

### 2. Boss 遗物

章节转场的 AI ready 已处理，但 **Boss 遗物如果走远程选择接口，仍可能卡住**。

- 遇到时按 F10 切视角，手动选一个。
- 这个问题还没有完全解决。

### 3. 部分特殊事件 / 小游戏

有些事件使用独立 UI 或底层 `PlayerChoiceSynchronizer`，目前没有完全覆盖：

- 特殊选牌小游戏
- 部分多页事件
- 需要复杂确认的界面

如果卡住，按 F10 人工接管。

### 4. AI 不是高手

AI 的目标是“把事情做完”，不是“打出最优解”：

- 出牌顺序是简单规则
- 不一定会留药水/金币
- 不一定会做最优路线
- 面对复杂 Mod 卡牌时可能表现一般

### 5. Mod 角色兼容性

角色列表会读取 `ModelDb.AllCharacters`，所以 Mod 角色会出现。

但不同 Mod 角色的 `CharacterModel` 实现不同：

- 大部分可以正常创建
- 个别角色可能在 `Player.CreateForNewRun` 阶段失败
- 失败时会回退到铁甲/静默，并在日志里记录

### 6. 不是联机 Mod

这是**单机本地模拟双人**：

- 不需要第二个玩家
- 不支持网络联机
- 不支持两个真人同时加入

---

## 从源码构建

需要：

- .NET 9 SDK
- 《Slay the Spire 2》v0.111.0（或兼容版本）
- 游戏目录下的 `data_sts2_windows_x86_64\sts2.dll` 和 `GodotSharp.dll`

```powershell
git clone https://github.com/nnniy-code/Sts2Companion.git
cd Sts2Companion

dotnet build -c Release
```

如果游戏不在默认路径，构建时覆盖 `GameDir`：

```powershell
dotnet build -c Release `
  -p:GameDir="E:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64"
```

部署到游戏 Mods 目录：

```powershell
.\deploy.ps1
```

`deploy.ps1` 会自动探测 Steam 游戏目录；也可以用参数指定。

> 仓库不包含 `sts2.dll`、`GodotSharp.dll` 或游戏资源。构建时必须使用你本机安装的游戏文件。

---

## Release 下载

**最新版本：**

https://github.com/nnniy-code/Sts2Companion/releases/latest

Release 压缩包内容：

```text
Sts2Companion-0.2.0.zip
├── Sts2Companion.dll
└── Sts2Companion.json
```

如果 Releases 页面暂时没有文件，说明发布包还没上传；可以先按上面的“从源码构建”自行编译。

---

## 项目结构

```text
Sts2Companion/
├── Mod.cs                 入口、UI、启动双人档
├── CoopAI.cs              事件/地图/篝火/宝箱逻辑
├── CoopCombat.cs          战斗 AI、选牌、奖励
├── CoopView.cs            F10 切视角
├── CoopWatch.cs           卡住诊断
├── Sts2Companion.csproj   C# 工程
├── Sts2Companion.json     Mod manifest
├── deploy.ps1             一键编译 + 部署
├── HANDOFF.md             开发交接文档
├── MOD_NOTES.md           卡点/API 笔记
├── tools/                 只读调查工具（apiprobe / ildump）
├── README.md
├── LICENSE
└── CHANGELOG.md
```

---

## 贡献 / 反馈

欢迎提 Issue 或 Pull Request。

提 Bug 时请带上：

- 游戏版本
- 是否安装了 RitsuLib 等 UI Mod
- 卡住时的 `mod_error.log` 最后 50 行
- 卡住的房间类型（战斗 / 事件 / 篝火 / 商店 / 宝箱 / Boss）

---

## License

本项目使用 **MIT License**，详见 [LICENSE](LICENSE)。

你可以自由使用、修改、分发，包括商用；保留版权声明即可。

---

## 免责声明

这是一个非官方 Mod，与 Mega Crit 无关。

《Slay the Spire 2》及其资源版权归 Mega Crit 所有。本项目不包含游戏本体文件。
