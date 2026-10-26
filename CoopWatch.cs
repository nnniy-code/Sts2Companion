using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Companion;

/// <summary>
/// 通用"卡住"看门狗。
///
/// 这套双人档里，任何"等玩家点一下"的界面都可能把 AI 卡死：
/// 事件选项、休息点、商店、宝箱、奖励结算、选牌/删牌/升级/附魔、卡牌奖励、章节转场……
/// 我们只主动驱动了其中一部分，剩下的只能靠这个看门狗把现场抓下来。
///
/// 逻辑：每 1 秒算一次"进度指纹"，指纹 15 秒不变就认为卡住了，打一条完整现场。
/// 只读，不做任何自动推进——自动点未知按钮比卡住更危险。
/// </summary>
public static partial class CompanionMod
{
    private static string _watchSig = "";
    private static long _watchChangedMs;
    private static long _watchLastLogMs;
    private static int _watchStallCount;

    // 看门狗只需要 1Hz：15 秒的卡死判定用 1 秒采样完全够。
    // 以前它跟着 PollCoopAI 跑 25Hz，而指纹里有一次反射 + 两次 synchronizer 枚举，
    // 是主循环里最贵的一段纯开销。
    private static long _watchSampleMs;

    // ScreenContext 的反射结果缓存（类型/属性/方法），避免每次采样都 GetType/GetMethod。
    private static Type? _screenCtxType;
    private static PropertyInfo? _screenInstanceProp;
    private static MethodInfo? _screenGetMethod;
    private static long _screenResolveMs;

