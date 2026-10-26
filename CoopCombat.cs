using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;

namespace Sts2Companion;

/// <summary>
/// 战斗里 AI 队友的决策与出牌。
///
/// 关键 API（sts2.dll 元数据确认）：
///   CombatManager.SetReadyToEndTurn(Player, bool, Func&lt;Task&gt;)   只把该玩家标记为已结束回合
///   CombatManager.IsPlayerReadyToEndTurn(Player) / AllPlayersReadyToEndTurn()
///   MonsterModel.GetIntents()（私有）→ List&lt;AbstractIntent&gt;
///   AttackIntent.GetTotalDamage(IEnumerable&lt;Creature&gt;, Creature) → 敌方这一手的真实伤害
///   CardModel.Type / GainsBlock / DynamicVars / TargetType；IsPlayable 与 CanonicalEnergyCost 是 protected，用反射
///
/// 每打一张牌都重新评估局面：斩杀 &gt; 会被打残时格挡 &gt; 能力 &gt; 伤害 &gt; 格挡，同分则 0 费优先。
/// </summary>
public static partial class CompanionMod
{
    private static bool _combatLogged;
    private static bool _aiDeadLogged;
    private static long _aiDeadUndoMs;
    private static long _combatIdleMs;
    private static CombatState? _lastCombatState;
    private static bool _aiPlaying;
    private static readonly HttpClient CoopApiHttp = new() { Timeout = TimeSpan.FromSeconds(12) };


    // IsPlayable 在 CardModel 上是 protected，只能反射读。
    // 费用/伤害/格挡都走公开 API（EnergyCost.GetResolved / DynamicVars），不再反射。
    private static readonly PropertyInfo? PiPlayable =
        typeof(CardModel).GetProperty("IsPlayable", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private static void CoopCombatTick(CombatState cs)
    {
        // 切到 AI 视角时暂停自动出牌，交给人工
        if (_viewSwitched) return;
        if (!Safe(() => CombatManager.Instance.IsInProgress, false))
        {
            CoopLogCombatNotStarted();
            _combatLogged = false;
            _aiDeadLogged = false;
            return;
        }

        // 注意：AI 选牌应答器绝不能整场战斗常驻。
        // CardSelectCmd.FromSimpleGridForRewards 会先看全局 Selector，只要它非 null，
        // 连【本地玩家】的卡牌奖励/弃牌/预见界面都会被直接跳过。应答器只在
        // PlayAiCardsAsync 自己出牌的那几秒挂上，出牌结束立即 Dispose。

        // 新一场战斗：CombatState 换了实例。必须重置回合计数，
        // 否则上一场打到第 5 回合，这一场 RoundNumber 从 1 开始，
        // `turn <= _aiLastTurn` 永远成立 —— AI 整场不出牌，玩家结束回合后双方等不齐，战斗卡死。
        if (!ReferenceEquals(_lastCombatState, cs))
        {
            _lastCombatState = cs;
            _aiLastTurn = -1;
            _combatLogged = false;
            _aiDeadLogged = false;
            LogError($"coop-combat: 新战斗（round={Safe(() => cs.RoundNumber, 0)}）→ 重置回合计数");
        }

        if (Safe(() => cs.CurrentSide, CombatSide.None) != CombatSide.Player) return;

        var ai = CoopAiPlayerInCombat(cs);
        if (ai == null) return;

        var pcs = Safe(() => ai.PlayerCombatState, (PlayerCombatState?)null);
        if (pcs == null) return;

        // Phase: None=0 Start=1 AutoPrePlay=2 Play=3 AutoPostPlay=4 End=5
        if (Safe(() => pcs.Phase, PlayerTurnPhase.None) != PlayerTurnPhase.Play)
        {
            CoopLogCombatIdle(cs, ai, "phase!=Play");
            return;
        }

        if (!_combatLogged)
        {
            _combatLogged = true;
            CoopLogCombatEntry(cs, ai);
        }

        // AI 阵亡后不再参与回合：只记一次现场，方便定位"我的回合被跳过"。
        if (!Safe(() => ai.Creature?.IsAlive ?? true, true))
        {
            if (!_aiDeadLogged)
            {
                _aiDeadLogged = true;
                var cm2 = CombatManager.Instance;
                LogError("coop-death: AI 已阵亡 "
                         + $"本地phase={Safe(() => CoopLocalPlayerInCombat(cs)?.PlayerCombatState?.Phase.ToString() ?? "null", "?")} "
                         + $"aiPhase={Safe(() => ai.PlayerCombatState?.Phase.ToString() ?? "null", "?")} "
                         + $"aiReady={Safe(() => cm2.IsPlayerReadyToEndTurn(ai), false)} "
                         + $"allReady={Safe(() => cm2.AllPlayersReadyToEndTurn(), false)}");
            }

            // AI 阵亡后，这套单人 net service 会把"全员就绪"判成 true，
            // 于是回合立刻结算、本地玩家的出牌阶段被跳过。
            // 在本地玩家仍处于 Play 时撤销它的 ready 标记，把回合还给它。
            var localP = CoopLocalPlayerInCombat(cs);
            if (localP != null
                && Safe(() => localP.PlayerCombatState?.Phase == PlayerTurnPhase.Play, false)
                && Safe(() => CombatManager.Instance.AllPlayersReadyToEndTurn(), false))
            {
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (nowMs - _aiDeadUndoMs >= 500)
                {
                    _aiDeadUndoMs = nowMs;
                    try
                    {
                        CombatManager.Instance.UndoReadyToEndTurn(localP);
                        LogError("coop-death: 已撤销本地玩家的 ready 标记，防止回合被跳过");
                    }
                    catch (Exception ex)
                    {
                        LogError("coop-death: UndoReadyToEndTurn 失败: " + ex.Message);
                    }
                }
            }
            return;
        }

        int turn = Safe(() => cs.RoundNumber, 0);
        if (turn <= _aiLastTurn) return;

        LogError($"coop: AI 回合 turn={turn} 手牌={Safe(() => pcs.Hand.Cards.Count, 0)} "
                 + $"能量={Safe(() => pcs.Energy, 0)} 敌方意图伤害={CoopIncomingDamage(cs)}");

        // 防重入：PlayAiCardsAsync 是 fire-and-forget 的，如果被并发触发会重复出牌
        if (_aiPlaying)
        {
            LogError("coop: AI 回合已经开始，跳过重复触发");
            return;
        }

        // 只有真正拿到执行权才记 turn。旧顺序在 _aiPlaying=true 时先把 turn 记掉，
        // 如果上一回合的异步出牌还没结束，这一回合就会被永久跳过。
        _aiLastTurn = turn;
        _aiPlaying = true;
        _ = PlayAiCardsAsync(ai, cs);
    }

    /// <summary>战斗房间已经存在但 IsInProgress 一直是 false —— 房间进场卡住了。</summary>
    private static void CoopLogCombatNotStarted()
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (nowMs - _combatIdleMs < 3000) return;
        _combatIdleMs = nowMs;

        var run = Safe(() => RunManager.Instance.DebugOnlyGetState(), (RunState?)null);
        if (run?.CurrentRoom is not CombatRoom) return;

        var cur = Safe(() => run.CurrentMapCoord, (MapCoord?)null);
        LogError($"coop-combat-idle: 房间已建立但战斗未开始 room={run.CurrentRoom.GetType().Name} "
                 + $"coord={(cur == null ? "null" : $"({cur.Value.col},{cur.Value.row})")} "
                 + $"inProgress={Safe(() => CombatManager.Instance.IsInProgress, false)}");
    }

