using ECommons.DalamudServices;
using System;

namespace AutoDuty.Helpers;

/// <summary>
/// 觀察 <see cref="AutoDuty.TaskManager"/> 的任務逾時，讓「逾時」這件事在 log 與 UI 上都看得見。
/// </summary>
/// <remarks>
/// <para>
/// 背景:AutoDuty 的 TaskManager 建立時是 <c>AbortOnTimeout = false</c> —— 任務逾時<b>不會</b>讓
/// <c>Stage</c> 變成 <c>Stopped</c>,它只是放行、直接往下一步跑。所以「自動化自己停下來就通知」
/// 那條路徑對逾時完全不會響:王戰整段被跳過、寶箱沒撿、人還在副本裡就走下一步,而使用者看不到
/// 任何訊息。這一支補的就是那個缺口。
/// </para>
/// <para>
/// 🔴 <b>為什麼用「從外面觀察」而不是改 ECommons</b>:<c>ECommons/</c> 是全艦隊 20+ 個消費端共用的
/// 子模組,不能為了 AutoDuty 一個外掛在裡面加事件。LegacyTaskManager 的 <c>Tick</c> 是 private、
/// 建構子就自己掛上 <c>Svc.Framework.Update</c>,沒有可覆寫的縫,所以只能從公開狀態
/// (<c>CurrentTaskName</c> / <c>AbortAt</c> / <c>NumQueuedTasks</c>)推。
/// </para>
/// <para>
/// <b>推法為什麼成立</b>:ECommons 的 <c>Tick</c> 一個 tick 只做一件事 —— <c>CurrentTask</c> 是
/// null 就「取出下一個」(並改寫 <c>AbortAt</c>),不是 null 就「跑它」。取出與完成<b>永遠不會在同一個
/// tick 發生</b>,所以任兩個任務之間必定隔著至少一個 <c>CurrentTask == null</c> 的 tick。而
/// <c>AutoDuty.Framework_Update</c> 是在 <c>TaskManager</c> 之<b>後</b>才掛上
/// <c>Svc.Framework.Update</c>(AutoDuty.cs 建構子:先 <c>TaskManager = new()</c>,後
/// <c>Svc.Framework.Update += Framework_Update</c>),多播委派照訂閱順序呼叫 ⇒ 我們每一幀看到的
/// 都是 ECommons 跑完之後的狀態,那個 null 的空檔一定看得到。
/// </para>
/// <para>
/// ⚠️ <b>已知的不精確,兩個方向都寫在這裡</b>:
/// <list type="number">
/// <item>
/// <b>可能多算一次(單幀競態)</b>:ECommons 是先判 <c>result == true</c> 才判逾時,所以一個任務在
/// 「跨過期限之後的第一次評估」剛好回 true 的話,它算成功、不算逾時,而我們只看得到「期限已過 + 任務
/// 不見了」⇒ 會多算一次。這個窗口正好是一幀寬,而且要求輪詢剛好落在期限後的那一幀才成功。
/// </item>
/// <item>
/// <b>可能少算(不具名任務)</b>:<c>Enqueue</c> 的 <c>name</c> 預設是 <c>null</c>,
/// <c>CurrentTaskName</c> 因此可能在任務正在跑的時候就回 null。這種任務我們靠
/// <c>AbortAt</c> 改寫或 <c>NumQueuedTasks == 0</c> 才收得到尾,晚一幀結算;結算不到就漏掉。
/// </item>
/// </list>
/// ⇒ <b>權威記錄是 log</b>(ECommons 自己那則 Warning 帶任務名),這裡的數字是給使用者掃一眼用的。
/// </para>
/// <para>
/// 📌 執行緒:只在 <c>Svc.Framework.Update</c>(framework 執行緒)上被呼叫,所以這裡的欄位不需要同步。
/// 節流用的是自己的 <see cref="Environment.TickCount64"/> 比較,<b>刻意不用</b>
/// <c>ECommons.Throttlers.EzThrottler</c> —— 那是整個外掛共用的靜態字典、沒有任何同步。
/// </para>
/// </remarks>
internal static class TaskTimeoutWatcher
{
    /// <summary>本次執行(從 <c>Run</c> 那一刻起算)累計的任務逾時次數。</summary>
    internal static int RunCount { get; private set; }

    /// <summary>這次遊戲工作階段(外掛載入以來)累計的任務逾時次數。</summary>
    internal static int SessionCount { get; private set; }

    /// <summary>最後一次逾時的任務名稱。ECommons 允許不具名任務 ⇒ 這裡可能是 null。</summary>
    internal static string? LastTaskName { get; private set; }

    /// <summary>最後一次逾時當下的 <see cref="Environment.TickCount64"/>;從沒逾時過就是 0。</summary>
    internal static long LastTimeoutTick { get; private set; }

    // 目前被觀察的那個任務。_armedAbortAt 是它的期限(TaskManager.AbortAt 的快照)。
    private static bool    _armed;
    private static long    _armedAbortAt;
    private static string? _armedName;

