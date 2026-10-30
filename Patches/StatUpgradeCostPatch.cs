using System;
using Customizing;
using HarmonyLib;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// 職員のステータス強化（勇気・慎重・自制・正義）に必要なLOBポイントを一律1にするパッチ。
    /// デバッグ・レベリング短縮用。ConfigでON/OFF切り替え可能。
    /// </summary>
    public static class StatUpgradeCostPatch
    {
        private static readonly int[] CheapCostAry = new int[] { 0, 1, 1, 1, 1, 1, 1 };

        private static int[] _originalRCost;
        private static int[] _originalWCost;
        private static int[] _originalBCost;
        private static int[] _originalPCost;
        private static bool _backupCaptured = false;

        public static bool IsEnabled
        {
            get
            {
                return Plugin.EnableCheapStatUpgrade == null || Plugin.EnableCheapStatUpgrade.Value;
            }
        }

        private static void CaptureOriginalCosts()
        {
            if (_backupCaptured) return;
            if (StatUI.RCost != null) _originalRCost = (int[])StatUI.RCost.Clone();
            if (StatUI.WCost != null) _originalWCost = (int[])StatUI.WCost.Clone();
            if (StatUI.BCost != null) _originalBCost = (int[])StatUI.BCost.Clone();
            if (StatUI.PCost != null) _originalPCost = (int[])StatUI.PCost.Clone();
            _backupCaptured = true;
        }

        [HarmonyPatch(typeof(StatUI), "Init", new Type[0])]
        public static class StatUI_Init_Patch
        {
            public static void Prefix(StatUI __instance)
            {
                CaptureOriginalCosts();

                if (IsEnabled)
                {
                    StatUI.RCost = CheapCostAry;
                    StatUI.WCost = CheapCostAry;
                    StatUI.BCost = CheapCostAry;
                    StatUI.PCost = CheapCostAry;
                }
                else if (_backupCaptured)
                {
                    StatUI.RCost = _originalRCost;
                    StatUI.WCost = _originalWCost;
                    StatUI.BCost = _originalBCost;
                    StatUI.PCost = _originalPCost;
                }
            }

            public static void Postfix(StatUI __instance)
            {
                if (IsEnabled && __instance != null)
                {
                    if (__instance.RStat != null) __instance.RStat.SetCostAry(CheapCostAry);
                    if (__instance.WStat != null) __instance.WStat.SetCostAry(CheapCostAry);
                    if (__instance.BStat != null) __instance.BStat.SetCostAry(CheapCostAry);
                    if (__instance.PStat != null) __instance.PStat.SetCostAry(CheapCostAry);
                }
            }
        }

        [HarmonyPatch(typeof(StatUI.StatController), "SetCost")]
        public static class StatController_SetCost_Patch
        {
            public static void Prefix(StatUI.StatController __instance)
            {
                if (!IsEnabled || __instance == null) return;

                // コスト配列がCheapCostAryでない場合は差し替え
                __instance.SetCostAry(CheapCostAry);
            }
        }
    }
}
