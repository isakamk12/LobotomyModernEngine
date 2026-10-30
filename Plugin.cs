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
        public const string PLUGIN_VERSION = "0.7.1";

        internal static new ManualLogSource Logger;
        internal static Harmony HarmonyInstance;

        // Config 設定（全パッチを個別にON/OFF・切り戻し可能）
        public static ConfigEntry<bool> EnableEngineOverhaul;
        public static ConfigEntry<bool> EnableUITextReadabilityFix;
        public static ConfigEntry<bool> EnableCheapStatUpgrade;
        public static ConfigEntry<bool> EnableVanillaBugFixes;
        public static ConfigEntry<bool> FixTwilightArmorGlobalBuff;
        public static ConfigEntry<bool> FixChesedBulletRefiningResearch;
        public static ConfigEntry<bool> FixMalkuthBossMovementReward;
        public static ConfigEntry<bool> FixSlimeGirlBugs;
        public static ConfigEntry<bool> FixLadyLookingAtWallWeapon;
        public static ConfigEntry<bool> FixDangoWeaponKillBuff;
        public static ConfigEntry<bool> FixBlueStarArmorHeal;
        public static ConfigEntry<bool> FixBigBirdOverloadImmunity;
        public static ConfigEntry<bool> FixRedHoodWeaponRange;
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
            EnableUITextReadabilityFix = Config.Bind("UIReadability", "EnableUITextReadabilityFix", true, "エンサイクロペディア・ギフト・ローディング画面のフォントサイズ適正化＆文字切れ修正を有効化する");
            EnableCheapStatUpgrade = Config.Bind("Debug", "CheapStatUpgrade", true, "デバッグ・レベリング短縮用に職員のステータス強化に必要なLOBポイントを一律1にする");
            EnableVanillaBugFixes = Config.Bind("VanillaBugFix", "EnableVanillaBugFixes", true, "バニラ固有バグ修正パッチ群を一括で有効化する");
            FixTwilightArmorGlobalBuff = Config.Bind("VanillaBugFix", "FixTwilightArmorGlobalBuff", true, "いつでもどこでも黄昏バグ修正 (黄昏防具が他武器の攻撃力まで最大2倍にしていたバグ＆UnitModelのnullチェックバグ修正)");
            FixChesedBulletRefiningResearch = Config.Bind("VanillaBugFix", "FixChesedBulletRefiningResearch", true, "プラシーボ効果バグ修正 (ケセド研究「HP、SP弾精製」ID:803を取得しても弾丸回復量が25→40に増えないバグ修正)");
            FixMalkuthBossMovementReward = Config.Bind("VanillaBugFix", "FixMalkuthBossMovementReward", true, "マルクトあげませんバグ修正 (マルクトコア抑制クリア報酬「職員の移動速度が増加」が未実装だったバグ修正)");
            FixSlimeGirlBugs = Config.Bind("VanillaBugFix", "FixSlimeGirlBugs", true, "溶ける愛ヤンデレ・ツンデレバグ修正 (洞察失敗時に感染者が変異しないバグ＆カウンター減少条件が施設全体分母になっていたバグ修正)");
            FixLadyLookingAtWallWeapon = Config.Bind("VanillaBugFix", "FixLadyLookingAtWallWeapon", true, "壁に向かう女武器「叫び」バグ修正 (説明文通り「勇気または慎重ランク3未満で50%の確率でSP5減少」に修正)");
            FixDangoWeaponKillBuff = Config.Bind("VanillaBugFix", "FixDangoWeaponKillBuff", true, "笑顔(団子)特殊攻撃キルバフ不発バグ修正 (特殊6連撃の2〜6撃目で敵を倒した際にも体力+3・正義+3バフが付与されるよう修正)");
            FixBlueStarArmorHeal = Config.Bind("VanillaBugFix", "FixBlueStarArmorHeal", true, "一等星は常に嘘つきバグ修正 (蒼星防具の同空間回復で精神力だけでなく体力も5回復するよう修正)");
            FixBigBirdOverloadImmunity = Config.Bind("VanillaBugFix", "FixBigBirdOverloadImmunity", true, "効かないねぇ！大鳥だから！バグ修正 (大鳥がmaxWorkCount=0のためクリフォト暴走の対象外になっていたバグ修正)");
            FixRedHoodWeaponRange = Config.Bind("VanillaBugFix", "FixRedHoodWeaponRange", true, "紅の傷跡短小バグ＆インベントリ射程表記ズレ修正 (通常攻撃時に射程が4→3に縮むバグとインベントリ射程閾値バグ修正)");
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

                // デバッグ強化LOBポイント一律1パッチ
                if (EnableCheapStatUpgrade.Value)
                {
                    ApplyAndVerifyPatch(typeof(StatUpgradeCostPatch.StatUI_Init_Patch), "DebugStatCost (StatUI Cost Array Hook)");
                    ApplyAndVerifyPatch(typeof(StatUpgradeCostPatch.StatController_SetCost_Patch), "DebugStatCost (StatController SetCost Hook)");
                }

                // UIテキストサイズ・エンサイクロペディア・ギフト・ローディング文字切れピンポイント修正パッチ
                if (EnableUITextReadabilityFix.Value)
                {
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.CreatureInfoWindow_SetDesc_Patch), "UITextReadability (Encyclopedia Story SetDesc)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.GiftSlot_SetModel_TypeInfo_Patch), "UITextReadability (Encyclopedia GiftSlot TypeInfo)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.GiftSlot_SetModel_Model_Patch), "UITextReadability (Encyclopedia GiftSlot Model)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.AgentGiftSlot_SetData_Patch), "UITextReadability (InGameUI AgentGiftSlot Data)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.ContinueLoadingScene_OnEnable_Patch), "UITextReadability (ContinueLoadingScene Overflow)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.TitleEndScene_OnEnable_Patch), "UITextReadability (TitleEndScene Overflow)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.StoryEndScene_OnEnable_Patch), "UITextReadability (StoryEndScene Overflow)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.DayEndScene_OnEnable_Patch), "UITextReadability (DayEndScene Overflow)");
                }

                // v0.7.0 バニラ固有バグ修正パッチ群 (note記事準拠・全9項目個別トグル対応)
                if (EnableVanillaBugFixes.Value)
                {
                    int activeGroups = 0;
                    if (FixTwilightArmorGlobalBuff.Value)
                    {
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.UnitModel_GetDamageFactorByEquipment_Patch), "VanillaBugFix (UnitModel Armor NullCheck)");
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.BossBirdArmor_GetDamageFactor_Patch), "VanillaBugFix (Twilight Armor Weapon Check)");
                        activeGroups++;
                    }
                    if (FixChesedBulletRefiningResearch.Value)
                    {
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.ResearchDataModel_IsUpgradedAbility_Patch), "VanillaBugFix (Chesed Bullet Refining ID:803)");
                        activeGroups++;
                    }
                    if (FixMalkuthBossMovementReward.Value)
                    {
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.AgentModel_GetMovementBufBySefiraAbility_Patch), "VanillaBugFix (Malkuth Boss Movement Reward)");
                        activeGroups++;
                    }
                    if (FixSlimeGirlBugs.Value)
                    {
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.SlimeGirl_OnReleaseWork_Patch), "VanillaBugFix (SlimeGirl Yandere Pawn Transform)");
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.SlimeGirl_OnFixedUpdate_Patch), "VanillaBugFix (SlimeGirl Tsundere Dept Ratio)");
                        activeGroups++;
                    }
                    if (FixLadyLookingAtWallWeapon.Value)
                    {
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.LadyLookingAtWallWeapon_OnGiveDamage_Patch), "VanillaBugFix (LadyLookingAtWall Weapon SP Penalty)");
                        activeGroups++;
                    }
                    if (FixDangoWeaponKillBuff.Value)
                    {
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.DangoCreatureWeapon_OnGiveDamage_Patch), "VanillaBugFix (Dango Weapon AliveBeforeHit)");
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.DangoCreatureWeapon_OnGiveDamageAfter_Patch), "VanillaBugFix (Dango Weapon MultiHit Kill Buff)");
                        activeGroups++;
                    }
                    if (FixBlueStarArmorHeal.Value)
                    {
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.BlueStarArmor_MpHeal_Patch), "VanillaBugFix (BlueStar Armor HP+SP Heal)");
                        activeGroups++;
                    }
                    if (FixBigBirdOverloadImmunity.Value)
                    {
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.CreatureModel_GetMaxWorkCount_Patch), "VanillaBugFix (BigBird Overload Eligibility)");
                        activeGroups++;
                    }
                    if (FixRedHoodWeaponRange.Value)
                    {
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.RedHoodWeapon_SetNextAttackType_Patch), "VanillaBugFix (RedHood Weapon Normal Range 4f)");
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.InventoryItemDescGetter_GetWeaponDesc_Patch), "VanillaBugFix (Inventory Weapon Range Threshold)");
                        activeGroups++;
                    }
                    VanillaBugFixPatch.ActiveBugFixGroupCount = activeGroups;
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
            VanillaBugFixPatch.ClearCache();
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
        public const string PLUGIN_VERSION = "0.7.1";
    }
}
