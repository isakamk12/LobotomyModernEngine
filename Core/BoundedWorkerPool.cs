using System;
using System.Collections.Generic;
using System.Threading;

namespace LobotomyModernEngine.Core
{
    /// <summary>
    /// .NET 3.5 / Mono 2.0 互換の有界ワーカースレッドプール
    /// ThreadPool の乱用や Task / async / await に依存せず、
    /// 固定数の長寿命スレッドとロック付き有界キューで非同期計算を実行する。
    /// 初回タスク投入時に遅延起動（Lazy Start）し、待機中はポーリングなしでスリープする。
    /// </summary>
    public static class BoundedWorkerPool
    {
        private struct WorkItem
        {
            public Action Work;
            public int Generation;
            public string Name;
        }

        private static Thread[] _workers;
        private static readonly Queue<WorkItem> TaskQueue = new Queue<WorkItem>(128);
        private static readonly object QueueLock = new object();
        private static readonly AutoResetEvent WorkEvent = new AutoResetEvent(false);
        private static volatile bool _initialized = false;
        private static volatile bool _threadsStarted = false;
        private static volatile bool _running = false;

        public static int ConfiguredThreadCount { get; private set; }
        public static int ActiveThreadCount => _threadsStarted ? ConfiguredThreadCount : 0;
        public static int WorkerThreadCount => ConfiguredThreadCount;
        public static long TotalTasksExecuted = 0;
        public static long TotalTasksDropped = 0;
        public static int CurrentQueueDepth => TaskQueue.Count;

        /// <summary>
        /// ワーカープールの設定初期化（スレッド生成は初回Submitまで遅延）
        /// </summary>
        public static void Initialize(int maxThreads)
        {
            if (_initialized) return;

            int cpuCount = Environment.ProcessorCount;
            int threadCount = Math.Min(Math.Max(cpuCount - 2, 1), Math.Min(maxThreads, 4));
            ConfiguredThreadCount = threadCount;
            _initialized = true;
            _running = true;

            Plugin.Logger.LogInfo(string.Format("[LME WorkerPool] Configured for up to {0} lazy worker thread(s) (CPU: {1}).", threadCount, cpuCount));
        }

        private static void EnsureThreadsStarted()
        {
            if (_threadsStarted || !_running) return;

            lock (QueueLock)
            {
                if (_threadsStarted || !_running) return;

                _workers = new Thread[ConfiguredThreadCount];
                for (int i = 0; i < ConfiguredThreadCount; i++)
                {
                    int workerIndex = i;
                    _workers[i] = new Thread(() => WorkerLoop(workerIndex))
                    {
                        Name = string.Format("LME-Worker-{0}", workerIndex),
                        IsBackground = true,
                        Priority = ThreadPriority.BelowNormal
                    };
                    _workers[i].Start();
                }

                _threadsStarted = true;
                Plugin.Logger.LogInfo(string.Format("[LME WorkerPool] Lazily started {0} worker thread(s).", ConfiguredThreadCount));
            }
        }

        /// <summary>
        /// 単純なバックグラウンドタスクの投入
        /// </summary>
        public static bool Submit(Action work, string taskName = null)
        {
            return Submit(work, GenerationTracker.CurrentGeneration, taskName);
        }

        /// <summary>
        /// 世代番号付きタスクの投入
        /// </summary>
        public static bool Submit(Action work, int generation, string taskName = null)
        {
            if (!_running || work == null) return false;

            EnsureThreadsStarted();

            lock (QueueLock)
            {
                if (TaskQueue.Count >= 256)
                {
                    TotalTasksDropped++;
                    Plugin.Logger.LogWarning(string.Format("[LME WorkerPool] Queue full ({0}), dropping task '{1}'", TaskQueue.Count, taskName ?? "Unknown"));
                    return false;
                }

                TaskQueue.Enqueue(new WorkItem
                {
                    Work = work,
                    Generation = generation,
                    Name = taskName
                });
            }

            WorkEvent.Set();
            return true;
        }

        /// <summary>
        /// ワーカーで計算し、結果をメインスレッドで受け取るタスクの投入
        /// </summary>
        public static bool SubmitWithCallback<T>(Func<T> computeFunc, Action<T> onCompleteOnMainThread, string taskName = null)
        {
            int gen = GenerationTracker.CurrentGeneration;

            return Submit(() =>
            {
                if (!GenerationTracker.IsValid(gen)) return;

                T result;
                try
                {
                    result = computeFunc();
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError(string.Format("[LME WorkerPool] Error in compute task '{0}': {1}", taskName ?? "Unknown", ex.Message));
                    return;
                }

                if (!GenerationTracker.IsValid(gen)) return;

                MainThreadDispatcher.Enqueue(() =>
                {
                    onCompleteOnMainThread(result);
                }, gen);
            }, gen, taskName);
        }

        private static void WorkerLoop(int workerIndex)
        {
            while (_running)
            {
                WorkItem item = default(WorkItem);
                bool hasWork = false;

                lock (QueueLock)
                {
                    if (TaskQueue.Count > 0)
                    {
                        item = TaskQueue.Dequeue();
                        hasWork = true;
                        if (TaskQueue.Count > 0)
                        {
                            // まだ残りタスクがあれば別スレッドも起こす
                            WorkEvent.Set();
                        }
                    }
                }

                if (hasWork)
                {
                    if (GenerationTracker.IsValid(item.Generation))
                    {
                        try
                        {
                            item.Work();
                            Interlocked.Increment(ref TotalTasksExecuted);
                        }
                        catch (Exception ex)
                        {
                            Plugin.Logger.LogError(string.Format("[LME WorkerPool] Uncaught exception in worker-{0} ('{1}'): {2}",
                                workerIndex, item.Name ?? "Unknown", ex.Message));
                        }
                    }
                    else
                    {
                        Interlocked.Increment(ref TotalTasksDropped);
                    }
                }
                else
                {
                    WorkEvent.WaitOne();
                }
            }
        }

        /// <summary>
        /// シャットダウン（ゲーム終了時）
        /// </summary>
        public static void Shutdown()
        {
            if (!_running) return;

            _running = false;

            if (_threadsStarted && _workers != null)
            {
                for (int i = 0; i < _workers.Length; i++)
                {
                    WorkEvent.Set();
                }

                for (int i = 0; i < _workers.Length; i++)
                {
                    if (_workers[i] != null && _workers[i].IsAlive)
                    {
                        WorkEvent.Set();
                        _workers[i].Join(200);
                    }
                }
            }

            lock (QueueLock)
            {
                TaskQueue.Clear();
            }

            Plugin.Logger.LogInfo("[LME WorkerPool] Shutdown complete.");
        }
    }
}
