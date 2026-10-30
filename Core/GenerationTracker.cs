using System.Threading;

namespace LobotomyModernEngine.Core
{
    /// <summary>
    /// シーン遷移やリセットを跨ぐ非同期タスクの無効化を保証する世代管理トラッカー
    /// シーン読込時やゲームリセット時に世代番号をインクリメントし、
    /// 古い世代のタスクやコールバックを安全に破棄する。
    /// </summary>
    public static class GenerationTracker
    {
        private static int _currentGeneration = 0;

        /// <summary>
        /// 現在の世代番号
        /// </summary>
        public static int CurrentGeneration => _currentGeneration;

        /// <summary>
        /// 新しい世代へ進める（シーン遷移時などに呼出）
        /// </summary>
        public static int NextGeneration()
        {
            return Interlocked.Increment(ref _currentGeneration);
        }

        /// <summary>
        /// 指定された世代番号が現在有効か検証
        /// </summary>
        public static bool IsValid(int generation)
        {
            return generation == _currentGeneration;
        }
    }
}
