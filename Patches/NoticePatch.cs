using System;
using System.Collections.Generic;
using HarmonyLib;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// Notice.Send Copy-on-Write 最適化パッチ
    /// バニラでは Send 呼出しのたびに noticeList[key].ToArray() で新規ヒープ配列を生成し、
    /// GC (Gen0) 発生の最大要因となっていた。
    /// 本パッチでは Observe/Remove 変更時のみスナップショットを再構築 (Copy-on-Write) し、
    /// Send 時のアロケーションを完全にゼロにする。
    /// </summary>
    public static class NoticePatch
    {
        // 静的空配列（Mono 2.0 / .NET 3.5 互換）
        private static readonly IObserver[] EmptyObservers = new IObserver[0];

        // Copy-on-Write スナップショットキャッシュ
        private static readonly Dictionary<string, IObserver[]> SnapshotCache = new Dictionary<string, IObserver[]>();

        // 計測・診断用カウンター
        public static long TotalSendCount = 0;
        public static int SendPerSec = 0;
        public static long AvoidedAllocations = 0;
        public static int MaxObserverCount = 0;
        public static int ActiveTopicCount => SnapshotCache.Count;

        /// <summary>
        /// キャッシュのクリア（シーン遷移・タイトル復帰時に呼出）
        /// </summary>
        public static void ClearCache()
        {
            lock (SnapshotCache)
            {
                SnapshotCache.Clear();
            }
        }

        /// <summary>
        /// Observe(string, IObserver) 呼出し後に当該トピックのスナップショットを無効化
        /// </summary>
        [HarmonyPatch(typeof(Notice), "Observe", new Type[] { typeof(string), typeof(IObserver) })]
        public static class Notice_Observe_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(string notice)
            {
                if (notice == null) return;
                lock (SnapshotCache)
                {
                    SnapshotCache.Remove(notice);
                }
            }
        }

        /// <summary>
        /// Remove(string, IObserver) 呼出し後に当該トピックのスナップショットを無効化
        /// </summary>
        [HarmonyPatch(typeof(Notice), "Remove", new Type[] { typeof(string), typeof(IObserver) })]
        public static class Notice_Remove_Observer_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(string notice)
            {
                if (notice == null) return;
                lock (SnapshotCache)
                {
                    SnapshotCache.Remove(notice);
                }
            }
        }

        /// <summary>
        /// Remove(string, int) 呼出し後に当該トピックのスナップショットを無効化
        /// </summary>
        [HarmonyPatch(typeof(Notice), "Remove", new Type[] { typeof(string), typeof(int) })]
        public static class Notice_Remove_Id_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(string notice)
            {
                if (notice == null) return;
                lock (SnapshotCache)
                {
                    SnapshotCache.Remove(notice);
                }
            }
        }

        /// <summary>
        /// Notice.Send を Prefix でインターセプト
        /// スナップショット配列から直接通知し、バニラの ToArray() をスキップ (return false)
        /// </summary>
        [HarmonyPatch(typeof(Notice), "Send", new Type[] { typeof(string), typeof(object[]) })]
        public static class Notice_Send_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(string notice, object[] param, Dictionary<string, List<IObserver>> ___noticeList)
            {
                TotalSendCount++;
                SendPerSec++;

                if (string.IsNullOrEmpty(notice) || ___noticeList == null)
                {
                    return false; // スキップ
                }

                IObserver[] observers = null;

                // スナップショットの取得または構築
                lock (SnapshotCache)
                {
                    if (!SnapshotCache.TryGetValue(notice, out observers))
                    {
                        if (___noticeList.TryGetValue(notice, out var list) && list != null && list.Count > 0)
                        {
                            observers = list.ToArray();
                            if (observers.Length > MaxObserverCount)
                            {
                                MaxObserverCount = observers.Length;
                            }
                        }
                        else
                        {
                            observers = EmptyObservers;
                        }

                        SnapshotCache[notice] = observers;
                    }
                    else
                    {
                        // キャッシュから読めた＝バニラの ToArray() を1回回避できた
                        AvoidedAllocations++;
                    }
                }

                if (observers == null || observers.Length == 0)
                {
                    return false;
                }

                // スナップショットを順次呼出
                for (int i = 0; i < observers.Length; i++)
                {
                    var obs = observers[i];
                    if (obs != null)
                    {
                        try
                        {
                            obs.OnNotice(notice, param);
                        }
                        catch (Exception ex)
                        {
                            // 個別のオブザーバーで例外が発生しても他の通知を巻き込まず、ログに記録
                            Plugin.Logger.LogError(string.Format("[LME Notice] Exception in observer {0} for notice '{1}': {2}",
                                obs.GetType().FullName, notice, ex.Message));
                        }
                    }
                }

                return false; // バニラの ToArray() 処理を完全にバイパス
            }
        }
    }
}
