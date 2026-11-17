using System;
using System.Numerics;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using System.Collections.Generic;
using System.IO;
using ECommons;
using ECommons.DalamudServices;
using ECommons.LanguageHelpers;
using AutoDuty.Windows;
using AutoDuty.IPC;
using AutoDuty.External;
using AutoDuty.Helpers;
using ECommons.Throttlers;
using Dalamud.Game.ClientState.Objects.Types;
using System.Linq;
using ECommons.GameFunctions;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Client.Game;
using Dalamud.Bindings.ImGui;
using ECommons.ExcelServices;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Dalamud.IoC;
using System.Diagnostics;
using Dalamud.Game.ClientState.Conditions;
using AutoDuty.Properties;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AutoDuty.Updater;

namespace AutoDuty;

using System.Text.RegularExpressions;
using Dalamud.Utility.Numerics;
using Data;
using ECommons.Configuration;
using ECommons.SimpleGui;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using Lumina.Excel.Sheets;
using Pictomancy;
using static Data.Classes;
using TaskManager = ECommons.Automation.LegacyTaskManager.TaskManager;

public sealed partial class AutoDuty
{
    /// <summary>
    /// 目前這一步能不能按「跳過」。
    /// </summary>
    /// <remarks>
    /// 只在「真的有一個路徑步驟正在跑」時成立。刻意排除:
    /// <c>Stage.Paused</c>(暫停會把 TaskManager 切成單步模式並記下 PreviousStage,
    /// 跳過要中止佇列並換 Stage,兩者疊起來會讓「繼續」回到一個已經不存在的狀態 —— 請先按繼續)、
    /// <c>Stage.Condition</c>(換區中,有一個排程動作稍後會把 Stage 拉回 Reading_Path,
    /// 這時跳過會被那個排程覆蓋掉)、<c>Stage.Dead</c>/<c>Stage.Revived</c>/<c>Stage.Looping</c>/
    /// <c>Stage.Stopped</c>(當下根本沒有正在執行的路徑步驟)。
    /// </remarks>
    internal bool CanSkipCurrentStep =>
        States.HasFlag(PluginState.Navigating) &&
        Indexer >= 0 && Indexer < Actions.Count &&
        // 主視窗與疊加層每幀都讀：用 is 樣式，EqualsAny 每次呼叫都配置 params 陣列、閉包與委派。
        Stage is Stage.Reading_Path or Stage.Moving or Stage.Action or Stage.Waiting_For_Combat;

