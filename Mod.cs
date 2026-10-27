using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

namespace Sts2Companion;

/// <summary>
/// 只读桥接模组：把当前战斗状态写成 JSON，供外部队友程序轮询。
/// 不改游戏逻辑、不改存档。
/// </summary>
[ModInitializer(nameof(Init))]
public static partial class CompanionMod
{
    private static readonly string[] OutputDirs =
    {
        @"D:\sts2_companion\state",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "SlayTheSpire2", "sts2_companion"),
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Regex BbCode = new(@"\[[^\]]*\]", RegexOptions.Compiled);

    private static string? _outFile;
    private static string _advicePath = "";
    private static Timer? _timer;
    private static string _lastBody = "";
    private static string _lastError = "";
    private static long _lastErrorTime;
    private static int _busy;

    private static Godot.PanelContainer? _panel;
    private static Godot.Label? _head;
    private static Godot.Label? _state;
    private static Godot.RichTextLabel? _body;
    private static string _uiHead = "";
    private static string _uiState = "";
    private static string _uiText = "";
    private static DateTime _lastBeat = DateTime.MinValue;

    private static Godot.LineEdit? _input;
    private static bool _panelForce;
    private static bool _tWasDown;
    private static bool _escWasDown;

    // 角色选择（含 mod 角色；ModelDb.AllCharacters 是唯一来源）
    private static Godot.OptionButton? _charLocalOption;
    private static Godot.OptionButton? _charAiOption;
    private static List<CharacterModel> _charChoices = new();
    private static string _charCfgPath = "";
    private static long _lastCharRefreshMs;

    // AI 来源：本地规则 / OpenAI 兼容 API（当前 API 只接管战斗出牌）
    private static bool _useApiBrain;
    private static string _apiBaseUrl = "https://api.openai.com/v1";
    private static string _apiKey = "";
    private static string _apiModel = "gpt-4o-mini";
    private static string _aiCfgPath = "";
    private static Godot.OptionButton? _aiModeOption;
    private static Godot.VBoxContainer? _apiBox;
    private static Godot.LineEdit? _apiBaseEdit;
    private static Godot.LineEdit? _apiKeyEdit;
    private static Godot.LineEdit? _apiModelEdit;
    private static Godot.Button? _apiSaveButton;
    private static Godot.Button? _panelCloseButton;
    private static Godot.Label? _aiSourceLabel;
    private static bool _loadingAiCfg;

    // 对话框：只有 API/本地模型模式可用；memory/persona 可持久化
    private static string _chatPersona = "";
    private static string _chatMemory = "";
    private static Godot.LineEdit? _chatPersonaEdit;
    private static Godot.LineEdit? _chatMemoryEdit;
    private static bool _chatBusy;
    private static string _chatDisplay = "";
    private static long _chatDisplayUntilMs;
    private static readonly List<Dictionary<string, string>> _chatHistory = new();

    // 0=follow game, 1=Chinese, 2=English
    private static int _langMode;
    private static string _resolvedLang = "en";
    private static Godot.Label? _langLabel;
    private static Godot.OptionButton? _langOption;
    private static Godot.Button? _charFlowAiButton;
    private static long _langCheckMs;

    // 主菜单「单人 + AI」入口 + 两段式选人
    private static Godot.Control? _coopMenuButton;
    private static long _menuEntryLastMs;
    private static long _menuMissLastMs;
    private static long _menuButtonDiagMs;
    private static bool _charFlowActive;
    private static long _charFlowOpenMs;
    private static int _charFlowStep;
    private static CharacterModel? _charFlowLocal;
    private static NCharacterSelectScreen? _charFlowScreen;
    private static Godot.Button? _charFlowButton;
    private static Godot.Label? _charFlowHint;
    private static Godot.Button? _charFlowMinus;
    private static Godot.Button? _charFlowPlus;
    private static Godot.Label? _charFlowCount;
    private static int _partySize = 3;
    private static readonly CharacterModel?[] _flowChars = new CharacterModel?[4];
    private static FieldInfo? _miSingleplayerMenuButton;
    private static FieldInfo? _miCharacterSelectedButton;
    private static FieldInfo? _miCharacterEmbarkButton;

    // ---------- 单人+AI 测试入口（阶段1：双人开局验证） ----------
    private static Godot.Button? _coopButton;
    private static bool _starting;

    // AI 双人档状态
    private static bool _aiMode;                       // 当前是否在 AI 双人档里
    private static long _aiLastDiagMs;                 // 上次 dump 诊断的时间戳
    private static long _aiLastTickMs;                 // 上次心跳日志时间戳
    private static long _lastCoopTickMs;               // PollCoopAI 节流时间戳
    private static CoopLoopNode? _coopLoopNode;        // 复用一个循环节点，避免每次开局都 AddChild
    private static readonly TimeSpan _aiMapRetryGap = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan _aiDiagGap = TimeSpan.FromMilliseconds(3000);
    private static readonly TimeSpan _aiTickGap = TimeSpan.FromMilliseconds(2000);

    // 有选项的房间类型（双人投票会卡 AI）
    private static readonly string[] ChoiceRoomKeywords = new[]
    {
        "Neow", "Ancient", "Event", "Shop", "Treasure", "Rest", "Relic", "Choice", "Select"
    };

    public static void Init()
    {
        foreach (string dir in OutputDirs)
        {
            try
            {
                Directory.CreateDirectory(dir);
                _outFile = Path.Combine(dir, "live.json");
                break;
            }
            catch
            {
                // 换下一个目录
            }
        }

        if (_outFile == null)
        {
            LogError("no writable output directory");
            return;
        }

        _advicePath = Path.Combine(Path.GetDirectoryName(_outFile)!, "advice.json");
        _charCfgPath = Path.Combine(Path.GetDirectoryName(_outFile)!, "companion_chars.json");
        _aiCfgPath = Path.Combine(Path.GetDirectoryName(_outFile)!, "companion_ai.json");
        LoadAiConfig();
        StartBackend();
        BuildUi();

        // 记下主线程身份，Tick 必须在主线程跑：
        // Tick → BuildState → SnapshotPlayer/Card 会读 Player、CardModel、本地化对象，
        // 而 System.Threading.Timer 的回调在线程池上。之前就是直接 Tick()，
        // godot.log 里能看到 Tick 的调用栈来自 ThreadPoolWorkQueue.Dispatch —— 战斗一忙就崩。
        _mainThreadId = Environment.CurrentManagedThreadId;
        _mainCtx = SynchronizationContext.Current;
        LogError($"ui: Init 主线程={_mainThreadId} SyncContext={(_mainCtx == null ? "null" : _mainCtx.GetType().Name)}");

        _timer = new Timer(_ => RunOnMain(Tick), null, 0, 500);
    }

    private static int _mainThreadId;
    private static SynchronizationContext? _mainCtx;
    private static bool _mainCaptured;

    private static void EnsureMainThreadCaptured()
    {
        if (_mainCaptured) return;
        _mainCaptured = true;
        _mainThreadId = Environment.CurrentManagedThreadId;
        _mainCtx ??= SynchronizationContext.Current;
        LogError($"ui: 主线程已确认 id={_mainThreadId} "
                 + $"ctx={(_mainCtx == null ? "null" : _mainCtx.GetType().Name)}");
    }

    /// <summary>
    /// 保证 action 在 Godot 主线程执行。
    /// 没有捕获到 SynchronizationContext 时宁可不执行，也不要在后台线程碰游戏对象——
    /// 那正是无响应/硬崩的来源。
    /// </summary>
    private static void RunOnMain(Action action)
    {
        if (Environment.CurrentManagedThreadId == _mainThreadId)
        {
            try { action(); }
            catch (Exception ex) { LogError("RunOnMain: " + ex); }
            return;
        }

        var ctx = _mainCtx;
        if (ctx == null) return;

        ctx.Post(_ =>
        {
            try { action(); }
            catch (Exception ex) { LogError("RunOnMain(post): " + ex); }
        }, null);
    }

    private static void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        try
        {
            Dictionary<string, object?> root = BuildState();
            string body = JsonSerializer.Serialize(root, JsonOpts);
            if (body != _lastBody)
            {
                // 局面没变化就不刷盘；变化时才更新 updated 时间戳。
                root["updated"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                string json = JsonSerializer.Serialize(root, JsonOpts);

                string tmp = _outFile + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _outFile!, overwrite: true);
                _lastBody = body;
                DumpCombat(root, body);
            }

            RefreshUi(root);
            Heartbeat();
        }
        catch (Exception ex)
        {
            LogError(ex.ToString());
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private static Dictionary<string, object?> BuildState()
    {
        var root = new Dictionary<string, object?>
        {
            ["coop"] = _aiMode,
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
        };

        CombatState? state = null;
        try
        {
            state = CombatManager.Instance.DebugOnlyGetState();
        }
        catch
        {
            // 忽略，按不在战斗处理
        }

        bool inCombat = state != null && Safe(() => CombatManager.Instance.IsInProgress, false);
        root["inCombat"] = inCombat;
        if (!inCombat || state == null)
        {
            return root;
        }

        root["round"] = state.RoundNumber;
        root["side"] = Safe(() => state.CurrentSide.ToString(), "Unknown");
        root["combatId"] = Safe(() => CombatManager.Instance.CurrentCombatId?.ToString(), "");

        var players = new List<object?>();
        foreach (Player p in state.Players)
        {
            players.Add(SnapshotPlayer(p));
        }
        root["players"] = players;
        root["turn"] = Safe(() => state.Players[0].PlayerCombatState?.TurnNumber ?? 0, 0);

        var enemies = new List<object?>();
        IReadOnlyList<Creature> targets = state.PlayerCreatures;
        foreach (Creature c in state.Enemies)
        {
            enemies.Add(SnapshotEnemy(c, targets));
        }
        root["enemies"] = enemies;

        return root;
    }

    private static Dictionary<string, object?> SnapshotPlayer(Player p)
    {
        PlayerCombatState? pcs = Safe<PlayerCombatState?>(() => p.PlayerCombatState, null);
        Creature creature = p.Creature;

        var hand = new List<object?>();
        var drawPile = new List<string>();
        var discardPile = new List<string>();
        if (pcs != null)
        {
            foreach (CardModel card in pcs.Hand.Cards)
            {
                hand.Add(SnapshotCard(card));
            }
            // 两堆只导牌名（不带描述）：控制 JSON 体积，也让模型能推理抽牌/捞牌。
            foreach (CardModel card in pcs.DrawPile.Cards)
            {
                drawPile.Add(Str(() => card.Title, card.Id.Entry));
            }
            foreach (CardModel card in pcs.DiscardPile.Cards)
            {
                discardPile.Add(Str(() => card.Title, card.Id.Entry));
            }
        }

        var relics = new List<string>();
        foreach (RelicModel relic in Safe<IReadOnlyList<RelicModel>>(() => p.Relics, Array.Empty<RelicModel>()))
        {
            // 名字+效果描述：mod 遗物也能直接把机制喂给队友，不用它猜。
            relics.Add(WithDesc(
                Str(() => relic.Title.GetFormattedText(), relic.Id.Entry),
                Clean(Safe(() => new LocString("relics", relic.Id.Entry + ".description").GetFormattedText(), ""))));
        }

        var potions = new List<string>();
        foreach (PotionModel? potion in Safe<IReadOnlyList<PotionModel?>>(() => p.PotionSlots, Array.Empty<PotionModel?>()))
        {
            if (potion == null)
            {
                continue;
            }
            potions.Add(WithDesc(
                Str(() => potion.Title.GetFormattedText(), potion.Id.Entry),
                Clean(Safe(() => new LocString("potions", potion.Id.Entry + ".description").GetFormattedText(), ""))));
        }

        var powers = new List<string>();
        foreach (PowerModel power in Safe<IReadOnlyList<PowerModel>>(() => creature.Powers, Array.Empty<PowerModel>()))
        {
            int amount = Safe(() => power.Amount, 0);
            string title = Str(() => power.Title.GetFormattedText(), power.Id.Entry);
            powers.Add(amount != 0 ? $"{title} {amount}" : title);
        }

        return new Dictionary<string, object?>
        {
            ["name"] = Str(() => creature.Name, p.Character.Id.Entry),
            ["netId"] = p.NetId,
            ["hp"] = Safe(() => creature.CurrentHp, 0),
            ["maxHp"] = Safe(() => creature.MaxHp, 0),
            ["block"] = Safe(() => creature.Block, 0),
            ["energy"] = pcs == null ? 0 : Safe(() => pcs.Energy, 0),
            ["maxEnergy"] = pcs == null ? 0 : Safe(() => pcs.MaxEnergy, 0),
            ["stars"] = pcs == null ? 0 : Safe(() => pcs.Stars, 0),
            ["gold"] = Safe(() => p.Gold, 0),
            ["hand"] = hand,
            ["drawPile"] = drawPile,
            ["discardPile"] = discardPile,
            ["drawCount"] = pcs == null ? 0 : pcs.DrawPile.Cards.Count,
            ["discardCount"] = pcs == null ? 0 : pcs.DiscardPile.Cards.Count,
            ["exhaustCount"] = pcs == null ? 0 : pcs.ExhaustPile.Cards.Count,
            ["relics"] = relics,
            ["potions"] = potions,
            ["powers"] = powers,
        };
    }

    private static string WithDesc(string name, string desc)
    {
        return desc.Length > 0 ? $"{name}：{desc}" : name;
    }

    private static Dictionary<string, object?> SnapshotCard(CardModel card)
    {
        int cost = Safe(() => card.EnergyCost.CostsX ? -1 : card.EnergyCost.GetWithModifiers(CostModifiers.All), 0);
        return new Dictionary<string, object?>
        {
            ["name"] = Str(() => card.Title, card.Id.Entry),
            ["cost"] = cost,
            ["upgraded"] = Safe(() => card.IsUpgraded, false),
            ["type"] = Safe(() => card.Type.ToString(), ""),
            ["desc"] = Clean(Safe(() => card.Description.GetFormattedText(), "")),
        };
    }

    private static Dictionary<string, object?> SnapshotEnemy(Creature c, IReadOnlyList<Creature> targets)
    {
        var intents = new List<string>();
        int damage = 0;
        try
        {
            MonsterModel? monster = c.Monster;
            if (monster != null)
            {
                foreach (AbstractIntent intent in monster.NextMove.Intents)
                {
                    intents.Add(Str(() => intent.GetIntentLabel(targets, c).GetFormattedText(),
                                    intent.IntentType.ToString()));

                    // 攻击意图额外给出实际伤害（已算力量/虚弱等修正，多次攻击已乘段数），
                    // 让队友能直接算账，不用自己猜意图文字里的数字。
                    if (intent is AttackIntent attack)
                    {
                        int hit = Safe(() => attack.GetTotalDamage(targets, c), 0);
                        if (hit > 0)
                        {
                            damage += hit;
                        }
                    }
                }
            }
        }
        catch
        {
            // 意图读取失败就给空列表
        }

        var powers = new List<string>();
        foreach (PowerModel power in Safe<IReadOnlyList<PowerModel>>(() => c.Powers, Array.Empty<PowerModel>()))
        {
            int amount = Safe(() => power.Amount, 0);
            string title = Str(() => power.Title.GetFormattedText(), power.Id.Entry);
            powers.Add(amount != 0 ? $"{title} {amount}" : title);
        }

        return new Dictionary<string, object?>
        {
            ["name"] = Str(() => c.Name, "?"),
            ["hp"] = Safe(() => c.CurrentHp, 0),
            ["maxHp"] = Safe(() => c.MaxHp, 0),
            ["block"] = Safe(() => c.Block, 0),
            ["intents"] = intents,
            ["damage"] = damage,
            ["powers"] = powers,
        };
    }

    private static string Clean(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }
        text = BbCode.Replace(text, "").Replace("\n", " ").Replace("\r", " ").Trim();
        return text.Length <= 80 ? text : text.Substring(0, 80) + "…";
    }

