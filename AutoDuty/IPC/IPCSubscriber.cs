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

// ─────────────────────────────────────────────────────────────────────────────
// 這一層是**門面**：外部呼叫點看到的名字、參數與回傳型別與遷移前逐字相同，
// 底下的委派管線換成 ECommons.IPC 套件（以及 Wrath 的 WrathCombo.API）。
//
// 🔴 wrapper 一律用「明確傳入建構式」而不是 IPCBase.DefaultWrapper：
//    套件的 ECommonsIPC.X 是 lazy 單例，wrapper 在第一次存取當下就烘死，而我們這裡
//    有兩種語意並存（BossMod／Wrath 是 AnyException，其餘是 IPCException）。
//    自己 new 一份並把 wrapper 當建構式參數傳進去，就不必靠初始化順序，
//    也不會因為別處先碰了 ECommonsIPC.X 而被烘成別人的 wrapper。
//
// 套件給不了的成員在 IPCSubscriberSidecar.cs，分類理由寫在那個檔的檔頭。
// ─────────────────────────────────────────────────────────────────────────────

namespace AutoDuty.IPC
{
    using System.ComponentModel;
    using ECommons.GameFunctions;
    using Helpers;

    internal static class AutoRetainer_IPCSubscriber
    {
        private static readonly AutoRetainerIPC Pkg = new(SafeWrapper.IPCException);

        internal static bool IsEnabled => IPCSubscriber_Common.IsReady("AutoRetainer");

        internal static bool IsBusy() => Pkg.IsBusy();
        internal static bool AreAnyRetainersAvailableForCurrentChara() => Pkg.AreAnyRetainersAvailableForCurrentChara();
        internal static void AbortAllTasks() => Pkg.AbortAllTasks();
        internal static void EnqueueGCInitiation() => Pkg.EnqueueInitiation();

        /// <summary>
        /// 推「排程器開著沒」用的三個讀值；讀不到給 null（不用 SafeWrapper：它把「沒有端點」吞成 false，會被當成「關著」）。
        /// 型別與提供端、套件宣告相同（皆 <c>Func&lt;bool&gt;</c>）。只在框架執行緒呼叫。
        /// </summary>
        internal static void ReadSchedulerInputs(out bool? anyActive, out bool? taskBusy, out bool? multiModeEnabled)
        {
            anyActive        = TryInvokeBool("AutoRetainer.IsBusy");
            taskBusy         = TryInvokeBool("AutoRetainer.PluginState.IsBusy");
            multiModeEnabled = TryInvokeBool("AutoRetainer.PluginState.GetMultiModeStatus");
        }

