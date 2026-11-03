using System.Diagnostics;
using HarmonyLib;
using LobotomyModernEngine.Core;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// ゲーム内主要CPU処理のリアルタイムプロファイラ計測パッチ群
    /// - AgentModel / OfficerModel (WorkerAI) および CreatureModel / OrdealCreatureModel (CreatureAI) の全サブクラスを網羅。
    /// - 入れ子呼び出し (ParentScope / RecordLeaf) により、親処理の Inclusive 時間と Exclusive (自己) 時間を正確に分離。
    /// </summary>
    public static class ProfilerPatch
    {
        public struct ParentState
        {
            public long StartTimestamp;
            public long SavedChildTicks;
        }

        // 1. 経路探索 (GraphAstar.SearchPath) - 葉ノード
        [HarmonyPatch(typeof(GraphAstar), "SearchPath")]
        public static class SearchPath_ProfilePatch
        {
            [HarmonyPrefix]
            public static void Prefix(out long __state)
            {
                __state = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            public static void Postfix(long __state)
            {
                GameProfiler.RecordLeaf(GameProfiler.Pathfinding, __state);
            }
        }

        // 2-A. 索敵・近傍判定 (PassageObjectModel.GetEnteredTargets(MovableObjectNode)) - 葉ノード
        [HarmonyPatch(typeof(PassageObjectModel), "GetEnteredTargets", typeof(MovableObjectNode))]
        public static class GetEnteredTargetsWithExclude_ProfilePatch
        {
            [HarmonyPrefix]
            public static void Prefix(out long __state)
            {
                __state = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            public static void Postfix(long __state)
            {
                GameProfiler.RecordLeaf(GameProfiler.TargetScan, __state);
            }
        }

        // 2-B. 索敵・近傍判定 (PassageObjectModel.GetEnteredTargets()) - 葉ノード
        [HarmonyPatch(typeof(PassageObjectModel), "GetEnteredTargets", new System.Type[0])]
        public static class GetEnteredTargetsNoArg_ProfilePatch
        {
            [HarmonyPrefix]
            public static void Prefix(out long __state)
            {
                __state = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            public static void Postfix(long __state)
            {
                GameProfiler.RecordLeaf(GameProfiler.TargetScan, __state);
            }
        }

        // 3-A. 職員AI・更新 (AgentModel.OnFixedUpdate) - 親スコープ
        [HarmonyPatch(typeof(AgentModel), "OnFixedUpdate")]
        public static class AgentModel_OnFixedUpdate_ProfilePatch
        {
            [HarmonyPrefix]
            public static void Prefix(out ParentState __state)
            {
                GameProfiler.EnterParentScope(out __state.SavedChildTicks);
                __state.StartTimestamp = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            public static void Postfix(ParentState __state)
            {
                GameProfiler.ExitParentScope(GameProfiler.WorkerAI, __state.StartTimestamp, __state.SavedChildTicks);
            }
        }

        // 3-B. オフィサーAI・更新 (OfficerModel.OnFixedUpdate) - 親スコープ
        [HarmonyPatch(typeof(OfficerModel), "OnFixedUpdate")]
        public static class OfficerModel_OnFixedUpdate_ProfilePatch
        {
            [HarmonyPrefix]
            public static void Prefix(out ParentState __state)
            {
                GameProfiler.EnterParentScope(out __state.SavedChildTicks);
                __state.StartTimestamp = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            public static void Postfix(ParentState __state)
            {
                GameProfiler.ExitParentScope(GameProfiler.WorkerAI, __state.StartTimestamp, __state.SavedChildTicks);
            }
        }

        // 3-C. 基底WorkerAI・更新 (WorkerModel.OnFixedUpdate) - 親スコープ
        [HarmonyPatch(typeof(WorkerModel), "OnFixedUpdate")]
        public static class WorkerModel_OnFixedUpdate_ProfilePatch
        {
            [HarmonyPrefix]
            public static void Prefix(out ParentState __state)
            {
                GameProfiler.EnterParentScope(out __state.SavedChildTicks);
                __state.StartTimestamp = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            public static void Postfix(ParentState __state)
            {
                GameProfiler.ExitParentScope(GameProfiler.WorkerAI, __state.StartTimestamp, __state.SavedChildTicks);
            }
        }

        // 4-A. 幻想体AI・更新 (CreatureModel.OnFixedUpdate) - 親スコープ
        [HarmonyPatch(typeof(CreatureModel), "OnFixedUpdate")]
        public static class CreatureModel_OnFixedUpdate_ProfilePatch
        {
            [HarmonyPrefix]
            public static void Prefix(out ParentState __state)
            {
                GameProfiler.EnterParentScope(out __state.SavedChildTicks);
                __state.StartTimestamp = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            public static void Postfix(ParentState __state)
            {
                GameProfiler.ExitParentScope(GameProfiler.CreatureAI, __state.StartTimestamp, __state.SavedChildTicks);
            }
        }

        // 4-B. 試練AI・更新 (OrdealCreatureModel.OnFixedUpdate) - 親スコープ
        [HarmonyPatch(typeof(OrdealCreatureModel), "OnFixedUpdate")]
        public static class OrdealCreatureModel_OnFixedUpdate_ProfilePatch
        {
            [HarmonyPrefix]
            public static void Prefix(out ParentState __state)
            {
                GameProfiler.EnterParentScope(out __state.SavedChildTicks);
                __state.StartTimestamp = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            public static void Postfix(ParentState __state)
            {
                GameProfiler.ExitParentScope(GameProfiler.CreatureAI, __state.StartTimestamp, __state.SavedChildTicks);
            }
        }

        // 5. 移動・物理座標進捗 (MovableObjectNode.ProcessMoveNode) - 葉ノード
        [HarmonyPatch(typeof(MovableObjectNode), "ProcessMoveNode")]
        public static class MovableObjectNode_ProcessMoveNode_ProfilePatch
        {
            [HarmonyPrefix]
            public static void Prefix(out long __state)
            {
                __state = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            public static void Postfix(long __state)
            {
                GameProfiler.RecordLeaf(GameProfiler.Movement, __state);
            }
        }
    }
}
