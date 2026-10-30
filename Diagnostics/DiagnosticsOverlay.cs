using System;
using UnityEngine;
using LobotomyModernEngine.Core;
using LobotomyModernEngine.Patches;

namespace LobotomyModernEngine.Diagnostics
{
    /// <summary>
    /// 計画書 Phase 0/1.5/B/Core 準拠: F9 キーでトグル可能なゲーム内診断オーバーレイ
    /// 古いMono 2.0互換（Array.Emptyを一切呼ばないゼロアロケーション設計）
    /// ロード区間時間、Notice.Send/秒、TakeDamage/秒、ネイティブリーク防止数、WorkerPool稼働状況を表示
    /// </summary>
    public class DiagnosticsOverlay : MonoBehaviour
    {
        public static bool IsVisible { get; set; } = false;

        private static readonly GUILayoutOption[] EmptyOptions = new GUILayoutOption[0];

        private const int SampleCount = 120;
        private readonly float[] _frameTimes = new float[SampleCount];
        private readonly float[] _sortedBuffer = new float[SampleCount];
        private int _sampleIndex = 0;

        private float _avgFrameTimeMs = 0f;
        private float _p50FrameTimeMs = 0f;
        private float _p95FrameTimeMs = 0f;
        private float _p99FrameTimeMs = 0f;
        private float _fps = 0f;

        private long _heapMemoryMb = 0;
        private int _gcCount0 = 0;
        private int _gcCount1 = 0;
        private int _gcCount2 = 0;

        // 1秒ごとのメトリクス更新用
        private float _lastSecUpdateTime = 0f;
        private int _displayNoticePerSec = 0;
        private int _displayDamagePerSec = 0;

