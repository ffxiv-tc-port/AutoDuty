namespace AutoDuty.Helpers
{
    /// <summary>
    /// 路徑推進的純判斷（不碰遊戲狀態、不寫 log），呼叫端負責取值與動作；離線探針直接測這裡。
    /// </summary>
    internal static class StepPolicies
    {
        /// <summary>
        /// 離開條件第 <paramref name="failures"/> 次不成立之後，是否該放棄重做、停下求助。
        /// <paramref name="maxRedo"/> 為 0（含負值）＝不限，永遠重做。
        /// </summary>
        internal static bool ShouldGiveUpRedo(int failures, int maxRedo) => maxRedo > 0 && failures > maxRedo;

        /// <summary>卡住處理第 4 階可不可以跳過這一步。首領步驟不跳：跳過＝副本打不完，後面整段路線都白走。</summary>
        internal static bool StuckSkipAllowed(string? stepName) =>
            !string.Equals(stepName, "Boss", System.StringComparison.OrdinalIgnoreCase);

        /// <summary>路線跑到底時，是否該當成「卡住要人接手」而不是「全部跑完」：這一場自動跳過步驟、而副本沒完成。</summary>
        internal static bool PathEndNeedsHelp(int autoSkippedSteps, bool dutyComplete) => autoSkippedSteps > 0 && !dutyComplete;

        /// <summary>
        /// 跳過計時中的 Wait 時要不要把已等時間寫回路徑檔。只有使用者「單獨」跳過才寫：
        /// 前一次或下一次跳步落在 <paramref name="quietMs"/> 內＝快轉／連續跳步，寫回會把機關等待砍到不夠用。
        /// </summary>
        /// <param name="previousSkipAgoMs">距上一次跳步的毫秒數;-1＝沒有上一次。</param>
        /// <param name="nextSkipWithinMs">之後多久又跳步;-1＝還沒看到下一次。</param>
        internal static bool ShouldWriteBackWait(bool userInitiated, long previousSkipAgoMs, long nextSkipWithinMs, int quietMs) =>
            userInitiated &&
            !(previousSkipAgoMs >= 0 && previousSkipAgoMs < quietMs) &&
            !(nextSkipWithinMs >= 0 && nextSkipWithinMs < quietMs);
    }
}
