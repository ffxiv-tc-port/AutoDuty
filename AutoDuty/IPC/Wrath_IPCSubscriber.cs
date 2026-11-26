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

// 門面層的共同規則（wrapper 明確傳入建構式等）見 IPCSubscriber.cs 檔頭。

namespace AutoDuty.IPC
{
    using System.ComponentModel;
    using ECommons.GameFunctions;
    using Helpers;

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
        // 🔴 觀測性：WrathCombo.API 有自己的一套錯誤處理，**不會**觸發 EzIPC.OnSafeInvocationException，
        //    照它預設的 ErrorType.All 全部靜音的話 Wrath IPC 失敗會變成完全沒有 log。
        //    所以我們讓它照常擲例外（AutoDuty.cs 裡 Init 時不加任何 suppress），在這裡自己 catch → 交給 EzIpcFailureLog
        //    節流印出 → 回傳 default ⇒ 對呼叫端來說語意等同 SafeWrapper.AnyException，但失敗看得見。

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
        ///     ⚠️ 呼叫整個失敗時回 <see cref="SetResult.IGNORED"/> 而不是 <c>default</c>（也就是
        ///     <see cref="SetResult.Okay"/>）——<see cref="CheckResult"/> 對 IGNORED 回 false，
        ///     所以「呼叫根本沒送到」不會被當成成功。
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

        /// <summary>分隊先發暫停自動循環用的租約；非 null＝暫停中（只有自己這把租約開著時才會暫停）。</summary>
        private static Guid? _pausedLease;
        private static long _pausedAtMs;

        /// <summary>分隊先發的暫停被 AutoDuty 每 5 秒的「開自動循環」攔住的最長時間；先發自己的上限遠小於這個值。</summary>
        private const long PauseSafetyMs = 10_000;

        internal static bool AutoRotationPaused => _pausedLease != null;

        /// <summary>
        /// 暫停 AutoDuty 經租約開著的 Wrath 自動循環。回 null＝已暫停；否則回不暫停的原因（不硬來、不註冊新租約）。
        /// 只在框架執行緒呼叫。恢復一律走 <see cref="ResumeAutoRotation"/>。
        /// </summary>
        internal static string TryPauseAutoRotation()
        {
            if (_pausedLease != null)
                return null;
            if (!Plugin.Configuration.AutoManageRotationPluginState || Plugin.Configuration.UsingAlternativeRotationPlugin)
                return "AutoDuty 沒有在管輪替插件（「自動管理連段插件狀態」沒開，或用的是其他輪替插件）";
            if (!IsEnabled)
                return "Wrath Combo 沒有載入";
            if (_curLease is not { } lease)
                return "AutoDuty 還沒拿到 Wrath Combo 的租借";
            if (!Plugin.RotationEngaged)
                return "AutoDuty 目前沒有開著自動循環";
            if (!GetAutoRotationState())
                return "Wrath Combo 自動循環本來就是關的";
            var result = FromApi(WrathSafe(() => WrathIPCWrapper.SetAutoRotationState(lease, false)));
            if (result is SetResult.Okay or SetResult.OkayWorking)
            {
                _pausedLease = lease;
                _pausedAtMs  = Environment.TickCount64;
                return null;
            }
            if (result == SetResult.InvalidLease)
                _curLease = null;
            return $"Wrath Combo 沒接受暫停（{result}）";
        }

        /// <summary>撤銷 <see cref="TryPauseAutoRotation"/>：租約沒換過才設回開著（暫停前就是開著）。回一句結果給 log。</summary>
        internal static string ResumeAutoRotation()
        {
            if (_pausedLease is not { } lease)
                return "沒有暫停中";
            _pausedLease = null;
            if (!IsEnabled)
                return "Wrath Combo 沒有載入";
            if (_curLease != lease)
                return "租借已失效，Wrath Combo 已回到使用者自己的設定";
            var result = FromApi(WrathSafe(() => WrathIPCWrapper.SetAutoRotationState(lease, true)));
            if (result == SetResult.InvalidLease)
                _curLease = null;
            return result is SetResult.Okay or SetResult.OkayWorking ? "已恢復" : $"恢復回 {result}（AutoDuty 之後的「開自動循環」會再補開）";
        }

        /// <summary>首領戰有限時必殺目標時把 DPS 選目標暫改「手動」的租約；非 null＝改著。只在框架執行緒碰。</summary>
        private static Guid? _manualTargetingLease;
        private static DPSRotationMode _manualTargetingRestore;
        private static long _manualTargetingRefreshMs;
        private static readonly HashSet<string> ManualTargetingSkipLogged = [];

        /// <summary>呼叫端停止續約超過這麼久就改回原值（王戰結束、AutoDuty 停止、換領地、例外都走這條）。</summary>
        private const long ManualTargetingKeepAliveMs = 6000;

        internal static bool ManualTargetingHeld => _manualTargetingLease != null;

        private static DPSRotationMode RoleDpsConfig() =>
            Plugin.CurrentPlayerItemLevelandClassJob.Value.GetCombatRole() == CombatRole.Tank ?
                Plugin.Configuration.Wrath_TargetingTank :
                Plugin.Configuration.Wrath_TargetingNonTank;