    /// <summary>
    /// 目前正在跑的那一步是不是「Wait」而且真的在計時中。是的話回傳原本設定的毫秒數與已經等掉的毫秒數。
    /// </summary>
    /// <remarks>
    /// 判「正在等」看的是 <c>ActionsManager.Wait</c> 自己記的計時,不是 <c>Plugin.Action</c> 的前綴 ——
    /// StopForCombat 開著時,佇列前面那個「等脫戰」任務執行期間 Action 已經是 "Wait: N" 了,
    /// 但 throttle 還沒起算,那段時間不算等待時間。
    /// 另外要求記下的時長與步驟參數相同,免得把動作內部自己排的短等待(例如 BossLoot 的 250ms)
    /// 誤認成這一步的等待。
    /// </remarks>
    internal bool TryGetCurrentWaitProgress(out int configuredMs, out int elapsedMs)
    {
        configuredMs = 0;
        elapsedMs    = 0;

        if (Indexer < 0 || Indexer >= Actions.Count)
            return false;

        PathAction step = Actions[Indexer];

        if (!step.Name.Equals("Wait", StringComparison.OrdinalIgnoreCase) || step.Arguments.Count == 0)
            return false;

        if (!int.TryParse(step.Arguments[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out configuredMs) || configuredMs <= 0)
        {
            configuredMs = 0;
            return false;
        }

        int duration = WaitStepDurationMs;

        if (duration <= 0 || duration != configuredMs)
            return false;

        long remaining = WaitStepEndTick - Environment.TickCount64;

        if (remaining <= 0 || remaining > duration)
            return false;

        elapsedMs = (int)(duration - remaining);
        return true;
    }

    /// <summary>
    /// 跳過正在執行的那一步,直接進入下一步。
    /// 跳過的是計時中的 Wait 步驟時,順便把已經等掉的時間寫回路徑檔。
    /// </summary>
    /// <remarks>
    /// 只動「這一步」的執行狀態:中止排隊中的任務、停掉導航、<see cref="Indexer"/> 加一、
    /// 回到 <c>Stage.Reading_Path</c>。迴圈計數(<c>CurrentLoop</c>)、<see cref="States"/>、
    /// 設定、已載入的路徑一律不動。
    /// </remarks>
    internal void SkipCurrentStep()
    {
        if (!CanSkipCurrentStep)
        {
            Svc.Log.Information($"[跳過步驟] 目前不在可跳過的狀態(Stage={Stage.ToCustomString()}, Indexer={Indexer}, Actions={Actions.Count}),不做任何事。");
            return;
        }

        int        skippedIndex = Indexer;
        PathAction step         = Actions[skippedIndex];
        var        stageBefore  = Stage;

        // 🔴 已等時間必須在做任何清理之前量 —— 清掉計時之後就量不到了。
        bool waitInProgress = TryGetCurrentWaitProgress(out int configuredMs, out int elapsedMs);

        switch (stageBefore)
        {
            case Stage.Action:
                // 對照 StageAction() 正常走完一步的收尾,再補兩件 Abort() 會弄丟的事:
                // (1) ForceAttack 可能正暫停著 BossMod 的「自動攻擊管理」,負責還原的任務排在佇列尾端;
                // (2) 動作自己發起的導航(MoveToObject / Interactable / JumpTo …)還在跑,不停掉的話
                //     StageReadingPath 會因為 Path_IsRunning() 而不肯開始下一步 —— 表現成「按了跳過卻卡住」。
                TaskManager.Abort();
                _actions?.RestoreBossModAutoAutos();
                BossMod_IPCSubscriber.DisableRealAIPreset();
                StopNavigationIfRunning();
                break;
            case Stage.Moving:
                // 對照 StageMoving() 卡住重走的分支:停導航後回 Reading_Path。
                StopNavigationIfRunning();
                break;
            case Stage.Waiting_For_Combat:
                // 對照 StageWaitingForCombat() 脫戰後的收尾。
                BossMod_IPCSubscriber.SetRange(Configuration.MaxDistanceToTargetFloat);
                BossMod_IPCSubscriber.DisableRealAIPreset();
                StopNavigationIfRunning();
                break;
            case Stage.Reading_Path:
                // 這一步還沒開始跑,沒有東西要清。
                break;
        }

        // EzThrottler 的 "Wait" 是全域且永久的 key。被我們 Abort() 中斷之後,尚未到期的殘值會留到
        // 下一個 Wait 步驟 —— 那一步的 Throttle 會先回 false、被反覆重試,等於多等掉殘餘的時間。
        // 這是我們自己造成的,所以自己清掉。
        if (step.Name.Equals("Wait", StringComparison.OrdinalIgnoreCase))
            EzThrottler.Reset("Wait");

        ClearWaitStepTiming();
        StuckEscalationHelper.Reset();

        PathAction = new();
        Action     = "";

        Indexer = skippedIndex + 1;
        Stage   = Stage.Reading_Path;

        Svc.Log.Information($"[跳過步驟] 已跳過第 {skippedIndex} 步「{step.Name}」(Stage={stageBefore.ToCustomString()}),Indexer -> {Indexer}。");

        if (waitInProgress)
            StepSkipHelper.WriteBackWaitTime(skippedIndex, step, configuredMs, elapsedMs);
        else
            Svc.Chat.Print($"已跳過目前步驟：{step.Name}", "AutoDuty");
    }

    /// <summary>
    /// 只停 AutoDuty 自己送的導航(中止／跳過「這一步」不是使用者要全部停),vnavmesh 沒載入就什麼都不做。
    /// 舊版 vnavmesh 沒有帶擁有者的端點時退回原本的「有在走或在算才取消全部尋路並停路徑」。
    /// </summary>
    private static void StopNavigationIfRunning()
    {
        if (!VNavmesh_IPCSubscriber.IsEnabled)
            return;

        VNavmesh_IPCSubscriber.StopOwnNavigation();
    }

    /// <summary>路徑改過的設定寫回原值（只改記憶體）；回 true＝有改回、呼叫端要存檔。不在框架執行緒上時排到框架執行緒自己存。</summary>
    private bool RestorePathConfigInMemory()
    {
        if (Svc.Framework.IsInFrameworkUpdateThread)
            return PathConfigJournal.RestoreInMemory() > 0;

        if (Svc.Framework.IsFrameworkUnloading)
        {
            int pending = PathConfigJournal.PendingCount;
            if (pending > 0)
                Svc.Log.Warning($"[路徑改設定] 遊戲正在關閉且不在框架執行緒上，略過還原路徑改過的 {pending} 項設定");
            return false;
        }

        _ = Svc.Framework.Run(() =>
        {
            if (PathConfigJournal.RestoreInMemory() > 0)
                Configuration.Save();
        });
        return false;
    }

    private void StopAndResetALL()
    {
        // 放最前面：後面任何一步擲例外，停下之前排的延遲回呼（OnDeath、傳送後改 Stage、復活後找捷徑）也都已失效。
        StopGeneration++;
        DeathHelper.OnAutomationStopped();
        ClearWaitStepTiming();
        StuckEscalationHelper.Reset();
        BossMod_IPCSubscriber.ResetStayCloseToTankCache();
        // 別的外掛透過 IPC 壓上來的設定覆寫,在停止時一律還原(呼叫端不必記得 Pop)。
        ConfigOverrideHelper.Pop();
        // 順序：Pop 之後（覆寫值不被當原值）、Bare 還原之前（Bare 三旗標以使用者值為準），存檔放在 Bare 還原之後（不把 Bare 暫值寫進檔）。
        bool pathConfigRestored = RestorePathConfigInMemory();
        if (_bareModeSettingsActive != SettingsActive.None)
        {
            Configuration.EnablePreLoopActions = _bareModeSettingsActive.HasFlag(SettingsActive.PreLoop_Enabled);
            Configuration.EnableBetweenLoopActions = _bareModeSettingsActive.HasFlag(SettingsActive.BetweenLoop_Enabled);
            Configuration.EnableTerminationActions = _bareModeSettingsActive.HasFlag(SettingsActive.TerminationActions_Enabled);
            _bareModeSettingsActive = SettingsActive.None;
        }
        ConfigOverrideHelper.ClearInternal();
        if (pathConfigRestored)
            Configuration.Save();
        RestorePandoraChests();
        // 沒在跑時不取消尋路:Nav.PathfindCancelAll 會連別的插件正在算的路一起取消。
        // 只剩 Paused 旗標＝閒置時暫停過,那時也沒有自己的路徑。
        bool ownsNavigation = (States & ~PluginState.Paused) != PluginState.None;
        // 閒置時（急停、卸載、重複 Stop）不碰 Wrath／RSR：關自動輪替要先拿 Wrath 租約，拿不到會把 AutoManageRotationPluginState 寫成 false 並存檔。
        bool rotationMayBeOurs = ownsNavigation || _rotationEngaged;
        States = PluginState.None;
        TaskManager?.SetStepMode(false);
        TaskManager?.Abort();

        // ForceAttack 有可能正暫停著 BossMod 的「自動攻擊管理」,而負責還原的那個任務
        // 剛剛被 TaskManager.Abort() 一起清掉了,所以這裡補一次(沒有暫停中就是 no-op)。
        // ⚠️ 用 ?. 的理由和上面 TaskManager?. 一樣:建構式整段包在 try 裡,_actions 是到
        //    中後段才指派的,建構式提早擲例外時 Dispose 仍會走到這裡。裸呼叫會在這裡再擲一個
        //    NullReferenceException,把真正的載入失敗原因蓋掉。
        _actions?.RestoreBossModAutoAutos();

        if (_unsyncedKeepMovingArmed)
        {
            _unsyncedKeepMovingArmed = false;
            BossMod_IPCSubscriber.SetMovement(true);
        }
        MainListClicked              = false;
        this.Framework_Update_InDuty = _ => {};
        if (!InDungeon)
            CurrentLoop = 0;
        if (Configuration.AutoManageBossModAISettings)
            BossMod_IPCSubscriber.DisablePresets();
        // 途中關掉「自動管理 BossMod AI」時上面那行不會跑,改過的 AI 預設仍要還原(沒改過是空操作)。
        BossMod_IPCSubscriber.RestoreUserAIPreset();
        BossMod_IPCSubscriber.RestoreBmrAIEnabled(null);

        SetGeneralSettings(true);
        if (rotationMayBeOurs && Configuration.AutoManageRotationPluginState && !Configuration.UsingAlternativeRotationPlugin)
            SetRotationPluginSettings(false);
        if (Indexer > 0 && !MainListClicked)
            Indexer = -1;
        if (this.Configuration is { ShowOverlay: true, HideOverlayWhenStopped: true })
            Overlay.IsOpen = false;
        // 放開路徑容許值租約。
        // 🔴 租約模式下只放約(提供端自動還原),只有真的走過舊路徑時才把容許值寫回去:
        //    Path.GetTolerance 回的是「實際生效的值」(租約值 ?? 使用者的值),讀到的可能是
        //    我們自己、甚至是別的外掛押著的租約值,而寫回去的目標卻是使用者那格欄位。
        VNavmesh_IPCSubscriber.Path_ReleaseToleranceRequest();
        FollowHelper.SetFollow(null);

        ActiveRunContext = null;

        // 沒在跑時尋路與路徑都不碰：Dispose 與重複的 Stop 會走到這裡，路徑多半是別的插件的。
        if (ownsNavigation && VNavmesh_IPCSubscriber.IsEnabled)
        {
            VNavmesh_IPCSubscriber.Nav_PathfindCancelAll();
            if (VNavmesh_IPCSubscriber.Path_IsRunning())
                VNavmesh_IPCSubscriber.Path_Stop();
        }

        // 兩者收尾都會呼叫全域 Path.Stop。MapHelper 跑著本身就是自己的旗標導航，另外列入：
        // 它設的 Other 旗標會被同時在跑的 helper 收工時清掉，只看 ownsNavigation 會漏停它。
        bool mapMarkerRunning = MapHelper.State == ActionState.Running;
        if (ownsNavigation || mapMarkerRunning)
        {
            if (mapMarkerRunning)
                MapHelper.StopMoveToMapMarker();

            if (DeathHelper.DeathState == PlayerLifeState.Revived)
                DeathHelper.Stop();
        }

        foreach (IActiveHelper helper in ActiveHelper.activeHelpers) 
            helper.StopIfRunning();


        Wrath_IPCSubscriber.Release();
        Action = "";
    }

    /// <summary>
    /// 標記「接下來這一次 Stage = Stage.Stopped 是 AutoDuty 自己停的」並附上原因。
    /// 只能在非使用者觸發的停止點呼叫。使用者按 Stop、下 /autoduty stop、
    /// 或別的外掛叫 IPC 端點 AutoDuty.Stop 都不可以呼叫 —— 那些不算「自己停了」。
    /// </summary>
    /// <param name="reason">給使用者看的英文原文,顯示時才做 .Loc() 查表。</param>
    /// <param name="endSoundAlreadyQueued">既有的終止流程是不是已經排了完成音效。</param>
    /// <param name="needsHelp">是卡住要人處理（塔塔露念「需要幫忙」），不是正常收工。</param>
    private void MarkSelfStop(string reason, bool endSoundAlreadyQueued = false, bool needsHelp = false)
    {
        _pendingStopReason       = reason;
        _pendingStopSoundHandled = endSoundAlreadyQueued;
        _pendingStopNeedsHelp    = needsHelp;
    }

    /// <summary>
    /// 記下一次死亡；這一場的死亡數到了 <see cref="Configuration.StopAfterDeathsInDuty"/> 就停下 AutoDuty 並回 true。
    /// </summary>
    /// <remarks>
    /// 只從「無法戰鬥」旗標由假轉真的事件進來，事件只在旗標改變時觸發 ⇒ 一次死亡只算一次。
    /// 暫停中或不在副本裡不算：那時是玩家自己在操作。停下後復活確認與接手都交給玩家，這裡不按任何視窗。
    /// </remarks>
    private bool CountDeathAndStopIfLimitReached()
    {
        if (!InDungeon || States.HasFlag(PluginState.Paused))
            return false;

        _deathsThisDuty++;
        int    limit    = Configuration.StopAfterDeathsInDuty;
        string dutyName = CurrentTerritoryContent?.Name ?? Svc.ClientState.TerritoryType.ToString();
        Svc.Log.Information($"[死亡上限] {dutyName}：這一場第 {_deathsThisDuty} 次死亡（門檻 {limit}，0＝不限）。");

        if (limit <= 0 || _deathsThisDuty < limit)
            return false;

        Svc.Log.Information($"[死亡上限] {dutyName}：死亡 {_deathsThisDuty} 次達到門檻 {limit}，停止 AutoDuty，復活與接手交給玩家。");
        // 多開的死亡回報照送：原本由 DeathState 的 setter 送，這條路不會走到那裡。
        global::AutoDuty.Multibox.MultiboxUtility.IsDead(true);
        Svc.Chat.Print("AutoDuty stopped after ?? deaths in ??. Revive and take over yourself; this run will not continue on its own.".Loc(_deathsThisDuty, dutyName), "AutoDuty");
        MarkSelfStop("Stopped after dying too many times in this duty. Revive and take over yourself.", needsHelp: true);
        Stage = Stage.Stopped;
        return true;
    }

    /// <summary>
    /// 在「Stage 真的由非 Stopped 掉到 Stopped」而且那次是 AutoDuty 自己停的時候,發一則通知。
    /// </summary>
    private void NotifyIfAutomationStoppedItself()
    {
        string? reason       = _pendingStopReason;
        bool    soundHandled = _pendingStopSoundHandled;
        bool    needsHelp    = _pendingStopNeedsHelp;
        _pendingStopReason       = null;
        _pendingStopSoundHandled = false;
        _pendingStopNeedsHelp    = false;

        // 使用者主動停:MainWindow 的按鈕、/autoduty stop、IPC Stop 都不會填 _pendingStopReason。
        if (reason == null)
            return;

        // Stage 的 setter 沒有 early-return 守衛:已經是 Stopped 時再賦值一次仍會整段跑一遍。
        // CheckFinishing() 的 else 分支就在每幀路徑上,少了這個邊緣判斷會一直念。
        if (_stage == Stage.Stopped)
            return;

        // 🔴 刻意放在 NotifyWhenStoppedItself 的閘門「之前」：那個旗標是「Dalamud 桌面通知」
        //    的開關而且預設關；語音通知是另一件事、有自己的開關，兩者不該互相牽連。
        // 🔴 IPC 的實作跑在呼叫端的執行緒上 ⇒ 一律丟回主執行緒再叫（已在主執行緒時是同步執行）。
        // 🔴 fail-safe：TataruPraise 沒裝／關著／池裡沒句子都只是安靜的 no-op，絕不影響跑本流程。
        if (Configuration.TataruPraiseOnStoppedItself)
        {
            // 區域副本：流程分析的「非 null」推不進 lambda，直接捕獲 reason 會是 string?。
            string praiseReason   = reason;
            string praiseCategory = needsHelp ? TataruPraiseIPC.CategoryNeedHelp : TataruPraiseIPC.CategoryDutyRunStopped;
            _ = Svc.Framework.RunOnFrameworkThread(() => TataruPraiseIPC.TryPraise(praiseReason, praiseCategory));
        }

        if (!Configuration.NotifyWhenStoppedItself)
            return;

        // 要使用者回報的診斷,事後對照用。
        Svc.Log.Information($"[StopNotify] AutoDuty 自己停了:{reason}(最後動作:{Action})");

        string localizedReason = reason.Loc();
        string headline        = "AutoDuty stopped on its own.".Loc();
        _ = Svc.Framework.RunOnFrameworkThread(() =>
        {
            Svc.NotificationManager.AddNotification(new Notification
            {
                Title           = "AutoDuty",
                Content         = headline + "\n" + localizedReason,
                MinimizedText   = headline,
                Type            = NotificationType.Warning,
                InitialDuration = TimeSpan.FromSeconds(30),
                Minimized       = false,
            });

            // 沿用既有的完成音效,不另外做播放層;正常跑完的路徑已經響過就不重複。
            if (Configuration.PlayEndSound && !soundHandled)
                SoundHelper.StartSound(true, Configuration.CustomSound, Configuration.SoundEnum);
        });
    }
}
