using System;
using System.Diagnostics;
using System.Text;

namespace LobotomyModernEngine.Core
{
    /// <summary>
    /// ゲーム内主要CPU処理のリアルタイム計測プロファイラ
    /// - Stopwatch.GetTimestamp() (高分解能タイマー / QPC) を使用。
    /// - 対象メソッドはすべてUnityメインスレッド上で実行されるため、Interlockedによるバスロック負荷を排し、
    ///   メインスレッド上の単純加算で計測オーバーヘッドを最小化。
    /// - 1回平均(μs/回)、秒間呼出(回/s)、秒間累積(ms/s)、1フレーム平均累積(ms/f)、最大スパイク(ms)を分離算出。
    /// - 入れ子呼び出し（WorkerAI / CreatureAI の内部で呼ばれる Movement / TargetScan / Pathfinding）による
    ///   二重計上を防ぐため、Inclusive（全体時間）と Exclusive（子処理を除いた純粋な自己時間）の双方を集計する。
    /// </summary>
    public static class GameProfiler
    {
        public class Metric
        {
            public readonly string Key;
            public readonly string DisplayName;
            public readonly bool HasNestedChildren;

            // メインスレッド集計用フィールド（Interlocked不使用）
            private long _inclusiveTicks;
            private long _childTicks;
            private long _callCount;
            private long _maxTicks;

            // 1秒スナップショット（UI・ログ出力用）
            public float TotalMsPerSec { get; private set; }      // 秒間累積 (Inclusive ms/s)
            public float ExclusiveMsPerSec { get; private set; }  // 秒間自己累積 (Exclusive ms/s)
            public float AvgMsPerFrame { get; private set; }      // フレーム累積 (Inclusive ms/frame)
            public float ExclusiveMsPerFrame { get; private set; }// フレーム自己累積 (Exclusive ms/frame)
            public float AvgUsPerCall { get; private set; }       // 1回あたり平均 (Inclusive μs/call)
            public float ExclusiveUsPerCall { get; private set; } // 1回あたり自己平均 (Exclusive μs/call)
            public float MaxMsPerCall { get; private set; }       // 単発最大スパイク (ms)
            public int CallsPerSec { get; private set; }          // 秒間呼出回数 (calls/s)

            // ステージ通算統計（日終了時のサマリーログ用）
            private long _stageInclusiveTicks;
            private long _stageExclusiveTicks;
            private long _stageCallCount;
            private long _stageMaxTicks;

            public Metric(string key, string displayName, bool hasNestedChildren)
            {
                Key = key;
                DisplayName = displayName;
                HasNestedChildren = hasNestedChildren;
            }

            public void Record(long elapsedTicks, long nestedChildTicks)
            {
                _inclusiveTicks += elapsedTicks;
                _childTicks += nestedChildTicks;
                _callCount++;
                if (elapsedTicks > _maxTicks)
                {
                    _maxTicks = elapsedTicks;
                }

                long excl = elapsedTicks > nestedChildTicks ? (elapsedTicks - nestedChildTicks) : 0L;
                _stageInclusiveTicks += elapsedTicks;
                _stageExclusiveTicks += excl;
                _stageCallCount++;
                if (elapsedTicks > _stageMaxTicks)
                {
                    _stageMaxTicks = elapsedTicks;
                }
            }

