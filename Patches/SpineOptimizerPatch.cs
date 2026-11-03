using HarmonyLib;
using Spine.Unity;
using UnityEngine;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// v0.5.0: SpineOptimizerModule / WorkerUnit / AgentUnit / OfficerUnit が毎フレーム全ユニット分
    /// Camera.main (FindGameObjectWithTag("MainCamera")) および transform.position / orthographicSize / aspect の
    /// Unity C++ 境界呼び出しを数百回繰り返すボトルネックを、1フレーム1回のスナップショット共有へ置き換える。
    /// </summary>
    public static class SpineOptimizerPatch
    {
        private static int _cachedFrame = -1;
        private static bool _hasValidCamera;
        private static Vector3 _camPos;
        private static float _orthoSize;
        private static float _halfWidth;
        private static float _halfHeight;
        private static bool _isGameStopped;

        public static long AvoidedCameraLookups { get; private set; }

        public static void ClearCache()
        {
            _cachedFrame = -1;
            _hasValidCamera = false;
        }

        private static bool EnsureFrameSnapshot()
        {
            int frame = Time.frameCount;
            if (_cachedFrame == frame)
            {
                AvoidedCameraLookups++;
                return _hasValidCamera;
            }

            _cachedFrame = frame;
            Camera main = Camera.main;
            if (main == null)
            {
                _hasValidCamera = false;
                return false;
            }

            _hasValidCamera = true;
            _orthoSize = main.orthographicSize;
            float aspect = main.aspect;
            _camPos = main.transform.position;
            _halfWidth = _orthoSize * aspect;
            _halfHeight = _orthoSize;
            _isGameStopped = GameManager.currentGameManager != null && GameManager.currentGameManager.state == GameState.STOP;
            return true;
        }

        [HarmonyPatch(typeof(SpineOptimizerModule), "Update")]
        public static class SpineOptimizerModule_Update_Patch
        {
            public static bool Prefix(
                SkeletonRenderer ____spineRenderer,
                UnitModel ____targetModel,
                float ____loosenessLevel,
                ref bool ____inCamera)
            {
                if (!Plugin.EnableSpineOptimizerCache.Value)
                {
                    return true;
                }

                if (____spineRenderer == null || ____targetModel == null)
                {
                    return false;
                }

                if (!EnsureFrameSnapshot())
                {
                    return true;
                }

                // 1. UpdateCheckInCamera 相当 (マージン 6f + 1.5f = 7.5f)
                Vector3 currentViewPosition = ____targetModel.GetCurrentViewPosition();
                float dx = currentViewPosition.x - _camPos.x;
                float dy = currentViewPosition.y - _camPos.y;
                float limitX = _halfWidth + 7.5f;
                float limitY = _halfHeight + 7.5f;

                bool inCam = dx < limitX && dx > -limitX && dy < limitY && dy > -limitY;
                ____inCamera = inCam;

                // 2. UpdateAnimationQuality 相当
                if (_isGameStopped || !inCam)
                {
                    ____spineRenderer.optimizeLevel = 5;
                }
                else
                {
                    float scale = 1f;
                    MovableObjectNode node = ____targetModel.GetMovableNode();
                    if (node != null && node.currentScale > 0.0001f)
                    {
                        scale = node.currentScale;
                    }

                    float num = (_orthoSize / scale) / (____loosenessLevel > 0.0001f ? ____loosenessLevel : 1f);
                    if (Plugin.EnableSmoothSpineAnimation.Value)
                    {
                        if (num > 85f)
                            ____spineRenderer.optimizeLevel = 3;
                        else if (num > 65f)
                            ____spineRenderer.optimizeLevel = 1;
                        else
                            ____spineRenderer.optimizeLevel = 0;
                    }
                    else
                    {
                        if (num > 80f)
                            ____spineRenderer.optimizeLevel = 4;
                        else if (num > 50f)
                            ____spineRenderer.optimizeLevel = 3;
                        else if (num > 30f)
                            ____spineRenderer.optimizeLevel = 2;
                        else if (num > 25f)
                            ____spineRenderer.optimizeLevel = 1;
                        else
                            ____spineRenderer.optimizeLevel = 0;
                    }
                }

                return false;
            }
        }

        [HarmonyPatch(typeof(WorkerUnit), "UpdateCheckInCamera")]
        public static class WorkerUnit_UpdateCheckInCamera_Patch
        {
            public static bool Prefix(Vector3 newPosition, ref bool ____inCamera)
            {
                if (!Plugin.EnableSpineOptimizerCache.Value)
                {
                    return true;
                }

                if (!EnsureFrameSnapshot())
                {
                    return true;
                }

                float dx = newPosition.x - _camPos.x;
                float dy = newPosition.y - _camPos.y;
                float limitX = _halfWidth + 1.5f;
                float limitY = _halfHeight + 1.5f;

                ____inCamera = dx < limitX && dx > -limitX && dy < limitY && dy > -limitY;
                return false;
            }
        }

        [HarmonyPatch(typeof(AgentUnit), "UpdateAnimationQuality")]
        public static class AgentUnit_UpdateAnimationQuality_Patch
        {
            public static bool Prefix(AgentUnit __instance, bool ____inCamera)
            {
                if (!Plugin.EnableSpineOptimizerCache.Value)
                {
                    return true;
                }

                if (__instance.spineRenderer == null)
                {
                    return false;
                }

                if (!EnsureFrameSnapshot())
                {
                    return true;
                }

                if (_isGameStopped || !____inCamera)
                {
                    __instance.spineRenderer.optimizeLevel = 5;
                }
                else
                {
                    float scale = 1f;
                    if (__instance.workerModel != null)
                    {
                        MovableObjectNode node = __instance.workerModel.GetMovableNode();
                        if (node != null && node.currentScale > 0.0001f)
                        {
                            scale = node.currentScale;
                        }
                    }

                    float num2 = _orthoSize / scale;
                    if (Plugin.EnableSmoothSpineAnimation.Value)
                    {
                        // 視界内の職員は通常視野 (orthoSize <= 55) では間引かず60FPSフル更新
                        if (num2 > 85f)
                            __instance.spineRenderer.optimizeLevel = 3;
                        else if (num2 > 65f)
                            __instance.spineRenderer.optimizeLevel = 1;
                        else
                            __instance.spineRenderer.optimizeLevel = 0;
                    }
                    else
                    {
                        if (num2 > 80f)
                            __instance.spineRenderer.optimizeLevel = 4;
                        else if (num2 > 50f)
                            __instance.spineRenderer.optimizeLevel = 3;
                        else if (num2 > 30f)
                            __instance.spineRenderer.optimizeLevel = 2;
                        else if (num2 > 25f)
                            __instance.spineRenderer.optimizeLevel = 1;
                        else
                            __instance.spineRenderer.optimizeLevel = 0;
                    }
                }

                return false;
            }
        }

        [HarmonyPatch(typeof(OfficerUnit), "UpdateAnimationQuality")]
        public static class OfficerUnit_UpdateAnimationQuality_Patch
        {
            public static bool Prefix(OfficerUnit __instance, bool ____inCamera)
            {
                if (!Plugin.EnableSpineOptimizerCache.Value)
                {
                    return true;
                }

                if (__instance.spineRenderer == null)
                {
                    return false;
                }

                if (!EnsureFrameSnapshot())
                {
                    return true;
                }

                if (_isGameStopped || !____inCamera)
                {
                    __instance.spineRenderer.optimizeLevel = 5;
                }
                else
                {
                    float scale = 1f;
                    if (__instance.workerModel != null)
                    {
                        MovableObjectNode node = __instance.workerModel.GetMovableNode();
                        if (node != null && node.currentScale > 0.0001f)
                        {
                            scale = node.currentScale;
                        }
                    }

                    float num2 = _orthoSize / scale;
                    if (Plugin.EnableSmoothSpineAnimation.Value)
                    {
                        // 視界内のオフィサーも通常視野ではフルアニメーション
                        if (num2 > 85f)
                            __instance.spineRenderer.optimizeLevel = 3;
                        else if (num2 > 65f)
                            __instance.spineRenderer.optimizeLevel = 1;
                        else
                            __instance.spineRenderer.optimizeLevel = 0;
                    }
                    else
                    {
                        if (num2 > 70f)
                            __instance.spineRenderer.optimizeLevel = 5;
                        else if (num2 > 45f)
                            __instance.spineRenderer.optimizeLevel = 4;
                        else if (num2 > 35f)
                            __instance.spineRenderer.optimizeLevel = 3;
                        else if (num2 > 30f)
                            __instance.spineRenderer.optimizeLevel = 2;
                        else if (num2 > 22f)
                            __instance.spineRenderer.optimizeLevel = 1;
                        else
                            __instance.spineRenderer.optimizeLevel = 0;
                    }
                }

                return false;
            }
        }
    }
}
