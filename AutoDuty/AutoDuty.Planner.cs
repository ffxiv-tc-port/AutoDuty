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
    private bool PlannerEnabled => Configuration.PlannerEnabled && Configuration.PlannerItems.Count > 0;

    private bool PlannerTryGetCurrentItem(out PlannerItem item)
    {
        item = default!;
        if (!PlannerEnabled)
            return false;
        if (Configuration.PlannerCurrentIndex < 0 || Configuration.PlannerCurrentIndex >= Configuration.PlannerItems.Count)
            return false;
        item = Configuration.PlannerItems[Configuration.PlannerCurrentIndex];
        return true;
    }

    internal RunContext? BuildPlannerRunContext(bool startFromZero = true, bool bareMode = false)
    {
        if (!PlannerEnabled)
            return null;

        var index = Math.Clamp(Configuration.PlannerCurrentIndex, 0, Configuration.PlannerItems.Count - 1);
        var item = Configuration.PlannerItems[index];

        if (!ContentHelper.DictionaryContent.TryGetValue(item.TerritoryType, out var content))
            return null;

        var targetRuns = Math.Max(1, item.TargetRuns);
        var completedRuns = Math.Clamp(item.CompletedRuns, 0, targetRuns);
        var loopsRemaining = targetRuns - completedRuns;
        if (loopsRemaining <= 0)
            return null;

        var pathIndex = -1;
        if (ContentPathsManager.DictionaryPaths.TryGetValue(item.TerritoryType, out var container))
        {
            if (!item.PathFileName.IsNullOrEmpty())
            {
                var idx = container.Paths.FindIndex(p => p.FileName.Equals(item.PathFileName, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                    pathIndex = idx;
                else
                    container.SelectPath(out pathIndex);
            }
            else
            {
                container.SelectPath(out pathIndex);
            }
        }

        return new RunContext
        {
            Source = RunSource.Planner,
            Duty = content,
            PathIndex = pathIndex,
            Loops = loopsRemaining,
            StartFromZero = startFromZero,
            BareMode = bareMode,
            PlannerItemIndex = index,
            PersistLoopsToConfig = false,
        };
    }

    internal RunContext? BuildCommandRunContext(uint territoryType, int loops = 0, bool startFromZero = true, bool bareMode = false, RunSource source = RunSource.Command, bool persistLoopsToConfig = true)
    {
        if (territoryType <= 0)
            return null;

        if (!ContentHelper.DictionaryContent.TryGetValue(territoryType, out var content))
            return null;

        var pathIndex = -1;
        if (ContentPathsManager.DictionaryPaths.TryGetValue(territoryType, out var container))
            container.SelectPath(out pathIndex);

        return new RunContext
        {
            Source = source,
            Duty = content,
            PathIndex = pathIndex,
            Loops = loops,
            StartFromZero = startFromZero,
            BareMode = bareMode,
            PlannerItemIndex = -1,
            PersistLoopsToConfig = persistLoopsToConfig,
        };
    }

    private int GetEffectiveLoopTimes()
    {
        if (ActiveRunContext?.Source == RunSource.Planner && PlannerTryGetCurrentItem(out var plannerItem))
            return Math.Max(1, plannerItem.TargetRuns);

        if (ActiveRunContext is { Loops: > 0 })
            return Math.Max(1, ActiveRunContext.Loops);

        // Legacy/UI fallback: when not running, keep displaying planner target runs if planner is active.
        if (ActiveRunContext == null && PlannerTryGetCurrentItem(out var item))
            return Math.Max(1, item.TargetRuns);

        return Math.Max(1, Configuration.LoopTimes);
    }

    private void PlannerResetProgress()
    {
        foreach (var it in Configuration.PlannerItems)
            it.CompletedRuns = 0;
        Configuration.PlannerCurrentIndex = 0;
        Configuration.Save();
    }

    private bool PlannerIsPlanCompleteNoRepeat()
    {
        if (!PlannerEnabled)
            return false;
        if (Configuration.PlannerRepeat)
            return false;
        // complete when every item hit target
        return Configuration.PlannerItems.All(it => it.CompletedRuns >= Math.Max(1, it.TargetRuns));
    }

    private bool PlannerTryApplyCurrentSelection(bool resetLoopCounter)
    {
        if (!PlannerEnabled)
            return true;

        // sanitize targets/progress
        foreach (var it in Configuration.PlannerItems)
        {
            it.TargetRuns = Math.Max(1, it.TargetRuns);
            it.CompletedRuns = Math.Clamp(it.CompletedRuns, 0, it.TargetRuns);
        }
        if (Configuration.PlannerCurrentIndex < 0)
            Configuration.PlannerCurrentIndex = 0;
        if (Configuration.PlannerCurrentIndex >= Configuration.PlannerItems.Count)
            Configuration.PlannerCurrentIndex = Configuration.PlannerItems.Count - 1;

        // If the plan is complete and we're not repeating, do NOT auto-reset.
        // Reset is an explicit user action (button), or happens at the repeat boundary.
        if (PlannerIsPlanCompleteNoRepeat())
        {
            if (resetLoopCounter)
                MainWindow.ShowPopup("排程器", "排程已完成。若要再次執行，請先重置進度或啟用循環執行。");
            return false;
        }

        if (!PlannerTryGetCurrentItem(out var item))
            return false;

        if (!ContentHelper.DictionaryContent.TryGetValue(item.TerritoryType, out var content))
        {
            MainWindow.ShowPopup("排程器", $"任務 ({item.TerritoryType}) 在目前的任務模式下不可用。");
            return false;
        }

        CurrentTerritoryContent = content;
        if (ContentPathsManager.DictionaryPaths.TryGetValue(content.TerritoryType, out var container))
        {
            if (!item.PathFileName.IsNullOrEmpty())
            {
                var index = container.Paths.FindIndex(p => p.FileName.Equals(item.PathFileName, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                    CurrentPath = index;
                else
                    container.SelectPath(out CurrentPath);
            }
            else
            {
                container.SelectPath(out CurrentPath);
            }
        }
        if (resetLoopCounter)
            CurrentLoop = 0;
        return true;
    }

    private void PlannerOnDutyCompleted()
    {
        if (ActiveRunContext?.Source != RunSource.Planner)
            return;

        if (!PlannerEnabled)
            return;

        if (!PlannerTryGetCurrentItem(out var item))
            return;

        // Guard against duplicate DutyCompleted events.
        var now = DateTime.UtcNow;
        if (item.TerritoryType == _plannerLastDutyCompletedTerritoryType && (now - _plannerLastDutyCompletedAtUtc).TotalMilliseconds < 2000)
        {
            Svc.Log.Debug("Planner: ignored duplicate DutyCompleted event.");
            return;
        }
        _plannerLastDutyCompletedTerritoryType = item.TerritoryType;
        _plannerLastDutyCompletedAtUtc = now;

        // Count only successful completions.
        item.TargetRuns = Math.Max(1, item.TargetRuns);
        item.CompletedRuns = Math.Clamp(item.CompletedRuns + 1, 0, item.TargetRuns);

        // Persist progress immediately (requirement: progress persistence).
        Configuration.Save();

        // If planner is paused, do not advance/retarget any further.
        if (Configuration.PlannerPaused)
            return;

        // Still have runs remaining for this duty.
        if (item.CompletedRuns < item.TargetRuns)
            return;

        // Duty finished for this item: advance.
        if (Configuration.PlannerCurrentIndex < Configuration.PlannerItems.Count - 1)
        {
            Configuration.PlannerCurrentIndex++;
            Configuration.Save();
            // reset per-duty loop counter so run-to-run logic treats the next duty as a fresh sequence
            CurrentLoop = 0;

            if (!PlannerTryApplyCurrentSelection(resetLoopCounter: false))
            {
                MarkSelfStop("Planner could not apply the next duty.");
                Stage = Stage.Stopped;
                return;
            }

            Svc.Log.Info($"Planner: advanced to index {Configuration.PlannerCurrentIndex + 1}/{Configuration.PlannerItems.Count} ({CurrentTerritoryContent?.Name}).");
            return;
        }

        // End of plan.
        if (Configuration.PlannerRepeat)
        {
            PlannerResetProgress();
            CurrentLoop = 0;
            PlannerTryApplyCurrentSelection(resetLoopCounter: false);
            Svc.Log.Info("Planner: plan cycle completed, repeating from start.");
        }
        else
        {
            MainWindow.ShowPopup("排程器", "排程已完成。");
            Svc.Log.Info("Planner: plan completed.");
        }
    }
}
