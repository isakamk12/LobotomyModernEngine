using System;
using System.Diagnostics;
using System.Threading;

namespace LobotomyModernEngine.Core
{
    /// <summary>
    /// ゲーム内主要CPU処理のリアルタイムプロファイラ
    /// ナノ秒精度の Stopwatch.GetTimestamp() を用いたロックフリー集計。
    /// 1秒ごとにスナップショットを更新し、F9モニターやログに実測値を提示する。
    /// </summary>
    public static class GameProfiler
    {
        public class Metric
        {
            public readonly string Key;
            public readonly string DisplayName;

            private long _totalTicks;
            private long _callCount;
            private long _maxTicks;

            // スナップショット（UI描画用）
            public float TotalMsPerSec { get; private set; }
            public float AvgMsPerFrame { get; private set; }
            public float MaxMsPerCall { get; private set; }
            public int CallsPerSec { get; private set; }

            public Metric(string key, string displayName)
            {
                Key = key;
                DisplayName = displayName;
            }

            public void Record(long elapsedTicks)
            {
                Interlocked.Add(ref _totalTicks, elapsedTicks);
                Interlocked.Increment(ref _callCount);

                long currentMax = _maxTicks;
                while (elapsedTicks > currentMax)
                {
                    if (Interlocked.CompareExchange(ref _maxTicks, elapsedTicks, currentMax) == currentMax)
                        break;
                    currentMax = _maxTicks;
                }
            }

            public void Sample(float elapsedSeconds, int frameCount)
            {
                long total = Interlocked.Exchange(ref _totalTicks, 0);
                long count = Interlocked.Exchange(ref _callCount, 0);
                long max = Interlocked.Exchange(ref _maxTicks, 0);

                double freq = Stopwatch.Frequency;
                float totalMs = (float)(total * 1000.0 / freq);

                TotalMsPerSec = elapsedSeconds > 0.001f ? (totalMs / elapsedSeconds) : 0f;
                AvgMsPerFrame = frameCount > 0 ? (totalMs / frameCount) : 0f;
                MaxMsPerCall = (float)(max * 1000.0 / freq);
                CallsPerSec = elapsedSeconds > 0.001f ? (int)(count / elapsedSeconds) : 0;
            }

            public void Reset()
            {
                Interlocked.Exchange(ref _totalTicks, 0);
                Interlocked.Exchange(ref _callCount, 0);
                Interlocked.Exchange(ref _maxTicks, 0);
                TotalMsPerSec = 0f;
                AvgMsPerFrame = 0f;
                MaxMsPerCall = 0f;
                CallsPerSec = 0;
            }
        }

        // 5大処理メトリック
        public static readonly Metric Pathfinding = new Metric("Pathfinding", "経路探索 (GraphAstar)");
        public static readonly Metric TargetScan = new Metric("TargetScan", "索敵・近傍判定 (GetEnteredTargets)");
        public static readonly Metric WorkerAI = new Metric("WorkerAI", "職員AI・更新 (WorkerModel.FixedUpdate)");
        public static readonly Metric CreatureAI = new Metric("CreatureAI", "怪異AI・更新 (CreatureModel.FixedUpdate)");
        public static readonly Metric Movement = new Metric("Movement", "移動・物理座標 (ProcessMoveNode)");

        public static readonly Metric[] AllMetrics = new Metric[]
        {
            Pathfinding,
            TargetScan,
            WorkerAI,
            CreatureAI,
            Movement
        };

        private static float _lastSampleTime;
        private static int _frameCounter;

        public static void Update(float unscaledTime)
        {
            _frameCounter++;
            float elapsed = unscaledTime - _lastSampleTime;
            if (elapsed >= 1.0f)
            {
                int frames = _frameCounter;
                _frameCounter = 0;
                _lastSampleTime = unscaledTime;

                for (int i = 0; i < AllMetrics.Length; i++)
                {
                    AllMetrics[i].Sample(elapsed, frames);
                }
            }
        }

        public static void ResetAll()
        {
            _frameCounter = 0;
            for (int i = 0; i < AllMetrics.Length; i++)
            {
                AllMetrics[i].Reset();
            }
        }
    }
}
