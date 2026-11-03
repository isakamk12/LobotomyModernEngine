using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Inventory;
using UnityEngine;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// v0.7.0 バニラ固有バグ修正パッチ群（note記事『ロボトミーコーポレーションのバグを全て解消するMODを作る企画』準拠）
    /// 全9項目のバニラバグを個別Configトグルで安全にON/OFF・切り戻し可能に設計。
    /// </summary>
    public static class VanillaBugFixPatch
    {
        public static int TotalTriggeredFixes { get; private set; }
        public static int ActiveBugFixGroupCount { get; set; }

        // DangoCreatureWeapon（笑顔）の各ヒット直前にターゲットが生存していたかを記録
        private static readonly Dictionary<DangoCreatureWeapon, bool> _dangoTargetAliveBeforeHit
            = new Dictionary<DangoCreatureWeapon, bool>();

        // RedHoodWeapon（紅の傷跡）の本来のXML射程（通常攻撃射程）を保持
        private static readonly Dictionary<int, float> _redHoodBaseRangeByEquipId
            = new Dictionary<int, float>();

        // プライベートフィールド高速アクセス用キャッシュ
        private static readonly FieldInfo _bossBirdArmorWorkerField
            = AccessTools.Field(typeof(BossBirdArmor), "_worker");

        private static readonly FieldInfo _slimeGirlLoverField
            = AccessTools.Field(typeof(SlimeGirl), "lover");

        private static readonly FieldInfo _dangoSkillAvailableField
            = AccessTools.Field(typeof(DangoCreatureWeapon), "skillAvailable");

        private static readonly FieldInfo _dangoBufCountField
            = AccessTools.Field(typeof(DangoCreatureWeapon), "_bufCount");

        private static readonly FieldInfo _dangoIsSpecialField
            = AccessTools.Field(typeof(DangoCreatureWeapon), "_isSpecial");

        private static readonly MethodInfo _dangoSlowAllMethod
            = AccessTools.Method(typeof(DangoCreatureWeapon), "SlowAll");

        private static readonly MethodInfo _dangoSlowTargetMethod
            = AccessTools.Method(typeof(DangoCreatureWeapon), "SlowTarget");

        private static readonly FieldInfo _ladyWallBackDmgStateField
            = AccessTools.Field(typeof(LadyLookingAtWallWeapon), "_backDmgState");

        private static readonly FieldInfo _blueStarWorkerField
            = AccessTools.Field(typeof(BlueStarArmor), "worker");

        private static readonly FieldInfo _redHoodIsSpecialField
            = AccessTools.Field(typeof(RedHoodWeapon), "isSpecial");

        private static readonly FieldInfo _redHoodDefaultInfoField
            = AccessTools.Field(typeof(RedHoodWeapon), "defaultInfo");

        private static readonly FieldInfo _redHoodSpecialInfoField
            = AccessTools.Field(typeof(RedHoodWeapon), "specialInfo");

        private static readonly FieldInfo _smallBirdPerchObjectField
            = AccessTools.Field(typeof(SmallBirdAnim), "perchObject");

        private static readonly FieldInfo _smallBirdPerchPositionField
            = AccessTools.Field(typeof(SmallBirdAnim), "perchPosition");

        private static readonly FieldInfo _binahIsHaltedField
            = AccessTools.Field(typeof(BinahCoreScript), "_isHalted");

        private static readonly FieldInfo _binahRecoverTimerField
            = AccessTools.Field(typeof(BinahCoreScript), "_recoverTimer");

        private static readonly FieldInfo _movablePathInfoField
            = AccessTools.Field(typeof(MovableObjectNode), "pathInfo");

        private static readonly FieldInfo _movablePathIndexField
            = AccessTools.Field(typeof(MovableObjectNode), "pathIndex");

        public static void ClearCache()
        {
            _dangoTargetAliveBeforeHit.Clear();
            VoidDreamUnconSafety_Patch.ClearCache();
        }

        // =========================================================================
        // 1. いつでもどこでも黄昏バグ修正
        //    - UnitModel.GetDamageFactorByEquipment() が armor ではなく weapon != null をチェックしていたコピペバグ修正
        //    - BossBirdArmor.GetDamageFactor() が黄昏武器(200038)以外の全武器の攻撃力まで最大2倍にしていたバグ修正
        // =========================================================================

        [HarmonyPatch(typeof(UnitModel), "GetDamageFactorByEquipment")]
        public static class UnitModel_GetDamageFactorByEquipment_Patch
        {
            public static bool Prefix(UnitModel __instance, ref float __result)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixTwilightArmorGlobalBuff == null || !Plugin.FixTwilightArmorGlobalBuff.Value)
                {
                    return true;
                }

                float factor = 1f;
                var equipment = __instance.Equipment;
                if (equipment != null)
                {
                    if (equipment.weapon != null)
                    {
                        factor *= equipment.weapon.GetDamageFactor();
                    }
                    if (equipment.armor != null)
                    {
                        factor *= equipment.armor.GetDamageFactor();
                    }
                    if (equipment.gifts != null)
                    {
                        factor *= equipment.gifts.GetDamageFactor();
                    }
                }
                __result = factor;
                return false;
            }
        }

        [HarmonyPatch(typeof(BossBirdArmor), "GetDamageFactor")]
        public static class BossBirdArmor_GetDamageFactor_Patch
        {
            public static void Postfix(BossBirdArmor __instance, ref float __result)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixTwilightArmorGlobalBuff == null || !Plugin.FixTwilightArmorGlobalBuff.Value)
                {
                    return;
                }

                // 特殊能力説明文「この武器を装着した職員の体力が減少するほど武器の攻撃力が増加」の通り、
                // 黄昏武器 (ID: 200038) を装着している場合のみ攻撃力増加を適用する
                WorkerModel worker = null;
                if (_bossBirdArmorWorkerField != null)
                {
                    worker = _bossBirdArmorWorkerField.GetValue(__instance) as WorkerModel;
                }
                if (worker == null && __instance.model != null)
                {
                    worker = __instance.model.owner as WorkerModel;
                }

                bool hasTwilightWeapon = worker != null
                    && worker.Equipment != null
                    && worker.Equipment.weapon != null
                    && worker.Equipment.weapon.metaInfo != null
                    && worker.Equipment.weapon.metaInfo.id == 200038;

                if (!hasTwilightWeapon && __result > 1f)
                {
                    __result = 1f;
                    TotalTriggeredFixes++;
                }
            }
        }

        // =========================================================================
        // 2. プラシーボ効果バグ修正（ケセド研究「HP、SP弾精製」ID:803 が機能しないバグ）
        //    GlobalBulletManager は IsUpgradedAbility("upgrade_recover_bullet") を参照するが、
        //    Research ID:803 側で specialAbility に登録されておらず永久に false になっていた問題を修正
        // =========================================================================

        [HarmonyPatch(typeof(ResearchDataModel), "IsUpgradedAbility", new Type[] { typeof(string) })]
        public static class ResearchDataModel_IsUpgradedAbility_Patch
        {
            public static void Postfix(ResearchDataModel __instance, string name, ref bool __result)
            {
                if (__result) return;
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixChesedBulletRefiningResearch == null || !Plugin.FixChesedBulletRefiningResearch.Value)
                {
                    return;
                }

                if (name == "upgrade_recover_bullet" && __instance != null)
                {
                    ResearchItemModel item = __instance.GetModel(803);
                    if (item != null && item.curLevel > 0)
                    {
                        __result = true;
                        TotalTriggeredFixes++;
                    }
                }
            }
        }

        // =========================================================================
        // 3. マルクトあげませんバグ修正（マルクトコア抑制報酬「職員の移動速度が増加」未実装バグ）
        //    SefiraBossDesc_jp.xml に明記されている移動速度ボーナスを GetMovementBufBySefiraAbility に追加
        // =========================================================================

        [HarmonyPatch(typeof(AgentModel), "GetMovementBufBySefiraAbility")]
        public static class AgentModel_GetMovementBufBySefiraAbility_Patch
        {
            public static void Postfix(ref int __result)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixMalkuthBossMovementReward == null || !Plugin.FixMalkuthBossMovementReward.Value)
                {
                    return;
                }

                if (MissionManager.instance != null && MissionManager.instance.ExistsFinishedBossMission(SefiraEnum.MALKUT))
                {
                    // マルクトの最高ランク連続勤務ボーナス相当(+10)を全職員に付与
                    __result += 10;
                    TotalTriggeredFixes++;
                }
            }
        }

        // =========================================================================
        // 4. 溶ける愛ヤンデレ・ツンデレバグ修正 (SlimeGirl)
        //    - ヤンデレバグ: OnReleaseWork で感染職員(item)ではなく親愛職員(agent)の SLIMEGIRL_PAWN を参照していたため、
        //      抑圧作業失敗時に感染者が一生スライム化しないバグを修正
        //    - ツンデレバグ: OnFixedUpdate で収容部門ではなく施設全体の生存職員数を分母にしていたバグを修正
        // =========================================================================

        [HarmonyPatch(typeof(SlimeGirl), "OnReleaseWork", new Type[] { typeof(UseSkill) })]
        public static class SlimeGirl_OnReleaseWork_Patch
        {
            public static void Postfix(SlimeGirl __instance, UseSkill skill)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixSlimeGirlBugs == null || !Plugin.FixSlimeGirlBugs.Value)
                {
                    return;
                }

                if (__instance == null || skill == null || skill.agent == null || skill.skillTypeInfo == null)
                {
                    return;
                }

                WorkerModel lover = _slimeGirlLoverField != null
                    ? _slimeGirlLoverField.GetValue(__instance) as WorkerModel
                    : null;

                // 親愛職員が洞察(id==2)を行い、成功数が24未満だった場合、感染職員(item)をスライムに変異させる
                if (lover != null && skill.agent == lover && skill.skillTypeInfo.id == 2 && skill.successCount < 24)
                {
                    if (WorkerManager.instance == null) return;
                    IList<WorkerModel> workers = WorkerManager.instance.GetWorkerList();
                    if (workers == null) return;

                    int count = workers.Count;
                    for (int i = 0; i < count; i++)
                    {
                        WorkerModel item = workers[i];
                        if (item != null && !item.IsDead() && !item.invincible)
                        {
                            SlimePawnBuf pawnBuf = item.GetUnitBufByType(UnitBufType.SLIMEGIRL_PAWN) as SlimePawnBuf;
                            if (pawnBuf != null)
                            {
                                pawnBuf.Transform();
                                TotalTriggeredFixes++;
                            }
                        }
                    }
                }
            }
        }

        [HarmonyPatch(typeof(SlimeGirl), "OnFixedUpdate", new Type[] { typeof(CreatureModel) })]
        public static class SlimeGirl_OnFixedUpdate_Patch
        {
            public static void Postfix(SlimeGirl __instance, CreatureModel creature)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixSlimeGirlBugs == null || !Plugin.FixSlimeGirlBugs.Value)
                {
                    return;
                }

                if (__instance == null || creature == null || creature.sefira == null)
                {
                    return;
                }

                if (__instance.phase != SlimeGirl.Phase.ISOLATE || creature.qliphothCounter <= 0)
                {
                    return;
                }

                WorkerModel lover = _slimeGirlLoverField != null
                    ? _slimeGirlLoverField.GetValue(__instance) as WorkerModel
                    : null;

                if (lover == null || lover.IsDead())
                {
                    return;
                }

                // 収容部門内の生存職員（エージェント＋オフィサー）に対する感染者比率で50%判定を行う
                Sefira dept = creature.sefira;
                int deptAlive = 0;
                int deptPawnCnt = 0;

                if (dept.agentList != null)
                {
                    for (int i = 0; i < dept.agentList.Count; i++)
                    {
                        AgentModel a = dept.agentList[i];
                        if (a != null && !a.IsDead())
                        {
                            deptAlive++;
                            if (a.HasUnitBuf(UnitBufType.SLIMEGIRL_PAWN)) deptPawnCnt++;
                        }
                    }
                }
                if (dept.officerList != null)
                {
                    for (int i = 0; i < dept.officerList.Count; i++)
                    {
                        OfficerModel o = dept.officerList[i];
                        if (o != null && !o.IsDead())
                        {
                            deptAlive++;
                            if (o.HasUnitBuf(UnitBufType.SLIMEGIRL_PAWN)) deptPawnCnt++;
                        }
                    }
                }

                if (deptAlive > 0 && ((float)deptPawnCnt / (float)deptAlive) >= 0.5f)
                {
                    creature.SetQliphothCounter(0);
                    TotalTriggeredFixes++;
                }
            }
        }

        // =========================================================================
        // 5. 壁に向かう女武器「叫び」バグ修正 (LadyLookingAtWallWeapon)
        //    説明文「勇気/慎重がランク3未満の場合、攻撃時に50％の確率で精神力が5減少」に対し、
        //    バニラコードが「勇気＜3 かつ 慎重＜3 で100%の確率で0.1ダメージ」になっていたバグを修正
        // =========================================================================

        [HarmonyPatch(typeof(LadyLookingAtWallWeapon), "OnGiveDamage")]
        public static class LadyLookingAtWallWeapon_OnGiveDamage_Patch
        {
            public static bool Prefix(LadyLookingAtWallWeapon __instance, UnitModel actor, UnitModel target, ref DamageInfo dmg, ref bool __result)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixLadyLookingAtWallWeapon == null || !Plugin.FixLadyLookingAtWallWeapon.Value)
                {
                    return true;
                }

                if (_ladyWallBackDmgStateField != null)
                {
                    bool backDmgState = (bool)_ladyWallBackDmgStateField.GetValue(__instance);
                    if (backDmgState)
                    {
                        AgentModel agentModel = actor as AgentModel;
                        if (agentModel != null && (agentModel.fortitudeLevel < 3 || agentModel.prudenceLevel < 3))
                        {
                            if (UnityEngine.Random.Range(0f, 1f) < 0.5f)
                            {
                                agentModel.TakeDamage(actor, new DamageInfo(RwbpType.W, 5f));
                                TotalTriggeredFixes++;
                            }
                        }
                        _ladyWallBackDmgStateField.SetValue(__instance, false);
                    }
                }

                __result = true;
                return false;
            }
        }

        // =========================================================================
        // 6. 笑顔（団子）特殊攻撃キルバフ不発バグ修正 (DangoCreatureWeapon)
        //    特殊攻撃は6連撃（damageInfos[1] + damageInfos[2]×5）だが、
        //    1撃目で敵が生き残ると OnGiveDamageAfter で skillAvailable = false にされてしまい、
        //    2〜6撃目で敵を倒しても一生キルバフ（体力+3・正義+3）が付与されないバグを修正
        // =========================================================================

        [HarmonyPatch(typeof(DangoCreatureWeapon), "OnGiveDamage")]
        public static class DangoCreatureWeapon_OnGiveDamage_Patch
        {
            public static bool Prefix(DangoCreatureWeapon __instance, UnitModel actor, UnitModel target, ref DamageInfo dmg, ref bool __result)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixDangoWeaponKillBuff == null || !Plugin.FixDangoWeaponKillBuff.Value)
                {
                    return true;
                }

                // このヒットの直前にターゲットが生存していたかを記録
                bool aliveBefore = target != null && target.hp > 0f;
                _dangoTargetAliveBeforeHit[__instance] = aliveBefore;
                __result = true;
                return false;
            }
        }

        [HarmonyPatch(typeof(DangoCreatureWeapon), "OnGiveDamageAfter")]
        public static class DangoCreatureWeapon_OnGiveDamageAfter_Patch
        {
            public static bool Prefix(DangoCreatureWeapon __instance, UnitModel actor, UnitModel target, DamageInfo dmg)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixDangoWeaponKillBuff == null || !Plugin.FixDangoWeaponKillBuff.Value)
                {
                    return true;
                }

                bool aliveBefore = false;
                if (_dangoTargetAliveBeforeHit.TryGetValue(__instance, out aliveBefore))
                {
                    _dangoTargetAliveBeforeHit.Remove(__instance);
                }

                bool skillAvailable = _dangoSkillAvailableField != null && (bool)_dangoSkillAvailableField.GetValue(__instance);
                int bufCount = _dangoBufCountField != null ? (int)_dangoBufCountField.GetValue(__instance) : 10;
                bool isSpecial = _dangoIsSpecialField != null && (bool)_dangoIsSpecialField.GetValue(__instance);

                // 「このヒットの直前まで生きており、このヒットでHPが0以下になった」場合にキルバフを付与
                if (aliveBefore && target != null && target.hp <= 0f && skillAvailable && bufCount < 10)
                {
                    WorkerModel workerModel = actor as WorkerModel;
                    if (workerModel != null)
                    {
                        UnitStatBuf unitStatBuf = new UnitStatBuf(float.MaxValue);
                        unitStatBuf.duplicateType = BufDuplicateType.UNLIMIT;
                        unitStatBuf.primaryStat.hp += 3;
                        unitStatBuf.primaryStat.battle += 3;
                        workerModel.AddUnitBuf(unitStatBuf);

                        if (_dangoBufCountField != null)
                        {
                            _dangoBufCountField.SetValue(__instance, bufCount + 1);
                        }
                        if (_dangoSkillAvailableField != null)
                        {
                            // 1回の攻撃モーションにつき最大1スタックまで
                            _dangoSkillAvailableField.SetValue(__instance, false);
                        }
                        TotalTriggeredFixes++;
                    }
                }

                if (isSpecial)
                {
                    if (_dangoSlowAllMethod != null && actor != null && actor.GetMovableNode() != null)
                    {
                        _dangoSlowAllMethod.Invoke(__instance, new object[] { actor.GetMovableNode().currentPassage });
                    }
                }
                else
                {
                    if (_dangoSlowTargetMethod != null)
                    {
                        _dangoSlowTargetMethod.Invoke(__instance, new object[] { target });
                    }
                }

                return false;
            }
        }

        // =========================================================================
        // 7. 一等星は常に嘘つきバグ修正 (BlueStarArmor)
        //    説明文「一定時間ごとに同じ空間にいる職員を回復」の通り、精神力だけでなく体力も5回復させる
        // =========================================================================

        [HarmonyPatch(typeof(BlueStarArmor), "MpHeal")]
        public static class BlueStarArmor_MpHeal_Patch
        {
            public static void Postfix(BlueStarArmor __instance)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixBlueStarArmorHeal == null || !Plugin.FixBlueStarArmorHeal.Value)
                {
                    return;
                }

                WorkerModel owner = null;
                if (_blueStarWorkerField != null)
                {
                    owner = _blueStarWorkerField.GetValue(__instance) as WorkerModel;
                }
                if (owner == null && __instance.model != null)
                {
                    owner = __instance.model.owner as WorkerModel;
                }
                if (owner == null || owner.GetMovableNode() == null)
                {
                    return;
                }

                PassageObjectModel currentPassage = owner.GetMovableNode().currentPassage;
                if (currentPassage == null)
                {
                    return;
                }

                foreach (MovableObjectNode enteredTarget in currentPassage.GetEnteredTargets())
                {
                    if (enteredTarget != null)
                    {
                        WorkerModel workerModel = enteredTarget.GetUnit() as WorkerModel;
                        if (workerModel != null && !workerModel.IsDead() && !workerModel.HasUnitBuf(UnitBufType.QUEENBEE_SPORE))
                        {
                            workerModel.RecoverHP(5f);
                        }
                    }
                }
                TotalTriggeredFixes++;
            }
        }

        // =========================================================================
        // 8. 効かないねぇ！大鳥だから！バグ修正（大鳥 100008L がクリフォト暴走対象外になるバグ）
        //    大鳥のXMLで maxWorkCount が 0 になっているため GetMaxWorkCount() == 0 判定で暴走から除外されていた問題を修正
        // =========================================================================

        [HarmonyPatch(typeof(CreatureModel), "GetMaxWorkCount")]
        public static class CreatureModel_GetMaxWorkCount_Patch
        {
            public static void Postfix(CreatureModel __instance, ref int __result)
            {
                if (__result != 0) return;
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixBigBirdOverloadImmunity == null || !Plugin.FixBigBirdOverloadImmunity.Value)
                {
                    return;
                }

                // 大鳥 (ID: 100008L) のみ暴走抽選用の GetMaxWorkCount() で非ゼロを返す（UI表示用の GetMaxWorkCountView() には影響しない）
                if (__instance != null && __instance.metadataId == 100008L)
                {
                    __result = 4;
                    TotalTriggeredFixes++;
                }
            }
        }

        // =========================================================================
        // 9. 紅の傷跡短小バグ & インベントリ射程表記ズレ修正 (RedHoodWeapon & InventoryItemDescGetter)
        //    - RedHoodWeapon.SetNextAttackType() が通常攻撃時に metaInfo.range = 3f (短) で上書きしていたバグを本来の 4f (中) に修正
        //    - InventoryItemDescGetter.GetWeaponDesc(WeaponModel) の射程閾値を EquipmentTypeInfo 版と統一
        // =========================================================================

        [HarmonyPatch(typeof(RedHoodWeapon), "SetNextAttackType")]
        public static class RedHoodWeapon_SetNextAttackType_Patch
        {
            public static bool Prefix(RedHoodWeapon __instance)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixRedHoodWeaponRange == null || !Plugin.FixRedHoodWeaponRange.Value)
                {
                    return true;
                }

                if (__instance == null || __instance.model == null || __instance.model.metaInfo == null)
                {
                    return true;
                }

                EquipmentTypeInfo metaInfo = __instance.model.metaInfo;
                float baseRange = 4f;
                if (!_redHoodBaseRangeByEquipId.TryGetValue(metaInfo.id, out baseRange))
                {
                    baseRange = metaInfo.range >= 4f && metaInfo.range < 15f ? metaInfo.range : 4f;
                    _redHoodBaseRangeByEquipId[metaInfo.id] = baseRange;
                }

                if (UnityEngine.Random.value < 0.3f)
                {
                    if (_redHoodIsSpecialField != null) _redHoodIsSpecialField.SetValue(__instance, true);
                    metaInfo.range = 15f;
                    if (_redHoodSpecialInfoField != null)
                    {
                        SplashInfo specialInfo = _redHoodSpecialInfoField.GetValue(__instance) as SplashInfo;
                        if (specialInfo != null) metaInfo.splashInfo = specialInfo;
                    }
                }
                else
                {
                    if (_redHoodIsSpecialField != null) _redHoodIsSpecialField.SetValue(__instance, false);
                    metaInfo.range = baseRange;
                    if (_redHoodDefaultInfoField != null)
                    {
                        SplashInfo defaultInfo = _redHoodDefaultInfoField.GetValue(__instance) as SplashInfo;
                        if (defaultInfo != null) metaInfo.splashInfo = defaultInfo;
                    }
                }

                TotalTriggeredFixes++;
                return false;
            }
        }

        [HarmonyPatch(typeof(InventoryItemDescGetter), "GetWeaponDesc", new Type[] { typeof(WeaponModel), typeof(string), typeof(string) },
            new ArgumentType[] { ArgumentType.Normal, ArgumentType.Out, ArgumentType.Out })]
        public static class InventoryItemDescGetter_GetWeaponDesc_Patch
        {
            public static bool Prefix(WeaponModel model, out string attackSpeed, out string attackRange)
            {
                attackSpeed = string.Empty;
                attackRange = string.Empty;

                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixRedHoodWeaponRange == null || !Plugin.FixRedHoodWeaponRange.Value ||
                    model == null || model.metaInfo == null)
                {
                    return true;
                }

                // EquipmentTypeInfo 版と同じ正しい閾値 (<3:超短, <4:短, <10:中, <20:長, >=20:超長) に統一し、
                // 紅の傷跡(200032)が特殊攻撃準備中(15f)でもインベントリ表記が「長」に化けないよう本来の4fで表示する
                float spd = model.metaInfo.attackSpeed;
                float rng = model.metaInfo.id == 200032 ? 4f : model.metaInfo.range;

                int spdIdx = (!((double)spd < 1.1)) ? ((spd < 2f) ? 1 : ((spd < 3f) ? 2 : ((!(spd < 5f)) ? 4 : 3))) : 0;
                int rngIdx = (rng < 3f) ? 4 : ((rng < 4f) ? 3 : ((rng < 10f) ? 2 : ((rng < 20f) ? 1 : 0)));

                attackSpeed = InventoryItemDescGetter.GetText(InventoryItemDescGetter.AttackSpeedKey[spdIdx]);
                attackRange = InventoryItemDescGetter.GetText(InventoryItemDescGetter.AttackRangeKey[rngIdx]);
                return false;
            }
        }

        // =========================================================================
        // 10. 速度デバフ武器（氷のかけら・笑顔・失楽園等）＆試練エネミー移動速度不全バグ修正
        //     - SnowQueenSlow, DangoCreatureWeaponSlow_Normal, DangoCreatureWeaponSlow_Special, DeathAngelWeaponSlow が
        //       creature.movementScale を直接代入して上書きし、幻想体AIの毎フレーム固定値代入で1フレームで無効化され、
        //       終了時に割り算して2倍・3倍に爆走暴走していたバグを修正（正規の GetMovementScaleByBuf() 乗算に一本化）。
        //     - OrdealCreatureModel.OnFixedUpdate() に GetMovementScaleByBuf() が脱落し試練エネミーに全減速デバフが無効だったバグ修正。
        // =========================================================================

        [HarmonyPatch(typeof(SnowQueenSlow))]
        public static class SnowQueenSlow_Patch
        {
            [HarmonyPatch("Init", new Type[] { typeof(UnitModel) })]
            [HarmonyPrefix]
            public static bool Prefix_Init(SnowQueenSlow __instance, UnitModel model)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixMovementSlowDebuffs == null || !Plugin.FixMovementSlowDebuffs.Value)
                    return true;

                __instance.model = model;
                __instance.remainTime = 3f;
                TotalTriggeredFixes++;
                return false; // creature.movementScale の直接代入を回避（GetMovementScaleByBuf が MovementScale() 0.5f を参照）
            }

            [HarmonyPatch("OnDestroy")]
            [HarmonyPrefix]
            public static bool Prefix_OnDestroy(SnowQueenSlow __instance)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixMovementSlowDebuffs == null || !Plugin.FixMovementSlowDebuffs.Value)
                    return true;

                return false; // creature.movementScale の除算（2倍爆走暴走）を回避
            }
        }

        [HarmonyPatch(typeof(DangoCreatureWeaponSlow_Normal))]
        public static class DangoSlowNormal_Patch
        {
            [HarmonyPatch("Init", new Type[] { typeof(UnitModel) })]
            [HarmonyPrefix]
            public static bool Prefix_Init(DangoCreatureWeaponSlow_Normal __instance, UnitModel model)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixMovementSlowDebuffs == null || !Plugin.FixMovementSlowDebuffs.Value)
                    return true;

                __instance.model = model;
                __instance.remainTime = 3f;
                UnitBuf special = model.GetUnitBufByType(UnitBufType.DANGO_CREATURE_WEAPON_SLOW_SPECIAL);
                if (special != null) special.Destroy();

                TotalTriggeredFixes++;
                return false;
            }

            [HarmonyPatch("OnDestroy")]
            [HarmonyPrefix]
            public static bool Prefix_OnDestroy(DangoCreatureWeaponSlow_Normal __instance)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixMovementSlowDebuffs == null || !Plugin.FixMovementSlowDebuffs.Value)
                    return true;

                return false;
            }
        }

        [HarmonyPatch(typeof(DangoCreatureWeaponSlow_Special))]
        public static class DangoSlowSpecial_Patch
        {
            [HarmonyPatch("Init", new Type[] { typeof(UnitModel) })]
            [HarmonyPrefix]
            public static bool Prefix_Init(DangoCreatureWeaponSlow_Special __instance, UnitModel model)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixMovementSlowDebuffs == null || !Plugin.FixMovementSlowDebuffs.Value)
                    return true;

                __instance.model = model;
                __instance.remainTime = 3f;
                UnitBuf normal = model.GetUnitBufByType(UnitBufType.DANGO_CREATURE_WEAPON_SLOW_NORMAL);
                if (normal != null) normal.Destroy();

                TotalTriggeredFixes++;
                return false;
            }

            [HarmonyPatch("OnDestroy")]
            [HarmonyPrefix]
            public static bool Prefix_OnDestroy(DangoCreatureWeaponSlow_Special __instance)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixMovementSlowDebuffs == null || !Plugin.FixMovementSlowDebuffs.Value)
                    return true;

                return false;
            }
        }

        [HarmonyPatch(typeof(DeathAngelWeaponSlow))]
        public static class DeathAngelSlow_Patch
        {
            [HarmonyPatch("Init", new Type[] { typeof(UnitModel) })]
            [HarmonyPrefix]
            public static bool Prefix_Init(DeathAngelWeaponSlow __instance, UnitModel model)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixMovementSlowDebuffs == null || !Plugin.FixMovementSlowDebuffs.Value)
                    return true;

                __instance.model = model;
                __instance.remainTime = 5f;
                TotalTriggeredFixes++;
                return false;
            }

            [HarmonyPatch("OnDestroy")]
            [HarmonyPrefix]
            public static bool Prefix_OnDestroy(DeathAngelWeaponSlow __instance)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixMovementSlowDebuffs == null || !Plugin.FixMovementSlowDebuffs.Value)
                    return true;

                return false;
            }
        }

        [HarmonyPatch(typeof(OrdealCreatureModel), "OnFixedUpdate")]
        public static class OrdealCreatureModel_OnFixedUpdate_Patch
        {
            private static readonly MethodInfo TargetProcessMoveNode = AccessTools.Method(typeof(MovableObjectNode), "ProcessMoveNode", new Type[] { typeof(float) });
            private static readonly MethodInfo ReplacementMethod = AccessTools.Method(typeof(OrdealCreatureModel_OnFixedUpdate_Patch), nameof(ProcessMoveNodeWithBuf));

            [HarmonyPrefix]
            public static void Prefix(OrdealCreatureModel __instance)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixMovementSlowDebuffs == null || !Plugin.FixMovementSlowDebuffs.Value ||
                    __instance == null)
                    return;

                // バニラで脱落していたバフ状態更新（減速デバフタイマー等）を実行
                __instance.UpdateBufState();
            }

            public static void ProcessMoveNodeWithBuf(MovableObjectNode node, float baseSpeed, OrdealCreatureModel creature)
            {
                if (node == null) return;
                float bufScale = creature != null ? creature.GetMovementScaleByBuf() : 1f;
                if (bufScale < 0.99f)
                {
                    TotalTriggeredFixes++;
                }
                node.ProcessMoveNode(baseSpeed * bufScale);
            }

            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
                for (int i = 0; i < codes.Count; i++)
                {
                    var inst = codes[i];
                    if (inst.opcode == OpCodes.Callvirt && inst.operand is MethodInfo mi && mi == TargetProcessMoveNode)
                    {
                        // ProcessMoveNode(float) -> ProcessMoveNodeWithBuf(node, float, this)
                        // スタックにはすでに (MovableObjectNode node, float speed) が積まれているので ldarg.0 を追加して ReplacementMethod を呼ぶ
                        codes.Insert(i, new CodeInstruction(OpCodes.Ldarg_0));
                        inst.opcode = OpCodes.Call;
                        inst.operand = ReplacementMethod;
                        break;
                    }
                }
                return codes;
            }
        }

        // =========================================================================
        // 【v0.7.7 武器関連バグ修正群】
        // =========================================================================

        /// <summary>
        /// ① 黄昏の通常攻撃で最後の青属性（Pale）が出ないバグ修正
        /// Spineイベントが3回しか鳴らずキューに残った4番目のPaleダメージを攻撃完了直前 (BossBirdWeapon.OnAttackEnd) に確実にディスパッチする。
        /// </summary>
        [HarmonyPatch(typeof(BossBirdWeapon), "OnAttackEnd", new Type[] { typeof(UnitModel), typeof(UnitModel) })]
        public static class TwilightPaleAttack_Patch
        {
            private static readonly FieldInfo _currentDamageInfosField = AccessTools.Field(typeof(WeaponModel), "_currentDamageInfos");

            [HarmonyPrefix]
            public static void Prefix(BossBirdWeapon __instance, UnitModel actor, UnitModel target)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixTwilightPaleAttack == null || !Plugin.FixTwilightPaleAttack.Value ||
                    __instance == null || actor == null)
                    return;

                var weapon = __instance.model as WeaponModel;
                if (weapon == null && actor.Equipment != null)
                {
                    weapon = actor.Equipment.weapon;
                }
                if (weapon != null)
                {
                    var queue = _currentDamageInfosField?.GetValue(weapon) as Queue<DamageInfo>;
                    if (queue != null && queue.Count > 0)
                    {
                        while (queue.Count > 0)
                        {
                            weapon.OnGiveDamage(actor);
                            TotalTriggeredFixes++;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// ② 銀河の子武器がメイスなのに射程が銃並み（range=8）になっているバグ修正
        /// 本来のメイス近接射程（range=3）に補正する。
        /// </summary>
        [HarmonyPatch(typeof(EquipmentTypeList), "GetData", new Type[] { typeof(int) })]
        public static class ChildOfGalaxyRange_Patch
        {
            private const int GALAXY_WEAPON_ID = 200031;
            private const float FIXED_MACE_RANGE = 3.0f;

            [HarmonyPostfix]
            public static void Postfix(int id, EquipmentTypeInfo __result)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixChildOfGalaxyWeaponRange == null || !Plugin.FixChildOfGalaxyWeaponRange.Value ||
                    __result == null)
                    return;

                if (id == GALAXY_WEAPON_ID && __result.type == EquipmentTypeInfo.EquipmentType.WEAPON)
                {
                    if (__result.range > FIXED_MACE_RANGE)
                    {
                        __result.range = FIXED_MACE_RANGE;
                        TotalTriggeredFixes++;
                    }
                }
            }
        }

        /// <summary>
        /// ③ 溶ける愛の武器「ラブ」の弾丸がパニック職員を無視・すり抜けて鎮圧できないバグ修正
        /// バニラで CannotControll / unconAction != null の職員を除外している判定をバイパスし、パニック職員に命中させる。
        /// </summary>
        [HarmonyPatch(typeof(SlimeGirlWeapon), "CheckHit")]
        public static class SlimeGirlWeaponPanicTarget_Patch
        {
            private static readonly MethodInfo _giveDamageMethod = AccessTools.Method(typeof(SlimeGirlWeapon), "GiveDamage", new Type[] { typeof(UnitModel) });

            [HarmonyPrefix]
            public static bool Prefix(SlimeGirlWeapon __instance, UnitModel target, ref bool __result)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixSlimeGirlWeaponPanicTarget == null || !Plugin.FixSlimeGirlWeaponPanicTarget.Value ||
                    __instance == null || target == null)
                    return true;

                if (!target.IsAttackTargetable())
                    return true;

                if (target is WorkerModel worker && worker != __instance.model.owner as WorkerModel && worker.IsPanic())
                {
                    _giveDamageMethod?.Invoke(__instance, new object[] { target });
                    TotalTriggeredFixes++;
                    __result = true;
                    return false;
                }

                return true;
            }
        }

        /// <summary>
        /// ④ 夢喰い獏（空虚な夢）覚醒時の職員永久操作不能・無敵化バグ修正
        /// アニメーションイベントコールバックのキュー枯渇保護と、覚醒タイマー安全救出機構を追加。
        /// </summary>
        [HarmonyPatch(typeof(Baku), "OnWorkerAnimCalled")]
        public static class BakuAnimCallGuard_Patch
        {
            private static readonly FieldInfo _wakeupField = AccessTools.Field(typeof(Baku), "wakeup");
            private static readonly FieldInfo _annoyedField = AccessTools.Field(typeof(Baku), "annoyed");

            [HarmonyPrefix]
            public static bool Prefix(Baku __instance, int i)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixVoidDreamWakeUpFreeze == null || !Plugin.FixVoidDreamWakeUpFreeze.Value ||
                    __instance == null)
                    return true;

                if (i == 1000600)
                {
                    var q = _wakeupField?.GetValue(__instance) as Queue<WorkerModel>;
                    if (q == null || q.Count == 0) return false;
                }
                else if (i == 1000601)
                {
                    var q = _annoyedField?.GetValue(__instance) as Queue<WorkerModel>;
                    if (q == null || q.Count == 0) return false;
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(Uncontrollable_Baku), "Execute")]
        public static class VoidDreamUnconSafety_Patch
        {
            private static readonly FieldInfo _modelField = AccessTools.Field(typeof(Uncontrollable_Baku), "model");
            private static readonly FieldInfo _wakedField = AccessTools.Field(typeof(Uncontrollable_Baku), "waked");

            private static readonly Dictionary<Uncontrollable_Baku, float> _wakeElapsedMap
                = new Dictionary<Uncontrollable_Baku, float>();

            [HarmonyPostfix]
            public static void Postfix(Uncontrollable_Baku __instance)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixVoidDreamWakeUpFreeze == null || !Plugin.FixVoidDreamWakeUpFreeze.Value ||
                    __instance == null)
                    return;

                bool waked = false;
                if (_wakedField != null)
                {
                    waked = (bool)_wakedField.GetValue(__instance);
                }

                if (waked)
                {
                    float elapsed = 0f;
                    _wakeElapsedMap.TryGetValue(__instance, out elapsed);
                    elapsed += Time.deltaTime;
                    _wakeElapsedMap[__instance] = elapsed;

                    if (elapsed > 2.0f)
                    {
                        var worker = _modelField?.GetValue(__instance) as WorkerModel;
                        if (worker != null && worker.unconAction == __instance)
                        {
                            worker.GetControl();
                            worker.SetInvincible(false);
                            _wakeElapsedMap.Remove(__instance);
                            TotalTriggeredFixes++;
                        }
                    }
                }
            }

            public static void ClearCache()
            {
                _wakeElapsedMap.Clear();
            }
        }

        /// <summary>
        /// ⑤ 多段演出なのにダメージが1回しか入らないバグ修正（宇宙の欠片等）
        /// 多段攻撃演出に合わせて適切に分割したDamageInfo配列を返す。
        /// </summary>
        [HarmonyPatch(typeof(CosmosInnerWeapon), "OnAttackStart")]
        public static class CosmosInnerWeapon_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(CosmosInnerWeapon __instance, UnitModel actor, UnitModel target, ref EquipmentScriptBase.WeaponDamageInfo __result)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixMultiHitWeaponDamage == null || !Plugin.FixMultiHitWeaponDamage.Value ||
                    __instance == null || actor == null)
                    return true;

                var weaponModel = __instance.model as WeaponModel;
                if (weaponModel == null || weaponModel.metaInfo == null || weaponModel.metaInfo.animationNames == null || weaponModel.metaInfo.animationNames.Length < 2)
                    return true;

                string anim = weaponModel.metaInfo.animationNames[UnityEngine.Random.Range(0, 2)];
                DamageInfo fullDmg = weaponModel.GetDamage(actor);
                DamageInfo d1 = fullDmg.Copy();
                DamageInfo d2 = fullDmg.Copy();
                d1.min *= 0.5f; d1.max *= 0.5f;
                d2.min *= 0.5f; d2.max *= 0.5f;

                __result = new EquipmentScriptBase.WeaponDamageInfo(anim, new DamageInfo[] { d1, d2 });
                TotalTriggeredFixes++;
                return false;
            }
        }

        /// <summary>
        /// ⑥ 攻撃キャンセルと命中判定の不一致（移動中のゴースト攻撃・キューズレ）修正
        /// CancelWeapon時に残存DamageInfoキューを完全消去し、Spineアニメーション状態を安全にリセットする。
        /// </summary>
        [HarmonyPatch(typeof(UnitModel), "CancelWeapon")]
        public static class AttackCancelHitboxMismatch_Patch
        {
            private static readonly FieldInfo _currentDamageInfosField = AccessTools.Field(typeof(WeaponModel), "_currentDamageInfos");

            [HarmonyPostfix]
            public static void Postfix(UnitModel __instance)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixAttackCancelHitboxMismatch == null || !Plugin.FixAttackCancelHitboxMismatch.Value ||
                    __instance == null)
                    return;

                if (__instance.Equipment != null && __instance.Equipment.weapon != null)
                {
                    var weapon = __instance.Equipment.weapon;
                    var queue = _currentDamageInfosField?.GetValue(weapon) as Queue<DamageInfo>;
                    if (queue != null && queue.Count > 0)
                    {
                        queue.Clear();
                        TotalTriggeredFixes++;
                    }
                    weapon.currentTarget = null;
                }

                if (__instance is WorkerModel worker)
                {
                    try
                    {
                        var unit = worker.GetWorkerUnit();
                        if (unit != null)
                        {
                            unit.EndAttack();
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        /// <summary>
        /// ① 調律者の停止・無敵化（特に49日目／ロード後）修正
        /// フェーズ移行時に無敵フラグが解除されないバグと、非暴走時に無敵のまま凍結するバグを安全に解除するWatchdog。
        /// </summary>
        [HarmonyPatch(typeof(BinahCoreScript), "MoveToNextPhase")]
        public static class BinahMoveToNextPhase_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(BinahCoreScript __instance)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixBinahInvincibilityFreeze == null || !Plugin.FixBinahInvincibilityFreeze.Value ||
                    __instance == null)
                    return;

                if (__instance.IsInvincible)
                {
                    __instance.IsInvincible = false;
                    TotalTriggeredFixes++;
                }
            }
        }

        [HarmonyPatch(typeof(BinahCoreScript), "OnFixedUpdate")]
        public static class BinahWatchdog_Patch
        {
            private static int _frozenTicks = 0;

            [HarmonyPostfix]
            public static void Postfix(BinahCoreScript __instance)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixBinahInvincibilityFreeze == null || !Plugin.FixBinahInvincibilityFreeze.Value ||
                    __instance == null)
                    return;

                bool isHalted = false;
                if (_binahIsHaltedField != null)
                {
                    try { isHalted = (bool)_binahIsHaltedField.GetValue(__instance); } catch (Exception) { }
                }

                if (!isHalted && __instance.IsInvincible)
                {
                    // 暴走中かどうか (HasOverload)
                    if (!__instance.HasOverload())
                    {
                        _frozenTicks++;
                        if (_frozenTicks > 150) // 50Hzで約3秒以上無敵フラグが放置された場合
                        {
                            __instance.IsInvincible = false;
                            _frozenTicks = 0;
                            TotalTriggeredFixes++;
                        }
                    }
                    else
                    {
                        _frozenTicks = 0;
                    }
                }
                else
                {
                    _frozenTicks = 0;
                }
            }
        }

        /// <summary>
        /// ② 大鳥の魅了対象が無敵化するバグ修正
        /// BigBirdAttractBufのPhase 2/3でダメージ倍率が0fになり味方の鎮圧・救出が不能になるのを解除し、
        /// 味方職員による鎮圧攻撃のダメージが正常に通るようにする。
        /// </summary>
        [HarmonyPatch(typeof(BigBirdAttractBuf), "OnTakeDamage")]
        public static class BigBirdAttractBuf_OnTakeDamage_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(BigBirdAttractBuf __instance, UnitModel attacker, ref float __result)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixBigBirdCharmedDamageImmunity == null || !Plugin.FixBigBirdCharmedDamageImmunity.Value ||
                    __instance == null)
                    return true;

                if (attacker is WorkerModel)
                {
                    if (__instance.phase == BigBirdAttractBuf.Phase.SECOND || __instance.phase == BigBirdAttractBuf.Phase.THIRD)
                    {
                        __result = 1.0f;
                        TotalTriggeredFixes++;
                        return false;
                    }
                }
                return true;
            }
        }

        /// <summary>
        /// ③ 逆行時計と琥珀の黄昏のゾンビ状態修正
        /// 逆行時計で鎮圧された試練エネミーがHP>0やOnFixedUpdate継続により無敵ゾンビ化するのを完全掃討・消滅させる。
        /// </summary>
        [HarmonyPatch(typeof(ReverseClock), "Retrograde")]
        public static class ReverseClock_Retrograde_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(ReverseClock __instance, bool b)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixReverseClockZombieState == null || !Plugin.FixReverseClockZombieState.Value ||
                    !b)
                    return;

                try
                {
                    if (OrdealManager.instance != null)
                    {
                        var ordealList = OrdealManager.instance.GetOrdealCreatureList();
                        if (ordealList != null)
                        {
                            foreach (var ordeal in ordealList)
                            {
                                if (ordeal == null) continue;
                                if (ordeal.OrdealBase != null && ordeal.OrdealBase.level == OrdealLevel.MIDNIGHT)
                                    continue; // 白夜(深夜)は除外仕様

                                bool fixedTarget = false;
                                if (ordeal.hp > 0f)
                                {
                                    ordeal.hp = 0f;
                                    fixedTarget = true;
                                }
                                if (ordeal.state != CreatureState.SUPPRESSED)
                                {
                                    ordeal.Suppressed();
                                    fixedTarget = true;
                                }
                                try
                                {
                                    ordeal.script?.OnAfterSuppressed();
                                }
                                catch (Exception) { }

                                var unit = ordeal.Unit;
                                if (unit != null && unit.gameObject != null && unit.gameObject.activeSelf)
                                {
                                    unit.gameObject.SetActive(false);
                                    fixedTarget = true;
                                }

                                if (fixedTarget)
                                    TotalTriggeredFixes++;
                            }
                        }
                    }
                }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// ④ 扉付近で走行・滑走状態のまま固まるバグ修正
        /// 閉じた扉の前で進行不能なのにStopMovingが呼ばれず走行アニメーションで永久スタックするのを安全に解除する。
        /// </summary>
        [HarmonyPatch(typeof(MovableObjectNode), "ProcessMoveNode")]
        public static class DoorPassingStuck_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(MovableObjectNode __instance)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixDoorPassingStuck == null || !Plugin.FixDoorPassingStuck.Value ||
                    __instance == null)
                    return;

                if (__instance.GetState() != MovableState.MOVE)
                    return;

                if (__instance.currentNode == null || _movablePathInfoField == null || _movablePathIndexField == null)
                    return;

                try
                {
                    var pathInfo = _movablePathInfoField.GetValue(__instance) as PathResult;
                    if (pathInfo == null || pathInfo.pathEdges == null)
                        return;

                    int pathIndex = (int)_movablePathIndexField.GetValue(__instance);
                    if (pathIndex >= pathInfo.pathEdges.Length)
                        return;

                    MapEdge edge = pathInfo.pathEdges[pathIndex];
                    if (edge == null) return;

                    EdgeDirection dir = pathInfo.edgeDirections[pathIndex];
                    MapNode goalNode = edge.GetGoalNode(dir);
                    if (goalNode != null && goalNode.closed)
                    {
                        var door = goalNode.GetDoor();
                        if (door != null)
                        {
                            var unitModel = __instance.GetUnit();
                            if (unitModel != null && unitModel.CanOpenDoor())
                            {
                                door.TryOpen();
                            }
                            else
                            {
                                __instance.StopMoving();
                                TotalTriggeredFixes++;
                            }
                        }
                        else
                        {
                            __instance.StopMoving();
                            TotalTriggeredFixes++;
                        }
                    }
                }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// ⑤ 赤ずきんの表示ランクと内部値の不一致修正
        /// 依頼コスト計算とUIの危険度表示を正規化し、職員・試練対象時も含めてUIと消費エネルギーを完全一致させる。
        /// </summary>
        [HarmonyPatch(typeof(RedHood), "GetRequestCost")]
        public static class RedHood_GetRequestCost_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(UnitModel unit, ref int __result)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixRedHoodRiskLevelSync == null || !Plugin.FixRedHoodRiskLevelSync.Value ||
                    unit == null)
                    return true;

                if (unit is AgentModel agent)
                {
                    __result = Mathf.Clamp(agent.level, 1, 5) * 40;
                    TotalTriggeredFixes++;
                    return false;
                }
                if (unit is CreatureModel creature)
                {
                    int riskInt = 1;
                    if (creature is OrdealCreatureModel ordeal && ordeal.OrdealBase != null)
                    {
                        riskInt = (int)ordeal.OrdealBase.level + 1;
                    }
                    else if (creature.metaInfo != null)
                    {
                        riskInt = (int)creature.metaInfo.GetRiskLevel() + 1;
                    }
                    __result = Mathf.Clamp(riskInt, 1, 5) * 40;
                    TotalTriggeredFixes++;
                    return false;
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(RedHoodRequestUI), "Update")]
        public static class RedHoodRequestUI_Update_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(RedHoodRequestUI __instance)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixRedHoodRiskLevelSync == null || !Plugin.FixRedHoodRiskLevelSync.Value ||
                    __instance == null)
                    return;

                if (__instance.CurrentOverlayCreature != null)
                {
                    if (__instance.CurrentOverlayCreature is AgentModel agent)
                    {
                        __instance.cursorAttachedRiskLevel.enabled = true;
                        __instance.cursorAttachedRiskLevel.text = "<color=#FFFFFFFF>LV " + agent.level + "</color>";
                    }
                    else if (__instance.CurrentOverlayCreature is OrdealCreatureModel ordeal && ordeal.OrdealBase != null)
                    {
                        __instance.cursorAttachedRiskLevel.enabled = true;
                        RiskLevel risk = (RiskLevel)Mathf.Clamp((int)ordeal.OrdealBase.level, 0, 4);
                        __instance.cursorAttachedRiskLevel.text = string.Format("<color=#{0}FF>{1}</color>",
                            ColorUtility.ToHtmlStringRGB(UIColorManager.instance.GetRiskColor(risk)),
                            risk.ToString().ToUpper());
                    }
                }
            }
        }

        /// <summary>
        /// ⑥ ペスト医師／白夜の部屋交換後に罰鳥分岐（止まり木 / Perch）が残るバグ修正
        /// 収容室が入れ替わった際に罰鳥の止まり木を現在の収容室位置へ即座に再同期し、白夜の部屋に枝が置き去りにされるのを防ぐ。
        /// </summary>
        [HarmonyPatch(typeof(CreatureManager), "ChangeCreaturePos")]
        public static class CreatureManager_ChangeCreaturePos_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(CreatureModel caller, CreatureModel changed)
            {
                if (Plugin.EnableVanillaBugFixes == null || !Plugin.EnableVanillaBugFixes.Value ||
                    Plugin.FixWhiteNightRoomSwapSmallBird == null || !Plugin.FixWhiteNightRoomSwapSmallBird.Value)
                    return;

                try
                {
                    if (CreatureManager.instance == null) return;
                    var creatureList = CreatureManager.instance.GetCreatureList();
                    if (creatureList == null) return;

                    foreach (var c in creatureList)
                    {
                        if (c != null && c.script is SmallBird smallBird)
                        {
                            var unit = smallBird.Unit;
                            if (unit != null && unit.animTarget is SmallBirdAnim anim)
                            {
                                if (_smallBirdPerchObjectField != null && _smallBirdPerchPositionField != null)
                                {
                                    var perchObj = _smallBirdPerchObjectField.GetValue(anim) as GameObject;
                                    var perchPos = (Vector3)_smallBirdPerchPositionField.GetValue(anim);
                                    if (perchObj != null && unit.transform.parent != null)
                                    {
                                        perchObj.transform.position = unit.transform.parent.position + perchPos;
                                        TotalTriggeredFixes++;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception) { }
            }
        }
    }
}
