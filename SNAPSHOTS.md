# 版本快照 / 回滚点

## v0.4.0-stable2-20261027

- 记录时间：2026-10-27 11:07（Asia/Shanghai）
- Git commit：`e0f08c7`
- Git tag：`v0.4.0-stable2-20261027`
- DLL SHA256：
  `3076E2B3BD98610AB2938D17A961AA7AEAFF7BA5B3F87AA590DEBCCC49B42E4C`
- DLL 大小：164,864 字节
- 本机快照目录：
  `snapshots\v0.4.0-20261027-1107`

### 这个快照包含

- 3 人队伍（2~4 人框架）
- 每人单独选角色
- AI 顺序出牌 2 → 3 → 4
- 商店 AI 待机
- 聊天 / API / 本地模型 / 记忆 / 调教
- 事件奖励处理、地图旅行解锁、扣血保护、残留选牌清理

### 回滚方法

代码回滚：

```powershell
cd C:\Users\LENOVO\Documents\deepseek-harness\default-workspace\Sts2Companion
git checkout v0.4.0-stable2-20261027
```

只回滚 DLL：

```powershell
Copy-Item `
  "C:\Users\LENOVO\Documents\deepseek-harness\default-workspace\Sts2Companion\snapshots\v0.4.0-20261027-1107\Sts2Companion.dll" `
  "D:\steam\steamapps\workshop\content\2868840\3827739973\Sts2Companion\Sts2Companion.dll" -Force
```

---

## v0.4.0-stable-20261027

- 记录时间：2026-10-27 10:42（Asia/Shanghai）
- Git commit：`6383e3b`
- Git tag：`v0.4.0-stable-20261027`
- DLL SHA256：
  `F3A8344E48FF326BE0957764D0B25FD6E7130D2D38D01B4F8175368EC2D473D5`
- DLL 大小：164,352 字节
- 本机快照目录：
  `snapshots\v0.4.0-20261027-1042`
