using System;

namespace AutoDuty.Helpers;

/// <summary>等條件成立就往下走,最多等 windowMs,沒等到也照常往下走。</summary>
/// <remarks>
/// 時限到自己回 true,不走 TaskManager 逾時:那條路會寫 Warning 並被 <see cref="TaskTimeoutWatcher"/> 計成跳步,而這種等待沒等到才是常態。
/// 計時從第一次執行起算;每次呼叫回傳新的閉包,同一個不可重複排進佇列。
/// </remarks>
internal static class SoftWait
{
    internal static Func<bool?> Until(Func<bool> condition, int windowMs)
    {
        long? deadline = null;
        return () =>
        {
            deadline ??= Environment.TickCount64 + windowMs;
            return condition() || Environment.TickCount64 > deadline;
        };
    }
}