    private static string Str(Func<string?> get, string fallback)
    {
        try
        {
            string? value = get();
            return string.IsNullOrWhiteSpace(value) ? fallback : value!;
        }
        catch
        {
            return fallback;
        }
    }

    private static T Safe<T>(Func<T> get, T fallback)
    {
        try
        {
            return get();
        }
        catch
        {
            return fallback;
        }
    }

    // ---------- 存活心跳：游戏关掉后让后台队友自己退出 ----------

    private static void Heartbeat()
    {
        try
        {
            if ((DateTime.UtcNow - _lastBeat).TotalSeconds < 10)
            {
                return;
            }
            string dir = Path.GetDirectoryName(_outFile!) ?? ".";
            File.WriteAllText(Path.Combine(dir, "game_alive"),
                              DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
            _lastBeat = DateTime.UtcNow;
        }
        catch
        {
            // 心跳写不了不影响主流程
        }
    }

    // ---------- 战斗快照留档：出问题时能回看当时导出的字段 ----------

    private static void DumpCombat(Dictionary<string, object?> root, string body)
    {
        try
        {
            if (!(root.TryGetValue("inCombat", out object? flag) && flag is bool combat && combat))
            {
                return;
            }
            string dir = Path.GetDirectoryName(_outFile!) ?? ".";
            string path = Path.Combine(dir, "combat.log");
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 1_000_000)
            {
                File.Delete(path);
            }
            File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss") + " " + body + "\n");
        }
        catch
        {
            // 留档失败不影响导出
        }
    }

    // ---------- 队友后端自动启动 ----------

