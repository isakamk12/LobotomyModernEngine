using System;
using System.Diagnostics;
using System.Text;

namespace LobotomyModernEngine.Core
{
    /// <summary>
    /// ゲーム内主要CPU処理のリアルタイム計測プロファイラ (v0.8.3)
    /// - Stopwatch.GetTimestamp() (高分解能タイマー / QPC) を使用。
    /// - 対象メソッドはすべてUnityメインスレッド上で実行されるため、Interlockedによるバスロック負荷を排し、
    ///   メインスレッド上の単純加算で計測オーバーヘッドを最小化。
    /// - 1回平均(μs/回)、秒間呼出(回/s)、秒間累積(ms/s)、1フレーム平均累積(ms/f)、最大スパイク(ms / 自己ms)を分離算出。
    /// - 入れ子呼び出し（WorkerAI / CreatureAI の内部で呼ばれる Movement / TargetScan / Pathfinding）による
    ///   二重計上を防ぐため、Inclusive（全体時間）と Exclusive（子処理を除いた純粋な自己時間）の双方を集計する。
    /// - v0.8.3:
    ///   1) 一時停止 (GameState.PAUSE) 時にステージ経過時間がリセットされていた集計バグを修正し、
    ///      純粋な管理パート進行時間 (_stageActivePlayingSeconds) のみを分母としてステージ通算サマリーを算出。
    ///   2) WorkerAI (AgentModel.OnFixedUpdate / OfficerModel.OnFixedUpdate) の内部処理を6項目 (3a〜3f) に細分化。
    ///   3) 単発 5.0ms 以上のスパイク発生時に、発生箇所・GC(Gen0)割り込み回数・対象ユニット/ノード条件を即時ログ記録。
    /// </summary>
    public static class GameProfiler
    {
        public const double SpikeLogThresholdMs = 5.0;

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
            private long _maxExclusiveTicks;

            // 1秒スナップショット（UI・ログ出力用）
            public float TotalMsPerSec { get; private set; }          // 秒間累積 (Inclusive ms/s)
            public float ExclusiveMsPerSec { get; private set; }      // 秒間自己累積 (Exclusive ms/s)
            public float AvgMsPerFrame { get; private set; }          // フレーム累積 (Inclusive ms/frame)
            public float ExclusiveMsPerFrame { get; private set; }    // フレーム自己累積 (Exclusive ms/frame)
            public float AvgUsPerCall { get; private set; }           // 1回あたり平均 (Inclusive μs/call)
            public float ExclusiveUsPerCall { get; private set; }     // 1回あたり自己平均 (Exclusive μs/call)
            public float MaxMsPerCall { get; private set; }           // 単発最大スパイク (Inclusive ms)
            public float MaxExclusiveMsPerCall { get; private set; }  // 単発最大自己スパイク (Exclusive ms)
            public int CallsPerSec { get; private set; }              // 秒間呼出回数 (calls/s)

            // ステージ通算統計（日終了時のサマリーログ用）
            private long _stageInclusiveTicks;
            private long _stageExclusiveTicks;
            private long _stageCallCount;
            private long _stageMaxTicks;
            private long _stageMaxExclusiveTicks;

            public Metric(string key, string displayName, bool hasNestedChildren)
            {
                Key = key;
                DisplayName = displayName;
                HasNestedChildren = hasNestedChildren;
            }

            public void Record(long elapsedTicks, long nestedChildTicks)
            {
                long excl = elapsedTicks > nestedChildTicks ? (elapsedTicks - nestedChildTicks) : 0L;

                _inclusiveTicks += elapsedTicks;
                _childTicks += nestedChildTicks;
                _callCount++;
                if (elapsedTicks > _maxTicks)
                {
                    _maxTicks = elapsedTicks;
                }
                if (excl > _maxExclusiveTicks)
                {
                    _maxExclusiveTicks = excl;
                }

                _stageInclusiveTicks += elapsedTicks;
                _stageExclusiveTicks += excl;
                _stageCallCount++;
                if (elapsedTicks > _stageMaxTicks)
                {
                    _stageMaxTicks = elapsedTicks;
                }
                if (excl > _stageMaxExclusiveTicks)
                {
                    _stageMaxExclusiveTicks = excl;
                }
            }

            public void Sample(float elapsedSeconds, int frameCount)
            {
                long incTicks = _inclusiveTicks;
                long chdTicks = _childTicks;
                long count = _callCount;
                long max = _maxTicks;
                long maxExcl = _maxExclusiveTicks;

                _inclusiveTicks = 0;
                _childTicks = 0;
                _callCount = 0;
                _maxTicks = 0;
                _maxExclusiveTicks = 0;

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
                MaxExclusiveMsPerCall = (float)(maxExcl * 1000.0 / freq);
                CallsPerSec = elapsedSeconds > 0.001f ? (int)(count / elapsedSeconds) : 0;
            }