        private static bool? TryInvokeBool(string endpoint)
        {
            try
            {
                var gate = Svc.PluginInterface.GetIpcSubscriber<bool>(endpoint);
                return gate.HasFunction ? gate.InvokeFunc() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static void Dispose() { }
    }

    /// <summary>套件沒有 AutoBot 的訂閱類，整類不遷。</summary>
    internal static class AM_IPCSubscriber
    {
        private static EzIPCDisposalToken[] _disposalTokens = EzIPC.Init(typeof(AM_IPCSubscriber), "AutoBot", SafeWrapper.IPCException);

        internal static bool IsEnabled => IPCSubscriber_Common.IsReady("AutoBot");

        [EzIPC] internal static readonly Action Start;
        [EzIPC] internal static readonly Action Stop;
        [EzIPC] internal static readonly Func<bool> IsRunning;

        internal static void Dispose() => IPCSubscriber_Common.DisposeAll(_disposalTokens);
    }

    /// <summary>套件沒有 ARDiscard 的訂閱類，整類不遷。</summary>
    internal static class DiscardHelper_IPCSubscriber
    {
        private static EzIPCDisposalToken[] _disposalTokens = EzIPC.Init(typeof(DiscardHelper_IPCSubscriber), "ARDiscard", SafeWrapper.AnyException);

        internal static bool IsEnabled => IPCSubscriber_Common.IsReady("ARDiscard");

        [EzIPC("IsRunning", true)] internal static readonly Func<bool> IsRunning;

        internal static void Dispose() => IPCSubscriber_Common.DisposeAll(_disposalTokens);
    }

    /// <summary>
    /// 套件的 BossModIPC 沒有 <c>AI.*</c> 這一組端點（那是 BossModReborn 專有的），整類不遷。
    /// </summary>
    internal static class BossModReborn_IPCSubscriber
    {
        private static EzIPCDisposalToken[] _disposalTokens = EzIPC.Init(typeof(BossModReborn_IPCSubscriber), "BossMod", SafeWrapper.AnyException);

        internal static bool IsEnabled => IPCSubscriber_Common.IsReady("BossModReborn");

        [EzIPC("AI.GetPreset", true)] internal static readonly Func<string> Presets_GetActive;

        [EzIPC("AI.SetPreset", true)] internal static readonly Action<string> Presets_SetActive;

        // 執行期覆寫,不寫使用者的 BMR 設定;舊版 BMR 沒有這組端點,SafeWrapper 吞掉後回 false。
        [EzIPC("Configuration.SetTransientString", true)] internal static readonly Func<string, string, string, bool> SetTransientString;

        [EzIPC("Configuration.ClearTransient", true)] internal static readonly Func<string, string, bool> ClearTransient;

        // 舊版 BMR 沒有這兩支,SafeWrapper 吞掉後回 false ⇒ 視同 AI 原本就關著、不記也不還原。
        [EzIPC("AI.IsEnabled", true)] internal static readonly Func<bool> AI_IsEnabled;

        [EzIPC("AI.SetEnabled", true)] internal static readonly Func<bool, bool> AI_SetEnabled;

        // 首領模組標記的限時必殺目標（InstanceID，優先度由高到低）；舊版 BMR 沒有這支，SafeWrapper 吞掉後回 null。
        [EzIPC("Hints.UrgentKillTargets", true)] internal static readonly Func<ulong[]> UrgentKillTargets;

        internal static void Dispose() => IPCSubscriber_Common.DisposeAll(_disposalTokens);
    }

    internal static class Gearsetter_IPCSubscriber
    {
        private static readonly GearsetterIPC Pkg = new(SafeWrapper.IPCException);

        internal static bool IsEnabled => IPCSubscriber_Common.IsReady("Gearsetter");

        internal static List<(uint ItemId, InventoryType? SourceInventory, int? SourceInventorySlot, RaptureGearsetModule.GearsetItemIndex TargetSlot)> GetRecommendationsForGearset(byte gearset) =>
            Pkg.GetRecommendationsForGearset(gearset);

        internal static void Dispose() { }
    }

    internal static class PandorasBox_IPCSubscriber
    {
        private static readonly PandorasBoxIPC Pkg = new(SafeWrapper.IPCException);

        internal static bool IsEnabled => IPCSubscriber_Common.IsReady("PandorasBox");

        internal static void PauseFeature(string feature, int ms)        => Pkg.PauseFeature(feature, ms);
        internal static void SetFeatureEnabled(string feature, bool on)  => Pkg.SetFeatureEnabled(feature, on);

        /// <summary>側車：套件回傳 <c>bool?</c>，我方是 <c>bool</c>。</summary>
        internal static bool GetFeatureEnabled(string feature) => PandorasBoxExtraIPC.GetFeatureEnabled(feature);

        /// <summary>側車：套件第三個參數是 <c>bool?</c>，我方是 <c>bool</c>。</summary>
        internal static void SetConfigEnabled(string feature, string config, bool on) => PandorasBoxExtraIPC.SetConfigEnabled(feature, config, on);

        internal static void Dispose() => PandorasBoxExtraIPC.Dispose();
    }
}