    private static void CoopWatchdog(RunState runState)
    {
        try
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (nowMs - _watchSampleMs < 1000) return;
            _watchSampleMs = nowMs;

            string sig = CoopProgressSignature(runState);
            if (sig != _watchSig)
            {
                _watchSig = sig;
                _watchChangedMs = nowMs;
                if (_watchStallCount > 0)
                {
                    LogError($"coop-watch: 状态恢复推进（之前连续卡住 {_watchStallCount} 次）");
                    _watchStallCount = 0;
                }
                return;
            }

            if (nowMs - _watchChangedMs < 15000) return;
            if (nowMs - _watchLastLogMs < 15000) return;   // 卡住期间每 15 秒最多一条
            _watchLastLogMs = nowMs;
            _watchStallCount++;

            LogError($"coop-watch: ⚠ 状态已 {(_watchChangedMs == 0 ? 0 : (nowMs - _watchChangedMs) / 1000)} 秒无变化"
                     + $"（第 {_watchStallCount} 次）—— 现场如下");
            LogError($"coop-watch:   屏幕={CoopScreenName()} 房间={runState.CurrentRoom?.GetType().Name ?? "null"} "
                     + $"coord={(runState.CurrentMapCoord is MapCoord mc ? $"({mc.col},{mc.row})" : "null")} "
                     + $"visited={Safe(() => runState.VisitedMapCoords.Count, -1)}");
            LogError($"coop-watch:   战斗={CoopCombatSummary(runState)}");
            LogError($"coop-watch:   事件={CoopEventSummary()} 地图={CoopMapSummary()} 奖励={CoopRewardSummary(runState)}");
            LogError($"coop-watch:   选牌器={(Safe(() => CardSelectCmd.Selector, null) != null ? "已挂上" : "未挂上")} "
                     + $"待应答选择={CoopChoiceSummary(runState)}");
        }
        catch (Exception ex)
        {
            LogError("CoopWatchdog: " + ex.Message);
        }
    }

    /// <summary>进度指纹：任何一项变化都说明游戏在往前走。</summary>
    private static string CoopProgressSignature(RunState runState)
    {
        var parts = new List<string>
        {
            CoopScreenName(),
            runState.CurrentRoom?.GetType().Name ?? "null",
            runState.CurrentMapCoord is MapCoord c ? $"{c.col},{c.row}" : "null",
            Safe(() => runState.VisitedMapCoords.Count, -1).ToString(),
            Safe(() => CombatManager.Instance.IsInProgress, false).ToString(),
        };

        var cs = Safe(() => CombatManager.Instance.DebugOnlyGetState(), (CombatState?)null);
        if (cs != null)
        {
            parts.Add(Safe(() => cs.RoundNumber, -1).ToString());
            parts.Add(Safe(() => cs.CurrentSide.ToString(), "?"));
            foreach (var p in cs.Players)
            {
                parts.Add($"{p?.NetId}:{Safe(() => p?.PlayerCombatState?.Phase.ToString() ?? "-", "-")}"
                          + $":{Safe(() => p?.Creature?.CurrentHp ?? -1, -1)}");
            }
        }

        var sync = Safe(() => RunManager.Instance.EventSynchronizer, (EventSynchronizer?)null);
        var ev = Safe(() => sync?.GetLocalEvent(), null);
        if (ev != null)
        {
            parts.Add($"ev:{Safe(() => ev.IsFinished, false)}:{Safe(() => ev.CurrentOptions.Count, -1)}");
        }

        var rss = Safe(() => RunManager.Instance.RewardsSetSynchronizer, (RewardsSetSynchronizer?)null);
        if (rss != null)
        {
            parts.Add("rw:" + string.Join("|", Safe(() => rss.GetNextRewardIds()?.ToList(), (List<int>?)null)
                                                     ?? new List<int>()));
        }

        return string.Join(" ", parts);
    }

    private static string CoopScreenName()
    {
        try
        {
            if (_screenCtxType == null || _screenInstanceProp == null || _screenGetMethod == null)
            {
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (nowMs - _screenResolveMs < 10000) return "?";
                _screenResolveMs = nowMs;

                _screenCtxType = Type.GetType(
                    "MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext.ActiveScreenContext, sts2",
                    throwOnError: false);
                if (_screenCtxType == null) return "?";

                _screenInstanceProp = _screenCtxType.GetProperty(
                    "Instance", BindingFlags.Public | BindingFlags.Static);
                _screenGetMethod = _screenCtxType.GetMethod(
                    "GetCurrentScreen", BindingFlags.Public | BindingFlags.Instance);
                if (_screenInstanceProp == null || _screenGetMethod == null) return "?";
            }

            object? inst = _screenInstanceProp.GetValue(null);
            if (inst == null) return "null";

            object? screen = _screenGetMethod.Invoke(inst, null);
            return screen?.GetType().Name ?? "null";
        }
        catch { return "?"; }
    }

    private static string CoopCombatSummary(RunState runState)
    {
        try
        {
            var cm = CombatManager.Instance;
            if (!Safe(() => cm.IsInProgress, false)) return "未进行";

            var cs = Safe(() => cm.DebugOnlyGetState(), (CombatState?)null);
            if (cs == null) return "进行中(无 state)";

            var players = new List<string>();
            foreach (var p in cs.Players)
            {
                if (p == null) continue;
                players.Add($"net{p.NetId}[{Safe(() => p.PlayerCombatState?.Phase.ToString() ?? "-", "-")}"
                            + $",hp={Safe(() => p.Creature?.CurrentHp ?? -1, -1)}"
                            + $",ready={Safe(() => cm.IsPlayerReadyToEndTurn(p), false)}]");
            }
            GameAction? action = Safe(
                () => RunManager.Instance?.ActionExecutor?.CurrentlyRunningAction,
                (GameAction?)null);
            string act = action == null ? "none" : $"{action.OwnerId}:{action.State}";
            return $"round={Safe(() => cs.RoundNumber, -1)} side={Safe(() => cs.CurrentSide.ToString(), "?")} "
                   + $"allReady={Safe(() => cm.AllPlayersReadyToEndTurn(), false)} action={act} "
                   + string.Join(" ", players);
        }
        catch (Exception ex) { return "异常:" + ex.Message; }
    }

    private static string CoopEventSummary()
    {
        try
        {
            var sync = RunManager.Instance.EventSynchronizer;
            if (sync == null) return "无";

            var ev = sync.GetLocalEvent();
            if (ev == null) return "无";

            return $"finished={ev.IsFinished} options={ev.CurrentOptions.Count} "
                   + $"[{string.Join(",", ev.CurrentOptions.Select(o => $"{o.TextKey}{(o.IsLocked ? "(锁)" : "")}{(o.IsProceed ? "(推进)" : "")}"))}]";
        }
        catch (Exception ex) { return "异常:" + ex.Message; }
    }

    private static string CoopMapSummary()
    {
        try
        {
            var sync = RunManager.Instance.MapSelectionSynchronizer;
            if (sync == null) return "无";
            var run = RunManager.Instance.DebugOnlyGetState();
            var local = run == null ? null : CoopLocalPlayer(run);
            var ai = run == null ? null : CoopAiPlayer(run);
            var lv = local == null ? null : CoopMapVoteOf(sync, local);
            var av = ai == null ? null : CoopMapVoteOf(sync, ai);
            return $"gen={sync.MapGenerationCount} "
                   + $"local={(lv == null ? "无" : $"({lv.Value.coord.col},{lv.Value.coord.row})")} "
                   + $"ai={(av == null ? "无" : $"({av.Value.coord.col},{av.Value.coord.row})")}";
        }
        catch (Exception ex) { return "异常:" + ex.Message; }
    }

    private static string CoopRewardSummary(RunState runState)
    {
        try
        {
            var rss = RunManager.Instance.RewardsSetSynchronizer;
            if (rss == null) return "无";
            var local = CoopLocalPlayer(runState);
            var ai = CoopAiPlayer(runState);
            var ids = Safe(() => rss.GetNextRewardIds()?.ToList(), (List<int>?)null) ?? new List<int>();
            if (ids.Count == 0) return "无待领";

            var parts = new List<string>();
            foreach (var id in ids)
            {
                parts.Add($"set{id}[本地={Safe(() => rss.IsRewardsSetCompleted(local!, id), false)}"
                          + $",AI={Safe(() => rss.IsRewardsSetCompleted(ai!, id), false)}]");
            }
            return string.Join(" ", parts);
        }
        catch (Exception ex) { return "异常:" + ex.Message; }
    }

    /// <summary>
    /// PlayerChoiceSynchronizer 的 pending choice —— 这是"选牌/选目标"最底层的等待点，
    /// 一旦有值且长时间不变，说明有个选择请求没人应答。
    /// </summary>
    private static string CoopChoiceSummary(RunState runState)
    {
        try
        {
            var pcs = RunManager.Instance.PlayerChoiceSynchronizer;
            if (pcs == null) return "无同步器";

            var ids = Safe(() => pcs.ChoiceIds, null);
            int count = ids?.Count ?? 0;
            if (count == 0) return "0";

            return $"{count} 个 ids=[{string.Join(",", ids!)}]";
        }
        catch (Exception ex) { return "异常:" + ex.Message; }
    }
}
