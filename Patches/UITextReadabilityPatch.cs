using System;
using CreatureInfo;
using HarmonyLib;
using InGameUI;
using UnityEngine;
using UnityEngine.UI;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// UIテキストサイズ・視認性改善パッチ (v0.7.3)
    /// ※ローディング画面 (ContinueLoadingScene / StoryEndScene 等) の RectTransform や Text には一切触れず、
    ///   バニラ本来の美しい座標・レイアウトを100%維持する。
    /// 1. エンサイクロペディアのアブノーマリティ記録 (CreatureInfoWindow.SetDesc):
    ///    - 段落フォントを適度に拡大(20pt)し、Canvas強制更新により正確な preferredHeight で段落重なりなく綺麗に配置。
    /// 2. エンサイクロペディアの E.G.O ギフト欄 (GiftSlot.ItemTrait):
    ///    - 効果文の豆粒化(10pt)を防止し、読みやすいフォントサイズ(15〜16pt)を保証。
    /// 3. 管理中職員のギフト説明文 (InGameUI.AgentGiftSlot):
    ///    - 効果テキストのフォントサイズを15〜16ptに引き上げ、SetLayoutによる自動枠高計算と連動。
    /// </summary>
    public static class UITextReadabilityPatch
    {
        public static int EnhancedTextCount { get; private set; }

        public static void ClearCache()
        {
        }

        private static bool IsEnabled
        {
            get { return Plugin.EnableUITextReadabilityFix == null || Plugin.EnableUITextReadabilityFix.Value; }
        }

        // =========================================================================
        // 1. エンサイクロペディア: アブノーマリティ記録 (CreatureInfoWindow.SetDesc)
        // =========================================================================
        [HarmonyPatch(typeof(CreatureInfoWindow), "SetDesc")]
        public static class CreatureInfoWindow_SetDesc_Patch
        {
            public static void Postfix(CreatureInfoWindow __instance)
            {
                if (!IsEnabled || __instance == null || __instance.descList == null || __instance.listParent == null)
                    return;

                if (__instance.descList.Count == 0) return;

                const int storyFontSize = 20;

                for (int i = 0; i < __instance.descList.Count; i++)
                {
                    Text descText = __instance.descList[i];
                    if (descText == null) continue;

                    descText.fontSize = storyFontSize;
                    descText.resizeTextForBestFit = false;
                    descText.horizontalOverflow = HorizontalWrapMode.Wrap;
                    descText.verticalOverflow = VerticalWrapMode.Overflow;
                    descText.lineSpacing = 1.15f;
                }

                UnityEngine.Canvas.ForceUpdateCanvases();

                float currentY = 0f;
                for (int k = 0; k < __instance.descList.Count; k++)
                {
                    Text t = __instance.descList[k];
                    if (t == null) continue;

                    RectTransform rt = t.rectTransform;
                    if (rt != null)
                    {
                        rt.anchoredPosition = new Vector2(0f, -currentY);
                    }
                    currentY += t.preferredHeight + __instance.Spacing;
                    EnhancedTextCount++;
                }

                currentY += __instance.LowerSpacing;
                __instance.listParent.sizeDelta = new Vector2(__instance.listParent.sizeDelta.x, currentY);
                __instance.listParent.anchoredPosition = Vector2.zero;
            }
        }

        // =========================================================================
        // 2. エンサイクロペディア: E.G.O ギフト欄 (GiftSlot)
        // =========================================================================
        [HarmonyPatch(typeof(GiftSlot), "SetModel", new Type[] { typeof(EquipmentTypeInfo) })]
        public static class GiftSlot_SetModel_TypeInfo_Patch
        {
            public static void Postfix(GiftSlot __instance)
            {
                EnhanceGiftSlot(__instance);
            }
        }

        [HarmonyPatch(typeof(GiftSlot), "SetModel", new Type[] { typeof(EquipmentModel) })]
        public static class GiftSlot_SetModel_Model_Patch
        {
            public static void Postfix(GiftSlot __instance)
            {
                EnhanceGiftSlot(__instance);
            }
        }

        private static void EnhanceGiftSlot(GiftSlot slot)
        {
            if (!IsEnabled || slot == null) return;

            if (slot.ItemName != null)
            {
                slot.ItemName.resizeTextForBestFit = true;
                slot.ItemName.resizeTextMinSize = 14;
                slot.ItemName.resizeTextMaxSize = 17;
            }

            if (slot.ItemTrait != null)
            {
                slot.ItemTrait.resizeTextForBestFit = true;
                slot.ItemTrait.resizeTextMinSize = 14;
                slot.ItemTrait.resizeTextMaxSize = 16;
                slot.ItemTrait.horizontalOverflow = HorizontalWrapMode.Wrap;
                slot.ItemTrait.verticalOverflow = VerticalWrapMode.Truncate;
                EnhancedTextCount++;
            }
        }

        // =========================================================================
        // 3. 管理中職員のギフト説明文 (InGameUI.AgentGiftSlot)
        // =========================================================================
        [HarmonyPatch(typeof(AgentGiftSlot), "SetData")]
        public static class AgentGiftSlot_SetData_Patch
        {
            public static void Postfix(AgentGiftSlot __instance)
            {
                if (!IsEnabled || __instance == null) return;

                if (__instance.GiftName != null)
                {
                    __instance.GiftName.resizeTextForBestFit = false;
                    __instance.GiftName.fontSize = 16;
                }

                if (__instance.GiftDescription != null)
                {
                    __instance.GiftDescription.resizeTextForBestFit = false;
                    __instance.GiftDescription.fontSize = 15;
                    EnhancedTextCount++;
                }
            }
        }
    }
}
