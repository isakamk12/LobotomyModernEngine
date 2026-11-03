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
        public const string PLUGIN_GUID = PluginInfo.PLUGIN_GUID;
        public const string PLUGIN_NAME = PluginInfo.PLUGIN_NAME;
        public const string PLUGIN_VERSION = PluginInfo.PLUGIN_VERSION;

        internal static new ManualLogSource Logger;
        internal static Harmony HarmonyInstance;

        // 集計単位を明確に分離
        public static int HookedModuleCount { get; private set; }
        public static int TotalModuleCount { get; private set; }
        public static int HookedTargetMethodCount { get; private set; }

        // Config 設定（全パッチを個別にON/OFF・切り戻し可能）
        public static ConfigEntry<bool> EnableEngineOverhaul;
        public static ConfigEntry<bool> ShowMiniHudBadge;
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
        public static ConfigEntry<bool> FixMovementSlowDebuffs;
        public static ConfigEntry<bool> FixTwilightPaleAttack;
        public static ConfigEntry<bool> FixChildOfGalaxyWeaponRange;
        public static ConfigEntry<bool> FixSlimeGirlWeaponPanicTarget;
        public static ConfigEntry<bool> FixVoidDreamWakeUpFreeze;
        public static ConfigEntry<bool> FixMultiHitWeaponDamage;
        public static ConfigEntry<bool> FixAttackCancelHitboxMismatch;
        public static ConfigEntry<bool> FixBinahInvincibilityFreeze;
        public static ConfigEntry<bool> FixBigBirdCharmedDamageImmunity;
        public static ConfigEntry<bool> FixReverseClockZombieState;
        public static ConfigEntry<bool> FixDoorPassingStuck;
        public static ConfigEntry<bool> FixRedHoodRiskLevelSync;
        public static ConfigEntry<bool> FixWhiteNightRoomSwapSmallBird;
        public static ConfigEntry<bool> EnableSpineAtlasCache;
        public static ConfigEntry<bool> EnableCursorLeakFix;
        public static ConfigEntry<bool> EnableWorkerPortraitOptimization;
        public static ConfigEntry<bool> EnableWorkerListCache;
        public static ConfigEntry<bool> EnableSpineOptimizerCache;
        public static ConfigEntry<bool> EnableSmoothSpineAnimation;
        public static ConfigEntry<bool> EnableDebugStarterGearBuff;
        public static ConfigEntry<bool> EnableNoticeCopyOnWrite;
        public static ConfigEntry<bool> EnableZeroBarrierOptimization;
        public static ConfigEntry<bool> EnableWorkerSpriteCache;
        public static ConfigEntry<bool> EnableIsolateRoomOptimization;
        public static ConfigEntry<bool> EnableSefiraPanelOptimization;
        public static ConfigEntry<bool> EnablePassableOptimization;
        public static ConfigEntry<bool> EnableUnitBufOptimization;
        public static ConfigEntry<bool> EnableSoundEffectCache;
        public static ConfigEntry<bool> EnablePathfindingBufferReuse;
        public static ConfigEntry<bool> EnableAgentUIOptimization;
        public static ConfigEntry<bool> EnableMovementInterpolation;
        public static ConfigEntry<int> MaxWorkerThreads;
        public static ConfigEntry<bool> EnableProfilerLog;
        public static ConfigEntry<bool> EnableCpuProfiler;

        private float _lastPruneTime;

        private void Awake()
        {
            Logger = base.Logger;
            Logger.LogInfo(string.Format("{0} v{1} initializing on CLR {2}...", PLUGIN_NAME, PLUGIN_VERSION, Environment.Version));

#if DEBUG_PLUS
            const bool defaultDebugFeatures = true;
#else
            const bool defaultDebugFeatures = false;
#endif

            // 設定の初期化
            EnableEngineOverhaul = Config.Bind("General", "EnableEngineOverhaul", true, "全般的な最適化パッチを有効化する");
            ShowMiniHudBadge = Config.Bind("General", "ShowMiniHudBadge", true, "画面左上にLMEの稼働状態・FPS・1LOB/神装備の状態が一目でわかるミニバッジを表示する (クリックまたはF9で詳細パネル開閉)");
            EnableUITextReadabilityFix = Config.Bind("UIReadability", "EnableUITextReadabilityFix", true, "エンサイクロペディア・ギフト欄のフォントサイズ適正化＆文字切れ修正を有効化する (ローディング画面の座標はバニラ維持)");
            EnableCheapStatUpgrade = Config.Bind("Debug", "CheapStatUpgrade", defaultDebugFeatures, "デバッグ・レベリング短縮用に職員のステータス強化に必要なLOBポイントを一律1にする");
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
            FixMovementSlowDebuffs = Config.Bind("VanillaBugFix", "FixMovementSlowDebuffs", true, "氷のかけら・笑顔・失楽園等の移動速度デバフ修正 (調律者以外にも効くよう正常化、効果終了後の超加速暴走防止、試練エネミーの減速無効修正)");
            FixTwilightPaleAttack = Config.Bind("VanillaBugFix", "FixTwilightPaleAttack", true, "黄昏青属性攻撃不発バグ修正 (黄昏の通常攻撃で最後の青属性(Pale)攻撃がSpineイベント数の不一致で消化されないバグ修正)");
            FixChildOfGalaxyWeaponRange = Config.Bind("VanillaBugFix", "FixChildOfGalaxyWeaponRange", true, "銀河の子武器射程バグ修正 (メイスなのに射程が銃並み8になっていた設定ミスを本来の近接射程3に補正)");
            FixSlimeGirlWeaponPanicTarget = Config.Bind("VanillaBugFix", "FixSlimeGirlWeaponPanicTarget", true, "ラブの弾丸パニック無視バグ修正 (溶ける愛武器「ラブ」でパニック職員を狙った際に弾がすり抜けて鎮圧できないバグ修正)");
            FixVoidDreamWakeUpFreeze = Config.Bind("VanillaBugFix", "FixVoidDreamWakeUpFreeze", true, "空虚な夢(夢喰い獏)覚醒スタックバグ修正 (職員覚醒時にキュー不整合やイベント欠落で永久操作不能・無敵化するバグの安全救出機構)");
            FixMultiHitWeaponDamage = Config.Bind("VanillaBugFix", "FixMultiHitWeaponDamage", true, "多段演出単発ダメージバグ修正 (宇宙の欠片等で多段ヒット演出なのにダメージが1回しか入らない不整合を適正分割化)");
            FixAttackCancelHitboxMismatch = Config.Bind("VanillaBugFix", "FixAttackCancelHitboxMismatch", true, "攻撃キャンセル判定残留バグ修正 (移動やターゲット喪失時にSpineアニメ中断と残存ダメージキュー破棄を徹底しゴースト判定を防止)");
            FixBinahInvincibilityFreeze = Config.Bind("VanillaBugFix", "FixBinahInvincibilityFreeze", true, "調律者停止・無敵化バグ修正 (49日目やフェーズ移行・ロード後に無敵フラグが解除されず進行不能になるバグの自動復帰・Watchdog)");
            FixBigBirdCharmedDamageImmunity = Config.Bind("VanillaBugFix", "FixBigBirdCharmedDamageImmunity", true, "大鳥魅了職員無敵化バグ修正 (大鳥の魅了第2・第3段階でダメージ倍率が0になり味方の鎮圧・救出が不能になるバグ修正)");
            FixReverseClockZombieState = Config.Bind("VanillaBugFix", "FixReverseClockZombieState", true, "逆行時計琥珀ゾンビ化バグ修正 (逆行時計発動時に試練エネミーのHPや死亡処理がスキップされ攻撃を続ける無敵ゾンビ化バグ修正)");
            FixDoorPassingStuck = Config.Bind("VanillaBugFix", "FixDoorPassingStuck", true, "扉付近滑走スタックバグ修正 (閉じた扉の前で移動状態のまま座標が進まず走行アニメーションで固まるバグの安全脱出機構)");
            FixRedHoodRiskLevelSync = Config.Bind("VanillaBugFix", "FixRedHoodRiskLevelSync", true, "赤ずきん危険度・コスト表示不一致修正 (依頼UIの危険度表示と内部コスト計算の不整合、試練・職員指定時の表示残存バグ修正)");
            FixWhiteNightRoomSwapSmallBird = Config.Bind("VanillaBugFix", "FixWhiteNightRoomSwapSmallBird", true, "白夜部屋交換・罰鳥止まり木残留バグ修正 (ペスト医師→白夜変身で部屋が入れ替わった際に罰鳥の止まり木が元の部屋に取り残されるバグ修正)");
            EnableSpineAtlasCache = Config.Bind("MemoryLeakFix", "SpineAtlasCache", true, "アニメーション切替時の Spine Texture2D/Material 永久リーク＆CPUスパイク防止 (日終了・シーン遷移時の安全一括Destroy)");
            EnableCursorLeakFix = Config.Bind("MemoryLeakFix", "CursorLeakFix", true, "弾丸カーソル・照準アニメーションの毎フレーム Texture2D 永久リーク防止 (日終了・シーン遷移時の安全一括Destroy)");
            EnableWorkerPortraitOptimization = Config.Bind("Optimization", "WorkerPortraitOptimization", true, "WorkerPortraitSetter.LateUpdate の毎フレーム全再構築抑止 (表示状態Dirty検知＆所要時間計測)");
            EnableWorkerListCache = Config.Bind("Optimization", "WorkerListCache", true, "AgentManager/OfficerManager の ReadOnlyCollection 毎フレーム生成をゼロアロケーション化");
            EnableSpineOptimizerCache = Config.Bind("Optimization", "SpineOptimizerCache", true, "SpineOptimizerModule/WorkerUnit の毎フレーム Camera.main タグ検索を1フレーム1回に共有化");
            EnableSmoothSpineAnimation = Config.Bind("Optimization", "SmoothSpineAnimation", true, "カメラを少し引いただけで画面内の職員・オフィサーの手足アニメーションが15〜30FPSへコマ落ち(optimizeLevel)するのを緩和し、通常のプレイ視野内では60FPSフルアニメーションで滑らかに歩かせる");
            EnableNoticeCopyOnWrite = Config.Bind("Optimization", "NoticeCopyOnWrite", true, "Notice.Send の Copy-on-Write ゼロアロケーション化");
            EnableZeroBarrierOptimization = Config.Bind("Optimization", "ZeroBarrierOptimization", true, "TakeDamage & TakeDamageWithoutEffect のバリア0件時アロケーション消去 (IL Transpiler)");
            EnableWorkerSpriteCache = Config.Bind("Optimization", "WorkerSpriteCache", true, "WorkerSpriteSetter の GetComponent キャッシュ化");
            EnableIsolateRoomOptimization = Config.Bind("Optimization", "IsolateRoomOptimization", true, "IsolateRoom の UI Canvas 再描画スパム防止");
            EnableSefiraPanelOptimization = Config.Bind("Optimization", "SefiraPanelOptimization", true, "SefiraPanel の NoData/Portrait キャッシュ化");
            EnablePassableOptimization = Config.Bind("Optimization", "PassableOptimization", true, "CheckPassable の走査最適化");
            EnableUnitBufOptimization = Config.Bind("Optimization", "UnitBufOptimization", true, "UnitModel.UpdateBufState の空リスト配列確保削減およびスナップショットバッファ再利用");
            EnableSoundEffectCache = Config.Bind("Optimization", "SoundEffectCache", true, "SoundEffectPlayer / CreatureUnit.PlaySound の AudioClip キャッシュ化および不在音声キーの再検索・エラーログ削減");
            EnablePathfindingBufferReuse = Config.Bind("Optimization", "PathfindingBufferReuse", true, "GraphAstar.SearchPath の探索コレクション・PathScore/SearchInfo プール再利用 (状態完全リセット＆再帰安全ガード付)");
            EnableAgentUIOptimization = Config.Bind("Optimization", "AgentUIOptimization", true, "イェソド解放時の毎フレーム AgentUnitUI.activateUI 呼び出し・文字列結合・Textレイアウト幅計算を Dirty 検知化して GC スパイクを根絶");
            EnableMovementInterpolation = Config.Bind("Optimization", "MovementInterpolation", true, "FixedUpdate(50Hz) と描画(60Hz+) のズレによる歩行カクつきを解消する描画専用の線形位置補間");
            MaxWorkerThreads = Config.Bind("Parallel", "MaxWorkerThreads", 4, "最大ワーカースレッド数 (1-4, 初回タスク投入時に遅延起動)");
            EnableDebugStarterGearBuff = Config.Bind("Debug", "DebugStarterGearBuff", defaultDebugFeatures, "デバッグ・検証短縮用に初期装備(警棒/スーツ)と懺悔(武器/防具)および初期・雇用職員のランク5(ALL 100)化を適用する (F8またはF9画面で即切替可)");
            EnableProfilerLog = Config.Bind("Debug", "EnableProfilerLog", true, "管理パート中に5秒おき＆ステージ終了時にCPU実測プロファイル結果をBepInExログ(LogOutput.log)へ出力する");
            EnableCpuProfiler = Config.Bind("Debug", "EnableCpuProfiler", true, "主要処理(経路探索/索敵/AI/移動)のCPU実行時間を実測プロファイリングする (F9画面およびログに出力)");

            DiagnosticsOverlay.ShowMiniStatusBadge = ShowMiniHudBadge.Value;

            // LMF.Core 基盤コンポーネントの初期化
            MainThreadDispatcher.Initialize(base.gameObject);
            FrameBudgetScheduler.Initialize(base.gameObject);
            BoundedWorkerPool.Initialize(MaxWorkerThreads.Value);

            HookedModuleCount = 0;
            TotalModuleCount = 0;
            HookedTargetMethodCount = 0;

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

                // v0.4.0 / v0.7.5 3大ネイティブメモリリーク＆UIスパム修正パッチ（日終了・シーン遷移時安全解放）
                if (EnableSpineAtlasCache.Value)
                    ApplyAndVerifyPatch(typeof(SpineAtlasCachePatch), "SpineAtlasCache (Animation Texture2D/Material Leak Fix)");

                if (EnableCursorLeakFix.Value)
                {
                    ApplyAndVerifyPatch(typeof(CursorLeakPatch.CursorManager_SetCursorScale_Patch), "CursorLeakFix (SetCursorScale Quantized Cache)");
                    ApplyAndVerifyPatch(typeof(CursorLeakPatch.WorkerSpriteManager_ScaleTexture_Patch), "CursorLeakFix (ScaleTexture Cache)");
                }

                if (EnableWorkerPortraitOptimization.Value)
                    ApplyAndVerifyPatch(typeof(WorkerPortraitPatch), "WorkerPortraitSetter (LateUpdate Dirty Check & Overhead Profile)");

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

                // デバッグ初期装備強化
                ApplyAndVerifyPatch(typeof(DebugGearBuffPatch.EquipmentDataLoader_Load_Patch), "DebugGearBuff (Starter & Penitence Gear Hook)");
                ApplyAndVerifyPatch(typeof(DebugGearBuffPatch.EquipmentScriptBase_OnGiveDamage_Patch), "DebugGearBuff (Dynamic Weakness OnGiveDamage Hook)");

                // デバッグ：初期・雇用ランク5職員パッチ
                ApplyAndVerifyPatch(typeof(DebugAgentBuffPatch.AgentModel_GetDefaultStat_Patch), "DebugAgentBuff (DefaultStat Rank 5 Hook)");
                ApplyAndVerifyPatch(typeof(DebugAgentBuffPatch.CustomizingWindow_SetAgentStatBonus_Patch), "DebugAgentBuff (Customizing Rank 5 Hook)");
                ApplyAndVerifyPatch(typeof(DebugAgentBuffPatch.AgentManager_LoadData_Patch), "DebugAgentBuff (AgentManager LoadData Sync)");
                ApplyAndVerifyPatch(typeof(DebugAgentBuffPatch.GameManager_StartStage_Patch), "DebugAgentBuff (GameManager StartStage Sync)");

                // デバッグ強化LOBポイント一律1パッチ
                StatUpgradeCostPatch.SyncStaticArrays();
                ApplyAndVerifyPatch(typeof(StatUpgradeCostPatch.StatUI_Init0_Patch), "DebugStatCost (StatUI Cost Array Hook)");
                ApplyAndVerifyPatch(typeof(StatUpgradeCostPatch.StatUI_Init1_Patch), "DebugStatCost (StatUI Data Init Hook)");
                ApplyAndVerifyPatch(typeof(StatUpgradeCostPatch.StatController_SetCost_Patch), "DebugStatCost (StatController 1-LOB Hook)");

                // UIテキストサイズ・エンサイクロペディア・ギフト視認性改善パッチ (ローディング画面はバニラ完全維持)
                if (EnableUITextReadabilityFix.Value)
                {
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.CreatureInfoWindow_SetDesc_Patch), "UITextReadability (Encyclopedia Story SetDesc)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.GiftSlot_SetModel_TypeInfo_Patch), "UITextReadability (Encyclopedia GiftSlot TypeInfo)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.GiftSlot_SetModel_Model_Patch), "UITextReadability (Encyclopedia GiftSlot Model)");
                    ApplyAndVerifyPatch(typeof(UITextReadabilityPatch.AgentGiftSlot_SetData_Patch), "UITextReadability (InGameUI AgentGiftSlot Data)");
                }

                // v0.7.0 バニラ固有バグ修正パッチ群 (全9項目)
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
                    if (FixMovementSlowDebuffs.Value)
                    {
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.SnowQueenSlow_Patch), "VanillaBugFix (SnowQueen Slow Direct Scale Bypass)");
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.DangoSlowNormal_Patch), "VanillaBugFix (Dango Normal Slow Direct Scale Bypass)");
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.DangoSlowSpecial_Patch), "VanillaBugFix (Dango Special Slow Direct Scale Bypass)");
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.DeathAngelSlow_Patch), "VanillaBugFix (DeathAngel Slow Direct Scale Bypass)");
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.OrdealCreatureModel_OnFixedUpdate_Patch), "VanillaBugFix (Ordeal MovementScaleByBuf Apply)");
                        activeGroups++;
                    }
                    if (FixTwilightPaleAttack.Value)
                    {
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.TwilightPaleAttack_Patch), "VanillaBugFix (Twilight Pale Attack Flush)");
                        activeGroups++;
                    }
                    if (FixChildOfGalaxyWeaponRange.Value)
                    {
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.ChildOfGalaxyRange_Patch), "VanillaBugFix (ChildOfGalaxy Weapon Range)");
                        activeGroups++;
                    }
                    if (FixSlimeGirlWeaponPanicTarget.Value)
                    {
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.SlimeGirlWeaponPanicTarget_Patch), "VanillaBugFix (SlimeGirl Love Panic Target)");
                        activeGroups++;
                    }
                    if (FixVoidDreamWakeUpFreeze.Value)
                    {
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.BakuAnimCallGuard_Patch), "VanillaBugFix (Baku Anim Call Guard)");
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.VoidDreamUnconSafety_Patch), "VanillaBugFix (VoidDream Uncon Safety Timer)");
                        activeGroups++;
                    }
                    if (FixMultiHitWeaponDamage.Value)
                    {
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.CosmosInnerWeapon_Patch), "VanillaBugFix (MultiHit Cosmos Weapon Damage)");
                        activeGroups++;
                    }
                    if (FixAttackCancelHitboxMismatch.Value)
                    {
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.AttackCancelHitboxMismatch_Patch), "VanillaBugFix (Attack Cancel Hitbox Flush)");
                        activeGroups++;
                    }
                    if (FixBinahInvincibilityFreeze.Value)
                    {
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.BinahMoveToNextPhase_Patch), "VanillaBugFix (Binah MoveToNextPhase Invincible Reset)");
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.BinahWatchdog_Patch), "VanillaBugFix (Binah Invincible Watchdog)");
                        activeGroups++;
                    }
                    if (FixBigBirdCharmedDamageImmunity.Value)
                    {
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.BigBirdAttractBuf_OnTakeDamage_Patch), "VanillaBugFix (BigBird Attract Friendly Damage Factor)");
                        activeGroups++;
                    }
                    if (FixReverseClockZombieState.Value)
                    {
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.ReverseClock_Retrograde_Patch), "VanillaBugFix (ReverseClock Ordeal Zombie Elimination)");
                        activeGroups++;
                    }
                    if (FixDoorPassingStuck.Value)
                    {
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.DoorPassingStuck_Patch), "VanillaBugFix (Door Passing Stuck Auto-Recovery)");
                        activeGroups++;
                    }
                    if (FixRedHoodRiskLevelSync.Value)
                    {
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.RedHoodRequestUI_Update_Patch), "VanillaBugFix (RedHood UI Risk & Cost Sync)");
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.RedHood_GetRequestCost_Patch), "VanillaBugFix (RedHood Request Cost Normalized)");
                        activeGroups++;
                    }
                    if (FixWhiteNightRoomSwapSmallBird.Value)
                    {
                        ApplyAndVerifyPatch(typeof(VanillaBugFixPatch.CreatureManager_ChangeCreaturePos_Patch), "VanillaBugFix (WhiteNight Room Swap SmallBird Perch Sync)");
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
                    ApplyAndVerifyPatch(typeof(TakeDamagePatch.TakeDamageWithoutEffect_Transpiler), "TakeDamageWithoutEffect (Zero-Barrier Transpiler)");
                }

                // v0.8.4 優先度1: UnitModel.UpdateBufState 空リスト配列確保削減＆スナップショットバッファ再利用
                UnitBufStatePatch.InitializeMainThread();
                if (EnableUnitBufOptimization.Value)
                {
                    ApplyAndVerifyPatch(typeof(UnitBufStatePatch.UnitModel_UpdateBufState_Patch), "UnitBufState (Zero-Alloc Snapshot Buffer)");
                }

                // v0.8.4 優先度2: 音声ロード (Resources.Load<AudioClip>) キャッシュ化＆不在音声キー再検索削減
                if (EnableSoundEffectCache.Value)
                {
                    ApplyAndVerifyPatch(typeof(SoundEffectCachePatch.SoundEffectPlayer_ResourcesLoad_Transpiler), "SoundEffectCache (Resources.Load<AudioClip> Cache Transpiler)");
                    ApplyAndVerifyPatch(typeof(SoundEffectCachePatch.CreatureUnit_PlaySound1_Patch), "SoundEffectCache (CreatureUnit PlaySound(key))");
                    ApplyAndVerifyPatch(typeof(SoundEffectCachePatch.CreatureUnit_PlaySound2_Patch), "SoundEffectCache (CreatureUnit PlaySound(key,vol))");
                    ApplyAndVerifyPatch(typeof(SoundEffectCachePatch.CreatureUnit_PlaySound3_Patch), "SoundEffectCache (CreatureUnit PlaySound(key,mode))");
                    ApplyAndVerifyPatch(typeof(SoundEffectCachePatch.CreatureUnit_PlaySoundLoop1_Patch), "SoundEffectCache (CreatureUnit PlaySoundLoop(key))");
                    ApplyAndVerifyPatch(typeof(SoundEffectCachePatch.CreatureUnit_PlaySoundLoop2_Patch), "SoundEffectCache (CreatureUnit PlaySoundLoop(key,vol))");
                }

                // v0.8.4 優先度3: 経路探索 (GraphAstar.SearchPath) バッファ再利用 (状態完全リセット＆再帰安全ガード付)
                if (EnablePathfindingBufferReuse.Value)
                {
                    ApplyAndVerifyPatch(typeof(PathfindingBufferPatch.GraphAstar_SearchPath_ReusePatch), "PathfindingBuffer (GraphAstar.SearchPath Workspace Reuse)");
                }

                // v0.8.5 施策1: イェソド能力有効時の毎フレーム AgentUnitUI.activateUI 呼び出し削減 (Dirty検知)
                if (EnableAgentUIOptimization.Value)
                {
                    ApplyAndVerifyPatch(typeof(AgentUIOptimizationPatch.AgentUnitUI_activateUI_Patch), "AgentUIOptimization (Dirty Check activateUI)");
                }

                // v0.8.5 施策2: 移動描画補間 (FixedUpdate 50Hz と Update 60Hz+ のズレ解消)
                if (EnableMovementInterpolation.Value)
                {
                    ApplyAndVerifyPatch(typeof(MovementInterpolationPatch.MovableObjectNode_ProcessMoveNode_SnapshotPatch), "MovementInterpolation (Snapshot Record)");
                    ApplyAndVerifyPatch(typeof(MovementInterpolationPatch.MovableObjectNode_SetCurrentNode_Patch), "MovementInterpolation (Teleport Reset)");
                    ApplyAndVerifyPatch(typeof(MovementInterpolationPatch.WorkerUnit_UpdateViewPosition_Patch), "MovementInterpolation (WorkerUnit Lerp)");
                    ApplyAndVerifyPatch(typeof(MovementInterpolationPatch.CreatureUnit_Update_InterpolationPatch), "MovementInterpolation (CreatureUnit Lerp)");
                }

                // Phase C: 主要処理リアルタイムCPUプロファイラ (実測値計測 & WorkerAI内部細分化)
                if (EnableCpuProfiler.Value)
                {
                    ApplyAndVerifyPatch(typeof(ProfilerPatch.SearchPath_ProfilePatch), "Profiler (GraphAstar SearchPath)");
                    ApplyAndVerifyPatch(typeof(ProfilerPatch.GetEnteredTargetsWithExclude_ProfilePatch), "Profiler (Passage WithExclude)");
                    ApplyAndVerifyPatch(typeof(ProfilerPatch.GetEnteredTargetsNoArg_ProfilePatch), "Profiler (Passage NoArg)");
                    ApplyAndVerifyPatch(typeof(ProfilerPatch.AgentModel_OnFixedUpdate_ProfilePatch), "Profiler (AgentModel FixedUpdate)");
                    ApplyAndVerifyPatch(typeof(ProfilerPatch.OfficerModel_OnFixedUpdate_ProfilePatch), "Profiler (OfficerModel FixedUpdate)");
                    ApplyAndVerifyPatch(typeof(ProfilerPatch.WorkerModel_OnFixedUpdate_ProfilePatch), "Profiler (WorkerModel FixedUpdate)");
                    ApplyAndVerifyPatch(typeof(ProfilerPatch.UnitModel_UpdateBufState_ProfilePatch), "Profiler (WorkerSub 3c: UpdateBufState)");
                    ApplyAndVerifyPatch(typeof(ProfilerPatch.UseSkill_OnFixedUpdate_ProfilePatch), "Profiler (WorkerSub 3d: UseSkill FixedUpdate)");
                    ApplyAndVerifyPatch(typeof(ProfilerPatch.UseSkill_OnEnterRoom_ProfilePatch), "Profiler (WorkerSub 3d-1: UseSkill OnEnterRoom)");
                    ApplyAndVerifyPatch(typeof(ProfilerPatch.CreatureOverloadManager_AddOverloadGague_ProfilePatch), "Profiler (WorkerSub 3e: AddOverloadGague/Ordeal)");
                    ApplyAndVerifyPatch(typeof(ProfilerPatch.OfficerModel_ProcessActionNormalOfficer_ProfilePatch), "Profiler (WorkerSub 3f: OfficerNormalAI)");
                    ApplyAndVerifyPatch(typeof(ProfilerPatch.CreatureModel_OnFixedUpdate_ProfilePatch), "Profiler (CreatureModel FixedUpdate)");
                    ApplyAndVerifyPatch(typeof(ProfilerPatch.OrdealCreatureModel_OnFixedUpdate_ProfilePatch), "Profiler (OrdealCreatureModel FixedUpdate)");
                    ApplyAndVerifyPatch(typeof(ProfilerPatch.MovableObjectNode_ProcessMoveNode_ProfilePatch), "Profiler (MovableObjectNode ProcessMoveNode)");
                }
            }

            // 診断オーバーレイの追加
            base.gameObject.AddComponent<DiagnosticsOverlay>();

            // シーン遷移イベントの購読（日の終了・シーン切り替え時に安全にネイティブテクスチャを一括破棄）
            SceneManager.sceneLoaded += OnSceneLoaded;

            Logger.LogInfo(string.Format("{0} initialization complete: {1}/{2} modules ({3} methods hooked). Ready for Lobotomy Corporation.",
                PLUGIN_NAME, HookedModuleCount, TotalModuleCount, HookedTargetMethodCount));
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            MetricsPatches.OnSceneLoadEnd(scene.name);

            // 世代番号を更新（旧世代の非同期ジョブを無効化）
            int nextGen = GenerationTracker.NextGeneration();

            // 共通キャッシュの初期化
            WorkerSpriteSetterPatch.ClearCache();
            IsolateRoomPatch.ClearCache();
            SefiraPanelPatch.ClearCache();
            WorkerPortraitPatch.ClearCache();
            WorkerListCachePatch.ClearCache();
            SpineOptimizerPatch.ClearCache();
            NoticePatch.ClearCache();
            UITextReadabilityPatch.ClearCache();
            VanillaBugFixPatch.ClearCache();
            UnitBufStatePatch.ClearCache();
            SoundEffectCachePatch.OnSceneTransition(scene.name);
            PathfindingBufferPatch.ClearCache();
            AgentUIOptimizationPatch.ClearCache();
            MovementInterpolationPatch.ClearCache();
            GameProfiler.ResetAll();
            MainThreadDispatcher.Clear();
            FrameBudgetScheduler.Clear();

            // 既にEquipmentTypeListがロード済みならデバッグ装備状態をConfigに同期
            DebugGearBuffPatch.SyncWithConfig();
            DebugAgentBuffPatch.ClearCache();
            DebugAgentBuffPatch.SyncWithConfig();

            // 【v0.7.6 安全なリソース解放コルーチン】
            // SceneManager.sceneLoaded 直後は旧シーンの GameObject 破棄キューが未処理の可能性がある。
            // 次フレーム (yield return null) まで待機し、確実に旧オブジェクトが消滅した状態で
            // アクティブな Renderer / UI Graphic の参照を精査して孤立リソースのみを Object.Destroy する。
            StartCoroutine(DeferredSceneCleanup(scene.name, nextGen));
        }

        private System.Collections.IEnumerator DeferredSceneCleanup(string sceneName, int generation)
        {
            // 旧シーンオブジェクトの完全破棄と新シーンAwake完了を待つため1フレーム待機
            yield return null;

            SpineAtlasCachePatch.SafeClearUnusedResources();
            CursorLeakPatch.SafeClearUnusedResources();

            Logger.LogInfo(string.Format(
                "[LME Lifecycle v0.8.2] Scene loaded: {0} (Gen:{1}). Verified source-lifetime caches: Spine (Destroyed:{2}, Retained:{3}), Cursor (Destroyed:{4}, Retained:{5})",
                sceneName,
                generation,
                SpineAtlasCachePatch.DestroyedNativeObjectCount,
                SpineAtlasCachePatch.RetainedInUseObjectCount,
                CursorLeakPatch.DestroyedCursorTexCount,
                CursorLeakPatch.RetainedCursorCount));
        }

        private void ApplyAndVerifyPatch(Type patchType, string patchName)
        {
            TotalModuleCount++;
            try
            {
                var processor = HarmonyInstance.CreateClassProcessor(patchType);
                var patchedMethods = processor.Patch();

                if (patchedMethods != null && patchedMethods.Count > 0)
                {
                    HookedModuleCount++;
                    HookedTargetMethodCount += patchedMethods.Count;
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
            GameProfiler.Update(Time.unscaledTime);

            // プレイ中のゲーム使用中オブジェクトの誤破棄を防ぐため、プレイ中のDestroy呼び出しは行わない
            if (Time.unscaledTime - _lastPruneTime >= 5.0f)
            {
                WorkerSpriteSetterPatch.PruneDeadReferences();
                IsolateRoomPatch.PruneDeadReferences();
                WorkerPortraitPatch.PruneDeadReferences();
                SoundEffectCachePatch.PruneDeadReferences();
                _lastPruneTime = Time.unscaledTime;
            }
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SpineAtlasCachePatch.ForceClearAllAndDestroy();
            CursorLeakPatch.ForceClearAllAndDestroy();
            BoundedWorkerPool.Shutdown();
        }
    }

    public static class PluginInfo
    {
        public const string PLUGIN_GUID = "com.isaka.lobotonymodernengine";
        public const string PLUGIN_NAME = "Lobotomy Modern Engine";
        public const string PLUGIN_VERSION = "0.8.5";
    }
}
