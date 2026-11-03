using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// v0.8.5: 移動補間パッチ (Movement Visual Interpolation)。
    /// バニラの Lobotomy Corporation では、移動座標の計算が FixedUpdate (50Hz〜66Hz) でのみ行われ、
    /// 画面描画 Update (60Hz+) と周期がズレることで「1秒間に約10回、描画フレームで座標が止まる」
    /// という規則的なマイクロスタッター (移動中のカクカク感) が発生していた。
    /// 
    /// 本パッチは物理進行 (FixedUpdate) のゲームロジック・当たり判定・イベント発火には一切触れず、
    /// WorkerUnit / CreatureUnit の描画座標 (base.transform.localPosition) のみ、
    /// 直前の FixedUpdate 位置と現在の FixedUpdate 位置の間を描画時間 (Time.time / FixedUpdate 進行度) に応じて
    /// 滑らかに線形補間 (Lerp) する。
    /// </summary>
    public static class MovementInterpolationPatch
    {
        private class UnitMotionState
        {
            public Vector3 PreviousPosition;
            public Vector3 CurrentPosition;
            public float LastFixedTime;
            public bool HasValidHistory;
        }

        private static readonly Dictionary<MovableObjectNode, UnitMotionState> _motionStates =
            new Dictionary<MovableObjectNode, UnitMotionState>(128);

        private static float _fixedTime;
        private static float _fixedInterval = 0.02f;

        public static long InterpolatedFrames { get; private set; }

        public static void ClearCache()
        {
            _fixedTime = 0f;
            _motionStates.Clear();
        }

        /// <summary>
        /// GameManager.FixedUpdate または MovableObjectNode.ProcessMoveNode 完了時に
        /// 物理ステップごとの位置スナップショットを記録する
        /// </summary>
        [HarmonyPatch(typeof(MovableObjectNode), "ProcessMoveNode")]
        public static class MovableObjectNode_ProcessMoveNode_SnapshotPatch
        {
            public static void Postfix(MovableObjectNode __instance)
            {
                if (!Plugin.EnableMovementInterpolation.Value)
                {
                    return;
                }

                _fixedTime = Time.fixedTime;
                _fixedInterval = Time.fixedDeltaTime > 0.0001f ? Time.fixedDeltaTime : 0.02f;

                if (!_motionStates.TryGetValue(__instance, out var state))
                {
                    state = new UnitMotionState();
                    _motionStates[__instance] = state;
                }

                Vector3 currentPos = __instance.GetCurrentViewPosition();

                if (!state.HasValidHistory)
                {
                    state.PreviousPosition = currentPos;
                    state.CurrentPosition = currentPos;
                    state.LastFixedTime = _fixedTime;
                    state.HasValidHistory = true;
                }
                else
                {
                    // テレポートや部屋移動など不連続なジャンプ (距離15以上) は補間せず即時追従
                    float distSq = (currentPos - state.CurrentPosition).sqrMagnitude;
                    if (distSq > 225f)
                    {
                        state.PreviousPosition = currentPos;
                        state.CurrentPosition = currentPos;
                    }
                    else
                    {
                        state.PreviousPosition = state.CurrentPosition;
                        state.CurrentPosition = currentPos;
                    }
                    state.LastFixedTime = _fixedTime;
                }
            }
        }

        /// <summary>
        /// テレポートやノード強制設定時に履歴をリセットしてゴースト移動を防止
        /// </summary>
        [HarmonyPatch(typeof(MovableObjectNode), "SetCurrentNode")]
        public static class MovableObjectNode_SetCurrentNode_Patch
        {
            public static void Postfix(MovableObjectNode __instance)
            {
                if (_motionStates.TryGetValue(__instance, out var state))
                {
                    Vector3 pos = __instance.GetCurrentViewPosition();
                    state.PreviousPosition = pos;
                    state.CurrentPosition = pos;
                }
            }
        }

        /// <summary>
        /// WorkerUnit (職員・オフィサー) の描画位置更新を補間
        /// </summary>
        [HarmonyPatch(typeof(WorkerUnit), "UpdateViewPosition")]
        public static class WorkerUnit_UpdateViewPosition_Patch
        {
            public static bool Prefix(
                WorkerUnit __instance,
                WorkerModel ___workerModel,
                Vector3 ___recoilPosition,
                float ___zValue,
                bool ___blockMoving)
            {
                if (!Plugin.EnableMovementInterpolation.Value)
                {
                    return true;
                }

                if (___blockMoving || ___workerModel == null)
                {
                    return true;
                }

                var movableNode = ___workerModel.GetMovableNode();
                if (movableNode == null)
                {
                    return true;
                }

                __instance.transform.localScale = new Vector3(
                    movableNode.currentScale,
                    movableNode.currentScale,
                    __instance.transform.localScale.z);

                MapEdge currentEdge = ___workerModel.GetCurrentEdge();
                Vector3 renderPos;

                if (_motionStates.TryGetValue(movableNode, out var state) && state.HasValidHistory)
                {
                    // FixedUpdate からの経過割合 (0.0f 〜 1.0f)
                    float alpha = _fixedInterval > 0.0001f ? (Time.time - state.LastFixedTime) / _fixedInterval : 1f;
                    alpha = Mathf.Clamp01(alpha);

                    renderPos = Vector3.Lerp(state.PreviousPosition, state.CurrentPosition, alpha) + ___recoilPosition;
                    InterpolatedFrames++;
                }
                else
                {
                    renderPos = ___workerModel.GetCurrentViewPosition() + ___recoilPosition;
                }

                if (currentEdge != null && currentEdge.type == "door")
                {
                    renderPos.z = 100000f;
                    __instance.transform.localPosition = renderPos;
                }
                else
                {
                    if (!movableNode.isIgnoreZValue)
                    {
                        renderPos.z += ___zValue;
                    }
                    __instance.transform.localPosition = renderPos;
                }

                // 【最重要】バニラ必須処理: カメラ内判定を直接更新しないと _inCamera=false になり
                // 全ユニットが optimizeLevel=5 (7.5FPSの超絶ガクガクコマ落ち) に固定されてしまう
                SpineOptimizerPatch.UpdateUnitInCamera(__instance, renderPos);

                return false; // バニラの UpdateViewPosition を完全代替
            }
        }

        /// <summary>
        /// CreatureUnit (幻想体・試練エネミー) の描画位置更新を Update 内でも反映してカクつきを解消
        /// </summary>
        [HarmonyPatch(typeof(CreatureUnit), "Update")]
        public static class CreatureUnit_Update_InterpolationPatch
        {
            public static void Postfix(CreatureUnit __instance, CreatureModel ___model, ref Vector3 ___viewPosition, ref bool ___visible)
            {
                if (!Plugin.EnableMovementInterpolation.Value)
                {
                    return;
                }

                if (___model == null)
                {
                    return;
                }

                var movableNode = ___model.GetMovableNode();
                if (movableNode == null)
                {
                    return;
                }

                MapEdge currentEdge = ___model.GetCurrentEdge();
                if (currentEdge != null && currentEdge.type == "door")
                {
                    if (___visible)
                    {
                        ___visible = false;
                        Vector3 currentViewPosition = ___model.GetCurrentViewPosition();
                        currentViewPosition.z = 100000f;
                        ___viewPosition = currentViewPosition;
                        __instance.transform.localPosition = ___viewPosition;
                    }
                    return;
                }

                if (!___visible)
                {
                    ___visible = true;
                }

                Vector3 renderPos;
                if (_motionStates.TryGetValue(movableNode, out var state) && state.HasValidHistory)
                {
                    float alpha = _fixedInterval > 0.0001f ? (Time.time - state.LastFixedTime) / _fixedInterval : 1f;
                    alpha = Mathf.Clamp01(alpha);
                    renderPos = Vector3.Lerp(state.PreviousPosition, state.CurrentPosition, alpha);
                }
                else
                {
                    renderPos = ___model.GetCurrentViewPosition();
                }

                ___viewPosition = renderPos;
                __instance.transform.localPosition = renderPos;
            }
        }
    }
}
