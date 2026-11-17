using Dalamud.Plugin;
using Dalamud.Plugin.Ipc.Exceptions;
using ECommons.DalamudServices;
using ECommons.EzIpcManager;
using ECommons.IPC.Subscribers;
using ECommons.IPC.Subscribers.AutoRetainer;
using ECommons.IPC.Subscribers.BossMod;
using ECommons.IPC.Subscribers.Gearsetter;
using ECommons.IPC.Subscribers.PandorasBox;
using ECommons.IPC.Subscribers.Vnavmesh;
using ECommons.IPC.Subscribers.YesAlready;
using ECommons.Reflection;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using WrathCombo.API;
using ApiConfigOption = WrathCombo.API.Enum.AutoRotationConfigOption;
using ApiDpsMode = WrathCombo.API.Enum.DPSRotationMode;
using ApiHealerMode = WrathCombo.API.Enum.HealerRotationMode;
using ApiSetResult = WrathCombo.API.Enum.SetResult;
#nullable disable

namespace AutoDuty.IPC
{
    using System.ComponentModel;
    using ECommons.GameFunctions;
    using Helpers;

    internal class IPCSubscriber_Common
    {
        /// <summary>清單裡有沒有同名且狀態為已載入的插件。任何執行緒可呼叫；清單快照新鮮時不配置記憶體。</summary>
        /// <remarks>
        /// 載入狀態每次即時讀，卸載一開始就回 false。已安裝清單最多每秒重抓一次，
        /// Dalamud 通知插件載入／卸載／更新時立即作廢 ⇒ 剛裝好或剛更新的插件最慢 1 秒看得到。
        /// </remarks>
        internal static bool IsReady(string pluginName)
        {
            if (CurrentInstalled().TryGetValue(pluginName, out var plugins))
            {
                foreach (var plugin in plugins)
                {
                    if (plugin.IsLoaded)
                        return true;
                }
            }
            return false;
        }

        private const long InstalledRefreshIntervalMs = 1000;

        /// <summary>依 InternalName 分組的已安裝清單。發布後不再改動，整份以一次參考指派換新。</summary>
        private sealed class InstalledSnapshot(Dictionary<string, IExposedPlugin[]> byName, long takenAt, int generation)
        {
            internal readonly Dictionary<string, IExposedPlugin[]> ByName = byName;
            internal readonly long TakenAt = takenAt;
            internal readonly int Generation = generation;
        }

        private static InstalledSnapshot _installed;
        private static int _installedGeneration;

        private static Dictionary<string, IExposedPlugin[]> CurrentInstalled()
        {
            // 世代要在讀清單之前取：讀到一半收到通知時，這份快照帶舊世代，下一次呼叫就會重抓。
            int generation = Volatile.Read(ref _installedGeneration);
            long now = Environment.TickCount64;
            var snapshot = Volatile.Read(ref _installed);
            if (snapshot != null && snapshot.Generation == generation && now - snapshot.TakenAt < InstalledRefreshIntervalMs)
                return snapshot.ByName;

            var byName = new Dictionary<string, IExposedPlugin[]>(snapshot?.ByName.Count ?? 0, StringComparer.Ordinal);
            try
            {
                foreach (var plugin in Svc.PluginInterface.InstalledPlugins)
                {
                    var name = plugin.InternalName;
                    if (name == null)
                        continue;
                    byName[name] = byName.TryGetValue(name, out var same) ? [.. same, plugin] : [plugin];
                }
            }
            catch (Exception ex)
            {
                byName.Clear();
                Svc.Log.Error($"讀取已安裝插件清單失敗，這一秒內把其他插件都當成沒載入：{ex}");
            }

            Volatile.Write(ref _installed, new InstalledSnapshot(byName, now, generation));
            return byName;
        }

        internal static void WatchInstalledPlugins() => Svc.PluginInterface.ActivePluginsChanged += OnActivePluginsChanged;

        internal static void StopWatchingInstalledPlugins() => Svc.PluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;

        // Dalamud 從載入插件的執行緒、持著插件清單鎖時呼叫：只作廢快照，不在這裡讀清單。
        private static void OnActivePluginsChanged(IActivePluginsChangedEventArgs args) => Interlocked.Increment(ref _installedGeneration);

        /// <summary>
        /// IPC 成員綁定檢查:<paramref name="member"/> 是 null 就代表 EzIPC 沒有把這個端點接上
        /// (委派型別不受支援、或參數超過 9 個),之後呼叫它會擲 NullReferenceException。
        /// <para>
        /// ⚠️ 這個檢查與「提供端有沒有安裝」無關:EzIPC 走的 GetIpcSubscriber 不看 InstalledPlugins,
        /// 沒裝那個外掛照樣綁得上(要到呼叫時才回 default)。所以這一行只會在真的接線失敗時出現,
        /// 不會對沒裝該外掛的使用者洗版。
        /// </para>
        /// <para>
        /// 📌 寫 Information:這是要使用者回報的診斷。
        /// </para>
        /// </summary>
        internal static void LogIfUnbound(Delegate member, string pluginName, string endpoint)
        {
            if (member != null)
                return;

            Svc.Log.Information($"[IPC 綁定檢查] {pluginName} 的「{endpoint}」綁定失敗:" +
                                "EzIPC 沒有指派這個成員,呼叫它會擲 NullReferenceException。" +
                                "請連同這一行回報給開發者。");
        }

        internal static Version Version(string pluginName) => DalamudReflector.TryGetDalamudPlugin(pluginName, out var dalamudPlugin, false, true) ? dalamudPlugin.GetType().Assembly.GetName().Version : new Version(0, 0, 0, 0);

