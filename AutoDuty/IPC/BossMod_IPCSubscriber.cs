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

        /// <summary>
        /// 套件把它宣告成自訂 delegate <c>BossModIPC.Delegates.AddTransientStrategyDelegate</c>；
        /// EzIPC 訂閱端要有 <c>TryGetDelegateSignature</c>（本 repo 釘的 ECommons 已有）才綁得上非泛型委派，
        /// 綁不上時欄位永遠停在 null。IPC 端點名是 <c>BossMod.Presets.AddTransientStrategy</c>。
        /// </summary>
        /// <remarks>string presetName, string moduleTypeName, string trackName, string value</remarks>
        internal static bool Presets_AddTransientStrategy(string presetName, string moduleTypeName, string trackName, string value) =>
            Pkg.Presets_AddTransientStrategy(presetName, moduleTypeName, trackName, value);

        /// <summary>
        /// 由 <see cref="IPCSubscriber_Common.DisposeAllSubscribers"/> 統一呼叫（其餘 <c>*_IPCSubscriber.Dispose()</c> 也一樣）。
        /// 套件實例的 IPC 成員全是訂閱端，而 EzIPC 只對提供端與事件產生 disposal token，
        /// 所以這裡沒有東西要拆；全域拆除由 <c>ECommonsMain.Dispose()</c> 負責。
        /// </summary>
        internal static void Dispose() { }

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
                // Presets.SetActive only assigns RotationModuleManager.Preset, which AIBehaviour overwrites from
                // AIManager.AiPreset every tick, so AI.SetPreset must be driven too or the assignment reverts next frame.
                // Only arm it while in combat: the preset's NormalMovement/StayCloseToTarget modules have no combat
                // gate, so during plain corridor navigation they fight vnavmesh for movement control and the character
                // just stands still. This check runs on every SetPreset invocation, not just the first.
                if (BossModReborn_IPCSubscriber.IsEnabled && PlayerHelper.InCombat && BossModReborn_IPCSubscriber.Presets_GetActive() != name)
                    WriteAIPreset(name);
            }
        }

        // Clears just the real AI.SetPreset arm (see SetPreset's comment) without touching the
        // generic Presets.SetActive state - call this once combat/an action finishes and control
        // is handing back to vnavmesh for plain navigation, so NormalMovement/StayCloseToTarget
        // stop fighting it again on the next corridor stretch.
        public static void DisableRealAIPreset()
        {
            if (Plugin.Configuration.AutoManageBossModAISettings && BossModReborn_IPCSubscriber.IsEnabled)
                WriteAIPreset("");
        }

        public static void DisablePresets()
        {
            if (Plugin.Configuration.AutoManageBossModAISettings)
            {
                // 只清 AutoDuty 自己的預設;使用者手動啟用的別的預設不動。
                if (Presets_GetActive() is "AutoDuty" or "AutoDuty Passive")
                {
                    Svc.Log.Debug($"BossMod Disabling Presets");
                    Presets_ClearActive();
                }
                RestoreUserAIPreset();
            }
        }

        // 新版 BMR:用執行期覆寫(Configuration.SetTransientString)切換,不寫使用者在 AI 視窗選的那一格,收尾 ClearTransient。
        // 舊版 BMR(沒有該端點):AI.SetPreset 會寫那一格(AIConfig.AIAutorotPresetName) ⇒ 這一趟第一次寫之前記下原值,
        // 收尾只在現值仍是我們最後寫的值時還原,途中被別人改過就不動。
        private const string BmrAIPresetKey = "AIConfig.AIAutorotPresetName";
        private enum AIPresetMode { None, Transient, Legacy }
        private static AIPresetMode _aiPresetMode;
        private static bool   _aiPresetOriginalKnown;
        private static string _aiPresetOriginal    = "";
        private static string _aiPresetLastWritten = "";

        private static void WriteAIPreset(string name)
        {
            if (_aiPresetMode != AIPresetMode.Legacy &&
                BossModReborn_IPCSubscriber.SetTransientString(Svc.PluginInterface.InternalName, BmrAIPresetKey, name))
            {
                if (_aiPresetMode == AIPresetMode.None)
                    Svc.Log.Information("[BMR AI 預設] 用 BMR 的暫時覆寫切換 AI 自動循環預設,不寫你的 BMR 設定;停止或副本結束時取消覆寫。");
                _aiPresetMode = AIPresetMode.Transient;
                return;
            }

            if (_aiPresetMode != AIPresetMode.Legacy)
            {
                _aiPresetMode          = AIPresetMode.Legacy;
                _aiPresetOriginalKnown = TryReadUserAIPreset(out string original);
                _aiPresetOriginal      = original ?? "";
                Svc.Log.Information(_aiPresetOriginalKnown
                                        ? $"[BMR AI 預設] 記下原本的 AI 自動循環預設「{_aiPresetOriginal}」(空白＝沒選),停止或副本結束時還原。"
                                        : "[BMR AI 預設] 讀不到原本的 AI 自動循環預設,停止或副本結束時照舊清空。");
            }

            BossModReborn_IPCSubscriber.Presets_SetActive(name);
            // 讀回實際生效的名字:BMR 找不到同名預設時會寫成空的,拿它比對才不會誤判成「被別人改過」。
            _aiPresetLastWritten = BossModReborn_IPCSubscriber.Presets_GetActive() ?? "";
        }

        /// <summary>
        /// 讀 BMR 設定裡使用者選的 AI 自動循環預設。AI.GetPreset 回的是執行期欄位,BMR 剛載入時是空的、
        /// 讀不到使用者存的值,所以用反射唯讀設定物件。只在框架執行緒呼叫(TryGetDalamudPlugin 會寫共用快取)。
        /// </summary>
        private static bool TryReadUserAIPreset(out string name)
        {
            name = null;
            try
            {
                if (!DalamudReflector.TryGetDalamudPlugin("BossModReborn", out var plugin, true, true))
                    return false;

                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                                                             System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;
                Type                          managerType = plugin.GetType().Assembly.GetType("BossMod.AI.AIManager");
                System.Reflection.FieldInfo configField = managerType?.GetField("_config", flags);
                if (managerType == null || configField == null)
                    return false;

                object config = configField.IsStatic ? configField.GetValue(null) : configField.GetValue(managerType.GetField("Instance", flags)?.GetValue(null));
                System.Reflection.FieldInfo nameField = config?.GetType().GetField("AIAutorotPresetName", flags);
                if (config == null || nameField == null || nameField.FieldType != typeof(string))
                    return false;

                name = (string)nameField.GetValue(config);
                return true;
            }
            catch (Exception ex)
            {
                Svc.Log.Information($"[BMR AI 預設] 讀原本的 AI 自動循環預設失敗:{ex.GetType().Name}:{ex.Message}");
                return false;
            }
        }

        /// <summary>這一趟切換過 AI 預設才動作;冪等。暫時覆寫＝取消覆寫;舊版 BMR 讀不到原值時照舊清空。</summary>
        public static void RestoreUserAIPreset()
        {
            AIPresetMode mode = _aiPresetMode;
            if (mode == AIPresetMode.None)
                return;

            _aiPresetMode = AIPresetMode.None;
            if (!BossModReborn_IPCSubscriber.IsEnabled)
                return;

            if (mode == AIPresetMode.Transient)
            {
                if (BossModReborn_IPCSubscriber.ClearTransient(Svc.PluginInterface.InternalName, BmrAIPresetKey))
                    Svc.Log.Information("[BMR AI 預設] 已取消暫時覆寫,BMR 回到你在 AI 視窗選的自動循環預設。");
                return;
            }

            if (!_aiPresetOriginalKnown)
            {
                BossModReborn_IPCSubscriber.Presets_SetActive("");
                return;
            }

            string current = BossModReborn_IPCSubscriber.Presets_GetActive() ?? "";
            if (!string.Equals(current, _aiPresetLastWritten, StringComparison.Ordinal))
            {
                Svc.Log.Information($"[BMR AI 預設] 途中被改成「{current}」,不是 AutoDuty 最後寫的「{_aiPresetLastWritten}」,不還原。");
                return;
            }

            if (string.Equals(current, _aiPresetOriginal, StringComparison.Ordinal))
                return;

            BossModReborn_IPCSubscriber.Presets_SetActive(_aiPresetOriginal);
            Svc.Log.Information($"[BMR AI 預設] 已還原成原本的「{_aiPresetOriginal}」(空白＝沒選)。");
        }

        // 路徑檔的 ChatCommand「/bmrai off」只該影響那一場:送出前 AI 開著才記下區域,
        // 離開該區域或停止時 AI 仍是關的就重新開啟;途中已被開回來(路徑自己的 /bmrai on 或使用者)就不動。
        private static ushort _bmrAIOffTerritory;

        /// <summary>只在框架執行緒呼叫(路徑的 ChatCommand 任務)。</summary>
        public static void NoteBmrAICommand(string command, ushort territory)
        {
            string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !parts[0].Equals("/bmrai", StringComparison.OrdinalIgnoreCase))
                return;

            if (parts[1].Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                _bmrAIOffTerritory = 0;
                return;
            }

            if (!parts[1].Equals("off", StringComparison.OrdinalIgnoreCase) || _bmrAIOffTerritory != 0 || territory == 0)
                return;

            if (BossModReborn_IPCSubscriber.IsEnabled && BossModReborn_IPCSubscriber.AI_IsEnabled())
            {
                _bmrAIOffTerritory = territory;
                Svc.Log.Information($"[BMR AI] 路徑關掉了 BMR AI(區域 {territory}),離開這個區域或停止時重新開啟。");
            }
        }

        /// <summary>離開記下的區域時才還原;<paramref name="territory"/> 為 null＝停止,一律還原。冪等。</summary>
        public static void RestoreBmrAIEnabled(ushort? territory)
        {
            if (_bmrAIOffTerritory == 0 || territory == 0 || territory == _bmrAIOffTerritory)
                return;

            ushort offTerritory = _bmrAIOffTerritory;
            _bmrAIOffTerritory = 0;
            if (!BossModReborn_IPCSubscriber.IsEnabled || BossModReborn_IPCSubscriber.AI_IsEnabled())
                return;

            // AI.SetEnabled 在 BMR 端走主執行緒閘門;非框架執行緒(卸載途中)同步等它可能卡 5 秒,刻意不還原。
            if (!Svc.Framework.IsInFrameworkUpdateThread)
            {
                Svc.Log.Information($"[BMR AI] 路徑在區域 {offTerritory} 關掉的 BMR AI 沒有重新開啟(不在框架執行緒)。");
                return;
            }

            bool now = BossModReborn_IPCSubscriber.AI_SetEnabled(true);
            Svc.Log.Information($"[BMR AI] 已重新開啟路徑在區域 {offTerritory} 關掉的 BMR AI,目前狀態:{(now ? "開啟" : "關閉")}。");
        }

        /// <summary>
        /// 上一次已經輸出過的 Range 診斷指紋（值 ＋ 兩個 preset 的接受結果）。
        /// </summary>
        /// <remarks>
        /// SetRange 在戰鬥中是每幀等級的呼叫（AutoDuty.cs 依周圍敵數在兩個距離之間切換），
        /// 所以 Information 只在「值或結果變了」時輸出。<b>送出本身不受影響</b>，照舊每次都送。
        /// </remarks>
        private static string _lastRangeReport = "";

        public static void SetRange(float range)
        {
            if (Plugin.Configuration.AutoManageBossModAISettings)
            {
                Svc.Log.Debug($"BossMod Setting Range to: {range}");

                string value = MathF.Round(range, 1).ToString(CultureInfo.InvariantCulture);

                // 🔴 軌道的 InternalName 是 "Range"（大寫 R），不是 "range"：BMR 的 DefineFloat 直接拿列舉成員名當
                //    InternalName，提供端的 addTransientStrategy 再用 ordinal == 去找那條軌道。
                //    大小寫不符 ⇒ 找不到軌道 ⇒ 回 false 而且**完全靜默**，整筆設定被丟掉，
                //    StayCloseToTarget 就一直停在預設值 0（＝哨兵值「貼著受擊框 ±1」）。
                bool active  = Presets_AddTransientStrategy("AutoDuty",         "BossMod.Autorotation.MiscAI.StayCloseToTarget", "Range", value);
                bool passive = Presets_AddTransientStrategy("AutoDuty Passive", "BossMod.Autorotation.MiscAI.StayCloseToTarget", "Range", value);

                string report = $"{value}|{active}|{passive}";
                if (report != _lastRangeReport)
                {
                    _lastRangeReport = report;

                    if (!IsEnabled)
                        Svc.Log.Information($"BMR StayCloseToTarget Range={value}：BossMod／BossModReborn 沒有啟用，這次沒有送出。");
                    else if (active || passive)
                        Svc.Log.Information($"BMR StayCloseToTarget Range={value} 已送出（AutoDuty={active}、AutoDuty Passive={passive}）。");
                    else
                        Svc.Log.Information($"BMR StayCloseToTarget Range={value} 沒有生效：兩個 preset 都不接受這條軌道（軌道名或模組不存在）。");
                }
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

        /// <summary>上一次真的送出去的 Role 值,避免每幀重送同一個值洗 IPC 與 log。</summary>
        private static string _lastPartyRoleSent = string.Empty;

        /// <summary>快取歸零。切換副本／停止時呼叫,免得跨副本沿用上一場的判斷。</summary>
        public static void ResetStayCloseToTankCache() => _lastPartyRoleSent = string.Empty;

        /// <summary>
        /// 送出 BossMod MiscAI 的 StayCloseToPartyRole「跟著坦克走」軌道。
        /// </summary>
        /// <remarks>
        /// 🔴 <c>Configuration.PartyCoherency</c> 關著時一律送 <c>None</c>(＝BossMod 該軌道的預設值),
        /// 所以「功能沒開」與「沒有這個功能」在 BossMod 端是同一個狀態,而且使用者中途關掉時
        /// 會自己歸位,不會留下上一次設好的 Tank。
        /// ⚠️ 軌道名 <c>Role</c> 是大寫 R(BossModReborn 的 <c>Tracks.Role</c> 直接拿列舉成員名當
        /// InternalName),大小寫不符會靜默回 false、整筆被丟掉 —— 與 SetRange 那條同一個坑。
        /// </remarks>
        public static void StayCloseToTank(bool close)
        {
            if (!Plugin.Configuration.AutoManageBossModAISettings)
                return;

            string role = close && Plugin.Configuration.PartyCoherency ? nameof(Data.Enums.Role.Tank) : "None";

            if (role == _lastPartyRoleSent)
                return;

            bool active  = Presets_AddTransientStrategy("AutoDuty",         "BossMod.Autorotation.MiscAI.StayCloseToPartyRole", "Role", role);
            bool passive = Presets_AddTransientStrategy("AutoDuty Passive", "BossMod.Autorotation.MiscAI.StayCloseToPartyRole", "Role", role);

            _lastPartyRoleSent = role;

            if (!IsEnabled)
                Svc.Log.Information($"BMR StayCloseToPartyRole Role={role}:BossMod／BossModReborn 沒有啟用,這次沒有送出。");
            else if (active || passive)
                Svc.Log.Information($"BMR StayCloseToPartyRole Role={role} 已送出(AutoDuty={active}、AutoDuty Passive={passive})。");
            else
                Svc.Log.Information($"BMR StayCloseToPartyRole Role={role} 沒有生效:兩個 preset 都不接受這條軌道(軌道名或模組不存在)。");
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
}
