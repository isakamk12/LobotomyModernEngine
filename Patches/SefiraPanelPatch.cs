using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// SefiraPanel の CreaturePortrait.UpdateCheck() が毎フレーム Resources.Load を呼び、
    /// かつ無条件で UI Canvas Dirty を引き起こす病巣を Dirty チェック＆キャッシュ化するパッチ
    /// 
    /// 【Dirtyフラグ完全性設計 (v0.7.4 監査硬化)】
    /// - 対象 CreatureModel の ID / インスタンス
    /// - 観測状態・ツール使用回数/時間・観測コスト
    /// - リスクレベル・portraitSrcForcely・部門カラー (sefiraColor)
    /// を全て含めたハッシュで比較し、バニラ SetCreature / SetEmpty と完全に整合する。
    /// </summary>
    [HarmonyPatch(typeof(SefiraPanel.CreaturePortrait))]
    public static class SefiraPanelPatch
    {
        private static Sprite _cachedNoDataSprite;
        private static readonly Dictionary<string, Sprite> PortraitCache = new Dictionary<string, Sprite>();

        // 各スロットごとの前回状態キャッシュ（世代管理対応）
        private static readonly Dictionary<int, SlotCache> Cache = new Dictionary<int, SlotCache>();

        public static long AvoidedSefiraPortraitUpdates = 0;

        private class SlotCache
        {
            public System.WeakReference TargetRef;
            public int LastObserveStateHash = -999;
            public bool Initialized;
        }

        public static Sprite GetNoDataSprite()
        {
            if (_cachedNoDataSprite == null)
            {
                _cachedNoDataSprite = Resources.Load<Sprite>("Sprites/Unit/creature/NoData");
            }
            return _cachedNoDataSprite;
        }

        public static Sprite GetPortraitSprite(string path)
        {
            if (string.IsNullOrEmpty(path)) return GetNoDataSprite();
            if (!PortraitCache.TryGetValue(path, out var sprite) || sprite == null)
            {
                sprite = Resources.Load<Sprite>(path);
                if (sprite != null) PortraitCache[path] = sprite;
            }
            return sprite ?? GetNoDataSprite();
        }

        public static void ClearCache()
        {
            Cache.Clear();
            PortraitCache.Clear();
            _cachedNoDataSprite = null;
        }

        [HarmonyPatch("SetEmpty")]
        [HarmonyPostfix]
        public static void Postfix_SetEmpty(SefiraPanel.CreaturePortrait __instance)
        {
            if (__instance == null) return;
            int id = __instance.GetHashCode();
            if (Cache.TryGetValue(id, out var slot))
            {
                slot.Initialized = false;
            }
        }

        [HarmonyPatch("UpdateCheck")]
        [HarmonyPrefix]
        public static bool Prefix_UpdateCheck(
            SefiraPanel.CreaturePortrait __instance,
            bool ___isInit,
            CreatureModel ___creature,
            Color ___sefiraColor)
        {
            if (!Plugin.EnableSefiraPanelOptimization.Value) return true;
            if (!___isInit || ___creature == null || ___creature.metaInfo == null) return true;

            int id = __instance.GetHashCode();
            if (!Cache.TryGetValue(id, out var slot) || slot.TargetRef == null || !slot.TargetRef.IsAlive || !object.ReferenceEquals(slot.TargetRef.Target, __instance))
            {
                slot = new SlotCache { TargetRef = new System.WeakReference(__instance) };
                Cache[id] = slot;
            }

            // アブノーマリティID・観測状態・カラーを含めた完全ハッシュ値を計算
            int currentHash = ComputeObserveHash(___creature, ___sefiraColor);
            if (slot.Initialized && slot.LastObserveStateHash == currentHash)
            {
                AvoidedSefiraPortraitUpdates++;
                // 観測状態・対象に変化がなければ、毎フレームの Resources.Load も Canvas Dirty 代入も完全スキップ
                return false;
            }

            slot.LastObserveStateHash = currentHash;
            slot.Initialized = true;

            // 変化時のみキャッシュを利用して安全に更新
            ApplyPortraitUpdate(__instance, ___creature, ___sefiraColor);
            return false;
        }

        private static int ComputeObserveHash(CreatureModel creature, Color sefiraColor)
        {
            if (creature == null || creature.metaInfo == null || creature.observeInfo == null) return 0;
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + (int)creature.instanceId;
                hash = hash * 31 + (int)creature.metadataId;
                hash = hash * 31 + (int)creature.metaInfo.GetRiskLevel();
                hash = hash * 31 + ((int)(sefiraColor.r * 255f) ^ ((int)(sefiraColor.g * 255f) << 8) ^ ((int)(sefiraColor.b * 255f) << 16));

                if (creature.metaInfo.creatureWorkType == CreatureWorkType.KIT)
                {
                    int observeCost = creature.observeInfo.GetObserveCost(CreatureModel.regionName[0]);
                    bool isObserved = creature.metaInfo.creatureKitType == CreatureKitType.ONESHOT
                        ? (creature.observeInfo.totalKitUseCount >= observeCost)
                        : ((int)creature.observeInfo.totalKitUseTime >= observeCost);
                    hash = hash * 31 + (isObserved ? 1 : 0);
                }
                else
                {
                    hash = hash * 31 + (creature.observeInfo.GetObserveState(CreatureModel.regionName[0]) ? 1 : 0);
                }
                return hash;
            }
        }

        private static void ApplyPortraitUpdate(SefiraPanel.CreaturePortrait portraitSlot, CreatureModel creature, Color sefiraColor)
        {
            string text = "Unknown";
            Sprite targetSprite = null;
            Color targetColor = sefiraColor;

            if (creature.metaInfo.creatureWorkType == CreatureWorkType.KIT)
            {
                int observeCost = creature.observeInfo.GetObserveCost(CreatureModel.regionName[0]);
                bool isObserved = creature.metaInfo.creatureKitType == CreatureKitType.ONESHOT
                    ? (creature.observeInfo.totalKitUseCount >= observeCost)
                    : ((int)creature.observeInfo.totalKitUseTime >= observeCost);

                if (isObserved)
                {
                    targetSprite = GetPortraitSprite(creature.metaInfo.portraitSrcForcely);
                    text = creature.metaInfo.riskLevelForce;
                    if (UIColorManager.instance != null)
                    {
                        targetColor = UIColorManager.instance.RiskLevelColor[(int)creature.metaInfo.GetRiskLevel()];
                    }
                }
                else
                {
                    targetSprite = GetNoDataSprite();
                    targetColor = sefiraColor;
                }
            }
            else if (creature.observeInfo != null && creature.observeInfo.GetObserveState(CreatureModel.regionName[0]))
            {
                targetSprite = GetPortraitSprite(creature.metaInfo.portraitSrcForcely);
                text = creature.metaInfo.riskLevelForce;
                if (UIColorManager.instance != null)
                {
                    targetColor = UIColorManager.instance.RiskLevelColor[(int)creature.metaInfo.GetRiskLevel()];
                }
            }
            else
            {
                targetSprite = GetNoDataSprite();
                targetColor = sefiraColor;
            }

            if (portraitSlot.portrait != null) portraitSlot.portrait.sprite = targetSprite;
            if (portraitSlot.riskLevel != null)
            {
                portraitSlot.riskLevel.text = text;
                portraitSlot.riskLevel.color = targetColor;
            }
            if (portraitSlot.riskColor != null)
            {
                foreach (MaskableGraphic g in portraitSlot.riskColor)
                {
                    if (g != null) g.color = targetColor;
                }
            }
        }
    }
}
