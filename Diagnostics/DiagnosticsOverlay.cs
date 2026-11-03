using System;
using UnityEngine;
using LobotomyModernEngine.Core;
using LobotomyModernEngine.Patches;

namespace LobotomyModernEngine.Diagnostics
{
    /// <summary>
    /// v0.7.5: 各パッチが実際に稼働・機能しているかを日本語＆色分けバッジで一目で確認できる診断モニター
    /// - 画面左上のミニ稼働バッジ（集計単位: モジュール数とメソッドフック数を明確に分離表記）
    /// - F9 キー（またはミニバッジクリック）で開く詳細リアルタイム稼働モニター
    /// - F8 キー（またはパネル内ボタン）でデバッグ装備強化の即時切替
    /// </summary>
    public class DiagnosticsOverlay : MonoBehaviour
    {
        public static bool IsVisible { get; set; } = false;
        public static bool ShowMiniStatusBadge { get; set; } = true;

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
        private GUIStyle _miniBadgeStyle;
        private GUIStyle _headerStyle;
        private GUIStyle _labelStyle;
        private GUIStyle _goodStyle;
        private GUIStyle _warnStyle;
        private GUIStyle _buttonStyle;
        private bool _stylesInitialized = false;

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F9))
            {
                IsVisible = !IsVisible;
            }

            if (Input.GetKeyDown(KeyCode.F8))
            {
                DebugGearBuffPatch.ToggleBuff();
                DebugAgentBuffPatch.SyncWithConfig();
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
            bgTex.hideFlags = HideFlags.HideAndDontSave;
            bgTex.SetPixel(0, 0, new Color(0.04f, 0.05f, 0.09f, 0.92f));
            bgTex.Apply();

            Texture2D badgeTex = new Texture2D(1, 1);
            badgeTex.hideFlags = HideFlags.HideAndDontSave;
            badgeTex.SetPixel(0, 0, new Color(0.05f, 0.12f, 0.08f, 0.85f));
            badgeTex.Apply();

            _boxStyle = new GUIStyle(GUI.skin.box);
            _boxStyle.normal.background = bgTex;

            _miniBadgeStyle = new GUIStyle(GUI.skin.button);
            _miniBadgeStyle.normal.background = badgeTex;
            _miniBadgeStyle.fontSize = 11;
            _miniBadgeStyle.fontStyle = FontStyle.Bold;
            _miniBadgeStyle.normal.textColor = new Color(0.45f, 1f, 0.55f);
            _miniBadgeStyle.alignment = TextAnchor.MiddleLeft;
            _miniBadgeStyle.padding = new RectOffset(8, 8, 2, 2);

            _headerStyle = new GUIStyle(GUI.skin.label);
            _headerStyle.fontSize = 12;
            _headerStyle.fontStyle = FontStyle.Bold;
            _headerStyle.normal.textColor = new Color(0.35f, 0.80f, 1f);

            _labelStyle = new GUIStyle(GUI.skin.label);
            _labelStyle.fontSize = 11;
            _labelStyle.normal.textColor = Color.white;

            _goodStyle = new GUIStyle(GUI.skin.label);
            _goodStyle.fontSize = 11;
            _goodStyle.normal.textColor = new Color(0.4f, 1f, 0.45f);

            _warnStyle = new GUIStyle(GUI.skin.label);
            _warnStyle.fontSize = 11;
            _warnStyle.normal.textColor = new Color(1f, 0.70f, 0.25f);

            _buttonStyle = new GUIStyle(GUI.skin.button);
            _buttonStyle.fontSize = 11;
            _buttonStyle.fontStyle = FontStyle.Bold;

            _stylesInitialized = true;
        }

        private void OnGUI()
        {
            InitStyles();

            // パネル非表示時は画面左上にコンパクトな稼働確認バッジを表示（集計単位を明確に区別）
            if (!IsVisible)
            {
                if (ShowMiniStatusBadge && (Plugin.ShowMiniHudBadge == null || Plugin.ShowMiniHudBadge.Value))
                {
                    bool gearOn = DebugGearBuffPatch.IsCurrentlyApplied;
                    bool lobOn = StatUpgradeCostPatch.IsEnabled;
                    string badgeText = string.Format(
                        "● LME v{0} [{1}/{2}モジュール適用済 / {3}メソッドフック済] | {4:F0} FPS | 1-LOB:{5} | 神装備:{6} [F9:詳細]",
                        PluginInfo.PLUGIN_VERSION,
                        Plugin.HookedModuleCount,
                        Plugin.TotalModuleCount,
                        Plugin.HookedTargetMethodCount,
                        _fps,
                        lobOn ? "ON" : "OFF",
                        gearOn ? "ON" : "OFF");

                    if (GUI.Button(new Rect(8f, 6f, 630f, 22f), badgeText, _miniBadgeStyle))
                    {
                        IsVisible = true;
                    }
                }
                return;
            }

            float width = 620f;
            float height = 755f;
            Rect rect = new Rect(10f, 10f, width, height);

            GUI.Box(rect, GUIContent.none, _boxStyle);

            GUILayout.BeginArea(new Rect(18f, 12f, width - 16f, height - 14f));

            GUILayout.Label(
                string.Format("Lobotomy Modern Engine v{0} 稼働診断モニター (F9:開閉 / F8:装備)", PluginInfo.PLUGIN_VERSION),
                _headerStyle,
                EmptyOptions);

            GUILayout.Label(
                string.Format("パッチ適用状態: [{0}/{1} 機能モジュール適用済 / 計{2} メソッドフック済]",
                    Plugin.HookedModuleCount,
                    Plugin.TotalModuleCount,
                    Plugin.HookedTargetMethodCount),
                _goodStyle,
                EmptyOptions);

            string transpilerStatus;
            GUIStyle transpilerStyle;
            if (TakeDamagePatch.IsTranspilerVerified)
            {
                transpilerStatus = "置換検証済 (8/8箇所一致・置換)";
                transpilerStyle = _goodStyle;
            }
            else if (TakeDamagePatch.TotalReplacedCallSites > 0)
            {
                transpilerStatus = string.Format("部分置換/検証中 (TakeDamage:{0}/4, WithoutEffect:{1}/4)",
                    TakeDamagePatch.ReplacedCallSitesInTakeDamage,
                    TakeDamagePatch.ReplacedCallSitesInTakeDamageWithoutEffect);
                transpilerStyle = _warnStyle;
            }
            else
            {
                transpilerStatus = "未適用 / 未フック";
                transpilerStyle = _warnStyle;
            }

            GUILayout.Label(
                string.Format("IL Transpiler: [{0}]", transpilerStatus),
                transpilerStyle,
                EmptyOptions);

            GUILayout.Space(2f);

            // 1. パフォーマンス・メモリ統計
            var fpsStyle = _fps >= 55f ? _goodStyle : (_fps >= 30f ? _warnStyle : _labelStyle);
            GUILayout.Label(
                string.Format("FPS: {0:F1} ({1:F1} ms)  |  Frame P50:{2:F1}ms / P95:{3:F1}ms / P99:{4:F1}ms",
                    _fps, _avgFrameTimeMs, _p50FrameTimeMs, _p95FrameTimeMs, _p99FrameTimeMs),
                fpsStyle,
                EmptyOptions);
            GUILayout.Label(
                string.Format("Managed Heap: {0} MB  |  GC回数 Gen0:{1} / Gen1:{2} / Gen2:{3}",
                    _heapMemoryMb, _gcCount0, _gcCount1, _gcCount2),
                _labelStyle,
                EmptyOptions);
            GUILayout.Space(3f);

            // 2. キャッシュ稼働状況 & 負荷オーバーヘッド計測（動的実働判定）
            GUILayout.Label("【1】メモリリーク防止・キャッシュ稼働状況 (実働監視):", _headerStyle, EmptyOptions);

            bool spineActive = SpineAtlasCachePatch.CacheHitCount > 0 || SpineAtlasCachePatch.DestroyedNativeObjectCount > 0;
            GUILayout.Label(
                string.Format("• [{0}] ①Spine PMAキャッシュ: 再利用 {1}回 / 保持 {2}個 (安全解放: {3}個 / 破棄保留: {4}個)",
                    spineActive ? "実働確認" : "未発生/待機中",
                    SpineAtlasCachePatch.CacheHitCount,
                    SpineAtlasCachePatch.CachedRegionCount,
                    SpineAtlasCachePatch.DestroyedNativeObjectCount,
                    SpineAtlasCachePatch.RetainedInUseObjectCount),
                spineActive ? _goodStyle : _labelStyle,
                EmptyOptions);

            bool cursorActive = CursorLeakPatch.AvoidedCursorTexAllocations > 0 || CursorLeakPatch.DestroyedCursorTexCount > 0;
            GUILayout.Label(
                string.Format("• [{0}] ①照準カーソルリーク遮断: 生成回避 {1}回 / 保持 {2}個 (安全解放: {3}個 / 破棄保留: {4}個)",
                    cursorActive ? "実働確認" : "未発生/待機中",
                    CursorLeakPatch.AvoidedCursorTexAllocations,
                    CursorLeakPatch.CachedCursorTextureCount,
                    CursorLeakPatch.DestroyedCursorTexCount,
                    CursorLeakPatch.RetainedCursorCount),
                cursorActive ? _goodStyle : _labelStyle,
                EmptyOptions);

            bool dirtyActive = WorkerPortraitPatch.AvoidedPortraitRebuilds > 0 || WorkerPortraitPatch.ExecutedPortraitRebuilds > 0;
            GUILayout.Label(
                string.Format("• [{0}] ②Dirty差分更新: スキップ {1}回 (実更新 {2}回)",
                    dirtyActive ? "実働確認" : "未発生/待機中",
                    WorkerPortraitPatch.AvoidedPortraitRebuilds,
                    WorkerPortraitPatch.ExecutedPortraitRebuilds),
                dirtyActive ? _goodStyle : _labelStyle,
                EmptyOptions);

            GUILayout.Label(
                string.Format("   └ 【実測プロファイラ】ハッシュ監視負荷: 最終 {0:F1}μs | 平均 {1:F1}μs | 最大 {2:F1}μs (計 {3}回)",
                    WorkerPortraitPatch.LastHashCalcMicroseconds,
                    WorkerPortraitPatch.AvgHashCalcMicroseconds,
                    WorkerPortraitPatch.MaxHashCalcMicroseconds,
                    WorkerPortraitPatch.TotalHashCalcCount),
                _labelStyle,
                EmptyOptions);

            bool listActive = WorkerListCachePatch.AvoidedReadOnlyAllocations > 0 || SpineOptimizerPatch.AvoidedCameraLookups > 0;
            GUILayout.Label(
                string.Format("• [{0}] ③職員リスト&カメラ共有: ReadOnly回避 {1}回 / Camera.main回避 {2}回",
                    listActive ? "実働確認" : "未発生/待機中",
                    WorkerListCachePatch.AvoidedReadOnlyAllocations,
                    SpineOptimizerPatch.AvoidedCameraLookups),
                listActive ? _goodStyle : _labelStyle,
                EmptyOptions);

            bool dmgActive = TakeDamagePatch.ZeroBarrierAllocAvoided > 0 || TakeDamagePatch.TotalTakeDamageCount > 0;
            GUILayout.Label(
                string.Format("• [{0}] ④TakeDamage IL最適化: 被弾 {1}/s (0バリア回避: {2}回 / 累計被弾: {3}回)",
                    dmgActive ? "実働確認" : (TakeDamagePatch.IsTranspilerVerified ? "置換済/被弾待機中" : "未検証"),
                    _displayDamagePerSec,
                    TakeDamagePatch.ZeroBarrierAllocAvoided,
                    TakeDamagePatch.TotalTakeDamageCount),
                dmgActive ? _goodStyle : (TakeDamagePatch.IsTranspilerVerified ? _goodStyle : _warnStyle),
                EmptyOptions);

            bool noticeActive = NoticePatch.AvoidedAllocations > 0;
            GUILayout.Label(
                string.Format("• [{0}] Notice.Send CoW化: 配信 {1}/s (ToArray配列生成回避: {2}回)",
                    noticeActive ? "実働確認" : "未発生/待機中",
                    _displayNoticePerSec,
                    NoticePatch.AvoidedAllocations),
                noticeActive ? _goodStyle : _labelStyle,
                EmptyOptions);

            GUILayout.Space(3f);

            // 3. バニラバグ修正・UI改善・育成緩和の稼働状況
            GUILayout.Label("【2】バニラ全22大バグ完全修正・UI文字改善・デバッグ機能:", _headerStyle, EmptyOptions);

            bool bugfixActive = VanillaBugFixPatch.TotalTriggeredFixes > 0;
            GUILayout.Label(
                string.Format("• [{0}] バニラ固有バグ修正: {1}/22 項目有効 (実ゲーム内でバグ発動阻止: {2}回)",
                    bugfixActive ? "実働確認" : "パッチ適用済/待機中",
                    VanillaBugFixPatch.ActiveBugFixGroupCount,
                    VanillaBugFixPatch.TotalTriggeredFixes),
                bugfixActive ? _goodStyle : _labelStyle,
                EmptyOptions);

            bool textActive = UITextReadabilityPatch.EnhancedTextCount > 0;
            GUILayout.Label(
                string.Format("• [{0}] 図鑑・ギフト文字サイズ改善: 累計 {1} 箇所のテキストを拡大補正 (ロード画面はバニラ維持)",
                    textActive ? "実働確認" : "未表示/待機中",
                    UITextReadabilityPatch.EnhancedTextCount),
                textActive ? _goodStyle : _labelStyle,
                EmptyOptions);

            bool lobEnabled = StatUpgradeCostPatch.IsEnabled;
            GUILayout.Label(
                string.Format("• [{0}] ステータス1-LOB強化: {1}",
                    lobEnabled ? "ON / 稼働中" : "OFF / バニラ",
                    lobEnabled ? "全ランク 1段階 = 1 LOB 適用中" : "バニラ通常のLOBコスト"),
                lobEnabled ? _goodStyle : _warnStyle,
                EmptyOptions);

            bool gearBuffOn = DebugGearBuffPatch.IsCurrentlyApplied;
            GUILayout.Label(
                string.Format("• [{0}] 初期装備&懺悔強化: {1} (対象 {2} 装備)",
                    gearBuffOn ? "ON / 稼働中" : "OFF / バニラ",
                    gearBuffOn ? "威力10万~15万・弱点自動変換・0.001倍耐性" : "バニラ通常性能",
                    DebugGearBuffPatch.BuffedEquipmentCount),
                gearBuffOn ? _goodStyle : _warnStyle,
                EmptyOptions);

            bool agentBuffOn = DebugAgentBuffPatch.IsCurrentlyApplied;
            GUILayout.Label(
                string.Format("• [{0}] 初期&雇用ランク5職員: {1} (現在 {2} 名強化中)",
                    agentBuffOn ? "ON / 稼働中" : "OFF / バニラ",
                    agentBuffOn ? "全ステータス100(ランクV / EX)・即戦力化" : "バニラ通常ステータス",
                    DebugAgentBuffPatch.BuffedAgentCount),
                agentBuffOn ? _goodStyle : _warnStyle,
                EmptyOptions);

            GUILayout.Space(2f);
            GUILayout.Label(
                string.Format("【3】主要処理CPU実測プロファイラ (毎秒更新 / 二重計上除外の純粋合計: {0:F2} ms/秒 [{1:F2} ms/f]):",
                    GameProfiler.TotalExclusiveMsPerSec,
                    GameProfiler.TotalExclusiveMsPerFrame),
                _headerStyle,
                EmptyOptions);

            for (int i = 0; i < GameProfiler.AllMetrics.Length; i++)
            {
                var m = GameProfiler.AllMetrics[i];
                string colorTag = m.TotalMsPerSec > 10.0f ? "<color=#FF6B6B>" : (m.TotalMsPerSec > 2.0f ? "<color=#FFD166>" : "<color=#06D6A0>");
                if (m.HasNestedChildren)
                {
                    GUILayout.Label(
                        string.Format("• {0}: {1}累積 {2:F2}ms/s (自己 {3:F2}ms/s)</color> | 1回 {4:F1}μs | {5}回/s | 最大 {6:F2}ms (自己 {7:F2}ms)",
                            m.DisplayName,
                            colorTag,
                            m.TotalMsPerSec,
                            m.ExclusiveMsPerSec,
                            m.AvgUsPerCall,
                            m.CallsPerSec,
                            m.MaxMsPerCall,
                            m.MaxExclusiveMsPerCall),
                        _labelStyle,
                        EmptyOptions);
                }
                else
                {
                    GUILayout.Label(
                        string.Format("• {0}: {1}累積 {2:F2}ms/s ({3:F2}ms/f)</color> | 1回 {4:F1}μs | {5}回/s | 最大 {6:F2}ms",
                            m.DisplayName,
                            colorTag,
                            m.TotalMsPerSec,
                            m.AvgMsPerFrame,
                            m.AvgUsPerCall,
                            m.CallsPerSec,
                            m.MaxMsPerCall),
                        _labelStyle,
                        EmptyOptions);
                }

                if (m == GameProfiler.WorkerAI)
                {
                    GUILayout.Label(
                        string.Format("   └ [内訳] 3a.Agent:{0:F2}ms/s(自己{1:F2}) | 3b.Officer:{2:F2}ms/s(自己{3:F2}) | 3c.Buf:{4:F2}ms/s | 3d.Skill:{5:F2}ms/s | 3e.暴走:{6:F2}ms/s | 3f.OffAI:{7:F2}ms/s",
                            GameProfiler.SubAgentUpdate.TotalMsPerSec,
                            GameProfiler.SubAgentUpdate.ExclusiveMsPerSec,
                            GameProfiler.SubOfficerUpdate.TotalMsPerSec,
                            GameProfiler.SubOfficerUpdate.ExclusiveMsPerSec,
                            GameProfiler.SubUpdateBufState.TotalMsPerSec,
                            GameProfiler.SubUseSkill.TotalMsPerSec,
                            GameProfiler.SubOverloadOrdeal.TotalMsPerSec,
                            GameProfiler.SubOfficerNormalAI.TotalMsPerSec),
                        _labelStyle,
                        EmptyOptions);
                }
            }

            GUILayout.BeginHorizontal(EmptyOptions);
            if (GUILayout.Button(gearBuffOn ? "[F8] デバッグ強化(装備+職員)をバニラに戻す" : "[F8] デバッグ強化(装備+ランク5職員)を適用", _buttonStyle, EmptyOptions))
            {
                DebugGearBuffPatch.ToggleBuff();
                DebugAgentBuffPatch.SyncWithConfig();
            }
            if (GUILayout.Button(lobEnabled ? "1-LOB強化をOFFにする" : "1-LOB強化をONにする", _buttonStyle, EmptyOptions))
            {
                StatUpgradeCostPatch.Toggle();
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal(EmptyOptions);
            if (GUILayout.Button(ShowMiniStatusBadge ? "左上ミニ稼働バッジを隠す" : "左上ミニ稼働バッジを表示", _buttonStyle, EmptyOptions))
            {
                ShowMiniStatusBadge = !ShowMiniStatusBadge;
                if (Plugin.ShowMiniHudBadge != null)
                {
                    Plugin.ShowMiniHudBadge.Value = ShowMiniStatusBadge;
                }
            }
            if (GUILayout.Button("[F9] 詳細パネルを閉じる", _buttonStyle, EmptyOptions))
            {
                IsVisible = false;
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(2f);

            // 4. コアスレッドプール＆セーブ保護
            GUILayout.Label(
                string.Format("Core: WorkerPool {0}/{1} (Gen:{2}) | Guarded MissingMOD: Abno {3}, Equip {4}",
                    BoundedWorkerPool.ActiveThreadCount,
                    BoundedWorkerPool.ConfiguredThreadCount,
                    GenerationTracker.CurrentGeneration,
                    MissingDataGuardPatch.GuardedCreatureCount,
                    MissingDataGuardPatch.GuardedEquipmentCount),
                _labelStyle,
                EmptyOptions);

            GUILayout.EndArea();
        }
    }
}
