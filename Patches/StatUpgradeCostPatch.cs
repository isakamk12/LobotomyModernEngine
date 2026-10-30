using System;
using System.Reflection;
using Customizing;
using HarmonyLib;
using UnityEngine;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// 職員のステータス強化（勇気・慎重・自制・正義）に必要なLOBポイントを一律1にするパッチ。
    /// 1段階強化するごとに消費コストが「1 LOB」になる。ConfigでON/OFF切り替え可能。
    /// </summary>
    public static class StatUpgradeCostPatch
    {
        private static readonly int[] CheapCostAry = new int[] { 0, 1, 1, 1, 1, 1, 1 };

        private static readonly FieldInfo _currentCostField = AccessTools.Field(typeof(StatUI.StatController), "_currentCost");
        private static readonly FieldInfo _statUIField = AccessTools.Field(typeof(StatUI.StatController), "statUI");

        public static bool IsEnabled
        {
            get
            {
                return Plugin.EnableCheapStatUpgrade == null || Plugin.EnableCheapStatUpgrade.Value;
            }
        }

        [HarmonyPatch(typeof(StatUI), "Init", new Type[0])]
        public static class StatUI_Init0_Patch
        {
            public static void Prefix(StatUI __instance)
            {
                if (!IsEnabled) return;
                StatUI.RCost = CheapCostAry;
                StatUI.WCost = CheapCostAry;
                StatUI.BCost = CheapCostAry;
                StatUI.PCost = CheapCostAry;
            }
        }

        [HarmonyPatch(typeof(StatUI), "Init", new Type[] { typeof(AgentData), typeof(bool) })]
        public static class StatUI_Init1_Patch
        {
            public static void Postfix(StatUI __instance)
            {
                if (!IsEnabled || __instance == null) return;
                // 各ステータスのコストを再計算
                if (__instance.RStat != null) __instance.RStat.SetCost();
                if (__instance.WStat != null) __instance.WStat.SetCost();
                if (__instance.BStat != null) __instance.BStat.SetCost();
                if (__instance.PStat != null) __instance.PStat.SetCost();
            }
        }

        /// <summary>
        /// StatController.SetCost を直接置き換え、1段階につき一律「1 LOB」として確定計算する。
        /// ボタンリスナーの多重登録を防ぎ、100%確実に反映させる。
        /// </summary>
        [HarmonyPatch(typeof(StatUI.StatController), "SetCost")]
        public static class StatController_SetCost_Patch
        {
            public static bool Prefix(StatUI.StatController __instance)
            {
                if (!IsEnabled || __instance == null) return true;

                try
                {
                    if (__instance.CurrentBonus == 0 && __instance.OriginalLevel >= StatUI.MaxStatLevel)
                    {
                        if (_currentCostField != null) _currentCostField.SetValue(__instance, 0);
                        if (__instance.CostText != null) __instance.CostText.text = "0";
                        if (CustomizingWindow.CurrentWindow != null)
                        {
                            if (__instance.PointText != null) __instance.PointText.color = CustomizingWindow.CurrentWindow.Normal;
                            if (__instance.CostText != null) __instance.CostText.color = CustomizingWindow.CurrentWindow.Normal;
                        }
                        __instance.SetGradeText(__instance.OriginalLevel);
                        return false;
                    }

                    // 1段階あたり一律 1 LOB
                    int cost = __instance.CurrentBonus > 0 ? __instance.CurrentBonus : 0;
                    if (_currentCostField != null) _currentCostField.SetValue(__instance, cost);
                    if (__instance.CostText != null) __instance.CostText.text = cost.ToString();
                    if (__instance.PointText != null) __instance.PointText.text = "LOB";

                    if (CustomizingWindow.CurrentWindow != null)
                    {
                        if (__instance.CurrentBonus == 0)
                        {
                            if (__instance.PointText != null) __instance.PointText.color = CustomizingWindow.CurrentWindow.Normal;
                            if (__instance.CostText != null) __instance.CostText.color = CustomizingWindow.CurrentWindow.Normal;
                        }
                        else
                        {
                            if (__instance.PointText != null) __instance.PointText.color = CustomizingWindow.CurrentWindow.Blue;
                            if (__instance.CostText != null) __instance.CostText.color = CustomizingWindow.CurrentWindow.Blue;
                        }
                    }

                    __instance.SetGradeText(__instance.OriginalLevel + __instance.CurrentBonus);

                    StatUI parentStatUI = _statUIField != null ? _statUIField.GetValue(__instance) as StatUI : null;
                    if (parentStatUI != null)
                    {
                        // 合計追加コストを再集計
                        int total = 0;
                        if (parentStatUI.RStat != null) total += parentStatUI.RStat.CurrentCost;
                        if (parentStatUI.WStat != null) total += parentStatUI.WStat.CurrentCost;
                        if (parentStatUI.BStat != null) total += parentStatUI.BStat.CurrentCost;
                        if (parentStatUI.PStat != null) total += parentStatUI.PStat.CurrentCost;
                        parentStatUI.CurrentAdditionalCost = total;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError(string.Format("[LME StatUpgradeCost] Error in SetCost: {0}", ex.Message));
                }

                return false;
            }
        }
    }
}