            public void Reset()
            {
                _inclusiveTicks = 0;
                _childTicks = 0;
                _callCount = 0;
                _maxTicks = 0;
                _maxExclusiveTicks = 0;
                _stageInclusiveTicks = 0;
                _stageExclusiveTicks = 0;
                _stageCallCount = 0;
                _stageMaxTicks = 0;
                _stageMaxExclusiveTicks = 0;

                TotalMsPerSec = 0f;
                ExclusiveMsPerSec = 0f;
                AvgMsPerFrame = 0f;
                ExclusiveMsPerFrame = 0f;
                AvgUsPerCall = 0f;
                ExclusiveUsPerCall = 0f;
                MaxMsPerCall = 0f;
                MaxExclusiveMsPerCall = 0f;
                CallsPerSec = 0;
            }

            public void ResetStageOnly()
            {
                _stageInclusiveTicks = 0;
                _stageExclusiveTicks = 0;
                _stageCallCount = 0;
                _stageMaxTicks = 0;
                _stageMaxExclusiveTicks = 0;
            }

            public string FormatStageTotal(float totalStageSeconds)
            {
                double freq = Stopwatch.Frequency;
                double incMs = _stageInclusiveTicks * 1000.0 / freq;
                double excMs = _stageExclusiveTicks * 1000.0 / freq;
                double avgUs = _stageCallCount > 0 ? (_stageInclusiveTicks * 1000000.0 / freq / _stageCallCount) : 0.0;
                double maxMs = _stageMaxTicks * 1000.0 / freq;
                double maxExclMs = _stageMaxExclusiveTicks * 1000.0 / freq;
                double callsPerSec = totalStageSeconds > 0.1f ? (_stageCallCount / totalStageSeconds) : 0.0;
                double msPerSec = totalStageSeconds > 0.1f ? (incMs / totalStageSeconds) : 0.0;
                double excMsPerSec = totalStageSeconds > 0.1f ? (excMs / totalStageSeconds) : 0.0;

                if (HasNestedChildren)
                {
                    return string.Format(
                        "{0,-38} | 累積:{1,6:F2}ms/s (自己:{2,6:F2}ms/s) | 1回平均:{3,6:F1}us | 呼出:{4,6:F0}回/s | 最大:{5,6:F2}ms (自己:{6,6:F2}ms) | 総計:{7,8:F1}ms ({8}回)",
                        DisplayName, msPerSec, excMsPerSec, avgUs, callsPerSec, maxMs, maxExclMs, incMs, _stageCallCount);
                }
                return string.Format(
                    "{0,-38} | 累積:{1,6:F2}ms/s (自己:{2,6:F2}ms/s) | 1回平均:{3,6:F1}us | 呼出:{4,6:F0}回/s | 最大:{5,6:F2}ms                | 総計:{6,8:F1}ms ({7}回)",
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

        // WorkerAI (3.職員AI更新) の内部細分化メトリック (3a〜3f)
        public static readonly Metric SubAgentUpdate = new Metric("SubAgentUpdate", "3a.Agent更新全体 (AgentModel.FixedUpdate)", true);
        public static readonly Metric SubOfficerUpdate = new Metric("SubOfficerUpdate", "3b.Officer更新全体 (OfficerModel.FixedUpdate)", true);
        public static readonly Metric SubUpdateBufState = new Metric("SubUpdateBufState", "3c.バフ状態更新 (UnitModel.UpdateBufState)", false);
        public static readonly Metric SubUseSkill = new Metric("SubUseSkill", "3d.作業・入退室処理 (UseSkill.OnFixedUpdate)", false);
        public static readonly Metric SubOverloadOrdeal = new Metric("SubOverloadOrdeal", "3e.暴走・試練発動 (AddOverloadGague)", false);
        public static readonly Metric SubOfficerNormalAI = new Metric("SubOfficerNormalAI", "3f.Officer自律行動 (ProcessActionNormalOfficer)", false);

        public static readonly Metric[] WorkerSubMetrics = new Metric[]
        {
            SubAgentUpdate,
            SubOfficerUpdate,
            SubUpdateBufState,
            SubUseSkill,
            SubOverloadOrdeal,
            SubOfficerNormalAI
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

        public static void ExitParentScope(
            Metric parentMetric,
            Metric optionalSubParentMetric,
            long startTimestamp,
            long savedChildTicks,
            out double inclusiveMs,
            out double exclusiveMs)
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
            if (optionalSubParentMetric != null)
            {
                optionalSubParentMetric.Record(elapsed, myChildTicks);
            }

            double freq = Stopwatch.Frequency;
            long exclTicks = elapsed > myChildTicks ? (elapsed - myChildTicks) : 0L;
            inclusiveMs = elapsed * 1000.0 / freq;
            exclusiveMs = exclTicks * 1000.0 / freq;
        }

        public static double RecordLeaf(Metric leafMetric, long startTimestamp)
        {
            long elapsed = Stopwatch.GetTimestamp() - startTimestamp;
            if (_activeParentDepth > 0)
            {
                _currentParentChildTicks += elapsed;
            }
            leafMetric.Record(elapsed, 0L);
            return elapsed * 1000.0 / Stopwatch.Frequency;
        }

        /// <summary>
        /// WorkerAI の内部細分化メトリック (3c〜3f) 用の記録メソッド。
        /// 親メトリック (WorkerAI) の _currentParentChildTicks には加算せず、内訳として独立計測する。
        /// </summary>
        public static double RecordSubMetric(Metric subMetric, long startTimestamp)
        {
            long elapsed = Stopwatch.GetTimestamp() - startTimestamp;
            subMetric.Record(elapsed, 0L);
            return elapsed * 1000.0 / Stopwatch.Frequency;
        }

        public static void LogSpikeEvent(string category, double inclusiveMs, double exclusiveMs, int gcGen0Delta, string detail)
        {
            if (Plugin.Logger == null) return;
            if (Plugin.EnableProfilerLog != null && !Plugin.EnableProfilerLog.Value) return;

            int day = 0;
            try
            {
                if (PlayerModel.instance != null) day = PlayerModel.instance.GetDay() + 1;
            }
            catch { }

            Plugin.Logger.LogInfo(string.Format(
                "[LME スパイク検知 Day:{0}] {1} | 所要: {2:F2} ms (自己: {3:F2} ms) | GC(Gen0)発生: +{4}回 | 詳細: {5}",
                day, category, inclusiveMs, exclusiveMs, gcGen0Delta, detail ?? "-"));
        }

        private static float _lastSampleTime;
        private static float _lastLogTime;
        private static float _stageActivePlayingSeconds;
        private static int _frameCounter;
        private static bool _wasStageActive;

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
                for (int i = 0; i < WorkerSubMetrics.Length; i++)
                {
                    WorkerSubMetrics[i].Sample(elapsed, frames);
                }

                TotalExclusiveMsPerSec = sumExclSec;
                TotalExclusiveMsPerFrame = sumExclFrame;

                CheckAndEmitPeriodicLog(unscaledTime, elapsed);
            }
        }

