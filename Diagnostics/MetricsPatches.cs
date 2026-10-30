using UnityEngine;
using LobotomyModernEngine.Patches;

namespace LobotomyModernEngine.Diagnostics
{
    /// <summary>
    /// シーンロード計測およびメトリクス集計
    /// </summary>
    public static class MetricsPatches
    {
        // Notice.Send メトリクス（NoticePatch への委譲）
        public static long NoticeSendTotalCount => NoticePatch.TotalSendCount;
        public static int NoticeSendSecCount => NoticePatch.SendPerSec;
        public static long AvoidedNoticeAllocations => NoticePatch.AvoidedAllocations;

        // TakeDamage メトリクス（TakeDamagePatch への委譲）
        public static long TakeDamageTotalCount => TakeDamagePatch.TotalTakeDamageCount;
        public static int TakeDamageSecCount => TakeDamagePatch.TakeDamagePerSec;
        public static long TakeDamageZeroBarrierCount => TakeDamagePatch.ZeroBarrierAllocAvoided;

        // ロード区間メトリクス
        public static float LastLoadDurationMs = 0f;
        public static string LastLoadedSceneName = "";
        private static float _loadStartTime = 0f;

        public static void OnSceneLoadStart(string sceneName)
        {
            _loadStartTime = Time.realtimeSinceStartup;
        }

        public static void OnSceneLoadEnd(string sceneName)
        {
            if (_loadStartTime > 0.001f)
            {
                LastLoadDurationMs = (Time.realtimeSinceStartup - _loadStartTime) * 1000f;
                LastLoadedSceneName = sceneName;
                _loadStartTime = 0f;
            }
        }

        public static void ResetPerSecondCounters()
        {
            NoticePatch.SendPerSec = 0;
            TakeDamagePatch.TakeDamagePerSec = 0;
        }
    }
}
