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
        internal static void DisableAllFunctions() => Pkg.DisableAllFunctions();
        internal static void EnableMultiMode() => Pkg.EnableMultiMode();
        internal static int GetInventoryFreeSlotCount() => Pkg.GetInventoryFreeSlotCount();
        internal static void EnqueueGCInitiation() => Pkg.EnqueueInitiation();

        /// <summary>側車：套件沒有這個端點。</summary>
        internal static Dictionary<ulong, HashSet<string>> GetEnabledRetainers() => AutoRetainerExtraIPC.GetEnabledRetainers();

        /// <summary>側車：套件的型別是 <c>Action&lt;bool, bool&gt;</c>，我方是 <c>Action&lt;Action&gt;</c>。</summary>
        internal static void EnqueueHET(Action onFailure) => AutoRetainerExtraIPC.EnqueueHET(onFailure);

        internal static void Dispose() => AutoRetainerExtraIPC.Dispose();
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

    /// <summary>套件沒有 Marketbuddy 的訂閱類，整類不遷。</summary>
    internal static class Marketbuddy_IPCSubscriber
    {
        private static EzIPCDisposalToken[] _disposalTokens = EzIPC.Init(typeof(Marketbuddy_IPCSubscriber), "Marketbuddy", SafeWrapper.IPCException);

        internal static bool IsEnabled => IPCSubscriber_Common.IsReady("Marketbuddy");

        [EzIPC] internal static readonly Func<string, bool> IsLocked;
        [EzIPC] internal static readonly Func<string, bool> Lock;
        [EzIPC] internal static readonly Func<string, bool> Unlock;

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

        internal static void Dispose() => IPCSubscriber_Common.DisposeAll(_disposalTokens);
    }


    internal static class BossMod_IPCSubscriber
    {
        private static readonly BossModIPC Pkg = new(SafeWrapper.AnyException);

        internal static bool IsEnabled => IPCSubscriber_Common.IsReady("BossMod") || IPCSubscriber_Common.IsReady("BossModReborn");

        internal static bool HasModuleByDataId(uint dataId) => Pkg.HasModuleByDataId(dataId);
        internal static List<string> Configuration(IReadOnlyList<string> args, bool b) => Pkg.Configuration(args, b);
        internal static string Presets_Get(string name) => Pkg.Presets_Get(name);
        internal static bool Presets_Create(string preset, bool overwrite) => Pkg.Presets_Create(preset, overwrite);
        internal static bool Presets_Delete(string name) => Pkg.Presets_Delete(name);
        internal static string Presets_GetActive() => Pkg.Presets_GetActive();
        internal static bool Presets_SetActive(string name) => Pkg.Presets_SetActive(name);
        internal static bool Presets_ClearActive() => Pkg.Presets_ClearActive();
        internal static bool Presets_GetForceDisabled() => Pkg.Presets_GetForceDisabled();
        internal static bool Presets_SetForceDisabled() => Pkg.Presets_SetForceDisabled();

        /// <summary>🔴 側車：套件把它宣告成自訂 delegate，本版 ECommons 綁不上會停在 null。</summary>
        /// <remarks>string presetName, string moduleTypeName, string trackName, string value</remarks>
        internal static bool Presets_AddTransientStrategy(string presetName, string moduleTypeName, string trackName, string value) =>
            BossModExtraIPC.Presets_AddTransientStrategy(presetName, moduleTypeName, trackName, value);

        internal static void Dispose() => BossModExtraIPC.Dispose();

        public static void AddPreset(string name, string preset)
        {
            if (Presets_Get(name) == null)
                Svc.Log.Debug($"BossMod Adding Preset: {name} {Presets_Create(preset, true)}");
        }

        public static void RefreshPreset(string name, string preset)
        {
            if (Presets_Get(name) != null)
                Presets_Delete(name);
            AddPreset(name, preset);
        }

        public static void SetPreset(string name, string preset)
        {
            if (Plugin.Configuration.AutoManageBossModAISettings)
            {
                if (Presets_GetActive() != name)
                {
                    Svc.Log.Debug($"BossMod Setting Preset: {name}");
                    AddPreset(name, preset);
                    Presets_SetActive(name);
                }
                // Presets.SetActive only assigns RotationModuleManager.Preset, which AIBehaviour
                // overwrites from AIManager.AiPreset every tick (see AIBehaviour.Execute). Without
                // also driving AI.SetPreset (-> AIManager.SetAIPreset), the AI tick loop reverts our
                // assignment on the very next frame and none of the transient movement/positional
                // strategies below ever take effect.
                // Only actually arm it while in combat: the preset's NormalMovement/StayCloseToTarget
                // modules have no combat gate (unlike GoToPositional), so activating them during plain
                // corridor navigation fights vnavmesh for movement control over an entirely separate
                // pathfinder, and neither system wins - the character just stands still. This call runs
                // on every SetPreset invocation (not just the first, guarded above), since duty-start
                // calls this before combat starts and the combat-transition call needs its own check.
                if (BossModReborn_IPCSubscriber.IsEnabled && PlayerHelper.InCombat && BossModReborn_IPCSubscriber.Presets_GetActive() != name)
                    BossModReborn_IPCSubscriber.Presets_SetActive(name);
            }
        }

        // Clears just the real AI.SetPreset arm (see SetPreset's comment) without touching the
        // generic Presets.SetActive state - call this once combat/an action finishes and control
        // is handing back to vnavmesh for plain navigation, so NormalMovement/StayCloseToTarget
        // stop fighting it again on the next corridor stretch.
        public static void DisableRealAIPreset()
        {
            if (Plugin.Configuration.AutoManageBossModAISettings && BossModReborn_IPCSubscriber.IsEnabled)
                BossModReborn_IPCSubscriber.Presets_SetActive("");
        }

        public static void DisablePresets()
        {
            if (Plugin.Configuration.AutoManageBossModAISettings)
            {
                if (Presets_GetActive() != null)
                {
                    Svc.Log.Debug($"BossMod Disabling Presets");
                    Presets_ClearActive();
                }
                if (BossModReborn_IPCSubscriber.IsEnabled)
                    BossModReborn_IPCSubscriber.Presets_SetActive("");
            }
        }

        public static void SetRange(float range)
        {
            if (Plugin.Configuration.AutoManageBossModAISettings)
            {
                Svc.Log.Debug($"BossMod Setting Range to: {range}");

                Presets_AddTransientStrategy("AutoDuty",         "BossMod.Autorotation.MiscAI.StayCloseToTarget", "range", MathF.Round(range, 1).ToString(CultureInfo.InvariantCulture));
                Presets_AddTransientStrategy("AutoDuty Passive", "BossMod.Autorotation.MiscAI.StayCloseToTarget", "range", MathF.Round(range, 1).ToString(CultureInfo.InvariantCulture));
            }
        }

        public enum DestinationStrategy { None, Pathfind, Explicit }

        public static void SetMovement(bool on)
        {
            if (Plugin.Configuration.AutoManageBossModAISettings)
            {
                Svc.Log.Debug($"BossMod Setting Movement: {on}");

                string destinationStrategy = (on ? DestinationStrategy.Pathfind : DestinationStrategy.None).ToString();

                Presets_AddTransientStrategy("AutoDuty",         "BossMod.Autorotation.MiscAI.NormalMovement", "Destination", destinationStrategy);
                Presets_AddTransientStrategy("AutoDuty Passive", "BossMod.Autorotation.MiscAI.NormalMovement", "Destination", destinationStrategy);
            }
        }

        public static void SetPositional(Positional positional)
        {
            if (Plugin.Configuration.AutoManageBossModAISettings)
            {
                Svc.Log.Debug($"BossMod Setting Positional: {positional}");

                Presets_AddTransientStrategy("AutoDuty Passive", "BossMod.Autorotation.MiscAI.GoToPositional", "Positional", positional.ToString());
            }
        }
    }


    internal static class YesAlready_IPCSubscriber
    {
        private static readonly YesAlreadyIPC Pkg = new(SafeWrapper.IPCException);

        internal static bool IsEnabled => IPCSubscriber_Common.IsReady("YesAlready");

        public static bool IsPluginEnabled() => Pkg.IsPluginEnabled();

        internal static void Dispose() { }

        public static void SetState(bool on) =>
            Pkg.SetPluginEnabled(on);
    }

    internal static class Gearsetter_IPCSubscriber
    {
        private static readonly GearsetterIPC Pkg = new(SafeWrapper.IPCException);

        internal static bool IsEnabled => IPCSubscriber_Common.IsReady("Gearsetter");

        internal static List<(uint ItemId, InventoryType? SourceInventory, byte? SourceInventorySlot, RaptureGearsetModule.GearsetItemIndex TargetSlot)> GetRecommendationsForGearset(byte gearset) =>
            Pkg.GetRecommendationsForGearset(gearset);

        internal static void Dispose() { }
    }

    internal static class VNavmesh_IPCSubscriber
    {
        private static readonly VnavmeshIPC Pkg = new(SafeWrapper.IPCException);

        internal static bool IsEnabled => IPCSubscriber_Common.IsReady("vnavmesh");

        internal static bool  Nav_IsReady()       => Pkg.IsReady();
        internal static float Nav_BuildProgress() => Pkg.BuildProgress();
        internal static bool  Nav_Reload()        => Pkg.Reload();
        internal static bool  Nav_Rebuild()       => Pkg.Rebuild();

        internal static void Path_Stop()                            => Pkg.Stop();
        internal static bool Path_IsRunning()                       => Pkg.IsRunning();
        internal static int  Path_NumWaypoints()                    => Pkg.NumWaypoints();
        internal static bool Path_GetMovementAllowed()              => Pkg.GetMovementAllowed();
        internal static void Path_SetMovementAllowed(bool allowed)  => Pkg.SetMovementAllowed(allowed);
        internal static bool Path_GetAlignCamera()                  => Pkg.GetAlignCamera();
        internal static void Path_SetAlignCamera(bool align)        => Pkg.SetAlignCamera(align);
        internal static float Path_GetTolerance()                   => Pkg.GetTolerance();
        internal static void Path_SetTolerance(float tolerance)     => Pkg.SetTolerance(tolerance);

        internal static bool SimpleMove_PathfindInProgress() => Pkg.PathfindInProgress();

        // ── 以下走側車，理由見 IPCSubscriberSidecar.cs ──
        internal static Task<List<Vector3>> Nav_Pathfind(Vector3 from, Vector3 to, bool fly) => VNavmeshExtraIPC.Nav_Pathfind(from, to, fly);
        internal static Task<List<Vector3>> Nav_PathfindCancelable(Vector3 from, Vector3 to, bool fly, CancellationToken token) => VNavmeshExtraIPC.Nav_PathfindCancelable(from, to, fly, token);
        internal static void Nav_PathfindCancelAll()      => VNavmeshExtraIPC.Nav_PathfindCancelAll();
        internal static bool Nav_PathfindInProgress()     => VNavmeshExtraIPC.Nav_PathfindInProgress();
        internal static int  Nav_PathfindNumQueued()      => VNavmeshExtraIPC.Nav_PathfindNumQueued();
        internal static bool Nav_IsAutoLoad()             => VNavmeshExtraIPC.Nav_IsAutoLoad();
        internal static void Nav_SetAutoLoad(bool on)     => VNavmeshExtraIPC.Nav_SetAutoLoad(on);

        internal static Vector3 Query_Mesh_NearestPoint(Vector3 p, float halfExtentXZ, float halfExtentY) => VNavmeshExtraIPC.Query_Mesh_NearestPoint(p, halfExtentXZ, halfExtentY);
        internal static Vector3 Query_Mesh_PointOnFloor(Vector3 p, bool allowUnlandable, float halfExtentXZ) => VNavmeshExtraIPC.Query_Mesh_PointOnFloor(p, allowUnlandable, halfExtentXZ);

        internal static void Path_MoveTo(List<Vector3> waypoints, bool fly) => VNavmeshExtraIPC.Path_MoveTo(waypoints, fly);
        internal static bool SimpleMove_PathfindAndMoveTo(Vector3 position, bool canFly) => VNavmeshExtraIPC.SimpleMove_PathfindAndMoveTo(position, canFly);

        internal static bool Window_IsOpen()          => VNavmeshExtraIPC.Window_IsOpen();
        internal static void Window_SetOpen(bool on)  => VNavmeshExtraIPC.Window_SetOpen(on);
        internal static bool DTR_IsShown()            => VNavmeshExtraIPC.DTR_IsShown();
        internal static void DTR_SetShown(bool on)    => VNavmeshExtraIPC.DTR_SetShown(on);

        internal static void Dispose() => VNavmeshExtraIPC.Dispose();
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

    public static class Wrath_IPCSubscriber
    {
        /// <summary>
        ///     Why a lease was cancelled.
        /// </summary>
        /// <remarks>
        ///     值與 <see cref="WrathCombo.API.Enum.CancellationReason"/> 逐一對齊；
        ///     這裡保留自己一份是因為 <see cref="CancelActions"/> 收到的是裸 int。
        /// </remarks>
        public enum CancellationReason
        {
            [Description("The Wrath user manually elected to revoke your lease.")]
            WrathUserManuallyCancelled = 0,

            [Description("Your plugin was detected as having been disabled, " +
                         "not that you're likely to see this.")]
            LeaseePluginDisabled = 1,

            [Description("The Wrath plugin is being disabled.")]
            WrathPluginDisabled = 2,

            [Description("Your lease was released by IPC call, " +
                         "theoretically this was done by you.")]
            LeaseeReleased = 3,

            [Description("IPC Services have been disabled remotely. "                 +
                         "Please see the commit history for /res/ipc_status.txt. \n " +
                         "https://github.com/PunishXIV/WrathCombo/commits/main/res/ipc_status.txt")]
            AllServicesSuspended = 4,

            [Description("Player job has been changed and leases will have to be reapplied.")]
            JobChanged = 5,
        }

        /// <summary>
        ///     The subset of <see cref="AutoRotationConfig" /> options that can be set
        ///     via IPC.
        /// </summary>
        public enum AutoRotationConfigOption
        {
            InCombatOnly         = 0, //bool
            DPSRotationMode      = 1,
            HealerRotationMode   = 2,
            FATEPriority         = 3,  //bool
            QuestPriority        = 4,  //bool
            SingleTargetHPP      = 5,  //int
            AoETargetHPP         = 6,  //int
            SingleTargetRegenHPP = 7,  //int
            ManageKardia         = 8,  //bool
            AutoRez              = 9,  //bool
            AutoRezDPSJobs       = 10, //bool
            AutoCleanse          = 11, //bool
            IncludeNPCs          = 12, //bool
            OnlyAttackInCombat   = 13, //bool
        }

        /// <remarks>
        ///     🔴 這個列舉是 <c>ConfigurationMain.Wrath_TargetingTank</c> 等設定欄位的**型別**，
        ///     換掉會動到使用者設定檔的序列化，所以維持在本類底下、不改用套件的同名列舉。
        ///     值與 <see cref="WrathCombo.API.Enum.DPSRotationMode"/> 逐一對齊。
        /// </remarks>
        public enum DPSRotationMode
        {
            Manual          = 0,
            Highest_Max     = 1,
            Lowest_Max      = 2,
            Highest_Current = 3,
            Lowest_Current  = 4,
            Tank_Target     = 5,
            Nearest         = 6,
            Furthest        = 7,
        }

        /// <summary>
        ///     The subset of <see cref="AutoRotationConfig.HealerRotationMode" /> options
        ///     that can be set via IPC.
        /// </summary>
        public enum HealerRotationMode
        {
            Manual          = 0,
            Highest_Current = 1,
            Lowest_Current  = 2
            //Self_Priority,
            //Tank_Priority,
            //Healer_Priority,
            //DPS_Priority,
        }

        public enum SetResult
        {
            [Description("A default value that shouldn't ever be seen.")]
            IGNORED = -1,

            // Success Statuses

            [Description("The configuration was set successfully.")]
            Okay = 0,

            [Description("The configuration will be set, it is working asynchronously.")]
            OkayWorking = 1,

            // Error Statuses
            [Description("IPC services are currently disabled.")]
            IPCDisabled = 10,

            [Description("Invalid lease.")]
            InvalidLease = 11,

            [Description("Blacklisted lease.")]
            BlacklistedLease = 12,

            [Description("Configuration you are trying to set is already set.")]
            Duplicate = 13,

            [Description("Player object is not available.")]
            PlayerNotAvailable = 14,

            [Description("The configuration you are trying to set is not available.")]
            InvalidConfiguration = 15,

            [Description("The value you are trying to set is invalid.")]
            InvalidValue = 16,
        }

        private static Guid? _curLease;


        internal static bool IsEnabled => IPCSubscriber_Common.IsReady("WrathCombo");

        // ── Wrath 的委派管線：WrathCombo.API（官方 IPC 用戶端程式庫），不走 EzIPC ──
        //
        // 🔴 觀測性：WrathCombo.API 有自己的一套錯誤處理，**不會**觸發
        //    EzIPC.OnSafeInvocationException，也就是不會經過 EzIpcFailureLog。
        //    若照它預設的 ErrorType.All 全部靜音，Wrath IPC 失敗會變成完全沒有 log
        //    ——那正是 EzIpcFailureLog 當初被寫出來要解決的問題。
        //    所以我們讓它照常擲例外（AutoDuty.cs 裡 Init 時不加任何 suppress），
        //    在這裡自己 catch → 交給 EzIpcFailureLog 節流印出 → 回傳與遷移前相同的 default。
        //    ⇒ 對呼叫端來說語意等同原本的 SafeWrapper.AnyException，但失敗看得見。

        private static T WrathSafe<T>(Func<T> call)
        {
            try
            {
                return call();
            }
            catch (Exception e)
            {
                EzIpcFailureLog.Report(e);
                // 與 WrathCombo.API 自己的 SafeInvokeRawMethod 同一個約定：SetResult 回 IGNORED
                // 而不是 default(=Okay)。default 會讓「呼叫根本沒送到」長得跟「設定成功」一樣。
                if (typeof(T) == typeof(ApiSetResult))
                    return (T)(object)ApiSetResult.IGNORED;
                return default;
            }
        }

        private static void WrathSafe(Action call)
        {
            try
            {
                call();
            }
            catch (Exception e)
            {
                EzIpcFailureLog.Report(e);
            }
        }

        /// <summary>
        ///     把 WrathCombo.API 的 <see cref="ApiSetResult"/> 轉回本類的 <see cref="SetResult"/>。
        ///     兩者的每一個成員值都一樣，所以是純粹的數值轉換。
        ///     ⚠️ 呼叫整個失敗時回 <see cref="SetResult.IGNORED"/>（遷移前是 <c>default</c> 也就是
        ///     <see cref="SetResult.Okay"/>）——<see cref="CheckResult"/> 對 IGNORED 已經回 false，
        ///     所以這是把「失敗被當成成功」改成「失敗被當成失敗」，只在 IPC 本來就不通時才看得出差別。
        /// </summary>
        private static SetResult FromApi(ApiSetResult result) => (SetResult)(int)result;

        /// <summary>
        ///     Get the current state of the Auto-Rotation setting in Wrath Combo.
        /// </summary>
        /// <returns>Whether Auto-Rotation is enabled or disabled</returns>
        /// <remarks>
        ///     This is only the state of Auto-Rotation, not whether any combos are
        ///     enabled in Auto-Mode.
        /// </remarks>
        internal static bool GetAutoRotationState() =>
            WrathSafe(WrathIPCWrapper.GetAutoRotationState);

        /// <summary>
        ///     Checks if the current job has a Single and Multi-Target combo configured
        ///     that are enabled in Auto-Mode.
        /// </summary>
        /// <returns>
        ///     If the user's current job is fully ready for Auto-Rotation.
        /// </returns>
        internal static bool IsCurrentJobAutoRotationReady() =>
            WrathSafe(WrathIPCWrapper.IsCurrentJobAutoRotationReady);

        /// <summary>
        ///     Get the state of Auto-Rotation Configuration in Wrath Combo.
        /// </summary>
        /// <param name="option">The option to check the value of.</param>
        /// <returns>The correctly-typed value of the configuration.</returns>
        private static object GetAutoRotationConfigState(AutoRotationConfigOption option) =>
            WrathSafe(() => WrathIPCWrapper.GetAutoRotationConfigState((ApiConfigOption)(int)option));

        private static SetResult SetAutoRotationState(Guid lease, bool enabled) =>
            FromApi(WrathSafe(() => WrathIPCWrapper.SetAutoRotationState(lease, enabled)));

        private static SetResult SetCurrentJobAutoRotationReady(Guid lease) =>
            FromApi(WrathSafe(() => WrathIPCWrapper.SetCurrentJobAutoRotationReady(lease)));

        private static SetResult SetAutoRotationConfigState(Guid lease, AutoRotationConfigOption option, bool value) =>
            FromApi(WrathSafe(() => WrathIPCWrapper.SetAutoRotationConfigState(lease, (ApiConfigOption)(int)option, value)));

        private static SetResult SetAutoRotationConfigState(Guid lease, AutoRotationConfigOption option, DPSRotationMode value) =>
            FromApi(WrathSafe(() => WrathIPCWrapper.SetAutoRotationConfigState(lease, (ApiConfigOption)(int)option, (ApiDpsMode)(int)value)));

        private static SetResult SetAutoRotationConfigState(Guid lease, AutoRotationConfigOption option, HealerRotationMode value) =>
            FromApi(WrathSafe(() => WrathIPCWrapper.SetAutoRotationConfigState(lease, (ApiConfigOption)(int)option, (ApiHealerMode)(int)value)));

        private static Guid? RegisterForLeaseWithCallback(string internalPluginName, string pluginName, string ipcPrefixForCallback) =>
            WrathSafe(() => WrathIPCWrapper.RegisterForLeaseWithCallback(internalPluginName, pluginName, ipcPrefixForCallback));

        private static void ReleaseControl(Guid lease) =>
            WrathSafe(() => WrathIPCWrapper.ReleaseControl(lease));

        public static bool DoThing(Func<SetResult> action)
        {
            SetResult result = action();
            bool      check  = result.CheckResult();
            if (!check && result == SetResult.InvalidLease)
                check = action().CheckResult();
            return check;
        }

        private static bool CheckResult(this SetResult result)
        {
            switch (result)
            {
                case SetResult.Okay:
                case SetResult.OkayWorking:
                    return true;
                case SetResult.InvalidLease:
                    _curLease = null;
                    Register();
                    return false;
                case SetResult.BlacklistedLease:
                    Plugin.Configuration.AutoManageRotationPluginState = false;
                    Plugin.Configuration.Save();
                    return false;
                case SetResult.IPCDisabled:
                case SetResult.Duplicate:
                case SetResult.PlayerNotAvailable:
                case SetResult.InvalidConfiguration:
                case SetResult.InvalidValue:
                case SetResult.IGNORED:
                    return false;
                default:
                    throw new ArgumentOutOfRangeException(nameof(result), result, null);
            }
        }

        internal static bool SetJobAutoReady() =>
            Register() && DoThing(() => SetCurrentJobAutoRotationReady(_curLease!.Value));

        internal static void SetAutoMode(bool on)
        {
            if (Register())
            {
                bool autoRotationState = DoThing(() => SetAutoRotationState(_curLease!.Value, on));
                if (autoRotationState && on)
                {
                    SetAutoRotationConfigState(_curLease.Value, AutoRotationConfigOption.InCombatOnly,       false);
                    SetAutoRotationConfigState(_curLease.Value, AutoRotationConfigOption.AutoRez,            true);
                    SetAutoRotationConfigState(_curLease.Value, AutoRotationConfigOption.AutoRezDPSJobs,     true);
                    SetAutoRotationConfigState(_curLease.Value, AutoRotationConfigOption.IncludeNPCs,        true);
                    SetAutoRotationConfigState(_curLease.Value, AutoRotationConfigOption.OnlyAttackInCombat, false);

                    DPSRotationMode dpsConfig = Plugin.CurrentPlayerItemLevelandClassJob.Value.GetCombatRole() == CombatRole.Tank ?
                                                    Plugin.Configuration.Wrath_TargetingTank :
                                                    Plugin.Configuration.Wrath_TargetingNonTank;
                    SetAutoRotationConfigState(_curLease.Value, AutoRotationConfigOption.DPSRotationMode, dpsConfig);

                    SetAutoRotationConfigState(_curLease.Value, AutoRotationConfigOption.HealerRotationMode, HealerRotationMode.Lowest_Current);

                }
            }
        }

        internal static bool Register()
        {
            if (_curLease == null)
            {
                _curLease = RegisterForLeaseWithCallback("AutoDuty", "AutoDuty", null);

                if (_curLease == null && IsEnabled)
                {
                    Plugin.Configuration.AutoManageRotationPluginState = false;
                    Plugin.Configuration.Save();
                }
            }
            return _curLease != null;
        }

        internal static void CancelActions(int reason, string s)
        {
            switch ((CancellationReason) reason)
            {
                case CancellationReason.WrathUserManuallyCancelled:
                    Plugin.Configuration.AutoManageRotationPluginState = false;
                    Plugin.Configuration.Save();
                    break;
                case CancellationReason.LeaseePluginDisabled:
                case CancellationReason.WrathPluginDisabled:
                case CancellationReason.LeaseeReleased:
                case CancellationReason.AllServicesSuspended:
                case CancellationReason.JobChanged:
                default:
                    break;
            }

            _curLease = null;
            Svc.Log.Info($"Wrath lease cancelled via {(CancellationReason) reason} for: {s}");
        }

        /// <summary>
        ///     租約我們只拿得到一個 handle，真狀態在 Wrath Combo 那一端。
        ///     這個欄位記住「已經試過釋放但沒被對方確認」的那一份，用來把重試次數限制成一次。
        /// </summary>
        private static Guid? _unconfirmedReleaseLease;

        internal static void Release()
        {
            if (!_curLease.HasValue)
            {
                _unconfirmedReleaseLease = null;
                return;
            }

            Guid lease = _curLease.Value;

            // 判準＝誰持有真狀態：租約的真狀態在 Wrath Combo 手上，我們這邊只是一個 handle。
            // 對方持有 ⇒ 確認對方放掉才放手，不能無條件把 _curLease 清成 null。
            if (!IsEnabled)
            {
                // Wrath Combo 根本沒載入，租約隨它一起消失了，沒有東西要等對方放掉。
                Svc.Log.Information($"Wrath Combo is not loaded - dropping Wrath lease {lease} handle without releasing.");
                _curLease                = null;
                _unconfirmedReleaseLease = null;
                return;
            }

            bool isRetry = _unconfirmedReleaseLease == lease;

            Svc.Log.Information(isRetry ?
                                    $"Retrying release of Wrath lease {lease}." :
                                    $"Releasing Wrath lease {lease}.");

            // ⚠️ ReleaseControl 是 void，而且失敗會被 WrathSafe 記進 log 後吞掉（見上面那段說明）：
            // 租約已失效、IPC 停用、或呼叫整個擲例外，在這裡通通長得跟成功一模一樣。
            // 成功時 Wrath Combo 會**同步**回呼 AutoDuty.WrathComboCallback → CancelActions，
            // 由它把 _curLease 清成 null —— 所以「呼叫後 _curLease 已經是 null」才是對方真的放掉的證據。
            ReleaseControl(lease);

            if (!_curLease.HasValue)
            {
                _unconfirmedReleaseLease = null;
                return;
            }

            if (isRetry)
            {
                // 重試也沒被確認就不要無限拖著：本機放手。
                // Wrath Combo 會自行清掉 leasee 外掛已不在的租約，所以這裡不會留下永久的孤兒租約。
                Svc.Log.Information($"Wrath lease {lease} release is still unconfirmed after a retry - dropping the handle locally.");
                _curLease                = null;
                _unconfirmedReleaseLease = null;
            }
            else
            {
                // 保留 handle：租約很可能還在對方那裡有效，丟掉 handle 才是真的把它變成孤兒。
                // 後續的 set 呼叫若拿到 InvalidLease，CheckResult 也會自行清掉並重新註冊。
                _unconfirmedReleaseLease = lease;
                Svc.Log.Information($"Wrath lease {lease} release was not confirmed by Wrath Combo - keeping the handle and retrying on the next release.");
            }
        }

        internal static void Dispose()
        {
            Release();
        }
    }


    internal class IPCSubscriber_Common
    {
        internal static bool IsReady(string pluginName) => DalamudReflector.TryGetDalamudPlugin(pluginName, out _, false, true);

        internal static Version Version(string pluginName) => DalamudReflector.TryGetDalamudPlugin(pluginName, out var dalamudPlugin, false, true) ? dalamudPlugin.GetType().Assembly.GetName().Version : new Version(0, 0, 0, 0);

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
    }
}
