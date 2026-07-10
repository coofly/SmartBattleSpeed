// 用途：判断当前攻击起手是否属于 NPC 攻击主角本人，并生成诊断日志文本。

using Il2Cpp;

namespace SmartBattleSpeedMod;

// NPC 攻击主角倍速的目标检测工具。
internal static class NpcAttackMainCharacterDetector
{
    // 判断当前起手攻击是否为非主角单位实际攻击主角本人。
    public static bool IsTargetingMainCharacter(BattleController battle, out string diagnostic)
    {
        BattleUnit? attacker = battle.nowActiveUnit;
        if (attacker == null)
        {
            diagnostic = "attacker=无";
            return false;
        }

        int attackerHeroId = attacker.heroData?.heroID ?? -1;
        int attackerTeamId = attacker.battleTeam?.ID ?? -1;
        bool attackerTeamHasPlayer = attacker.battleTeam?.havePlayer ?? false;
        Il2CppSystem.Collections.Generic.List<GridUnitData>? targetGrids = battle.damageRangeGridUnits;
        if (targetGrids == null)
        {
            diagnostic = $"attackerHeroID={attackerHeroId}，attackerTeamID={attackerTeamId}，attackerTeamHasPlayer={attackerTeamHasPlayer}，damageRange=无";
            return false;
        }

        bool containsMainCharacterTarget = false;
        int targetCount = targetGrids.Count;
        string targetSummary = string.Empty;
        for (int i = 0; i < targetCount; i++)
        {
            GridUnitData? grid = targetGrids[i];
            BattleUnit? targetUnit = grid?.battleUnit;
            int targetHeroId = targetUnit?.heroData?.heroID ?? -1;
            bool targetPlayerControl = targetUnit?.playerControl ?? false;
            bool availableEnemy = grid != null && battle.HaveAvailableEnemyUnit(grid);
            if (targetHeroId == 0 && availableEnemy)
            {
                containsMainCharacterTarget = true;
            }

            if (i < 8)
            {
                if (targetSummary.Length > 0)
                {
                    targetSummary += "；";
                }

                targetSummary += $"#{i}:heroID={targetHeroId},playerControl={targetPlayerControl},availableEnemy={availableEnemy}";
            }
        }

        if (targetCount > 8)
        {
            targetSummary += $"；...共{targetCount}格";
        }

        diagnostic = $"attackerHeroID={attackerHeroId}，attackerTeamID={attackerTeamId}，attackerTeamHasPlayer={attackerTeamHasPlayer}，damageRangeCount={targetCount}，targets=[{targetSummary}]";
        return attackerHeroId != 0 && containsMainCharacterTarget;
    }
}
