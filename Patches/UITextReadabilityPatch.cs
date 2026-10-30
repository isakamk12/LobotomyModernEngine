using System;
using System.Collections.Generic;
using Assets.Scripts.UI.Utils;
using CreatureInfo;
using HarmonyLib;
using InGameUI;
using Inventory;
using UnityEngine;
using UnityEngine.UI;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// v0.6.0: UIテキストサイズ・視認性・文字切れ包括修正パッチ (最優先対応)
    /// 対象:
    /// 1. エンサイクロペディア (CreatureInfoWindow / CreatureInfo.*):
    ///    - アブノーマリティ記録 (SetDesc): フォント拡大・行間調整・段落座標＆スクロール高さの完全再計算
    ///    - E.G.O ギフト欄 (GiftSlot): 効果テキスト(ItemTrait)・名称(ItemName)・見出し(Title)の縮小下限引き上げ＆縦溢れ許可
    ///    - E.G.O 武器/防具欄 (WeaponSlot / ArmorSlot / EquipSlot): 各種ステータス・名称・必要観測レベル文字の拡大
    ///    - 管理方法 (CreatureInfoCaretakingSlot): タイトル・開放コスト文字・スクロール判定の適正化
    ///    - 観測ボーナス・図鑑一覧・ツール型アブノーマリティ記録 (ObserveLevelEffectSlot / Codex / KitLayoutController)
    /// 2. ギフト表示＆インベントリ詳細 (AgentInfoWindow / AgentGiftSlot / InventoryAgentController / InventoryItemController):
    ///    - 職員情報ウィンドウのギフト一覧 (AgentInfoWindow.InitGiftArea): 複数行ギフト効果による極小化・文字潰れ解消
    ///    - 管理中職員ギフト詳細 (InGameUI.AgentGiftSlot): フォント拡大と preferredHeight 連動フレーム拡張
    ///    - インベントリ画面の職員装着ギフト一覧 (InventoryAgentController.SetUI): フォント拡大とレイアウト高さ自動追従
    ///    - インベントリ武器・防具詳細ツールチップ (InventoryItemController.OnClickDetailInfo): 特殊能力・解説文の拡大と再レイアウト
    /// 3. ローディング画面の文字切れ修正 ("システム初期化" が3文字しか表示されない問題など):
    ///    - ContinueLoadingScene / TitleEndScene / StoryEndScene / DayEndScene の Text Overflow 解放
    /// 4. 全般ローカライズテキスト＆オーバーレイツールチップの極小縮小防止:
    ///    - LocalizeTextLoadScript / OverlayManager の最小フォントサイズ引き上げ
    /// </summary>
    public static class UITextReadabilityPatch
    {
        public static int EnhancedTextCount { get; private set; }

        private static readonly Dictionary<int, int> _baseFontSizeCache = new Dictionary<int, int>(2048);

        public static void ClearCache()
        {
            _baseFontSizeCache.Clear();
        }

        private static bool IsEnabled
        {
            get { return Plugin.EnableUITextReadabilityFix == null || Plugin.EnableUITextReadabilityFix.Value; }
        }

        private static float ScaleMultiplier
        {
            get
            {
                if (Plugin.UITextScaleMultiplier == null) return 1.25f;
                return Mathf.Clamp(Plugin.UITextScaleMultiplier.Value, 1.0f, 1.8f);
            }
        }

        private static int MinFontSize
        {
            get
            {
                if (Plugin.UITextMinFontSize == null) return 18;
                return Mathf.Clamp(Plugin.UITextMinFontSize.Value, 12, 28);
            }
        }

        /// <summary>
        /// Unity Text コンポーネントの元のフォントサイズを記録し、累積拡大を防ぎつつ視認性を向上させるヘルパー
        /// </summary>
        public static void EnhanceText(
            Text text,
            int minFontFloor,
            int minBestFitFloor,
            float scaleMult = -1f,
            bool allowVerticalOverflow = false,
            bool allowHorizontalOverflow = false,
            float lineSpacing = -1f)
        {
            if (text == null || !IsEnabled) return;

            int id = text.GetInstanceID();
            int baseSize;
            if (!_baseFontSizeCache.TryGetValue(id, out baseSize))
            {
                baseSize = text.fontSize > 0 ? text.fontSize : 16;
                _baseFontSizeCache[id] = baseSize;
            }

            float mult = scaleMult > 0f ? scaleMult : ScaleMultiplier;
            int scaledSize = Mathf.RoundToInt(baseSize * mult);
            int targetSize = Mathf.Max(scaledSize, minFontFloor);

            text.fontSize = targetSize;

            if (text.resizeTextForBestFit)
            {
                if (text.resizeTextMinSize < minBestFitFloor)
                {
                    text.resizeTextMinSize = minBestFitFloor;
                }
                if (text.resizeTextMaxSize < targetSize)
                {
                    text.resizeTextMaxSize = targetSize;
                }
            }

            if (allowVerticalOverflow)
            {
                text.verticalOverflow = VerticalWrapMode.Overflow;
            }

            if (allowHorizontalOverflow)
            {
                text.horizontalOverflow = HorizontalWrapMode.Overflow;
            }

            if (lineSpacing > 0f && text.lineSpacing < lineSpacing)
            {
                text.lineSpacing = lineSpacing;
            }

            EnhancedTextCount++;
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

                int minStoryFont = Mathf.Max(MinFontSize + 4, 24);
                float num = 0f;

                for (int i = 0; i < __instance.descList.Count; i++)
                {
                    Text descText = __instance.descList[i];
                    if (descText == null) continue;

                    // ストーリー本文は BestFit による不均一な縮小を止め、十分なフォントサイズと行間を確保する
                    descText.resizeTextForBestFit = false;
                    descText.horizontalOverflow = HorizontalWrapMode.Wrap;
                    descText.verticalOverflow = VerticalWrapMode.Overflow;
                    EnhanceText(descText, minStoryFont, minStoryFont, ScaleMultiplier, true, false, 1.18f);

                    RectTransform rt = descText.rectTransform;
                    if (rt != null)
                    {
                        rt.anchoredPosition = new Vector2(0f, -num);
                    }
                    num += descText.preferredHeight + __instance.Spacing;
                }

                num += __instance.LowerSpacing;
                __instance.listParent.sizeDelta = new Vector2(__instance.listParent.sizeDelta.x, num);
                __instance.listParent.anchoredPosition = Vector2.zero;
            }
        }

        // =========================================================================
        // 2. エンサイクロペディア: E.G.O ギフト・武器・防具欄 (GiftSlot / WeaponSlot / ArmorSlot / EquipSlot)
        // =========================================================================
        [HarmonyPatch(typeof(GiftSlot), "SetModel", new Type[] { typeof(EquipmentTypeInfo) })]
        public static class GiftSlot_SetModel_TypeInfo_Patch
        {
            public static void Postfix(GiftSlot __instance)
            {
                EnhanceEncyclopediaGiftSlot(__instance);
            }
        }

        [HarmonyPatch(typeof(GiftSlot), "SetModel", new Type[] { typeof(EquipmentModel) })]
        public static class GiftSlot_SetModel_Model_Patch
        {
            public static void Postfix(GiftSlot __instance)
            {
                EnhanceEncyclopediaGiftSlot(__instance);
            }
        }

        [HarmonyPatch(typeof(GiftSlot), "SetProb")]
        public static class GiftSlot_SetProb_Patch
        {
            public static void Postfix(GiftSlot __instance)
            {
                if (!IsEnabled || __instance == null) return;
                EnhanceText(__instance.Title, Mathf.Max(MinFontSize + 2, 20), MinFontSize, ScaleMultiplier, true, true);
            }
        }

        private static void EnhanceEncyclopediaGiftSlot(GiftSlot slot)
        {
            if (!IsEnabled || slot == null) return;

            int minFloor = MinFontSize;
            int minTraitSize = Mathf.Max(minFloor + 2, 20);

            EnhanceText(slot.Title, minTraitSize, minFloor, ScaleMultiplier, true, true);
            EnhanceText(slot.ItemName, minTraitSize, minFloor, ScaleMultiplier, true, false);
            EnhanceText(slot.BlockText, minTraitSize, minFloor, ScaleMultiplier, true, false);

            if (slot.ItemTrait != null)
            {
                // エンサイクロペディアのギフト効果説明文 (ItemTrait):
                // 3行の日本語テキストでも極小化・縦切れしないよう最小サイズを引き上げ、縦Overflowを許可
                EnhanceText(slot.ItemTrait, minTraitSize, minFloor, ScaleMultiplier, true, false, 1.12f);
            }
        }

        [HarmonyPatch(typeof(WeaponSlot), "SetModel", new Type[] { typeof(EquipmentTypeInfo) })]
        public static class WeaponSlot_SetModel_TypeInfo_Patch
        {
            public static void Postfix(WeaponSlot __instance)
            {
                EnhanceEncyclopediaWeaponSlot(__instance);
            }
        }

        [HarmonyPatch(typeof(WeaponSlot), "SetModel", new Type[] { typeof(EquipmentModel) })]
        public static class WeaponSlot_SetModel_Model_Patch
        {
            public static void Postfix(WeaponSlot __instance)
            {
                EnhanceEncyclopediaWeaponSlot(__instance);
            }
        }

        private static void EnhanceEncyclopediaWeaponSlot(WeaponSlot slot)
        {
            if (!IsEnabled || slot == null) return;

            int minFloor = MinFontSize;
            EnhanceText(slot.Title, minFloor + 2, minFloor, ScaleMultiplier, true, true);
            EnhanceText(slot.ItemName, minFloor + 2, minFloor, ScaleMultiplier, true, false);
            EnhanceText(slot.DamageRange, minFloor + 2, minFloor, ScaleMultiplier, true, true);
            EnhanceText(slot.AttackSpeed, minFloor, minFloor - 2, ScaleMultiplier, true, false);
            EnhanceText(slot.AttackRange, minFloor, minFloor - 2, ScaleMultiplier, true, false);
            EnhanceText(slot.MakeCount, minFloor, minFloor - 2, ScaleMultiplier, true, true);
            EnhanceText(slot.TypeText, minFloor, minFloor - 2, ScaleMultiplier, true, true);
            EnhanceText(slot.BlockText, minFloor + 2, minFloor, ScaleMultiplier, true, false);
        }

        [HarmonyPatch(typeof(ArmorSlot), "SetModel", new Type[] { typeof(EquipmentTypeInfo) })]
        public static class ArmorSlot_SetModel_TypeInfo_Patch
        {
            public static void Postfix(ArmorSlot __instance)
            {
                EnhanceEncyclopediaArmorSlot(__instance);
            }
        }

        [HarmonyPatch(typeof(ArmorSlot), "SetModel", new Type[] { typeof(EquipmentModel) })]
        public static class ArmorSlot_SetModel_Model_Patch
        {
            public static void Postfix(ArmorSlot __instance)
            {
                EnhanceEncyclopediaArmorSlot(__instance);
            }
        }

        private static void EnhanceEncyclopediaArmorSlot(ArmorSlot slot)
        {
            if (!IsEnabled || slot == null) return;

            int minFloor = MinFontSize;
            EnhanceText(slot.Title, minFloor + 2, minFloor, ScaleMultiplier, true, true);
            EnhanceText(slot.ItemName, minFloor + 2, minFloor, ScaleMultiplier, true, false);
            EnhanceText(slot.MakeCount, minFloor, minFloor - 2, ScaleMultiplier, true, true);
            EnhanceText(slot.BlockText, minFloor + 2, minFloor, ScaleMultiplier, true, false);

            if (slot.TypeText != null)
            {
                for (int i = 0; i < slot.TypeText.Length; i++)
                {
                    EnhanceText(slot.TypeText[i], minFloor - 1, minFloor - 3, ScaleMultiplier, true, true);
                }
            }

            if (slot.RWBP_Defense != null)
            {
                for (int i = 0; i < slot.RWBP_Defense.Length; i++)
                {
                    EnhanceText(slot.RWBP_Defense[i], minFloor, minFloor - 2, ScaleMultiplier, true, false);
                }
            }
        }

        [HarmonyPatch(typeof(EquipSlot), "CheckBlocked")]
        public static class EquipSlot_CheckBlocked_Patch
        {
            public static void Postfix(EquipSlot __instance)
            {
                if (!IsEnabled || __instance == null) return;
                EnhanceText(__instance.BlockText, MinFontSize + 2, MinFontSize, ScaleMultiplier, true, false);
            }
        }

        [HarmonyPatch(typeof(EquipSlot), "NoData")]
        public static class EquipSlot_NoData_Patch
        {
            public static void Postfix(EquipSlot __instance)
            {
                if (!IsEnabled || __instance == null) return;
                EnhanceText(__instance.BlockText, MinFontSize + 2, MinFontSize, ScaleMultiplier, true, false);
            }
        }

        // =========================================================================
        // 3. エンサイクロペディア: 管理方法・基本情報・作業・観測ボーナス・図鑑・ツール型
        // =========================================================================
        [HarmonyPatch(typeof(CreatureInfoCaretakingSlot), "SetData")]
        public static class CreatureInfoCaretakingSlot_SetData_Patch
        {
            public static void Postfix(CreatureInfoCaretakingSlot __instance)
            {
                if (!IsEnabled || __instance == null) return;

                EnhanceText(__instance.Title, MinFontSize + 2, MinFontSize, ScaleMultiplier, true, true);
                if (__instance.Open != null)
                {
                    EnhanceText(__instance.Open.AreaText, MinFontSize + 2, MinFontSize, ScaleMultiplier, true, false);
                    EnhanceText(__instance.Open.CostText, MinFontSize + 2, MinFontSize, ScaleMultiplier, true, true);
                }

                if (__instance.CaretakingSection != null)
                {
                    // バニラは fontSize = 30 固定だが、行間を少し広げて漢字の詰まりを緩和する
                    if (__instance.CaretakingSection.lineSpacing < 1.12f)
                    {
                        __instance.CaretakingSection.lineSpacing = 1.12f;
                    }
                }
            }
        }

        [HarmonyPatch(typeof(CreatureInfoObserveLevelEffectSlot), "SetText")]
        public static class CreatureInfoObserveLevelEffectSlot_SetText_Patch
        {
            public static void Postfix(CreatureInfoObserveLevelEffectSlot __instance)
            {
                if (!IsEnabled || __instance == null) return;
                EnhanceText(__instance.InnerText, MinFontSize, MinFontSize - 2, ScaleMultiplier, true, false);
                EnhanceText(__instance.GradeText, MinFontSize, MinFontSize - 2, ScaleMultiplier, true, true);
            }
        }

        [HarmonyPatch(typeof(CreatureInfoStatRoot), "Initialize", new Type[0])]
        public static class CreatureInfoStatRoot_Initialize0_Patch
        {
            public static void Postfix(CreatureInfoStatRoot __instance)
            {
                EnhanceStatRoot(__instance);
            }
        }

        [HarmonyPatch(typeof(CreatureInfoStatRoot), "Initialize", new Type[] { typeof(CreatureModel) })]
        public static class CreatureInfoStatRoot_Initialize1_Patch
        {
            public static void Postfix(CreatureInfoStatRoot __instance)
            {
                EnhanceStatRoot(__instance);
            }
        }

        private static void EnhanceStatRoot(CreatureInfoStatRoot root)
        {
            if (!IsEnabled || root == null) return;
            int minFloor = MinFontSize;
            EnhanceText(root.Name, minFloor + 4, minFloor, ScaleMultiplier, true, false);
            EnhanceText(root.CodeNo, minFloor + 2, minFloor, ScaleMultiplier, true, true);
            EnhanceText(root.WorkDamageType, minFloor, minFloor - 2, ScaleMultiplier, true, true);
            EnhanceText(root.WorkDamageRange, minFloor + 2, minFloor, ScaleMultiplier, true, true);
            if (root.slots != null)
            {
                for (int i = 0; i < root.slots.Count; i++)
                {
                    CreatureInfoStatFeelingStateSlot s = root.slots[i];
                    if (s != null)
                    {
                        EnhanceText(s.Label, minFloor, minFloor - 2, ScaleMultiplier, true, true);
                        EnhanceText(s.RangeText, minFloor + 2, minFloor, ScaleMultiplier, true, true);
                    }
                }
            }
        }

        [HarmonyPatch(typeof(CreatureInfoWorkSlot), "SetRWBPType")]
        public static class CreatureInfoWorkSlot_SetRWBPType_Patch
        {
            public static void Postfix(CreatureInfoWorkSlot __instance)
            {
                if (!IsEnabled || __instance == null) return;
                EnhanceText(__instance.WorkName, MinFontSize + 2, MinFontSize, ScaleMultiplier, true, true);
            }
        }

        [HarmonyPatch(typeof(CreatureInfoWorkSlot), "Initialize", new Type[0])]
        public static class CreatureInfoWorkSlot_Initialize0_Patch
        {
            public static void Postfix(CreatureInfoWorkSlot __instance)
            {
                EnhanceWorkSlotPercentages(__instance);
            }
        }

        [HarmonyPatch(typeof(CreatureInfoWorkSlot), "Initialize", new Type[] { typeof(CreatureModel) })]
        public static class CreatureInfoWorkSlot_Initialize1_Patch
        {
            public static void Postfix(CreatureInfoWorkSlot __instance)
            {
                EnhanceWorkSlotPercentages(__instance);
            }
        }

        private static void EnhanceWorkSlotPercentages(CreatureInfoWorkSlot slot)
        {
            if (!IsEnabled || slot == null) return;
            EnhanceText(slot.WorkName, MinFontSize + 2, MinFontSize, ScaleMultiplier, true, true);
            if (slot.levelSuccessPercentage != null)
            {
                for (int i = 0; i < slot.levelSuccessPercentage.Length; i++)
                {
                    EnhanceText(slot.levelSuccessPercentage[i], MinFontSize, MinFontSize - 2, ScaleMultiplier, true, true);
                }
            }
        }

        [HarmonyPatch(typeof(CreatureInfoCodex), "OnOeverlayEnter")]
        public static class CreatureInfoCodex_OnOeverlayEnter_Patch
        {
            public static void Postfix(CreatureInfoCodex __instance)
            {
                if (!IsEnabled || __instance == null) return;
                EnhanceText(__instance.Name, MinFontSize + 4, MinFontSize, ScaleMultiplier, true, false);
                EnhanceText(__instance.Code, MinFontSize + 2, MinFontSize, ScaleMultiplier, true, true);
                EnhanceText(__instance.Risk, MinFontSize + 2, MinFontSize, ScaleMultiplier, true, true);
                EnhanceText(__instance.Observation_Title, MinFontSize + 2, MinFontSize, ScaleMultiplier, true, true);
                EnhanceText(__instance.Observation_Percent, MinFontSize + 4, MinFontSize, ScaleMultiplier, true, true);
            }
        }

        [HarmonyPatch(typeof(CreatureInfoCodexSlot), "Init")]
        public static class CreatureInfoCodexSlot_Init_Patch
        {
            public static void Postfix(CreatureInfoCodexSlot __instance)
            {
                if (!IsEnabled || __instance == null) return;
                EnhanceText(__instance.Text, MinFontSize, MinFontSize - 2, ScaleMultiplier, true, true);
            }
        }

        [HarmonyPatch(typeof(CreatureInfoKitLayoutController), "MakeEmptySlot")]
        public static class CreatureInfoKitLayoutController_MakeEmptySlot_Patch
        {
            public static void Postfix(CreatureInfoKitDataSlot __result)
            {
                if (!IsEnabled || __result == null) return;
                int minKitSize = Mathf.Max(MinFontSize + 2, 20);
                EnhanceText(__result.Left_RecordText, minKitSize, MinFontSize, ScaleMultiplier, true, false, 1.12f);
                EnhanceText(__result.Right_UseText, minKitSize, MinFontSize, ScaleMultiplier, true, false, 1.12f);
                EnhanceText(__result.needCostText, minKitSize, MinFontSize, ScaleMultiplier, true, false);
            }
        }

        // =========================================================================
        // 4. ギフト表示＆インベントリ詳細 (AgentInfoWindow / AgentGiftSlot / Inventory)
        // =========================================================================

        /// <summary>
        /// 職員情報ウィンドウ (配備画面等) の E.G.O ギフト一覧と連続勤務ボーナス表示を拡大・レイアウト調整
        /// </summary>
        [HarmonyPatch(typeof(AgentInfoWindow), "InitGiftArea")]
        public static class AgentInfoWindow_InitGiftArea_Patch
        {
            public static void Postfix(AgentInfoWindow __instance)
            {
                if (!IsEnabled || __instance == null) return;

                int minFloor = MinFontSize;
                int giftTextSize = Mathf.Max(minFloor + 2, 20);

                EnhanceText(__instance.GiftSlot_Title, giftTextSize, minFloor, ScaleMultiplier, true, false);
                EnhanceText(__instance.GiftSlot_EffectText, giftTextSize, minFloor, ScaleMultiplier, true, false, 1.12f);

                if (__instance.GiftSlotParent != null)
                {
                    foreach (Transform child in __instance.GiftSlotParent)
                    {
                        if (child == null) continue;
                        Text giftText = child.GetComponent<Text>();
                        if (giftText == null) continue;

                        // 複数行のギフト効果でも極小化しないよう BestFit を解除または最小サイズを引き上げ、
                        // テキストの preferredHeight に合わせて RectTransform と LayoutElement の高さを自動拡張する
                        giftText.resizeTextForBestFit = false;
                        giftText.horizontalOverflow = HorizontalWrapMode.Wrap;
                        giftText.verticalOverflow = VerticalWrapMode.Overflow;
                        EnhanceText(giftText, giftTextSize, minFloor, ScaleMultiplier, true, false, 1.12f);

                        float prefH = giftText.preferredHeight + 6f;
                        RectTransform rt = giftText.rectTransform;
                        if (rt != null && rt.sizeDelta.y < prefH)
                        {
                            rt.sizeDelta = new Vector2(rt.sizeDelta.x, prefH);
                        }

                        LayoutElement le = child.GetComponent<LayoutElement>();
                        if (le != null && le.preferredHeight < prefH)
                        {
                            le.preferredHeight = prefH;
                            le.minHeight = prefH;
                        }
                    }

                    LayoutRebuilder.ForceRebuildLayoutImmediate(__instance.GiftSlotParent);
                }
            }
        }

        /// <summary>
        /// 管理中の職員詳細ウィンドウ内 E.G.O ギフトスロット (InGameUI.AgentGiftSlot):
        /// SetData 時点でフォントサイズを拡大しておくことで、直後の LateUpdate -> SetLayout() 内の
        /// `GiftDescription.preferredHeight + 5f` 計算が自動的に拡大後の高さに合わせてスロット枠 (rectTransform / Frame) を広げる！
        /// </summary>
        [HarmonyPatch(typeof(AgentGiftSlot), "SetData")]
        public static class AgentGiftSlot_SetData_Patch
        {
            public static void Postfix(AgentGiftSlot __instance)
            {
                if (!IsEnabled || __instance == null) return;

                int minFloor = MinFontSize;
                int descSize = Mathf.Max(minFloor + 2, 20);

                EnhanceText(__instance.RegionName, minFloor, minFloor - 2, ScaleMultiplier, true, true);
                EnhanceText(__instance.GiftName, descSize, minFloor, ScaleMultiplier, true, false);

                if (__instance.GiftDescription != null)
                {
                    __instance.GiftDescription.resizeTextForBestFit = false;
                    __instance.GiftDescription.horizontalOverflow = HorizontalWrapMode.Wrap;
                    __instance.GiftDescription.verticalOverflow = VerticalWrapMode.Overflow;
                    EnhanceText(__instance.GiftDescription, descSize, minFloor, ScaleMultiplier, true, false, 1.12f);
                }
            }
        }

        [HarmonyPatch(typeof(AgentGiftSlot), "SetRegionName")]
        public static class AgentGiftSlot_SetRegionName_Patch
        {
            public static void Postfix(AgentGiftSlot __instance)
            {
                if (!IsEnabled || __instance == null) return;
                EnhanceText(__instance.RegionName, MinFontSize, MinFontSize - 2, ScaleMultiplier, true, true);
            }
        }

        /// <summary>
        /// インベントリ（装備変更）画面右側の職員情報・装着中 E.G.O ギフトリスト (InventoryAgentController.SetUI)
        /// </summary>
        [HarmonyPatch(typeof(InventoryAgentController), "SetUI")]
        public static class InventoryAgentController_SetUI_Patch
        {
            public static void Postfix(InventoryAgentController __instance)
            {
                if (!IsEnabled || __instance == null) return;

                int minFloor = MinFontSize;
                int giftSize = Mathf.Max(minFloor + 2, 20);

                EnhanceText(__instance.WeaponTitle, giftSize, minFloor, ScaleMultiplier, true, false);
                EnhanceText(__instance.DamageText, giftSize, minFloor, ScaleMultiplier, true, true);
                EnhanceText(__instance.ArmorTitle, giftSize, minFloor, ScaleMultiplier, true, false);
                EnhanceText(__instance.SubEquipTitle, giftSize, minFloor, ScaleMultiplier, true, true);

                if (__instance.SubEquipListParent != null)
                {
                    Transform parentTransform = __instance.SubEquipListParent.transform;
                    foreach (Transform child in parentTransform)
                    {
                        if (child == null) continue;
                        InventoryAttachmentUnit unit = child.GetComponent<InventoryAttachmentUnit>();
                        if (unit == null || unit.text == null) continue;

                        Text t = unit.text;
                        t.resizeTextForBestFit = false;
                        t.horizontalOverflow = HorizontalWrapMode.Wrap;
                        t.verticalOverflow = VerticalWrapMode.Overflow;
                        EnhanceText(t, giftSize, minFloor, ScaleMultiplier, true, false, 1.12f);

                        float prefH = t.preferredHeight + 6f;
                        RectTransform textRt = t.rectTransform;
                        if (textRt != null && textRt.sizeDelta.y < prefH)
                        {
                            textRt.sizeDelta = new Vector2(textRt.sizeDelta.x, prefH);
                        }

                        RectTransform unitRt = child.GetComponent<RectTransform>();
                        if (unitRt != null && unitRt.sizeDelta.y < prefH)
                        {
                            unitRt.sizeDelta = new Vector2(unitRt.sizeDelta.x, prefH);
                        }

                        LayoutElement le = child.GetComponent<LayoutElement>();
                        if (le != null && le.preferredHeight < prefH)
                        {
                            le.preferredHeight = prefH;
                            le.minHeight = prefH;
                        }
                    }

                    VerticalLayoutGroup vlg = parentTransform.GetComponent<VerticalLayoutGroup>();
                    if (vlg != null)
                    {
                        vlg.enabled = false;
                        vlg.enabled = true;
                    }
                    RectTransform parentRt = parentTransform.GetComponent<RectTransform>();
                    if (parentRt != null)
                    {
                        LayoutRebuilder.ForceRebuildLayoutImmediate(parentRt);
                    }
                }
            }
        }

        /// <summary>
        /// インベントリ（装備一覧）の武器・防具詳細ツールチップ (特殊能力説明 Tooltip_Middle & 解説文 TooltipDesc)
        /// </summary>
        [HarmonyPatch(typeof(InventoryItemController), "OnClickDetailInfo", new Type[] { typeof(EquipmentTypeInfo) })]
        public static class InventoryItemController_OnClickDetailInfo_TypeInfo_Patch
        {
            public static void Postfix(InventoryItemController __instance)
            {
                EnhanceInventoryDetailTooltip(__instance);
            }
        }

        [HarmonyPatch(typeof(InventoryItemController), "OnClickDetailInfo", new Type[] { typeof(EquipmentModel) })]
        public static class InventoryItemController_OnClickDetailInfo_Model_Patch
        {
            public static void Postfix(InventoryItemController __instance)
            {
                EnhanceInventoryDetailTooltip(__instance);
            }
        }

        private static void EnhanceInventoryDetailTooltip(InventoryItemController controller)
        {
            if (!IsEnabled || controller == null) return;

            int minFloor = MinFontSize;
            int detailSize = Mathf.Max(minFloor + 3, 22);

            EnhanceText(controller.TooltipTitle_ItemName, detailSize + 2, minFloor + 2, ScaleMultiplier, true, false);

            if (controller.Tooltip_Middle != null)
            {
                controller.Tooltip_Middle.resizeTextForBestFit = false;
                controller.Tooltip_Middle.horizontalOverflow = HorizontalWrapMode.Wrap;
                controller.Tooltip_Middle.verticalOverflow = VerticalWrapMode.Overflow;
                EnhanceText(controller.Tooltip_Middle, detailSize, minFloor, ScaleMultiplier, true, false, 1.15f);
            }

            if (controller.TooltipDesc != null)
            {
                controller.TooltipDesc.resizeTextForBestFit = false;
                controller.TooltipDesc.horizontalOverflow = HorizontalWrapMode.Wrap;
                controller.TooltipDesc.verticalOverflow = VerticalWrapMode.Overflow;
                EnhanceText(controller.TooltipDesc, detailSize, minFloor, ScaleMultiplier, true, false, 1.15f);
            }

            if (controller.ToolTipControl != null && controller.ToolTipControl.transform.parent != null)
            {
                ContentSizeFitter csf = controller.ToolTipControl.transform.parent.GetComponent<ContentSizeFitter>();
                if (csf != null)
                {
                    csf.SetLayoutVertical();
                    csf.enabled = false;
                    csf.enabled = true;
                }
                LayoutRebuilder.ForceRebuildLayoutImmediate(controller.ToolTipControl);
            }
        }

        /// <summary>
        /// インベントリの武器・防具スロット自体の名称・攻撃力・速度・射程・耐性テキスト視認性向上
        /// </summary>
        [HarmonyPatch(typeof(InventoryWeaponSlot), "UpdateUI")]
        public static class InventoryWeaponSlot_UpdateUI_Patch
        {
            public static void Postfix(InventoryWeaponSlot __instance)
            {
                if (!IsEnabled || __instance == null) return;
                int minFloor = MinFontSize;
                EnhanceText(__instance.Name, minFloor + 2, minFloor, ScaleMultiplier, true, false);
                EnhanceText(__instance.DamageRange, minFloor + 2, minFloor, ScaleMultiplier, true, true);
                EnhanceText(__instance.AttackSpeed, minFloor, minFloor - 2, ScaleMultiplier, true, false);
                EnhanceText(__instance.Range, minFloor, minFloor - 2, ScaleMultiplier, true, false);
            }
        }

        [HarmonyPatch(typeof(InventoryArmorSlot), "UpdateUI")]
        public static class InventoryArmorSlot_UpdateUI_Patch
        {
            public static void Postfix(InventoryArmorSlot __instance)
            {
                if (!IsEnabled || __instance == null) return;
                int minFloor = MinFontSize;
                EnhanceText(__instance.Name, minFloor + 2, minFloor, ScaleMultiplier, true, false);
                if (__instance.DefenseInfo != null)
                {
                    for (int i = 0; i < __instance.DefenseInfo.Length; i++)
                    {
                        EnhanceText(__instance.DefenseInfo[i], minFloor, minFloor - 2, ScaleMultiplier, true, false);
                    }
                }
                if (__instance.DefenseFactor != null)
                {
                    for (int i = 0; i < __instance.DefenseFactor.Length; i++)
                    {
                        EnhanceText(__instance.DefenseFactor[i], minFloor, minFloor - 2, ScaleMultiplier, true, true);
                    }
                }
            }
        }

        // =========================================================================
        // 5. ローディング画面の文字切れ修正 ("システム初期化" が3文字で切れる問題など)
        // =========================================================================
        private static void FixLoadingScreenText(Text t)
        {
            if (t == null || !IsEnabled) return;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            if (t.resizeTextForBestFit)
            {
                int minLoadingSize = Mathf.Max(MinFontSize + 4, 24);
                if (t.resizeTextMinSize < minLoadingSize)
                {
                    t.resizeTextMinSize = minLoadingSize;
                }
            }
            RectTransform rt = t.rectTransform;
            if (rt != null && rt.sizeDelta.x > 0f && rt.sizeDelta.x < 900f)
            {
                rt.sizeDelta = new Vector2(Mathf.Max(rt.sizeDelta.x, 900f), rt.sizeDelta.y);
            }
            EnhancedTextCount++;
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
                        if (__instance.texts[i] != null)
                        {
                            FixLoadingScreenText(__instance.texts[i].text);
                        }
                    }
                }
                if (__instance.sefiras != null)
                {
                    for (int j = 0; j < __instance.sefiras.Length; j++)
                    {
                        if (__instance.sefiras[j] != null && __instance.sefiras[j].name != null)
                        {
                            __instance.sefiras[j].name.horizontalOverflow = HorizontalWrapMode.Overflow;
                            __instance.sefiras[j].name.verticalOverflow = VerticalWrapMode.Overflow;
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
                        FixLoadingScreenText(__instance.texts[i].text);
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
                        FixLoadingScreenText(__instance.texts[i].text);
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
                        FixLoadingScreenText(__instance.texts[i].text);
                        if (__instance.texts[i].bg != null)
                        {
                            RectTransform bgRt = __instance.texts[i].bg.GetComponent<RectTransform>();
                            if (bgRt != null)
                            {
                                Vector2 sd = bgRt.sizeDelta;
                                sd.x = __instance.texts[i].text.preferredWidth + 100f;
                                bgRt.sizeDelta = sd;
                            }
                        }
                    }
                }
            }
        }

        // =========================================================================
        // 6. 全般ローカライズテキスト＆マウスオーバーツールチップの極小化防止
        // =========================================================================
        [HarmonyPatch(typeof(LocalizeTextLoadScript), "SetText", new Type[0])]
        public static class LocalizeTextLoadScript_SetText_Patch
        {
            public static void Postfix(LocalizeTextLoadScript __instance)
            {
                if (!IsEnabled || __instance == null) return;
                Text t = __instance.Text;
                if (t == null) return;

                // BestFit により minSize = 10 等へ潰れて漢字が判読不能になるのを防止
                int generalMinFloor = Mathf.Max(MinFontSize - 2, 16);
                if (t.resizeTextForBestFit && t.resizeTextMinSize < generalMinFloor)
                {
                    t.resizeTextMinSize = generalMinFloor;
                }
                if (t.fontSize > 0 && t.fontSize < generalMinFloor)
                {
                    t.fontSize = generalMinFloor;
                }

                // ローディング画面用の ID が設定されている場合は横・縦 Overflow を確実に解放
                if (!string.IsNullOrEmpty(__instance.id) &&
                    (__instance.id.StartsWith("ContinueLoadingScene") ||
                     __instance.id.StartsWith("TitleEndScene") ||
                     __instance.id.StartsWith("StoryEndScene") ||
                     __instance.id.StartsWith("DayEndScene")))
                {
                    FixLoadingScreenText(t);
                }
            }
        }

        [HarmonyPatch(typeof(OverlayManager), "SetText")]
        public static class OverlayManager_SetText_Patch
        {
            public static void Postfix(OverlayManager __instance)
            {
                if (!IsEnabled || __instance == null || __instance.OverlayText == null || __instance.OverlayBox == null)
                    return;

                int overlayMin = Mathf.Max(MinFontSize + 4, 24);
                EnhanceText(__instance.OverlayText, overlayMin, overlayMin, ScaleMultiplier, true, false, 1.12f);

                float preferredWidth = __instance.OverlayText.preferredWidth;
                float preferredHeight = __instance.OverlayText.preferredHeight;
                float maxW = Mathf.Max(__instance.MaxWidth, 380f);
                float num = Mathf.Min(preferredWidth, maxW * 2f) + 10f;

                RectTransform textRect = __instance.OverlayText.rectTransform;
                if (textRect != null)
                {
                    textRect.sizeDelta = new Vector2(num, preferredHeight);
                }
                __instance.OverlayBox.sizeDelta = new Vector2(
                    num * 0.5f + __instance.VerticalSpace * 2f,
                    preferredHeight * 0.5f + __instance.VerticalSpace * 2f);
                __instance.SetPosition(initial: true);
            }
        }
    }
}
