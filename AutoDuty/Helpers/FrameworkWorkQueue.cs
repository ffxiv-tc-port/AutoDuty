using System;
using System.Collections.Concurrent;
using ECommons.DalamudServices;

namespace AutoDuty.Helpers
{
    /// <summary>
    /// 別的執行緒（IPC 端點、多開連線迴圈）要交給 framework 執行緒做的工作，全插件共用這一條先進先出佇列；
    /// 由 <c>AutoDuty.Framework_Update</c> 每幀在最前面排乾。不同來源之間也照排入的先後執行。
    /// </summary>
    /// <remarks>
    /// 🔴 刻意不用 <c>Svc.Framework.RunOnFrameworkThread</c>／<c>RunOnTick</c> 逐則排隊：本 pin 的
    /// <c>ThreadBoundTaskScheduler</c> 把待跑的工作放在 <c>ConcurrentDictionary</c>、走訪 <c>Keys</c>，同一格內不保證先進先出；
    /// 而 IPC 的 <c>Stop()</c> 再 <c>Run()</c>、多開的 PATH_STEPS 先於 STEP_START 都有順序語意。
    /// </remarks>
    internal static class FrameworkWorkQueue
    {
        private static readonly ConcurrentQueue<(string Name, Action Action, Action<string, Exception> OnError)> Pending = new();

        /// <summary>
        /// 已在 framework 執行緒＝就地執行，例外照樣往呼叫端擲；否則排進佇列、不等待。
        /// <paramref name="onError"/> 只在排乾時用：記下這一則的例外，不可再往外擲。
        /// </summary>
        internal static void Run(string name, Action action, Action<string, Exception> onError)
        {
            if (Svc.Framework.IsInFrameworkUpdateThread)
            {
                action();
                return;
            }
            Pending.Enqueue((name, action, onError));
        }

        /// <summary>每一則各自包 try：一則擲例外不擋後面，也不中斷這一格的其餘處理。</summary>
        internal static void Drain()
        {
            while (Pending.TryDequeue(out var work))
            {
                try
                {
                    work.Action();
                }
                catch (Exception e)
                {
                    work.OnError(work.Name, e);
                }
            }
        }
    }
}
