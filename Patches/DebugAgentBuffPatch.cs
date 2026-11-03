using System;
using System.Collections.Generic;
using HarmonyLib;
using Customizing;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// デバッグ・検証時間短縮用：初期職員および新規雇用職員のランク5(EX / ALL 100)強化パッチ。
    /// - 新規雇用・デフォルト職員のステータスを最初からランク5(HP/精神/作業/正義 = 各100)にする。
    /// - 既存職員の元ステータスは完全バックアップされ、F8キーまたはF9モニターで即座にバニラ値へ復元可能。
    /// </summary>
    public static class DebugAgentBuffPatch
    {
        private class OriginalStatState
        {
            public int hp;
            public int mental;
            public int work;
            public int battle;
        }

        private static readonly Dictionary<long, OriginalStatState> _originalStats = new Dictionary<long, OriginalStatState>();

        public static bool IsCurrentlyApplied { get; private set; }
        public static int BuffedAgentCount { get; private set; }

        public const int DEBUG_RANK5_STAT_VALUE = 100;

        /// <summary>
        /// 1. デフォルトステータスの底上げ (新規雇用時の基礎ステータス & 初期職員)
        /// </summary>
        [HarmonyPatch(typeof(AgentModel), "GetDefaultStat")]
        public static class AgentModel_GetDefaultStat_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(ref WorkerPrimaryStat __result)
            {
                if (Plugin.EnableDebugStarterGearBuff == null || !Plugin.EnableDebugStarterGearBuff.Value)
                    return;

                if (!IsCurrentlyApplied)
                    return;

                if (__result != null)
                {
                    __result.hp = DEBUG_RANK5_STAT_VALUE;
                    __result.mental = DEBUG_RANK5_STAT_VALUE;
                    __result.work = DEBUG_RANK5_STAT_VALUE;
                    __result.battle = DEBUG_RANK5_STAT_VALUE;
                }
            }
        }

        /// <summary>
        /// 2. 新規雇用確定時のステータス補正 (雇用画面でボーナスが0の場合でも確実にランク5化)
        /// </summary>
        [HarmonyPatch(typeof(CustomizingWindow), "SetAgentStatBonus")]
        public static class CustomizingWindow_SetAgentStatBonus_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(AgentModel agent)
            {
                if (Plugin.EnableDebugStarterGearBuff == null || !Plugin.EnableDebugStarterGearBuff.Value)
                    return;

                if (!IsCurrentlyApplied || agent == null || agent.primaryStat == null)
                    return;

                // バックアップ
                BackupAgentIfNeeded(agent);

                // 各ステータスを最低100へ底上げ
                if (agent.primaryStat.hp < DEBUG_RANK5_STAT_VALUE) agent.primaryStat.hp = DEBUG_RANK5_STAT_VALUE;
                if (agent.primaryStat.mental < DEBUG_RANK5_STAT_VALUE) agent.primaryStat.mental = DEBUG_RANK5_STAT_VALUE;
                if (agent.primaryStat.work < DEBUG_RANK5_STAT_VALUE) agent.primaryStat.work = DEBUG_RANK5_STAT_VALUE;
                if (agent.primaryStat.battle < DEBUG_RANK5_STAT_VALUE) agent.primaryStat.battle = DEBUG_RANK5_STAT_VALUE;

                agent.hp = agent.maxHp;
                agent.mental = agent.maxMental;
                agent.UpdateTitle(5);
            }
        }

        private static void BackupAgentIfNeeded(AgentModel agent)
        {
            if (agent == null || agent.primaryStat == null) return;
            if (!_originalStats.ContainsKey(agent.instanceId))
            {
                _originalStats[agent.instanceId] = new OriginalStatState
                {
                    hp = agent.primaryStat.hp,
                    mental = agent.primaryStat.mental,
                    work = agent.primaryStat.work,
                    battle = agent.primaryStat.battle
                };
            }
        }

        /// <summary>
        /// 既存の全職員に対してランク5強化を一括適用する
        /// </summary>
        public static void ApplyBuffToAllAgents()
        {
            try
            {
                if (AgentManager.instance == null) return;
                var list = AgentManager.instance.GetAgentList();
                if (list == null) return;

                int count = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var agent = list[i];
                    if (agent == null || agent.primaryStat == null) continue;

                    BackupAgentIfNeeded(agent);

                    if (agent.primaryStat.hp < DEBUG_RANK5_STAT_VALUE) agent.primaryStat.hp = DEBUG_RANK5_STAT_VALUE;
                    if (agent.primaryStat.mental < DEBUG_RANK5_STAT_VALUE) agent.primaryStat.mental = DEBUG_RANK5_STAT_VALUE;
                    if (agent.primaryStat.work < DEBUG_RANK5_STAT_VALUE) agent.primaryStat.work = DEBUG_RANK5_STAT_VALUE;
                    if (agent.primaryStat.battle < DEBUG_RANK5_STAT_VALUE) agent.primaryStat.battle = DEBUG_RANK5_STAT_VALUE;

                    agent.hp = agent.maxHp;
                    agent.mental = agent.maxMental;
                    agent.UpdateTitle(5);
                    count++;
                }

                BuffedAgentCount = count;
                IsCurrentlyApplied = true;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError("[LME DebugAgentBuff] Apply error: " + ex.Message);
            }
        }

        /// <summary>
        /// 全職員のステータスを退避しておいたバニラ数値へ完全復元する
        /// </summary>
        public static void RevertAllAgents()
        {
            try
            {
                if (AgentManager.instance == null) return;
                var list = AgentManager.instance.GetAgentList();
                if (list == null) return;

                for (int i = 0; i < list.Count; i++)
                {
                    var agent = list[i];
                    if (agent == null || agent.primaryStat == null) continue;

                    if (_originalStats.TryGetValue(agent.instanceId, out var orig))
                    {
                        agent.primaryStat.hp = orig.hp;
                        agent.primaryStat.mental = orig.mental;
                        agent.primaryStat.work = orig.work;
                        agent.primaryStat.battle = orig.battle;

                        agent.hp = agent.maxHp;
                        agent.mental = agent.maxMental;
                        agent.UpdateTitle(agent.level);
                    }
                }

                BuffedAgentCount = 0;
                IsCurrentlyApplied = false;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError("[LME DebugAgentBuff] Revert error: " + ex.Message);
            }
        }

        /// <summary>
        /// トグル切替
        /// </summary>
        public static void Toggle()
        {
            if (IsCurrentlyApplied)
            {
                RevertAllAgents();
            }
            else
            {
                ApplyBuffToAllAgents();
            }
        }

        public static void ClearCache()
        {
            _originalStats.Clear();
            BuffedAgentCount = 0;
        }

        public static void SyncWithConfig()
        {
            bool shouldEnable = Plugin.EnableDebugStarterGearBuff != null && Plugin.EnableDebugStarterGearBuff.Value;
            if (shouldEnable)
            {
                ApplyBuffToAllAgents();
            }
            else
            {
                RevertAllAgents();
            }
        }
    }
}
