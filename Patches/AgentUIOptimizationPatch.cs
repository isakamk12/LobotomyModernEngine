using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// v0.8.5: イェソド研究解放後、AgentUnit.Update 内で毎描画フレーム全職員(24人)に対して
    /// activateUI() が呼ばれ、毎秒1,440回もの文字列結合 (model.GetTitle, model.GetUnitName) と
    /// Textレイアウト幅計算 (title.preferredWidth >= 510f) が強制実行されて
    /// 4.5秒ごとに 39ms の Gen0 GC 停止を招いていた問題を解消する。
    /// 
    /// AgentUnitUI.activateUI を Prefix でフックし、職員の名前・称号・エース状態・ボス戦状態に
    /// 変更がない場合は即座に return false して重い文字列処理・レイアウト処理をスキップする。
    /// </summary>
    public static class AgentUIOptimizationPatch
    {
        private class AgentUIState
        {
            public string LastUnitName;
            public string LastTitle;
            public bool LastIsAce;
            public int LastKitId = -1;
            public bool LastBossActive;
            public bool IsInitialized;
        }

        private static readonly Dictionary<long, AgentUIState> _stateCache = new Dictionary<long, AgentUIState>(64);

        public static long AvoidedActivateUICalls { get; private set; }
        public static long RealActivateUICalls { get; private set; }

        public static void ClearCache()
        {
            _stateCache.Clear();
        }

        [HarmonyPatch(typeof(AgentUnitUI), "activateUI")]
        public static class AgentUnitUI_activateUI_Patch
        {
            public static bool Prefix(AgentUnitUI __instance, AgentModel model)
            {
                if (!Plugin.EnableAgentUIOptimization.Value)
                {
                    return true;
                }

                if (model == null)
                {
                    return true;
                }

                bool bossActive = SefiraBossManager.Instance != null &&
                                  SefiraBossManager.Instance.CheckBossActivation(SefiraEnum.YESOD);

                if (bossActive)
                {
                    __instance.DeactivateAllUI();
                    return false;
                }

                if (!_stateCache.TryGetValue(model.instanceId, out var state))
                {
                    state = new AgentUIState();
                    _stateCache[model.instanceId] = state;
                }

                string currentTitle = model.GetTitle();
                string currentName = model.GetUnitName();
                bool currentAce = model.isAce;
                int currentKitId = model.Equipment?.kitCreature != null ? (int)model.Equipment.kitCreature.metadataId : -1;

                // 状態が何も変わっていなければ、文字列生成・TextPreferredWidth計算をスキップ
                if (state.IsInitialized &&
                    state.LastBossActive == bossActive &&
                    state.LastIsAce == currentAce &&
                    state.LastKitId == currentKitId &&
                    state.LastUnitName == currentName &&
                    state.LastTitle == currentTitle)
                {
                    AvoidedActivateUICalls++;
                    return false;
                }

                // 状態変化時または初回のみ完全更新を実行
                state.LastUnitName = currentName;
                state.LastTitle = currentTitle;
                state.LastIsAce = currentAce;
                state.LastKitId = currentKitId;
                state.LastBossActive = bossActive;
                state.IsInitialized = true;
                RealActivateUICalls++;

                __instance.Activated = true;
                if (__instance.Name != null)
                {
                    __instance.Name.text = currentName;
                }

                string pre;
                string post;
                model.GetTitle(out pre, out post);

                if (__instance.title != null)
                {
                    __instance.title.text = currentTitle;
                    if (__instance.title.preferredWidth >= 510f)
                    {
                        __instance.title.text = string.Format("{0}\n{1}", pre, post);
                    }
                    __instance.title.gameObject.SetActive(true);
                }

                if (__instance.kitCreatureIcon != null)
                {
                    Vector2 anchoredPosition = __instance.kitCreatureIcon.rectTransform.anchoredPosition;
                    anchoredPosition.x = currentAce ? 419f : 362f;
                    __instance.kitCreatureIcon.rectTransform.anchoredPosition = anchoredPosition;
                }

                return false; // バニラの activateUI を完全代替
            }
        }
    }
}
