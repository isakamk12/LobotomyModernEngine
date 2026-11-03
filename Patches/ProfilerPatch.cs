using System.Diagnostics;
using HarmonyLib;
using LobotomyModernEngine.Core;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// ゲーム内主要CPU処理のリアルタイムプロファイラ計測パッチ群
    /// Stopwatch.GetTimestamp() の軽量 Prefix/Postfix により実測値を収集。
    /// </summary>
    public static class ProfilerPatch
    {
        // 1. 経路探索 (GraphAstar.SearchPath)
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
                long elapsed = Stopwatch.GetTimestamp() - __state;
                GameProfiler.Pathfinding.Record(elapsed);
            }
        }

        // 2-A. 索敵・近傍判定 (PassageObjectModel.GetEnteredTargets(MovableObjectNode))
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
                long elapsed = Stopwatch.GetTimestamp() - __state;
                GameProfiler.TargetScan.Record(elapsed);
            }
        }

        // 2-B. 索敵・近傍判定 (PassageObjectModel.GetEnteredTargets())
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
                long elapsed = Stopwatch.GetTimestamp() - __state;
                GameProfiler.TargetScan.Record(elapsed);
            }
        }

        // 3. 職員AI・更新 (WorkerModel.OnFixedUpdate)
        [HarmonyPatch(typeof(WorkerModel), "OnFixedUpdate")]
        public static class WorkerModel_OnFixedUpdate_ProfilePatch
        {
            [HarmonyPrefix]
            public static void Prefix(out long __state)
            {
                __state = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            public static void Postfix(long __state)
            {
                long elapsed = Stopwatch.GetTimestamp() - __state;
                GameProfiler.WorkerAI.Record(elapsed);
            }
        }

        // 4. 怪異AI・更新 (CreatureModel.OnFixedUpdate)
        [HarmonyPatch(typeof(CreatureModel), "OnFixedUpdate")]
        public static class CreatureModel_OnFixedUpdate_ProfilePatch
        {
            [HarmonyPrefix]
            public static void Prefix(out long __state)
            {
                __state = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            public static void Postfix(long __state)
            {
                long elapsed = Stopwatch.GetTimestamp() - __state;
                GameProfiler.CreatureAI.Record(elapsed);
            }
        }

        // 5. 移動・物理座標進捗 (MovableObjectNode.ProcessMoveNode)
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
                long elapsed = Stopwatch.GetTimestamp() - __state;
                GameProfiler.Movement.Record(elapsed);
            }
        }
    }
}
