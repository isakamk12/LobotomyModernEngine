using System.Collections.Generic;
using System.Collections.ObjectModel;
using HarmonyLib;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// v0.5.0 / v0.7.4: AgentManager.GetAgentList / GetAgentSpareList および OfficerManager.GetOfficerList が
    /// 毎回の呼び出しで list.AsReadOnly() による ReadOnlyCollection ラッパーをヒープ確保する問題を解消する。
    /// 
    /// 【不変性・安全性検証 (Assembly-CSharp.dll 実機解析)】
    /// バニラの元メソッド自体が `return agentList.AsReadOnly();` (`ReadOnlyCollection<T>`) を返しており、
    /// 最初から変更不可能なラッパー（呼び出し側が Add/Remove すると NotSupportedException を投げる設計）となっている。
    /// また ReadOnlyCollection<T> は内部の List<T> 参照をラップするビューであるため、
    /// agentList に要素が追加・削除されてもラッパーを再生成する必要はなく、
    /// AgentManager.InitValues() / OfficerManager.Init() で内部 List インスタンス自体が差し替わった時のみ
    /// ReferenceEquals チェックによって自動的に新しい ReadOnlyCollection ラッパーへ更新される。
    /// </summary>
    public static class WorkerListCachePatch
    {
        private static List<AgentModel> _lastAgentListRef;
        private static ReadOnlyCollection<AgentModel> _cachedAgentReadOnly;

        private static List<AgentModel> _lastAgentSpareListRef;
        private static ReadOnlyCollection<AgentModel> _cachedAgentSpareReadOnly;

        private static List<OfficerModel> _lastOfficerListRef;
        private static ReadOnlyCollection<OfficerModel> _cachedOfficerReadOnly;

        public static long AvoidedReadOnlyAllocations { get; private set; }

        public static void ClearCache()
        {
            _lastAgentListRef = null;
            _cachedAgentReadOnly = null;
            _lastAgentSpareListRef = null;
            _cachedAgentSpareReadOnly = null;
            _lastOfficerListRef = null;
            _cachedOfficerReadOnly = null;
        }

        [HarmonyPatch(typeof(AgentManager), "GetAgentList")]
        public static class AgentManager_GetAgentList_Patch
        {
            public static bool Prefix(List<AgentModel> ___agentList, ref IList<AgentModel> __result)
            {
                if (!Plugin.EnableWorkerListCache.Value || ___agentList == null)
                {
                    return true;
                }

                if (! object.ReferenceEquals(___agentList, _lastAgentListRef) || _cachedAgentReadOnly == null)
                {
                    _lastAgentListRef = ___agentList;
                    _cachedAgentReadOnly = ___agentList.AsReadOnly();
                }
                else
                {
                    AvoidedReadOnlyAllocations++;
                }

                __result = _cachedAgentReadOnly;
                return false;
            }
        }

        [HarmonyPatch(typeof(AgentManager), "GetAgentSpareList")]
        public static class AgentManager_GetAgentSpareList_Patch
        {
            public static bool Prefix(AgentManager __instance, ref IList<AgentModel> __result)
            {
                if (!Plugin.EnableWorkerListCache.Value || __instance.agentListSpare == null)
                {
                    return true;
                }

                if (!object.ReferenceEquals(__instance.agentListSpare, _lastAgentSpareListRef) || _cachedAgentSpareReadOnly == null)
                {
                    _lastAgentSpareListRef = __instance.agentListSpare;
                    _cachedAgentSpareReadOnly = __instance.agentListSpare.AsReadOnly();
                }
                else
                {
                    AvoidedReadOnlyAllocations++;
                }

                __result = _cachedAgentSpareReadOnly;
                return false;
            }
        }

        [HarmonyPatch(typeof(OfficerManager), "GetOfficerList")]
        public static class OfficerManager_GetOfficerList_Patch
        {
            public static bool Prefix(List<OfficerModel> ___officerList, ref IList<OfficerModel> __result)
            {
                if (!Plugin.EnableWorkerListCache.Value || ___officerList == null)
                {
                    return true;
                }

                if (!object.ReferenceEquals(___officerList, _lastOfficerListRef) || _cachedOfficerReadOnly == null)
                {
                    _lastOfficerListRef = ___officerList;
                    _cachedOfficerReadOnly = ___officerList.AsReadOnly();
                }
                else
                {
                    AvoidedReadOnlyAllocations++;
                }

                __result = _cachedOfficerReadOnly;
                return false;
            }
        }
    }
}