        private static void CheckAndEmitPeriodicLog(float unscaledTime, float sampleElapsedSeconds)
        {
            if (Plugin.EnableProfilerLog != null && !Plugin.EnableProfilerLog.Value)
                return;

            bool isStageActive = false;
            bool isPlayingNow = false;
            try
            {
                if (GameManager.currentGameManager != null &&
                    GameManager.currentGameManager.ManageStarted &&
                    GameManager.currentGameManager.state != GameState.STOP)
                {
                    isStageActive = true;
                    if (GameManager.currentGameManager.state == GameState.PLAYING)
                    {
                        isPlayingNow = true;
                    }
                }
            }
            catch { }

            if (isStageActive && !_wasStageActive)
            {
                // 管理パート開始時（ポーズ解除時ではなくステージ自体の開始時のみ初期化）
                _wasStageActive = true;
                _stageActivePlayingSeconds = 0f;
                _lastLogTime = unscaledTime;
                for (int i = 0; i < AllMetrics.Length; i++)
                {
                    AllMetrics[i].ResetStageOnly();
                }
                for (int i = 0; i < WorkerSubMetrics.Length; i++)
                {
                    WorkerSubMetrics[i].ResetStageOnly();
                }
            }
            else if (!isStageActive && _wasStageActive)
            {
                // 管理パート終了時（一時停止 PAUSE では発火せず、日終了・リトライ・シーン終了時のみ発火）
                _wasStageActive = false;
                EmitStageSummaryLog(_stageActivePlayingSeconds);
                _stageActivePlayingSeconds = 0f;
                return;
            }

            // ポーズ中 (GameState.PAUSE) は経過時間分母に加算せず、PLAYING 中のみ実稼働秒数を積算
            if (isPlayingNow)
            {
                if (sampleElapsedSeconds > 0f && sampleElapsedSeconds < 10.0f)
                {
                    _stageActivePlayingSeconds += sampleElapsedSeconds;
                }

                // 管理パート進行中は 5秒おきに実測ログを出力
                if (unscaledTime - _lastLogTime >= 5.0f)
                {
                    _lastLogTime = unscaledTime;
                    EmitPeriodicSnapshotLog();
                }
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

                StringBuilder sb = new StringBuilder(1024);
                sb.AppendFormat(
                    "[LME CPU実測 Day:{0} Speed:{1}x | 職員:{2} オフィサー:{3} 幻想体:{4}(脱走:{5}) 試練:{6} | 純粋合計:{7:F2}ms/s ({8:F2}ms/f)]",
                    day, speed, agents, officers, creatures, escaped, ordeals,
                    TotalExclusiveMsPerSec, TotalExclusiveMsPerFrame);
                sb.AppendLine();

                for (int i = 0; i < AllMetrics.Length; i++)
                {
                    var m = AllMetrics[i];
                    AppendMetricSnapshotLine(sb, "  - ", m);
                    sb.AppendLine();

                    // 3.職員AI更新 の直下に 3a〜3f の内部細分化内訳を出力
                    if (m == WorkerAI)
                    {
                        for (int j = 0; j < WorkerSubMetrics.Length; j++)
                        {
                            AppendMetricSnapshotLine(sb, "      [内訳] ", WorkerSubMetrics[j]);
                            sb.AppendLine();
                        }
                    }
                }

                Plugin.Logger.LogInfo(sb.ToString().TrimEnd());
            }
            catch { }
        }

