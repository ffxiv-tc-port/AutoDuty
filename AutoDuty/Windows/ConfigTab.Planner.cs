using AutoDuty.Helpers;
using AutoDuty.IPC;
using global::AutoDuty.Multibox;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using ECommons.LanguageHelpers;
using ECommons.MathHelpers;
using FFXIVClientStructs.FFXIV.Client.UI;
using Dalamud.Bindings.ImGui;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using static AutoDuty.Helpers.RepairNPCHelper;
using static AutoDuty.Windows.ConfigTab;

namespace AutoDuty.Windows;

using Data;
using ECommons.Configuration;
using ECommons.ExcelServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using ECommons.UIHelpers.AtkReaderImplementations;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using Properties;
using System.IO;
using System.Numerics;
using System.Text;
using ReflectionHelper = Helpers.ReflectionHelper;
using Vector2 = FFXIVClientStructs.FFXIV.Common.Math.Vector2;

public static partial class ConfigTab
{
    /// <summary>
    /// 排程播放清單列:載入 / 儲存 / 刪除具名排程快照。
    /// </summary>
    /// <remarks>
    /// 🔴 這一段只搬動排程資料,<b>不啟動任何跑本流程</b>。載入之後仍然要由使用者按
    /// 「執行排程」,既有守衛(任務模式、組隊人數、練級互斥、路徑是否存在)一條都沒有繞過,
    /// 也沒有任何「登入自動跑清單」的路徑。
    /// ⚠️ 存的是<b>快照</b>:載入之後再改排程不會回寫已存的清單,要再按一次儲存才會覆蓋。
    /// </remarks>
    private static void DrawPlaylistBar()
    {
        List<Playlist> playlists = ConfigurationMain.Instance.Playlists;

        // 名稱輸入框第一次繪製時帶入目前載入中的清單名,之後就完全交給使用者編輯。
        if (!plannerPlaylistNameInputInit)
        {
            plannerPlaylistNameInputInit = true;
            plannerPlaylistNameInput     = Configuration.PlannerPlaylistName;
        }

        ImGui.TextDisabled("播放清單：");
        ImGui.SameLine();

        // 「沒載入」與「沒有存檔」是兩種不同狀態,列上就要分得出來,不要都畫成空白。
        string comboPreview = playlists.Count == 0                              ? "(尚無已儲存的清單)" :
                              Configuration.PlannerPlaylistName.IsNullOrEmpty() ? "(未載入)" :
                                                                                  Configuration.PlannerPlaylistName;

        ImGui.SetNextItemWidth(220f.Scale());
        using (ImRaii.Disabled(playlists.Count == 0))
        {
            if (ImGui.BeginCombo("##PlannerPlaylistCombo", comboPreview))
            {
                for (int i = 0; i < playlists.Count; i++)
                {
                    Playlist playlist = playlists[i];

                    if (ImGui.Selectable($"{playlist.Name}（{playlist.Entries?.Count ?? 0} 項）##PlannerPlaylistLoad{i}",
                                         playlist.Name.Equals(Configuration.PlannerPlaylistName, StringComparison.OrdinalIgnoreCase)))
                        LoadPlaylist(playlist);
                }

                ImGui.EndCombo();
            }
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            SetTooltipFullAlpha(playlists.Count == 0 ?
                                 "還沒有已儲存的播放清單。在右邊填名稱後按儲存,就會把目前的排程存成一份。" :
                                 "載入已儲存的播放清單:取代目前的排程清單,並把進度歸零以便重跑。\n載入不會開始執行,仍要自己按「執行排程」。");

        ImGui.SameLine(0, 15f);

        ImGui.SetNextItemWidth(160f.Scale());
        ImGui.InputTextWithHint("##PlannerPlaylistName", "清單名稱", ref plannerPlaylistNameInput, 64);

        string trimmedName = plannerPlaylistNameInput.Trim();

        ImGui.SameLine();
        using (ImRaii.Disabled(trimmedName.IsNullOrEmpty() || Configuration.PlannerItems.Count == 0))
            if (ImGuiComponents.IconButton("##PlannerPlaylistSave", FontAwesomeIcon.Save))
                SavePlaylist(trimmedName);

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            SetTooltipFullAlpha("把目前的排程清單存成這個名稱。\n同名的清單會被覆蓋。\n排程清單為空、或沒填名稱時不能按。");

        int existingIndex = playlists.FindIndex(p => p.Name.Equals(trimmedName, StringComparison.OrdinalIgnoreCase));

        ImGui.SameLine();
        using (ImRaii.Disabled(existingIndex < 0 || !ImGui.GetIO().KeyCtrl))
            if (ImGuiComponents.IconButton("##PlannerPlaylistDelete", FontAwesomeIcon.TrashAlt))
                DeletePlaylist(existingIndex);

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            SetTooltipFullAlpha("刪除名稱欄位裡那個已儲存的播放清單。\n按住 Ctrl 才能按。\n只刪存檔,目前的排程清單不受影響。");
    }

    /// <summary>
    /// 載入具名播放清單:取代目前的排程清單並把進度歸零(這就是「重跑」語意)。
    /// </summary>
    private static void LoadPlaylist(Playlist playlist)
    {
        // 存的是快照,載入必須再複製一份,否則之後編輯排程會就地改到存檔內容。
        List<PlannerItem> entries = (playlist.Entries ?? []).JSONClone();

        foreach (PlannerItem entry in entries)
        {
            entry.TargetRuns    = Math.Max(1, entry.TargetRuns);
            entry.CompletedRuns = 0;
        }

        Configuration.PlannerItems        = entries;
        Configuration.PlannerCurrentIndex = 0;
        Configuration.PlannerPlaylistName = playlist.Name;
        plannerPlaylistNameInput          = playlist.Name;
        Configuration.Save();

        Svc.Log.Information($"AutoDuty 排程:已載入播放清單「{playlist.Name}」,共 {entries.Count} 項,進度已歸零。未開始執行。");
    }

    /// <summary>
    /// 把目前的排程清單存成具名播放清單。同名(忽略大小寫)覆蓋,否則新增。
    /// </summary>
    private static void SavePlaylist(string name)
    {
        List<PlannerItem> snapshot = (Configuration.PlannerItems ?? []).JSONClone();

        // 存的是「要跑什麼」不是「跑到哪」——進度不進存檔,載入時才會是乾淨的重跑。
        foreach (PlannerItem entry in snapshot)
            entry.CompletedRuns = 0;

        List<Playlist> playlists     = ConfigurationMain.Instance.Playlists;
        int            existingIndex = playlists.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Playlist       playlist      = new() { Name = name, Entries = snapshot };

        if (existingIndex >= 0)
            playlists[existingIndex] = playlist;
        else
            playlists.Add(playlist);

        Configuration.PlannerPlaylistName = name;
        Configuration.Save();

        Svc.Log.Information($"AutoDuty 排程:已{(existingIndex >= 0 ? "覆蓋" : "新增")}播放清單「{name}」,共 {snapshot.Count} 項。");
    }

    /// <summary>
    /// 刪除已儲存的播放清單。只動存檔,目前的排程清單保持原狀。
    /// </summary>
    private static void DeletePlaylist(int index)
    {
        List<Playlist> playlists = ConfigurationMain.Instance.Playlists;
        if (index < 0 || index >= playlists.Count)
            return;

        string name = playlists[index].Name;
        playlists.RemoveAt(index);

        // 存檔沒了,「目前載入的是誰」這個標示就不能再指著它。
        if (Configuration.PlannerPlaylistName.Equals(name, StringComparison.OrdinalIgnoreCase))
            Configuration.PlannerPlaylistName = string.Empty;

        Configuration.Save();

        Svc.Log.Information($"AutoDuty 排程:已刪除播放清單「{name}」。目前的排程清單未變動。");
    }

    internal static void DrawPlannerUi()
    {
        var plannerLocked = Plugin.States.HasFlag(PluginState.Looping) || Plugin.States.HasFlag(PluginState.Navigating);
        if (plannerLocked)
            ImGui.TextColored(ImGuiColors.DalamudYellow, "請先停止 AutoDuty 以編輯排程。");

        using (ImRaii.Disabled(plannerLocked))
        {
            if (ImGui.Checkbox("啟用排程器", ref Configuration.PlannerEnabled))
                Configuration.Save();
            ImGui.SameLine();
            if (ImGui.Checkbox("循環執行", ref Configuration.PlannerRepeat))
                Configuration.Save();
            ImGuiHelper.HelpMarkerAllowWhenDisabled("依序執行任務：A×N 次後執行 B×M 次。成功完成後計數增加。");

            ImGui.Separator();
            DrawPlaylistBar();

            // Align key Main tab toggles in Planner.
            if (Configuration.DutyModeEnum.EqualsAny(DutyMode.Regular, DutyMode.Trial, DutyMode.Raid))
            {
                if (ImGuiEx.CheckboxWrapped("解除限制", ref Configuration.Unsynced))
                    Configuration.Save();
            }

            if (ImGui.Checkbox("隱藏不可用", ref Configuration.HideUnavailableDuties))
                Configuration.Save();

            ImGui.Separator();
            ImGui.TextDisabled("新增任務至排程：");

            plannerAddTargetRuns = Math.Max(1, plannerAddTargetRuns);
            ImGui.SetNextItemWidth(120f.Scale());
            ImGui.InputInt("次數##PlannerAddRuns", ref plannerAddTargetRuns);
            if (plannerAddTargetRuns < 1)
                plannerAddTargetRuns = 1;

            ImGui.InputTextWithHint("##PlannerSearch", "搜尋任務...", ref plannerSearchText, 100);
            ImGuiHelper.HelpMarkerAllowWhenDisabled("與 Main 一致：可用此開關隱藏目前不可執行的任務。");

            using (ImRaii.Child("##PlannerDutySearch", new Vector2(0, 140f.Scale()), true))
            {
                var level = PlayerHelper.GetCurrentLevelFromSheet();
                foreach (var content in ContentHelper.DictionaryContent.Values
                             .Where(c => c.DutyModes.HasFlag(Configuration.DutyModeEnum))
                             .OrderBy(c => c.ClassJobLevelRequired)
                             .ThenBy(c => c.Name))
                {
                    if (!plannerSearchText.IsNullOrEmpty() && !content.Name.Contains(plannerSearchText, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var canRun = content.CanRun(level);
                    if (Configuration.HideUnavailableDuties && !canRun)
                        continue;

                    using (ImRaii.Disabled(!canRun))
                    {
                        if (ImGui.Selectable($"L{content.ClassJobLevelRequired} ({content.TerritoryType}) {content.Name}##PlannerAdd{content.TerritoryType}"))
                        {
                            Configuration.PlannerItems.Add(new PlannerItem
                            {
                                TerritoryType = content.TerritoryType,
                                TargetRuns = plannerAddTargetRuns,
                                CompletedRuns = 0,
                                PathFileName = null,
                            });

                            if (Configuration.PlannerCurrentIndex < 0)
                                Configuration.PlannerCurrentIndex = 0;
                            Configuration.Save();
                        }
                    }
                }
            }

            if (Configuration.DutyModeEnum == DutyMode.Trust && Configuration.PlannerItems.Count > 0)
            {
                var trustIndex = Math.Clamp(Configuration.PlannerCurrentIndex, 0, Configuration.PlannerItems.Count - 1);
                var trustTerritory = Configuration.PlannerItems[trustIndex].TerritoryType;
                if (ContentHelper.DictionaryContent.TryGetValue(trustTerritory, out var trustContent) && trustContent.TrustMembers.Count > 0)
                {
                    ImGui.Separator();
                    ImGuiEx.LineCentered(() => ImGuiEx.TextUnderlined("選擇親信隊友（目前排程項）"));

                    TrustHelper.ResetTrustIfInvalid();
                    for (int i = 0; i < Configuration.SelectedTrustMembers.Length; i++)
                    {
                        var member = Configuration.SelectedTrustMembers[i];
                        if (member is null)
                            continue;

                        if (trustContent.TrustMembers.All(x => x.MemberName != member))
                            Configuration.SelectedTrustMembers[i] = null;
                    }

                    ImGui.Columns(3);
                    MainTab.DrawTrustMembers(trustContent);
                    ImGui.Columns(1);
                }
            }

            ImGui.Separator();

            if (Configuration.PlannerItems.Count == 0)
            {
                ImGui.TextDisabled("排程清單為空。");
                return;
            }

            if (ImGui.Button("重置排程進度"))
            {
                foreach (var item in Configuration.PlannerItems)
                    item.CompletedRuns = 0;
                Configuration.PlannerCurrentIndex = 0;
                Configuration.Save();
            }

            if (ImGui.BeginTable("##PlannerTable", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 22f.Scale());
                ImGui.TableSetupColumn("任務", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("路徑", ImGuiTableColumnFlags.WidthFixed, 180f.Scale());
                ImGui.TableSetupColumn("次數", ImGuiTableColumnFlags.WidthFixed, 70f.Scale());
                ImGui.TableSetupColumn("進度", ImGuiTableColumnFlags.WidthFixed, 70f.Scale());
                ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 120f.Scale());
                ImGui.TableHeadersRow();

                for (var i = 0; i < Configuration.PlannerItems.Count; i++)
                {
                    var item = Configuration.PlannerItems[i];
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted((i + 1).ToString());

                    ImGui.TableNextColumn();
                    var name = ContentHelper.DictionaryContent.TryGetValue(item.TerritoryType, out var c)
                                    ? $"L{c.ClassJobLevelRequired} ({c.TerritoryType}) {c.Name}"
                                    : $"({item.TerritoryType}) <未知>";
                    ImGui.TextUnformatted(name);
                    if (i == Configuration.PlannerCurrentIndex)
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(ImGuiColors.DalamudYellow, "← 目前");
                    }

                    ImGui.TableNextColumn();
                    if (!ContentPathsManager.DictionaryPaths.TryGetValue(item.TerritoryType, out var container) || container.Paths.Count == 0)
                    {
                        ImGui.TextDisabled("無路徑");
                    }
                    else
                    {
                        string preview;
                        if (item.PathFileName.IsNullOrEmpty())
                        {
                            preview = "(自動)";
                        }
                        else
                        {
                            var selectedPath = container.Paths.FirstOrDefault(p => p.FileName.Equals(item.PathFileName, StringComparison.OrdinalIgnoreCase));
                            preview = selectedPath != null ? selectedPath.Name : "(缺失)";
                        }

                        ImGui.SetNextItemWidth(-1);
                        if (ImGui.BeginCombo($"##PlannerPath{i}", preview))
                        {
                            var isAuto = item.PathFileName.IsNullOrEmpty();
                            if (ImGui.Selectable("(自動)", isAuto))
                            {
                                item.PathFileName = null;
                                Configuration.Save();
                            }

                            foreach (var path in container.Paths)
                            {
                                var selected = !item.PathFileName.IsNullOrEmpty() && path.FileName.Equals(item.PathFileName, StringComparison.OrdinalIgnoreCase);
                                if (ImGui.Selectable(path.Name, selected))
                                {
                                    item.PathFileName = path.FileName;
                                    Configuration.Save();
                                }
                            }

                            ImGui.EndCombo();
                        }

                        if (!item.PathFileName.IsNullOrEmpty() && !container.Paths.Any(p => p.FileName.Equals(item.PathFileName, StringComparison.OrdinalIgnoreCase)))
                        {
                            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                                SetTooltipFullAlpha(item.PathFileName);
                        }
                    }

                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(-1);
                    var runs = item.TargetRuns;
                    if (ImGui.InputInt($"##PlannerRuns{i}", ref runs, 0, 0))
                    {
                        item.TargetRuns = Math.Max(1, runs);
                        item.CompletedRuns = Math.Clamp(item.CompletedRuns, 0, item.TargetRuns);
                        Configuration.Save();
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{item.CompletedRuns}/{item.TargetRuns}");

                    ImGui.TableNextColumn();
                    var changed = false;
                    if (ImGui.Button($"上移##PlannerUp{i}") && i > 0)
                    {
                        (Configuration.PlannerItems[i - 1], Configuration.PlannerItems[i]) = (Configuration.PlannerItems[i], Configuration.PlannerItems[i - 1]);
                        if (Configuration.PlannerCurrentIndex == i)
                            Configuration.PlannerCurrentIndex = i - 1;
                        else if (Configuration.PlannerCurrentIndex == i - 1)
                            Configuration.PlannerCurrentIndex = i;
                        changed = true;
                    }
                    ImGui.SameLine();
                    if (ImGui.Button($"刪除##PlannerDel{i}"))
                    {
                        Configuration.PlannerItems.RemoveAt(i);
                        if (Configuration.PlannerItems.Count == 0)
                            Configuration.PlannerCurrentIndex = 0;
                        else if (Configuration.PlannerCurrentIndex >= Configuration.PlannerItems.Count)
                            Configuration.PlannerCurrentIndex = Configuration.PlannerItems.Count - 1;
                        changed = true;
                        i--;
                    }

                    if (changed)
                        Configuration.Save();
                }

                ImGui.EndTable();
            }
        }
    }
}
