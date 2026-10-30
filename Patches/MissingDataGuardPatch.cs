using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// セーブデータや図鑑データ内にMOD由来の未定義アブノーマリティIDや装備IDが存在した際、
    /// NullReferenceException でゲームの初期化がクラッシュし「観測情報画面でフリーズする」
    /// 悪名高いバニラの致命的バグを完全防止する安全ガードパッチ。
    /// ガードしたIDと回数を診断用に追跡・記録する。
    /// </summary>
    public static class MissingDataGuardPatch
    {
        public static int GuardedEquipmentCount { get; private set; } = 0;
        public static int GuardedCreatureCount { get; private set; } = 0;
        public static readonly HashSet<int> GuardedEquipmentIds = new HashSet<int>();
        public static readonly HashSet<long> GuardedCreatureIds = new HashSet<long>();

        /// <summary>
        /// 存在しない装備IDをロードしようとした際にクラッシュせず安全にスキップ
        /// </summary>
        [HarmonyPatch(typeof(InventoryModel), "CreateEquipment", new Type[] { typeof(int), typeof(long) })]
        public static class InventoryModel_CreateEquipment_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(int id, long instanceId, ref EquipmentModel __result, List<EquipmentModel> ____equipList)
            {
                EquipmentTypeInfo data = EquipmentTypeList.instance != null ? EquipmentTypeList.instance.GetData(id) : null;
                if (data == null)
                {
                    GuardedEquipmentCount++;
                    if (!GuardedEquipmentIds.Contains(id))
                    {
                        GuardedEquipmentIds.Add(id);
                        Plugin.Logger.LogWarning($"[MissingDataGuard] Ignored undefined MOD equipment ID: {id}");
                    }
                    __result = null;
                    return false;
                }

                if (____equipList != null)
                {
                    int count = 0;
                    for (int i = 0; i < ____equipList.Count; i++)
                    {
                        if (____equipList[i] != null && ____equipList[i].metaInfo != null && ____equipList[i].metaInfo.id == id)
                        {
                            count++;
                        }
                    }
                    if (count >= data.MaxNum)
                    {
                        __result = null;
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>
        /// 観測情報初期化時に、バニラに存在しないアブノーマリティIDなら例外を出さずにスキップ
        /// </summary>
        [HarmonyPatch(typeof(CreatureObserveInfoModel), "InitData")]
        public static class CreatureObserveInfoModel_InitData_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(CreatureObserveInfoModel __instance)
            {
                if (CreatureTypeList.instance == null) return false;
                var meta = CreatureTypeList.instance.GetData(__instance.creatureTypeId);
                if (meta == null || meta.observeData == null)
                {
                    GuardedCreatureCount++;
                    if (!GuardedCreatureIds.Contains(__instance.creatureTypeId))
                    {
                        GuardedCreatureIds.Add(__instance.creatureTypeId);
                        Plugin.Logger.LogWarning($"[MissingDataGuard] Ignored undefined MOD creature ID in observe data: {__instance.creatureTypeId}");
                    }
                    return false;
                }
                return true;
            }
        }

        /// <summary>
        /// 図鑑（Codex）が参照する観測情報リストから、バニラに存在しないMODアブノーマリティを除外
        /// </summary>
        [HarmonyPatch(typeof(CreatureManager), "GetObserveInfoList")]
        public static class CreatureManager_GetObserveInfoList_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(ref List<CreatureObserveInfoModel> __result)
            {
                if (__result == null || CreatureTypeList.instance == null) return;
                __result.RemoveAll(x => x == null || CreatureTypeList.instance.GetData(x.creatureTypeId) == null);
            }
        }

        /// <summary>
        /// IsMaxObserved の null ガード
        /// </summary>
        [HarmonyPatch(typeof(CreatureObserveInfoModel), "IsMaxObserved")]
        public static class CreatureObserveInfoModel_IsMaxObserved_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(CreatureObserveInfoModel __instance, CreatureTypeInfo ____metaInfo, ref bool __result)
            {
                if (____metaInfo == null)
                {
                    __result = false;
                    return false;
                }
                return true;
            }
        }
    }
}