        private static void AppendMetricSnapshotLine(StringBuilder sb, string prefix, Metric m)
        {
            if (m.HasNestedChildren)
            {
                sb.AppendFormat(
                    "{0}{1,-38}: 累積 {2,6:F2} ms/s (自己 {3,6:F2} ms/s) | {4,5:F2} ms/f | 1回平均 {5,6:F1} us (自己 {6,6:F1} us) | {7,5} 回/s | 最大 {8,5:F2} ms (自己 {9,5:F2} ms)",
                    prefix, m.DisplayName, m.TotalMsPerSec, m.ExclusiveMsPerSec, m.AvgMsPerFrame,
                    m.AvgUsPerCall, m.ExclusiveUsPerCall, m.CallsPerSec, m.MaxMsPerCall, m.MaxExclusiveMsPerCall);
            }
            else
            {
                sb.AppendFormat(
                    "{0}{1,-38}: 累積 {2,6:F2} ms/s (自己 {3,6:F2} ms/s) | {4,5:F2} ms/f | 1回平均 {5,6:F1} us                  | {6,5} 回/s | 最大 {7,5:F2} ms",
                    prefix, m.DisplayName, m.TotalMsPerSec, m.ExclusiveMsPerSec, m.AvgMsPerFrame,
                    m.AvgUsPerCall, m.CallsPerSec, m.MaxMsPerCall);
            }
        }

        public static void EmitStageSummaryLog(float totalStageSeconds)
        {
            if (Plugin.Logger == null || totalStageSeconds < 1.0f) return;

            try
            {
                StringBuilder sb = new StringBuilder(1024);
                sb.AppendFormat("=== [LME CPU実測 ステージ通算サマリー (実稼働PLAYING時間: {0:F1}秒)] ===", totalStageSeconds);
                sb.AppendLine();
                for (int i = 0; i < AllMetrics.Length; i++)
                {
                    var m = AllMetrics[i];
                    sb.Append("  * ").Append(m.FormatStageTotal(totalStageSeconds)).AppendLine();
                    if (m == WorkerAI)
                    {
                        for (int j = 0; j < WorkerSubMetrics.Length; j++)
                        {
                            sb.Append("      [内訳] ").Append(WorkerSubMetrics[j].FormatStageTotal(totalStageSeconds)).AppendLine();
                        }
                    }
                }
                Plugin.Logger.LogInfo(sb.ToString().TrimEnd());
            }
            catch { }
        }

        public static void ResetAll()
        {
            if (_wasStageActive && _stageActivePlayingSeconds >= 1.0f)
            {
                EmitStageSummaryLog(_stageActivePlayingSeconds);
            }
            _wasStageActive = false;
            _stageActivePlayingSeconds = 0f;
            _frameCounter = 0;
            _activeParentDepth = 0;
            _currentParentChildTicks = 0L;
            TotalExclusiveMsPerSec = 0f;
            TotalExclusiveMsPerFrame = 0f;
            for (int i = 0; i < AllMetrics.Length; i++)
            {
                AllMetrics[i].Reset();
            }
            for (int i = 0; i < WorkerSubMetrics.Length; i++)
            {
                WorkerSubMetrics[i].Reset();
            }
        }
    }
}