            public void Sample(float elapsedSeconds, int frameCount)
            {
                long incTicks = _inclusiveTicks;
                long chdTicks = _childTicks;
                long count = _callCount;
                long max = _maxTicks;

                _inclusiveTicks = 0;
                _childTicks = 0;
                _callCount = 0;
                _maxTicks = 0;

                long excTicks = incTicks > chdTicks ? (incTicks - chdTicks) : 0L;

                double freq = Stopwatch.Frequency;
                float incMs = (float)(incTicks * 1000.0 / freq);
                float excMs = (float)(excTicks * 1000.0 / freq);
                float incUs = (float)(incTicks * 1000000.0 / freq);
                float excUs = (float)(excTicks * 1000000.0 / freq);

                TotalMsPerSec = elapsedSeconds > 0.001f ? (incMs / elapsedSeconds) : 0f;
                ExclusiveMsPerSec = elapsedSeconds > 0.001f ? (excMs / elapsedSeconds) : 0f;

                AvgMsPerFrame = frameCount > 0 ? (incMs / frameCount) : 0f;
                ExclusiveMsPerFrame = frameCount > 0 ? (excMs / frameCount) : 0f;

                AvgUsPerCall = count > 0 ? (incUs / count) : 0f;
                ExclusiveUsPerCall = count > 0 ? (excUs / count) : 0f;

                MaxMsPerCall = (float)(max * 1000.0 / freq);
                CallsPerSec = elapsedSeconds > 0.001f ? (int)(count / elapsedSeconds) : 0;
            }

            public void Reset()
            {
                _inclusiveTicks = 0;
                _childTicks = 0;
                _callCount = 0;
                _maxTicks = 0;
                _stageInclusiveTicks = 0;
                _stageExclusiveTicks = 0;
                _stageCallCount = 0;
                _stageMaxTicks = 0;

                TotalMsPerSec = 0f;
                ExclusiveMsPerSec = 0f;
                AvgMsPerFrame = 0f;
                ExclusiveMsPerFrame = 0f;
                AvgUsPerCall = 0f;
                ExclusiveUsPerCall = 0f;
                MaxMsPerCall = 0f;
                CallsPerSec = 0;
            }

            public string FormatStageTotal(float totalStageSeconds)
            {
                double freq = Stopwatch.Frequency;
                double incMs = _stageInclusiveTicks * 1000.0 / freq;
                double excMs = _stageExclusiveTicks * 1000.0 / freq;
                double avgUs = _stageCallCount > 0 ? (_stageInclusiveTicks * 1000000.0 / freq / _stageCallCount) : 0.0;
                double maxMs = _stageMaxTicks * 1000.0 / freq;
                double callsPerSec = totalStageSeconds > 0.1f ? (_stageCallCount / totalStageSeconds) : 0.0;
                double msPerSec = totalStageSeconds > 0.1f ? (incMs / totalStageSeconds) : 0.0;
                double excMsPerSec = totalStageSeconds > 0.1f ? (excMs / totalStageSeconds) : 0.0;

                return string.Format(
                    "{0,-36} | 累積:{1,6:F2}ms/s (自己:{2,6:F2}ms/s) | 1回平均:{3,6:F1}us | 呼出:{4,6:F0}回/s | 最大:{5,6:F2}ms | 総計:{6,8:F1}ms ({7}回)",
                    DisplayName, msPerSec, excMsPerSec, avgUs, callsPerSec, maxMs, incMs, _stageCallCount);
            }
        }

        // 5大処理メトリック（WorkerAI / CreatureAI は内部で Movement, TargetScan, Pathfinding を呼ぶため HasNestedChildren = true）
        public static readonly Metric Pathfinding = new Metric("Pathfinding", "1.経路探索 (GraphAstar.SearchPath)", false);
        public static readonly Metric TargetScan = new Metric("TargetScan", "2.索敵判定 (Passage.GetEnteredTargets)", false);
        public static readonly Metric WorkerAI = new Metric("WorkerAI", "3.職員AI更新 (Agent/Officer.FixedUpdate)", true);
        public static readonly Metric CreatureAI = new Metric("CreatureAI", "4.怪異AI更新 (Creature/Ordeal.FixedUpdate)", true);
        public static readonly Metric Movement = new Metric("Movement", "5.移動座標更新 (Movable.ProcessMoveNode)", false);

        public static readonly Metric[] AllMetrics = new Metric[]
        {
            Pathfinding,
            TargetScan,
            WorkerAI,
            CreatureAI,
            Movement
        };

        // 入れ子呼び出し（Parent-Child）の減算用アキュムレータ（メインスレッド専用）
        private static int _activeParentDepth;
        private static long _currentParentChildTicks;