    private static void StartBackend()
    {
        try
        {
            string root = @"D:\sts2_companion";
            string pythonw = @"D:\ai\.venv\Scripts\pythonw.exe";

            string modDir = Path.GetDirectoryName(typeof(CompanionMod).Assembly.Location) ?? "";
            string cfg = Path.Combine(modDir, "companion_path.txt");
            if (File.Exists(cfg))
            {
                string[] lines = File.ReadAllLines(cfg);
                if (lines.Length > 0 && lines[0].Trim().Length > 0)
                {
                    root = lines[0].Trim();
                }
                if (lines.Length > 1 && lines[1].Trim().Length > 0)
                {
                    pythonw = lines[1].Trim();
                }
            }

            string agent = Path.Combine(root, "agent.py");
            if (!File.Exists(agent) || !File.Exists(pythonw))
            {
                LogError("backend not found: " + agent + " / " + pythonw);
                return;
            }

            string pidFile = Path.Combine(root, "state", "agent.pid");
            if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out int pid))
            {
                try
                {
                    using Process existing = Process.GetProcessById(pid);
                    if (!existing.HasExited)
                    {
                        return;
                    }
                }
                catch
                {
                    // 进程已退出，继续启动
                }
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = pythonw,
                Arguments = "\"" + agent + "\"",
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            LogError("start backend: " + ex);
        }
    }

    // ---------- 游戏内面板 ----------

    private static void BuildUi()
    {
        try
        {
            if (Godot.Engine.GetMainLoop() is not Godot.SceneTree tree)
            {
                return;
            }

            var layer = new Godot.CanvasLayer { Layer = 100 };
            var panel = new Godot.PanelContainer
            {
                MouseFilter = Godot.Control.MouseFilterEnum.Ignore,
            };

            var style = new Godot.StyleBoxFlat
            {
                BgColor = new Godot.Color(0.09f, 0.08f, 0.11f, 0.90f),
                BorderColor = new Godot.Color(0.95f, 0.76f, 0.31f, 0.45f),
            };
            style.SetCornerRadiusAll(10);
            style.SetBorderWidthAll(1);
            style.SetContentMarginAll(12);
            panel.AddThemeStyleboxOverride("panel", style);

            panel.AnchorLeft = 1f;
            panel.AnchorRight = 1f;
            panel.AnchorTop = 0f;
            panel.AnchorBottom = 0f;
            panel.OffsetLeft = -400f;
            panel.OffsetRight = -16f;
            panel.OffsetTop = 110f;
            panel.OffsetBottom = 700f;

            var vbox = new Godot.VBoxContainer
            {
                MouseFilter = Godot.Control.MouseFilterEnum.Ignore,
            };
            vbox.AddThemeConstantOverride("separation", 6);

            _head = new Godot.Label
            {
                Text = Tr("STS2 队友", "STS2 Companion"),
                MouseFilter = Godot.Control.MouseFilterEnum.Ignore,
            };
            _head.AddThemeFontSizeOverride("font_size", 15);
            _head.AddThemeColorOverride("font_color", new Godot.Color(0.95f, 0.76f, 0.31f));

            _state = new Godot.Label
            {
                Text = "",
                AutowrapMode = Godot.TextServer.AutowrapMode.WordSmart,
                MouseFilter = Godot.Control.MouseFilterEnum.Ignore,
            };
            _state.AddThemeFontSizeOverride("font_size", 12);
            _state.AddThemeColorOverride("font_color", new Godot.Color(0.72f, 0.70f, 0.76f));

            _body = new Godot.RichTextLabel
            {
                BbcodeEnabled = false,
                ScrollActive = true,
                FitContent = false,
                AutowrapMode = Godot.TextServer.AutowrapMode.WordSmart,
                MouseFilter = Godot.Control.MouseFilterEnum.Ignore,
                SizeFlagsVertical = Godot.Control.SizeFlags.ExpandFill,
            };
            _body.AddThemeFontSizeOverride("normal_font_size", 13);
            _body.AddThemeColorOverride("default_color", new Godot.Color(0.92f, 0.90f, 0.86f));

            _input = new Godot.LineEdit
            {
                PlaceholderText = Tr("按 T 输入，Enter 发送，Esc 退出",
                                     "Press T to type, Enter to send, Esc to close"),
                FocusMode = Godot.Control.FocusModeEnum.All,
                MouseFilter = Godot.Control.MouseFilterEnum.Stop,
            };
            _input.AddThemeFontSizeOverride("font_size", 13);
            _input.TextSubmitted += OnChatSubmit;

            var aiRow = new Godot.HBoxContainer
            {
                MouseFilter = Godot.Control.MouseFilterEnum.Stop,
            };
            aiRow.AddThemeConstantOverride("separation", 6);

            var aiLabel = new Godot.Label
            {
                Text = "AI 来源",
                CustomMinimumSize = new Godot.Vector2(58, 0),
                VerticalAlignment = Godot.VerticalAlignment.Center,
                MouseFilter = Godot.Control.MouseFilterEnum.Ignore,
            };
            aiLabel.AddThemeFontSizeOverride("font_size", 12);
            aiLabel.AddThemeColorOverride("font_color", new Godot.Color(0.85f, 0.82f, 0.70f));

            _aiModeOption = new Godot.OptionButton
            {
                CustomMinimumSize = new Godot.Vector2(180, 30),
                SizeFlagsHorizontal = Godot.Control.SizeFlags.ExpandFill,
                TooltipText = "本地规则不联网；API 模式目前只接管战斗出牌，其它流程仍用本地规则",
                MouseFilter = Godot.Control.MouseFilterEnum.Stop,
            };
            _aiModeOption.AddItem("本地规则", 0);
            _aiModeOption.AddItem("API（战斗）", 1);
            _aiModeOption.AddThemeFontSizeOverride("font_size", 12);
            _aiModeOption.ItemSelected += _ =>
            {
                if (_loadingAiCfg) return;
                _useApiBrain = _aiModeOption.Selected == 1;
                ReadApiFieldsFromUi();
                UpdateApiBoxVisibility();
                SaveAiConfig();
            };
            aiRow.AddChild(aiLabel);
            aiRow.AddChild(_aiModeOption);
            _aiSourceLabel = aiLabel;

            var langRow = new Godot.HBoxContainer
            {
                MouseFilter = Godot.Control.MouseFilterEnum.Stop,
            };
            langRow.AddThemeConstantOverride("separation", 6);

            _langLabel = new Godot.Label
            {
                Text = Tr("语言", "Language"),
                CustomMinimumSize = new Godot.Vector2(58, 0),
                VerticalAlignment = Godot.VerticalAlignment.Center,
                MouseFilter = Godot.Control.MouseFilterEnum.Ignore,
            };
            _langLabel.AddThemeFontSizeOverride("font_size", 12);
            _langLabel.AddThemeColorOverride("font_color", new Godot.Color(0.85f, 0.82f, 0.70f));

            _langOption = new Godot.OptionButton
            {
                CustomMinimumSize = new Godot.Vector2(180, 30),
                SizeFlagsHorizontal = Godot.Control.SizeFlags.ExpandFill,
                TooltipText = Tr("跟随游戏语言；也可以手动锁定中文或 English",
                                 "Follow the game language, or lock Chinese/English manually"),
                MouseFilter = Godot.Control.MouseFilterEnum.Stop,
            };
            _langOption.AddItem(Tr("跟随游戏", "Follow game"), 0);
            _langOption.AddItem("中文", 1);
            _langOption.AddItem("English", 2);
            _langOption.AddThemeFontSizeOverride("font_size", 12);
            _langOption.ItemSelected += _ =>
            {
                if (_loadingAiCfg) return;
                _langMode = Math.Clamp(_langOption.Selected, 0, 2);
                _resolvedLang = PreferredLanguage();
                ApplyLanguageToUi();
                SaveAiConfig();
            };
            langRow.AddChild(_langLabel);
            langRow.AddChild(_langOption);

            _apiBox = new Godot.VBoxContainer
            {
                MouseFilter = Godot.Control.MouseFilterEnum.Stop,
                Visible = false,
            };
            _apiBox.AddThemeConstantOverride("separation", 4);

            _apiBaseEdit = new Godot.LineEdit
            {
                PlaceholderText = "Base URL，例如 https://api.openai.com/v1",
                CustomMinimumSize = new Godot.Vector2(0, 30),
            };
            _apiKeyEdit = new Godot.LineEdit
            {
                PlaceholderText = "API Key（只保存在本机 state 目录）",
                Secret = true,
                CustomMinimumSize = new Godot.Vector2(0, 30),
            };
            _apiModelEdit = new Godot.LineEdit
            {
                PlaceholderText = "模型，例如 gpt-4o-mini / deepseek-chat",
                CustomMinimumSize = new Godot.Vector2(0, 30),
            };
            _chatPersonaEdit = new Godot.LineEdit
            {
                PlaceholderText = "调教 / 角色设定（可选）",
                CustomMinimumSize = new Godot.Vector2(0, 30),
            };
            _chatMemoryEdit = new Godot.LineEdit
            {
                PlaceholderText = "长期记忆（可选，会发给模型）",
                CustomMinimumSize = new Godot.Vector2(0, 30),
            };
            foreach (var edit in new[]
                     {
                         _apiBaseEdit, _apiKeyEdit, _apiModelEdit,
                         _chatPersonaEdit, _chatMemoryEdit
                     })
            {
                edit.AddThemeFontSizeOverride("font_size", 12);
                _apiBox.AddChild(edit);
            }

            var apiSave = new Godot.Button
            {
                Text = "保存 API 设置",
                CustomMinimumSize = new Godot.Vector2(0, 30),
                MouseFilter = Godot.Control.MouseFilterEnum.Stop,
            };
            apiSave.AddThemeFontSizeOverride("font_size", 12);
            apiSave.Pressed += () =>
            {
                ReadApiFieldsFromUi();
                SaveAiConfig();
            };
            _apiBox.AddChild(apiSave);
            _apiSaveButton = apiSave;

            var closePanel = new Godot.Button
            {
                Text = "关闭面板",
                CustomMinimumSize = new Godot.Vector2(0, 28),
                MouseFilter = Godot.Control.MouseFilterEnum.Stop,
            };
            closePanel.AddThemeFontSizeOverride("font_size", 12);
            closePanel.Pressed += () =>
            {
                _panelForce = false;
                if (_panel != null) _panel.Visible = false;
            };
            _panelCloseButton = closePanel;

            vbox.AddChild(_head);
            vbox.AddChild(_state);
            vbox.AddChild(aiRow);
            vbox.AddChild(langRow);
            vbox.AddChild(_apiBox);
            vbox.AddChild(_body);
            vbox.AddChild(_input);
            vbox.AddChild(closePanel);
            panel.AddChild(vbox);

            ApplyAiConfigToUi();

            // 独立按钮：不动 panel/vbox 结构，单独 Add 到 layer
            var coopBtn = new Godot.Button
            {
                Text = "AI 双人档",
                Flat = true,
                MouseFilter = Godot.Control.MouseFilterEnum.Stop,
                Position = new Godot.Vector2(1380, 55),  // 屏幕右上，面板正上方
                Size = new Godot.Vector2(180, 44),
                ZIndex = 101,
                Visible = false, // 入口改到主菜单的「单人 + AI」按钮
            };
            coopBtn.AddThemeFontSizeOverride("font_size", 22);
            coopBtn.AddThemeColorOverride("font_color", new Godot.Color(0.92f, 0.80f, 0.45f));
            coopBtn.AddThemeColorOverride("font_hover_color", new Godot.Color(1f, 0.93f, 0.65f));
            coopBtn.AddThemeColorOverride("font_pressed_color", new Godot.Color(0.85f, 0.68f, 0.30f));
            coopBtn.Pressed += OnCoopPressed;

            layer.AddChild(panel);
            layer.AddChild(coopBtn);
            tree.Root.AddChild(layer);
            _kwInstance = new KeyWatcher();
            tree.Root.AddChild(_kwInstance);

            // 注册 SceneTree.TreeProcessed 信号——这是 SceneTree 级别，永不会被场景切换踢掉！
            if (_treeForSignals == null)
            {
                _treeForSignals = tree;
                tree.ProcessFrame += OnTreeProcessed;
                LogError("ui: 注册 ProcessFrame 信号 ✓（SceneTree 级别，永不丢）");
            }

            // 角色选择条：放在顶部，主菜单也能用；选项来自 ModelDb.AllCharacters（含 mod）。
            BuildCharacterSelectors(layer);

            // AI 独立循环节点。复用同一个实例：每次点「AI 双人档」都 AddChild
            // 会在场景树里堆一串节点，虽然 40ms 节流让逻辑不重复，但每帧回调会线性增加。
            EnsureCoopLoopNode();

            _panel = panel;
        }
        catch (Exception ex)
        {
            LogError("ui: " + ex);
        }
    }

    // ---------- 角色选择（含 mod 角色） ----------

    private static void BuildCharacterSelectors(Godot.CanvasLayer layer)
    {
        try
        {
            var bar = new Godot.HBoxContainer
            {
                Position = new Godot.Vector2(1000, 8),
                Size = new Godot.Vector2(430, 38),
                ZIndex = 101,
                MouseFilter = Godot.Control.MouseFilterEnum.Stop,
                Visible = false, // 改用主菜单两段式选人；保留控件只做 OpenCoopPressed 的兜底。
            };
            bar.AddThemeConstantOverride("separation", 8);

            _charLocalOption = new Godot.OptionButton
            {
                CustomMinimumSize = new Godot.Vector2(205, 36),
                ClipText = true,
                TooltipText = "本地玩家角色（含 mod；下局生效，改完点 AI 双人档）",
                MouseFilter = Godot.Control.MouseFilterEnum.Stop,
            };
            _charAiOption = new Godot.OptionButton
            {
                CustomMinimumSize = new Godot.Vector2(205, 36),
                ClipText = true,
                TooltipText = "AI 队友角色（含 mod；下局生效，改完点 AI 双人档）",
                MouseFilter = Godot.Control.MouseFilterEnum.Stop,
            };
            _charLocalOption.AddThemeFontSizeOverride("font_size", 15);
            _charAiOption.AddThemeFontSizeOverride("font_size", 15);
            _charLocalOption.ItemSelected += _ => SaveCharacterChoice();
            _charAiOption.ItemSelected += _ => SaveCharacterChoice();

            bar.AddChild(_charLocalOption);
            bar.AddChild(_charAiOption);
            layer.AddChild(bar);

            RefreshCharacterOptions();
            LoadCharacterChoice();
        }
        catch (Exception ex)
        {
            LogError("chars: UI: " + ex.Message);
        }
    }

    private static void RefreshCharacterOptions()
    {
        string oldLocal = Safe(() => SelectedCharacter(_charLocalOption)?.Id.Entry ?? "", "");
        string oldAi = Safe(() => SelectedCharacter(_charAiOption)?.Id.Entry ?? "", "");

        List<CharacterModel> chars;
        try
        {
            chars = ModelDb.AllCharacters
                .Where(c => c != null)
                .OrderBy(c => c.Id.Entry, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            chars = new List<CharacterModel>();
        }

        // 极端情况下 ModelDb 还没准备好：至少保留原版两人，别让按钮空着。
        if (chars.Count == 0)
        {
            try { chars.Add(ModelDb.Character<Ironclad>()); } catch { }
            try { chars.Add(ModelDb.Character<Silent>()); } catch { }
        }

        _charChoices = chars;
        _charLocalOption?.Clear();
        _charAiOption?.Clear();
        for (int i = 0; i < chars.Count; i++)
        {
            CharacterModel c = chars[i];
            string id = Safe(() => c.Id.Entry, "?");
            string name = Safe(() => c.Title.GetFormattedText(), id);
            name = Clean(name);
            if (name.Length > 22) name = name.Substring(0, 22) + "…";
            _charLocalOption?.AddItem("我: " + name, i);
            _charAiOption?.AddItem("AI: " + name, i);
        }

        if (chars.Count > 0)
        {
            int local = IndexOfCharacter(oldLocal);
            if (local < 0) local = IndexOfCharacter("IRONCLAD");
            int ai = IndexOfCharacter(oldAi);
            if (ai < 0) ai = IndexOfCharacter("SILENT");
            if (_charLocalOption != null) _charLocalOption.Selected = local >= 0 ? local : 0;
            if (_charAiOption != null) _charAiOption.Selected = ai >= 0 ? ai : Math.Min(1, chars.Count - 1);
        }
    }

    /// <summary>
    /// ModelDb 可能在 mod Init 之后才填满；主菜单期间每 3 秒检查一次，
    /// 有新的 mod 角色就刷新下拉框（保留当前选择）。
    /// </summary>
    private static void EnsureCharacterOptions()
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (nowMs - _lastCharRefreshMs < 3000) return;
        _lastCharRefreshMs = nowMs;

        int total = Safe(() => ModelDb.AllCharacters.Count(), 0);
        if (total > _charChoices.Count)
        {
            RefreshCharacterOptions();
            LogError($"chars: 发现 {total} 个角色，已刷新下拉框");
        }
    }

    private static int IndexOfCharacter(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return -1;
        for (int i = 0; i < _charChoices.Count; i++)
        {
            if (string.Equals(Safe(() => _charChoices[i].Id.Entry, ""), id,
                              StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    private static CharacterModel? SelectedCharacter(Godot.OptionButton? option)
    {
        if (option == null) return null;
        int index = option.Selected;
        if (index >= 0 && index < _charChoices.Count) return _charChoices[index];
        return null;
    }

    private static void LoadCharacterChoice()
    {
        try
        {
            if (_charCfgPath.Length == 0 || !File.Exists(_charCfgPath)) return;
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_charCfgPath));
            if (dict == null) return;

            if (dict.TryGetValue("local", out string? localId))
            {
                int i = IndexOfCharacter(localId);
                if (i >= 0 && _charLocalOption != null) _charLocalOption.Selected = i;
            }
            if (dict.TryGetValue("ai", out string? aiId))
            {
                int i = IndexOfCharacter(aiId);
                if (i >= 0 && _charAiOption != null) _charAiOption.Selected = i;
            }
        }
        catch (Exception ex)
        {
            LogError("chars: load: " + ex.Message);
        }
    }

    private static void SaveCharacterChoice()
    {
        try
        {
            if (_charCfgPath.Length == 0) return;
            var dict = new Dictionary<string, string>
            {
                ["local"] = Safe(() => SelectedCharacter(_charLocalOption)?.Id.Entry ?? "", ""),
                ["ai"] = Safe(() => SelectedCharacter(_charAiOption)?.Id.Entry ?? "", ""),
            };
            string? dir = Path.GetDirectoryName(_charCfgPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_charCfgPath, JsonSerializer.Serialize(dict, JsonOpts));
        }
        catch (Exception ex)
        {
            LogError("chars: save: " + ex.Message);
        }
    }

    private static void RefreshUi(Dictionary<string, object?> root)
    {
        if (_panel == null)
        {
            return;
        }

        try
        {
            bool inCombat = root.TryGetValue("inCombat", out object? flag) && flag is bool b && b;
            int round = root.TryGetValue("round", out object? rv) && rv is int ri ? ri : 0;
            bool show = inCombat || _panelForce;
            _panel.SetDeferred("visible", show);
            if (!show)
            {
                return;
            }

            string status = ReadAdvice(out string advice, out int adviceRound, out string ask, out string mate);
            if (mate.Length == 0)
            {
                mate = Tr("队友", "Teammate");
            }
            if (adviceRound > 0)
            {
                round = adviceRound;
            }

            string head;
            string text;
            if (status == "thinking")
            {
                head = inCombat
                    ? Tr($"第{round}回合 · {mate}思考中…", $"Round {round} · {mate} is thinking…")
                    : Tr($"{mate}思考中…", $"{mate} is thinking…");
                text = ask.Length > 0
                    ? Tr("你：", "You: ") + ask
                    : Tr("正在看你的手牌…", "Reading your hand…");
            }
            else if (status == "ready")
            {
                head = inCombat
                    ? Tr($"第{round}回合 · {mate}建议", $"Round {round} · {mate}'s suggestion")
                    : mate;
                text = ask.Length > 0
                    ? Tr($"你：{ask}\n\n{mate}：{advice}", $"You: {ask}\n\n{mate}: {advice}")
                    : advice;
            }
            else
            {
                head = inCombat ? Tr($"第{round}回合", $"Round {round}") : mate;
                text = Tr("按 T 输入，Enter 发送，Esc 退出",
                          "Press T to type, Enter to send, Esc to close");
            }

            long nowChatMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_chatBusy)
            {
                text = Tr("正在请求模型…", "Contacting model…");
            }
            else if (_chatDisplay.Length > 0 && nowChatMs < _chatDisplayUntilMs)
            {
                text = _chatDisplay;
            }

            string summary = inCombat ? BuildSummary(root) : "";
            if (summary != _uiState)
            {
                _state?.SetDeferred("text", summary);
                _uiState = summary;
            }

            if (head != _uiHead)
            {
                _head?.SetDeferred("text", head);
                _uiHead = head;
            }
            if (text != _uiText)
            {
                _body?.SetDeferred("text", text);
                _uiText = text;
            }
        }
        catch
        {
            // 面板刷新失败不影响状态导出
        }
    }

    // ---------- 对话输入：T 唤起，Enter 发送，Esc 退出 ----------

    private static long _kwHeartbeatMs;
    private static KeyWatcher? _kwInstance;
    private static Godot.SceneTree? _treeForSignals;

    /// <summary>
    /// 注册 TreeProcessed 信号——这是 SceneTree 级别信号，不依赖任何 Node 存活！
    /// 每帧 Godot 都会发，我们在里面确保 KeyWatcher 活着 + 跑 PollCoopAI。
    /// </summary>
    private static void EnsureKeyWatcherAlive()
    {
        try
        {
            if (_kwInstance == null)
            {
                _kwInstance = new KeyWatcher();
                _kwInstance.Name = "Sts2CompanionKeyWatcher";
            }
            if (!_kwInstance.IsInsideTree())
            {
                var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                if (tree != null && tree.Root != null)
                {
                    tree.Root.AddChild(_kwInstance);
                    LogError($"kw: 被踢了 → 加回 Root, inTree={_kwInstance.IsInsideTree()}");
                }
            }

            // 注册 TreeProcessed 信号（只注册一次）
            if (_treeForSignals == null)
            {
                var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                if (tree != null)
                {
                    _treeForSignals = tree;
                    tree.ProcessFrame += OnTreeProcessed;
                    LogError("kw: 注册 ProcessFrame 信号 ✓");
                }
            }
        }
        catch (Exception ex)
        {
            LogError($"EnsureKeyWatcherAlive: {ex.Message}");
        }
    }

    /// <summary>
    /// SceneTree 级别每帧信号——不管 Root 重建多少次都会触发！
    /// </summary>
    private static void OnTreeProcessed()
    {
        // 确保 KeyWatcher 存在（场景切换可能丢）
        EnsureKeyWatcherAlive_Quiet();

        try { RefreshLanguageIfNeeded(); }
        catch (Exception ex) { LogError("OnTreeProcessed lang: " + ex.Message); }

        // 主菜单入口必须挂 SceneTree 信号：KeyWatcher 偶尔在菜单场景里不跑。
        try { PollCoopMenuEntry(); }
        catch (Exception ex) { LogError("OnTreeProcessed PollCoopMenuEntry: " + ex.Message); }

        // AI 双人档核心逻辑。
        // 多个驱动点是故意保留的：实测 CoopLoopNode._Process 在某些场景里不生效，
        // 只留一个会让整个循环停摆。重复调用的开销由 PollCoopAI 内部的 40ms 节流兜住，
        // 所以三个入口加起来仍然只有 ~25Hz。
        if (_aiMode)
        {
            try { PollCoopAI(); }
            catch (Exception ex) { LogError("OnTreeProcessed PollCoopAI: " + ex); }
        }
    }

    private static void EnsureKeyWatcherAlive_Quiet()
    {
        try
        {
            if (_kwInstance == null)
            {
                _kwInstance = new KeyWatcher();
                _kwInstance.Name = "Sts2CompanionKeyWatcher";
            }
            if (!_kwInstance.IsInsideTree())
            {
                var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                if (tree?.Root != null)
                {
                    tree.Root.AddChild(_kwInstance);
                }
            }
        }
        catch { }
    }

    private sealed partial class KeyWatcher : Godot.Node
    {
        public KeyWatcher()
        {
            Name = "Sts2CompanionKeyWatcher";
        }

        public override void _Process(double delta)
        {
            // 心跳降频到 30 秒：它只用于确认节点没被场景切换踢掉，
            // 每秒一条纯属给主线程和 mod_error.log 增加无意义 IO。
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - _kwHeartbeatMs > 30000)
            {
                _kwHeartbeatMs = now;
                LogError($"kw: _Process 心跳, _aiMode={_aiMode}, inTree={IsInsideTree()}");
            }

            if (!IsInsideTree()) return;

            PollHotkey();
            PollMenuButton();
            PollCoopMenuEntry();
            PollCoopCharacterFlow();
            EnsureCharacterOptions();

            if (_aiMode) PollCoopAI();
        }
    }

    /// <summary>
    /// 保证场景树里只有一个 CoopLoopNode。重复 AddChild 的节点即使逻辑被节流，
    /// 每帧回调仍会叠加，长时间反复开局会越来越卡。
    /// </summary>
    private static void EnsureCoopLoopNode()
    {
        try
        {
            if (_coopLoopNode != null
                && Godot.GodotObject.IsInstanceValid(_coopLoopNode)
                && _coopLoopNode.IsInsideTree())
            {
                return;
            }

            var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
            if (tree?.Root == null) return;

            _coopLoopNode = new CoopLoopNode();
            tree.Root.AddChild(_coopLoopNode);
            LogError($"ui: CoopLoopNode added, parent={_coopLoopNode.GetParent()?.GetType().Name ?? "null"}, "
                     + $"isInsideTree={_coopLoopNode.IsInsideTree()}");
        }
        catch (Exception ex)
        {
            LogError("EnsureCoopLoopNode: " + ex.Message);
        }
    }

    /// <summary>
    /// AI 双人档独立循环节点——每帧 Godot 主线程里同步跑 PollCoopAI。
    /// 不依赖 KeyWatcher，不受其他逻辑崩溃影响。
    /// </summary>
    private sealed class CoopLoopNode : Godot.Node
    {
        private long _lastBeatMs;

        public override void _Process(double delta)
        {
            if (!_aiMode) return;

            // 心跳只用于确认节点还在驱动；30 秒一条足够，避免每帧/每秒写日志。
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (nowMs - _lastBeatMs >= 30000)
            {
                _lastBeatMs = nowMs;
                LogError("coop loop 心跳：PollCoopAI 仍在被驱动");
            }

            try
            {
                PollCoopAI();
            }
            catch (Exception ex)
            {
                LogError("coop loop PollCoopAI exception: " + ex);
            }
        }
    }

    // ---------- 主菜单按钮 + 双人开局 ----------
    // 注：主菜单 AddChild 已证明不可行（NMainMenuScroller + 线程模型问题），
    // 改为面板内「AI 双人档」按钮触发 OnCoopPressed。PollMenuButton 暂时留空。

    // ---------- AI 来源配置 ----------

    private static bool CoopUseApiBrain() => _useApiBrain;
    private static string CoopApiBaseUrl() => _apiBaseUrl;
    private static string CoopApiKey() => _apiKey;
    private static string CoopApiModel() => _apiModel;

    private static string Tr(string zh, string en) => _resolvedLang == "zh" ? zh : en;

    private static string GameLanguage()
    {
        try
        {
            string lang = Safe(() => LocManager.Instance.Language, "");
            if (lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh";
            if (lang.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "en";
        }
        catch { }

        string culture = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return culture == "zh" ? "zh" : "en";
    }

    private static string PreferredLanguage()
    {
        return _langMode switch
        {
            1 => "zh",
            2 => "en",
            _ => GameLanguage(),
        };
    }

    private static void RefreshLanguageIfNeeded()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now - _langCheckMs < 1000) return;
        _langCheckMs = now;

        string want = PreferredLanguage();
        if (want == _resolvedLang) return;
        _resolvedLang = want;
        ApplyLanguageToUi();
        LogError($"lang: 切换为 {_resolvedLang}（mode={_langMode}）");
    }

    private static void LoadAiConfig()
    {
        try
        {
            _loadingAiCfg = true;
            if (_aiCfgPath.Length == 0 || !File.Exists(_aiCfgPath)) return;

            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(_aiCfgPath));
            if (dict == null) return;

            if (dict.TryGetValue("mode", out string? mode))
            {
                _useApiBrain = string.Equals(mode, "api", StringComparison.OrdinalIgnoreCase);
            }
            if (dict.TryGetValue("baseUrl", out string? baseUrl) && baseUrl.Length > 0)
            {
                _apiBaseUrl = baseUrl;
            }
            if (dict.TryGetValue("model", out string? model) && model.Length > 0)
            {
                _apiModel = model;
            }
            if (dict.TryGetValue("apiKey", out string? key))
            {
                _apiKey = key;
            }
            if (dict.TryGetValue("chatPersona", out string? persona))
            {
                _chatPersona = persona;
            }
            if (dict.TryGetValue("chatMemory", out string? memory))
            {
                _chatMemory = memory;
            }
            if (dict.TryGetValue("langMode", out string? langText)
                && int.TryParse(langText, out int langMode))
            {
                _langMode = Math.Clamp(langMode, 0, 2);
            }
            _resolvedLang = PreferredLanguage();

            LogError($"ai: 配置加载 mode={(_useApiBrain ? "api" : "local")} "
                     + $"base={_apiBaseUrl} model={_apiModel} key={(_apiKey.Length > 0 ? "已设置" : "空")} "
                     + $"lang={_resolvedLang} langMode={_langMode}");
        }
        catch (Exception ex)
        {
            LogError("ai: 配置加载失败: " + ex.Message);
        }
        finally
        {
            _resolvedLang = PreferredLanguage();
            _loadingAiCfg = false;
        }
    }

    private static void SaveAiConfig()
    {
        try
        {
            if (_aiCfgPath.Length == 0) return;

            var dict = new Dictionary<string, string>
            {
                ["mode"] = _useApiBrain ? "api" : "local",
                ["baseUrl"] = _apiBaseUrl,
                ["apiKey"] = _apiKey,
                ["model"] = _apiModel,
                ["chatPersona"] = _chatPersona,
                ["chatMemory"] = _chatMemory,
                ["langMode"] = _langMode.ToString(),
            };
            string? dir = Path.GetDirectoryName(_aiCfgPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_aiCfgPath, JsonSerializer.Serialize(dict, JsonOpts));
            LogError($"ai: 配置已保存 mode={(_useApiBrain ? "api" : "local")} "
                     + $"base={_apiBaseUrl} model={_apiModel} key={(_apiKey.Length > 0 ? "已设置" : "空")} "
                     + $"lang={_resolvedLang} langMode={_langMode}");
        }
        catch (Exception ex)
        {
            LogError("ai: 配置保存失败: " + ex.Message);
        }
    }

    private static void ApplyAiConfigToUi()
    {
        _loadingAiCfg = true;
        try
        {
            if (_aiModeOption != null)
            {
                _aiModeOption.Selected = _useApiBrain ? 1 : 0;
            }
            if (_apiBaseEdit != null) _apiBaseEdit.Text = _apiBaseUrl;
            if (_apiKeyEdit != null) _apiKeyEdit.Text = _apiKey;
            if (_apiModelEdit != null) _apiModelEdit.Text = _apiModel;
            if (_chatPersonaEdit != null) _chatPersonaEdit.Text = _chatPersona;
            if (_chatMemoryEdit != null) _chatMemoryEdit.Text = _chatMemory;
            if (_langOption != null) _langOption.Selected = _langMode;
            UpdateApiBoxVisibility();
            ApplyLanguageToUi();
        }
        finally
        {
            _loadingAiCfg = false;
        }
    }

    private static void UpdateApiBoxVisibility()
    {
        if (_apiBox != null) _apiBox.Visible = _useApiBrain;
    }

    private static void OpenAiSettingsPanel()
    {
        _panelForce = true;
        if (_panel != null) _panel.Visible = true;
        LogError("ai: 打开设置面板");
    }

    private static void ApplyLanguageToUi()
    {
        try
        {
            if (_head != null) _head.Text = Tr("STS2 队友", "STS2 Companion");
            if (_aiSourceLabel != null) _aiSourceLabel.Text = Tr("AI 来源", "AI source");
            if (_aiModeOption != null)
            {
                _aiModeOption.SetItemText(0, Tr("本地规则", "Local rules"));
                _aiModeOption.SetItemText(1, Tr("API / 本地模型", "API / local model"));
                _aiModeOption.TooltipText = Tr(
                    "本地规则不联网；选择 API/本地模型后，战斗出牌和对话框都会走这个 OpenAI 兼容接口。"
                    + "本地模型可填 http://127.0.0.1:11434/v1 这类地址。",
                    "Local rules stay offline. API/local-model mode uses the OpenAI-compatible endpoint "
                    + "for combat cards and chat. For Ollama, use e.g. http://127.0.0.1:11434/v1.");
            }
            if (_apiBaseEdit != null)
            {
                _apiBaseEdit.PlaceholderText = Tr(
                    "Base URL，例如 https://api.openai.com/v1",
                    "Base URL, e.g. https://api.openai.com/v1");
            }
            if (_apiKeyEdit != null)
            {
                _apiKeyEdit.PlaceholderText = Tr(
                    "API Key（只保存在本机 state 目录，可留空）",
                    "API key (stored locally, may be empty)");
            }
            if (_apiModelEdit != null)
            {
                _apiModelEdit.PlaceholderText = Tr(
                    "模型，例如 gpt-4o-mini / deepseek-chat / qwen2.5",
                    "Model, e.g. gpt-4o-mini / deepseek-chat / qwen2.5");
            }
            if (_chatPersonaEdit != null)
            {
                _chatPersonaEdit.PlaceholderText = Tr(
                    "调教 / 角色设定（可选）",
                    "Persona / role instructions (optional)");
            }
            if (_chatMemoryEdit != null)
            {
                _chatMemoryEdit.PlaceholderText = Tr(
                    "长期记忆（可选，会发给模型）",
                    "Long-term memory (optional, sent to model)");
            }
            if (_apiSaveButton != null) _apiSaveButton.Text = Tr("保存模型设置", "Save model settings");
            if (_panelCloseButton != null) _panelCloseButton.Text = Tr("关闭面板", "Close panel");
            if (_input != null)
            {
                _input.PlaceholderText = Tr(
                    "按 T 输入，Enter 发送，Esc 退出",
                    "Press T to type, Enter to send, Esc to close");
            }
            if (_langLabel != null) _langLabel.Text = Tr("语言", "Language");
            if (_langOption != null)
            {
                _langOption.SetItemText(0, Tr("跟随游戏", "Follow game"));
                _langOption.SetItemText(1, "中文");
                _langOption.SetItemText(2, "English");
                _langOption.TooltipText = Tr(
                    "跟随游戏语言；也可以手动锁定中文或 English",
                    "Follow the game language, or lock Chinese/English manually.");
            }
            if (_charFlowAiButton != null) _charFlowAiButton.Text = Tr("AI 设置", "AI settings");
            if (_charFlowActive) UpdateCharFlowUi();
            if (_coopMenuButton is NMainMenuTextButton menuText)
            {
                var label = menuText.label ?? FindNode<Godot.Label>(menuText);
                if (label != null) label.Text = Tr("和 AI 玩", "Play with AI");
            }
            _uiState = ""; // 让 RefreshUi 重新生成状态摘要
        }
        catch (Exception ex)
        {
            LogError("lang: apply: " + ex.Message);
        }
    }

    private static void ReadApiFieldsFromUi()
    {
        if (_apiBaseEdit != null && _apiBaseEdit.Text.Trim().Length > 0)
        {
            _apiBaseUrl = _apiBaseEdit.Text.Trim();
        }
        if (_apiModelEdit != null && _apiModelEdit.Text.Trim().Length > 0)
        {
            _apiModel = _apiModelEdit.Text.Trim();
        }
        if (_apiKeyEdit != null)
        {
            _apiKey = _apiKeyEdit.Text.Trim();
        }
        if (_chatPersonaEdit != null)
        {
            _chatPersona = _chatPersonaEdit.Text.Trim();
        }
        if (_chatMemoryEdit != null)
        {
            _chatMemory = _chatMemoryEdit.Text.Trim();
        }
    }

    // ---------- 主菜单「单人 + AI」入口 / 两段式选人 ----------

    /// <summary>
    /// RitsuLib 的 NMainMenuScroller 用私有 _items/_buttons 做滚动布局；
    /// 直接 AddChild 不会进列。这里反射触发它自己的收集和重排。
    /// </summary>
    private static void ReSyncMenuScroller(Godot.Node parent)
    {
        try
        {
            var type = parent.GetType();
            var sync = type.GetMethod("SynchronizeItems",
                BindingFlags.Instance | BindingFlags.NonPublic);
            sync?.Invoke(parent, null);

            var request = type.GetMethod("RequestLayout",
                BindingFlags.Instance | BindingFlags.NonPublic);
            request?.Invoke(parent, null);

            var refresh = type.GetMethod("RefreshLayout",
                BindingFlags.Instance | BindingFlags.NonPublic);
            refresh?.Invoke(parent, null);

            var init = type.GetProperty("Initialized",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var buttons = type.GetField("_buttons",
                BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(parent) as System.Collections.ICollection;
            var items = type.GetField("_items",
                BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(parent) as System.Collections.ICollection;
            LogError($"menu: 已触发 scroller 重排 init={init?.GetValue(parent)} "
                     + $"buttons={buttons?.Count} items={items?.Count}");
            LogMenuLayout(parent);
        }
        catch (Exception ex)
        {
            LogError("menu: scroller 重排失败: " + ex.Message);
        }
    }

    private static void LogMenuLayout(Godot.Node parent)
    {
        try
        {
            foreach (Godot.Node child in parent.GetChildren())
            {
                if (child is NMainMenuTextButton b)
                {
                    string label = b.label?.Text ?? "?";
                    LogError($"menu:   {b.Name} pos={b.Position} size={b.Size} vis={b.Visible} label={label}");
                }
            }
        }
        catch (Exception ex)
        {
            LogError("menu: dump layout 失败: " + ex.Message);
        }
    }

    private static void PollCoopMenuEntry()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now - _menuEntryLastMs < 500) return;
        _menuEntryLastMs = now;

        try
        {
            var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
            if (tree?.Root == null) return;

            var menu = FindNode<NMainMenu>(tree.Root);
            if (menu == null)
            {
                if (now - _menuMissLastMs > 5000)
                {
                    _menuMissLastMs = now;
                    LogError("menu: 当前不在主菜单（没找到 NMainMenu）");
                }
                return;
            }

            if (_coopMenuButton != null && Godot.GodotObject.IsInstanceValid(_coopMenuButton))
            {
                _coopMenuButton.Visible = true;
                if (_coopMenuButton is NMainMenuTextButton text)
                {
                    var label = text.label ?? FindNode<Godot.Label>(text);
                    if (label != null) label.Text = Tr("和 AI 玩", "Play with AI");
                }
                if (now - _menuButtonDiagMs > 3000)
                {
                    _menuButtonDiagMs = now;
                    var parentNow = _coopMenuButton.GetParent();
                    LogError($"menu: 按钮状态 vis={_coopMenuButton.Visible} "
                             + $"pos={_coopMenuButton.Position} size={_coopMenuButton.Size} "
                             + $"parent={parentNow?.GetType().Name} children={parentNow?.GetChildCount()}");
                }
                return;
            }

            _miSingleplayerMenuButton ??= typeof(NMainMenu).GetField(
                "_singleplayerButton",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var single = _miSingleplayerMenuButton?.GetValue(menu) as Godot.Control;
            if (single == null)
            {
                LogError("menu: 找不到 _singleplayerButton");
                return;
            }

            LogError($"menu: 找到 single parent={single.GetParent()?.GetType().Name} "
                     + $"path={single.GetPath()} pos={single.Position} size={single.Size}");

            var parent = single.GetParent();
            if (parent == null)
            {
                LogError("menu: 单人按钮没有父节点");
                return;
            }

            // 优先复制原版「单人游戏」按钮：样式、字号、间距、父容器全部一致，
            // 这样无论 MainMenuTextButtons 是 VBox 还是绝对布局都能落在同一列。
            if (single is NMainMenuTextButton textButton)
            {
                try
                {
                    var clone = (NMainMenuTextButton)textButton.Duplicate();
                    clone.Name = "CoopAiMenuButton";
                    parent.AddChild(clone);
                    // 放在「单人模式」上面：当前窗口高度下，下面会滚出可视区。
                    try { parent.MoveChild(clone, single.GetIndex()); } catch { }

                    var cloneLabel = clone.label ?? FindNode<Godot.Label>(clone);
                    if (cloneLabel != null) cloneLabel.Text = Tr("和 AI 玩", "Play with AI");
                    clone.Released += _ => OpenCoopCharacterSelect();

                    // RitsuLib 的 NMainMenuScroller 不会自动把新子节点纳入滚动列：
                    // 必须手动触发它的 SynchronizeItems + RequestLayout。
                    ReSyncMenuScroller(parent);

                    _coopMenuButton = clone;
                    LogError($"menu: 已克隆原版按钮插入「和 AI 玩」 parent={parent.GetType().Name} "
                             + $"full={parent.GetType().FullName} base={parent.GetType().BaseType?.Name} "
                             + $"children={parent.GetChildCount()} index={clone.GetIndex()}");
                    return;
                }
                catch (Exception ex)
                {
                    LogError("menu: 克隆按钮失败，改用普通按钮: " + ex.Message);
                }
            }

            // 兜底：普通按钮
            var btn = new Godot.Button
            {
                Name = "CoopAiMenuButton",
                Text = Tr("和 AI 玩", "Play with AI"),
                Flat = true,
                Alignment = Godot.HorizontalAlignment.Center,
                MouseFilter = Godot.Control.MouseFilterEnum.Stop,
                CustomMinimumSize = new Godot.Vector2(0, 48),
                SizeFlagsHorizontal = Godot.Control.SizeFlags.ExpandFill,
                ZIndex = 20,
            };
            btn.AddThemeFontSizeOverride("font_size", 30);
            btn.AddThemeColorOverride("font_color", new Godot.Color(0.92f, 0.80f, 0.45f));
            btn.AddThemeColorOverride("font_hover_color", new Godot.Color(1f, 0.93f, 0.65f));
            btn.AddThemeColorOverride("font_pressed_color", new Godot.Color(0.85f, 0.68f, 0.30f));
            btn.Pressed += OpenCoopCharacterSelect;

            parent.AddChild(btn);
            try { parent.MoveChild(btn, single.GetIndex()); } catch { }

            if (parent is not Godot.Container)
            {
                // 绝对定位的老布局：直接贴到单人按钮下方
                btn.Position = new Godot.Vector2(single.Position.X, single.Position.Y + single.Size.Y + 8f);
                btn.Size = single.Size;
            }

            _coopMenuButton = btn;
            ReSyncMenuScroller(parent);
            LogError($"menu: 已插入普通「和 AI 玩」 parent={parent.GetType().Name}");
        }
        catch (Exception ex)
        {
            LogError("menu: 插入失败: " + ex.Message);
        }
    }

    private static void OpenCoopCharacterSelect()
    {
        try
        {
            if (RunManager.Instance.IsInProgress)
            {
                LogError("chars: 当前局还没结束，先退出再选");
                return;
            }
            if (_charFlowActive)
            {
                bool stillOpen = false;
                try
                {
                    var oldScreen = _charFlowScreen;
                    var oldTree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                    var oldStack = oldTree?.Root == null
                        ? null
                        : FindNode<NMainMenuSubmenuStack>(oldTree.Root);
                    stillOpen = oldScreen != null
                                && Godot.GodotObject.IsInstanceValid(oldScreen)
                                && oldScreen.IsInsideTree()
                                && (oldStack == null || ReferenceEquals(oldStack.Peek(), oldScreen));
                }
                catch { }

                if (stillOpen)
                {
                    return;
                }

                LogError("chars: 检测到选人界面已退出，清理旧状态后重开");
                ResetCharFlowFields();
            }

            var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
            if (tree?.Root == null) return;

            var stack = FindNode<NMainMenuSubmenuStack>(tree.Root);
            if (stack == null)
            {
                LogError("chars: 找不到 NMainMenuSubmenuStack");
                return;
            }

            // 必须按原版 NSingleplayerSubmenu.OpenCharacterSelect 的顺序：
            // 先 GetSubmenuType + InitializeSingleplayer，再 Push；
            // 直接 PushSubmenuType 会在未初始化时触发 OnSubmenuOpened，
            // 某些 UI 插件（RitsuLib 等）会在 RefreshPlayerIcons 里空引用。
            var screen = stack.GetSubmenuType<NCharacterSelectScreen>();
            if (screen == null)
            {
                LogError("chars: GetSubmenuType 返回 null");
                return;
            }

            screen.InitializeSingleplayer();
            stack.Push(screen);

            _charFlowScreen = screen;
            _charFlowStep = 0;
            _charFlowLocal = null;
            _partySize = 3;
            for (int i = 0; i < _flowChars.Length; i++) _flowChars[i] = null;
            _charFlowActive = true;
            _charFlowOpenMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // 隐藏原版「启程」按钮，换成我们自己的两段式按钮。
            _miCharacterEmbarkButton ??= typeof(NCharacterSelectScreen).GetField(
                "_embarkButton",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (_miCharacterEmbarkButton?.GetValue(screen) is Godot.Control embark)
            {
                embark.Visible = false;
                if (embark is Godot.BaseButton baseButton) baseButton.Disabled = true;
            }

            var hint = screen.GetNodeOrNull<Godot.Label>("CoopFlowHint");
            if (hint == null)
            {
                hint = new Godot.Label
                {
                    Name = "CoopFlowHint",
                    Text = Tr("第一步：选你玩的角色", "Step 1: choose your character"),
                    HorizontalAlignment = Godot.HorizontalAlignment.Center,
                    MouseFilter = Godot.Control.MouseFilterEnum.Ignore,
                    ZIndex = 100,
                };
                hint.AnchorLeft = 0f;
                hint.AnchorRight = 1f;
                hint.AnchorTop = 0f;
                hint.AnchorBottom = 0f;
                hint.OffsetTop = 26f;
                hint.OffsetBottom = 66f;
                hint.AddThemeFontSizeOverride("font_size", 24);
                hint.AddThemeColorOverride("font_color", new Godot.Color(0.95f, 0.80f, 0.40f));
                screen.AddChild(hint);
            }
            hint.Visible = true;
            _charFlowHint = hint;

            var countRow = screen.GetNodeOrNull<Godot.HBoxContainer>("CoopFlowCountRow");
            if (countRow == null)
            {
                countRow = new Godot.HBoxContainer
                {
                    Name = "CoopFlowCountRow",
                    MouseFilter = Godot.Control.MouseFilterEnum.Stop,
                    ZIndex = 100,
                };
                countRow.AnchorLeft = 0.5f;
                countRow.AnchorRight = 0.5f;
                countRow.AnchorTop = 0f;
                countRow.AnchorBottom = 0f;
                countRow.OffsetLeft = -180f;
                countRow.OffsetRight = 180f;
                countRow.OffsetTop = 74f;
                countRow.OffsetBottom = 112f;
                countRow.AddThemeConstantOverride("separation", 8);
                screen.AddChild(countRow);
            }

            _charFlowMinus = countRow.GetNodeOrNull<Godot.Button>("CoopFlowMinus");
            if (_charFlowMinus == null)
            {
                _charFlowMinus = new Godot.Button
                {
                    Name = "CoopFlowMinus",
                    Text = "－",
                    CustomMinimumSize = new Godot.Vector2(44, 40),
                    MouseFilter = Godot.Control.MouseFilterEnum.Stop,
                };
                _charFlowMinus.AddThemeFontSizeOverride("font_size", 20);
                _charFlowMinus.Pressed += OnPartyMinus;
                countRow.AddChild(_charFlowMinus);
            }

            _charFlowCount = countRow.GetNodeOrNull<Godot.Label>("CoopFlowCount");
            if (_charFlowCount == null)
            {
                _charFlowCount = new Godot.Label
                {
                    Name = "CoopFlowCount",
                    HorizontalAlignment = Godot.HorizontalAlignment.Center,
                    VerticalAlignment = Godot.VerticalAlignment.Center,
                    SizeFlagsHorizontal = Godot.Control.SizeFlags.ExpandFill,
                    MouseFilter = Godot.Control.MouseFilterEnum.Ignore,
                };
                _charFlowCount.AddThemeFontSizeOverride("font_size", 16);
                _charFlowCount.AddThemeColorOverride("font_color", new Godot.Color(0.90f, 0.86f, 0.72f));
                countRow.AddChild(_charFlowCount);
            }

            _charFlowPlus = countRow.GetNodeOrNull<Godot.Button>("CoopFlowPlus");
            if (_charFlowPlus == null)
            {
                _charFlowPlus = new Godot.Button
                {
                    Name = "CoopFlowPlus",
                    Text = "＋",
                    CustomMinimumSize = new Godot.Vector2(44, 40),
                    MouseFilter = Godot.Control.MouseFilterEnum.Stop,
                };
                _charFlowPlus.AddThemeFontSizeOverride("font_size", 20);
                _charFlowPlus.Pressed += OnPartyPlus;
                countRow.AddChild(_charFlowPlus);
            }

            countRow.Visible = true;
            UpdateCharFlowUi();

            var next = screen.GetNodeOrNull<Godot.Button>("CoopFlowNext");
            if (next == null)
            {
                next = new Godot.Button
                {
                    Name = "CoopFlowNext",
                    Text = Tr("下一步：选队友（点这里）", "Next: choose teammate (click here)"),
                    MouseFilter = Godot.Control.MouseFilterEnum.Stop,
                    ZIndex = 100,
                };
                next.AnchorLeft = 0.5f;
                next.AnchorRight = 0.5f;
                next.AnchorTop = 1f;
                next.AnchorBottom = 1f;
                next.OffsetLeft = -200f;
                next.OffsetRight = 200f;
                next.OffsetTop = -110f;
                next.OffsetBottom = -58f;
                next.AddThemeFontSizeOverride("font_size", 22);
                next.Pressed += OnCharFlowNext;
                screen.AddChild(next);
            }
            next.Text = Tr("下一步：选队友（点这里）", "Next: choose teammate (click here)");
            next.Visible = true;
            _charFlowButton = next;
            UpdateCharFlowUi();

            var aiSettings = screen.GetNodeOrNull<Godot.Button>("CoopAiSettings");
            if (aiSettings == null)
            {
                aiSettings = new Godot.Button
                {
                    Name = "CoopAiSettings",
                    Text = Tr("AI 设置", "AI settings"),
                    MouseFilter = Godot.Control.MouseFilterEnum.Stop,
                    ZIndex = 100,
                };
                aiSettings.AnchorLeft = 1f;
                aiSettings.AnchorRight = 1f;
                aiSettings.AnchorTop = 0f;
                aiSettings.AnchorBottom = 0f;
                aiSettings.OffsetLeft = -132f;
                aiSettings.OffsetRight = -24f;
                aiSettings.OffsetTop = 26f;
                aiSettings.OffsetBottom = 62f;
                aiSettings.AddThemeFontSizeOverride("font_size", 16);
                aiSettings.Pressed += OpenAiSettingsPanel;
                screen.AddChild(aiSettings);
            }
            aiSettings.Visible = true;
            _charFlowAiButton = aiSettings;

            LogError("chars: 打开两段式选人（第一步=本地）");
        }
        catch (Exception ex)
        {
            LogError("chars: 打开选人崩: " + ex);
            _charFlowActive = false;
        }
    }

    private static void PollCoopCharacterFlow()
    {
        if (!_charFlowActive) return;

        var screen = _charFlowScreen;
        bool grace = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _charFlowOpenMs < 1500;
        if (screen != null && Godot.GodotObject.IsInstanceValid(screen)
            && _miCharacterEmbarkButton?.GetValue(screen) is Godot.Control embark
            && embark.Visible)
        {
            embark.Visible = false;
            if (embark is Godot.BaseButton baseButton) baseButton.Disabled = true;
        }

        bool isTop = true;
        try
        {
            var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
            var stack = tree?.Root == null ? null : FindNode<NMainMenuSubmenuStack>(tree.Root);
            if (stack != null) isTop = ReferenceEquals(stack.Peek(), screen);
        }
        catch { }

        if (screen == null || !Godot.GodotObject.IsInstanceValid(screen)
            || (!grace && (!screen.IsInsideTree() || !isTop)))
        {
            ResetCharFlowFields();
        }
    }

    private static void ResetCharFlowFields()
    {
        _charFlowActive = false;
        _charFlowScreen = null;
        _charFlowButton = null;
        _charFlowHint = null;
        _charFlowAiButton = null;
        _charFlowMinus = null;
        _charFlowPlus = null;
        _charFlowCount = null;
        _charFlowLocal = null;
        _charFlowStep = 0;
    }

    private static CharacterModel? ReadCharacterScreenSelection()
    {
        var screen = _charFlowScreen;
        if (screen == null || !Godot.GodotObject.IsInstanceValid(screen)) return null;

        _miCharacterSelectedButton ??= typeof(NCharacterSelectScreen).GetField(
            "_selectedButton",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var btn = _miCharacterSelectedButton?.GetValue(screen) as NCharacterSelectButton;
        return btn?.Character;
    }

    private static void UpdateCharFlowUi()
    {
        _charFlowStep = Math.Clamp(_charFlowStep, 0, _partySize - 1);
        if (_charFlowHint != null)
        {
            _charFlowHint.Text = _charFlowStep == 0
                ? Tr("第一步：选你自己", "Step 1: choose yourself")
                : Tr($"第{_charFlowStep + 1}步：选队友 {_charFlowStep}",
                     $"Step {_charFlowStep + 1}: choose teammate {_charFlowStep}");
        }
        if (_charFlowButton != null)
        {
            _charFlowButton.Text = _charFlowStep < _partySize - 1
                ? Tr($"下一步：选队友 {_charFlowStep + 1}",
                     $"Next: choose teammate {_charFlowStep + 1}")
                : Tr($"开始 {_partySize} 人（点这里）",
                     $"Start {_partySize} players (click here)");
        }
        if (_charFlowCount != null)
        {
            _charFlowCount.Text = Tr(
                $"队友 {_partySize - 1} 人 / 总人数 {_partySize}",
                $"{_partySize - 1} teammate(s) / {_partySize} total");
        }
        if (_charFlowMinus != null) _charFlowMinus.Visible = _partySize > 2;
        if (_charFlowPlus != null) _charFlowPlus.Visible = _partySize < 4;
    }

    private static void OnPartyPlus()
    {
        if (_partySize >= 4) return;
        _partySize++;
        UpdateCharFlowUi();
        LogError($"chars: 队友数量 → {_partySize - 1}（总人数 {_partySize}）");
    }

    private static void OnPartyMinus()
    {
        if (_partySize <= 2) return;
        _partySize--;
        for (int i = _partySize; i < 4; i++) _flowChars[i] = null;
        UpdateCharFlowUi();
        LogError($"chars: 队友数量 → {_partySize - 1}（总人数 {_partySize}）");
    }

    private static void OnCharFlowNext()
    {
        var selected = ReadCharacterScreenSelection();
        if (selected == null)
        {
            if (_charFlowHint != null)
            {
                _charFlowHint.Text = Tr("先点一个角色，再点下一步",
                                        "Pick a character first, then click Next");
            }
            return;
        }

        _flowChars[_charFlowStep] = selected;
        LogError($"chars: 第{_charFlowStep + 1}步 角色{_charFlowStep}="
                 + $"{Safe(() => selected.Id.Entry, "?")}");

        if (_charFlowStep < _partySize - 1)
        {
            _charFlowStep++;
            UpdateCharFlowUi();
            return;
        }

        var party = new List<CharacterModel>();
        for (int i = 0; i < _partySize; i++)
        {
            CharacterModel? c = _flowChars[i];
            if (c == null)
            {
                _charFlowStep = i;
                UpdateCharFlowUi();
                if (_charFlowHint != null)
                {
                    _charFlowHint.Text = Tr($"还差角色 {i}，请先选一个",
                                            $"Character {i} is still missing; pick one");
                }
                return;
            }
            party.Add(c);
        }

        _charFlowLocal = party[0];
        CloseCoopCharacterFlow();
        _ = StartCoopRun(null, null, party);
    }

    private static void CloseCoopCharacterFlow()
    {
        _charFlowActive = false;
        _charFlowScreen = null;
        _charFlowButton = null;
        _charFlowHint = null;
        _charFlowAiButton = null;
        _charFlowMinus = null;
        _charFlowPlus = null;
        _charFlowCount = null;
        _charFlowLocal = null;
        _charFlowStep = 0;

        try
        {
            var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
            if (tree?.Root == null) return;
            var stack = FindNode<NMainMenuSubmenuStack>(tree.Root);
            if (stack != null && stack.SubmenusOpen) stack.Pop();
        }
        catch (Exception ex)
        {
            LogError("chars: 关闭选人异常: " + ex.Message);
        }
    }

    private static void PollMenuButton()
    {
        // 已废弃：主线程 AddChild 到 NMainMenuScroller 也会被自动布局清理
        // 改成面板按钮方案，见 BuildUi 里的 coopBtn
    }

    // ---------- AI 双人档辅助：存档标记 + 自动前进 + 自动出牌 ----------

    private static void WriteCoopRunMarker(RunState runState)
    {
        try
        {
            string dir = Path.GetDirectoryName(_outFile!) ?? ".";
            string json = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["coop"] = true,
                ["startedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                ["players"] = runState.Players.Select(p => new
                {
                    netId = p.NetId,
                    character = p.Character.Id.Entry,
                    isAi = p.NetId != 1uL,
                }).ToList(),
            }, JsonOpts);
            string tmp = Path.Combine(dir, "coop_run.tmp");
            File.WriteAllText(tmp, json);
            File.Move(tmp, Path.Combine(dir, "coop_run.json"), overwrite: true);
        }
        catch (Exception ex)
        {
            LogError("coop marker: " + ex.Message);
        }
    }

    /// <summary>
    /// 每帧在 Godot 主线程里跑：
    ///   1. 选项房间 → 本地玩家自己选；AI 跟选；两人都选完才 Proceed（CoopAI.cs）
    ///   2. 地图房间 → 本地玩家先投票；AI 跟票；由游戏自己走旅行流程（CoopAI.cs）
    ///   3. 战斗场景 → AI 回合自动出牌 + EndTurn
    /// </summary>
    private static void PollCoopAI()
    {
        // PollCoopAI 的驱动点全是 Godot 回调（ProcessFrame 信号 / Node._Process），
        // 所以这里是确认"主线程是谁"的最可靠时机；Init 有可能在别的线程上跑。
        EnsureMainThreadCaptured();

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 节流到 ~25Hz：游戏主循环可能跑到 300fps，全速跑这套反射逻辑没有意义还会掉帧。
        // 多个驱动入口共用这一个时间戳，所以无论多少个调用者，总频率都封顶在 25Hz。
        if (nowMs - _lastCoopTickMs < 40) return;
        if (_lastCoopTickMs == 0) LogError("coop: PollCoopAI 已启动（25Hz 节流）");
        _lastCoopTickMs = nowMs;

        try
        {
            var runState = Safe(() => RunManager.Instance.DebugOnlyGetState(), (RunState?)null);

            // 心跳：每 2 秒一条（runState=null 也打，确认循环在跑）
            if (nowMs - _aiLastTickMs >= _aiTickGap.TotalMilliseconds)
            {
                _aiLastTickMs = nowMs;
                if (runState == null)
                {
                    LogError("coop tick: runState=null (RunManager 还没 Launch?)");
                }
            }

            if (runState == null)
            {
                if (_aiMode && Safe(() => !RunManager.Instance.IsInProgress, true))
                {
                    _aiMode = false;
                }
                return;
            }

            // 存档标记补写
            if (!File.Exists(Path.Combine(Path.GetDirectoryName(_outFile!) ?? ".", "coop_run.json")))
            {
                WriteCoopRunMarker(runState);
            }

            AbstractRoom? curRoom = Safe(() => runState.CurrentRoom, (AbstractRoom?)null);
            string roomType = curRoom == null ? "null" : curRoom.GetType().Name;

            // 每 3 秒 dump 诊断
            if (nowMs - _aiLastDiagMs >= _aiDiagGap.TotalMilliseconds)
            {
                _aiLastDiagMs = nowMs;
                var curCoord = Safe(() => runState.CurrentMapCoord, (MapCoord?)null);
                var rooms = Safe(() => runState.CurrentRoomCount, 0);
                var visited = Safe(() => runState.VisitedMapCoords.Count, 0);
                var curPt = Safe(() => runState.CurrentMapPoint?.PointType.ToString() ?? "null", "null");
                LogError($"coop diag: room={roomType}, coord={curCoord?.col},{curCoord?.row ?? -1}, rooms={rooms}, visited={visited}, curPt={curPt}");
            }

            // 地图跟票不依赖 CurrentRoom —— 地图界面上它经常是 null，
            // 所以必须放在下面那个 curRoom==null 的提前 return 之前。
            CoopResolveStaleCardChoice(runState);
            CoopMapTick(runState);
            CoopRewardTick(runState);
            CoopWatchdog(runState);
            CoopActChangeTick(runState);

            if (curRoom == null) return;

            // ==============================================================
            // 分支 1：选项房间 —— AI 跟选；本地玩家自己选；两人都选完才推进（见 CoopAI.cs）
            if (curRoom is RestSiteRoom restRoom) CoopRestSiteTick(runState, restRoom);
            if (curRoom is MerchantRoom merchantRoom) CoopShopTick(runState, merchantRoom);
            if (curRoom is TreasureRoom treasureRoom) CoopTreasureTick(runState, treasureRoom);
            if (IsChoiceRoom(roomType)) CoopEventTick(runState, curRoom);

            // 分支 2（地图跟票）已在上面 curRoom==null 之前调用，见 CoopMapTick

            // ==============================================================
            // 分支 3：战斗场景 → AI 出牌 + 结束回合（逻辑在 CoopCombat.cs）
            // ==============================================================
            CombatState? cs = Safe(() => CombatManager.Instance.DebugOnlyGetState(), (CombatState?)null);
            if (cs == null || !Safe(() => CombatManager.Instance.IsInProgress, false)) return;

            CoopCombatTick(cs);
        }
        catch (Exception ex)
        {
            LogError("PollCoopAI: " + ex);
        }
    }

    /// <summary>房间名是否包含任一"有选项"关键字。用循环代替 LINQ，避免 25Hz 下每帧分配闭包。</summary>
    private static bool IsChoiceRoom(string roomType)
    {
        foreach (string keyword in ChoiceRoomKeywords)
        {
            if (roomType.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static MapPoint? FindNextMapPoint(RunState runState)
    {
        try
        {
            var map = runState.Map;
            var cur = Safe(() => runState.CurrentMapCoord, (MapCoord?)null);
            if (cur == null) return Safe(() => map.StartingMapPoint, (MapPoint?)null);

            // 从当前坐标找下一层的可选点
            int nextFloor = cur.Value.row + 1;
            var nextLayer = map.GetPointsInRow(nextFloor);
            if (nextLayer == null) return null;

            // 过滤掉已访问过的，剩下随便选一个
            var visited = runState.VisitedMapCoords;
            foreach (var p in nextLayer)
            {
                if (!visited.Contains(p.coord)) return p;
            }
            return nextLayer.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string DumpTree(Godot.Node node, int depth, int maxDepth)
    {
        if (depth > maxDepth) return "";
        string indent = new string(' ', depth * 2);
        Godot.Control? ctrl = node as Godot.Control;
        string extra = ctrl == null ? ""
            : $" pos=({ctrl.Position.X:F0},{ctrl.Position.Y:F0}) size=({ctrl.Size.X:F0},{ctrl.Size.Y:F0})";
        string line = $"{indent}- {node.Name} <{node.GetType().Name}>{extra}\n";
        foreach (Godot.Node child in node.GetChildren())
        {
            line += DumpTree(child, depth + 1, maxDepth);
        }
        return line;
    }

    private static void FindButtonsRecursive(Godot.Node node, ref Godot.Control? single, ref Godot.Control? multi)
    {
        if (single != null && multi != null) return;
        Godot.Control? c = node as Godot.Control;
        if (c != null)
        {
            string cname = c.Name.ToString();
            if (single == null && (cname.Contains("Single") || cname.Contains("Player") || cname.Contains("Play"))) single = c;
            if (multi == null && cname.Contains("Multi")) multi = c;
            // 也看按钮下面的 Label 文字
            if (single == null || multi == null)
            {
                foreach (Godot.Node n in c.GetChildren())
                {
                    if (n is Godot.Label lbl)
                    {
                        string t = lbl.Text ?? "";
                        if (single == null && t.Contains("单人")) single = c;
                        if (multi == null && t.Contains("多人")) multi = c;
                    }
                }
            }
        }
        foreach (Godot.Node child in node.GetChildren())
        {
            FindButtonsRecursive(child, ref single, ref multi);
            if (single != null && multi != null) return;
        }
    }

    private static void CreateCoopButton(Godot.Control scroller)
    {
        var btn = new Godot.Button
        {
            Name = "CoopAiButton",
            Text = "单人+AI",
            Flat = true,
            Alignment = Godot.HorizontalAlignment.Center,
            ZIndex = 100,
            MouseFilter = Godot.Control.MouseFilterEnum.Stop,
        };
        btn.AddThemeFontSizeOverride("font_size", 28);
        btn.AddThemeColorOverride("font_color", new Godot.Color(0.92f, 0.80f, 0.45f));
        btn.AddThemeColorOverride("font_hover_color", new Godot.Color(1f, 0.93f, 0.65f));
        btn.AddThemeColorOverride("font_pressed_color", new Godot.Color(0.85f, 0.68f, 0.30f));
        btn.Pressed += OnCoopPressed;
        scroller.AddChild(btn);
        _coopButton = btn;
        MenuDiag("button inserted into " + scroller.GetType().Name + ", parent=" + (_coopButton.GetParent()?.GetType().Name ?? "null"));
    }

    private static void PositionCoopButton(Godot.Control scroller, Godot.Control? single, Godot.Control? multi)
    {
        if (_coopButton == null) return;
        Godot.Control? anchor = multi ?? single;
        if (anchor == null) return;

        // 锚点按钮正下方 2px 间隙
        Godot.Vector2 target = new Godot.Vector2(anchor.Position.X, anchor.Position.Y + anchor.Size.Y + 2f);

        if (Math.Abs(_coopButton.Position.X - target.X) > 0.5f ||
            Math.Abs(_coopButton.Position.Y - target.Y) > 0.5f)
        {
            _coopButton.Position = target;
        }
        _coopButton.Size = anchor.Size;
        _coopButton.Visible = true;
        _coopButton.ZIndex = 100;

        MenuDiag("coop live pos=" + _coopButton.Position + " parent=" + (_coopButton.GetParent()?.GetType().Name ?? "null"));
    }

    private static T? FindNode<T>(Godot.Node node) where T : Godot.Node
    {
        if (node is T match)
        {
            return match;
        }
        foreach (Godot.Node child in node.GetChildren())
        {
            T? found = FindNode<T>(child);
            if (found != null)
            {
                return found;
            }
        }
        return null;
    }

    private static void MenuDiag(string msg)
    {
        // 不去重、直接追加——我们需要看到完整调用轨迹。
        try
        {
            string dir = Path.GetDirectoryName(_outFile!) ?? ".";
            string path = Path.Combine(dir, "menu_diag.log");
            if (File.Exists(path) && new FileInfo(path).Length > 200_000)
            {
                File.Delete(path);
            }
            File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss ") + msg + "\n");
        }
        catch
        {
            // 诊断日志写不了就算了
        }
    }

    private static async void OnCoopPressed()
    {
        await StartCoopRun(null, null);
    }

    private static async Task StartCoopRun(
        CharacterModel? chosenLocal, CharacterModel? chosenAi,
        IReadOnlyList<CharacterModel>? chosenParty = null)
    {
        if (_starting)
        {
            return;
        }

        // 防重复点击：已经在跑 RunManager 就不允许再 SetUpTest
        if (RunManager.Instance.IsInProgress)
        {
            LogError("coop: RunManager 还在跑（已经在某场局里了），先退出当前 run 再点这个按钮");
            return;
        }

        _starting = true;
        _aiMode = true;
        _aiLastDiagMs = 0;
        try
        {
            LogError("coop step 1: 创建玩家/Acts");

            // 选人界面传入的完整队伍优先；没有传入时才回退到旧的两下拉框/默认角色。
            if (chosenParty == null && chosenLocal == null && chosenAi == null)
            {
                RefreshCharacterOptions();
            }

            var partyChars = new List<CharacterModel>();
            if (chosenParty != null && chosenParty.Count >= 2)
            {
                partyChars.AddRange(chosenParty.Take(4));
            }
            else
            {
                partyChars.Add(chosenLocal
                               ?? SelectedCharacter(_charLocalOption)
                               ?? ModelDb.Character<Ironclad>());
                partyChars.Add(chosenAi
                               ?? SelectedCharacter(_charAiOption)
                               ?? ModelDb.Character<Silent>());
            }

            UnlockState unlockMe = SaveManager.Instance.GenerateUnlockStateFromProgress();
            var players = new List<Player>();
            for (int i = 0; i < partyChars.Count; i++)
            {
                ulong netId = (ulong)(i + 1);
                CharacterModel wanted = partyChars[i];
                UnlockState unlock = SaveManager.Instance.GenerateUnlockStateFromProgress();
                try
                {
                    players.Add(Player.CreateForNewRun(wanted, unlock, netId));
                }
                catch (Exception ex)
                {
                    CharacterModel fallback = i == 0
                        ? ModelDb.Character<Ironclad>()
                        : ModelDb.Character<Silent>();
                    LogError($"coop: 角色 {Safe(() => wanted.Id.Entry, "?")} 创建失败，"
                             + $"回退 {Safe(() => fallback.Id.Entry, "?")}: {ex.Message}");
                    players.Add(Player.CreateForNewRun(fallback, unlock, netId));
                }
            }

            string seed = SeedHelper.GetRandomSeed();
            var rng = new Rng(StringHelper.GetDeterministicHashCode(seed), "act_selection");
            List<ActModel> acts = ActModel.GetRandomList(rng, unlockMe, true)
                                          .Select(a => a.ToMutable()).ToList();

            LogError($"coop: 队伍 {players.Count} 人 "
                     + string.Join(" ", players.Select(p => $"net{p.NetId}={Safe(() => p.Character.Id.Entry, "?")}")));

            LogError($"coop step 2: CreateForNewRun {acts.Count} acts");
            RunState runState = RunState.CreateForNewRun(
                players, acts, Array.Empty<ModifierModel>(),
                GameMode.Standard, 0, seed);

            // 关键：跳过 Neow 遗物选择（双人投票会把 AI 卡死）
            runState.ExtraFields.StartedWithNeow = false;

            LogError("coop step 3: SetUpNewSingleplayer（单人同步器，不卡双人投票）");
            RunManager.Instance.SetUpNewSingleplayer(runState, shouldSave: true);

            LogError("coop step 4: GenerateRooms");
            RunManager.Instance.GenerateRooms();

            // 不再废同步器！SetUpNewSingleplayer 创建的是单人同步器
            // PlayerChoiceSynchronizer 里 NetSingleplayerGameService.Type.IsMultiplayer()=false
            // → 投票逻辑只关心 netId=1，不会等 AI！
            LogError("coop step 5: WriteCoopRunMarker");
            WriteCoopRunMarker(runState);

            // 把 CoopLoopNode 挪这里加（BuildUi 时机太早可能还没 Root）。
            // EnsureCoopLoopNode 会复用已有节点，反复开局不会越堆越多。
            EnsureCoopLoopNode();

            // 复刻 NGame.StartRun 的开局流程
            LogError("coop step 6: NetLoadingHandle + PreloadManager");
            using (new NetLoadingHandle(RunManager.Instance.NetService))
            {
                LogError("coop step 6a: LoadRunAssets");
                await PreloadManager.LoadRunAssets(runState.Players.Select(p => p.Character));
                LogError("coop step 6b: LoadActAssets");
                await PreloadManager.LoadActAssets(runState.Acts[0]);
                LogError("coop step 6c: FinalizeStartingRelics");
                await RunManager.Instance.FinalizeStartingRelics();
                LogError("coop step 6d: Launch");
                RunManager.Instance.Launch();
                LogError("coop step 6e: SetCurrentScene NRun.Create");
                NGame.Instance!.RootSceneContainer.SetCurrentScene(NRun.Create(runState));

                // 场景切换后 KeyWatcher 可能被踢 → 重新 AddChild
                try
                {
                    var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                    if (tree?.Root != null)
                    {
                        // 先踢掉旧的
                        var old = tree.Root.GetNodeOrNull<KeyWatcher>("Sts2CompanionKeyWatcher");
                        if (old != null) old.QueueFree();

                        _kwInstance = new KeyWatcher();
                        tree.Root.AddChild(_kwInstance);
                        LogError($"coop step 6f: KeyWatcher re-added, inTree={_kwInstance.IsInsideTree()}");
                    }
                }
                catch (Exception ex)
                {
                    LogError($"coop step 6f: KeyWatcher re-add 异常: {ex.Message}");
                }

                LogError("coop step 7: EnterAct(0)");
                await RunManager.Instance.EnterAct(0, doTransition: false);
                EnsureKeyWatcherAlive();  // EnterAct 会切场景 → 再补一次

                // 直接绕过 MapRoom，强制进第一间房
                var startPoint = runState.Map.StartingMapPoint;
                int startFloor = startPoint.coord.row + 1;
                LogError($"coop step 8: EnterMapPointInternal floor={startFloor} pointType={startPoint.PointType}");
                await RunManager.Instance.EnterMapPointInternal(startFloor, startPoint.PointType, null, saveGame: false);
                EnsureKeyWatcherAlive();  // 进房又切场景 → 再补一次
                LogError("coop step 9: 全部完成 ✓");
            }
        }
        catch (Exception ex)
        {
            LogError("coop 崩溃: " + ex);
            _aiMode = false;
        }
        finally
        {
            _starting = false;
        }
    }

    private static void PollHotkey()
    {
        // F10 切视角：不依赖聊天输入框，所以要放在最前面（_input 为 null 时也要能按）
        bool f10Down = Godot.Input.IsKeyPressed(Godot.Key.F10);
        bool f10Pressed = f10Down && !_f10WasDown;
        _f10WasDown = f10Down;
        if (f10Pressed && _aiMode)
        {
            ToggleView();
        }

        if (_input == null || !Godot.GodotObject.IsInstanceValid(_input))
        {
            return;
        }

        bool tDown = Godot.Input.IsKeyPressed(Godot.Key.T);
        bool tPressed = tDown && !_tWasDown;
        _tWasDown = tDown;

        bool escDown = Godot.Input.IsKeyPressed(Godot.Key.Escape);
        bool escPressed = escDown && !_escWasDown;
        _escWasDown = escDown;

        if (tPressed && !_input.HasFocus())
        {
            _panelForce = true;
            if (_panel != null)
            {
                _panel.Visible = true;
            }
            _input.GrabFocus();
        }
        else if (escPressed && _input.HasFocus())
        {
            _input.Text = "";
            _input.ReleaseFocus();
            _panelForce = false;
        }
    }

    private static bool ChatModelConfigured()
    {
        return _useApiBrain
               && !string.IsNullOrWhiteSpace(_apiModel)
               && !string.IsNullOrWhiteSpace(_apiBaseUrl);
    }

    private static void ShowChatMessage(string text)
    {
        _chatDisplay = text;
        _chatDisplayUntilMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 120000;
        _uiText = "";
        LogError("chat: " + (text.Length > 160 ? text.Substring(0, 160) : text));
    }

    private static void OnChatSubmit(string text)
    {
        text = text.Trim();
        if (_input != null)
        {
            _input.Text = "";
        }
        if (text.Length == 0 || _chatBusy)
        {
            return;
        }

        ReadApiFieldsFromUi();
        SaveAiConfig();

        if (!ChatModelConfigured())
        {
            ShowChatMessage(Tr(
                "未接入模型，目前对话不可用。\n请在「AI 设置」里选择「API / 本地模型」，填写 Base URL 和模型名。",
                "No model is connected, so chat is unavailable.\n"
                + "Open AI Settings, choose API / local model, and fill in the Base URL and model."));
            return;
        }

        _chatBusy = true;
        _chatDisplay = Tr("你：", "You: ") + text + "\n\n" + Tr("正在请求模型…", "Contacting model…");
        _chatDisplayUntilMs = long.MaxValue;
        _uiText = "";
        _ = ChatWithModelAsync(text);
    }

    private static async Task ChatWithModelAsync(string userText)
    {
        try
        {
            string baseUrl = _apiBaseUrl.Trim();
            if (baseUrl.Length == 0) baseUrl = "https://api.openai.com/v1";
            string endpoint = baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
                ? baseUrl
                : baseUrl.TrimEnd('/') + "/chat/completions";

            var messages = new List<object>();
            var system = new StringBuilder();
            system.AppendLine("You are the AI teammate in Slay the Spire 2. "
                              + "Answer the player's chat naturally and concisely. "
                              + "You are a game companion, not an all-purpose assistant.");
            if (_chatPersona.Length > 0)
            {
                system.AppendLine("Persona / instructions from the player:");
                system.AppendLine(_chatPersona);
            }
            if (_chatMemory.Length > 0)
            {
                system.AppendLine("Long-term memory from the player (treat as true):");
                system.AppendLine(_chatMemory);
            }
            messages.Add(new { role = "system", content = system.ToString() });

            foreach (var item in _chatHistory)
            {
                if (item.TryGetValue("role", out string? role)
                    && item.TryGetValue("content", out string? content))
                {
                    messages.Add(new { role, content });
                }
            }
            messages.Add(new { role = "user", content = userText });

            var payload = new
            {
                model = _apiModel,
                messages,
                temperature = 0.7,
                max_tokens = 500
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            string key = _apiKey.Trim();
            if (key.Length > 0)
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }
            req.Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var resp = await CoopApiHttp.SendAsync(req);
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                string snippet = body.Length > 240 ? body.Substring(0, 240) : body;
                FinishChat(Tr($"模型请求失败：HTTP {(int)resp.StatusCode}\n{snippet}",
                              $"Model request failed: HTTP {(int)resp.StatusCode}\n{snippet}"));
                return;
            }

            string? reply = ExtractApiText(body);
            if (string.IsNullOrWhiteSpace(reply))
            {
                FinishChat(Tr("模型返回了空内容。", "The model returned an empty response."));
                return;
            }

            _chatHistory.Add(new Dictionary<string, string>
            {
                ["role"] = "user", ["content"] = userText
            });
            _chatHistory.Add(new Dictionary<string, string>
            {
                ["role"] = "assistant", ["content"] = reply
            });
            while (_chatHistory.Count > 10) _chatHistory.RemoveAt(0);

            FinishChat(Tr("你：", "You: ") + userText + "\n\n"
                       + Tr("队友：", "Teammate: ") + reply);
        }
        catch (Exception ex)
        {
            FinishChat(Tr("对话请求异常：", "Chat request error: ") + ex.Message);
        }
    }

    private static void FinishChat(string display)
    {
        RunOnMain(() =>
        {
            _chatBusy = false;
            ShowChatMessage(display);
        });
    }

    private static string BuildSummary(Dictionary<string, object?> root)
    {
        var lines = new List<string>();

        var players = root.TryGetValue("players", out object? pv) ? pv as List<object?> : null;
        var me = players != null && players.Count > 0 ? players[0] as Dictionary<string, object?> : null;
        if (me != null)
        {
            string line = $"{AsString(me, "name")} {AsInt(me, "hp")}/{AsInt(me, "maxHp")} "
                          + Tr("血", "HP");
            int block = AsInt(me, "block");
            if (block > 0)
            {
                line += Tr($" · 格挡{block}", $" · Block {block}");
            }
            line += Tr($" · {AsInt(me, "energy")}/{AsInt(me, "maxEnergy")} 能量",
                       $" · {AsInt(me, "energy")}/{AsInt(me, "maxEnergy")} Energy");
            lines.Add(line);

            var hand = me.TryGetValue("hand", out object? hv) ? hv as List<object?> : null;
            var names = new List<string>();
            if (hand != null)
            {
                foreach (object? item in hand)
                {
                    if (item is Dictionary<string, object?> card)
                    {
                        names.Add(AsString(card, "name"));
                    }
                }
            }
            lines.Add(Tr("手牌：", "Hand: ")
                      + (names.Count > 0 ? string.Join(Tr("、", ", "), names) : Tr("无", "none")));
        }

        var enemies = root.TryGetValue("enemies", out object? ev) ? ev as List<object?> : null;
        var enemyTexts = new List<string>();
        if (enemies != null)
        {
            foreach (object? item in enemies)
            {
                if (item is not Dictionary<string, object?> enemy)
                {
                    continue;
                }
                string text = $"{AsString(enemy, "name")} {AsInt(enemy, "hp")}/{AsInt(enemy, "maxHp")}";
                int eblock = AsInt(enemy, "block");
                if (eblock > 0)
                {
                    text += Tr($"(盾{eblock})", $" (Block {eblock})");
                }
                var intents = enemy.TryGetValue("intents", out object? iv) ? iv as List<string> : null;
                if (intents != null && intents.Count > 0)
                {
                    text += Tr(" 意图：", " Intent: ") + string.Join(Tr("、", ", "), intents);
                }
                else
                {
                    text += Tr(" 意图：未知", " Intent: unknown");
                }
                enemyTexts.Add(text);
            }
        }
        if (enemyTexts.Count > 0)
        {
            lines.Add(Tr("敌人：", "Enemies: ") + string.Join(Tr("；", "; "), enemyTexts));
        }

        return string.Join("\n", lines);
    }

    private static string AsString(Dictionary<string, object?> dict, string key)
    {
        return dict.TryGetValue(key, out object? value) && value != null ? value.ToString() ?? "" : "";
    }

    private static int AsInt(Dictionary<string, object?> dict, string key)
    {
        return dict.TryGetValue(key, out object? value) && value is int number ? number : 0;
    }

    private static string ReadAdvice(out string text, out int round, out string ask, out string mate)
    {
        text = "";
        ask = "";
        mate = "";
        round = 0;
        try
        {
            if (_advicePath.Length == 0 || !File.Exists(_advicePath))
            {
                return "";
            }

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(_advicePath));
            JsonElement root = doc.RootElement;
            string status = root.TryGetProperty("status", out JsonElement s) ? s.GetString() ?? "" : "";
            text = root.TryGetProperty("text", out JsonElement t) ? t.GetString() ?? "" : "";
            ask = root.TryGetProperty("ask", out JsonElement a) ? a.GetString() ?? "" : "";
            mate = root.TryGetProperty("mate", out JsonElement m) ? m.GetString() ?? "" : "";
            if (root.TryGetProperty("round", out JsonElement r) && r.TryGetInt32(out int value))
            {
                round = value;
            }
            return status;
        }
        catch
        {
            return "";
        }
    }

    private static void LogError(string message)
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // 完全相同的消息 5 秒内去重（避免刷屏，但心跳每秒一条能过去）
        if (message == _lastError && nowMs - _lastErrorTime < 5000)
        {
            return;
        }
        _lastError = message;
        _lastErrorTime = nowMs;
        try
        {
            string dir = Path.GetDirectoryName(_outFile) ?? ".";
            string line = DateTime.Now.ToString("HH:mm:ss") + " " + message + Environment.NewLine;
            File.AppendAllText(Path.Combine(dir, "mod_error.log"), line);
        }
        catch
        {
        }
    }
}