        /// <summary>
        /// 場上有限時必殺目標的每個節拍呼叫：沒改就改成「手動」（只打玩家目前目標），改著就續約。
        /// 只有 AutoDuty 經租約開著自動循環時才改；其他情況不硬來，同一原因只記一次。
        /// </summary>
        internal static void HoldManualTargeting(long nowMs)
        {
            if (_manualTargetingLease != null)
            {
                _manualTargetingRefreshMs = nowMs;
                return;
            }
            string skip = null;
            if (!Plugin.Configuration.AutoManageRotationPluginState || Plugin.Configuration.UsingAlternativeRotationPlugin)
                skip = "AutoDuty 沒有在管輪替插件";
            else if (!IsEnabled)
                skip = "Wrath Combo 沒有載入";
            else if (_curLease == null)
                skip = "AutoDuty 還沒拿到 Wrath Combo 的租借";
            else if (!Plugin.RotationEngaged)
                skip = "AutoDuty 目前沒有開著自動循環";
            var restore = RoleDpsConfig();
            if (skip == null && restore == DPSRotationMode.Manual)
                skip = "DPS 選目標本來就是手動";
            if (skip != null)
            {
                if (ManualTargetingSkipLogged.Add(skip))
                    Svc.Log.Information($"[Boss] 有限時必殺目標，但不改 Wrath Combo 的 DPS 選目標：{skip}");
                return;
            }
            var lease  = _curLease!.Value;
            var result = SetAutoRotationConfigState(lease, AutoRotationConfigOption.DPSRotationMode, DPSRotationMode.Manual);
            if (result is SetResult.Okay or SetResult.OkayWorking)
            {
                _manualTargetingLease     = lease;
                _manualTargetingRestore   = restore;
                _manualTargetingRefreshMs = nowMs;
                Svc.Log.Information($"[Boss] 有限時必殺目標：Wrath Combo 的 DPS 選目標暫改「手動」（原本 {restore}），只打 AutoDuty 選的目標");
                return;
            }
            if (result == SetResult.InvalidLease)
                _curLease = null;
            if (ManualTargetingSkipLogged.Add("result:" + result))
                Svc.Log.Information($"[Boss] Wrath Combo 沒接受把 DPS 選目標改成手動（{result}）");
        }

        /// <summary>每幀呼叫、放在 Framework_Update 最前面：續約斷了就改回原值。</summary>
        internal static void TickManualTargeting()
        {
            if (_manualTargetingLease == null || Environment.TickCount64 - _manualTargetingRefreshMs < ManualTargetingKeepAliveMs)
                return;
            try
            {
                Svc.Log.Information($"[Boss] 限時必殺目標已不在（或王戰結束）：Wrath Combo 的 DPS 選目標改回 {_manualTargetingRestore}：{ReleaseManualTargeting()}");
            }
            catch (Exception e)
            {
                _manualTargetingLease = null;
                EzIpcFailureLog.Report(e);
            }
        }

        /// <summary>改回 <see cref="HoldManualTargeting"/> 之前的值；租約換過就不動（新租約沒改過它）。</summary>
        internal static string ReleaseManualTargeting()
        {
            if (_manualTargetingLease is not { } lease)
                return "沒有改著";
            _manualTargetingLease = null;
            if (!IsEnabled)
                return "Wrath Combo 沒有載入";
            if (_curLease != lease)
                return "租借已失效，Wrath Combo 已回到使用者自己的設定";
            var result = SetAutoRotationConfigState(lease, AutoRotationConfigOption.DPSRotationMode, _manualTargetingRestore);
            if (result == SetResult.InvalidLease)
                _curLease = null;
            return result is SetResult.Okay or SetResult.OkayWorking ? "已改回" : $"改回時回 {result}";
        }

        internal static void SetAutoMode(bool on)
        {
            if (_pausedLease != null)
            {
                if (!on)
                    _pausedLease = null;
                else if (Environment.TickCount64 - _pausedAtMs < PauseSafetyMs)
                    return;
                else
                    Svc.Log.Information($"[分隊先發] Wrath Combo 自動循環暫停超過 {PauseSafetyMs / 1000} 秒安全上限，強制恢復：{ResumeAutoRotation()}");
            }
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

                    // 限時必殺目標期間維持手動；改回由 TickManualTargeting 負責。
                    DPSRotationMode dpsConfig = _manualTargetingLease != null && _manualTargetingLease == _curLease ? DPSRotationMode.Manual : RoleDpsConfig();
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

            _curLease             = null;
            _pausedLease          = null;
            _manualTargetingLease = null;
            Svc.Log.Info($"Wrath lease cancelled via {(CancellationReason) reason} for: {s}");
        }

        /// <summary>
        ///     租約我們只拿得到一個 handle，真狀態在 Wrath Combo 那一端。
        ///     這個欄位記住「已經試過釋放但沒被對方確認」的那一份，用來把重試次數限制成一次。
        /// </summary>
        private static Guid? _unconfirmedReleaseLease;

        internal static void Release()
        {
            // 釋放沒被確認時租約還留在對方那裡：先撤銷暫停與手動選目標，免得停在我們改過的值。
            if (_pausedLease != null)
                Svc.Log.Information($"[分隊先發] 釋放 Wrath Combo 租借前撤銷自動循環暫停：{ResumeAutoRotation()}");
            if (_manualTargetingLease != null)
                Svc.Log.Information($"[Boss] 釋放 Wrath Combo 租借前把 DPS 選目標改回 {_manualTargetingRestore}：{ReleaseManualTargeting()}");

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

            // ⚠️ ReleaseControl 是 void，而且失敗會被 WrathSafe 記進 log 後吞掉：
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
}
