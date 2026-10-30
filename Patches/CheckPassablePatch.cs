using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// 移動エンティティが毎FixedUpdateで new List<AgentModel>() を生成し全職員を走査する病巣を排除するパッチ
    /// </summary>
    [HarmonyPatch(typeof(MovableObjectNode))]
    public static class CheckPassablePatch
    {
        [HarmonyPatch("CheckPassable")]
        [HarmonyPrefix]
        public static bool Prefix_CheckPassable(
            MovableObjectNode __instance,
            MapEdge edge,
            EdgeDirection edgeDir,
            float oldEdgePosRate,
            float newEdgePosRate,
            ref bool __result,
            List<PassType> ___unpassableList)
        {
            if (!Plugin.EnablePassableOptimization.Value) return true;

            // currentPassage はパブリックプロパティ
            if (__instance.currentPassage == null)
            {
                __result = true;
                return false;
            }

            // SHIELDBEARER の判定が必要かチェック
            bool checkShield = false;
            if (___unpassableList != null)
            {
                for (int i = 0; i < ___unpassableList.Count; i++)
                {
                    if (___unpassableList[i] == PassType.SHIELDBEARER)
                    {
                        checkShield = true;
                        break;
                    }
                }
            }

            if (!checkShield)
            {
                __result = true;
                return false; // シールド判定不要なら即座に true
            }

            Vector3 viewPositionInEdge = MovableObjectNode.GetViewPositionInEdge(edge, edgeDir, Mathf.Clamp01(newEdgePosRate));

            // 全職員リストを new List せずに直接 1 パス走査
            var agentList = AgentManager.instance != null ? AgentManager.instance.GetAgentList() : null;
            if (agentList == null || agentList.Count == 0)
            {
                __result = true;
                return false;
            }

            for (int i = 0; i < agentList.Count; i++)
            {
                AgentModel agent = agentList[i];
                if (agent != null && agent.IsShieldBlock())
                {
                    Vector3 currentViewPosition = agent.GetCurrentViewPosition();
                    float minX = currentViewPosition.x - 1f;
                    float maxX = currentViewPosition.x + 1f;
                    if (minX <= viewPositionInEdge.x && viewPositionInEdge.x <= maxX)
                    {
                        __result = false;
                        return false; // 遮られたら即座に終了
                    }
                }
            }

            __result = true;
            return false;
        }
    }
}
