using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Companion;

/// <summary>
/// 双人档里 AI 队友的"跟人"逻辑：事件房间跟选、地图跟票。
///
/// 只用 sts2.dll 的公开 API，唯一需要反射的是 EventSynchronizer.ChooseOptionForEvent（私有）。
/// 核心原则：**读本地玩家的动作，替 AI 做同样的动作**，绝不替本地玩家做决定。
///
/// 关键 API（从 sts2.dll 元数据确认）：
///   EventSynchronizer.GetPlayerVote(Player)  -> uint?   该玩家本轮是否已选
///   EventSynchronizer.GetEventForPlayer(Player) -> EventModel?  每个玩家自己那份事件
///   EventSynchronizer.IsShared -> bool                  是否共享事件（投票制）
///   MapSelectionSynchronizer.GetVote(Player) -> MapVote?
///   MapSelectionSynchronizer.PlayerVotedForMapCoord(Player, MapLocation, MapVote?)
///   NEventRoom.Proceed() -> Task                        static，推进到下一间
/// </summary>
public static partial class CompanionMod
{
    // ---------- 通用 ----------

    private static Player? CoopAiPlayer(RunState runState)
    {
        // 这套双人档的约定：AI 固定 netId=2
        foreach (var p in runState.Players)
        {
            if (p != null && p.NetId == 2uL) return p;
        }
        return null;
    }

    private static Player? CoopLocalPlayer(RunState runState)
    {
        Player? fallback = null;
        foreach (var p in runState.Players)
        {
            if (p == null) continue;
            if (p.NetId != 2uL) return p;
            fallback ??= p;
        }
        return fallback;
    }

    /// <summary>战斗中按 netId 找 AI 玩家（不要用数组下标，顺序不保证）。</summary>
    private static Player? CoopAiPlayerInCombat(CombatState cs)
    {
        try
        {
            foreach (var p in cs.Players)
            {
                if (p != null && p.NetId == 2uL) return p;
            }
        }
        catch { }
        return null;
    }

    // ---------- 问题1：事件房间 ----------

    private static int _evRoomHash = int.MinValue;
    private static string _evPrevSig = "";
    private static long _evLastTryMs;
    private static bool _evAdvanced;
    private static bool _evAiPickedPage;
    private static int _evPageAttempts;
    private static int _evRoomChoices;
    private static int _evProceedRetries;
    private static long _evAdvancedMs;
    private static bool _evZombieLogged;
    private static MethodInfo? _miChooseForEvent;

    // 共享事件（投票制）走 EventSynchronizer 的另一条路径：
    // ChooseLocalOption 只写票；PlayerVotedForSharedOptionIndex 在票齐且 Type != Client 时
    // 会自己调 ChooseSharedEventOption（单人档也满足），所以我们只需要让 AI 也投同一票。
    private static MethodInfo? _miVoteSharedOption;
    private static FieldInfo? _fiEventPageIndex;

