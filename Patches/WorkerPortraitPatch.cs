using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// WorkerPortraitSetter.LateUpdate の毎フレーム全再構築スパム抑止パッチ
    /// バニラでは職員ポートレートUIが表示されている間、毎フレーム LateUpdate() で SetWorker(model) を呼び出し、
    /// 毎フレーム3個の new List<EGOgiftModel>() アロケーションと20部位以上の UI Image 再設定を繰り返していた。
    /// 本パッチでは WorkerModel の表示状態ハッシュ（装備・ギフト・顔・部門・生存/パニック）を計算し、
    /// 変化がないフレームは LateUpdate での再構築を完全スキップする。
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

        public static long AvoidedPortraitRebuilds = 0;

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

        private static int ComputeWorkerVisualHash(WorkerPortraitSetter setter, WorkerModel worker)
        {
            if (worker == null) return 0;

            unchecked
            {
                int hash = 17;
                hash = hash * 31 + (worker.IsDead() ? 1 : 0);
                hash = hash * 31 + (worker.IsPanic() ? 1 : 0);
                hash = hash * 31 + (setter.WeaponSet ? 1 : 0);

                var equip = worker.Equipment;
                if (equip != null)
                {
                    int armorId = (equip.armor != null && equip.armor.metaInfo != null) ? equip.armor.metaInfo.id : -1;
                    int weaponId = (equip.weapon != null && equip.weapon.metaInfo != null) ? equip.weapon.metaInfo.id : -1;
                    hash = hash * 31 + armorId;
                    hash = hash * 31 + weaponId;

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

                var spriteData = worker.spriteData;
                if (spriteData != null)
                {
                    hash = hash * 31 + (spriteData.Eye != null ? spriteData.Eye.GetInstanceID() : 0);
                    hash = hash * 31 + (spriteData.EyeBrow != null ? spriteData.EyeBrow.GetInstanceID() : 0);
                    hash = hash * 31 + (spriteData.Mouth != null ? spriteData.Mouth.GetInstanceID() : 0);
                    hash = hash * 31 + (spriteData.FrontHair != null ? spriteData.FrontHair.GetInstanceID() : 0);
                    hash = hash * 31 + (spriteData.RearHair != null ? spriteData.RearHair.GetInstanceID() : 0);
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
        }

        [HarmonyPatch("LateUpdate")]
        [HarmonyPrefix]
        public static bool Prefix_LateUpdate(WorkerPortraitSetter __instance, WorkerModel ___model)
        {
            if (!Plugin.EnableWorkerPortraitOptimization.Value) return true;
            if (___model == null) return false;

            var state = GetOrCreateState(__instance);
            int currentWorkerId = (int)___model.instanceId;
            int currentHash = ComputeWorkerVisualHash(__instance, ___model);

            if (state.Initialized && state.LastWorkerInstanceId == currentWorkerId && state.LastStateHash == currentHash)
            {
                AvoidedPortraitRebuilds++;
                return false; // 装備・ギフト・顔・部門に変化がないため毎フレームの SetWorker 再構築をスキップ
            }

            return true;
        }
    }
}
