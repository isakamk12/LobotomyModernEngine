using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LobotomyModernEngine.Core;
using LobotomyModernEngine.Diagnostics;
using LobotomyModernEngine.Patches;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LobotomyModernEngine
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID = "com.isaka.lobotonymodernengine";
        public const string PLUGIN_NAME = "Lobotomy Modern Engine";
        public const string PLUGIN_VERSION = "0.6.0";

        internal static new ManualLogSource Logger;
        internal static Harmony HarmonyInstance;

        // Config 設定（全パッチを個別にON/OFF・切り戻し可能）
        public static ConfigEntry<bool> EnableEngineOverhaul;
        public static ConfigEntry<bool> EnableUITextReadabilityFix;
        public static ConfigEntry<float> UITextScaleMultiplier;
        public static ConfigEntry<int> UITextMinFontSize;
        public static ConfigEntry<bool> EnableSpineAtlasCache;
        public static ConfigEntry<bool> EnableCursorLeakFix;
        public static ConfigEntry<bool> EnableWorkerPortraitOptimization;
        public static ConfigEntry<bool> EnableWorkerListCache;
        public static ConfigEntry<bool> EnableSpineOptimizerCache;
        public static ConfigEntry<bool> EnableDebugStarterGearBuff;
        public static ConfigEntry<bool> EnableNoticeCopyOnWrite;
        public static ConfigEntry<bool> EnableZeroBarrierOptimization;
        public static ConfigEntry<bool> EnableWorkerSpriteCache;
        public static ConfigEntry<bool> EnableIsolateRoomOptimization;
        public static ConfigEntry<bool> EnableSefiraPanelOptimization;
        public static ConfigEntry<bool> EnablePassableOptimization;
        public static ConfigEntry<int> MaxWorkerThreads;
        public static ConfigEntry<bool> EnableProfilerLog;

        private float _lastPruneTime;

        private void Awake()
        {
            Logger = base.Logger;
            Logger.LogInfo(string.Format("{0} v{1} initializing on CLR {2}...", PLUGIN_NAME, PLUGIN_VERSION, Environment.Version));

            // 設定の初期化
            EnableEngineOverhaul = Config.Bind("General", "EnableEngineOverhaul", true, "全般的な最適化パッチを有効化する");
            EnableUITextReadabilityFix = Config.Bind("UIReadability", "EnableUITextReadabilityFix", true, "エンサイクロペディア・ギフト・装備詳細・ローディング画面のフォントサイズ拡大＆文字切れ修正を有効化する");
            UITextScaleMultiplier = Config.Bind("UIReadability", "UITextScaleMultiplier", 1.25f, "UIテキストの拡大倍率 (推奨: 1.20 ~ 1.35)");
            UITextMinFontSize = Config.Bind("UIReadability", "UITextMinFontSize", 18, "UIテキストの最小フォントサイズ下限 (推奨: 18 ~ 20)");
            EnableSpineAtlasCache = Config.Bind("MemoryLeakFix", "SpineAtlasCache", true, "アニメーション切替時の Spine Texture2D/Material 永久リーク＆CPUスパイク防止");
            EnableCursorLeakFix = Config.Bind("MemoryLeakFix", "CursorLeakFix", true, "弾丸カーソル・照準アニメーションの毎フレーム Texture2D 永久リーク防止");
            EnableWorkerPortraitOptimization = Config.Bind("Optimization", "WorkerPortraitOptimization", true, "WorkerPortraitSetter.LateUpdate の毎フレーム全再構築抑止");
            EnableWorkerListCache = Config.Bind("Optimization", "WorkerListCache", true, "AgentManager/OfficerManager の ReadOnlyCollection 毎フレーム生成をゼロアロケーション化");
            EnableSpineOptimizerCache = Config.Bind("Optimization", "SpineOptimizerCache", true, "SpineOptimizerModule/WorkerUnit の毎フレーム Camera.main タグ検索を1フレーム1回に共有化");
            EnableNoticeCopyOnWrite = Config.Bind("Optimization", "NoticeCopyOnWrite", true, "Notice.Send の Copy-on-Write ゼロアロケーション化");
            EnableZeroBarrierOptimization = Config.Bind("Optimization", "ZeroBarrierOptimization", true, "TakeDamage のバリア0件時アロケーション消去 (Transpiler)");
            EnableWorkerSpriteCache = Config.Bind("Optimization", "WorkerSpriteCache", true, "WorkerSpriteSetter の GetComponent キャッシュ化");
            EnableIsolateRoomOptimization = Config.Bind("Optimization", "IsolateRoomOptimization", true, "IsolateRoom の UI Canvas 再描画スパム防止");
            EnableSefiraPanelOptimization = Config.Bind("Optimization", "SefiraPanelOptimization", true, "SefiraPanel の NoData/Portrait キャッシュ化");
            EnablePassableOptimization = Config.Bind("Optimization", "PassableOptimization", true, "CheckPassable の走査最適化");
            MaxWorkerThreads = Config.Bind("Parallel", "MaxWorkerThreads", 4, "最大ワーカースレッド数 (1-4, 初回タスク投入時に遅延起動)");
            EnableDebugStarterGearBuff = Config.Bind("Debug", "DebugStarterGearBuff", true, "デバッグ・検証短縮用に初期装備(警棒/スーツ)と懺悔(武器/防具)を強化する (F8またはF9画面で即切替可)");
            EnableProfilerLog = Config.Bind("Debug", "EnableProfilerLog", false, "定期的にFPS・メモリ・GC情報をログ出力する");

            // LMF.Core 基盤コンポーネントの初期化
            MainThreadDispatcher.Initialize(base.gameObject);
            FrameBudgetScheduler.Initialize(base.gameObject);
            BoundedWorkerPool.Initialize(MaxWorkerThreads.Value);

            if (EnableEngineOverhaul.Value)
            {
                HarmonyInstance = new Harmony(PLUGIN_GUID);

                // Phase 1.5 安定化パッチ
                if (EnableWorkerSpriteCache.Value)
                    ApplyAndVerifyPatch(typeof(WorkerSpriteSetterPatch), "WorkerSpriteSetter (GetComponent Cache)");

                if (EnableIsolateRoomOptimization.Value)
                    ApplyAndVerifyPatch(typeof(IsolateRoomPatch), "IsolateRoom (Canvas Dirty Optimization & Finalizer)");

                if (EnableSefiraPanelOptimization.Value)
                    ApplyAndVerifyPatch(typeof(SefiraPanelPatch), "SefiraPanel (NoData/Portrait Dirty Check)");

                if (EnablePassableOptimization.Value)
                    ApplyAndVerifyPatch(typeof(CheckPassablePatch), "MovableObjectNode (CheckPassable Scan Optimization)");

                // v0.4.0 3大ネイティブメモリリーク＆UIスパム修正パッチ
                if (EnableSpineAtlasCache.Value)
                    ApplyAndVerifyPatch(typeof(SpineAtlasCachePatch), "SpineAtlasCache (Animation Texture2D/Material Leak Fix)");

                if (EnableCursorLeakFix.Value)
                {
                    ApplyAndVerifyPatch(typeof(CursorLeakPatch.CursorManager_SetCursorScale_Patch), "CursorLeakFix (SetCursorScale Quantized Cache)");
                    ApplyAndVerifyPatch(typeof(CursorLeakPatch.WorkerSpriteManager_ScaleTexture_Patch), "CursorLeakFix (ScaleTexture Cache)");
                }

                if (EnableWorkerPortraitOptimization.Value)
                    ApplyAndVerifyPatch(typeof(WorkerPortraitPatch), "WorkerPortraitSetter (LateUpdate Dirty Check)");

                // v0.5.0 リストReadOnlyラッパーキャッシュ & カメラ最適化 & デバッグ装備強化
                if (EnableWorkerListCache.Value)
                {
                    ApplyAndVerifyPatch(typeof(WorkerListCachePatch.AgentManager_GetAgentList_Patch), "WorkerListCache (AgentList ReadOnly Cache)");
                    ApplyAndVerifyPatch(typeof(WorkerListCachePatch.AgentManager_GetAgentSpareList_Patch), "WorkerListCache (AgentSpareList ReadOnly Cache)");
                    ApplyAndVerifyPatch(typeof(WorkerListCachePatch.OfficerManager_GetOfficerList_Patch), "WorkerListCache (OfficerList ReadOnly Cache)");
                }

                if (EnableSpineOptimizerCache.Value)
                {
                    ApplyAndVerifyPatch(typeof(SpineOptimizerPatch.SpineOptimizerModule_Update_Patch), "SpineOptimizerCache (Creature Camera Snapshot)");
                    ApplyAndVerifyPatch(typeof(SpineOptimizerPatch.WorkerUnit_UpdateCheckInCamera_Patch), "SpineOptimizerCache (WorkerUnit Camera Check)");
                    ApplyAndVerifyPatch(typeof(SpineOptimizerPatch.AgentUnit_UpdateAnimationQuality_Patch), "SpineOptimizerCache (AgentUnit Quality)");
                    ApplyAndVerifyPatch(typeof(SpineOptimizerPatch.OfficerUnit_UpdateAnimationQuality_Patch), "SpineOptimizerCache (OfficerUnit Quality)");
                }

                // デバッグ初期装備強化（ゲーム中にもON/OFF切替できるよう常時フックし、Config値に従って適用/復元）
                ApplyAndVerifyPatch(typeof(DebugGearBuffPatch.EquipmentDataLoader_Load_Patch), "DebugGearBuff (Starter & Penitence Gear Hook)");

                // v0.6.0 UIテキストサイズ・エンサイクロペディア・ギフト・ローディング文字切れ包括修正パッチ
                if (EnableUITextReadabilityFix.Value)
                {
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.CreatureInfoWindow_SetDesc_Patch), "UITextReadability (Encyclopedia Story SetDesc)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.GiftSlot_SetModel_TypeInfo_Patch), "UITextReadability (Encyclopedia GiftSlot TypeInfo)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.GiftSlot_SetModel_Model_Patch), "UITextReadability (Encyclopedia GiftSlot Model)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.GiftSlot_SetProb_Patch), "UITextReadability (Encyclopedia GiftSlot Prob)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.WeaponSlot_SetModel_TypeInfo_Patch), "UITextReadability (Encyclopedia WeaponSlot TypeInfo)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.WeaponSlot_SetModel_Model_Patch), "UITextReadability (Encyclopedia WeaponSlot Model)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.ArmorSlot_SetModel_TypeInfo_Patch), "UITextReadability (Encyclopedia ArmorSlot TypeInfo)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.ArmorSlot_SetModel_Model_Patch), "UITextReadability (Encyclopedia ArmorSlot Model)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.EquipSlot_CheckBlocked_Patch), "UITextReadability (Encyclopedia EquipSlot CheckBlocked)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.EquipSlot_NoData_Patch), "UITextReadability (Encyclopedia EquipSlot NoData)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.CreatureInfoCaretakingSlot_SetData_Patch), "UITextReadability (Encyclopedia CaretakingSlot)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.CreatureInfoObserveLevelEffectSlot_SetText_Patch), "UITextReadability (Encyclopedia ObserveBonusSlot)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.CreatureInfoStatRoot_Initialize0_Patch), "UITextReadability (Encyclopedia StatRoot Init0)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.CreatureInfoStatRoot_Initialize1_Patch), "UITextReadability (Encyclopedia StatRoot Init1)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.CreatureInfoWorkSlot_SetRWBPType_Patch), "UITextReadability (Encyclopedia WorkSlot Type)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.CreatureInfoWorkSlot_Initialize0_Patch), "UITextReadability (Encyclopedia WorkSlot Init0)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.CreatureInfoWorkSlot_Initialize1_Patch), "UITextReadability (Encyclopedia WorkSlot Init1)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.CreatureInfoCodex_OnOeverlayEnter_Patch), "UITextReadability (Encyclopedia Codex Overlay)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.CreatureInfoCodexSlot_Init_Patch), "UITextReadability (Encyclopedia CodexSlot)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.CreatureInfoKitLayoutController_MakeEmptySlot_Patch), "UITextReadability (Encyclopedia KitSlot)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.AgentInfoWindow_InitGiftArea_Patch), "UITextReadability (AgentInfoWindow Gift List)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.AgentGiftSlot_SetData_Patch), "UITextReadability (InGameUI AgentGiftSlot Data)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.AgentGiftSlot_SetRegionName_Patch), "UITextReadability (InGameUI AgentGiftSlot Region)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.InventoryAgentController_SetUI_Patch), "UITextReadability (Inventory Agent Gift List)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.InventoryItemController_OnClickDetailInfo_TypeInfo_Patch), "UITextReadability (Inventory Detail Tooltip TypeInfo)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.InventoryItemController_OnClickDetailInfo_Model_Patch), "UITextReadability (Inventory Detail Tooltip Model)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.InventoryWeaponSlot_UpdateUI_Patch), "UITextReadability (Inventory WeaponSlot)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.InventoryArmorSlot_UpdateUI_Patch), "UITextReadability (Inventory ArmorSlot)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.ContinueLoadingScene_OnEnable_Patch), "UITextReadability (ContinueLoadingScene Overflow)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.TitleEndScene_OnEnable_Patch), "UITextReadability (TitleEndScene Overflow)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.StoryEndScene_OnEnable_Patch), "UITextReadability (StoryEndScene Overflow)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.DayEndScene_OnEnable_Patch), "UITextReadability (DayEndScene Overflow)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.LocalizeTextLoadScript_SetText_Patch), "UITextReadability (LocalizeTextLoadScript MinSize Floor)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.OverlayManager_SetText_Patch), "UITextReadability (OverlayManager Tooltip)");
                }
                
                // セーブデータガード
                ApplyAndVerifyPatch(typeof(MissingDataGuardPatch.InventoryModel_CreateEquipment_Patch), "MissingDataGuard (Equipment Skip)");
                ApplyAndVerifyPatch(typeof(MissingDataGuardPatch.CreatureObserveInfoModel_InitData_Patch), "MissingDataGuard (Observe Init)");
                ApplyAndVerifyPatch(typeof(MissingDataGuardPatch.CreatureManager_GetObserveInfoList_Patch), "MissingDataGuard (Codex Filter)");
                ApplyAndVerifyPatch(typeof(MissingDataGuardPatch.CreatureObserveInfoModel_IsMaxObserved_Patch), "MissingDataGuard (MaxObserved)");

                // Phase B: Notice.Send Copy-on-Write パッチ
                if (EnableNoticeCopyOnWrite.Value)
                {
                    ApplyAndVerifyPatch(typeof(NoticePatch.Notice_Observe_Patch), "Notice (Observe Invalidation)");
                    ApplyAndVerifyPatch(typeof(NoticePatch.Notice_Remove_Observer_Patch), "Notice (Remove Observer Invalidation)");
                    ApplyAndVerifyPatch(typeof(NoticePatch.Notice_Remove_Id_Patch), "Notice (Remove ID Invalidation)");
                    ApplyAndVerifyPatch(typeof(NoticePatch.Notice_Send_Patch), "Notice (Send Copy-on-Write Dispatch)");
                }

                // Phase B: TakeDamage ゼロバリア最適化 & メトリクス
                ApplyAndVerifyPatch(typeof(TakeDamagePatch.TakeDamage_UnitModel_Prefix), "TakeDamage (UnitModel Prefix Metrics)");
                ApplyAndVerifyPatch(typeof(TakeDamagePatch.TakeDamage_DamageInfo_Prefix), "TakeDamage (DamageInfo Prefix Metrics)");
                if (EnableZeroBarrierOptimization.Value)
                {
                    ApplyAndVerifyPatch(typeof(TakeDamagePatch.TakeDamage_Transpiler), "TakeDamage (Zero-Barrier Transpiler)");
                }
            }

            // F9 診断オーバーレイの追加
            base.gameObject.AddComponent<DiagnosticsOverlay>();

            // シーン遷移イベントの購読（世代管理・キャッシュクリーンアップ・ロード時間計測）
            SceneManager.sceneLoaded += OnSceneLoaded;

            Logger.LogInfo(string.Format("{0} initialization complete. Ready for Lobotomy Corporation.", PLUGIN_NAME));
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            MetricsPatches.OnSceneLoadEnd(scene.name);

            // 世代番号を更新（旧世代の非同期ジョブを無効化）
            int nextGen = GenerationTracker.NextGeneration();

            // シーン遷移時にシーン固有キャッシュを消去・破棄済み参照を掃除
            WorkerSpriteSetterPatch.ClearCache();
            IsolateRoomPatch.ClearCache();
            SefiraPanelPatch.ClearCache();
            WorkerPortraitPatch.ClearCache();
            WorkerListCachePatch.ClearCache();
            SpineOptimizerPatch.ClearCache();
            NoticePatch.ClearCache();
            UITextReadabilityPatch.ClearCache();
            SpineAtlasCachePatch.PruneDeadReferences();
            MainThreadDispatcher.Clear();
            FrameBudgetScheduler.Clear();

            // 既にEquipmentTypeListがロード済みならデバッグ装備状態をConfigに同期
            DebugGearBuffPatch.SyncWithConfig();

            Logger.LogInfo(string.Format("[LME Lifecycle] Scene loaded: {0}. Advanced to Generation {1}, cleaned caches.", scene.name, nextGen));
        }

        private void ApplyAndVerifyPatch(Type patchType, string patchName)
        {
            try
            {
                var processor = HarmonyInstance.CreateClassProcessor(patchType);
                var patchedMethods = processor.Patch();

                if (patchedMethods != null && patchedMethods.Count > 0)
                {
                    Logger.LogInfo(string.Format("[LME Verified] Applied {0} -> {1} method(s) hooked.", patchName, patchedMethods.Count));
                }
                else
                {
                    Logger.LogWarning(string.Format("[LME Warning] Patch {0} was processed but 0 methods were hooked!", patchName));
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(string.Format("[LME Error] Failed to apply patch {0}: {1}", patchName, ex.Message));
            }
        }

        private void Update()
        {
            // 5秒おきに破棄されたUnityオブジェクトの弱参照を掃除
            if (Time.unscaledTime - _lastPruneTime >= 5.0f)
            {
                WorkerSpriteSetterPatch.PruneDeadReferences();
                IsolateRoomPatch.PruneDeadReferences();
                WorkerPortraitPatch.PruneDeadReferences();
                SpineAtlasCachePatch.PruneDeadReferences();
                _lastPruneTime = Time.unscaledTime;
            }
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            BoundedWorkerPool.Shutdown();
        }
    }

    public static class PluginInfo
    {
        public const string PLUGIN_GUID = "com.isaka.lobotonymodernengine";
        public const string PLUGIN_NAME = "Lobotomy Modern Engine";
        public const string PLUGIN_VERSION = "0.6.0";
    }
}
