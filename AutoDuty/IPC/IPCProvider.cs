using AutoDuty.Helpers;
using ECommons.DalamudServices;
using ECommons.EzIpcManager;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
#nullable disable

namespace AutoDuty.IPC
{
    internal class IPCProvider
    {
        internal IPCProvider()
        {
            EzIPC.Init(this);
        }

        /// <summary>
        /// 從別的執行緒進來的端點工作,照先進先出排在這裡等 framework 執行緒來排乾。
        /// </summary>
        /// <remarks>
        /// 🔴 刻意<b>不</b>用 <c>Svc.Framework.RunOnFrameworkThread</c> 逐則排隊:本 pin 的
        /// <c>ThreadBoundTaskScheduler</c> 把待跑的工作放在 <c>ConcurrentDictionary</c> 裡、
        /// <c>Run()</c> 走訪的是 <c>Keys</c>(Dalamud/Utility/ThreadBoundTaskScheduler.cs)——
        /// <b>同一格內不保證先進先出</b>。而這幾個端點的順序是有語意的:呼叫端連著打
        /// <c>Stop()</c> 再 <c>Run()</c>,順序一倒過來就變成「先開始跑、再把它整個停掉」。
        /// </remarks>
        private static readonly ConcurrentQueue<(string Name, Action Action)> PendingWork = new();

        /// <summary>
        /// 把端點的實際工作放到 framework 執行緒上跑。
        /// </summary>
        /// <remarks>
        /// 🔴 IPC 端點跑在<b>呼叫端的執行緒</b>上。<see cref="Run"/>／<see cref="Start"/>／
        /// <see cref="Stop"/> 這三支是「開始/停止跑副本」的指令型端點,底下會走到:
        /// <c>Player.Available</c>／<c>Player.Object.ClassJob</c>(<c>IObjectTable</c> 的包裝是
        /// 每格重用、Address 就地改寫的,從別的執行緒讀等於對隨時可能被換掉的原生指標解參考)、
        /// <c>TaskManager</c> 的 Enqueue/Abort(framework 執行緒同時在走訪那兩個裸 List)、
        /// 路徑檔的讀取、對 vnavmesh/BossMod/PandorasBox 的 IPC、以及 ImGui 視窗的開關。
        /// 這些沒有一項可以在別人的執行緒上做,而 AccessViolation 在 .NET Core 是
        /// corrupted-state exception,try/catch 攔不到。
        /// <br/><br/>
        /// 已經在 framework 執行緒時<b>就地執行</b>:例外照樣往呼叫端擲,回傳值與時序與改動前
        /// 完全相同(Questionable 從自己的 framework tick 打進來的呼叫走這條)。
        /// 在別的執行緒時排進佇列,下一次 Framework_Update 開頭排乾且<b>不等待</b> ——
        /// 這三支回傳型別都是 <c>void</c>,「不等待」不改變任何回傳語意;不等待也避免了
        /// 「呼叫端持著鎖同步等 framework 執行緒」這種死結形狀。
        /// </remarks>
        private static void RunOnFramework(string endpointName, Action action)
        {
            if (Svc.Framework.IsInFrameworkUpdateThread)
            {
                action();
                return;
            }
            PendingWork.Enqueue((endpointName, action));
        }

        /// <summary>
        /// 由 <c>AutoDuty.Framework_Update</c> 每幀在最前面呼叫一次,把 <see cref="PendingWork"/> 排乾。
        /// 每一則各自包 try:其中一則擲例外不會讓後面的排不出去,也不會中斷這一格的其餘處理。
        /// </summary>
        internal static void DrainPendingWork()
        {
            while (PendingWork.TryDequeue(out var work))
            {
                try
                {
                    work.Action();
                }
                catch (Exception e)
                {
                    Svc.Log.Error($"AutoDuty IPC {work.Name} 在 framework 執行緒上執行失敗:{e}");
                }
            }
        }

        [EzIPC] public void ListConfig() => ConfigHelper.ListConfig();
        [EzIPC] public string GetConfig(string config) => ConfigHelper.GetConfig(config);
        [EzIPC] public void SetConfig (string config, string setting) => ConfigHelper.ModifyConfig(config, setting);

        /// <summary>
        /// 暫時覆寫一組設定:只改執行期的值,存檔時寫回使用者原本的值,<see cref="PopConfigOverrides"/>
        /// 或 AutoDuty 停止時還原。任何一項驗不過就整批不套用並回 <c>false</c>。
        /// </summary>
        /// <remarks>
        /// ⚠️ 這支跑在<b>呼叫端的執行緒</b>上。CallGate 對型別不同的參數會做一次 JSON 來回轉換,
        /// 所以 <c>Dictionary&lt;string, string&gt;</c> 到這裡可能已經變成 <c>JObject</c> —— 兩種都收。
        /// </remarks>
        [EzIPC]
        public bool PushConfigOverrides(object overrides)
        {
            Dictionary<string, string> dict;
            try
            {
                dict = overrides switch
                       {
                           Dictionary<string, string> d => d,
                           JObject jo                   => jo.ToObject<Dictionary<string, string>>(),
                           _                            => null
                       };
            }
            catch (Exception ex)
            {
                Svc.Log.Error($"AutoDuty 設定覆寫:參數轉不成 Dictionary<string, string>:{ex.Message}");
                return false;
            }

            if (dict == null)
            {
                Svc.Log.Error($"AutoDuty 設定覆寫:參數要是 Dictionary<string, string>,收到的是 {overrides?.GetType().FullName ?? "null"}。");
                return false;
            }

            return ConfigOverrideHelper.Push(dict);
        }

        /// <summary>還原所有設定覆寫。AutoDuty 停止時本來就會自己做一次,呼叫端不一定要用。</summary>
        [EzIPC] public bool PopConfigOverrides() => ConfigOverrideHelper.Pop();

        [EzIPC]
        public void Run(uint territoryType, int loops = 0, bool bareMode = false)
        {
            RunOnFramework(nameof(Run), () =>
            {
                var ctx = Plugin.BuildCommandRunContext(territoryType, loops, startFromZero: true, bareMode: bareMode, source: RunSource.IPC, persistLoopsToConfig: true);
                if (ctx != null)
                    Plugin.Run(ctx);
                else
                    Plugin.Run(territoryType, loops, startFromZero: true, bareMode: bareMode);
            });
        }
        [EzIPC] public void Start(bool startFromZero = true) => RunOnFramework(nameof(Start), () => Plugin.StartNavigation(startFromZero));
        [EzIPC] public void Stop() => RunOnFramework(nameof(Stop), () => Plugin.Stage = Stage.Stopped);
        [EzIPC] public bool IsNavigating() => Plugin.States.HasFlag(PluginState.Navigating);
        [EzIPC] public bool IsLooping() => Plugin.States.HasFlag(PluginState.Looping);
        [EzIPC] public bool IsStopped() => Plugin.Stage == Stage.Stopped;
        [EzIPC] public bool ContentHasPath(uint territoryType) => ContentPathsManager.DictionaryPaths.ContainsKey(territoryType);

        //Callback for Wrath Combo Lease Cancel
        [EzIPC] public void WrathComboCallback(int reason, string s) => Wrath_IPCSubscriber.CancelActions(reason, s);
    }
}
