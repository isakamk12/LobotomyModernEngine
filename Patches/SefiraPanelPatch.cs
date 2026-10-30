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
    /// </summary>
    [HarmonyPatch(typeof(SefiraPanel.CreaturePortrait))]
    public static class SefiraPanelPatch
    {
        private static Sprite _cachedNoDataSprite;
        private static readonly Dictionary<string, Sprite> PortraitCache = new Dictionary<string, Sprite>();

        // 各スロットごとの前回状態キャッシュ（世代管理対応）
        private static readonly Dictionary<int, SlotCache> Cache = new Dictionary<int, SlotCache>();

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
            if (!PortraitCache.TryGetValue(path, out var sprite))
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

            // 観測状態のハッシュ値を計算（変化したかどうかの判定）
            int currentHash = ComputeObserveHash(___creature);
            if (slot.Initialized && slot.LastObserveStateHash == currentHash)
            {
                // 観測状態に変化がなければ、毎フレームの Resources.Load も Canvas Dirty 代入も完全スキップ！
                return false;
            }

            slot.LastObserveStateHash = currentHash;
            slot.Initialized = true;

            // 変化時のみキャッシュを利用して安全に更新
            ApplyPortraitUpdate(__instance, ___creature, ___sefiraColor);
            return false;
        }

        private static int ComputeObserveHash(CreatureModel creature)
        {
            if (creature.observeInfo == null) return 0;
            if (creature.metaInfo.creatureWorkType == CreatureWorkType.KIT)
            {
                return creature.metaInfo.creatureKitType == CreatureKitType.ONESHOT
                    ? creature.observeInfo.totalKitUseCount
                    : (int)creature.observeInfo.totalKitUseTime;
            }
            return creature.observeInfo.GetObserveState(CreatureModel.regionName[0]) ? 1 : 0;
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
