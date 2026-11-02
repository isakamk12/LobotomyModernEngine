using System;
using System.Collections.Generic;
using System.Diagnostics;
using Customizing;
using HarmonyLib;
using UnityEngine;
using WorkerSprite;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// WorkerPortraitSetter.LateUpdate の毎フレーム全再構築スパム抑止パッチ
    /// 
    /// 【Dirtyフラグ完全性 & 負荷計測設計 (v0.7.5)】
    /// - バニラの SetWorker(worker) が参照する表示状態を網羅したハッシュを計算し、変化がないフレームのみ再構築をスキップ。
    /// - 状態監視（ハッシュ計算）自体の所要時間を Stopwatch で精密計測（マイクロ秒）し、
    ///   バニラ負荷に対して確実に軽量化が成立しているかを診断モニターへ提供。
    /// </summary>
    [HarmonyPatch(typeof(WorkerPortraitSetter))]
    public static class WorkerPortraitPatch
    {
        private class PortraitState
        {
            public WeakReference TargetRef;
            public int LastWorkerInstanceId = -1;
            public int LastStateHash = 0;
            public bool Initialized = false;
        }

        private static readonly Dictionary<int, PortraitState> Cache = new Dictionary<int, PortraitState>(64);
        private static readonly Stopwatch _watch = new Stopwatch();

        public static long AvoidedPortraitRebuilds = 0;
        public static long ExecutedPortraitRebuilds = 0;
        public static double LastHashCalcMicroseconds = 0.0;
        public static double AvgHashCalcMicroseconds = 0.0;
        private static double _hashTimeSum = 0.0;
        private static long _hashCalcCount = 0;

        public static void ClearCache()
        {
            Cache.Clear();
        }

        public static void PruneDeadReferences()
        {
            if (Cache.Count == 0) return;

            List<int> deadKeys = null;
            foreach (var kvp in Cache)
            {
                if (kvp.Value.TargetRef == null || !kvp.Value.TargetRef.IsAlive || (kvp.Value.TargetRef.Target as WorkerPortraitSetter) == null)
                {
                    if (deadKeys == null) deadKeys = new List<int>();
                    deadKeys.Add(kvp.Key);
                }
            }

            if (deadKeys != null)
            {
                for (int i = 0; i < deadKeys.Count; i++)
                {
                    Cache.Remove(deadKeys[i]);
                }
            }
        }

        private static PortraitState GetOrCreateState(WorkerPortraitSetter setter)
        {
            int id = setter.GetInstanceID();
            if (!Cache.TryGetValue(id, out var state) || state.TargetRef == null || !state.TargetRef.IsAlive || !object.ReferenceEquals(state.TargetRef.Target, setter))
            {
                state = new PortraitState { TargetRef = new WeakReference(setter) };
                Cache[id] = state;
            }
            return state;
        }

        private static int ColorToHash(Color c)
        {
            unchecked
            {
                int r = (int)(c.r * 255f) & 0xFF;
                int g = (int)(c.g * 255f) & 0xFF;
                int b = (int)(c.b * 255f) & 0xFF;
                int a = (int)(c.a * 255f) & 0xFF;
                return (r << 24) | (g << 16) | (b << 8) | a;
            }
        }

        private static int ComputeWorkerVisualHash(WorkerPortraitSetter setter, WorkerModel worker)
        {
            if (worker == null) return 0;

            unchecked
            {
                int hash = 17;
                // 1. 生存・パニック・制御不能状態・死因タイプ
                hash = hash * 31 + (worker.IsDead() ? 1 : 0);
                hash = hash * 31 + (worker.IsPanic() ? 1 : 0);
                hash = hash * 31 + (worker.CannotControll() ? 1 : 0);
                hash = hash * 31 + (int)worker.DeadType;
                hash = hash * 31 + (setter.WeaponSet ? 1 : 0);

                // 2. 装備（武器・防具・スクリプト・武器スプライト・全ギフトのID＋表示/非表示＋部位）
                var equip = worker.Equipment;
                if (equip != null)
                {
                    int armorId = (equip.armor != null && equip.armor.metaInfo != null) ? equip.armor.metaInfo.id : -1;
                    int armorSpriteId = (equip.armor != null && equip.armor.metaInfo != null) ? equip.armor.metaInfo.armorId : -1;
                    int weaponId = (equip.weapon != null && equip.weapon.metaInfo != null) ? equip.weapon.metaInfo.id : -1;
                    int weaponType = (equip.weapon != null && equip.weapon.metaInfo != null) ? (int)equip.weapon.metaInfo.weaponClassType : -1;
                    int weaponSpriteHash = (equip.weapon != null && equip.weapon.metaInfo != null && equip.weapon.metaInfo.sprite != null)
                        ? equip.weapon.metaInfo.sprite.GetHashCode() : 0;

                    hash = hash * 31 + armorId;
                    hash = hash * 31 + armorSpriteId;
                    hash = hash * 31 + weaponId;
                    hash = hash * 31 + weaponType;
                    hash = hash * 31 + weaponSpriteHash;

                    var gifts = equip.gifts;
                    if (gifts != null)
                    {
                        var added = gifts.addedGifts;
                        if (added != null)
                        {
                            hash = hash * 31 + added.Count;
                            for (int i = 0; i < added.Count; i++)
                            {
                                var g = added[i];
                                if (g != null && g.metaInfo != null)
                                {
                                    hash = hash * 31 + g.metaInfo.id;
                                    hash = hash * 31 + (gifts.GetDisplayState(g) ? 1 : 0);
                                }
                            }
                        }

                        var replaced = gifts.replacedGifts;
                        if (replaced != null)
                        {
                            hash = hash * 31 + replaced.Count;
                            for (int i = 0; i < replaced.Count; i++)
                            {
                                var g = replaced[i];
                                if (g != null && g.metaInfo != null)
                                {
                                    hash = hash * 31 + g.metaInfo.id;
                                    hash = hash * 31 + (gifts.GetDisplayState(g) ? 1 : 0);
                                }
                            }
                        }
                    }
                }

                // 3. 顔パーツ・髪色/瞳色・防具パーツの実スプライト参照
                var spriteData = worker.spriteData;
                if (spriteData != null)
                {
                    hash = hash * 31 + (spriteData.Eye != null ? spriteData.Eye.GetInstanceID() : 0);
                    hash = hash * 31 + (spriteData.EyeBrow != null ? spriteData.EyeBrow.GetInstanceID() : 0);
                    hash = hash * 31 + (spriteData.Mouth != null ? spriteData.Mouth.GetInstanceID() : 0);
                    hash = hash * 31 + (spriteData.FrontHair != null ? spriteData.FrontHair.GetInstanceID() : 0);
                    hash = hash * 31 + (spriteData.RearHair != null ? spriteData.RearHair.GetInstanceID() : 0);
                    hash = hash * 31 + ColorToHash(spriteData.EyeColor);
                    hash = hash * 31 + ColorToHash(spriteData.HairColor);

                    // 防具スプライト実体 (GetArmorData 更新検知)
                    hash = hash * 31 + (spriteData.Armor.Body != null ? spriteData.Armor.Body.GetInstanceID() : 0);
                    hash = hash * 31 + (spriteData.Armor.Coat_Back != null ? spriteData.Armor.Coat_Back.GetInstanceID() : 0);
                    hash = hash * 31 + (spriteData.Armor.Left_Hand != null ? spriteData.Armor.Left_Hand.GetInstanceID() : 0);
                    hash = hash * 31 + (spriteData.Armor.Right_Hand != null ? spriteData.Armor.Right_Hand.GetInstanceID() : 0);
                }

                // 4. 所属部門・セフィラ・連続勤務レベル（腕章シンボル）
                if (worker.currentSefira != null)
                {
                    hash = hash * 31 + worker.currentSefira.GetHashCode();
                }

                if (worker is AgentModel agent)
                {
                    var sefira = agent.GetCurrentSefira();
                    hash = hash * 31 + (sefira != null ? (int)sefira.sefiraEnum : -1);
                    hash = hash * 31 + agent.GetContinuousServiceLevel();
                }

                return hash;
            }
        }

        [HarmonyPatch("SetWorker", new Type[] { typeof(WorkerModel) })]
        [HarmonyPostfix]
        public static void Postfix_SetWorker(WorkerPortraitSetter __instance, WorkerModel worker)
        {
            if (!Plugin.EnableWorkerPortraitOptimization.Value || __instance == null) return;

            var state = GetOrCreateState(__instance);
            if (worker == null)
            {
                state.Initialized = false;
                state.LastWorkerInstanceId = -1;
                return;
            }

            state.LastWorkerInstanceId = (int)worker.instanceId;
            state.LastStateHash = ComputeWorkerVisualHash(__instance, worker);
            state.Initialized = true;
            ExecutedPortraitRebuilds++;
        }

        [HarmonyPatch("SetArmor", new Type[] { typeof(EquipmentTypeInfo) })]
        [HarmonyPostfix]
        public static void Postfix_SetArmor(WorkerPortraitSetter __instance)
        {
            if (__instance == null) return;
            var state = GetOrCreateState(__instance);
            state.Initialized = false;
        }

        [HarmonyPatch("SetCustomizing", new Type[] { typeof(AgentData), typeof(WorkerFaceType) })]
        [HarmonyPostfix]
        public static void Postfix_SetCustomizing(WorkerPortraitSetter __instance)
        {
            if (__instance == null) return;
            var state = GetOrCreateState(__instance);
            state.Initialized = false;
        }

        [HarmonyPatch("LateUpdate")]
        [HarmonyPrefix]
        public static bool Prefix_LateUpdate(WorkerPortraitSetter __instance, WorkerModel ___model)
        {
            if (!Plugin.EnableWorkerPortraitOptimization.Value) return true;
            if (___model == null) return false;

            _watch.Reset();
            _watch.Start();

            var state = GetOrCreateState(__instance);
            int currentWorkerId = (int)___model.instanceId;
            int currentHash = ComputeWorkerVisualHash(__instance, ___model);

            _watch.Stop();
            double us = (_watch.ElapsedTicks * 1000000.0) / Stopwatch.Frequency;
            LastHashCalcMicroseconds = us;
            _hashTimeSum += us;
            _hashCalcCount++;
            AvgHashCalcMicroseconds = _hashTimeSum / _hashCalcCount;

            if (state.Initialized && state.LastWorkerInstanceId == currentWorkerId && state.LastStateHash == currentHash)
            {
                AvoidedPortraitRebuilds++;
                return false; // 表示要素に変化がないため毎フレームの SetWorker 再構築をスキップ
            }

            return true;
        }
    }
}