    /// <summary>
    /// 每帧调用。三条规则：
    ///   1. 本地玩家没选 → 什么都不做（让他自己选）
    ///   2. 本地玩家选了 → AI 跟选（多页事件每翻一页跟一次）
    ///   3. 两人都选完、事件结束、UI 出现 Proceed → 推进到下一间
    /// </summary>
    private static void CoopEventTick(RunState runState, AbstractRoom room)
    {
        if (room is not EventRoom) return;

        // 切到 AI 视角时暂停自动操作，把决策权交给人工
        if (_viewSwitched) return;

        // ★ 关键：只有事件界面真的开着才处理。
        // 玩家离开事件房间后，runState.CurrentRoom 可能还停留在 EventRoom（转场没刷新），
        // 这时继续调 ChooseOptionForEvent 会不断重放选项效果——
        // 浴场事件刷最大生命值就是这么来的：本地事件已 finished、屏幕早就是地图了，AI 还在选。
        var eventScreen = NEventRoom.Instance;
        if (eventScreen == null || !Godot.GodotObject.IsInstanceValid(eventScreen)) return;

        var sync = RunManager.Instance?.EventSynchronizer;
        if (sync == null) return;

        int hash = room.GetHashCode();
        if (hash != _evRoomHash)
        {
            _evRoomHash = hash;
            _evPrevSig = "";
            _evLastTryMs = 0;
            _evAdvanced = false;
            _evRoomChoices = 0;
            _evPageAttempts = 0;
            _evProceedRetries = 0;
            _evZombieLogged = false;
            LogError($"coop-event: 进入事件 shared={Safe(() => sync.IsShared, false)} "
                     + $"page={CoopEventPage(sync)} room={room.GetType().Name}");
        }

        var local = CoopLocalPlayer(runState);
        var ai = CoopAiPlayer(runState);
        if (local == null || ai == null) return;

        // GetEventForPlayer 在事件刚创建、_events 还没填满时会抛 ArgumentOutOfRangeException，
        // 一旦抛出去会把整个 PollCoopAI 打断（地图/战斗逻辑一起跳过），所以这里包住。
        var localEvent = CoopEventFor(sync, local) ?? CoopLocalEvent(sync);
        if (localEvent == null) return;
        var aiEvent = CoopEventFor(sync, ai);

        // ---- 每页独立决策 ----
        // 重要：ChooseOptionForEvent 不写 _playerVotes（只有共享事件的 PlayerVotedForSharedOptionIndex 才写），
        // 所以独立事件里 GetPlayerVote 永远是 null，"本地已选/AI 已选"都不能靠它判断。
        // 改用「AI 自己那份事件的页指纹」：页一变就允许再选一次，保证多环节事件能一页页走完。
        string aiSig = aiEvent != null ? CoopEventSignature(aiEvent) : "";
        if (aiSig != _evPrevSig)
        {
            _evPrevSig = aiSig;
            _evAiPickedPage = false;
            _evPageAttempts = 0;
        }

        bool localDone = localEvent.IsFinished;
        bool aiDone = aiEvent?.IsFinished ?? localDone;

        // ★ 僵尸事件闸门：本地事件已结束、当前页没有任何选项，但 AI 那份还没结束。
        // 这种情况再调 ChooseOptionForEvent 只会把选项效果重放一遍
        // （浴场事件无限加最大生命值就是这条路径）。
        if (localDone && localEvent.CurrentOptions.Count == 0)
        {
            if (aiEvent != null && !aiEvent.IsFinished && !_evZombieLogged)
            {
                _evZombieLogged = true;
                LogError("coop-event: 本地事件已结束但 AI 的未结束 —— 判定为僵尸事件，停止跟选");
            }
            return;
        }

        // 共享事件是投票制：不能走 ChooseOptionForEvent，否则会读到另一份事件/空选项，
        // 表现就是“我选了他不选”+ IndexOutOfRange。这里让 AI 投同一票并手动结算。
        if (Safe(() => sync.IsShared, false))
        {
            CoopSharedEventVote(sync, local, ai, aiEvent);
        }
        else if (aiEvent != null && !aiEvent.IsFinished && !_evAiPickedPage
            && _evPageAttempts < 8 && _evRoomChoices < 6)
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (nowMs - _evLastTryMs >= 400)   // 限流：选择没生效时不要每帧刷
            {
                _evLastTryMs = nowMs;
                _evPageAttempts++;
                if (CoopChooseForAi(sync, local, ai, aiEvent, localEvent))
                {
                    _evAiPickedPage = true;
                    _evRoomChoices++;
                    if (_evRoomChoices >= 6)
                    {
                        LogError("coop-event: 本事件已跟选 6 次，停止（防止选项效果被重复触发）");
                    }
                }
                else if (_evPageAttempts >= 8)
                {
                    LogError("coop-event: 本页已尝试 8 次仍失败，停止重试（等翻页或人工处理）");
                }
            }
        }

