using System;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Companion;

/// <summary>
/// 「切视角」人工兜底。
///
/// 原理：游戏用 <c>LocalContext.NetId</c>（公开可写的静态属性）判定"本地玩家是谁"。
/// 把它临时改到 AI 的 netId，接下来你在界面上点的东西（事件选项、选牌、结束回合……）
/// 就会作用在 AI 身上——正好用来救"AI 卡住"的场合。
///
/// 两条硬性约束：
///   1. 切过去期间必须暂停 AI 自动操作，否则它还会跟你抢同一个决策；
///   2. 切回来时必须重置 AI 的状态机（回合计数、事件页、地图旅行锁），
///      否则它会拿着旧缓存做出错误判断。
/// </summary>
public static partial class CompanionMod
{
    private static bool _viewSwitched;
    private static bool _f10WasDown;
    private static ulong _humanNetId = 1;

    /// <summary>切换视角。按钮和 F10 都调它。</summary>
    private static void ToggleView()
    {
        try
        {
            var run = RunManager.Instance?.DebugOnlyGetState();
            if (run == null)
            {
                LogError("view: 没有进行中的 run，无法切换");
                return;
            }

            var ai = CoopAiPlayer(run);
            var human = CoopLocalPlayer(run);   // 按 netId 找，不受视角影响
            if (ai == null || human == null)
            {
                LogError("view: 找不到两个玩家，无法切换");
                return;
            }

            _humanNetId = human.NetId;

            _viewSwitched = !_viewSwitched;
            ulong target = _viewSwitched ? ai.NetId : _humanNetId;

            LocalContext.NetId = target;

            if (_viewSwitched)
            {
                LogError($"view: 已切到 AI 视角（netId={target}）—— AI 自动操作暂停，"
                         + "现在你点的东西会作用在 AI 身上；再按一次切回");
            }
            else
            {
                CoopResetAfterViewSwitch();
                LogError($"view: 已切回本地视角（netId={target}）—— AI 自动操作恢复");
            }

            // 事件界面如果开着，提示一下按钮归属变化（不改逻辑，只打日志）
            var evRoom = NEventRoom.Instance;
            if (evRoom != null && Godot.GodotObject.IsInstanceValid(evRoom))
            {
                LogError("view: 事件界面正开着，选项现在归属"
                         + (_viewSwitched ? "AI" : "本地玩家"));
            }
        }
        catch (Exception ex)
        {
            LogError("view: 切换失败: " + ex.Message);
        }
    }

    /// <summary>切回本地视角时清掉 AI 的状态缓存，避免用旧回合/旧页面做判断。</summary>
    private static void CoopResetAfterViewSwitch()
    {
        _combatLogged = false;
        _aiDeadLogged = false;
        _aiPlaying = false;

        _evRoomHash = int.MinValue;   // 下一次 tick 当作"新事件房间"重新初始化
        _evAiSig.Clear();
        _evAiPicked.Clear();
        _evAiAttempts.Clear();
        _evAiStartHp.Clear();
        _evLocalDoneMs = 0;
        _evForcedProceedLogged = false;
        _evLastForceMs = 0;
        _evRoomChoices = 0;
        _evProceedRetries = 0;
        _evZombieLogged = false;

        _mapTravelStarted = false;
        _mapTravelCoord = null;
        _mapTravelRetries = 0;

        LogError("view: AI 状态缓存已重置");
    }
}
