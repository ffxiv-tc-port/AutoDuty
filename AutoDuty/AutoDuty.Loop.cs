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
    public void Run(RunContext ctx)
    {
        if (ctx == null)
            return;

        if (ctx.Duty == null)
            return;

        // Mutual exclusion (policy 3): forbid switching between Planner and Main while running.
        if (States.HasFlag(PluginState.Looping) && ActiveRunContext != null)
        {
            var runningPlanner = ActiveRunContext.Source == RunSource.Planner;
            var startingPlanner = ctx.Source == RunSource.Planner;
            if (runningPlanner != startingPlanner)
            {
                MainWindow.ShowPopup("Mode".Loc(), runningPlanner
                    ? "Planner is running. Stop it first.".Loc()
                    : "AutoDuty is running. Stop it first.".Loc());
                return;
            }
        }

        ActiveRunContext = ctx;

        CurrentTerritoryContent = ctx.Duty;
        if (ctx.PathIndex >= 0)
            CurrentPath = ctx.PathIndex;

        if (ctx.PersistLoopsToConfig && ctx.Loops > 0)
            Configuration.LoopTimes = ctx.Loops;

        // Reuse the existing legacy Run() pipeline to avoid duplicating logic.
        Run(0, 0, ctx.StartFromZero, ctx.BareMode);
    }

    private void TrustLeveling()
    {
        if (TrustLevelingEnabled && TrustHelper.Members.Any(tm => tm.Value.Level < tm.Value.LevelCap))
        {
            TaskManager.Enqueue(() => Svc.Log.Debug($"Trust Leveling Enabled"), "TrustLeveling-Debug");
            TaskManager.Enqueue(() => TrustHelper.ClearCachedLevels(CurrentTerritoryContent!), "TrustLeveling-ClearCachedLevels");
            TaskManager.Enqueue(() => TrustHelper.GetLevels(CurrentTerritoryContent), "TrustLeveling-GetLevels");
            TaskManager.DelayNext(50);
            TaskManager.Enqueue(() => TrustHelper.State != ActionState.Running, "TrustLeveling-RecheckingTrustLevels");
        }
    }

    public void Run(uint territoryType = 0, int loops = 0, bool startFromZero = true, bool bareMode = false)
    {
        Svc.Log.Debug($"Run: territoryType={territoryType} loops={loops} bareMode={bareMode}");
        if (territoryType > 0)
        {
            if (ContentHelper.DictionaryContent.TryGetValue(territoryType, out var content))
                CurrentTerritoryContent = content;
            else
            {
                Svc.Log.Error($"({territoryType}) is not in our Dictionary as a compatible Duty");
                return;
            }
        }

        if (CurrentTerritoryContent == null)
            return;

        // 這是 Run(RunContext) 也會匯流進來的唯一入口,而且過了上面那幾道退出檢查才算真的要跑。
        // 每開新的一輪就把「本次執行」的逾時計數歸零(工作階段總數不歸零,tooltip 仍看得到)。
        TaskTimeoutWatcher.OnRunStarted();
        _deathsThisDuty = 0;

        // Legacy entrypoint: infer source only when no explicit RunContext was provided.
        ActiveRunContext ??= new RunContext
        {
            Source = territoryType > 0 ? RunSource.Command : RunSource.Manual,
            Duty = CurrentTerritoryContent,
            PathIndex = CurrentPath,
            Loops = 0,
            StartFromZero = startFromZero,
            BareMode = bareMode,
            PlannerItemIndex = -1,
            PersistLoopsToConfig = false,
        };

        // Preserve legacy behavior: only persist loop times from the legacy Run() path
        // when planner is not active.
        if (!PlannerEnabled && loops > 0)
            Configuration.LoopTimes = loops;

        if (bareMode)
        {
            _bareModeSettingsActive |= SettingsActive.BareMode_Active;
            if (Configuration.EnablePreLoopActions)
                _bareModeSettingsActive |= SettingsActive.PreLoop_Enabled;
            if (Configuration.EnableBetweenLoopActions)
                _bareModeSettingsActive |= SettingsActive.BetweenLoop_Enabled;
            if (Configuration.EnableTerminationActions)
                _bareModeSettingsActive |= SettingsActive.TerminationActions_Enabled;
            // 先登記再改：途中任何存檔都寫使用者原本的值，遊戲在 Stop 前結束也不會把三項留成關。
            foreach (string name in BareModeFieldNames)
                ConfigOverrideHelper.RegisterInternal(typeof(Configuration).GetField(name)!, Configuration);
            Configuration.EnablePreLoopActions = false;
            Configuration.EnableBetweenLoopActions = false;
            Configuration.EnableTerminationActions = false;
        }

        Svc.Log.Info($"Running AutoDuty in {CurrentTerritoryContent.EnglishName}, Looping {GetEffectiveLoopTimes()} times{(bareMode ? " in BareMode (No Pre, Between or Termination Loop Actions)" : "")}");

        //MainWindow.OpenTab("Mini");
        if (Configuration.ShowOverlay)
        {
            //MainWindow.IsOpen = false;
            Overlay.IsOpen = true;
        }
        Stage = Stage.Looping;
        States |= PluginState.Looping;
        SetGeneralSettings(false);
        // 刻意不呼叫 Path_SetMovementAllowed(true):Get 回的是租約生效後的值,寫 true 壓不過租約,
        // 只會蓋掉使用者手動取消的「Allow movement」;AutoDuty 自己從不關它,所以沒有要開回來的帳。
        if (VNavmesh_IPCSubscriber.IsEnabled && !VNavmesh_IPCSubscriber.Path_GetMovementAllowed())
            Svc.Log.Information("vnavmesh 目前不允許移動(使用者在 Navmesh manager 取消了 Allow movement,或有插件持移動租約壓住);AutoDuty 不會替你改這個設定,路徑照算但角色會停在原地,誰在壓請看 vnavmesh 的 log");
        TaskManager.Abort();
        Svc.Log.Info($"Running {CurrentTerritoryContent.Name} {GetEffectiveLoopTimes()} Times");
        if (!InDungeon)
        {
            CurrentLoop = 0;
            if (Configuration.EnablePreLoopActions)
            {
                if (Configuration.ExecuteCommandsPreLoop)
                {
                    TaskManager.Enqueue(() => Svc.Log.Debug($"ExecutingCommandsPreLoop, executing {Configuration.CustomCommandsTermination.Count} commands"));
                    Configuration.CustomCommandsPreLoop.Each(x => TaskManager.Enqueue(() => Chat.ExecuteCommand(x), "Run-ExecuteCommandsPreLoop"));
                }

                AutoConsume();

                if (LevelingModeEnum == LevelingMode.None)
                    AutoEquipRecommendedGear();

                if (Configuration.AutoRepair && InventoryHelper.CanRepair())
                {
                    TaskManager.Enqueue(() => Svc.Log.Debug($"AutoRepair PreLoop Action"));
                    TaskManager.Enqueue(() => RepairHelper.Invoke(), "Run-AutoRepair");
                    TaskManager.DelayNext("Run-AutoRepairDelay50", 50);
                    TaskManager.Enqueue(() => RepairHelper.State != ActionState.Running, int.MaxValue, "Run-WaitAutoRepairComplete");
                    TaskManager.Enqueue(() => PlayerHelper.IsReadyFull, "Run-WaitAutoRepairIsReadyFull");
                }

                if (Configuration.DutyModeEnum != DutyMode.Squadron && Configuration.RetireMode)
                {
                    TaskManager.Enqueue(() => Svc.Log.Debug($"Retire PreLoop Action"));
                    if (Configuration.RetireLocationEnum == RetireLocation.GC_Barracks)
                        TaskManager.Enqueue(() => GotoBarracksHelper.Invoke(), "Run-GotoBarracksInvoke");
                    else if (Configuration.RetireLocationEnum == RetireLocation.Inn)
                        TaskManager.Enqueue(() => GotoInnHelper.Invoke(), "Run-GotoInnInvoke");
                    else
                        TaskManager.Enqueue(() => GotoHousingHelper.Invoke((Housing)Configuration.RetireLocationEnum), "Run-GotoHousingInvoke");
                    TaskManager.DelayNext("Run-RetireModeDelay50", 50);
                    TaskManager.Enqueue(() => GotoHousingHelper.State != ActionState.Running && GotoBarracksHelper.State != ActionState.Running && GotoInnHelper.State != ActionState.Running, int.MaxValue, "Run-WaitGotoComplete");
                }
            }

            TaskManager.Enqueue(() => Svc.Log.Debug($"Queueing First Run"));
            Queue(CurrentTerritoryContent);
        }
        TaskManager.Enqueue(() => Svc.Log.Debug($"Done Queueing-WaitDutyStarted, NavIsReady"));
        TaskManager.Enqueue(() => Svc.DutyState.IsDutyStarted,          "Run-WaitDutyStarted");
        TaskManager.Enqueue(WaitForNavReady(), int.MaxValue, "Run-WaitNavIsReady");
        TaskManager.Enqueue(() => Svc.Log.Debug($"Start Navigation"));
        TaskManager.Enqueue(() => StartNavigation(startFromZero), "Run-StartNavigation");
        if (CurrentLoop == 0)
            CurrentLoop = 1;
    }

    internal unsafe void LoopTasks(bool queue = true)
    {
        if (CurrentTerritoryContent == null) return;

        // IMPORTANT: queue=false is used to run completion actions (LoopsCompleteActions).
        // Do not apply planner duty selection on that path, or it can interfere with termination/between-loop actions.
        if (queue && ActiveRunContext?.Source == RunSource.Planner && !Configuration.PlannerPaused && !PlannerTryApplyCurrentSelection(resetLoopCounter: false))
        {
            MarkSelfStop("Planner could not apply the next duty.");
            Stage = Stage.Stopped;
            return;
        }

        if (Configuration.EnableBetweenLoopActions)
        {
            if (Configuration.ExecuteCommandsBetweenLoop)
            {
                TaskManager.Enqueue(() => Svc.Log.Debug($"ExecutingCommandsBetweenLoops, executing {Configuration.CustomCommandsBetweenLoop.Count} commands"));
                Configuration.CustomCommandsBetweenLoop.Each(x => Chat.ExecuteCommand(x));
                TaskManager.DelayNext("Loop-DelayAfterCommands", 1000);
            }

            if (Configuration.AutoOpenCoffers) 
                EnqueueActiveHelper<CofferHelper>();

            if (Configuration.EnableAutoRetainer && AutoRetainer_IPCSubscriber.IsEnabled && AutoRetainer_IPCSubscriber.AreAnyRetainersAvailableForCurrentChara())
            {
                TaskManager.Enqueue(() => Svc.Log.Debug($"AutoRetainer BetweenLoop Actions"));
                TaskManager.Enqueue(() => AutoRetainerHelper.Invoke(), "Loop-AutoRetainer");
                TaskManager.DelayNext("Loop-Delay50", 50);
                TaskManager.Enqueue(() => AutoRetainerHelper.State != ActionState.Running, int.MaxValue, "Loop-WaitAutoRetainerComplete");
            }

            AutoConsume();

            AutoEquipRecommendedGear();

            if (Configuration.AutoRepair && InventoryHelper.CanRepair()) 
                EnqueueActiveHelper<RepairHelper>();

            if (Configuration.AutoExtract && QuestManager.IsQuestComplete(66174)) 
                EnqueueActiveHelper<ExtractHelper>();

            if (Configuration.AutoDesynth) 
                EnqueueActiveHelper<DesynthHelper>();

            if (Configuration.AutoGCTurnin && (!Configuration.AutoGCTurninSlotsLeftBool || InventoryManager.Instance()->GetEmptySlotsInBag() <= Configuration.AutoGCTurninSlotsLeft) && PlayerHelper.GetGrandCompanyRank() > 5)
                EnqueueActiveHelper<GCTurninHelper>();

            if (Configuration.TripleTriadEnabled)
            {
                if (Configuration.TripleTriadRegister) 
                    EnqueueActiveHelper<TripleTriadCardUseHelper>();
                if (Configuration.TripleTriadSell) 
                    EnqueueActiveHelper<TripleTriadCardSellHelper>();
            }

            if (Configuration.DiscardItems) 
                EnqueueActiveHelper<DiscardHelper>();

            if (Configuration.DutyModeEnum != DutyMode.Squadron && Configuration.RetireMode)
            {
                TaskManager.Enqueue(() => Svc.Log.Debug($"Retire Between Loop Action"));
                if (Configuration.RetireLocationEnum == RetireLocation.GC_Barracks)
                    TaskManager.Enqueue(() => GotoBarracksHelper.Invoke(), "Loop-GotoBarracksInvoke");
                else if (Configuration.RetireLocationEnum == RetireLocation.Inn)
                    TaskManager.Enqueue(() => GotoInnHelper.Invoke(), "Loop-GotoInnInvoke");
                else
                {
                    Svc.Log.Info($"{(Housing)Configuration.RetireLocationEnum} {Configuration.RetireLocationEnum}");
                    TaskManager.Enqueue(() => GotoHousingHelper.Invoke((Housing)Configuration.RetireLocationEnum), "Loop-GotoHousingInvoke");
                }
                TaskManager.DelayNext("Loop-Delay50", 50);
                TaskManager.Enqueue(() => GotoHousingHelper.State != ActionState.Running && GotoBarracksHelper.State != ActionState.Running && GotoInnHelper.State != ActionState.Running, int.MaxValue, "Loop-WaitGotoComplete");
            }
        }

        void EnqueueActiveHelper<T>() where T : ActiveHelperBase<T>, new()
        {
            TaskManager.Enqueue(() => Svc.Log.Debug($"Enqueueing {typeof(T).Name}"), "Loop-ActiveHelper");
            TaskManager.Enqueue(() => ActiveHelperBase<T>.Invoke(), $"Loop-{typeof(T).Name}");
            TaskManager.DelayNext("Loop-Delay50", 50);
            TaskManager.Enqueue(() => ActiveHelperBase<T>.State != ActionState.Running, int.MaxValue, $"Loop-Wait-{typeof(T).Name}-Complete");
            TaskManager.Enqueue(() => PlayerHelper.IsReadyFull, "Loop-WaitIsReadyFull");
        }


        if (!queue)
        {
            LoopsCompleteActions();
            return;
        }

        if (LevelingEnabled)
        {
            Svc.Log.Info("Leveling Enabled");
            Content? duty = LevelingHelper.SelectHighestLevelingRelevantDuty(LevelingModeEnum == LevelingMode.Trust);
            if (duty != null)
            {
                if (this.LevelingModeEnum == LevelingMode.Support && Configuration.PreferTrustOverSupportLeveling && duty.ClassJobLevelRequired > 70)
                {
                    levelingModeEnum           = LevelingMode.Trust;
                    Configuration.dutyModeEnum = DutyMode.Trust;

                    Content? dutyTrust = LevelingHelper.SelectHighestLevelingRelevantDuty(true);

                    if (duty != dutyTrust)
                    {
                        levelingModeEnum           = LevelingMode.Support;
                        Configuration.dutyModeEnum = DutyMode.Support;
                    }
                }

                Svc.Log.Info("Next Leveling Duty: " + duty.Name);
                CurrentTerritoryContent = duty;
                ContentPathsManager.DictionaryPaths[duty.TerritoryType].SelectPath(out CurrentPath);
            }
            else
            {
                CurrentLoop = GetEffectiveLoopTimes();
                LoopsCompleteActions();
                return;
            }
        }
        TaskManager.Enqueue(() => Svc.Log.Debug($"Registering New Loop"));
        Queue(CurrentTerritoryContent);
        TaskManager.Enqueue(() => Svc.Log.Debug($"Incrementing LoopCount, Setting Action Var, Wait for CorrectTerritory, PlayerIsValid, DutyStarted, and NavIsReady"));
        TaskManager.Enqueue(() => CurrentLoop++, "Loop-IncrementCurrentLoop");
        TaskManager.Enqueue(() => { Action = $"Looping: {CurrentTerritoryContent.Name} {CurrentLoop} of {GetEffectiveLoopTimes()}"; }, "Loop-SetAction");
        TaskManager.Enqueue(() => Svc.ClientState.TerritoryType == CurrentTerritoryContent.TerritoryType, int.MaxValue, "Loop-WaitCorrectTerritory");
        TaskManager.Enqueue(() => PlayerHelper.IsValid, int.MaxValue, "Loop-WaitPlayerValid");
        TaskManager.Enqueue(() => Svc.DutyState.IsDutyStarted, int.MaxValue, "Loop-WaitDutyStarted");
        TaskManager.Enqueue(WaitForNavReady(), int.MaxValue, "Loop-WaitNavReady");
        TaskManager.Enqueue(() => Svc.Log.Debug($"StartNavigation"));
        TaskManager.Enqueue(() => StartNavigation(true), "Loop-StartNavigation");
    }

    private void LoopsCompleteActions()
    {

        SetGeneralSettings(false);

        if (Configuration.EnableTerminationActions)
        {
            TaskManager.Enqueue(() => PlayerHelper.IsReadyFull);
            TaskManager.Enqueue(() => Svc.Log.Debug($"TerminationActions are Enabled"));
            if (Configuration.ExecuteCommandsTermination)
            {
                TaskManager.Enqueue(() => Svc.Log.Debug($"ExecutingCommandsTermination, executing {Configuration.CustomCommandsTermination.Count} commands"));
                Configuration.CustomCommandsTermination.Each(x => Chat.ExecuteCommand(x));
            }

            if (Configuration.PlayEndSound)
            {
                TaskManager.Enqueue(() => Svc.Log.Debug($"Playing End Sound"));
                SoundHelper.StartSound(Configuration.PlayEndSound, Configuration.CustomSound, Configuration.SoundEnum);
            }

            if (Configuration.TerminationMethodEnum == TerminationMode.Kill_PC)
            {
                TaskManager.Enqueue(() => Svc.Log.Debug($"Killing PC"));
                if (!Configuration.TerminationKeepActive)
                {
                    Configuration.TerminationMethodEnum = TerminationMode.Do_Nothing;
                    Configuration.Save();
                }
                TaskManager.Enqueue(() =>
                                    {
                                        if (OperatingSystem.IsWindows())
                                        {
                                            ProcessStartInfo startinfo = new("shutdown.exe", "-s -t 20");
                                            Process.Start(startinfo);
                                        }
                                        else if (OperatingSystem.IsLinux())
                                        {
                                            //Educated guess
                                            ProcessStartInfo startinfo = new("shutdown", "-t 20");
                                            Process.Start(startinfo);
                                        }
                                        else if (OperatingSystem.IsMacOS())
                                        {
                                            //hell if I know
                                        }
                                    }, "Enqueuing SystemShutdown");
                TaskManager.Enqueue(() => Chat.ExecuteCommand($"/xlkill"), "Killing the game");
            }
            else if (Configuration.TerminationMethodEnum == TerminationMode.Kill_Client)
            {
                TaskManager.Enqueue(() => Svc.Log.Debug($"Killing Client"));
                if (!Configuration.TerminationKeepActive)
                {
                    Configuration.TerminationMethodEnum = TerminationMode.Do_Nothing;
                    Configuration.Save();
                }

                TaskManager.Enqueue(() => Chat.ExecuteCommand($"/xlkill"), "Killing the game");
            }
            else if (Configuration.TerminationMethodEnum == TerminationMode.Logout)
            {
                TaskManager.Enqueue(() => Svc.Log.Debug($"Logging Out"));
                if (!Configuration.TerminationKeepActive)
                {
                    Configuration.TerminationMethodEnum = TerminationMode.Do_Nothing;
                    Configuration.Save();
                }

                TaskManager.Enqueue(() => PlayerHelper.IsReady);
                TaskManager.DelayNext(2000);
                TaskManager.Enqueue(() => Chat.ExecuteCommand($"/logout"));
                TaskManager.Enqueue(() => AddonHelper.ClickSelectYesno());
            }
            else if (Configuration.TerminationMethodEnum == TerminationMode.Start_AR_Multi_Mode)
            {
                TaskManager.Enqueue(() => Svc.Log.Debug($"Starting AR Multi Mode"));
                TaskManager.Enqueue(() => Chat.ExecuteCommand($"/ays multi e"));
            }
        }

        Svc.Log.Debug($"Removing Looping, Setting CurrentLoop to 0, and Setting Stage to Stopped");

        States      &= ~PluginState.Looping;
        CurrentLoop =  0;
        // 這一停是排程延遲執行的,所以原因必須跟賦值寫在同一個 lambda 裡 ——
        // 旗標的存活期才不會被中間插進來的別的停止動作吃掉。
        TaskManager.Enqueue(() => SchedulerHelper.ScheduleAction("SetStageStopped", () =>
        {
            MarkSelfStop("All loops finished.", Configuration.EnableTerminationActions && Configuration.PlayEndSound);
            Stage = Stage.Stopped;
        }, 1));
    }

    private void AutoEquipRecommendedGear()
    {
        if (Configuration.AutoEquipRecommendedGear)
        {
            TaskManager.Enqueue(() => Svc.Log.Debug($"AutoEquipRecommendedGear Between Loop Action"));
            TaskManager.Enqueue(() => AutoEquipHelper.Invoke(), "AutoEquipRecommendedGear-Invoke");
            TaskManager.DelayNext("AutoEquipRecommendedGear-Delay50", 50);
            TaskManager.Enqueue(() => AutoEquipHelper.State != ActionState.Running, int.MaxValue, "AutoEquipRecommendedGear-WaitAutoEquipComplete");
            TaskManager.Enqueue(() => PlayerHelper.IsReadyFull, "AutoEquipRecommendedGear-WaitANotIsOccupied");
        }
    }

    private void AutoConsume()
    {
        if (Configuration.AutoConsume)
        {
            TaskManager.Enqueue(() => Svc.Log.Debug($"AutoConsume PreLoop Action"));
            Configuration.AutoConsumeItemsList.Each(x =>
            {
                var isAvailable = InventoryHelper.IsItemAvailable(x.Value.ItemId, x.Value.CanBeHq);
                if (isAvailable)
                {
                    if (Configuration.AutoConsumeIgnoreStatus)
                        TaskManager.Enqueue(() => InventoryHelper.UseItemUntilAnimationLock(x.Value.ItemId, x.Value.CanBeHq), $"AutoConsume - {x.Value.Name} is available: {isAvailable}");
                    else
                        TaskManager.Enqueue(() => InventoryHelper.UseItemUntilStatus(x.Value.ItemId, x.Key, Plugin.Configuration.AutoConsumeTime * 60, x.Value.CanBeHq), $"AutoConsume - {x.Value.Name} is available: {isAvailable}");
                }
                TaskManager.DelayNext("AutoConsume-DelayNext50", 50);
                TaskManager.Enqueue(() => PlayerHelper.IsReadyFull, "AutoConsume-WaitPlayerIsReadyFull");
                TaskManager.DelayNext("AutoConsume-DelayNext250", 250);
            });
        }
    }

    private void Queue(Content content)
    {
        if (Configuration.DutyModeEnum == DutyMode.Variant)
            _variantManager.RegisterVariantDuty(content);
        else if (Configuration.DutyModeEnum.EqualsAny(DutyMode.Regular, DutyMode.Trial, DutyMode.Raid, DutyMode.Support, DutyMode.Trust))
        {
            TaskManager.Enqueue(() => QueueHelper.Invoke(content, Configuration.DutyModeEnum), "Queue-Invoke");
            TaskManager.DelayNext("Queue-Delay50", 50);
            TaskManager.Enqueue(() => QueueHelper.State != ActionState.Running, int.MaxValue, "Queue-WaitQueueComplete");
        }
        else if (Configuration.DutyModeEnum == DutyMode.Squadron)
        {
            TaskManager.Enqueue(() => GotoBarracksHelper.Invoke(), "Queue-GotoBarracksInvoke");
            TaskManager.DelayNext("Queue-GotoBarracksDelay50", 50);
            TaskManager.Enqueue(() => GotoBarracksHelper.State != ActionState.Running && GotoInnHelper.State != ActionState.Running, int.MaxValue, "Queue-WaitGotoComplete");
            _squadronManager.RegisterSquadron(content);
        }
        TaskManager.Enqueue(() => !PlayerHelper.IsValid, "Queue-WaitNotValid");
        TaskManager.Enqueue(() => PlayerHelper.IsValid, int.MaxValue, "Queue-WaitValid");
    }
}