        /// <summary>同一個端點的故障訊息重印間隔（毫秒）。</summary>
        private const long ProviderFaultLogIntervalMs = 10000;

        /// <summary>節流表上限，避免端點名意外發散時無限成長。</summary>
        private const int MaxTrackedFaultKeys = 64;

        private static readonly Dictionary<string, long> ProviderFaultLogTimes = new Dictionary<string, long>();

        /// <summary>
        /// 提供端的端點實作自己擲了例外（被 <c>Delegate.DynamicInvoke</c> 包成
        /// <see cref="System.Reflection.TargetInvocationException"/>）—— 寫一行給使用者看的說明。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 🔴 寫 <c>Information</c>：這是要使用者回報的診斷。
        /// </para>
        /// <para>
        /// 🔴 自帶節流字典而不是用 <c>EzThrottler</c>：後者是整個外掛共用的靜態 <c>Dictionary</c>
        /// 且零同步，並行插入弄壞的是整張表 —— 連帶弄壞外掛裡所有模組的節流。
        /// 鎖內只碰字典，不寫 log、不做 I/O、不呼叫別的外掛。
        /// </para>
        /// </remarks>
        internal static void ReportProviderFault(string pluginName, string endpoint, Exception ex)
        {
            if (!ShouldLogProviderFault(endpoint))
                return;

            Exception inner = ex.InnerException ?? ex;
            Svc.Log.Information($"[IPC] {pluginName} 的「{endpoint}」端點自己擲了例外，這一次當成「沒有開始」處理：{inner.GetType().Name}: {inner.Message}。這通常代表對方那一側現在做不到這件事（例如導航網格還沒載入）；持續出現請連同這一行回報。");
        }

        private static bool ShouldLogProviderFault(string key)
        {
            long now = Environment.TickCount64;
            lock (ProviderFaultLogTimes)
            {
                if (ProviderFaultLogTimes.TryGetValue(key, out long last) && now - last < ProviderFaultLogIntervalMs)
                    return false;
                if (ProviderFaultLogTimes.Count >= MaxTrackedFaultKeys && !ProviderFaultLogTimes.ContainsKey(key))
                    ProviderFaultLogTimes.Clear();
                ProviderFaultLogTimes[key] = now;
                return true;
            }
        }

        internal static void DisposeAll(EzIPCDisposalToken[] _disposalTokens)
        {
            foreach (var token in _disposalTokens)
            {
                try
                {
                    token.Dispose();
                }
                catch (Exception ex)
                {
                    Svc.Log.Error($"Error while unregistering IPC: {ex}");
                }
            }
        }

        /// <summary>
        /// 逐一拆掉所有 <c>*_IPCSubscriber</c>。每一步各自 try/catch —— 其中一支擲例外
        /// 不可以讓後面幾支拆不掉。
        /// <para>
        /// 🔴 必須在 <c>ECommonsMain.Dispose()</c> <b>之前</b>呼叫：拆除要用到 Svc 的服務，
        /// ECommons 收掉之後就沒有東西可以 Unregister 了。
        /// </para>
        /// <para>
        /// 📌 現況下多數 <c>Dispose()</c> 是空操作，這不是漏寫：這些類的 IPC 成員全是
        /// <b>訂閱端欄位</b>，而 <c>EzIPC.Init</c> 只對提供端與 <c>[EzIPCEvent]</c> 產生
        /// disposal token（訂閱端欄位不產生）⇒ 它們的 <c>_disposalTokens</c> 是空陣列。
        /// 真的有動作的是 <c>Wrath_IPCSubscriber</c>（交回 Wrath Combo 的租約），而那一支
        /// 在 <c>StopAndResetALL()</c> 裡已經先放過一次，這裡是冪等的第二次。
        /// </para>
        /// <para>
        /// 所以這條鏈<b>現在不改變任何行為</b>，是為了以後往這些類加提供端或事件時
        /// 不會靜默漏拆。
        /// </para>
        /// </summary>
        internal static void DisposeAllSubscribers()
        {
            SafeDispose(nameof(AutoRetainer_IPCSubscriber), AutoRetainer_IPCSubscriber.Dispose);
            SafeDispose(nameof(AM_IPCSubscriber), AM_IPCSubscriber.Dispose);
            SafeDispose(nameof(DiscardHelper_IPCSubscriber), DiscardHelper_IPCSubscriber.Dispose);
            SafeDispose(nameof(BossModReborn_IPCSubscriber), BossModReborn_IPCSubscriber.Dispose);
            SafeDispose(nameof(BossMod_IPCSubscriber), BossMod_IPCSubscriber.Dispose);
            SafeDispose(nameof(YesAlready_IPCSubscriber), YesAlready_IPCSubscriber.Dispose);
            SafeDispose(nameof(Gearsetter_IPCSubscriber), Gearsetter_IPCSubscriber.Dispose);
            SafeDispose(nameof(VNavmesh_IPCSubscriber), VNavmesh_IPCSubscriber.Dispose);
            SafeDispose(nameof(PandorasBox_IPCSubscriber), PandorasBox_IPCSubscriber.Dispose);
            SafeDispose(nameof(Wrath_IPCSubscriber), Wrath_IPCSubscriber.Dispose);
            SafeDispose(nameof(StopWatchingInstalledPlugins), StopWatchingInstalledPlugins);
        }

        private static void SafeDispose(string name, Action dispose)
        {
            try
            {
                dispose();
            }
            catch (Exception ex)
            {
                Svc.Log.Error($"Error while disposing {name}: {ex}");
            }
        }
    }
}
