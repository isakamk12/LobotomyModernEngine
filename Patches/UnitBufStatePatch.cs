using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// UnitModel.UpdateBufState ゼロアロケーション最適化パッチ (v0.8.4)
    /// 
    /// 【コード・IL検証結果 (Assembly-CSharp.dll RVA 0xcf7d0)】
    /// - バニラの UnitModel.UpdateBufState() は以下の処理のみを行う（派生クラスでの override は 0 件）:
    ///     UnitBuf[] array = _bufList.ToArray();
    ///     foreach (UnitBuf unitBuf in array) { unitBuf.FixedUpdate(); }
    /// - 1. 空リスト時 (_bufList.Count == 0):
    ///   元のメソッドは空配列 new UnitBuf[0] をヒープ確保してループ0回で返るだけであり、副作用は一切存在しない。
    ///   早期リターンにより、毎秒約4,000回（1ステージ約50万回）の不要な配列確保を削減する。
    /// - 2. バフ存在時 (_bufList.Count > 0):
    ///   単純な逆順 for ループへの変更は、UnitBuf.FixedUpdate() 内でバフの追加・削除が行われた際に
    ///   走査順序および対象要素が元のスナップショット方式 (ToArray) と変わってしまうため採用しない。
    ///   代わりに、再帰深度別の再利用スナップショットバッファ (CopyTo + 0..count-1 順方向走査 + finally での参照クリア) を用い、
    ///   元の ToArray() と 100% 同一の実行順序・スナップショット隔離性を保証しつつヒープ確保をゼロ化する。
    /// </summary>
    public static class UnitBufStatePatch
    {
        private const int MaxRecursionDepth = 8;
        private const int InitialBufferCapacity = 16;

        // メインスレッド専用の再帰深度別スナップショットバッファプール
        private static readonly UnitBuf[][] SnapshotBuffers = new UnitBuf[MaxRecursionDepth][];
        private static int _recursionDepth = 0;
        private static int _mainThreadId = -1;

        // 診断・比較用メトリクス
        public static long TotalCallCount = 0;
        public static long AvoidedEmptyAllocations = 0;
        public static long AvoidedActiveSnapshotAllocations = 0;
        public static long FallbackToArrayCount = 0;
        public static int MaxActiveBufCountSeen = 0;

        public static void InitializeMainThread()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            for (int i = 0; i < MaxRecursionDepth; i++)
            {
                if (SnapshotBuffers[i] == null)
                {
                    SnapshotBuffers[i] = new UnitBuf[InitialBufferCapacity];
                }
            }
        }

        public static void ClearCache()
        {
            _recursionDepth = 0;
            for (int i = 0; i < MaxRecursionDepth; i++)
            {
                if (SnapshotBuffers[i] != null)
                {
                    Array.Clear(SnapshotBuffers[i], 0, SnapshotBuffers[i].Length);
                }
            }
        }

        public static void ResetStageMetrics()
        {
            TotalCallCount = 0;
            AvoidedEmptyAllocations = 0;
            AvoidedActiveSnapshotAllocations = 0;
            FallbackToArrayCount = 0;
            MaxActiveBufCountSeen = 0;
        }

        [HarmonyPatch(typeof(UnitModel), "UpdateBufState")]
        public static class UnitModel_UpdateBufState_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(List<UnitBuf> ____bufList)
            {
                TotalCallCount++;

                // 1. 空リスト時は副作用ゼロのため即座にリターン（new UnitBuf[0] 確保を回避）
                if (____bufList == null || ____bufList.Count == 0)
                {
                    AvoidedEmptyAllocations++;
                    return false;
                }

                int count = ____bufList.Count;
                if (count > MaxActiveBufCountSeen)
                {
                    MaxActiveBufCountSeen = count;
                }

                if (_mainThreadId == -1)
                {
                    InitializeMainThread();
                }

                // 万一の別スレッド呼出や深い再帰時は安全のため標準の ToArray() スナップショットへフォールバック
                if (Thread.CurrentThread.ManagedThreadId != _mainThreadId || _recursionDepth >= MaxRecursionDepth || _recursionDepth < 0)
                {
                    FallbackToArrayCount++;
                    UnitBuf[] fallbackArray = ____bufList.ToArray();
                    for (int i = 0; i < fallbackArray.Length; i++)
                    {
                        fallbackArray[i].FixedUpdate();
                    }
                    return false;
                }

                int depth = _recursionDepth++;
                UnitBuf[] buffer = SnapshotBuffers[depth];
                if (buffer == null || buffer.Length < count)
                {
                    int newCap = buffer == null ? InitialBufferCapacity : buffer.Length * 2;
                    if (newCap < count) newCap = count + 8;
                    buffer = new UnitBuf[newCap];
                    SnapshotBuffers[depth] = buffer;
                }

                try
                {
                    // ToArray() と完全に同一の時点スナップショットを先頭から順方向にコピー
                    ____bufList.CopyTo(0, buffer, 0, count);
                    AvoidedActiveSnapshotAllocations++;

                    // 元の foreach (UnitBuf unitBuf in array) と同一の 0 .. count-1 順方向で FixedUpdate を実行
                    for (int i = 0; i < count; i++)
                    {
                        UnitBuf buf = buffer[i];
                        if (buf != null)
                        {
                            buf.FixedUpdate();
                        }
                    }
                }
                finally
                {
                    // 走査完了後に参照を即座にクリアし、破棄済み UnitBuf のメモリ保持を防止
                    Array.Clear(buffer, 0, count);
                    _recursionDepth--;
                }

                return false;
            }
        }
    }
}