    /// <summary>战斗在进行但 AI 不在 Play 阶段时的现场记录（每 3 秒一条）。</summary>
    private static void CoopLogCombatIdle(CombatState cs, Player ai, string why)
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (nowMs - _combatIdleMs < 3000) return;
        _combatIdleMs = nowMs;

        var local = CoopLocalPlayerInCombat(cs);
        LogError($"coop-combat-idle: {why} side={Safe(() => cs.CurrentSide.ToString(), "?")} "
                 + $"round={Safe(() => cs.RoundNumber, 0)} "
                 + $"aiPhase={Safe(() => ai.PlayerCombatState?.Phase.ToString() ?? "null", "?")} "
                 + $"localPhase={Safe(() => local?.PlayerCombatState?.Phase.ToString() ?? "null", "?")} "
                 + $"aiAlive={Safe(() => ai.Creature?.IsAlive ?? false, false)} "
                 + $"aiReady={Safe(() => CombatManager.Instance.IsPlayerReadyToEndTurn(ai), false)} "
                 + $"allReady={Safe(() => CombatManager.Instance.AllPlayersReadyToEndTurn(), false)}");
    }

    /// <summary>战斗开始时打一次全量状态，用来确认谁是"本地玩家"、谁有手牌。</summary>
    private static void CoopLogCombatEntry(CombatState cs, Player ai)
    {
        try
        {
            string me = "?";
            try
            {
                var r = RunManager.Instance.DebugOnlyGetState();
                if (r != null) me = LocalContext.GetMe(r)?.NetId.ToString() ?? "null";
            }
            catch { }

            var parts = new List<string>();
            foreach (var p in cs.Players)
            {
                if (p == null) continue;
                var pcs = Safe(() => p.PlayerCombatState, (PlayerCombatState?)null);
                bool isMe = false;
                try { isMe = LocalContext.IsMe(p); } catch { }
                parts.Add($"[net={p.NetId} me={isMe} phase={Safe(() => pcs?.Phase.ToString() ?? "null", "?")} "
                          + $"hand={Safe(() => pcs?.Hand.Cards.Count ?? 0, 0)} hp={Safe(() => p.Creature?.CurrentHp ?? 0, 0)}]");
            }

            LogError($"coop-combat: LocalContext.GetMe={me} 玩家={string.Join(" ", parts)} "
                     + $"敌人={cs.HittableEnemies.Count} 意图伤害={CoopIncomingDamage(cs)}");
        }
        catch (Exception ex)
        {
            LogError("CoopLogCombatEntry: " + ex.Message);
        }
    }

    /// <summary>
    /// 敌方这一回合会打出的总伤害（读怪物"当前这一步"的意图）。
    ///
    /// 注意不能用 MonsterModel.GetIntents()：它的 IL 是把**所有 MoveState** 的 Intents
    /// 全部 AddRange 后返回，等于把整只怪每个招式的伤害加在一起，数值会严重偏高。
    /// 当前招式在公开的 NextMove.Intents 上。
    /// </summary>
    private static int CoopIncomingDamage(CombatState cs)
    {
        int total = 0;
        try
        {
            foreach (var enemy in cs.HittableEnemies)
            {
                if (enemy == null || !Safe(() => enemy.IsAlive, false)) continue;

                var monster = Safe(() => enemy.Monster, (MonsterModel?)null);
                if (monster == null) continue;

                var intents = Safe<IReadOnlyList<AbstractIntent>>(
                    () => monster.NextMove.Intents, Array.Empty<AbstractIntent>());
                if (intents == null) continue;

                foreach (var intent in intents)
                {
                    // 只有攻击意图有伤害；GetTotalDamage 已算力量/虚弱，多段攻击已乘段数。
                    if (intent is AttackIntent attack)
                    {
                        total += Safe(() => attack.GetTotalDamage(cs.PlayerCreatures, enemy), 0);
                    }
                }
            }
        }
        catch { }
        return total;
    }

    private static int CoopCardDamage(CardModel c)
    {
        try { return c.DynamicVars?.Damage?.IntValue ?? 0; }
        catch { return 0; }
    }

    private static int CoopCardBlock(CardModel c)
    {
        try { return c.DynamicVars?.Block?.IntValue ?? 0; }
        catch { return 0; }
    }

    private static bool CoopCardPlayable(CardModel c)
    {
        try { return PiPlayable?.GetValue(c) is true; }
        catch { return true; }   // 读不到就乐观放行，TryManualPlay 会兜底
    }

    /// <summary>
    /// 当前实际能量消耗。必须走公开的 CardEnergyCost.GetResolved()：
    /// 它内部是 max(0, GetWithModifiers(CostModifiers.All))，会算上本回合/本场费用变化。
    /// 旧实现反射找 Amount/Cost，但 CardEnergyCost 根本没有这两个属性，
    /// 于是永远退到 CanonicalEnergyCost（基础费用），AI 会误判"打不起"或"打得起"。
    /// </summary>
    private static int CoopCardCost(CardModel c)
    {
        try
        {
            var ec = c.EnergyCost;
            if (ec == null) return 0;
            if (ec.CostsX) return 0;   // X 费牌不占固定费用，开打前按 0 算
            return Math.Max(0, ec.GetResolved());
        }
        catch { return 0; }
    }

    private static async Task PlayAiCardsAsync(Player ai, CombatState cs)
    {
        var tried = new HashSet<CardModel>(ReferenceEqualityComparer.Instance);
        int played = 0;

        // 只在 AI 自己出牌的这段窗口挂应答器。战斗结束/本地玩家操作时必须已经 Dispose，
        // 否则 CardSelectCmd.Selector 非 null 会让本地卡牌奖励、弃牌、预见界面全部被跳过。
        IDisposable? selectorScope = null;
        try
        {
            selectorScope = CardSelectCmd.PushSelector(new AiCardSelector(), false);
            LogError("coop-select: 已挂上 AI 选牌应答器（仅本次出牌）");
        }
        catch (Exception ex)
        {
            LogError("coop-select: PushSelector 失败: " + ex.Message);
        }

        try
        {
            for (int step = 0; step < 15; step++)
            {
                if (!Safe(() => CombatManager.Instance.IsInProgress, false)) break;

                var pcs = Safe(() => ai.PlayerCombatState, (PlayerCombatState?)null);
                if (pcs == null) break;
                if (Safe(() => pcs.Phase, PlayerTurnPhase.None) != PlayerTurnPhase.Play) break;

                var hand = Safe(() => pcs.Hand.Cards, (IReadOnlyList<CardModel>?)null);
                if (hand == null) break;

                var candidates = hand
                    .Where(c => c != null
                                && !tried.Contains(c)
                                && CoopCardPlayable(c)
                                && Safe(() => c.Pile?.Type == PileType.Hand, false))
                    .ToList();
                if (candidates.Count == 0) break;

                var best = await PickBestCardAsync(cs, ai, pcs, candidates);
                if (best == null) break;

                tried.Add(best);   // 同一张牌这一回合只尝试一次，避免反复打同一张

                var target = ResolveAiTarget(cs, best);
                bool ok;
                try { ok = best.TryManualPlay(target); }
                catch (Exception ex)
                {
                    LogError($"coop: 出牌崩 {Safe(() => best.Id.Entry, "?")}: {ex.Message}");
                    continue;
                }
                if (!ok) continue;

                bool leftHand = await CoopWaitCardPlayedAsync(ai, best, 2500);
                played++;
                LogError($"coop: AI 出牌 {Safe(() => best.Id.Entry, "?")} → "
                         + $"{(target == null ? "无目标" : Safe(() => target.Name, "?"))}"
                         + (leftHand ? "" : "（未离手，停止出牌）"));

                if (leftHand)
                {
                    // 牌离手后动作可能还没结束（弃 2 张/选牌这类会暂停等选择）。
                    // 等这个 AI 动作真正完成再卸 selector，否则多选请求会落到没人应答的
                    // PlayerChoiceSynchronizer 上，表现为选一张可以、选多张卡住。
                    GameAction? playedAction = Safe(
                        () => RunManager.Instance?.ActionExecutor?.CurrentlyRunningAction,
                        (GameAction?)null);
                    if (playedAction != null && playedAction.OwnerId == ai.NetId)
                    {
                        await CoopWaitActionCompletionAsync(playedAction, 2500);
                    }
                }

                if (!leftHand)
                {
                    // 牌没离手通常意味着动作队列被一个没人应答的选择堵住了。
                    // 取消卡住的动作来解锁队列，否则这一整局后面都出不了牌。
                    CoopCancelStuckAction();
                    break;
                }
            }

            // 最后一张牌可能还会弹一次选牌/弃牌；给请求一点落地时间再卸应答器。
            await Task.Delay(250);
        }
        catch (Exception ex)
        {
            LogError("PlayAiCardsAsync: " + ex);
        }
        finally
        {
            try { selectorScope?.Dispose(); } catch { }
            selectorScope = null;
            _aiPlaying = false;
        }

        LogError($"coop: AI 出牌结束（{played} 张）");
        await Task.Delay(120);
        CoopEndAiTurn(ai, cs);
    }

    private static async Task CoopWaitActionCompletionAsync(GameAction action, int timeoutMs)
    {
        try
        {
            Task? completion = Safe(() => action.CompletionTask, (Task?)null);
            if (completion == null || completion.IsCompleted) return;
            Task done = await Task.WhenAny(completion, Task.Delay(timeoutMs));
            if (done == completion)
            {
                try { await completion; } catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// 等一张牌真正离手。动作队列是异步的，固定 Delay(200) 往往不够——
    /// 之前就是第二张还没离手就被判失败，于是每回合只出一张。
    /// </summary>
    private static async Task<bool> CoopWaitCardPlayedAsync(Player ai, CardModel card, int timeoutMs)
    {
        int waited = 0;
        while (waited < timeoutMs)
        {
            await Task.Delay(100);
            waited += 100;

            if (!Safe(() => CombatManager.Instance.IsInProgress, false)) return false;
            if (!Safe(() => card.Pile?.Type == PileType.Hand, false)) return true;

            var pcs = Safe(() => ai.PlayerCombatState, (PlayerCombatState?)null);
            if (pcs == null) return false;
            if (Safe(() => pcs.Phase, PlayerTurnPhase.None) != PlayerTurnPhase.Play) return false;
        }

        return !Safe(() => card.Pile?.Type == PileType.Hand, false);
    }

    /// <summary>
    /// 强行取消正在执行的动作，用来解开被未应答选择堵死的动作队列。
    /// 参考 ActionExecutor：CurrentlyRunningAction.Cancel() + Cancel()。
    /// </summary>
    private static void CoopCancelStuckAction()
    {
        try
        {
            var executor = RunManager.Instance?.ActionExecutor;
            if (executor == null) return;

            var running = Safe(() => executor.CurrentlyRunningAction, (GameAction?)null);
            if (running == null)
            {
                LogError("coop: 队列卡住但当前没有运行中的动作");
                return;
            }

            try { running.Cancel(); } catch (Exception ex) { LogError("coop: action.Cancel 崩: " + ex.Message); }
            try { executor.Cancel(); } catch (Exception ex) { LogError("coop: executor.Cancel 崩: " + ex.Message); }
            LogError($"coop: 已取消卡住的动作 {Safe(() => running.ToString(), "?")}，解锁动作队列");
        }
        catch (Exception ex)
        {
            LogError("CoopCancelStuckAction: " + ex.Message);
        }
    }

    private static bool _apiKeyWarned;

    /// <summary>
    /// AI 出牌入口：API 模式先问 API，失败/没配置就回退本地规则。
    /// API 只决定"打哪张牌"，目标仍由 ResolveAiTarget 本地选择。
    /// </summary>
    private static async Task<CardModel?> PickBestCardAsync(
        CombatState cs, Player ai, PlayerCombatState pcs, List<CardModel> candidates)
    {
        if (!CoopUseApiBrain())
        {
            return PickBestCard(cs, ai, pcs, candidates);
        }

        var apiPick = await PickCardViaApiAsync(cs, ai, pcs, candidates);
        if (apiPick != null) return apiPick;

        return PickBestCard(cs, ai, pcs, candidates);
    }

    private static async Task<CardModel?> PickCardViaApiAsync(
        CombatState cs, Player ai, PlayerCombatState pcs, List<CardModel> candidates)
    {
        try
        {
            string key = CoopApiKey().Trim();
            if (key.Length == 0)
            {
                if (!_apiKeyWarned)
                {
                    _apiKeyWarned = true;
                    LogError("coop-api: 未配置 API Key，回退本地规则");
                }
                return null;
            }

            string baseUrl = CoopApiBaseUrl().Trim();
            if (baseUrl.Length == 0) baseUrl = "https://api.openai.com/v1";
            string endpoint = baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
                ? baseUrl
                : baseUrl.TrimEnd('/') + "/chat/completions";

            string prompt = BuildApiCardPrompt(cs, ai, pcs, candidates);
            var payload = new
            {
                model = CoopApiModel(),
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content = "You control one character in Slay the Spire 2. "
                                  + "Pick exactly one card from the provided candidates. "
                                  + "Return ONLY JSON: {\"card\":\"<id>\",\"reason\":\"<short>\"}."
                    },
                    new { role = "user", content = prompt }
                },
                temperature = 0.2,
                max_tokens = 120
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            req.Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            long started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var resp = await CoopApiHttp.SendAsync(req);
            string body = await resp.Content.ReadAsStringAsync();
            long elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - started;

            if (!resp.IsSuccessStatusCode)
            {
                string snippet = body.Length > 240 ? body.Substring(0, 240) : body;
                LogError($"coop-api: HTTP {(int)resp.StatusCode} {elapsed}ms: {snippet}");
                return null;
            }

            string? text = ExtractApiText(body);
            if (string.IsNullOrWhiteSpace(text))
            {
                LogError("coop-api: 响应没有 content，回退本地");
                return null;
            }

            int l = text.IndexOf('{');
            int r = text.LastIndexOf('}');
            if (l < 0 || r <= l)
            {
                LogError("coop-api: 响应不是 JSON: " + (text.Length > 160 ? text.Substring(0, 160) : text));
                return null;
            }

            using var doc = JsonDocument.Parse(text.Substring(l, r - l + 1));
            var root = doc.RootElement;

            string? want = root.TryGetProperty("card", out var cardEl) ? cardEl.GetString() : null;
            int? wantIndex = null;
            if (root.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number
                && idxEl.TryGetInt32(out int idx))
            {
                wantIndex = idx;
            }

            CardModel? picked = null;
            if (wantIndex.HasValue && wantIndex.Value >= 0 && wantIndex.Value < candidates.Count)
            {
                picked = candidates[wantIndex.Value];
            }
            else if (!string.IsNullOrWhiteSpace(want))
            {
                picked = candidates.FirstOrDefault(c =>
                    string.Equals(Safe(() => c.Id.Entry, ""), want, StringComparison.OrdinalIgnoreCase));
                if (picked == null)
                {
                    picked = candidates.FirstOrDefault(c =>
                        string.Equals(Safe(() => c.Title, ""), want,
                                      StringComparison.OrdinalIgnoreCase));
                }
            }

            if (picked == null)
            {
                LogError($"coop-api: 无效卡牌 {want ?? "?"}，回退本地");
                return null;
            }

            string reason = root.TryGetProperty("reason", out var reasonEl)
                ? (reasonEl.GetString() ?? "")
                : "";
            if (reason.Length > 80) reason = reason.Substring(0, 80);
            LogError($"coop-api: 选择 {Safe(() => picked.Id.Entry, "?")} "
                     + $"({elapsed}ms) {reason}");
            return picked;
        }
        catch (TaskCanceledException)
        {
            LogError("coop-api: 请求超时，回退本地");
            return null;
        }
        catch (Exception ex)
        {
            LogError("coop-api: 请求失败，回退本地: " + ex.Message);
            return null;
        }
    }

    private static string? ExtractApiText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            {
                return null;
            }

            var first = choices[0];
            if (first.TryGetProperty("message", out var msg)
                && msg.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.String) return content.GetString();
                if (content.ValueKind == JsonValueKind.Array)
                {
                    var parts = new StringBuilder();
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var t)) parts.Append(t.GetString());
                    }
                    return parts.ToString();
                }
            }

            if (first.TryGetProperty("text", out var textEl)) return textEl.GetString();
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildApiCardPrompt(
        CombatState cs, Player ai, PlayerCombatState pcs, List<CardModel> candidates)
    {
        var sb = new StringBuilder();
        sb.AppendLine("State:");
        sb.AppendLine($"- Your HP: {Safe(() => ai.Creature?.CurrentHp ?? 0, 0)}/"
                      + $"{Safe(() => ai.Creature?.MaxHp ?? 0, 0)}, "
                      + $"Block: {Safe(() => ai.Creature?.Block ?? 0, 0)}, "
                      + $"Energy: {Safe(() => pcs.Energy, 0)}");
        sb.AppendLine($"- Total incoming attack damage: {CoopIncomingDamage(cs)}");

        sb.AppendLine("Enemies:");
        int enemyIndex = 0;
        foreach (var enemy in cs.HittableEnemies)
        {
            if (enemy == null || !Safe(() => enemy.IsAlive, false)) continue;
            sb.AppendLine($"- [{enemyIndex}] {Safe(() => enemy.Name, "?")}: "
                          + $"HP {Safe(() => enemy.CurrentHp, 0)}/{Safe(() => enemy.MaxHp, 0)}, "
                          + $"Block {Safe(() => enemy.Block, 0)}, "
                          + $"intent damage {CoopEnemyIntentDamage(cs, enemy)}");
            enemyIndex++;
        }

        sb.AppendLine("Playable cards:");
        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            string desc = Safe(() => c.Description.GetFormattedText(), "");
            if (desc.Length > 110) desc = desc.Substring(0, 110);
            sb.AppendLine($"- [{i}] id={Safe(() => c.Id.Entry, "?")}, "
                          + $"name={Safe(() => c.Title, "?")}, "
                          + $"cost={CoopCardCost(c)}, type={Safe(() => c.Type.ToString(), "?")}, "
                          + $"damage={CoopCardDamage(c)}, block={CoopCardBlock(c)}, "
                          + $"desc={desc}");
        }

        sb.AppendLine("Pick one index/id. Do not explain outside JSON.");
        return sb.ToString();
    }

    private static int CoopEnemyIntentDamage(CombatState cs, Creature enemy)
    {
        int total = 0;
        try
        {
            var monster = Safe(() => enemy.Monster, (MonsterModel?)null);
            if (monster == null) return 0;
            var intents = Safe<IReadOnlyList<AbstractIntent>>(
                () => monster.NextMove.Intents, Array.Empty<AbstractIntent>());
            foreach (var intent in intents)
            {
                if (intent is AttackIntent attack)
                {
                    total += Safe(() => attack.GetTotalDamage(cs.PlayerCreatures, enemy), 0);
                }
            }
        }
        catch { }
        return total;
    }

    /// <summary>给当前局面挑一张最该打的牌。每打一张都重新调用，所以会随局面变化。</summary>
    private static CardModel? PickBestCard(
        CombatState cs, Player ai, PlayerCombatState pcs, List<CardModel> candidates)
    {
        int energy = Safe(() => pcs.Energy, 0);
        int myHp = Safe(() => ai.Creature?.CurrentHp ?? 1, 1);
        int myBlock = Safe(() => ai.Creature?.Block ?? 0, 0);
        int incoming = CoopIncomingDamage(cs);

        int enemyHp = 0;
        foreach (var e in cs.HittableEnemies) enemyHp += Safe(() => e.CurrentHp, 0);

        // 斩杀判断：把付得起的攻击按伤害从大到小凑一遍，看能否覆盖敌方总血
        int budget = energy, burst = 0;
        foreach (var x in candidates
                     .Where(IsAttackCard)
                     .Select(c => (dmg: CoopCardDamage(c), cost: CoopCardCost(c)))
                     .Where(x => x.cost <= energy)
                     .OrderByDescending(x => x.dmg))
        {
            if (x.cost > budget) continue;
            budget -= x.cost;
            burst += x.dmg;
        }
        bool lethal = enemyHp > 0 && burst >= enemyHp;

        int needBlock = Math.Max(0, incoming - myBlock);
        bool inDanger = needBlock >= Math.Max(1, myHp / 2);   // 这一手会打掉我一半以上的血

        CardModel? best = null;
        int bestScore = int.MinValue;

        foreach (var c in candidates)
        {
            int cost = CoopCardCost(c);
            if (cost > energy) continue;

            CardType type = Safe(() => c.Type, CardType.None);
            if (type == CardType.Status || type == CardType.Curse) continue;

            bool isAttack = type == CardType.Attack;
            bool gainsBlock = Safe(() => c.GainsBlock, false);

            int score;
            if (lethal)
            {
                // 能斩杀就全打输出，别浪费在格挡上
                score = isAttack ? 1000 + CoopCardDamage(c) : -100;
            }
            else if (inDanger && gainsBlock)
            {
                score = 800 + Math.Min(needBlock, CoopCardBlock(c)) * 5;
            }
            else
            {
                if (type == CardType.Power) score = 500;                 // 能力是长期收益，先铺
                else if (isAttack) score = 200 + CoopCardDamage(c) * 10 / Math.Max(1, cost);
                else if (gainsBlock) score = 120 + CoopCardBlock(c);
                else score = 80;                                         // 抽牌/加费等其它

                if (incoming > myBlock && gainsBlock) score += 60;       // 有伤害打进来时格挡更值
            }

            if (cost == 0) score += 40;                                  // 白嫖的先打

            if (score > bestScore)
            {
                bestScore = score;
                best = c;
            }
        }

        return best;
    }

    private static bool IsAttackCard(CardModel c)
        => Safe(() => c.Type == CardType.Attack, false);

    private static Creature? ResolveAiTarget(CombatState cs, CardModel card)
    {
        try
        {
            TargetType tt = Safe(() => card.TargetType, TargetType.None);

            if (tt == TargetType.AnyEnemy)
            {
                // 优先补刀：血量最低的可命中敌人
                Creature? weakest = null;
                int minHp = int.MaxValue;
                foreach (var e in cs.HittableEnemies)
                {
                    int hp = Safe(() => e.CurrentHp, int.MaxValue);
                    if (hp < minHp) { minHp = hp; weakest = e; }
                }
                return weakest;
            }

            if (tt == TargetType.AnyAlly)
            {
                foreach (var p in cs.Players)
                {
                    if (p != null && p.NetId != 2uL && p.Creature != null) return p.Creature;
                }
                return card.Owner?.Creature;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 结束 AI 的回合。
    ///
    /// 单人 net service 档下**绝对不能替 AI 标记就绪**：实测
    /// <c>IsPlayerReadyToEndTurn(本地)=False</c> 的同时 <c>AllPlayersReadyToEndTurn()=True</c>，
    /// 也就是说这个"全员就绪"的判定不包含本地玩家——一旦 AI 就绪，回合立刻结算，
    /// 本地玩家直接失去这一回合（被架空）。
    /// 这套档的回合是共享的，结束回合只能由本地玩家自己做。
    ///
    /// 只有真多人（host/client）时，AI 这一侧才是独立玩家，才需要替它标记就绪。
    /// </summary>
    private static void CoopEndAiTurn(Player ai, CombatState cs)
    {
        try
        {
            string netType = Safe(() => RunManager.Instance.NetService?.Type.ToString() ?? "?", "?");
            bool realMultiplayer = netType is not ("Singleplayer" or "None" or "?");

            if (!realMultiplayer)
            {
                LogError($"coop: 单人档(net={netType}) 不替 AI 结束回合 —— 等本地玩家自己按结束回合");
                return;
            }

            var cm = CombatManager.Instance;
            if (cm.IsPlayerReadyToEndTurn(ai))
            {
                LogError("coop: AI 已经是 ready 状态，跳过 EndTurn");
                return;
            }

            bool isMe = false;
            try { isMe = LocalContext.IsMe(ai); } catch { }

            cm.SetReadyToEndTurn(ai, false, null);

            var local = CoopLocalPlayerInCombat(cs);
            bool localReady = local != null && Safe(() => cm.IsPlayerReadyToEndTurn(local), false);
            LogError($"coop: AI EndTurn → aiReady={Safe(() => cm.IsPlayerReadyToEndTurn(ai), false)} "
                     + $"localReady={localReady} allReady={Safe(() => cm.AllPlayersReadyToEndTurn(), false)} "
                     + $"IsMe(AI)={isMe}");
        }
        catch (Exception ex)
        {
            LogError("CoopEndAiTurn: " + ex.Message);
        }
    }

    private static Player? CoopLocalPlayerInCombat(CombatState cs)
    {
        try
        {
            foreach (var p in cs.Players)
            {
                if (p != null && p.NetId != 2uL) return p;
            }
        }
        catch { }
        return null;
    }

    private static bool _rewardLogged;
    private static bool _sawCombatThisRoom;
    private static long _lastRewardAttemptMs;
    private static bool _aiRewardClaimBusy;
    private static long _aiRewardNoSetMs;

    // SelectRewardForPlayer(Player, int) 是 private；它按"该玩家当前栈顶奖励集"的
    // Rewards[index] 走 Reward.SelectUnsynchronized()，金币/遗物/药水直接生效，
    // 卡牌奖励会走 CardSelectCmd，由我们临时挂的 AiCardSelector 应答。
    private static readonly MethodInfo? MiSelectRewardForPlayer =
        typeof(RewardsSetSynchronizer).GetMethod(
            "SelectRewardForPlayer",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(Player), typeof(int) },
            modifiers: null);

    // GetRewardStateForPlayer -> PlayerRewardState.rewardsStack -> 栈顶 RewardsSetState.set
    // 用来知道 AI 当前奖励集里"还剩哪一个下标没选"，避免重复选 index 0。
    private static readonly MethodInfo? MiGetRewardStateForPlayer =
        typeof(RewardsSetSynchronizer).GetMethod(
            "GetRewardStateForPlayer",
            BindingFlags.Instance | BindingFlags.NonPublic);

    // 领取连续失败时的兜底：至少让奖励流程能往前走，避免整个房间卡死。
    private static readonly MethodInfo? MiSkipRewardSetForPlayer =
        typeof(RewardsSetSynchronizer).GetMethod(
            "SkipRewardsSetOnStackTopForPlayer",
            BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly MethodInfo? MiCompleteRewardsSet =
        typeof(RewardsSetSynchronizer).GetMethod(
            "CompleteRewardsSetIfNecessary",
            BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly MethodInfo? MiSetRewardSelected =
        typeof(Reward).GetProperty("SuccessfullySelected", BindingFlags.Instance | BindingFlags.Public)
            ?.GetSetMethod(nonPublic: true);

    private static readonly FieldInfo? FiRewardSelected =
        typeof(Reward).GetField("<SuccessfullySelected>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);

    private static void CoopFallbackSkipAiRewardSet(RewardsSetSynchronizer rss, Player ai)
    {
        try
        {
            MiSkipRewardSetForPlayer?.Invoke(rss, new object?[] { ai });
            LogError("coop-reward: 已回退为跳过 AI 奖励集（领取连续失败）");
        }
        catch (Exception ex)
        {
            LogError("coop-reward: 回退跳过也失败: " + (ex.InnerException ?? ex).Message);
        }
    }

    private static object? CoopAiRewardSetState(RewardsSetSynchronizer rss, Player ai)
    {
        try
        {
            object? state = MiGetRewardStateForPlayer?.Invoke(rss, new object?[] { ai });
            if (state == null) return null;

            var stack = state.GetType().GetField("rewardsStack",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(state) as System.Collections.IList;
            if (stack == null || stack.Count == 0) return null;

            return stack[stack.Count - 1];
        }
        catch { return null; }
    }

    private static RewardsSet? CoopAiRewardSet(RewardsSetSynchronizer rss, Player ai)
    {
        try
        {
            object? top = CoopAiRewardSetState(rss, ai);
            return top?.GetType().GetField("set",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(top) as RewardsSet;
        }
        catch { return null; }
    }

    /// <summary>
    /// CardReward.OnSelect 对非本地玩家会直接 WaitForRemoteChoice，
    /// 而单人档里这会抛 "Cannot wait for remote choice in singleplayer!"。
    /// 所以卡牌奖励不能走 SelectRewardForPlayer：自己挑一张、进牌组，再标记奖励已选。
    /// </summary>
    private static async Task<bool> CoopTakeCardRewardAsync(CardReward reward, Player ai)
    {
        try
        {
            if (!reward.IsPopulated) reward.Populate();
            CardModel? card = reward.Cards?.FirstOrDefault(c => c != null);
            if (card == null)
            {
                LogError("coop-reward: 卡牌奖励没有可选的卡");
                return false;
            }

            if (card.Owner == null) card.Owner = ai;
            CardPileAddResult result = await CardPileCmd.Add(
                card, PileType.Deck, CardPilePosition.Bottom, null, false);
            if (!result.success)
            {
                LogError("coop-reward: 卡牌进牌组失败");
                return false;
            }

            LogError($"coop-reward: AI 选卡 {Safe(() => card.Id.Entry, "?")} "
                     + $"owner={Safe(() => card.Owner?.NetId.ToString() ?? "null", "?")}（直接进牌组）");
            return true;
        }
        catch (Exception ex)
        {
            LogError("coop-reward: 选卡异常: " + ex.Message);
            return false;
        }
    }

    private static bool CoopMarkRewardSelected(Reward reward)
    {
        try
        {
            if (MiSetRewardSelected != null)
            {
                MiSetRewardSelected.Invoke(reward, new object?[] { true });
            }
            else if (FiRewardSelected != null)
            {
                FiRewardSelected.SetValue(reward, true);
            }
            else
            {
                return false;
            }

            try { reward.MarkContentAsSeen(); } catch { }
            try { reward.ParentRewardSet?.RemoveReward(reward); } catch { }
            return true;
        }
        catch (Exception ex)
        {
            LogError("coop-reward: 标记奖励已选失败: " + (ex.InnerException ?? ex).Message);
            return false;
        }
    }

    /// <summary>本地玩家正开着卡牌选择界面时不要抢答，等他选完。</summary>
    private static bool CoopLocalCardScreenOpen()
    {
        try
        {
            var top = NOverlayStack.Instance?.Peek();
            string name = top?.GetType().Name ?? "";
            return name.Contains("Card", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// 替 AI 逐个领取它那份奖励（不是跳过）。
    /// 每次找栈顶奖励集里第一个 SuccessfullySelected=false 的下标去选；
    /// 直到栈空（抛 not currently viewing）或 20 次上限。
    /// </summary>
    private static async Task CoopClaimAiRewardsAsync(Player ai)
    {
        if (_aiRewardClaimBusy) return;
        // 上一次 AI 出牌的应答器可能还没卸干净；等它卸掉再领奖，避免叠两层。
        if (Safe(() => CardSelectCmd.Selector, (ICardSelector?)null) != null) return;

        _aiRewardClaimBusy = true;
        int claimed = 0;
        int lastIndex = -1;
        int sameIndexTries = 0;

        try
        {
            var rss = RunManager.Instance?.RewardsSetSynchronizer;
            if (rss == null || MiSelectRewardForPlayer == null)
            {
                LogError("coop-reward: 找不到 SelectRewardForPlayer，无法替 AI 领取");
                return;
            }

            for (int step = 0; step < 20; step++)
            {
                if (CoopLocalCardScreenOpen()) return;

                RewardsSet? set = CoopAiRewardSet(rss, ai);
                if (set == null)
                {
                    _aiRewardNoSetMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    break;
                }

                int index = -1;
                for (int i = 0; i < set.Rewards.Count; i++)
                {
                    Reward? reward = set.Rewards[i];
                    if (reward != null && !reward.SuccessfullySelected)
                    {
                        index = i;
                        break;
                    }
                }

                if (index < 0)
                {
                    // 全部领过，等 CompleteRewardsSetIfNecessary 把 set 弹掉。
                    await Task.Delay(150);
                    continue;
                }

                if (index == lastIndex) sameIndexTries++;
                else { lastIndex = index; sameIndexTries = 1; }

                if (sameIndexTries > 3)
                {
                    LogError($"coop-reward: set={set.Id} index={index} 连续 3 次没领成功，回退跳过");
                    CoopFallbackSkipAiRewardSet(rss, ai);
                    break;
                }

                Reward? currentReward = set.Rewards[index];
                if (currentReward is CardReward cardReward)
                {
                    bool taken = await CoopTakeCardRewardAsync(cardReward, ai);
                    if (taken && CoopMarkRewardSelected(currentReward))
                    {
                        object? setState = CoopAiRewardSetState(rss, ai);
                        if (setState != null)
                        {
                            try { MiCompleteRewardsSet?.Invoke(rss, new object?[] { setState }); }
                            catch (Exception ex) { LogError("coop-reward: 完成奖励集失败: " + (ex.InnerException ?? ex).Message); }
                        }

                        claimed++;
                        LogError($"coop-reward: AI 领取第 {claimed} 个奖励 set={set.Id} index={index} type=CardReward");
                        lastIndex = -1;
                        sameIndexTries = 0;
                        await Task.Delay(150);
                        continue;
                    }

                    LogError("coop-reward: 卡牌奖励领取失败，回退跳过该奖励集");
                    CoopFallbackSkipAiRewardSet(rss, ai);
                    break;
                }

                IDisposable? selectorScope = null;
                try
                {
                    selectorScope = CardSelectCmd.PushSelector(new AiCardSelector(), false);
                    object? result = MiSelectRewardForPlayer.Invoke(rss, new object?[] { ai, index });
                    if (result is Task task)
                    {
                        Task finished = await Task.WhenAny(task, Task.Delay(5000));
                        if (finished != task)
                        {
                            LogError("coop-reward: AI 领取奖励超时（可能弹了界面），先放弃本次");
                            break;
                        }
                        await task;
                    }

                    claimed++;
                    LogError($"coop-reward: AI 领取第 {claimed} 个奖励 "
                             + $"set={set.Id} index={index} type={Safe(() => set.Rewards[index].GetType().Name, "?")}");
                }
                catch (TargetInvocationException ex)
                {
                    string message = (ex.InnerException ?? ex).Message;
                    if (message.Contains("not currently viewing", StringComparison.OrdinalIgnoreCase))
                    {
                        _aiRewardNoSetMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        break;
                    }
                    LogError("coop-reward: AI 领奖失败: " + message);
                    break;
                }
                catch (Exception ex)
                {
                    LogError("coop-reward: AI 领奖异常: " + ex.Message);
                    break;
                }
                finally
                {
                    try { selectorScope?.Dispose(); } catch { }
                }

                await Task.Delay(150);
            }
        }
        catch (Exception ex)
        {
            LogError("CoopClaimAiRewardsAsync: " + ex.Message);
        }
        finally
        {
            _aiRewardClaimBusy = false;
        }
    }

    /// <summary>
    /// 胜利后的奖励/推进状态。AI 那侧的奖励集没人结束，下一次进战斗就会卡住。
    /// </summary>
    private static void CoopRewardTick(RunState runState)
    {
        try
        {
            if (runState.CurrentRoom is not CombatRoom)
            {
                _rewardLogged = false;
                _sawCombatThisRoom = false;
                _aiRewardNoSetMs = 0;
                return;
            }
            if (Safe(() => CombatManager.Instance.IsInProgress, false))
            {
                _sawCombatThisRoom = true;   // 真打过一场，之后的"结束"才可信
                return;
            }
            // 刚进战斗房间、战斗还没开始时 IsInProgress 也是 false，
            // 不加这个判断就会在开局瞬间误报一次"本地完成=False"。
            if (!_sawCombatThisRoom) return;

            // 这个 tick 被 PollCoopAI 以 25Hz 调用。先把 500ms 节流放在最前面，
            // 否则即使不尝试，每 40ms 也会 GetNextRewardIds().ToList() 分配一次。
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (nowMs - _lastRewardAttemptMs < 500) return;
            _lastRewardAttemptMs = nowMs;

            var rss = RunManager.Instance.RewardsSetSynchronizer;
            if (rss == null)
            {
                if (!_rewardLogged) { _rewardLogged = true; LogError("coop-reward: RewardsSetSynchronizer 是 null"); }
                return;
            }

            var local = CoopLocalPlayer(runState);
            var ai = CoopAiPlayer(runState);
            // GetNextRewardIds 可能对本地/AI 各报一次同一个 set id（日志里出现过 ids=[0,0]）。
            // 去重后再枚举，避免同一 set 被重复处理、日志也重复。
            var ids = (Safe(() => rss.GetNextRewardIds()?.ToList(), (List<int>?)null) ?? new List<int>())
                      .Distinct()
                      .ToList();

            if (!_rewardLogged)
            {
                _rewardLogged = true;
                LogError($"coop-reward: 战斗结束 待领奖励集={ids.Count} ids=[{string.Join(",", ids)}]");
                foreach (var id in ids)
                {
                    bool localDone = local != null && Safe(() => rss.IsRewardsSetCompleted(local, id), false);
                    bool aiDone = ai != null && Safe(() => rss.IsRewardsSetCompleted(ai, id), false);
                    LogError($"coop-reward:   set={id} 本地完成={localDone} AI完成={aiDone}");
                }
            }

            if (ai == null || ids.Count == 0) return;

            // AI 没有奖励集时 SelectRewardForPlayer 会抛 not currently viewing；
            // 退避 2 秒，避免每 500ms 白试一次。
            long nowClaimMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (nowClaimMs - _aiRewardNoSetMs < 500) return;

            _ = CoopClaimAiRewardsAsync(ai);
        }
        catch (Exception ex)
        {
            LogError("CoopRewardTick: " + ex.Message);
        }
    }

    /// <summary>
    /// AI 的选牌应答器。
    ///
    /// 卡牌效果里所有"选一张 / 弃 N 张 / 从牌堆挑"的请求最终都走 CardSelectCmd。
    /// 它按 <c>ShouldSelectLocalCard(player)</c> 分流：本地玩家 → 本地选择栈/UI；
    /// 非本地玩家 → <c>_selectorStack</c>，取不到就 <c>ReportSoftlock()</c>，也就是永久卡住。
    /// 这就是"带弃牌效果的牌一打就卡死"的原因。
    ///
    /// 用 PushSelector(selector, isLocal:false) 只影响 AI，本地玩家仍然走自己的 UI。
    /// </summary>
    private sealed class AiCardSelector : ICardSelector
    {
        public Task<IEnumerable<CardModel>> GetSelectedCards(
            IEnumerable<CardModel> candidates, int min, int max)
        {
            var list = (candidates ?? Enumerable.Empty<CardModel>()).ToList();
            if (list.Count == 0)
            {
                LogError("coop-select: 没有候选牌");
                return Task.FromResult<IEnumerable<CardModel>>(Array.Empty<CardModel>());
            }

            // 多选的关键：优先满足 min；min==0 且 max 是 2~3 张时，尽量选满 max
            // （例如"弃 2 张"这类效果）。max 很大时只选 1 张，避免把整手牌全扔了。
            int upper = max > 0 ? max : (min > 0 ? min : list.Count);
            int lower = Math.Max(0, min);
            int take;
            // 2~3 张的小多选优先选满 max（"弃 2 张 / 选 2 张"这类；min 可能是 1 或 0）。
            if (max >= 2 && max <= 3) take = max;
            else if (lower > 0) take = lower;
            else take = 1;

            take = Math.Min(take, Math.Min(upper, list.Count));
            if (take <= 0) take = Math.Min(1, list.Count);
            if (take < lower)
            {
                LogError($"coop-select: 候选不足，要求 {lower} 张但只有 {list.Count} 张");
            }

            // 要选的时候优先扔掉最没用的：状态/诅咒排最前，其余按 伤害+格挡+费用 升序
            var picked = list
                .OrderByDescending(IsJunkCard)
                .ThenBy(CoopSimpleCardValue)
                .Take(take)
                .ToList();

            LogError($"coop-select: 请求 {min}..{max} 张，从 {list.Count} 张里选 {take} 张 → "
                     + $"{string.Join(", ", picked.Select(c => Safe(() => c.Id.Entry, "?")))}");
            return Task.FromResult<IEnumerable<CardModel>>(picked);
        }

        public CardRewardSelection GetSelectedCardReward(
            IReadOnlyList<CardCreationResult> cards,
            IReadOnlyList<CardRewardAlternative> alternatives)
        {
            // 之前返回 default（card 为 null），事件/奖励里"选一张卡"的流程会一直等、
            // 或者直接 NRE —— 这类"选牌/删牌/升级/附魔"的卡住都和它有关。
            try
            {
                if (cards != null && cards.Count > 0)
                {
                    var pick = cards[0].Card;
                    LogError($"coop-select: 卡牌奖励选第 1 张 → {Safe(() => pick.Id.Entry, "?")}");
                    return new CardRewardSelection { card = pick, alternative = null };
                }
            }
            catch (Exception ex)
            {
                LogError("coop-select: 奖励选择崩: " + ex.Message);
            }
            return default;
        }

        private static bool IsJunkCard(CardModel c)
        {
            CardType t = Safe(() => c.Type, CardType.None);
            return t == CardType.Status || t == CardType.Curse;
        }

        private static int CoopSimpleCardValue(CardModel c)
            => CoopCardDamage(c) + CoopCardBlock(c) + CoopCardCost(c);
    }
}
