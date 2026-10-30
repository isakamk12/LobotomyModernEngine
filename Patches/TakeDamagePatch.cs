using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// WorkerModel.TakeDamage バリア配列アロケーション最適化パッチ
    /// バニラでは攻撃を受けるたびに Rwbp 各属性判定で _barrierBufList.ToArray() を呼び出す。
    /// バリアが付与されていない通常時（全被弾の95%以上）でも空配列 new BarrierBuf[0] を新規生成するため
    /// 戦闘時の GC スパムの大きな要因となっていた。
    /// 本パッチではバリア0件時のアロケーションを完全ゼロ化（静的空配列返却）し、
    /// バリア所持時のみ配列を生成する。
    /// </summary>
    public static class TakeDamagePatch
    {
        // 静的空配列（Mono 2.0 / .NET 3.5 互換）
        private static readonly BarrierBuf[] EmptyBarriers = new BarrierBuf[0];

        // メトリクス
        public static long TotalTakeDamageCount = 0;
        public static int TakeDamagePerSec = 0;
        public static long ZeroBarrierAllocAvoided = 0;
        public static long ActiveBarrierCount = 0;
        public static int MaxBarrierSeen = 0;

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

            // バリアが存在する場合のみ配列化（安全なスナップショット）
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
        /// TakeDamage(UnitModel, DamageInfo) 内の _barrierBufList.ToArray() を GetBarrierSnapshot に置換
        /// </summary>
        [HarmonyPatch(typeof(WorkerModel), "TakeDamage", new Type[] { typeof(UnitModel), typeof(DamageInfo) })]
        public static class TakeDamage_Transpiler
        {
            private static readonly MethodInfo TargetToArrayMethod = AccessTools.Method(typeof(List<BarrierBuf>), "ToArray");
            private static readonly MethodInfo ReplacementMethod = AccessTools.Method(typeof(TakeDamagePatch), nameof(GetBarrierSnapshot));

            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                int replacedCount = 0;
                foreach (var instruction in instructions)
                {
                    if (instruction.opcode == OpCodes.Callvirt && instruction.operand is MethodInfo mi && mi == TargetToArrayMethod)
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = ReplacementMethod;
                        replacedCount++;
                    }
                    yield return instruction;
                }

                Plugin.Logger.LogInfo(string.Format("[LME Transpiler] Hooked TakeDamage: Replaced {0} BarrierBuf.ToArray calls with zero-alloc snapshot.", replacedCount));
            }
        }
    }
}
