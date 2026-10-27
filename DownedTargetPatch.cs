using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;

namespace Sts2Companion;

/// <summary>
/// 测试版：downed 玩家不允许被任何攻击/队友卡牌选中。
/// 游戏判定“能不能打中”最终都会走 Hook.ShouldAllowHitting。
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.ShouldAllowHitting))]
internal static class DownedTargetPatch
{
    private static bool _applied;

    public static void Ensure()
    {
        if (_applied) return;
        _applied = true;
        try
        {
            var harmony = new Harmony("sts2companion.downed-target");
            harmony.PatchAll(typeof(DownedTargetPatch).Assembly);
            System.Console.Error.WriteLine("[Sts2Companion] downed target patch applied");
        }
        catch (System.Exception ex)
        {
            System.Console.Error.WriteLine("[Sts2Companion] downed target patch failed: " + ex);
        }
    }

    [HarmonyPrefix]
    private static bool Prefix(ICombatState combatState, Creature creature, ref bool __result)
    {
        if (!CompanionMod.IsDowned(creature)) return true;
        __result = false;
        return false;
    }
}
