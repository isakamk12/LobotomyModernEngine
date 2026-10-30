using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace LobotomyModernEngine.Core
{
    /// <summary>
    /// ワーカーからメインスレッドへの安全なコールバック返却機構
    /// Unity API はメインスレッドからしか呼べないため、
    /// ワーカーで計算した結果の適用はこのディスパッチャーを経由する。
    /// フレームバジェット（最大消費時間）を監視し、スパイクを抑制する。
    /// </summary>
    public class MainThreadDispatcher : MonoBehaviour
    {
        private struct DispatchItem
        {
            public Action Action;
            public int Generation;
        }

        private static MainThreadDispatcher _instance;
        private static readonly object QueueLock = new object();
        private static readonly Queue<DispatchItem> ActionQueue = new Queue<DispatchItem>(128);

        public static int MainThreadId { get; private set; }
        public static long TotalDispatchedCount = 0;
        public static long DiscardedOldGenCount = 0;
        public static int QueueDepth => ActionQueue.Count;

        /// <summary>
        /// 1フレームあたりの最大消費時間（ミリ秒）
        /// </summary>
        public static float FrameBudgetMs = 2.0f;

        public static void Initialize(GameObject host)
        {
            if (_instance == null)
            {
                MainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                _instance = host.AddComponent<MainThreadDispatcher>();
            }
        }

        /// <summary>
        /// メインスレッドで実行するアクションをキューイング
        /// </summary>
        public static void Enqueue(Action action, int generation)
        {
            if (action == null) return;

            lock (QueueLock)
            {
                // キューが異常に肥大化した場合は古い低世代ジョブを破棄
                if (ActionQueue.Count > 1024)
                {
                    DiscardedOldGenCount++;
                    return;
                }

                ActionQueue.Enqueue(new DispatchItem
                {
                    Action = action,
                    Generation = generation
                });
            }
        }

        private void Update()
        {
            if (ActionQueue.Count == 0) return;

            var stopwatch = Stopwatch.StartNew();
            int currentGen = GenerationTracker.CurrentGeneration;

            while (true)
            {
                DispatchItem item;
                lock (QueueLock)
                {
                    if (ActionQueue.Count == 0) break;
                    item = ActionQueue.Dequeue();
                }

                // 世代チェック: シーン遷移等で破棄された古いジョブはスキップ
                if (item.Generation != currentGen)
                {
                    DiscardedOldGenCount++;
                    continue;
                }

                try
                {
                    item.Action();
                    TotalDispatchedCount++;
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError(string.Format("[LME Dispatcher] Callback error: {0}", ex.Message));
                }

                // フレームバジェット超過チェック（メインスレッドのフレーム落ちを防止）
                if (stopwatch.ElapsedMilliseconds >= FrameBudgetMs)
                {
                    break;
                }
            }
        }

        public static void Clear()
        {
            lock (QueueLock)
            {
                ActionQueue.Clear();
            }
        }
    }
}
