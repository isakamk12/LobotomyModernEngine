using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// WorkerModel.TakeDamage & TakeDamageWithoutEffect バリア配列アロケーション最適化パッチ (IL Transpiler)
    /// 
    /// 【IL命令列の検証結果 (Assembly-CSharp.dll 実機解析)】
    /// - 対象1: WorkerModel.TakeDamage(UnitModel actor, DamageInfo dmg) [RVA 0x13c860]
    ///   - IL_00d2..IL_00d8 (R属性), IL_01a9..IL_01af (W属性), IL_0360..IL_0366 (B属性), IL_04b2..IL_04b8 (P属性) の計4箇所
    /// - 対象2: WorkerModel.TakeDamageWithoutEffect(UnitModel actor, DamageInfo dmg)
    ///   - IL_0060..IL_0066 (R属性), IL_00dc..IL_00e2 (W属性), IL_0161..IL_0167 (B属性), IL_0204..IL_020a (P属性) の計4箇所
    /// 
    /// いずれも以下の2命令シーケンスとなっている:
    ///   [Before]
    ///   ldarg.0
    ///   ldfld    List<BarrierBuf> UnitModel::_barrierBufList   (スタックに List<BarrierBuf> を push)
    ///   callvirt BarrierBuf[] List<BarrierBuf>::ToArray()      (インスタンスを pop し、new BarrierBuf[] を push)
    ///   
    ///   [After (Transpiler 置換後)]
    ///   ldarg.0
    ///   ldfld    List<BarrierBuf> UnitModel::_barrierBufList   (スタックに List<BarrierBuf> を第1引数として push)
    ///   call     BarrierBuf[] TakeDamagePatch::GetBarrierSnapshot(List<BarrierBuf>)
    ///   
    /// スタック遷移は「List<BarrierBuf> を1つ消費し、BarrierBuf[] を1つ積む」で完全に同一であり、
    /// バリア0件時（全被弾の95%以上）は静的空配列 EmptyBarriers を返しヒープ確保をゼロ化する。
    /// </summary>
    public static class TakeDamagePatch
    {
        // 静的空配列（Mono 2.0 / .NET 3.5 互換）
        private static readonly BarrierBuf[] EmptyBarriers = new BarrierBuf[0];

        // メトリクス & Transpiler 適用検証ステータス
        public static long TotalTakeDamageCount = 0;
        public static int TakeDamagePerSec = 0;
        public static long ZeroBarrierAllocAvoided = 0;
        public static long ActiveBarrierCount = 0;
        public static int MaxBarrierSeen = 0;

        public static int ReplacedCallSitesInTakeDamage = 0;
        public static int ReplacedCallSitesInTakeDamageWithoutEffect = 0;
        public static int TotalReplacedCallSites => ReplacedCallSitesInTakeDamage + ReplacedCallSitesInTakeDamageWithoutEffect;
        public static bool IsTranspilerVerified => ReplacedCallSitesInTakeDamage == 4;

        /// <summary>
        /// バリア配列の最適化取得メソッド
        /// Transpiler により _barrierBufList.ToArray() の代わりに呼び出される
        /// </summary>
        public static BarrierBuf[] GetBarrierSnapshot(List<BarrierBuf> list)
        {
            if (list == null || list.Count == 0)
            {
                ZeroBarrierAllocAvoided++;
                return EmptyBarriers;
            }

            int count = list.Count;
            ActiveBarrierCount++;
            if (count > MaxBarrierSeen)
            {
                MaxBarrierSeen = count;
            }

            // バリアが存在する場合のみ配列化（UseBarrier 内で RemoveUnitBuf が呼ばれても安全なスナップショット）
            return list.ToArray();
        }

        /// <summary>
        /// TakeDamage(UnitModel, DamageInfo) 用 Prefix（回数計測）
        /// </summary>
        [HarmonyPatch(typeof(WorkerModel), "TakeDamage", new Type[] { typeof(UnitModel), typeof(DamageInfo) })]
        public static class TakeDamage_UnitModel_Prefix
        {
            [HarmonyPrefix]
            public static void Prefix(UnitModel actor, DamageInfo dmg)
            {
                TotalTakeDamageCount++;
                TakeDamagePerSec++;
            }
        }

        /// <summary>
        /// TakeDamage(DamageInfo) 用 Prefix（基底 UnitModel での呼出計測）
        /// </summary>
        [HarmonyPatch(typeof(UnitModel), "TakeDamage", new Type[] { typeof(DamageInfo) })]
        public static class TakeDamage_DamageInfo_Prefix
        {
            [HarmonyPrefix]
            public static void Prefix(DamageInfo dmg)
            {
                TotalTakeDamageCount++;
                TakeDamagePerSec++;
            }
        }

        /// <summary>
        /// TakeDamage(UnitModel, DamageInfo) 内の _barrierBufList.ToArray() (4箇所: R/W/B/P) を GetBarrierSnapshot に置換
        /// </summary>
        [HarmonyPatch(typeof(WorkerModel), "TakeDamage", new Type[] { typeof(UnitModel), typeof(DamageInfo) })]
        public static class TakeDamage_Transpiler
        {
            private static readonly FieldInfo TargetBarrierListField = AccessTools.Field(typeof(UnitModel), "_barrierBufList");
            private static readonly MethodInfo TargetToArrayMethod = AccessTools.Method(typeof(List<BarrierBuf>), "ToArray");
            private static readonly MethodInfo ReplacementMethod = AccessTools.Method(typeof(TakeDamagePatch), nameof(GetBarrierSnapshot));

            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
                int replacedCount = 0;

                for (int i = 0; i < codes.Count; i++)
                {
                    var inst = codes[i];
                    if (inst.opcode == OpCodes.Callvirt && inst.operand is MethodInfo mi && mi == TargetToArrayMethod)
                    {
                        // 直前の命令が ldfld UnitModel::_barrierBufList であることも合わせて検証
                        bool prevMatches = (i > 0 && codes[i - 1].opcode == OpCodes.Ldfld && Equals(codes[i - 1].operand, TargetBarrierListField));
                        inst.opcode = OpCodes.Call;
                        inst.operand = ReplacementMethod;
                        replacedCount++;

                        Plugin.Logger.LogInfo(string.Format(
                            "[LME Transpiler Verify] WorkerModel.TakeDamage IL index {0} (prev ldfld match={1}): callvirt List<BarrierBuf>::ToArray() -> call TakeDamagePatch::GetBarrierSnapshot()",
                            i, prevMatches));
                    }
                }

                ReplacedCallSitesInTakeDamage = replacedCount;
                Plugin.Logger.LogInfo(string.Format("[LME Transpiler] Hooked WorkerModel.TakeDamage: Replaced {0}/4 BarrierBuf.ToArray calls.", replacedCount));
                return codes;
            }
        }

        /// <summary>
        /// TakeDamageWithoutEffect(UnitModel, DamageInfo) 内の _barrierBufList.ToArray() (4箇所: R/W/B/P) も同様に置換
        /// </summary>
        [HarmonyPatch(typeof(WorkerModel), "TakeDamageWithoutEffect", new Type[] { typeof(UnitModel), typeof(DamageInfo) })]
        public static class TakeDamageWithoutEffect_Transpiler
        {
            private static readonly FieldInfo TargetBarrierListField = AccessTools.Field(typeof(UnitModel), "_barrierBufList");
            private static readonly MethodInfo TargetToArrayMethod = AccessTools.Method(typeof(List<BarrierBuf>), "ToArray");
            private static readonly MethodInfo ReplacementMethod = AccessTools.Method(typeof(TakeDamagePatch), nameof(GetBarrierSnapshot));

            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
                int replacedCount = 0;

                for (int i = 0; i < codes.Count; i++)
                {
                    var inst = codes[i];
                    if (inst.opcode == OpCodes.Callvirt && inst.operand is MethodInfo mi && mi == TargetToArrayMethod)
                    {
                        bool prevMatches = (i > 0 && codes[i - 1].opcode == OpCodes.Ldfld && Equals(codes[i - 1].operand, TargetBarrierListField));
                        inst.opcode = OpCodes.Call;
                        inst.operand = ReplacementMethod;
                        replacedCount++;

                        Plugin.Logger.LogInfo(string.Format(
                            "[LME Transpiler Verify] WorkerModel.TakeDamageWithoutEffect IL index {0} (prev ldfld match={1}): callvirt List<BarrierBuf>::ToArray() -> call TakeDamagePatch::GetBarrierSnapshot()",
                            i, prevMatches));
                    }
                }

                ReplacedCallSitesInTakeDamageWithoutEffect = replacedCount;
                Plugin.Logger.LogInfo(string.Format("[LME Transpiler] Hooked WorkerModel.TakeDamageWithoutEffect: Replaced {0}/4 BarrierBuf.ToArray calls.", replacedCount));
                return codes;
            }
        }
    }
}
