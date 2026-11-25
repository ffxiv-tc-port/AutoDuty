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

        internal enum MovementHold { Hold, NotHoldStep, NotWaiting, CapReached, Dead, LowHp, Fulfilled }

        /// <summary>
        /// 站定等待（路徑步驟 holdPosition）這一幀要不要請 BMR 暫停自動移動與閃避。
        /// 只在 Wait 計時已起算、未結束、起算後 <paramref name="maxHoldMs"/> 內才要；死亡、血量比低於 <paramref name="minHpRatio"/>、離開條件已成立都不要。
        /// </summary>
        /// <param name="waitEndTick">Wait 計時的結束時刻(TickCount64);0＝沒在計時。</param>
        internal static MovementHold DecideMovementHold(bool holdStepInAction, long nowMs, long waitEndTick, int waitDurationMs, int maxHoldMs,
                                                        bool playerAlive, uint curHp, uint maxHp, float minHpRatio, bool untilFulfilled)
        {
            if (!holdStepInAction)
                return MovementHold.NotHoldStep;
            if (waitEndTick == 0 || nowMs >= waitEndTick)
                return MovementHold.NotWaiting;
            if (nowMs - (waitEndTick - waitDurationMs) >= maxHoldMs)
                return MovementHold.CapReached;
            if (!playerAlive || curHp == 0)
                return MovementHold.Dead;
            // maxHp 為 0 時讀不到血量＝當作不夠（照常閃）。
            if (maxHp == 0 || (float)curHp / maxHp < minHpRatio)
                return MovementHold.LowHp;
            return untilFulfilled ? MovementHold.Fulfilled : MovementHold.Hold;
        }
    }
}
