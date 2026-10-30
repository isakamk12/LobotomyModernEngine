using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace LobotomyModernEngine.Core
{
    /// <summary>
    /// フレーム分散スケジューラー (FrameBudgetScheduler)
    /// 計画書 Phase 3 準拠: 重要度の低い更新処理やチェック処理を
    /// 毎フレーム一斉に呼ぶのではなく、フレームごとの予算時間（例: 1.0ms）内で
    /// ラウンドロビン分散実行し、スパイク（コマ落ち）を防止する。
    /// </summary>
    public class FrameBudgetScheduler : MonoBehaviour
    {
        public interface IThrottledTask
        {
            void Execute();
            bool IsActive { get; }
        }

        private static FrameBudgetScheduler _instance;
        private static readonly List<IThrottledTask> TaskList = new List<IThrottledTask>(64);
        private static int _cursorIndex = 0;

        /// <summary>
        /// 1フレームあたり分散処理に使える最大予算時間（ミリ秒）
        /// </summary>
        public static float BudgetMs = 1.0f;

        public static int RegisteredTaskCount => TaskList.Count;
        public static long TotalTasksExecuted = 0;

        public static void Initialize(GameObject host)
        {
            if (_instance == null)
            {
                _instance = host.AddComponent<FrameBudgetScheduler>();
            }
        }

        public static void Register(IThrottledTask task)
        {
            if (task == null) return;
            lock (TaskList)
            {
                if (!TaskList.Contains(task))
                {
                    TaskList.Add(task);
                }
            }
        }

        public static void Unregister(IThrottledTask task)
        {
            if (task == null) return;
            lock (TaskList)
            {
                TaskList.Remove(task);
            }
        }

        private void Update()
        {
            if (TaskList.Count == 0) return;

            var stopwatch = Stopwatch.StartNew();
            int count = TaskList.Count;
            int visited = 0;

            while (visited < count)
            {
                IThrottledTask task;
                lock (TaskList)
                {
                    if (TaskList.Count == 0) break;
                    if (_cursorIndex >= TaskList.Count) _cursorIndex = 0;
                    task = TaskList[_cursorIndex];
                    _cursorIndex = (_cursorIndex + 1) % TaskList.Count;
                }

                visited++;

                if (task != null && task.IsActive)
                {
                    try
                    {
                        task.Execute();
                        TotalTasksExecuted++;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Logger.LogError(string.Format("[LME Scheduler] Throttled task error: {0}", ex.Message));
                    }
                }

                // フレーム予算チェック
                if (stopwatch.ElapsedMilliseconds >= BudgetMs)
                {
                    break;
                }
            }
        }

        public static void Clear()
        {
            lock (TaskList)
            {
                TaskList.Clear();
                _cursorIndex = 0;
            }
        }
    }
}
