using AutoDuty.Data;
using AutoDuty.Helpers;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using ImGuiNET;
using System;
using System.Linq;
using System.Numerics;

namespace AutoDuty.Windows;

public static class PlannerTab
{
    private static string DutyModeToZh(DutyMode mode)
    {
        return mode switch
        {
            DutyMode.None => "無",
            DutyMode.Support => "劇情輔助器",
            DutyMode.Trust => "親信戰友",
            DutyMode.Squadron => "冒險者小隊",
            DutyMode.Regular => "一般模式(解限)",
            DutyMode.Trial => "討伐",
            DutyMode.Raid => "大型副本",
            DutyMode.Variant => "多變迷宮",
            _ => mode.ToCustomString(),
        };
    }

    public static void Draw()
    {
        if (MainWindow.CurrentTabName != "排程器")
            MainWindow.CurrentTabName = "排程器";

        // Duty Mode selector (required to queue)
        ImGui.TextColored(Plugin.Configuration.DutyModeEnum == DutyMode.None ? new Vector4(1, 0, 0, 1) : new Vector4(0, 1, 0, 1), "選擇任務模式：");
        ImGui.SameLine(0);
        ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.BeginCombo("##PlannerDutyModeEnum", DutyModeToZh(Plugin.Configuration.DutyModeEnum)))
        {
            foreach (DutyMode mode in Enum.GetValues(typeof(DutyMode)))
            {
                if (ImGui.Selectable(DutyModeToZh(mode)))
                {
                    Plugin.Configuration.DutyModeEnum = mode;
                    Plugin.Configuration.Save();
                }
            }
            ImGui.EndCombo();
        }
        ImGui.PopItemWidth();

        ImGui.Separator();

        var canRun = Plugin.Configuration.DutyModeEnum != DutyMode.None && Plugin.Configuration.PlannerItems.Count > 0;
        using (ImRaii.Disabled(Plugin.States.HasFlag(PluginState.Looping) || !canRun))
        {
            if (ImGui.Button("執行排程"))
            {
                if (Plugin.Configuration.DutyModeEnum == DutyMode.None)
                    MainWindow.ShowPopup("錯誤", "請先選擇任務模式。");
                else if (Plugin.Configuration.PlannerItems.Count == 0)
                    MainWindow.ShowPopup("錯誤", "排程清單為空。");
                else if (Svc.Party.PartyId > 0 && (Plugin.Configuration.DutyModeEnum == DutyMode.Support || Plugin.Configuration.DutyModeEnum == DutyMode.Squadron || Plugin.Configuration.DutyModeEnum == DutyMode.Trust))
                    MainWindow.ShowPopup("錯誤", "執行劇情輔助器、冒險者小隊或親信戰友時不可組隊。");
                else if (Plugin.Configuration.DutyModeEnum == DutyMode.Regular && !Plugin.Configuration.Unsynced && !Plugin.Configuration.OverridePartyValidation && Svc.Party.PartyId == 0)
                    MainWindow.ShowPopup("錯誤", "執行一般模式(解限)需要 4 人小隊。");
                else if (Plugin.Configuration.DutyModeEnum == DutyMode.Regular && !Plugin.Configuration.Unsynced && !Plugin.Configuration.OverridePartyValidation && !ObjectHelper.PartyValidation())
                    MainWindow.ShowPopup("錯誤", "執行一般模式(解限)需要正確的職業配置。");
                else
                {
                    var index = Math.Clamp(Plugin.Configuration.PlannerCurrentIndex, 0, Plugin.Configuration.PlannerItems.Count - 1);
                    var territoryType = Plugin.Configuration.PlannerItems[index].TerritoryType;
                    if (ContentPathsManager.DictionaryPaths.ContainsKey(territoryType))
                        Plugin.Run();
                    else
                        MainWindow.ShowPopup("錯誤", "找不到路徑檔案。");
                }
            }
        }

        if (Plugin.States.HasFlag(PluginState.Looping))
        {
            ImGui.SameLine(0, 12);
            MainWindow.StopResumePause();
        }
        else
        {
            ImGui.SameLine(0, 12);
            ImGui.TextDisabled(canRun ? "" : "（請先選擇任務模式並新增排程項目）");
        }

        ImGuiComponents.HelpMarker("可直接從此處開始執行，無需返回主頁。");
        ImGui.Separator();

        ConfigTab.DrawPlannerUi();
    }
}