        public static void EnterParentScope(out long savedChildTicks)
        {
            savedChildTicks = _currentParentChildTicks;
            _currentParentChildTicks = 0L;
            _activeParentDepth++;
        }

        public static void ExitParentScope(Metric parentMetric, long startTimestamp, long savedChildTicks)
        {
            long elapsed = Stopwatch.GetTimestamp() - startTimestamp;
            long myChildTicks = _currentParentChildTicks;

            _activeParentDepth--;
            if (_activeParentDepth > 0)
            {
                _currentParentChildTicks = savedChildTicks + elapsed;
            }
            else
            {
                _currentParentChildTicks = 0L;
                _activeParentDepth = 0;
            }

            parentMetric.Record(elapsed, myChildTicks);
        }

        public static void RecordLeaf(Metric leafMetric, long startTimestamp)
        {
            long elapsed = Stopwatch.GetTimestamp() - startTimestamp;
            if (_activeParentDepth > 0)
            {
                _currentParentChildTicks += elapsed;
            }
            leafMetric.Record(elapsed, 0L);
        }

        private static float _lastSampleTime;
        private static float _lastLogTime;
        private static float _stageStartTime;
        private static int _frameCounter;
        private static bool _wasManaging;

        public static float TotalExclusiveMsPerSec { get; private set; }
        public static float TotalExclusiveMsPerFrame { get; private set; }

        public static void Update(float unscaledTime)
        {
            _frameCounter++;
            float elapsed = unscaledTime - _lastSampleTime;
            if (elapsed >= 1.0f)
            {
                int frames = _frameCounter;
                _frameCounter = 0;
                _lastSampleTime = unscaledTime;

                float sumExclSec = 0f;
                float sumExclFrame = 0f;
                for (int i = 0; i < AllMetrics.Length; i++)
                {
                    AllMetrics[i].Sample(elapsed, frames);
                    sumExclSec += AllMetrics[i].ExclusiveMsPerSec;
                    sumExclFrame += AllMetrics[i].ExclusiveMsPerFrame;
                }
                TotalExclusiveMsPerSec = sumExclSec;
                TotalExclusiveMsPerFrame = sumExclFrame;

                CheckAndEmitPeriodicLog(unscaledTime);
            }
        }

        private static void CheckAndEmitPeriodicLog(float unscaledTime)
        {
            if (Plugin.EnableProfilerLog != null && !Plugin.EnableProfilerLog.Value)
                return;

            bool isManaging = false;
            try
            {
                if (GameManager.currentGameManager != null &&
                    GameManager.currentGameManager.ManageStarted &&
                    GameManager.currentGameManager.state == GameState.PLAYING)
                {
                    isManaging = true;
                }
            }
            catch { }

            if (isManaging && !_wasManaging)
            {
                _wasManaging = true;
                _stageStartTime = unscaledTime;
                _lastLogTime = unscaledTime;
            }
            else if (!isManaging && _wasManaging)
            {
                _wasManaging = false;
                EmitStageSummaryLog(unscaledTime - _stageStartTime);
                return;
            }

            // 管理パート進行中は 5秒おきに実測ログを出力
            if (isManaging && (unscaledTime - _lastLogTime >= 5.0f))
            {
                _lastLogTime = unscaledTime;
                EmitPeriodicSnapshotLog();
            }
        }

