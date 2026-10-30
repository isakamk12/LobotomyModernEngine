using System.Collections.Generic;
using HarmonyLib;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// v0.5.0: テスト・レベリング時間を短縮するためのデバッグモード用初期装備＆懺悔(Penitence)強化パッチ。
    /// 対象:
    /// - 初期武器「警棒」(ID: 1)
    /// - 初期防具「スーツ」(ID: 22)
    /// - たった一つの罪と何百もの善 E.G.O 武器「懺悔」(script == "OneBadManyGoodWeapon", ID: 200009)
    /// - たった一つの罪と何百もの善 E.G.O 防具「懺悔」(script == "OneBadManyGoodArmor", ID: 300009)
    /// 
    /// 元のバニラ数値は完全バックアップし、F9 オーバーレイまたは F8 キーでゲーム中いつでも即座に ON/OFF（バニラ数値へ復元）可能。
    /// </summary>
    public static class DebugGearBuffPatch
    {
        private class OriginalGearState
        {
            public int Id;
            public EquipmentTypeInfo.EquipmentType Type;
            public string Grade;
            public int MaxNum;
            public float Range;
            public float AttackSpeed;
            public List<EgoRequire> Requires;
            public DamageInfo[] DamageInfos;
            public DefenseInfo Defense;
        }

        private static readonly Dictionary<int, OriginalGearState> _backups = new Dictionary<int, OriginalGearState>();
        public static bool IsCurrentlyApplied { get; private set; }
        public static int BuffedEquipmentCount { get; private set; }

        [HarmonyPatch(typeof(EquipmentDataLoader), "Load")]
        public static class EquipmentDataLoader_Load_Patch
        {
            public static void Postfix()
            {
                CaptureBackupsIfNeeded();
                SyncWithConfig();
            }
        }

        private static bool IsTargetGear(EquipmentTypeInfo info)
        {
            if (info == null) return false;
            // ID 1: 警棒 (Riot Stick), ID 22: スーツ (Standard Suit)
            // ID 200009: 懺悔 E.G.O 武器 (Penitence Weapon), ID 300009: 懺悔 E.G.O 防具 (Penitence Armor)
            return info.id == 1 || info.id == 22 || info.id == 200009 || info.id == 300009;
        }

        private static void CaptureBackupsIfNeeded()
        {
            if (EquipmentTypeList.instance == null || !EquipmentTypeList.instance.loaded)
                return;

            List<EquipmentTypeInfo> allData = EquipmentTypeList.instance.GetAllData();
            for (int i = 0; i < allData.Count; i++)
            {
                EquipmentTypeInfo info = allData[i];
                if (!IsTargetGear(info) || _backups.ContainsKey(info.id))
                    continue;

                OriginalGearState state = new OriginalGearState
                {
                    Id = info.id,
                    Type = info.type,
                    Grade = info.grade,
                    MaxNum = info.maxNum,
                    Range = info.range,
                    AttackSpeed = info.attackSpeed,
                    Requires = new List<EgoRequire>(info.requires)
                };

                if (info.damageInfos != null)
                {
                    state.DamageInfos = new DamageInfo[info.damageInfos.Length];
                    for (int d = 0; d < info.damageInfos.Length; d++)
                    {
                        state.DamageInfos[d] = info.damageInfos[d].Copy();
                    }
                }

                if (info.defenseInfo != null)
                {
                    state.Defense = info.defenseInfo.Copy();
                }

                _backups[info.id] = state;
            }
        }

        public static void SyncWithConfig()
        {
            if (Plugin.EnableDebugStarterGearBuff != null && Plugin.EnableDebugStarterGearBuff.Value)
            {
                ApplyBuff();
            }
            else
            {
                RestoreVanilla();
            }
        }

        public static void ToggleBuff()
        {
            if (Plugin.EnableDebugStarterGearBuff == null) return;
            Plugin.EnableDebugStarterGearBuff.Value = !Plugin.EnableDebugStarterGearBuff.Value;
            SyncWithConfig();
        }

        public static void ApplyBuff()
        {
            if (EquipmentTypeList.instance == null || !EquipmentTypeList.instance.loaded)
                return;

            CaptureBackupsIfNeeded();

            int count = 0;
            List<string> itemSummary = new List<string>();
            foreach (KeyValuePair<int, OriginalGearState> kvp in _backups)
            {
                EquipmentTypeInfo info = EquipmentTypeList.instance.GetData(kvp.Key);
                if (info == null) continue;

                // Lv1職員でも即装備できるように装着条件をクリアし、Gradeは5(ALEPH相当のランク補正)へ引き上げ
                info.requires.Clear();
                info.grade = "5";
                if (info.maxNum < 5)
                {
                    info.maxNum = 5;
                }

                if (info.type == EquipmentTypeInfo.EquipmentType.WEAPON)
                {
                    // 警棒(ID:1) および 懺悔武器(OneBadManyGoodWeapon): 高火力・高速・中距離化で鎮圧テストを高速化
                    if (info.damageInfos != null)
                    {
                        for (int i = 0; i < info.damageInfos.Length; i++)
                        {
                            info.damageInfos[i].min = 45f;
                            info.damageInfos[i].max = 65f;
                        }
                    }
                    if (info.range < 6f)
                    {
                        info.range = 6f;
                    }
                    info.attackSpeed = 2.0f;
                    count++;
                    itemSummary.Add(string.Format("Weapon#{0}({1})", info.id, string.IsNullOrEmpty(info.script) ? "Default" : info.script));
                }
                else if (info.type == EquipmentTypeInfo.EquipmentType.ARMOR)
                {
                    // スーツ(ID:22) および 懺悔防具(OneBadManyGoodArmor): 全属性 0.1倍 (90%軽減) で作業・鎮圧中の即死やパニックを防止
                    if (info.defenseInfo != null)
                    {
                        info.defenseInfo.R = 0.1f;
                        info.defenseInfo.W = 0.1f;
                        info.defenseInfo.B = 0.1f;
                        info.defenseInfo.P = 0.1f;
                    }
                    count++;
                    itemSummary.Add(string.Format("Armor#{0}({1})", info.id, string.IsNullOrEmpty(info.script) ? "Default" : info.script));
                }
            }

            BuffedEquipmentCount = count;
            IsCurrentlyApplied = count > 0;
            if (Plugin.Logger != null)
            {
                Plugin.Logger.LogInfo(string.Format("[LME DebugGear] Applied starter gear buff to {0} items: {1}", count, string.Join(", ", itemSummary.ToArray())));
            }
        }

        public static void RestoreVanilla()
        {
            if (EquipmentTypeList.instance == null || !EquipmentTypeList.instance.loaded)
                return;

            int restored = 0;
            foreach (KeyValuePair<int, OriginalGearState> kvp in _backups)
            {
                EquipmentTypeInfo info = EquipmentTypeList.instance.GetData(kvp.Key);
                if (info == null) continue;

                OriginalGearState orig = kvp.Value;
                info.grade = orig.Grade;
                info.maxNum = orig.MaxNum;
                info.range = orig.Range;
                info.attackSpeed = orig.AttackSpeed;
                info.requires = new List<EgoRequire>(orig.Requires);

                if (orig.DamageInfos != null && info.damageInfos != null && orig.DamageInfos.Length == info.damageInfos.Length)
                {
                    for (int i = 0; i < orig.DamageInfos.Length; i++)
                    {
                        info.damageInfos[i].min = orig.DamageInfos[i].min;
                        info.damageInfos[i].max = orig.DamageInfos[i].max;
                        info.damageInfos[i].type = orig.DamageInfos[i].type;
                    }
                }

                if (orig.Defense != null && info.defenseInfo != null)
                {
                    info.defenseInfo.R = orig.Defense.R;
                    info.defenseInfo.W = orig.Defense.W;
                    info.defenseInfo.B = orig.Defense.B;
                    info.defenseInfo.P = orig.Defense.P;
                }
                restored++;
            }

            IsCurrentlyApplied = false;
            if (Plugin.Logger != null && restored > 0)
            {
                Plugin.Logger.LogInfo(string.Format("[LME DebugGear] Restored vanilla stats for {0} starter gear items.", restored));
            }
        }
    }
}
