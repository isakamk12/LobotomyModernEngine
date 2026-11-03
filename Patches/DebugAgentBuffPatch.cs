using System;
using System.Collections.Generic;
using HarmonyLib;
using Customizing;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// デバッグ・検証時間短縮用：初期職員および新規雇用職員のランク5(EX / ALL 100)強化パッチ。
    /// - 新規雇用・デフォルト職員のステータスを最初からランク5(HP/精神/作業/正義 = 各100)にする。
    /// - 既存職員および控え(Spare)職員の元ステータスは完全バックアップされ、F8キーまたはF9モニターで即座にバニラ値へ復元可能。
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
        /// 1. デフォルトステータスの底上げ (新規雇用画面や初期職員の生成時)
        /// </summary>
        [HarmonyPatch(typeof(AgentModel), "GetDefaultStat")]
        public static class AgentModel_GetDefaultStat_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(ref WorkerPrimaryStat __result)
            {
                if (Plugin.EnableDebugStarterGearBuff == null || !Plugin.EnableDebugStarterGearBuff.Value)
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
        /// 2. 新規雇用確定時・強化時のステータス補正 (雇用画面でボーナスが0の場合でも確実にランク5化)
        /// </summary>
        [HarmonyPatch(typeof(CustomizingWindow), "SetAgentStatBonus")]
        public static class CustomizingWindow_SetAgentStatBonus_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(AgentModel agent)
            {
                if (Plugin.EnableDebugStarterGearBuff == null || !Plugin.EnableDebugStarterGearBuff.Value)
                    return;

                if (agent == null || agent.primaryStat == null)
                    return;

                BuffSingleAgent(agent);
                IsCurrentlyApplied = true;
            }
        }

        /// <summary>
        /// 3. セーブデータからの職員ロード完了時や報酬職員の追加時にも確実に同期
        /// </summary>
        [HarmonyPatch(typeof(AgentManager), "LoadData")]
        public static class AgentManager_LoadData_Patch
        {
            [HarmonyPostfix]
            public static void Postfix()
            {
                SyncWithConfig();
            }
        }

        /// <summary>
        /// 4. ステージ開始準備(DeployUI表示前)に配置済み・控えの全職員を同期
        /// </summary>
        [HarmonyPatch(typeof(GameManager), "StartStage")]
        public static class GameManager_StartStage_Patch
        {
            [HarmonyPrefix]
            public static void Prefix()
            {
                SyncWithConfig();
            }
        }

        private static void BackupAgentIfNeeded(AgentModel agent)
        {
            if (agent == null || agent.primaryStat == null) return;
            if (!_originalStats.ContainsKey(agent.instanceId))
            {
                // もし GetDefaultStat 経由で既に100になっている新規職員の場合はバニラ初期値(15)を退避値とする
                bool isFreshHundred = agent.primaryStat.hp == DEBUG_RANK5_STAT_VALUE &&
                                      agent.primaryStat.mental == DEBUG_RANK5_STAT_VALUE &&
                                      agent.primaryStat.work == DEBUG_RANK5_STAT_VALUE &&
                                      agent.primaryStat.battle == DEBUG_RANK5_STAT_VALUE &&
                                      agent.continuousServiceDay <= 1;

                int fallback = 15;
                try
                {
                    if (MissionManager.instance != null && MissionManager.instance.ExistsFinishedBossMission(SefiraEnum.HOD))
                    {
                        fallback = 45;
                    }
                }
                catch { }

                _originalStats[agent.instanceId] = new OriginalStatState
                {
                    hp = isFreshHundred ? fallback : agent.primaryStat.hp,
                    mental = isFreshHundred ? fallback : agent.primaryStat.mental,
                    work = isFreshHundred ? fallback : agent.primaryStat.work,
                    battle = isFreshHundred ? fallback : agent.primaryStat.battle
                };
            }
        }

        private static bool BuffSingleAgent(AgentModel agent)
        {
            if (agent == null || agent.primaryStat == null) return false;

            BackupAgentIfNeeded(agent);

            if (agent.primaryStat.hp < DEBUG_RANK5_STAT_VALUE) agent.primaryStat.hp = DEBUG_RANK5_STAT_VALUE;
            if (agent.primaryStat.mental < DEBUG_RANK5_STAT_VALUE) agent.primaryStat.mental = DEBUG_RANK5_STAT_VALUE;
            if (agent.primaryStat.work < DEBUG_RANK5_STAT_VALUE) agent.primaryStat.work = DEBUG_RANK5_STAT_VALUE;
            if (agent.primaryStat.battle < DEBUG_RANK5_STAT_VALUE) agent.primaryStat.battle = DEBUG_RANK5_STAT_VALUE;

            agent.hp = agent.maxHp;
            agent.mental = agent.maxMental;
            try
            {
                agent.UpdateTitle(5);
                if (Notice.instance != null)
                {
                    Notice.instance.Send(NoticeName.InitAgent, agent);
                }
            }
            catch { }

            return true;
        }

        private static void RevertSingleAgent(AgentModel agent)
        {
            if (agent == null || agent.primaryStat == null) return;

            if (_originalStats.TryGetValue(agent.instanceId, out var orig))
            {
                agent.primaryStat.hp = orig.hp;
                agent.primaryStat.mental = orig.mental;
                agent.primaryStat.work = orig.work;
                agent.primaryStat.battle = orig.battle;

                agent.hp = agent.maxHp;
                agent.mental = agent.maxMental;
                try
                {
                    agent.UpdateTitle(agent.level);
                    if (Notice.instance != null)
                    {
                        Notice.instance.Send(NoticeName.InitAgent, agent);
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// 既存の全職員（配置済み＋控え）に対してランク5強化を一括適用する
        /// </summary>
        public static void ApplyBuffToAllAgents()
        {
            try
            {
                IsCurrentlyApplied = true;
                if (AgentManager.instance == null) return;

                int count = 0;
                var activeList = AgentManager.instance.GetAgentList();
                if (activeList != null)
                {
                    for (int i = 0; i < activeList.Count; i++)
                    {
                        if (BuffSingleAgent(activeList[i])) count++;
                    }
                }

                var spareList = AgentManager.instance.GetAgentSpareList();
                if (spareList != null)
                {
                    for (int i = 0; i < spareList.Count; i++)
                    {
                        if (BuffSingleAgent(spareList[i])) count++;
                    }
                }

                BuffedAgentCount = count;
            }
            catch (Exception ex)
            {
                if (Plugin.Logger != null)
                    Plugin.Logger.LogError("[LME DebugAgentBuff] Apply error: " + ex.Message);
            }
        }

        /// <summary>
        /// 全職員（配置済み＋控え）のステータスを退避しておいたバニラ数値へ完全復元する
        /// </summary>
        public static void RevertAllAgents()
        {
            try
            {
                IsCurrentlyApplied = false;
                BuffedAgentCount = 0;
                if (AgentManager.instance == null) return;

                var activeList = AgentManager.instance.GetAgentList();
                if (activeList != null)
                {
                    for (int i = 0; i < activeList.Count; i++)
                    {
                        RevertSingleAgent(activeList[i]);
                    }
                }

                var spareList = AgentManager.instance.GetAgentSpareList();
                if (spareList != null)
                {
                    for (int i = 0; i < spareList.Count; i++)
                    {
                        RevertSingleAgent(spareList[i]);
                    }
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Logger != null)
                    Plugin.Logger.LogError("[LME DebugAgentBuff] Revert error: " + ex.Message);
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