        private float _lastStatUpdateTime = 0f;
        private GUIStyle _boxStyle;
        private GUIStyle _headerStyle;
        private GUIStyle _labelStyle;
        private GUIStyle _goodStyle;
        private GUIStyle _warnStyle;
        private bool _stylesInitialized = false;

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F9))
            {
                IsVisible = !IsVisible;
            }

            float dt = Time.unscaledDeltaTime;
            _frameTimes[_sampleIndex] = dt;
            _sampleIndex = (_sampleIndex + 1) % SampleCount;

            // 0.25秒おきに統計計算
            if (Time.unscaledTime - _lastStatUpdateTime >= 0.25f)
            {
                CalculateStats();
                _lastStatUpdateTime = Time.unscaledTime;
            }

            // 1秒おきに秒間レート更新
            if (Time.unscaledTime - _lastSecUpdateTime >= 1.0f)
            {
                _displayNoticePerSec = NoticePatch.SendPerSec;
                _displayDamagePerSec = TakeDamagePatch.TakeDamagePerSec;
                MetricsPatches.ResetPerSecondCounters();
                _lastSecUpdateTime = Time.unscaledTime;
            }
        }

        private void CalculateStats()
        {
            Array.Copy(_frameTimes, _sortedBuffer, SampleCount);
            Array.Sort(_sortedBuffer);

            float sum = 0f;
            for (int i = 0; i < SampleCount; i++) sum += _sortedBuffer[i];

            _avgFrameTimeMs = (sum / SampleCount) * 1000f;
            _fps = _avgFrameTimeMs > 0.001f ? (1000f / _avgFrameTimeMs) : 0f;

            _p50FrameTimeMs = _sortedBuffer[(int)(SampleCount * 0.50f)] * 1000f;
            _p95FrameTimeMs = _sortedBuffer[(int)(SampleCount * 0.95f)] * 1000f;
            _p99FrameTimeMs = _sortedBuffer[(int)(SampleCount * 0.99f)] * 1000f;

            _heapMemoryMb = GC.GetTotalMemory(false) / (1024 * 1024);
            _gcCount0 = GC.CollectionCount(0);
            _gcCount1 = GC.CollectionCount(1);
            _gcCount2 = GC.CollectionCount(2);
        }

        private void InitStyles()
        {
            if (_stylesInitialized) return;

            Texture2D bgTex = new Texture2D(1, 1);
            bgTex.SetPixel(0, 0, new Color(0.04f, 0.04f, 0.07f, 0.90f));
            bgTex.Apply();

            _boxStyle = new GUIStyle(GUI.skin.box);
            _boxStyle.normal.background = bgTex;

            _headerStyle = new GUIStyle(GUI.skin.label);
            _headerStyle.fontSize = 12;
            _headerStyle.fontStyle = FontStyle.Bold;
            _headerStyle.normal.textColor = new Color(0.35f, 0.75f, 1f);

            _labelStyle = new GUIStyle(GUI.skin.label);
            _labelStyle.fontSize = 11;
            _labelStyle.normal.textColor = Color.white;

            _goodStyle = new GUIStyle(GUI.skin.label);
            _goodStyle.fontSize = 11;
            _goodStyle.normal.textColor = new Color(0.4f, 1f, 0.4f);

            _warnStyle = new GUIStyle(GUI.skin.label);
            _warnStyle.fontSize = 11;
            _warnStyle.normal.textColor = new Color(1f, 0.65f, 0.2f);

            _stylesInitialized = true;
        }

        private void OnGUI()
        {
            if (!IsVisible) return;

            InitStyles();

            float width = 375f;
            float height = 385f;
            Rect rect = new Rect(10f, 10f, width, height);

            GUI.Box(rect, GUIContent.none, _boxStyle);

            GUILayout.BeginArea(new Rect(18f, 10f, width - 16f, height - 10f));
            
            GUILayout.Label("Lobotomy Modern Engine v0.4.0 (F9)", _headerStyle, EmptyOptions);
            GUILayout.Space(1f);

            // FPS & フレーム時間
            var fpsStyle = _fps >= 55f ? _goodStyle : (_fps >= 30f ? _warnStyle : _labelStyle);
            GUILayout.Label(string.Format("FPS: {0:F1}  ({1:F1} ms)", _fps, _avgFrameTimeMs), fpsStyle, EmptyOptions);
            GUILayout.Label(string.Format("Frame Time: P50: {0:F1}ms | P95: {1:F1}ms | P99: {2:F1}ms", _p50FrameTimeMs, _p95FrameTimeMs, _p99FrameTimeMs), _labelStyle, EmptyOptions);
            GUILayout.Space(2f);

            // メモリ & GC
            GUILayout.Label(string.Format("Managed Heap: {0} MB", _heapMemoryMb), _labelStyle, EmptyOptions);
            GUILayout.Label(string.Format("GC Counts:  Gen0: {0} | Gen1: {1} | Gen2: {2}", _gcCount0, _gcCount1, _gcCount2), _labelStyle, EmptyOptions);
            GUILayout.Space(2f);

            // 実行時最適化 & メモリリーク防止メトリクス
            GUILayout.Label("Memory Leak & Hotpath Metrics:", _headerStyle, EmptyOptions);
            GUILayout.Label(string.Format("• Spine PMA Cache: {0} hits / {1} cached (Leak Cut)", SpineAtlasCachePatch.CacheHitCount, SpineAtlasCachePatch.CachedRegionCount), _goodStyle, EmptyOptions);
            GUILayout.Label(string.Format("• Cursor Leak Cut: {0} avoided ({1} cached)", CursorLeakPatch.AvoidedCursorTexAllocations, CursorLeakPatch.CachedCursorTextureCount), _goodStyle, EmptyOptions);
            GUILayout.Label(string.Format("• Portrait Skip:   {0} LateUpdates skipped", WorkerPortraitPatch.AvoidedPortraitRebuilds), _labelStyle, EmptyOptions);
            GUILayout.Label(string.Format("• Notice.Send:     {0}/s (Avoided Alloc: {1})", _displayNoticePerSec, NoticePatch.AvoidedAllocations), _labelStyle, EmptyOptions);
            GUILayout.Label(string.Format("• TakeDamage:      {0}/s (0-Barrier Cut: {1})", _displayDamagePerSec, TakeDamagePatch.ZeroBarrierAllocAvoided), _labelStyle, EmptyOptions);
            if (!string.IsNullOrEmpty(MetricsPatches.LastLoadedSceneName))
            {
                GUILayout.Label(string.Format("• Last Load:       {0} ({1:F0} ms)", MetricsPatches.LastLoadedSceneName, MetricsPatches.LastLoadDurationMs), _labelStyle, EmptyOptions);
            }
            if (MissingDataGuardPatch.GuardedCreatureCount > 0 || MissingDataGuardPatch.GuardedEquipmentCount > 0)
            {
                GUILayout.Label(string.Format("• Guarded:         {0} Abno, {1} Equip", MissingDataGuardPatch.GuardedCreatureCount, MissingDataGuardPatch.GuardedEquipmentCount), _warnStyle, EmptyOptions);
            }
            GUILayout.Space(2f);

            // 並列・分散基盤ステータス
            GUILayout.Label("Core Architecture (LMF.Core):", _headerStyle, EmptyOptions);
            GUILayout.Label(string.Format("• WorkerPool:      {0}/{1} active (Queue: {2}, Gen: {3})", 
                BoundedWorkerPool.ActiveThreadCount, BoundedWorkerPool.ConfiguredThreadCount, BoundedWorkerPool.CurrentQueueDepth, GenerationTracker.CurrentGeneration), _labelStyle, EmptyOptions);
            GUILayout.Label(string.Format("• Dispatcher:      Queue: {0} (Done: {1}, Budget: {2:F1}ms)",
                MainThreadDispatcher.QueueDepth, MainThreadDispatcher.TotalDispatchedCount, MainThreadDispatcher.FrameBudgetMs), _labelStyle, EmptyOptions);

            GUILayout.EndArea();
        }
    }
}
