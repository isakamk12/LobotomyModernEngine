using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using WorkerSprite;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// 全職員の毎フレーム GetComponent<Animator>() 連打（職員50人で毎フレーム500回）を完全排除するパッチ
    /// WeakReference による世代管理・生存検証と定期クリーンアップを備えた硬化実装
    /// </summary>
    [HarmonyPatch(typeof(WorkerSpriteSetter))]
    public static class WorkerSpriteSetterPatch
    {
        private static readonly Dictionary<int, CachedData> Cache = new Dictionary<int, CachedData>();

        private class CachedData
        {
            public System.WeakReference TargetRef;
            public Animator Animator;
            public bool HasMove;
            public bool HasBattle;
            public bool HasBattleReady;
            public bool HasPanic;
            public bool Initialized;
        }

        public static void ClearCache()
        {
            Cache.Clear();
        }

        public static void PruneDeadReferences()
        {
            List<int> deadKeys = null;
            foreach (var kvp in Cache)
            {
                if (kvp.Value.TargetRef == null || !kvp.Value.TargetRef.IsAlive)
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

        [HarmonyPatch("LateUpdate")]
        [HarmonyPrefix]
        public static bool Prefix_LateUpdate(WorkerSpriteSetter __instance)
        {
            if (!Plugin.EnableWorkerSpriteCache.Value) return true;

            int id = __instance.GetInstanceID();
            if (!Cache.TryGetValue(id, out var data) || data.TargetRef == null || !data.TargetRef.IsAlive || !object.ReferenceEquals(data.TargetRef.Target, __instance))
            {
                data = new CachedData
                {
                    TargetRef = new System.WeakReference(__instance),
                    Animator = __instance.GetComponent<Animator>()
                };

                if (data.Animator != null)
                {
                    data.HasMove = AnimatorUtil.HasParameter(data.Animator, "Move");
                    data.HasBattle = AnimatorUtil.HasParameter(data.Animator, "Battle");
                    data.HasBattleReady = AnimatorUtil.HasParameter(data.Animator, "BattleReady");
                    data.HasPanic = AnimatorUtil.HasParameter(data.Animator, "Panic");
                }
                data.Initialized = true;
                Cache[id] = data;
            }

            var anim = data.Animator;
            if (anim == null) return false;

            var weaponGo = __instance.WeaponRenderer != null ? __instance.WeaponRenderer.gameObject : null;
            if (weaponGo == null) return false;

            if (data.HasMove)
            {
                bool isMove = anim.GetBool("Move");
                bool isCombatOrPanic = (data.HasBattle && anim.GetBool("Battle"))
                                    || (data.HasBattleReady && anim.GetBool("BattleReady"))
                                    || (data.HasPanic && anim.GetBool("Panic"));
                bool isUnique = anim.GetBool("UniqueBattleMove");

                if (isMove && isCombatOrPanic && !isUnique)
                {
                    if (!weaponGo.activeInHierarchy)
                    {
                        weaponGo.SetActive(true);
                    }
                }
                else if (weaponGo.activeInHierarchy)
                {
                    weaponGo.SetActive(false);
                }
            }
            else if (weaponGo.activeInHierarchy)
            {
                weaponGo.SetActive(false);
            }

            return false;
        }
    }
}