    // 自帶節流:逾時可能連續發生,log 不能被洗版。被壓下的次數會併進下一則訊息,不會憑空消失。
    private const  int  LogThrottleMs = 5000;
    private static long _lastLoggedTick;
    private static int  _suppressedSinceLastLog;

    /// <summary>開始一輪新的執行:把「本次執行」的計數歸零。工作階段總數刻意<b>不</b>歸零。</summary>
    internal static void OnRunStarted()
    {
        RunCount = 0;
    }

    /// <summary>每一幀從 <c>AutoDuty.Framework_Update</c> 呼叫一次。</summary>
    internal static void Tick()
    {
        // 刻意用 var:LegacyTaskManager.TaskManager 這個型別名只在 AutoDuty.cs 裡有 using 別名,
        // 這裡寫出型別名就得再引一次,沒有必要。
        var tm = Plugin?.TaskManager;
        if (tm == null)
        {
            // 還沒建好或已經拆掉:丟掉觀察狀態,免得下次接上時拿舊期限去比。
            _armed = false;
            return;
        }

        string? name    = tm.CurrentTaskName;
        long    abortAt = tm.AbortAt;

        // 🔴 AbortAt 只在「取出一個任務成為 CurrentTask」那一刻被改寫,別的地方都不動它。
        //    所以它變了 ⇔ 這一幀剛取出一個新任務 ⇔ 上一個(如果有)已經結束。
        //    這是唯一可靠的「新任務開始了」信號:CurrentTaskName 對不具名任務會回 null,
        //    NumQueuedTasks 在「兩個任務中間的空檔」照樣是正數,兩者都不能拿來當開關。
        bool dequeued = abortAt != _armedAbortAt;

        if (_armed)
        {
            // 被觀察的那個任務結束了嗎?三個都是「已結束」的充分條件:
            //  (a) 剛取出下一個任務 ⇒ 上一個必定已經結束;
            //  (b) 佇列整個空了(NumQueuedTasks == 0 蘊含 CurrentTask == null);
            //  (c) 我們觀察的是具名任務,而現在讀不到具名的當前任務。
            bool ended = dequeued
                         || tm.NumQueuedTasks == 0
                         || (_armedName != null && name == null);

            if (ended)
            {
                _armed = false;

                // 期限已經過去才算逾時。正常完成的任務走到這裡一定還沒到期。
                if (Environment.TickCount64 > _armedAbortAt)
                {
                    RunCount++;
                    SessionCount++;
                    LastTaskName    = _armedName;
                    LastTimeoutTick = Environment.TickCount64;
                    ReportThrottled(_armedName);
                }
            }
        }

        // ⚠️ 只在「剛取出新任務」時開始觀察。
        //    用 NumQueuedTasks > 0 當條件是錯的:在「兩個任務中間、CurrentTask 已經是 null」的那個
        //    空檔它照樣是正數,於是會用「舊的」AbortAt 再武裝一次,下一幀取出新任務時又拿同一個
        //    已經過期的期限結算 ⇒ 同一次逾時被算兩次。
        if (dequeued)
        {
            _armed        = true;
            _armedAbortAt = abortAt;
            _armedName    = name;
        }
    }

    /// <summary>
    /// 寫一則帶 AutoDuty 上下文的 Warning。
    /// </summary>
    /// <remarks>
    /// ECommons 自己那則(<c>Task X took too long to execute</c> + 堆疊)只有任務名,沒有
    /// 「當時 AutoDuty 在做什麼」。事後對照實機 log 時缺的正是後者,所以這裡補上 Stage/動作/副本。
    /// 🔴 用 <c>Svc.Log</c> 不用 <c>ECommons.Logging.DuoLog</c> —— <c>DuoLog</c> 在<b>每一個</b>等級都
    /// 無條件 <c>Svc.Chat.Print</c> 到使用者的聊天視窗,沒有等級閘門。
    /// </remarks>
    private static void ReportThrottled(string? taskName)
    {
        long now = Environment.TickCount64;
        if (_lastLoggedTick != 0 && now - _lastLoggedTick < LogThrottleMs)
        {
            _suppressedSinceLastLog++;
            return;
        }

        _lastLoggedTick = now;

        string suppressed = _suppressedSinceLastLog > 0
                                ? $"(另有 {_suppressedSinceLastLog} 次被節流沒印)"
                                : "";
        _suppressedSinceLastLog = 0;

        // 名字可能真的是 null(不具名任務)。畫成空字串會讓人以為是別的問題,明講「不具名」。
        string shownName = taskName ?? "<不具名任務>";

        Svc.Log.Warning($"[TaskTimeout] 任務「{shownName}」超過時限沒完成{suppressed}。"
                        + $"AbortOnTimeout=false ⇒ 自動化不會停,會直接跳到下一步。"
                        + $"本次執行第 {RunCount} 次、本工作階段第 {SessionCount} 次。"
                        + $"Stage={Plugin?.Stage} 動作={Plugin?.Action} 副本={Plugin?.CurrentTerritoryContent?.Name}");
    }
}
