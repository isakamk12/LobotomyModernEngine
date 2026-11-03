using System;
using System.Diagnostics;
using HarmonyLib;
using LobotomyModernEngine.Core;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// ゲーム内主要CPU処理のリアルタイムプロファイラ計測パッチ群 (v0.8.4)
    /// - AgentModel / OfficerModel (WorkerAI) および CreatureModel / OrdealCreatureModel (CreatureAI) の全サブクラスを網羅。
    /// - 入れ子呼び出し (ParentScope / RecordLeaf) により、親処理の Inclusive 時間と Exclusive (自己) 時間を正確に分離。
    /// - WorkerAI の内部処理を 3a(Agent), 3b(Officer), 3c(UpdateBufState), 3d(UseSkill), 3d-1(OnEnterRoom), 3e(AddOverloadGague/Ordeal), 3f(OfficerNormalAI), 3g(SoundEffect) に細分化。
    /// - 最適化パッチ (UnitBufStatePatch / PathfindingBufferPatch) が Prefix で return false する場合でも正確に処理時間を計測できるよう、
    ///   計測 Prefix には [HarmonyPriority(Priority.First)] を設定して最優先で開始タイムスタンプを記録する。
    /// </summary>
    public static class ProfilerPatch
    {
        public struct ParentState
        {
            public long StartTimestamp;
            public long SavedChildTicks;
            public int GcGen0Before;
        }

        public struct LeafState
        {
            public long StartTimestamp;
            public int GcGen0Before;
        }

        // 1. 経路探索 (GraphAstar.SearchPath) - 葉ノード + スパイク条件特定ログ
        [HarmonyPatch(typeof(GraphAstar), "SearchPath")]
        public static class SearchPath_ProfilePatch
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            public static void Prefix(out LeafState __state)
            {
                __state.GcGen0Before = GC.CollectionCount(0);
                __state.StartTimestamp = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            public static void Postfix(MapNode startPoint, MapNode endPoint, bool isRabbit, PathResult __result, LeafState __state)
            {
                double ms = GameProfiler.RecordLeaf(GameProfiler.Pathfinding, __state.StartTimestamp);
                if (ms >= GameProfiler.SpikeLogThresholdMs)
                {
                    int gcDelta = GC.CollectionCount(0) - __state.GcGen0Before;
                    string startId = startPoint != null ? startPoint.GetId() : "null";
                    string endId = endPoint != null ? endPoint.GetId() : "null";
                    int edges = (__result != null && __result.pathEdges != null) ? __result.pathEdges.Length : -1;
                    GameProfiler.LogSpikeEvent(
                        "1.GraphAstar.SearchPath",
                        ms,
                        ms,
                        gcDelta,
                        string.Format("Start:{0} -> End:{1}, Edges:{2}, Rabbit:{3}", startId, endId, edges, isRabbit));
                }
            }
        }

        // 2-A. 索敵・近傍判定 (PassageObjectModel.GetEnteredTargets(MovableObjectNode)) - 葉ノード
        [HarmonyPatch(typeof(PassageObjectModel), "GetEnteredTargets", typeof(MovableObjectNode))]
        public static class GetEnteredTargetsWithExclude_ProfilePatch
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            public static void Prefix(out LeafState __state)
            {
                __state.GcGen0Before = GC.CollectionCount(0);
                __state.StartTimestamp = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            public static void Postfix(PassageObjectModel __instance, LeafState __state)
            {
                double ms = GameProfiler.RecordLeaf(GameProfiler.TargetScan, __state.StartTimestamp);
                if (ms >= GameProfiler.SpikeLogThresholdMs)
                {
                    int gcDelta = GC.CollectionCount(0) - __state.GcGen0Before;
                    string passageId = __instance != null ? __instance.GetId() : "null";
                    GameProfiler.LogSpikeEvent(
                        "2.Passage.GetEnteredTargets(exclude)",
                        ms,
                        ms,
                        gcDelta,
                        string.Format("Passage:{0}", passageId));
                }
            }
        }

        // 2-B. 索敵・近傍判定 (PassageObjectModel.GetEnteredTargets()) - 葉ノード
        [HarmonyPatch(typeof(PassageObjectModel), "GetEnteredTargets", new Type[0])]
        public static class GetEnteredTargetsNoArg_ProfilePatch
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            public static void Prefix(out LeafState __state)
            {
                __state.GcGen0Before = GC.CollectionCount(0);
                __state.StartTimestamp = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            public static void Postfix(PassageObjectModel __instance, LeafState __state)
            {
                double ms = GameProfiler.RecordLeaf(GameProfiler.TargetScan, __state.StartTimestamp);
                if (ms >= GameProfiler.SpikeLogThresholdMs)
                {
                    int gcDelta = GC.CollectionCount(0) - __state.GcGen0Before;
                    string passageId = __instance != null ? __instance.GetId() : "null";
                    GameProfiler.LogSpikeEvent(
                        "2.Passage.GetEnteredTargets()",
                        ms,
                        ms,
                        gcDelta,
                        string.Format("Passage:{0}", passageId));
                }
            }
        }

        // 3-A. 職員AI・更新 (AgentModel.OnFixedUpdate) - 親スコープ + 3a内訳 + スパイク条件ログ
        [HarmonyPatch(typeof(AgentModel), "OnFixedUpdate")]
        public static class AgentModel_OnFixedUpdate_ProfilePatch
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            public static void Prefix(out ParentState __state)
            {
                __state.GcGen0Before = GC.CollectionCount(0);
                GameProfiler.EnterParentScope(out __state.SavedChildTicks);
                __state.StartTimestamp = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            public static void Postfix(AgentModel __instance, ParentState __state)
            {
                double incMs, excMs;
                GameProfiler.ExitParentScope(
                    GameProfiler.WorkerAI,
                    GameProfiler.SubAgentUpdate,
                    __state.StartTimestamp,
                    __state.SavedChildTicks,
                    out incMs,
                    out excMs);

                if (incMs >= GameProfiler.SpikeLogThresholdMs)
                {
                    int gcDelta = GC.CollectionCount(0) - __state.GcGen0Before;
                    string name = __instance != null ? __instance.GetUnitName() : "null";
                    string state = __instance != null ? __instance.GetState().ToString() : "null";
                    var cmd = __instance != null ? __instance.GetCurrentCommand() : null;
                    string cmdName = cmd != null ? cmd.GetType().Name : "None";
                    string skillTarget = (__instance != null && __instance.currentSkill != null && __instance.currentSkill.targetCreature != null && __instance.currentSkill.targetCreature.metaInfo != null)
                        ? __instance.currentSkill.targetCreature.metaInfo.name
                        : "-";
                    GameProfiler.LogSpikeEvent(
                        "3a.AgentModel.OnFixedUpdate",
                        incMs,
                        excMs,
                        gcDelta,
                        string.Format("Agent:{0}, State:{1}, Cmd:{2}, WorkTarget:{3}", name, state, cmdName, skillTarget));
                }
            }
        }

        // 3-B. オフィサーAI・更新 (OfficerModel.OnFixedUpdate) - 親スコープ + 3b内訳 + スパイク条件ログ
        [HarmonyPatch(typeof(OfficerModel), "OnFixedUpdate")]
        public static class OfficerModel_OnFixedUpdate_ProfilePatch
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            public static void Prefix(out ParentState __state)
            {
                __state.GcGen0Before = GC.CollectionCount(0);
                GameProfiler.EnterParentScope(out __state.SavedChildTicks);
                __state.StartTimestamp = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            public static void Postfix(OfficerModel __instance, ParentState __state)
            {
                double incMs, excMs;
                GameProfiler.ExitParentScope(
                    GameProfiler.WorkerAI,
                    GameProfiler.SubOfficerUpdate,
                    __state.StartTimestamp,
                    __state.SavedChildTicks,
                    out incMs,
                    out excMs);

                if (incMs >= GameProfiler.SpikeLogThresholdMs)
                {
                    int gcDelta = GC.CollectionCount(0) - __state.GcGen0Before;
                    string name = __instance != null ? __instance.GetUnitName() : "null";
                    string state = __instance != null ? __instance.state.ToString() : "null";
                    bool panic = __instance != null && __instance.IsPanic();
                    var cmd = __instance != null ? __instance.GetCurrentCommand() : null;
                    string cmdName = cmd != null ? cmd.GetType().Name : "None";
                    GameProfiler.LogSpikeEvent(
                        "3b.OfficerModel.OnFixedUpdate",
                        incMs,
                        excMs,
                        gcDelta,
                        string.Format("Officer:{0}, State:{1}, Panic:{2}, Cmd:{3}", name, state, panic, cmdName));
                }
            }
        }

        // 3-C. 基底WorkerAI・更新 (WorkerModel.OnFixedUpdate) - 親スコープ
        [HarmonyPatch(typeof(WorkerModel), "OnFixedUpdate")]
        public static class WorkerModel_OnFixedUpdate_ProfilePatch
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            public static void Prefix(out ParentState __state)
            {
                __state.GcGen0Before = GC.CollectionCount(0);
                GameProfiler.EnterParentScope(out __state.SavedChildTicks);
                __state.StartTimestamp = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            public static void Postfix(ParentState __state)
            {
                double incMs, excMs;
                GameProfiler.ExitParentScope(
                    GameProfiler.WorkerAI,
                    null,
                    __state.StartTimestamp,
                    __state.SavedChildTicks,
                    out incMs,
                    out excMs);
            }
        }

        // 3c. WorkerAI内訳：バフ状態更新 (UnitModel.UpdateBufState)
        [HarmonyPatch(typeof(UnitModel), "UpdateBufState")]
        public static class UnitModel_UpdateBufState_ProfilePatch
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            public static void Prefix(out LeafState __state)
            {
                __state.GcGen0Before = GC.CollectionCount(0);
                __state.StartTimestamp = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            public static void Postfix(UnitModel __instance, LeafState __state)
            {
                double ms = GameProfiler.RecordSubMetric(GameProfiler.SubUpdateBufState, __state.StartTimestamp);
                if (ms >= GameProfiler.SpikeLogThresholdMs)
                {
                    int gcDelta = GC.CollectionCount(0) - __state.GcGen0Before;
                    string typeName = __instance != null ? __instance.GetType().Name : "null";
                    GameProfiler.LogSpikeEvent("3c.UnitModel.UpdateBufState", ms, ms, gcDelta, "UnitType:" + typeName);
                }
            }
        }

        // 3d. WorkerAI内訳：作業進行・入退室・EGOギフト判定 (UseSkill.OnFixedUpdate)
        [HarmonyPatch(typeof(UseSkill), "OnFixedUpdate")]
        public static class UseSkill_OnFixedUpdate_ProfilePatch
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            public static void Prefix(out LeafState __state)
            {
                __state.GcGen0Before = GC.CollectionCount(0);
                __state.StartTimestamp = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            public static void Postfix(UseSkill __instance, LeafState __state)
            {
                double ms = GameProfiler.RecordSubMetric(GameProfiler.SubUseSkill, __state.StartTimestamp);
                if (ms >= GameProfiler.SpikeLogThresholdMs)
                {
                    int gcDelta = GC.CollectionCount(0) - __state.GcGen0Before;
                    string agentName = (__instance != null && __instance.agent != null) ? __instance.agent.GetUnitName() : "null";
                    string creatureName = (__instance != null && __instance.targetCreature != null && __instance.targetCreature.metaInfo != null)
                        ? __instance.targetCreature.metaInfo.name
                        : "null";
                    float elapsed = __instance != null ? __instance.elapsedTime : -1f;
                    GameProfiler.LogSpikeEvent(
                        "3d.UseSkill.OnFixedUpdate",
                        ms,
                        ms,
                        gcDelta,
                        string.Format("Agent:{0}, Creature:{1}, WorkElapsed:{2:F2}s", agentName, creatureName, elapsed));
                }
            }
        }

        // 3d-1. WorkerAI内訳：収容室入室処理 (UseSkill.OnEnterRoom) - 音声ロード・暴走ゲージ等の内訳を分離特定
        [HarmonyPatch(typeof(UseSkill), "OnEnterRoom")]
        public static class UseSkill_OnEnterRoom_ProfilePatch
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            public static void Prefix(out LeafState __state)
            {
                SoundEffectCachePatch.LastSoundCallMs = 0.0;
                SoundEffectCachePatch.LastColdLoadMs = 0.0;
                GameProfiler.LastOverloadCallMs = 0.0;
                __state.GcGen0Before = GC.CollectionCount(0);
                __state.StartTimestamp = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            public static void Postfix(UseSkill __instance, LeafState __state)
            {
                double ms = GameProfiler.RecordSubMetric(GameProfiler.SubEnterRoom, __state.StartTimestamp);
                if (ms >= GameProfiler.SpikeLogThresholdMs)
                {
                    int gcDelta = GC.CollectionCount(0) - __state.GcGen0Before;
                    string agentName = (__instance != null && __instance.agent != null) ? __instance.agent.GetUnitName() : "null";
                    string creatureName = (__instance != null && __instance.targetCreature != null && __instance.targetCreature.metaInfo != null)
                        ? __instance.targetCreature.metaInfo.name
                        : "null";
                    double otherMs = ms - SoundEffectCachePatch.LastSoundCallMs - GameProfiler.LastOverloadCallMs;
                    if (otherMs < 0.0) otherMs = 0.0;
                    GameProfiler.LogSpikeEvent(
                        "3d-1.UseSkill.OnEnterRoom",
                        ms,
                        otherMs,
                        gcDelta,
                        string.Format("Agent:{0}, Creature:{1} | 内訳[Sound:{2:F2}ms(ColdLoad:{3:F2}ms), Overload:{4:F2}ms, OtherRoomUI/Horror:{5:F2}ms]",
                            agentName,
                            creatureName,
                            SoundEffectCachePatch.LastSoundCallMs,
                            SoundEffectCachePatch.LastColdLoadMs,
                            GameProfiler.LastOverloadCallMs,
                            otherMs));
                }
            }
        }

        // 3e. WorkerAI内訳：クリフォト暴走・試練発動 (CreatureOverloadManager.AddOverloadGague)
        [HarmonyPatch(typeof(CreatureOverloadManager), "AddOverloadGague")]
        public static class CreatureOverloadManager_AddOverloadGague_ProfilePatch
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            public static void Prefix(out LeafState __state)
            {
                __state.GcGen0Before = GC.CollectionCount(0);
                __state.StartTimestamp = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            public static void Postfix(LeafState __state)
            {
                double ms = GameProfiler.RecordSubMetric(GameProfiler.SubOverloadOrdeal, __state.StartTimestamp);
                GameProfiler.LastOverloadCallMs = ms;
                if (ms >= GameProfiler.SpikeLogThresholdMs)
                {
                    int gcDelta = GC.CollectionCount(0) - __state.GcGen0Before;
                    GameProfiler.LogSpikeEvent("3e.CreatureOverloadManager.AddOverloadGague", ms, ms, gcDelta, "Overload/Ordeal Triggered inside UseSkill.OnEnterRoom");
                }
            }
        }

        // 3f. WorkerAI内訳：オフィサー自律AI判断 (OfficerModel.ProcessActionNormalOfficer)
        [HarmonyPatch(typeof(OfficerModel), "ProcessActionNormalOfficer")]
        public static class OfficerModel_ProcessActionNormalOfficer_ProfilePatch
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            public static void Prefix(out LeafState __state)
            {
                __state.GcGen0Before = GC.CollectionCount(0);
                __state.StartTimestamp = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            public static void Postfix(OfficerModel __instance, LeafState __state)
            {
                double ms = GameProfiler.RecordSubMetric(GameProfiler.SubOfficerNormalAI, __state.StartTimestamp);
                if (ms >= GameProfiler.SpikeLogThresholdMs)
                {
                    int gcDelta = GC.CollectionCount(0) - __state.GcGen0Before;
                    string state = __instance != null ? __instance.state.ToString() : "null";
                    bool panic = __instance != null && __instance.IsPanic();
                    GameProfiler.LogSpikeEvent(
                        "3f.OfficerModel.ProcessActionNormalOfficer",
                        ms,
                        ms,
                        gcDelta,
                        string.Format("State:{0}, Panic:{1}", state, panic));
                }
            }
        }

        // 4-A. 幻想体AI・更新 (CreatureModel.OnFixedUpdate) - 親スコープ
        [HarmonyPatch(typeof(CreatureModel), "OnFixedUpdate")]
        public static class CreatureModel_OnFixedUpdate_ProfilePatch
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            public static void Prefix(out ParentState __state)
            {
                __state.GcGen0Before = GC.CollectionCount(0);
                GameProfiler.EnterParentScope(out __state.SavedChildTicks);
                __state.StartTimestamp = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            public static void Postfix(CreatureModel __instance, ParentState __state)
            {
                double incMs, excMs;
                GameProfiler.ExitParentScope(
                    GameProfiler.CreatureAI,
                    null,
                    __state.StartTimestamp,
                    __state.SavedChildTicks,
                    out incMs,
                    out excMs);

                if (incMs >= GameProfiler.SpikeLogThresholdMs)
                {
                    int gcDelta = GC.CollectionCount(0) - __state.GcGen0Before;
                    string name = (__instance != null && __instance.metaInfo != null) ? __instance.metaInfo.name : "null";
                    GameProfiler.LogSpikeEvent("4a.CreatureModel.OnFixedUpdate", incMs, excMs, gcDelta, "Creature:" + name);
                }
            }
        }

        // 4-B. 試練AI・更新 (OrdealCreatureModel.OnFixedUpdate) - 親スコープ
        [HarmonyPatch(typeof(OrdealCreatureModel), "OnFixedUpdate")]
        public static class OrdealCreatureModel_OnFixedUpdate_ProfilePatch
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            public static void Prefix(out ParentState __state)
            {
                __state.GcGen0Before = GC.CollectionCount(0);
                GameProfiler.EnterParentScope(out __state.SavedChildTicks);
                __state.StartTimestamp = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            public static void Postfix(OrdealCreatureModel __instance, ParentState __state)
            {
                double incMs, excMs;
                GameProfiler.ExitParentScope(
                    GameProfiler.CreatureAI,
                    null,
                    __state.StartTimestamp,
                    __state.SavedChildTicks,
                    out incMs,
                    out excMs);

                if (incMs >= GameProfiler.SpikeLogThresholdMs)
                {
                    int gcDelta = GC.CollectionCount(0) - __state.GcGen0Before;
                    string name = (__instance != null && __instance.metaInfo != null) ? __instance.metaInfo.name : "null";
                    GameProfiler.LogSpikeEvent("4b.OrdealCreatureModel.OnFixedUpdate", incMs, excMs, gcDelta, "Ordeal:" + name);
                }
            }
        }

        // 5. 移動・物理座標進捗 (MovableObjectNode.ProcessMoveNode) - 葉ノード
        [HarmonyPatch(typeof(MovableObjectNode), "ProcessMoveNode")]
        public static class MovableObjectNode_ProcessMoveNode_ProfilePatch
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            public static void Prefix(out long __state)
            {
                __state = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            public static void Postfix(long __state)
            {
                GameProfiler.RecordLeaf(GameProfiler.Movement, __state);
            }
        }
    }
}
