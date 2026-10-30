using System;
using Assets.Scripts.UI.Utils;
using CreatureInfo;
using HarmonyLib;
using InGameUI;
using UnityEngine;
using UnityEngine.UI;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// UIテキストサイズ・視認性・文字切れ修正パッチ (v0.7.2 改訂版)
    /// 1. エンサイクロペディアのアブノーマリティ記録 (CreatureInfoWindow.SetDesc):
    ///    - 段落フォントを適度に拡大(20pt)し、Canvas強制更新により正確な preferredHeight で段落重なりなく綺麗に配置。
    /// 2. エンサイクロペディアの E.G.O ギフト欄 (GiftSlot.ItemTrait):
    ///    - 3行効果文の豆粒化を防止し、読みやすいフォントサイズ(下限14pt)を保証。
    /// 3. 管理中職員のギフト説明文 (InGameUI.AgentGiftSlot):
    ///    - 効果テキストの最小フォント下限を引き上げ。
    /// 4. ローディング画面の文字切れ ("システム初期化" が3文字で切れる問題など):
    ///    - LocalizeTextLoadScript の id が "ContinueScene@" やローディング画面に該当する場合、
    ///      確実に横幅拡張(1200px)と Horizontal/Vertical Overflow を解放して全文字フル表示。
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
                slot.ItemName.resizeTextMinSize = 14;
                slot.ItemName.resizeTextMaxSize = 18;
            }

            if (slot.ItemTrait != null)
            {
                slot.ItemTrait.resizeTextMinSize = 14;
                slot.ItemTrait.resizeTextMaxSize = 17;
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
                    __instance.GiftName.resizeTextMinSize = 14;
                    __instance.GiftName.resizeTextMaxSize = 18;
                }

                if (__instance.GiftDescription != null)
                {
                    __instance.GiftDescription.resizeTextMinSize = 14;
                    __instance.GiftDescription.resizeTextMaxSize = 16;
                    EnhancedTextCount++;
                }
            }
        }

        // =========================================================================
        // 4. ローディング画面の文字切れ修正 ("システム初期化" が3文字で切れる問題など)
        // =========================================================================
        private static void FixLoadingText(Text t)
        {
            if (t == null) return;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            RectTransform rt = t.rectTransform;
            if (rt != null)
            {
                rt.sizeDelta = new Vector2(Mathf.Max(rt.sizeDelta.x, 1200f), Mathf.Max(rt.sizeDelta.y, 60f));
            }
            EnhancedTextCount++;
        }

        /// <summary>
        /// LocalizeTextLoadScript でローディング関連ID (ContinueScene@ 等) のテキストが代入された瞬間に
        /// 横幅とOverflowを解放し、文字切れを100%防止する
        /// </summary>
        [HarmonyPatch(typeof(LocalizeTextLoadScript), "SetText", new Type[0])]
        public static class LocalizeTextLoadScript_SetText_Patch
        {
            public static void Postfix(LocalizeTextLoadScript __instance)
            {
                if (!IsEnabled || __instance == null || string.IsNullOrEmpty(__instance.id)) return;

                string id = __instance.id;
                if (id.StartsWith("ContinueScene@") ||
                    id.StartsWith("Loading") ||
                    id.StartsWith("TitleEndScene") ||
                    id.StartsWith("StoryEndScene") ||
                    id.StartsWith("DayEndScene"))
                {
                    FixLoadingText(__instance.Text);
                }
            }
        }

        [HarmonyPatch(typeof(ContinueLoadingScene), "OnEnable")]
        public static class ContinueLoadingScene_OnEnable_Patch
        {
            public static void Postfix(ContinueLoadingScene __instance)
            {
                if (!IsEnabled || __instance == null) return;
                if (__instance.texts != null)
                {
                    for (int i = 0; i < __instance.texts.Length; i++)
                    {
                        if (__instance.texts[i] != null && __instance.texts[i].text != null)
                        {
                            FixLoadingText(__instance.texts[i].text);
                        }
                    }
                }
                if (__instance.sefiras != null)
                {
                    for (int j = 0; j < __instance.sefiras.Length; j++)
                    {
                        if (__instance.sefiras[j] != null && __instance.sefiras[j].name != null)
                        {
                            FixLoadingText(__instance.sefiras[j].name);
                        }
                    }
                }
            }
        }

        [HarmonyPatch(typeof(TitleEndScene), "OnEnable")]
        public static class TitleEndScene_OnEnable_Patch
        {
            public static void Postfix(TitleEndScene __instance)
            {
                if (!IsEnabled || __instance == null || __instance.texts == null) return;
                for (int i = 0; i < __instance.texts.Length; i++)
                {
                    if (__instance.texts[i] != null)
                    {
                        FixLoadingText(__instance.texts[i].text);
                    }
                }
            }
        }

        [HarmonyPatch(typeof(StoryEndScene), "OnEnable")]
        public static class StoryEndScene_OnEnable_Patch
        {
            public static void Postfix(StoryEndScene __instance)
            {
                if (!IsEnabled || __instance == null || __instance.texts == null) return;
                for (int i = 0; i < __instance.texts.Length; i++)
                {
                    if (__instance.texts[i] != null)
                    {
                        FixLoadingText(__instance.texts[i].text);
                    }
                }
            }
        }

        [HarmonyPatch(typeof(DayEndScene), "OnEnable")]
        public static class DayEndScene_OnEnable_Patch
        {
            public static void Postfix(DayEndScene __instance)
            {
                if (!IsEnabled || __instance == null || __instance.texts == null) return;
                for (int i = 0; i < __instance.texts.Length; i++)
                {
                    if (__instance.texts[i] != null && __instance.texts[i].text != null)
                    {
                        FixLoadingText(__instance.texts[i].text);
                    }
                }
            }
        }
    }
}
