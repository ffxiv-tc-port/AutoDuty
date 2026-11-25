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
    }
}