        public static void EmitPeriodicSnapshotLog()
        {
            if (Plugin.Logger == null) return;

            try
            {
                int day = 0;
                int speed = 1;
                int agents = 0;
                int officers = 0;
                int creatures = 0;
                int escaped = 0;
                int ordeals = 0;

                if (PlayerModel.instance != null) day = PlayerModel.instance.GetDay() + 1;
                if (GameManager.currentGameManager != null) speed = GameManager.currentGameManager.gameSpeedLevel;
                if (AgentManager.instance != null && AgentManager.instance.GetAgentList() != null)
                    agents = AgentManager.instance.GetAgentList().Count;
                if (OfficerManager.instance != null && OfficerManager.instance.GetOfficerList() != null)
                    officers = OfficerManager.instance.GetOfficerList().Count;
                if (CreatureManager.instance != null)
                {
                    var cList = CreatureManager.instance.GetCreatureList();
                    if (cList != null)
                    {
                        creatures = cList.Length;
                        for (int i = 0; i < cList.Length; i++)
                        {
                            if (cList[i] != null && cList[i].state == CreatureState.ESCAPE)
                                escaped++;
                        }
                    }
                }
                if (OrdealManager.instance != null)
                {
                    var oList = OrdealManager.instance.GetOrdealCreatureList();
                    if (oList != null) ordeals = oList.Length;
                }

                StringBuilder sb = new StringBuilder(512);
                sb.AppendFormat(
                    "[LME CPU実測 Day:{0} Speed:{1}x | 職員:{2} オフィサー:{3} 幻想体:{4}(脱走:{5}) 試練:{6} | 純粋合計:{7:F2}ms/s ({8:F2}ms/f)]",
                    day, speed, agents, officers, creatures, escaped, ordeals,
                    TotalExclusiveMsPerSec, TotalExclusiveMsPerFrame);
                sb.AppendLine();

                for (int i = 0; i < AllMetrics.Length; i++)
                {
                    var m = AllMetrics[i];
                    if (m.HasNestedChildren)
                    {
                        sb.AppendFormat(
                            "  - {0,-38}: 累積 {1,6:F2} ms/s (自己 {2,6:F2} ms/s) | {3,5:F2} ms/f | 1回平均 {4,6:F1} us (自己 {5,6:F1} us) | {6,5} 回/s | 最大 {7,5:F2} ms",
                            m.DisplayName, m.TotalMsPerSec, m.ExclusiveMsPerSec, m.AvgMsPerFrame,
                            m.AvgUsPerCall, m.ExclusiveUsPerCall, m.CallsPerSec, m.MaxMsPerCall);
                    }
                    else
                    {
                        sb.AppendFormat(
                            "  - {0,-38}: 累積 {1,6:F2} ms/s (自己 {2,6:F2} ms/s) | {3,5:F2} ms/f | 1回平均 {4,6:F1} us                  | {5,5} 回/s | 最大 {6,5:F2} ms",
                            m.DisplayName, m.TotalMsPerSec, m.ExclusiveMsPerSec, m.AvgMsPerFrame,
                            m.AvgUsPerCall, m.CallsPerSec, m.MaxMsPerCall);
                    }
                    if (i < AllMetrics.Length - 1) sb.AppendLine();
                }

                Plugin.Logger.LogInfo(sb.ToString());
            }
            catch { }
        }

        public static void EmitStageSummaryLog(float totalStageSeconds)
        {
            if (Plugin.Logger == null || totalStageSeconds < 1.0f) return;

            try
            {
                StringBuilder sb = new StringBuilder(512);
                sb.AppendFormat("=== [LME CPU実測 ステージ通算サマリー (計測時間: {0:F1}秒)] ===", totalStageSeconds);
                sb.AppendLine();
                for (int i = 0; i < AllMetrics.Length; i++)
                {
                    sb.Append("  * ").Append(AllMetrics[i].FormatStageTotal(totalStageSeconds));
                    if (i < AllMetrics.Length - 1) sb.AppendLine();
                }
                Plugin.Logger.LogInfo(sb.ToString());
            }
            catch { }
        }

        public static void ResetAll()
        {
            if (_wasManaging && _stageStartTime > 0f)
            {
                EmitStageSummaryLog(_lastSampleTime - _stageStartTime);
            }
            _wasManaging = false;
            _frameCounter = 0;
            _activeParentDepth = 0;
            _currentParentChildTicks = 0L;
            TotalExclusiveMsPerSec = 0f;
            TotalExclusiveMsPerFrame = 0f;
            for (int i = 0; i < AllMetrics.Length; i++)
            {
                AllMetrics[i].Reset();
            }
        }
    }
}