        // 两人的事件都结束 → 推进；推进 8 秒没生效就重试（最多 3 次），避免 Proceed 静默失败后卡死
        if (!_evAdvanced && localDone && (aiDone || aiEvent == null))
        {
            CoopProceed(localEvent, aiEvent);
        }
        else if (_evAdvanced && _evProceedRetries < 3
                 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _evAdvancedMs > 8000)
        {
            _evProceedRetries++;
            _evAdvanced = false;
            LogError($"coop-event: Proceed 8 秒未生效，重试（第 {_evProceedRetries} 次）");
        }
    }

    private static uint CoopEventPage(EventSynchronizer sync)
    {
        try
        {
            _fiEventPageIndex ??= typeof(EventSynchronizer).GetField(
                "_pageIndex", BindingFlags.Instance | BindingFlags.NonPublic);
            object? value = _fiEventPageIndex?.GetValue(sync);
            return value is uint page ? page : 0u;
        }
        catch { return 0u; }
    }

    private static bool CoopVoteShared(EventSynchronizer sync, Player ai, uint optionIndex, uint pageIndex)
    {
        try
        {
            _miVoteSharedOption ??= typeof(EventSynchronizer).GetMethod(
                "PlayerVotedForSharedOptionIndex",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (_miVoteSharedOption == null)
            {
                LogError("coop-event: 找不到 PlayerVotedForSharedOptionIndex");
                return false;
            }
            _miVoteSharedOption.Invoke(sync, new object?[] { ai, optionIndex, pageIndex });
            return true;
        }
        catch (Exception ex)
        {
            LogError("coop-event: 共享事件投票崩: " + (ex.InnerException ?? ex).Message);
            return false;
        }
    }

    /// <summary>
    /// 共享事件：本地玩家投谁，AI 就投谁。
    /// PlayerVotedForSharedOptionIndex 在票齐且 Type != Client 时会自己调
    /// ChooseSharedEventOption（单人档也满足），所以不要重复手动结算。
    /// </summary>
    private static void CoopSharedEventVote(EventSynchronizer sync, Player local, Player ai, EventModel? aiEvent)
    {
        try
        {
            if (aiEvent == null || aiEvent.IsFinished) return;

            uint page = CoopEventPage(sync);
            var localVote = CoopVoteOf(sync, local);
            if (!localVote.HasValue) return;   // 本地还没投，等着

            int index = (int)localVote.Value;
            if (index < 0 || index >= aiEvent.CurrentOptions.Count) return;
            if (aiEvent.CurrentOptions[index].IsLocked || aiEvent.CurrentOptions[index].IsProceed) return;

            var aiVote = CoopVoteOf(sync, ai);
            if (aiVote.HasValue && aiVote.Value == (uint)index) return;

            if (CoopVoteShared(sync, ai, (uint)index, page))
            {
                LogError($"coop-event: 共享事件 AI 投票 index={index} page={page}（票齐后游戏自动结算）");
            }
        }
        catch (Exception ex)
        {
            LogError("coop-event: 共享事件处理崩: " + ex.Message);
        }
    }

    /// <summary>GetEventForPlayer 的安全包装：事件刚创建时它会越界。</summary>
    private static EventModel? CoopEventFor(EventSynchronizer sync, Player p)
    {
        try { return sync.GetEventForPlayer(p); }
        catch { return null; }
    }

    private static EventModel? CoopLocalEvent(EventSynchronizer sync)
    {
        try { return sync.GetLocalEvent(); }
        catch { return null; }
    }

    /// <summary>GetPlayerVote 的安全包装：_playerVotes 还没按玩家数填满时会越界。</summary>
    private static uint? CoopVoteOf(EventSynchronizer sync, Player p)
    {
        try { return sync.GetPlayerVote(p); }
        catch { return null; }
    }

    private static bool CoopHasProceedOption(EventModel ev)
    {
        foreach (var o in ev.CurrentOptions)
        {
            if (o.IsProceed) return true;
        }
        return false;
    }

    /// <summary>事件状态指纹：是否结束 + 每个选项的文本/锁/已选/推进标记。</summary>
    private static string CoopEventSignature(EventModel ev)
    {
        var sb = new StringBuilder();
        sb.Append(ev.IsFinished ? '1' : '0');
        foreach (var o in ev.CurrentOptions)
        {
            sb.Append('|').Append(o.TextKey)
              .Append(o.IsLocked ? 'L' : '-')
              .Append(o.WasChosen ? 'W' : '-')
              .Append(o.IsProceed ? 'P' : '-');
        }
        return sb.ToString();
    }

    /// <summary>
    /// 替 AI 选一个选项：优先跟本地玩家同一个下标，否则取 AI 自己第一个可选项。
    /// 注意用的是 AI 自己那份 EventModel 的选项表（随机事件两人选项可能不同）。
    /// </summary>
    private static bool CoopChooseForAi(
        EventSynchronizer sync, Player local, Player ai, EventModel? aiEvent, EventModel localEvent)
    {
        // AI 还没有自己的 EventModel（_events 未就绪）时不能调 ChooseOptionForEvent，
        // 它内部按下标取 _events，会抛 ArgumentOutOfRangeException。
        if (aiEvent == null)
        {
            LogError("coop-event: AI 的 EventModel 还没就绪，跳过这次跟选");
            return false;
        }

        var options = aiEvent.CurrentOptions;
        if (options == null || options.Count == 0) return false;

        bool Valid(int v) => v >= 0 && v < options.Count && !options[v].IsLocked && !options[v].IsProceed;

        int index = -1;

        // ① 共享事件：投票槽里就是本地玩家这一页选的下标
        var localVote = CoopVoteOf(sync, local);
        if (localVote.HasValue && Valid((int)localVote.Value)) index = (int)localVote.Value;

        // ② 独立事件：本地事件里被标记 WasChosen 的那一项，就是玩家刚点的下标
        if (index < 0)
        {
            var localOptions = localEvent.CurrentOptions;
            if (localOptions != null)
            {
                for (int i = 0; i < localOptions.Count; i++)
                {
                    if (localOptions[i].WasChosen && Valid(i)) { index = i; break; }
                }
            }
        }

        // ③ 退而求其次：AI 自己第一个可选项（保证事件能一页页走完，不会卡死）
        if (index < 0)
        {
            for (int i = 0; i < options.Count; i++)
            {
                if (options[i].IsLocked || options[i].IsProceed) continue;
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            LogError("coop-event: AI 没有可选项（全被锁 / 只剩 Proceed）");
            return false;
        }

        // 事件可能在这一帧翻页/被清空；用最新的一份再校验一次，避免 stale index。
        var freshAi = CoopEventFor(sync, ai);
        if (freshAi == null || freshAi.IsFinished || freshAi.CurrentOptions.Count <= index
            || freshAi.CurrentOptions[index].IsLocked || freshAi.CurrentOptions[index].IsProceed)
        {
            LogError($"coop-event: AI 事件已变化（options={freshAi?.CurrentOptions.Count ?? -1}），本页重试");
            return false;
        }

        try
        {
            _miChooseForEvent ??= typeof(EventSynchronizer).GetMethod(
                "ChooseOptionForEvent",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            if (_miChooseForEvent == null)
            {
                LogError("coop-event: 找不到 EventSynchronizer.ChooseOptionForEvent");
                return false;
            }

            _miChooseForEvent.Invoke(sync, new object?[] { ai, index });
            LogError($"coop-event: AI 跟选 index={index}（{options[index].TextKey}）");
            return true;
        }
        catch (TargetInvocationException ex)
        {
            var inner = ex.InnerException ?? ex;
            // "已经结束"说明这次选择本来就完成了，不是失败
            if (inner.Message.Contains("finish", StringComparison.OrdinalIgnoreCase)) return true;
            LogError($"coop-event: AI 选崩 index={index}: {inner.Message}");
        }
        catch (Exception ex)
        {
            LogError($"coop-event: AI 选崩 index={index}: {ex.Message}");
        }
        return false;
    }

    /// <summary>两人都选完 → 事件房间已显示 Proceed 时推进到下一间。</summary>
    private static void CoopProceed(EventModel localEvent, EventModel? aiEvent)
    {
        // 事件还没结束，或 UI 还没刷出 Proceed 选项 → 下一帧再看
        if (!localEvent.IsFinished) return;
        if (!CoopHasProceedOption(localEvent)) return;

        // NEventRoom.Proceed() 内部同步解引用 NMapScreen.Instance，转场瞬间它是 null
        if (NMapScreen.Instance == null) return;

        _evAdvanced = true;
        _evAdvancedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            var task = NEventRoom.Proceed();
            if (task != null)
            {
                _ = task.ContinueWith(
                    t => LogError("coop-event: Proceed 异常: "
                                  + (t.Exception?.InnerException?.Message ?? "?")),
                    System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
            }
            LogError("coop-event: 两人都选完 → NEventRoom.Proceed()");
        }
        catch (Exception ex)
        {
            _evAdvanced = false;
            LogError($"coop-event: Proceed 崩: {ex.Message}");
        }
    }

    // ---------- 休息点（篝火） ----------

    private static int _restRoomHash = int.MinValue;
    private static bool _restChoiceSent;
    private static long _restLastTryMs;
    private static MethodInfo? _miRestChoose;

    /// <summary>
    /// 休息点：AI 也要选一个选项（治疗/锻造/其它），否则双人档会一直等。
    /// 优先跟本地玩家的选择；本地还没选时，AI 自己按"缺血治疗、否则锻造、再否则第一个可用"。
    /// </summary>
    private static void CoopRestSiteTick(RunState runState, RestSiteRoom room)
    {
        if (_viewSwitched) return;

        var sync = RunManager.Instance?.RestSiteSynchronizer;
        if (sync == null) return;

        var local = CoopLocalPlayer(runState);
        var ai = CoopAiPlayer(runState);
        if (local == null || ai == null) return;

        int hash = room.GetHashCode();
        if (hash != _restRoomHash)
        {
            _restRoomHash = hash;
            _restChoiceSent = false;
            _restLastTryMs = 0;
            LogError("coop-rest: 进入休息点");
        }

        if (_restChoiceSent) return;

        int? aiChosen = Safe(() => sync.GetChosenOptionIndex(ai.NetId), (int?)null);
        if (aiChosen.HasValue)
        {
            _restChoiceSent = true;
            LogError($"coop-rest: AI 已选过 index={aiChosen.Value}");
            return;
        }

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (nowMs - _restLastTryMs < 400) return;
        _restLastTryMs = nowMs;

        var options = Safe(() => sync.GetOptionsForPlayer(ai), (IReadOnlyList<RestSiteOption>?)null);
        if (options == null || options.Count == 0) return;

        int? localChosen = Safe(() => sync.GetChosenOptionIndex(local.NetId), (int?)null);
        int index = -1;

        // 1) 缺血优先治疗（避免 AI 为了跟锻造把自己送掉，也减少卡牌选择界面冲突）
        if (Safe(() => ai.Creature.CurrentHp < ai.Creature.MaxHp, false))
        {
            for (int i = 0; i < options.Count; i++)
            {
                if (options[i] is HealRestSiteOption && options[i].IsEnabled) { index = i; break; }
            }
        }

        // 2) 不缺血时跟本地玩家的选择（同一套休息点选项通常下标一致）
        if (index < 0 && localChosen.HasValue && localChosen.Value >= 0 && localChosen.Value < options.Count
            && options[localChosen.Value].IsEnabled)
        {
            index = localChosen.Value;
        }

        // 3) 否则锻造
        if (index < 0)
        {
            for (int i = 0; i < options.Count; i++)
            {
                if (options[i] is SmithRestSiteOption && options[i].IsEnabled) { index = i; break; }
            }
        }

        // 4) 再否则第一个可用项
        if (index < 0)
        {
            for (int i = 0; i < options.Count; i++)
            {
                if (options[i].IsEnabled) { index = i; break; }
            }
        }

        if (index < 0)
        {
            LogError("coop-rest: 没有可用的休息点选项");
            return;
        }

        // 本地玩家正在挑牌时先别抢答；锻造/复制/烹饪会走 CardSelectCmd。
        if (Safe(() => CoopLocalCardScreenOpen(), false)) return;

        IDisposable? selectorScope = null;
        bool needsSelector = options[index] is SmithRestSiteOption
                             or CloneRestSiteOption
                             or CookRestSiteOption;
        if (needsSelector)
        {
            try
            {
                selectorScope = CardSelectCmd.PushSelector(new AiCardSelector(), false);
                LogError($"coop-rest: 已挂上选牌应答器（{Safe(() => options[index].OptionId, "?")}）");
            }
            catch (Exception ex)
            {
                LogError("coop-rest: PushSelector 失败: " + ex.Message);
            }
        }

        try
        {
            _miRestChoose ??= typeof(RestSiteSynchronizer).GetMethod(
                "ChooseOption",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(Player), typeof(int) },
                modifiers: null);

            if (_miRestChoose == null)
            {
                LogError("coop-rest: 找不到 ChooseOption");
                try { selectorScope?.Dispose(); } catch { }
                return;
            }

            object? result = _miRestChoose.Invoke(sync, new object?[] { ai, index });
            _restChoiceSent = true;
            LogError($"coop-rest: AI 选择 {Safe(() => options[index].OptionId, "?")} (index={index})");

            if (result is Task task)
            {
                _ = task.ContinueWith(
                    t =>
                    {
                        bool failed = t.IsFaulted || t.IsCanceled
                                      || (t.Status == TaskStatus.RanToCompletion
                                          && t is Task<bool> tb && !tb.Result);
                        if (t.IsFaulted)
                        {
                            LogError("coop-rest: ChooseOption 异常: "
                                     + (t.Exception?.InnerException?.Message ?? "?"));
                        }
                        RunOnMain(() =>
                        {
                            try { selectorScope?.Dispose(); } catch { }
                            if (!failed && !Safe(() => sync.GetChosenOptionIndex(ai.NetId).HasValue, false))
                            {
                                LogError("coop-rest: ChooseOption 完成但没记录选择，允许重试");
                                failed = true;
                            }
                            if (failed)
                            {
                                _restChoiceSent = false;
                                _restLastTryMs = 0;
                            }
                        });
                    },
                    TaskContinuationOptions.ExecuteSynchronously);
            }
            else
            {
                try { selectorScope?.Dispose(); } catch { }
            }
        }
        catch (Exception ex)
        {
            try { selectorScope?.Dispose(); } catch { }
            LogError("coop-rest: 选择崩: " + (ex.InnerException ?? ex).Message);
        }
    }

    // ---------- 宝箱 / 遗物 ----------

    private static int _treasureRoomHash = int.MinValue;
    private static bool _treasurePicked;
    private static bool _treasurePickingStarted;
    private static long _treasureProceedAtMs;
    private static bool _treasureProceedSent;

    private static void CoopTreasureTick(RunState runState, TreasureRoom room)
    {
        var sync = RunManager.Instance?.TreasureRoomRelicSynchronizer;
        if (sync == null) return;

        var ai = CoopAiPlayer(runState);
        if (ai == null) return;

        int hash = room.GetHashCode();
        if (hash != _treasureRoomHash)
        {
            _treasureRoomHash = hash;
            _treasurePicked = false;
            _treasurePickingStarted = false;
            _treasureProceedAtMs = 0;
            _treasureProceedSent = false;
            LogError("coop-treasure: 进入宝箱房");
        }

        if (Safe(() => sync.CurrentRelics != null, false))
        {
            _treasurePickingStarted = true;
        }

        // 即使切到 AI 视角，也要在双方选完后自动点"继续"，否则宝箱会停住。
        CoopTreasureProceedTick(sync);

        if (_viewSwitched) return;
        if (_treasurePicked) return;
        if (sync.GetPlayerVote(ai) != null) { _treasurePicked = true; return; }

        var relics = Safe(() => sync.CurrentRelics, (IReadOnlyList<RelicModel>?)null);
        if (relics == null) return;
        if (relics.Count == 0)
        {
            sync.CompleteWithNoRelics();
            _treasurePicked = true;
            LogError("coop-treasure: 没有遗物，直接完成");
            return;
        }

        try
        {
            // OnPicked 是 public 的按玩家入口；index=0 表示拿第一个。
            sync.OnPicked(ai, 0);
            _treasurePicked = true;
            LogError($"coop-treasure: AI 选择遗物 index=0 "
                     + $"({Safe(() => relics[0].Id.Entry, "?")})");
        }
        catch (Exception ex)
        {
            LogError("coop-treasure: OnPicked 崩: " + ex.Message);
        }
    }

    private static void CoopTreasureProceedTick(TreasureRoomRelicSynchronizer sync)
    {
        if (!_treasurePickingStarted || _treasureProceedSent) return;

        // 还在选：CurrentRelics 非空，不点继续
        if (Safe(() => sync.CurrentRelics != null, false))
        {
            _treasureProceedAtMs = 0;
            return;
        }

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_treasureProceedAtMs == 0)
        {
            _treasureProceedAtMs = nowMs;
            return;
        }

        // 留 1.2 秒给遗物飞入/奖励动画，再自动点继续。
        if (nowMs - _treasureProceedAtMs < 1200) return;

        _treasureProceedSent = true;
        LogError("coop-treasure: 遗物选完，自动点继续");

        try
        {
            var rm = RunManager.Instance;
            if (rm == null) return;
            Task task = rm.ProceedFromTerminalRewardsScreen();
            if (task != null)
            {
                _ = task.ContinueWith(
                    t => LogError("coop-treasure: Proceed 异常: "
                                  + (t.Exception?.InnerException?.Message ?? "?")),
                    TaskContinuationOptions.OnlyOnFaulted);
            }
        }
        catch (Exception ex)
        {
            LogError("coop-treasure: Proceed 崩: " + ex.Message);
        }
    }

    // ---------- 章节转场 / Boss 遗物 ready ----------

    private static int _actReadyIndex = -1;

    private static void CoopActChangeTick(RunState runState)
    {
        try
        {
            var sync = RunManager.Instance?.ActChangeSynchronizer;
            var ai = CoopAiPlayer(runState);
            if (sync == null || ai == null) return;

            int act = runState.CurrentActIndex;
            if (_actReadyIndex == act) return;
            if (!sync.IsWaitingForOtherPlayers()) return;

            sync.OnPlayerReady(ai, act);
            _actReadyIndex = act;
            LogError($"coop-act: AI 已准备进入下一幕 act={act}");
        }
        catch (Exception ex)
        {
            LogError("coop-act: " + ex.Message);
        }
    }

    // ---------- 问题2：地图跟票 ----------

    private static long _mapLastMs;    private static int _mapTriedGen = -1;
    private static long _mapRestBlockMs;
    private static long _mapTreasureBlockMs;
    private static long _mapShopBlockMs;
    private static int _mapTries;
    private static bool _mapBothLogged;
    private static bool _mapTravelStarted;
    private static MapCoord? _mapTravelCoord;
    private static int _mapTravelRetries;
    private static long _mapTravelAtMs;

    /// <summary>
    /// 每帧调用（内部限流 200ms）。玩家先投票 → AI 投同一个 coord。
    /// 走游戏自己的 PlayerVotedForMapCoord，不用 EnterMapCoordDebug。
    /// </summary>
    private static void CoopMapTick(RunState runState)
    {
        // 切到 AI 视角时暂停，人工自己选
        if (_viewSwitched) return;
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (nowMs - _mapLastMs < 200) return;
        _mapLastMs = nowMs;

        var sync = RunManager.Instance?.MapSelectionSynchronizer;
        if (sync == null) return;

        // 关键：地图界面上 runState.CurrentRoom 经常是 null，所以判据必须是"地图屏幕开着"。
        // 之前用 room is MapRoom 判断 + PollCoopAI 里的 curRoom==null 提前 return，
        // 导致这里一次都没跑到过（日志里完全没有 coop-map 就是这个原因）。
        var screen = NMapScreen.Instance;
        if (screen == null || !screen.IsOpen) return;

        var local = CoopLocalPlayer(runState);
        var ai = CoopAiPlayer(runState);
        if (local == null || ai == null) return;

        // 休息点里地图界面可能已经能打开；双方没选完之前绝不能自动旅行，
        // 否则会跳过篝火选择、把房间状态留成 null，后面直接进不去。
        if (runState.CurrentRoom is RestSiteRoom && NRestSiteRoom.Instance != null)
        {
            var restSync = RunManager.Instance?.RestSiteSynchronizer;
            int? localChoice = Safe(() => restSync?.GetChosenOptionIndex(local.NetId), (int?)null);
            int? aiChoice = Safe(() => restSync?.GetChosenOptionIndex(ai.NetId), (int?)null);
            if (restSync != null && (!localChoice.HasValue || !aiChoice.HasValue))
            {
                long blockMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (blockMs - _mapRestBlockMs > 3000)
                {
                    _mapRestBlockMs = blockMs;
                    LogError($"coop-map: 休息点未选完，暂停自动旅行 "
                             + $"local={localChoice?.ToString() ?? "null"} ai={aiChoice?.ToString() ?? "null"}");
                }
                return;
            }
        }

        if (runState.CurrentRoom is MerchantRoom && NMerchantRoom.Instance != null)
        {
            long shopBlockMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (shopBlockMs - _mapShopBlockMs > 3000)
            {
                _mapShopBlockMs = shopBlockMs;
                LogError("coop-map: 商店界面还开着，暂停自动旅行（走商店的离开按钮）");
            }
            return;
        }

        if (runState.CurrentRoom is TreasureRoom)
        {
            var treasureSync = RunManager.Instance?.TreasureRoomRelicSynchronizer;
            if (treasureSync != null && Safe(() => treasureSync.CurrentRelics?.Count ?? 0, 0) > 0)
            {
                bool tLocalVoted = Safe(() => treasureSync.GetPlayerVote(local) != null, false);
                bool tAiVoted = Safe(() => treasureSync.GetPlayerVote(ai) != null, false);
                if (!tLocalVoted || !tAiVoted)
                {
                    long blockMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (blockMs - _mapTreasureBlockMs > 3000)
                    {
                        _mapTreasureBlockMs = blockMs;
                        LogError($"coop-map: 宝箱遗物未选完，暂停自动旅行 "
                                 + $"local={tLocalVoted} ai={tAiVoted}");
                    }
                    return;
                }
            }
        }

        int gen = sync.MapGenerationCount;
        if (gen != _mapTriedGen)
        {
            _mapTriedGen = gen;
            _mapTries = 0;
            _mapBothLogged = false;
            _mapTravelStarted = false;
            _mapTravelCoord = null;
            CoopLogMapEntry(runState, sync, screen, local, ai, gen);
        }

        if (screen.IsTraveling) return;

        // 玩家还没投票 → 等着，绝不替他选路
        var localVote = CoopMapVoteOf(sync, local);
        if (localVote == null) return;

        var coord = localVote.Value.coord;

        // ★ 关键分支：这套档是用 SetUpNewSingleplayer 建的，NetService.Type=Singleplayer。
        // 而 MapSelectionSynchronizer 只在 host/client 下才会把票结算成旅行
        // （PlayerVotedForMapCoord 里 _votes 集齐后还要过一个 host 判定），
        // 单人 net service 下票投进去了但永远不会有人开旅行；
        // 更糟的是 AI 那一票会把 _votes 填满、触发半截转场 → 黑屏卡死。
        // 所以单人档下不让 AI 投票，直接自己进房间。
        string netType = Safe(() => RunManager.Instance?.NetService?.Type.ToString() ?? "?", "?");
        bool votesResolvable = netType is not ("Singleplayer" or "None" or "?");

        if (!votesResolvable)
        {
            long nowMs2 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // _mapTravelStarted 只用来防止「同一个目标点」重复触发。
            if (_mapTravelStarted && _mapTravelCoord.HasValue && _mapTravelCoord.Value.Equals(coord))
            {
                // 已经发起过，但 12 秒还没离开地图 → 进房间可能静默失败，有限次重试
                if (_mapTravelRetries < 2 && nowMs2 - _mapTravelAtMs > 12000)
                {
                    _mapTravelRetries++;
                    _mapTravelAtMs = nowMs2;
                    LogError($"coop-map: 上次进房间 12 秒未生效，重试（第 {_mapTravelRetries} 次）");
                    CoopTravelToCoord(runState, coord, netType);
                }
                return;
            }

            _mapTravelStarted = true;
            _mapTravelCoord = coord;
            _mapTravelRetries = 0;
            _mapTravelAtMs = nowMs2;
            _mapTries = 0;          // 新目标，重试计数归零
            CoopTravelToCoord(runState, coord, netType);
            return;
        }

        bool aiVoted = CoopMapVoteOf(sync, ai) != null;
        if (aiVoted)
        {
            if (!_mapBothLogged)
            {
                _mapBothLogged = true;
                LogError("coop-map: 两票齐了，等游戏自己开旅行");
            }
            return;
        }

        if (_mapTries >= 5) return;
        _mapTries++;

        // 和本地玩家点击时完全一样的构造（见 NMapScreen.OnMapPointSelectedLocally）：
        //   new MapLocation(runState.CurrentMapCoord, runState.CurrentActIndex)
        //   new MapVote { coord, mapGenerationCount }
        var location = new MapLocation(runState.CurrentMapCoord, runState.CurrentActIndex);
        var vote = new MapVote { coord = coord, mapGenerationCount = gen };

        try
        {
            sync.PlayerVotedForMapCoord(ai, location, vote);
        }
        catch (Exception ex)
        {
            LogError($"coop-map: 直接投票崩: {ex.Message}");
        }

        if (CoopMapVoteOf(sync, ai) != null)
        {
            LogError($"coop-map: AI 跟票成功 coord=({coord.col},{coord.row}) gen={gen}");
            return;
        }

        // 直接投票没进票箱（_acceptingVotesFromSource 不匹配会被静默丢弃）
        // → 退回本地玩家同款的动作队列，让 ExecuteAction 用同样的参数再投一次
        try
        {
            var actionQueue = RunManager.Instance?.ActionQueueSynchronizer;
            if (actionQueue == null)
            {
                LogError("coop-map: ActionQueueSynchronizer 为 null，无法入队");
                return;
            }
            actionQueue.RequestEnqueue(new VoteForMapCoordAction(ai, location, vote));
            LogError($"coop-map: 直接投票未生效，改用 VoteForMapCoordAction 入队 coord=({coord.col},{coord.row})");
        }
        catch (Exception ex)
        {
            LogError($"coop-map: 入队崩: {ex.Message}");
        }
    }

    /// <summary>
    /// 单人 net service 下的旅行。
    ///
    /// 必须用 EnterMapCoord，不能用 EnterMapPointInternal。IL 对比：
    ///   EnterMapCoord(coord):
    ///       State.AddVisitedMapCoord(coord);                     ← 这一步才更新位置 + 已访问
    ///       EnterMapCoordInternal(coord, null, true)
    ///           → EnterMapPointInternal(coord.row + 1, pointType, ...)
    ///   EnterMapPointInternal(floor, ...) 只建房间，不动坐标。
    ///
    /// 之前只调了后者：房间能进，但 CurrentMapCoord 一直停在原节点（visited 也不涨），
    /// 打完怪地图就还停在起点、无法继续。
    /// </summary>
    private static void CoopTravelToCoord(RunState runState, MapCoord coord, string netType)
    {
        try
        {
            var before = Safe(() => runState.CurrentMapCoord, (MapCoord?)null);
            string beforeText = before == null ? "null" : $"({before.Value.col},{before.Value.row})";

            // 先清掉 AI 残留的旧投票标记（地图上那个卡在打过节点的小图标）。
            // source 必须用「当前所在位置」，否则 PlayerVotedForMapCoord 会以
            // _acceptingVotesFromSource 不匹配为由静默丢弃。
            try
            {
                var sync = RunManager.Instance.MapSelectionSynchronizer;
                var ai = CoopAiPlayer(runState);
                if (sync != null && ai != null)
                {
                    var cur = new MapLocation(runState.CurrentMapCoord, runState.CurrentActIndex);
                    sync.PlayerVotedForMapCoord(ai, cur, null);   // null = 取消投票
                    LogError("coop-map: 已清除 AI 的旧投票标记");
                }
            }
            catch (Exception ex)
            {
                LogError("coop-map: 清除旧投票失败: " + ex.Message);
            }

            // 用「手动标位置 + EnterMapPointInternal(saveGame:false)」组合：
            // EnterMapCoord 内部是 AddVisitedMapCoord + EnterMapPointInternal(..., saveGame:true)，
            // 而工程里唯一验证过能稳定进房间的路径（OnCoopPressed step 8）用的是 saveGame:false。
            // 实测 EnterMapCoord 进 '?' 节点会停在 room=null（位置更新了、房间没建）。
            var point = runState.Map.GetPoint(coord);
            if (point == null)
            {
                LogError($"coop-map: 坐标 ({coord.col},{coord.row}) 找不到 MapPoint");
                _mapTravelStarted = false;
                return;
            }

            bool added = false;
            try
            {
                added = runState.AddVisitedMapCoord(coord);
            }
            catch (Exception ex)
            {
                LogError("coop-map: AddVisitedMapCoord 失败: " + ex.Message);
            }

            int floor = coord.row + 1;   // EnterMapPointInternal 的 floor 是 1-based
            var travelTask = RunManager.Instance.EnterMapPointInternal(floor, point.PointType, null, saveGame: false);
            // 这是 fire-and-forget 的进房间流程；异步失败必须记下来，否则只会表现为"没反应"。
            _ = travelTask.ContinueWith(
                t => LogError("coop-map: EnterMapPointInternal 异常: "
                              + (t.Exception?.InnerException?.Message ?? "?")),
                TaskContinuationOptions.OnlyOnFaulted);
            LogError($"coop-map: 单机档(net={netType}) 进房间 floor={floor} "
                     + $"coord=({coord.col},{coord.row}) point={point.PointType} "
                     + $"addVisited={added} 原位置={beforeText}");

            // 到站后再清一次票并刷新地图 UI。
            // _votes 被清空时如果没有触发 PlayerVotesCleared，NMapScreen.PlayerVoteDictionary 会留下
            // 上一个节点的图标——就是地图上那个甩不掉的"队友图标"。
            _ = CoopResetVotesAfterTravelAsync(coord);
        }
        catch (Exception ex)
        {
            _mapTravelStarted = false;
            LogError($"coop-map: EnterMapCoord 崩: {ex.Message}");
        }
    }

    /// <summary>
    /// 到达新节点后：把同步器的投票状态重置到新位置，并刷新地图上的投票图标。
    /// OnLocationChanged 会清空 _votes、把 _acceptingVotesFromSource 指向新位置，
    /// 并触发 PlayerVotesCleared —— NMapScreen 靠这个事件清掉旧图标。
    /// </summary>
    private static async Task CoopResetVotesAfterTravelAsync(MapCoord coord)
    {
        await Task.Delay(400);

        // Task.Delay 的续体不保证回到主线程；OnLocationChanged 会触发 UI 事件，
        // RefreshAllMapPointVotes 直接摸 Godot 节点，必须在主线程做。
        RunOnMain(() =>
        {
            try
            {
                int actIndex = Safe(
                    () => RunManager.Instance.DebugOnlyGetState()?.CurrentActIndex ?? 0, 0);
                var loc = new MapLocation(coord, actIndex);

                var sync = RunManager.Instance.MapSelectionSynchronizer;
                if (sync != null)
                {
                    sync.OnLocationChanged(loc);
                    LogError($"coop-map: 到站后重置投票状态 → ({coord.col},{coord.row}) act={actIndex}");
                }

                var screen = NMapScreen.Instance;
                if (screen != null && Godot.GodotObject.IsInstanceValid(screen))
                {
                    screen.RefreshAllMapPointVotes();
                    LogError("coop-map: 已刷新地图投票图标");
                }
            }
            catch (Exception ex)
            {
                LogError("coop-map: 重置投票状态失败: " + ex.Message);
            }
        });
    }

    /// <summary>GetVote 的安全包装：_votes 还没按玩家数填满时会越界。</summary>
    private static MapVote? CoopMapVoteOf(MapSelectionSynchronizer sync, Player p)
    {
        try { return sync.GetVote(p); }
        catch { return null; }
    }

    /// <summary>每张地图打一次：把所有可能挡住跟票的状态一次性写进日志。</summary>
    private static void CoopLogMapEntry(
        RunState runState, MapSelectionSynchronizer sync, NMapScreen screen,
        Player local, Player ai, int gen)
    {
        var lv = CoopMapVoteOf(sync, local);
        var av = CoopMapVoteOf(sync, ai);
        string net = Safe(() => RunManager.Instance.NetService?.Type.ToString() ?? "?", "?");
        string lvText = lv == null ? "null" : $"({lv.Value.coord.col},{lv.Value.coord.row})#{lv.Value.mapGenerationCount}";

        LogError($"coop-map: 进入地图 gen={gen} room={runState.CurrentRoom?.GetType().Name ?? "null"} "
                 + $"players={runState.Players.Count} net={net} "
                 + $"travel={screen.IsTravelEnabled}/{screen.IsTraveling} "
                 + $"localVote={lvText} aiVote={(av == null ? "null" : "有")}");
    }
}
