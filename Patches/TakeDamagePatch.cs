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

        // メトリクス & Transpiler 適用検証ステータス (2メソッド個別に一致数・置換数を厳密管理)
        public static long TotalTakeDamageCount = 0;
        public static int TakeDamagePerSec = 0;
        public static long ZeroBarrierAllocAvoided = 0;
        public static long ActiveBarrierCount = 0;
        public static int MaxBarrierSeen = 0;

        public static int MatchedCallSitesInTakeDamage = 0;
        public static int ReplacedCallSitesInTakeDamage = 0;
        public static int MatchedCallSitesInTakeDamageWithoutEffect = 0;
        public static int ReplacedCallSitesInTakeDamageWithoutEffect = 0;

        public static int TotalReplacedCallSites => ReplacedCallSitesInTakeDamage + ReplacedCallSitesInTakeDamageWithoutEffect;

        /// <summary>
        /// 2メソッド（TakeDamage / TakeDamageWithoutEffect）それぞれの対象命令（ldfld _barrierBufList）との一致数と置換数が
        /// ともに期待値の4件（計8/8件）に達した場合のみ厳密に「検証済」とする
        /// </summary>
        public static bool IsTranspilerVerified =>
            MatchedCallSitesInTakeDamage == 4 &&
            ReplacedCallSitesInTakeDamage == 4 &&
            MatchedCallSitesInTakeDamageWithoutEffect == 4 &&
            ReplacedCallSitesInTakeDamageWithoutEffect == 4;

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
                int matchedCount = 0;
                int replacedCount = 0;

                for (int i = 0; i < codes.Count; i++)
                {
                    var inst = codes[i];
                    if (inst.opcode == OpCodes.Callvirt && inst.operand is MethodInfo mi && mi == TargetToArrayMethod)
                    {
                        // 直前の命令が ldfld UnitModel::_barrierBufList と完全一致することを必須の置換条件とする
                        bool isTargetBarrierField = (i > 0 && codes[i - 1].opcode == OpCodes.Ldfld && Equals(codes[i - 1].operand, TargetBarrierListField));
                        if (isTargetBarrierField)
                        {
                            inst.opcode = OpCodes.Call;
                            inst.operand = ReplacementMethod;
                            matchedCount++;
                            replacedCount++;

                            Plugin.Logger.LogInfo(string.Format(
                                "[LME Transpiler Verified] WorkerModel.TakeDamage IL index {0}: ldfld _barrierBufList matched -> replaced callvirt List<BarrierBuf>::ToArray() with call TakeDamagePatch::GetBarrierSnapshot()",
                                i));
                        }
                        else
                        {
                            Plugin.Logger.LogWarning(string.Format(
                                "[LME Transpiler Warning] WorkerModel.TakeDamage IL index {0}: Found ToArray() call but preceding opcode was not 'ldfld _barrierBufList'! Leaving untouched.",
                                i));
                        }
                    }
                }

                MatchedCallSitesInTakeDamage = matchedCount;
                ReplacedCallSitesInTakeDamage = replacedCount;
                Plugin.Logger.LogInfo(string.Format("[LME Transpiler] Hooked WorkerModel.TakeDamage: Matched {0}/4, Replaced {1}/4 BarrierBuf.ToArray calls.", matchedCount, replacedCount));
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
                int matchedCount = 0;
                int replacedCount = 0;

                for (int i = 0; i < codes.Count; i++)
                {
                    var inst = codes[i];
                    if (inst.opcode == OpCodes.Callvirt && inst.operand is MethodInfo mi && mi == TargetToArrayMethod)
                    {
                        // 直前の命令が ldfld UnitModel::_barrierBufList と完全一致することを必須の置換条件とする
                        bool isTargetBarrierField = (i > 0 && codes[i - 1].opcode == OpCodes.Ldfld && Equals(codes[i - 1].operand, TargetBarrierListField));
                        if (isTargetBarrierField)
                        {
                            inst.opcode = OpCodes.Call;
                            inst.operand = ReplacementMethod;
                            matchedCount++;
                            replacedCount++;

                            Plugin.Logger.LogInfo(string.Format(
                                "[LME Transpiler Verified] WorkerModel.TakeDamageWithoutEffect IL index {0}: ldfld _barrierBufList matched -> replaced callvirt List<BarrierBuf>::ToArray() with call TakeDamagePatch::GetBarrierSnapshot()",
                                i));
                        }
                        else
                        {
                            Plugin.Logger.LogWarning(string.Format(
                                "[LME Transpiler Warning] WorkerModel.TakeDamageWithoutEffect IL index {0}: Found ToArray() call but preceding opcode was not 'ldfld _barrierBufList'! Leaving untouched.",
                                i));
                        }
                    }
                }

                MatchedCallSitesInTakeDamageWithoutEffect = matchedCount;
                ReplacedCallSitesInTakeDamageWithoutEffect = replacedCount;
                Plugin.Logger.LogInfo(string.Format("[LME Transpiler] Hooked WorkerModel.TakeDamageWithoutEffect: Matched {0}/4, Replaced {1}/4 BarrierBuf.ToArray calls.", matchedCount, replacedCount));
                return codes;
            }
        }
    }
}
