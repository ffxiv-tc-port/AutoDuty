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
    internal static string FollowName = "";

    private static Configuration Configuration => Plugin.Configuration;
    private static string preLoopCommand = string.Empty;
    private static string betweenLoopCommand = string.Empty;
    private static string terminationCommand = string.Empty;

    private static string plannerSearchText = string.Empty;
    private static int plannerAddTargetRuns = 1;
    private static Dictionary<uint, Item> Items { get; set; } = Svc.Data.GetExcelSheet<Item>()?.Where(x => !x.Name.ToString().IsNullOrEmpty()).ToDictionary(x => x.RowId, x => x) ?? [];
    private static string stopItemQtyItemNameInput = "";
    private static KeyValuePair<uint, string> stopItemQtySelectedItem = new(0, "");

    private static string                     autoOpenCoffersNameInput    = "";
    private static KeyValuePair<uint, string> autoOpenCoffersSelectedItem = new(0, "");

    public class ConsumableItem
    {
        public uint ItemId;
        public string Name = string.Empty;
        public bool CanBeHq;
        public ushort StatusId;
    }

    private static List<ConsumableItem> ConsumableItems { get; set; } = Svc.Data.GetExcelSheet<Item>()?.Where(x => !x.Name.ToString().IsNullOrEmpty() && x.ItemUICategory.ValueNullable?.RowId is 44 or 45 or 46 && x.ItemAction.ValueNullable?.Data[0] is 48 or 49).Select(x => new ConsumableItem() { StatusId = x.ItemAction.Value!.Data[0], ItemId = x.RowId, Name = x.Name.ToString(), CanBeHq = x.CanBeHq }).ToList() ?? [];

    private static string consumableItemsItemNameInput = "";
    private static ConsumableItem consumableItemsSelectedItem = new();

    private static string profileRenameInput = "";

    private static readonly Sounds[] _validSounds = ((Sounds[])Enum.GetValues(typeof(Sounds))).Where(s => s != Sounds.None && s != Sounds.Unknown).ToArray();

    private static bool overlayHeaderSelected      = false;
    private static bool devHeaderSelected          = false;
    private static bool dutyConfigHeaderSelected   = false;
    private static bool bmaiSettingHeaderSelected  = false;
    private static bool wrathSettingHeaderSelected = false;
    private static bool w2wSettingHeaderSelected   = false;
    private static bool advModeHeaderSelected      = false;
    private static bool preLoopHeaderSelected      = false;
    private static bool betweenLoopHeaderSelected  = false;
    private static bool terminationHeaderSelected  = false;
    private static bool multiboxHeaderSelected     = false;

    public static void BuildManuals()
    {
        ConsumableItems.Add(new ConsumableItem { StatusId = 1086, ItemId = 14945, Name = "Squadron Enlistment Manual", CanBeHq = false });
        ConsumableItems.Add(new ConsumableItem { StatusId = 1080, ItemId = 14948, Name = "Squadron Battle Manual", CanBeHq = false });
        ConsumableItems.Add(new ConsumableItem { StatusId = 1081, ItemId = 14949, Name = "Squadron Survival Manual", CanBeHq = false });
        ConsumableItems.Add(new ConsumableItem { StatusId = 1082, ItemId = 14950, Name = "Squadron Engineering Manual", CanBeHq = false });
        ConsumableItems.Add(new ConsumableItem { StatusId = 1083, ItemId = 14951, Name = "Squadron Spiritbonding Manual", CanBeHq = false });
        ConsumableItems.Add(new ConsumableItem { StatusId = 1084, ItemId = 14952, Name = "Squadron Rationing Manual", CanBeHq = false });
        ConsumableItems.Add(new ConsumableItem { StatusId = 1085, ItemId = 14953, Name = "Squadron Gear Maintenance Manual", CanBeHq = false });
    }

    private static string plannerPlaylistNameInput     = string.Empty;
    private static bool   plannerPlaylistNameInputInit = false;

    /// <summary>
    /// 給「停用區段裡、項目灰掉也要看得到」的說明框用:區段把 Style.Alpha 乘上 DisabledAlpha,
    /// 在區段裡打開的說明框會跟著變淡,這裡推回 1 再畫。
    /// </summary>
    private static void SetTooltipFullAlpha(string text)
    {
        using var fullAlpha = ImRaii.PushStyle(ImGuiStyleVar.Alpha, 1f);
        ImGui.SetTooltip(text);
    }

    public static void Draw()
    {
        if (MainWindow.CurrentTabName != "Config")
            MainWindow.CurrentTabName = "Config";

        // 有別的外掛正在用 IPC 覆寫設定時,這一頁顯示的是「覆寫後」的值,改了也會在覆寫結束時
        // 被還原掉 —— 這件事必須在頁面上看得見,不能只寫進 log。
        bool overridesActive = ConfigOverrideHelper.HasOverrides;
        if (overridesActive)
        {
            ImGuiEx.TextWrapped(ImGuiColors.DalamudYellow,
                                "Another plugin is temporarily overriding AutoDuty's settings. The values below are the overridden ones and cannot be edited until it releases them; your own settings are untouched and will come back.".Loc());
            ImGui.Separator();
        }

        using var overrideLock = ImRaii.Disabled(overridesActive);

        //Start of Profile Selection
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Currently selected profile: ".Loc());
        ImGui.SameLine();
        if (ConfigurationMain.Instance.ActiveProfileName == ConfigurationMain.CONFIGNAME_BARE)
            ImGuiHelper.DrawIcon(FontAwesomeIcon.Lock);
        if (ConfigurationMain.Instance.ActiveProfileName == ConfigurationMain.Instance.DefaultConfigName)
            ImGuiHelper.DrawIcon(FontAwesomeIcon.CheckCircle);
        ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X - 180f.Scale());
        ImGui.SetItemAllowOverlap();
        using (ImRaii.IEndObject configCombo = ImRaii.Combo("##ConfigCombo", ConfigurationMain.Instance.ActiveProfileName))
        {
            if (configCombo)
                foreach (string key in ConfigurationMain.Instance.ConfigNames)
                {
                    float selectableX = ImGui.GetCursorPosX();
                    if (key == ConfigurationMain.CONFIGNAME_BARE)
                        ImGuiHelper.DrawIcon(FontAwesomeIcon.Lock);
                    if (key == ConfigurationMain.Instance.DefaultConfigName)
                        ImGuiHelper.DrawIcon(FontAwesomeIcon.CheckCircle);

                    float textX = ImGui.GetCursorPosX();
                        
                    ImGui.SetCursorPosX(selectableX);
                    ImGui.SetItemAllowOverlap();
                    if (ImGui.Selectable($"###{key}ConfigSelectable"))
                        ConfigurationMain.Instance.SetProfile(key);
                    ImGui.SameLine(textX);
                    ImGui.Text(key);

                    ProfileData? profile = ConfigurationMain.Instance.GetProfile(key);
                    if(profile?.CIDs.Any() ?? false)
                    {
                        ImGui.SameLine();
                        ImGuiEx.TextWrapped(ImGuiHelper.VersionColor, string.Join(", ", profile.CIDs.Select(cid => ConfigurationMain.Instance.charByCID.TryGetValue(cid, out ConfigurationMain.CharData cd) ? cd.GetName() : cid.ToString())));
                    }
                }
        }

        ImGui.PopItemWidth();
        ImGui.SameLine();

        if (ImGui.IsPopupOpen("##RenameProfile"))
        {
            bool    open     = true;
            Vector2 textSize = ImGui.CalcTextSize(profileRenameInput);
            ImGui.SetNextWindowSize(new System.Numerics.Vector2(textSize.X + 200, textSize.Y + 120).Scale());
            if (ImGui.BeginPopupModal($"##RenameProfile", ref open, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoMove))
            {
                ImGuiHelper.CenterNextElement(ImGui.CalcTextSize("New Profile Name".Loc()).X);
                ImGui.Text("New Profile Name".Loc());
                ImGui.NewLine();
                ImGui.SameLine(50f.Scale());
                ImGui.SetNextItemWidth((textSize.X + 100).Scale());

                ImGui.InputText("##RenameProfileInput", ref profileRenameInput, 100);
                ImGui.Spacing();
                ImGuiHelper.CenterNextElement(ImGui.CalcTextSize("Change Profile Name".Loc()).X);
                if (ImGui.Button("Change Profile Name".Loc()))
                {
                    if (ConfigurationMain.Instance.RenameCurrentProfile(profileRenameInput))
                    {
                        open = false;
                        ImGui.CloseCurrentPopup();
                    }
                }

                ImGui.EndPopup();
            }
        }



        bool bareProfile = ConfigurationMain.Instance.ActiveProfileName == ConfigurationMain.CONFIGNAME_BARE;

        if (ImGuiComponents.IconButton(FontAwesomeIcon.Plus))
            ConfigurationMain.Instance.CreateNewProfile();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            SetTooltipFullAlpha("Create new Profile".Loc());

        ImGui.SameLine(0, 15f);
        using (ImRaii.Disabled(bareProfile))
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Pen))
            {
                profileRenameInput = ConfigurationMain.Instance.ActiveProfileName;
                ImGui.OpenPopup("##RenameProfile");
            }

        if (ImGui.IsMouseHoveringRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax()))
            SetTooltipFullAlpha("Rename Profile".Loc());

        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Copy))
            ConfigurationMain.Instance.DuplicateCurrentProfile();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            SetTooltipFullAlpha("Duplicate Profile".Loc());

        ImGui.SameLine();
        using (ImRaii.Disabled(ImGui.GetIO().KeyCtrl ? ConfigurationMain.Instance.GetCurrentProfile.CIDs.Contains(Player.CID) != ImGui.GetIO().KeyShift : ConfigurationMain.Instance.DefaultConfigName == ConfigurationMain.Instance.ActiveProfileName))
            if (ImGuiComponents.IconButton(FontAwesomeIcon.CheckCircle))
                if(ImGui.GetIO().KeyCtrl)
                    if (ImGui.GetIO().KeyShift)
                        ConfigurationMain.Instance.RemoveCharacterDefault();
                    else
                        ConfigurationMain.Instance.SetCharacterDefault();
                else
                    ConfigurationMain.Instance.SetProfileAsDefault();
        if (ImGui.IsMouseHoveringRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax()))
            SetTooltipFullAlpha("Make Default\nHold ctrl to make default for the current character\nctrl+shift to remove it as default for the current character".Loc());


        ImGui.SameLine();
        using (ImRaii.Disabled(bareProfile || !ImGui.GetIO().KeyCtrl))
            if (ImGuiComponents.IconButton(FontAwesomeIcon.TrashAlt))
                ConfigurationMain.Instance.RemoveCurrentProfile();
        if (ImGui.IsMouseHoveringRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax()))
            SetTooltipFullAlpha("Delete Config\nHold ctrl to enable".Loc());

        if (bareProfile)
            ImGuiEx.TextWrapped("The bare profile is for just running a duty, and nothing else. You can duplicate it to make edits.".Loc());
        using ImRaii.IEndObject _ = ImRaii.Disabled(bareProfile);

        //Start of Window & Overlay Settings
        ImGui.Spacing();
        ImGui.Separator();
        ImGuiHelper.SectionHeaderAlwaysEnabled("Window & Overlay Settings".Loc(), ref overlayHeaderSelected);

        if (overlayHeaderSelected == true)
        {
            if (ImGui.Checkbox("Show Overlay".Loc(), ref Configuration.showOverlay))
            {
                Configuration.ShowOverlay = Configuration.showOverlay;
                Configuration.Save();
            }
            ImGuiHelper.HelpMarkerAllowWhenDisabled("Note that the quickaction buttons (TurnIn/Desynth/etc) require their respective configs to be enabled!\nOr Override Overlay Buttons to be Enabled".Loc());
            if (Configuration.ShowOverlay)
            {
                ImGui.Indent();
                ImGui.Columns(2, "##OverlayColumns", false);

                //ImGui.SameLine(0, 53);
                if (ImGui.Checkbox("Hide When Stopped".Loc(), ref Configuration.hideOverlayWhenStopped))
                {
                    Configuration.HideOverlayWhenStopped = Configuration.hideOverlayWhenStopped;
                    Configuration.Save();
                }
                ImGui.NextColumn();
                if (ImGui.Checkbox("Lock Overlay".Loc(), ref Configuration.lockOverlay))
                {
                    Configuration.LockOverlay = Configuration.lockOverlay;
                    Configuration.Save();
                }
                ImGui.NextColumn();
                //ImGui.SameLine(0, 57);

                if (ImGui.Checkbox("Show Duty/Loops Text".Loc(), ref Configuration.ShowDutyLoopText))
                    Configuration.Save();
                ImGui.NextColumn();
                if (ImGui.Checkbox("Use Transparent BG".Loc(), ref Configuration.overlayNoBG))
                {
                    Configuration.OverlayNoBG = Configuration.overlayNoBG;
                    Configuration.Save();
                }
                ImGui.NextColumn();
                if (ImGui.Checkbox("Override Overlay Buttons".Loc(), ref Configuration.OverrideOverlayButtons))
                    Configuration.Save();
                ImGuiHelper.HelpMarkerAllowWhenDisabled("Overlay buttons by default are enabled if their config is enabled\nThis will allow you to chose which buttons are enabled".Loc());
                ImGui.NextColumn();
                if (ImGui.Checkbox("Show AD Action Text".Loc(), ref Configuration.ShowActionText))
                    Configuration.Save();
                if (Configuration.OverrideOverlayButtons)
                {
                    ImGui.Indent();
                    ImGui.Columns(3, "##OverlayButtonColumns", false);
                    if (ImGui.Checkbox("Goto".Loc(), ref Configuration.GotoButton))
                        Configuration.Save();
                    ImGui.NextColumn();
                    if (ImGui.Checkbox("Turnin".Loc(), ref Configuration.TurninButton))
                        Configuration.Save();
                    ImGui.NextColumn();
                    if (ImGui.Checkbox("Desynth".Loc(), ref Configuration.DesynthButton))
                        Configuration.Save();
                    ImGui.NextColumn();
                    if (ImGui.Checkbox("Extract".Loc(), ref Configuration.ExtractButton))
                        Configuration.Save();
                    ImGui.NextColumn();
                    if (ImGui.Checkbox("Repair".Loc(), ref Configuration.RepairButton))
                        Configuration.Save();
                    ImGui.NextColumn();
                    if (ImGui.Checkbox("Equip".Loc(), ref Configuration.EquipButton))
                        Configuration.Save();
                    ImGui.NextColumn();
                    if (ImGui.Checkbox("Coffer".Loc(), ref Configuration.CofferButton))
                        Configuration.Save();
                    ImGui.NextColumn();
                    if (ImGui.Checkbox("Triple Triad".Loc() + "##TTButton", ref Configuration.TTButton))
                        Configuration.Save();
                    ImGui.Unindent();
                }
                ImGui.Unindent();
            }
            ImGui.Columns(1);
            if (ImGui.Checkbox("Show Main Window on Startup".Loc(), ref Configuration.ShowMainWindowOnStartup))
                Configuration.Save();
            ImGui.SameLine();
            if (ImGui.Checkbox("Slider Inputs".Loc(), ref Configuration.UseSliderInputs))
                Configuration.Save();
            
        }

        DrawMultiboxSection();

        if (Plugin.isDev)
        {
            ImGui.Separator();
            ImGui.Spacing();
            ImGuiHelper.SectionHeaderAlwaysEnabled("Dev Settings", ref devHeaderSelected);

            if (devHeaderSelected)
            {
                if (ImGui.Checkbox("Update Paths on startup", ref ConfigurationMain.Instance.updatePathsOnStartup))
                    Configuration.Save();

                if (ImGui.Button("Print mod list")) 
                    Svc.Log.Info(string.Join("\n", PluginInterface.InstalledPlugins.Where(pl => pl.IsLoaded).GroupBy(pl => pl.Manifest.InstalledFromUrl).OrderByDescending(g => g.Count()).Select(g => g.Key+"\n\t"+string.Join("\n\t", g.Select(pl => pl.Name)))));

                if (ImGuiHelper.CollapsingHeaderAlwaysEnabled("Available Duty Support"))//ImGui.Button("check duty support?"))
                {
                    if(GenericHelpers.TryGetAddonMaster<AddonMaster.DawnStory>(out AddonMaster.DawnStory? m))
                    {
                        if (m.IsAddonReady)
                        {
                            ImGuiEx.Text("Selected: " + m.Reader.CurrentSelection);

                            ImGuiEx.Text($"Cnt: {m.Reader.EntryCount}");
                            foreach (var x in m.Entries)
                            {
                                ImGuiEx.Text($"{x.Name} / {x.ReaderEntry.Callback} / {x.Index}");
                                if (ImGuiEx.HoveredAndClicked() && x.Status != 2)
                                {
                                    // 全 repo 唯一不經 AddonHelper 的按法(Entry.Select() 逐字是 Callback.Fire(Base, true, 12, cb)),
                                    // 手動點擊也走同一道守衛:同一扇窗同一個項目在窗走完前重按一律擋。
                                    unsafe
                                    {
                                        if (AddonPressGuard.TryBeginPress("DawnStory", m.Base, AddonPressGuard.BuildPressKey(true, [12, x.ReaderEntry.Callback])))
                                            x.Select();
                                    }
                                }
                            }
                        }
                    }
                }

                if (ImGuiHelper.CollapsingHeaderAlwaysEnabled("Available TT cards"))
                {
                    unsafe
                    {
                        if (GenericHelpers.TryGetAddonByName("TripleTriadCoinExchange", out AtkUnitBase* exchangeAddon))
                        {
                            if (exchangeAddon->IsReady)
                            {
                                ReaderTripleTriadCoinExchange exchange = new(exchangeAddon);

                                ImGuiEx.Text($"Cnt: {exchange.EntryCount}");
                                foreach (var x in exchange.Entries)
                                {
                                    ImGuiEx.Text($"({x.Id}) {x.Name} | {x.Count} | {x.Value} | {x.InDeck}");
                                    if (ImGuiEx.HoveredAndClicked())
                                    {
                                        //x.Select();
                                    }
                                }
                            }
                        }
                    }
                }



                if (ImGui.Button("Turn on rotation"))
                {
                    Plugin.SetRotationPluginSettings(true, ignoreTimer: true);
                }

                ImGui.SameLine();
                if (ImGui.Button("Turn off rotation"))
                {
                    Plugin.SetRotationPluginSettings(false);
                    if(Wrath_IPCSubscriber.IsEnabled)
                        Wrath_IPCSubscriber.Release();
                }

                if (ImGui.Button("BetweenLoopActions##DevBetweenLoops"))
                {
                    Plugin.CurrentTerritoryContent =  ContentHelper.DictionaryContent.Values.First();
                    Plugin.States                  |= PluginState.Other;
                    Plugin.LoopTasks(false);
                }

                if (ImGuiHelper.CollapsingHeaderAlwaysEnabled("teleport playthings"))
                {
                    if (ImGuiHelper.CollapsingHeaderAlwaysEnabled("Warps"))
                    {
                        ImGui.Indent();
                        foreach (Warp warp in Svc.Data.GameData.GetExcelSheet<Warp>())
                        {
                            if (warp.TerritoryType.RowId != 152)
                                continue;

                            if (ImGuiHelper.CollapsingHeaderAlwaysEnabled($"{warp.Name} {warp.Question} to {warp.TerritoryType.ValueNullable?.PlaceName.ValueNullable?.Name.ToString()}##{warp.RowId}"))
                            {
                                if (warp.PopRange.ValueNullable is { } level)
                                {
                                    ImGui.Text($"{level.X} {level.Y} {level.Z} in {level.Territory.ValueNullable?.PlaceName.ValueNullable?.Name.ToString()}");
                                    ImGui.Text($"{(new Vector3(level.X, level.Y, level.Z) - Player.Position)}");
                                }
                            }
                        }

                        ImGui.Unindent();
                    }

                    if (ImGuiHelper.CollapsingHeaderAlwaysEnabled("LevelTest"))
                    {
                        foreach ((Level lvl, Vector3, Vector3) level in Svc.Data.GameData.GetExcelSheet<Level>().Where(lvl => lvl.Territory.RowId == 152)
                                                                           .Select(lvl => (lvl, (new Vector3(lvl.X, lvl.Y, lvl.Z))))
                                                                           .Select(tuple => (tuple.lvl, tuple.Item2, (tuple.Item2 - Player.Position))).OrderBy(lvl => lvl.Item3.LengthSquared()))
                        {
                            ImGui.Text($"{level.lvl.RowId} {level.Item2} {level.Item3} {string.Join(" | ", level.lvl.Object.GetType().GenericTypeArguments.Select(t => t.FullName))}: {level.lvl.Object.RowId}");
                        }
                    }

                    ImGuiEx.Text($"{typeof(Achievement).Assembly.GetTypes().Where(x => x.FullName.StartsWith("Lumina.Excel.Sheets")).Select(x => (x, x.GetProperties().Where(f => f.PropertyType.Name == "RowRef`1" && f.PropertyType.GenericTypeArguments[0].FullName == typeof(Map).FullName))).Where(x => x.Item2.Any()).Select(x => $"{x.Item1} references {x.Item2.Select(x => x.Name).Print(", ")}").Print("\n")}");
                }
            }
        }
        ImGui.Spacing();
        ImGui.Separator();
        ImGuiHelper.SectionHeaderAlwaysEnabled("Duty Config Settings".Loc(), ref dutyConfigHeaderSelected);

        if (dutyConfigHeaderSelected == true)
        {
            ImGui.Columns(2, "##DutyConfigHeaderColumns");
            if (ImGui.Checkbox("Auto Leave Duty in last loop".Loc(), ref Configuration.AutoExitDuty))
                Configuration.Save();
            ImGuiHelper.HelpMarkerAllowWhenDisabled("Will automatically exit the dungeon upon completion of the path.".Loc());
            ImGui.NextColumn();
            if (ImGui.Checkbox("Block leaving duty until it's complete".Loc(), ref Configuration.OnlyExitWhenDutyDone))
                Configuration.Save();
            //ImGuiComponents.HelpMarker("Blocks leaving dungeon before duty is completed");
            ImGui.Columns(1);
            if (ImGui.Checkbox("Auto Manage Rotation Plugin State".Loc(), ref Configuration.AutoManageRotationPluginState))
                Configuration.Save();
            ImGuiHelper.HelpMarkerAllowWhenDisabled("Autoduty will enable the Rotation Plugin at the start of each duty\n*Only if using Wrath Combo, Rotation Solver or BossMod AutoRotation\n**AutoDuty will try to use them in that order".Loc());

            ImGui.Separator();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("排程器：");
            ImGui.SameLine(0, 6);
            if (ImGui.Button("開啟排程分頁"))
                MainWindow.OpenTab("排程器");
            ImGui.SameLine(0, 6);
            ImGui.TextDisabled(Configuration.PlannerEnabled ? $"已啟用（{Configuration.PlannerItems.Count} 項）" : "未啟用");
            ImGuiHelper.HelpMarkerAllowWhenDisabled("排程設定已移至「排程器」分頁。");

            if (Configuration.AutoManageRotationPluginState)
            {
                if (Wrath_IPCSubscriber.IsEnabled)
                {
                    ImGui.Indent();
                    ImGuiHelper.SectionHeaderAlwaysEnabled("> Wrath Combo Config Options <".Loc(), ref wrathSettingHeaderSelected);

                    if (wrathSettingHeaderSelected)
                    {
                        bool wrath_AutoSetupJobs = Configuration.Wrath_AutoSetupJobs;
                        if (ImGui.Checkbox("Auto setup jobs for autorotation".Loc(), ref wrath_AutoSetupJobs))
                        {
                            Configuration.Wrath_AutoSetupJobs = wrath_AutoSetupJobs;
                            Configuration.Save();
                        }
                        ImGuiHelper.HelpMarkerAllowWhenDisabled("If this is not enabled and a job is not setup in Wrath Combo, AD will instead use RSR or bm AutoRotation".Loc());

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text("Targeting | Tank: ".Loc());
                        ImGui.SameLine(0, 5);
                        ImGui.PushItemWidth(150f.Scale());
                        if (ImGui.BeginCombo("##ConfigWrathTargetingTank", Configuration.Wrath_TargetingTank.ToCustomString()))
                        {
                            foreach (Wrath_IPCSubscriber.DPSRotationMode targeting in Enum.GetValues(typeof(Wrath_IPCSubscriber.DPSRotationMode)))
                            {
                                if(targeting == Wrath_IPCSubscriber.DPSRotationMode.Tank_Target)
                                    continue;

                                if (ImGui.Selectable(targeting.ToCustomString()))
                                {
                                    Configuration.Wrath_TargetingTank = targeting;
                                    Configuration.Save();
                                }
                            }
                            ImGui.EndCombo();
                        }

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text("Targeting | Non-Tank: ".Loc());
                        ImGui.SameLine(0, 5);
                        ImGui.PushItemWidth(150f.Scale());
                        if (ImGui.BeginCombo("##ConfigWrathTargetingNonTank", Configuration.Wrath_TargetingNonTank.ToCustomString()))
                        {
                            foreach (Wrath_IPCSubscriber.DPSRotationMode targeting in Enum.GetValues(typeof(Wrath_IPCSubscriber.DPSRotationMode)))
                            {
                                if (ImGui.Selectable(targeting.ToCustomString()))
                                {
                                    Configuration.Wrath_TargetingNonTank = targeting;
                                    Configuration.Save();
                                }
                            }
                            ImGui.EndCombo();
                        }

                        ImGui.Separator();
                    }
                    ImGui.Unindent();
                }
            }

            if (ImGui.Checkbox("Auto Manage BossMod AI Settings".Loc(), ref Configuration.autoManageBossModAISettings))
                Configuration.Save();
            ImGuiHelper.HelpMarkerAllowWhenDisabled("Autoduty will enable BMAI and any options you configure at the start of each duty.".Loc());

            if (ImGui.Checkbox("王戰時自動切到限時必殺的小怪", ref Configuration.BossTargetUrgentAdds))
                Configuration.Save();
            ImGuiHelper.HelpMarkerAllowWhenDisabled("首領模組標記為「時限內沒打掉就會致死」的小怪（例：伊弗利特討伐戰的火獄之楔、泰坦討伐戰的泰坦的核心與花崗石牢）一出現，就把目標切過去；打掉後目標空了會切回首領。\n需要 BossModReborn；BMR 的 AI 有沒有開、上一格有沒有勾都不影響。");

            if (ImGui.Checkbox("冒險者分隊：小怪路段先下號令再開打", ref Configuration.SquadronOpener))
                Configuration.Save();
            ImGuiHelper.HelpMarkerAllowWhenDisabled("只在分隊攻略任務的小怪路段生效（首領戰交給 BossModReborn 的分隊號令）；玩家是坦克時不做。\n要選新的敵人時，先對牠下「號令：戰鬥開始」，等分隊坦克換過去（沒有坦克就看任一名分隊員）才選牠當目標，讓輪替插件晚一步出手、讓坦克先接仇恨。\n等到上限還沒人跟上，就先選目標再下一次號令，然後開打；等待中玩家血量低於一半就直接開打。\n輪替插件的目標要設成「手動」才有「晚一步」的效果。");
            if (Configuration.SquadronOpener)
            {
                ImGui.Indent();
                ImGui.SetNextItemWidth(150f.Scale());
                if (ImGui.SliderFloat("等待上限（秒）##SquadronOpenerMaxWait", ref Configuration.SquadronOpenerMaxWaitSeconds, 0.5f, 6f, "%.1f"))
                    Configuration.Save();
                ImGui.Unindent();
            }

            if (Configuration.autoManageBossModAISettings)
            {
                ImGui.Indent();
                ImGuiHelper.SectionHeaderAlwaysEnabled("> BMAI Config Options <".Loc(), ref bmaiSettingHeaderSelected);
            
                if (bmaiSettingHeaderSelected == true)
                {
                    if (ImGui.Button("Update Presets".Loc()))
                    {
                        BossMod_IPCSubscriber.RefreshPreset("AutoDuty", Resources.AutoDutyPreset);
                        BossMod_IPCSubscriber.RefreshPreset("AutoDuty Passive", Resources.AutoDutyPassivePreset);
                    }
                    if (ImGui.Checkbox("Update Presets automatically".Loc(), ref Configuration.BM_UpdatePresetsAutomatically))
                        Configuration.Save();
                    if (ImGui.Checkbox("Stay Close to Tank (Party Coherency)".Loc(), ref Configuration.PartyCoherency))
                    {
                        // 關掉的當下就把 BossMod 那條軌道歸零,不要等到下一次讀路徑步驟。
                        BossMod_IPCSubscriber.StayCloseToTank(false);
                        Configuration.Save();
                    }
                    ImGuiHelper.HelpMarkerAllowWhenDisabled("Off by default. When on, AutoDuty asks BossMod AI to keep you near the party's tank on every non-boss step. Individual path steps can opt out with the NoPartyCoherency flag.".Loc());
                    if (ImGui.Checkbox("Set Max Distance To Target Based on Player Role".Loc(), ref Configuration.maxDistanceToTargetRoleBased))
                    {
                        Configuration.MaxDistanceToTargetRoleBased = Configuration.maxDistanceToTargetRoleBased;
                        Configuration.Save();
                    }
                    using (ImRaii.Disabled(Configuration.MaxDistanceToTargetRoleBased))
                    {
                        ImGui.PushItemWidth(195f.Scale());
                        // 滑桿在拖曳期間每一幀都回 true，存檔只能在放開滑鼠（編輯結束）那一刻做一次
                        if (ImGui.SliderFloat("Max Distance To Target".Loc(), ref Configuration.MaxDistanceToTargetFloat, 1, 30))
                            Configuration.MaxDistanceToTargetFloat = Math.Clamp(Configuration.MaxDistanceToTargetFloat, 1, 30);
                        if (ImGui.IsItemDeactivatedAfterEdit())
                            Configuration.Save();
                        if (ImGui.SliderFloat("Max Distance To Target AoE".Loc(), ref Configuration.MaxDistanceToTargetAoEFloat, 1, 10))
                            Configuration.MaxDistanceToTargetAoEFloat = Math.Clamp(Configuration.MaxDistanceToTargetAoEFloat, 1, 10);
                        if (ImGui.IsItemDeactivatedAfterEdit())
                            Configuration.Save();
                        ImGui.PopItemWidth();
                    }
                    using (ImRaii.Disabled(!Configuration.MaxDistanceToTargetRoleBased))
                    {
                        ImGui.PushItemWidth(195f.Scale());
                        if (ImGui.SliderFloat("Max Distance To Target | Melee".Loc(), ref Configuration.MaxDistanceToTargetRoleMelee, 1, 30))
                            Configuration.MaxDistanceToTargetRoleMelee = Math.Clamp(Configuration.MaxDistanceToTargetRoleMelee, 1, 30);
                        if (ImGui.IsItemDeactivatedAfterEdit())
                            Configuration.Save();
                        if (ImGui.SliderFloat("Max Distance To Target | Ranged".Loc(), ref Configuration.MaxDistanceToTargetRoleRanged, 1, 30))
                            Configuration.MaxDistanceToTargetRoleRanged = Math.Clamp(Configuration.MaxDistanceToTargetRoleRanged, 1, 30);
                        if (ImGui.IsItemDeactivatedAfterEdit())
                            Configuration.Save();
                        ImGui.PopItemWidth();
                    }
                    if (ImGui.Checkbox("Set Positional Based on Player Role".Loc(), ref Configuration.positionalRoleBased))
                    {
                        Configuration.PositionalRoleBased = Configuration.positionalRoleBased;
                        Plugin.BMRoleChecks();
                        Configuration.Save();
                    }
                    using (ImRaii.Disabled(Configuration.positionalRoleBased))
                    {
                        ImGui.SameLine(0, 10);
                        if (ImGui.Button(Configuration.PositionalEnum.ToCustomString()))
                            ImGui.OpenPopup("PositionalPopup");
            
                        if (ImGui.BeginPopup("PositionalPopup"))
                        {
                            foreach (Positional positional in Enum.GetValues(typeof(Positional)))
                            {
                                if (ImGui.Selectable(positional.ToCustomString()))
                                {
                                    Configuration.PositionalEnum = positional;
                                    Configuration.Save();
                                }
                            }
                            ImGui.EndPopup();
                        }
                    }
                    if (ImGui.Button("Use Default BMAI Settings".Loc()))
                    {
                        Configuration.maxDistanceToTargetRoleBased = true;
                        Configuration.positionalRoleBased = true;
                        Configuration.Save();
                    }
                    ImGuiHelper.HelpMarkerAllowWhenDisabled("Clicking this will reset your BMAI config to the default and *recommended* settings for AD".Loc());

                    ImGui.Separator();
                }
                ImGui.Unindent();
            }
            if (ImGui.Checkbox("Auto Manage Vnav Align Camera".Loc(), ref Configuration.AutoManageVnavAlignCamera))
                Configuration.Save();
            ImGuiHelper.HelpMarkerAllowWhenDisabled("Autoduty will enable AlignCamera in VNav at the start of each duty, and disable it when done if it was not set.".Loc());

            if (ImGui.Checkbox("Loot Treasure Coffers".Loc(), ref Configuration.LootTreasure))
                Configuration.Save();

            if (Configuration.LootTreasure)
            {
                ImGui.Indent();
                ImGui.Text("Select Method: ".Loc());
                ImGui.SameLine(0, 5);
                ImGui.PushItemWidth(150f.Scale());
                if (ImGui.BeginCombo("##ConfigLootMethod", Configuration.LootMethodEnum.ToCustomString()))
                {
                    foreach (LootMethod lootMethod in Enum.GetValues(typeof(LootMethod)))
                    {
                        if(lootMethod == LootMethod.RotationSolver)
                            continue;
                        using (ImRaii.Disabled((lootMethod == LootMethod.Pandora && !PandorasBox_IPCSubscriber.IsEnabled)))
                        {
                            if (ImGui.Selectable(lootMethod.ToCustomString()))
                            {
                                Configuration.LootMethodEnum = lootMethod;
                                Configuration.Save();
                            }
                        }
                    }
                    ImGui.EndCombo();
                }
                
                if (ImGui.Checkbox("Loot Boss Treasure Only".Loc(), ref Configuration.LootBossTreasureOnly))
                        Configuration.Save();

                ImGuiHelper.HelpMarkerAllowWhenDisabled("AutoDuty will walk around non-boss chests, and only loot boss chests.\nNot all paths may accomodate.".Loc());
                ImGui.PopItemWidth();
                ImGui.Unindent();
            }
            ImGui.PushItemWidth(150f.Scale());
            if (ImGui.InputInt("Minimum time before declared stuck (in ms)".Loc(), ref Configuration.MinStuckTime))
            {
                Configuration.MinStuckTime = Math.Max(250, Configuration.MinStuckTime);
                Configuration.Save();
            }

            if (ImGui.Checkbox("Escalate when stuck (jump, sidestep, skip step)".Loc(), ref Configuration.StuckEscalationEnabled))
                Configuration.Save();

            ImGuiHelper.HelpMarkerAllowWhenDisabled("When stuck, AutoDuty first re-issues the move, then jumps, then sidesteps, then skips the current step. Rebuilding the navmesh always runs last.".Loc());

            if (Configuration.StuckEscalationEnabled)
            {
                ImGui.Indent();
                if (ImGui.InputInt("Jump after X stuck detections".Loc() + "##StuckJumpAfterXTimes", ref Configuration.StuckJumpAfterXTimes))
                {
                    Configuration.StuckJumpAfterXTimes = Math.Clamp(Configuration.StuckJumpAfterXTimes, 1, 60);
                    Configuration.Save();
                }

                if (ImGui.InputInt("Sidestep after X stuck detections".Loc() + "##StuckSidestepAfterXTimes", ref Configuration.StuckSidestepAfterXTimes))
                {
                    Configuration.StuckSidestepAfterXTimes = Math.Clamp(Configuration.StuckSidestepAfterXTimes, 1, 60);
                    Configuration.Save();
                }

                if (ImGui.InputFloat("Sidestep distance (in y)".Loc() + "##StuckSidestepDistance", ref Configuration.StuckSidestepDistance))
                {
                    Configuration.StuckSidestepDistance = Math.Clamp(Configuration.StuckSidestepDistance, 0.5f, 10f);
                    Configuration.Save();
                }

                if (ImGui.InputInt("Skip the current step after X stuck detections".Loc() + "##StuckSkipStepAfterXTimes", ref Configuration.StuckSkipStepAfterXTimes))
                {
                    Configuration.StuckSkipStepAfterXTimes = Math.Clamp(Configuration.StuckSkipStepAfterXTimes, 1, 60);
                    Configuration.Save();
                }
                ImGui.Unindent();
            }

            if (ImGui.Checkbox("Rebuild Navmesh when stuck".Loc(), ref Configuration.RebuildNavmeshOnStuck))
                Configuration.Save();

            if (Configuration.RebuildNavmeshOnStuck)
            {
                ImGui.SameLine();
                int rebuildX = Configuration.RebuildNavmeshAfterStuckXTimes;
                if(ImGui.InputInt("times".Loc() + "##RebuildNavmeshAfterStuckXTimes", ref rebuildX))
                {
                    Configuration.RebuildNavmeshAfterStuckXTimes = (byte) Math.Clamp(rebuildX, byte.MinValue+1, byte.MaxValue);
                    Configuration.Save();
                }
            }

            if (ImGui.InputInt("Stop after dying X times in one duty (0 = never)".Loc() + "##StopAfterDeathsInDuty", ref Configuration.StopAfterDeathsInDuty))
            {
                Configuration.StopAfterDeathsInDuty = Math.Clamp(Configuration.StopAfterDeathsInDuty, 0, 99);
                Configuration.Save();
            }
            ImGuiHelper.HelpMarkerAllowWhenDisabled("When you die this many times in the same duty, AutoDuty stops instead of reviving and running back in, and prints a chat message. If \"Ask Tataru to speak when AutoDuty stops on its own\" (Loop Termination Settings) is on and TataruPraise is installed, Tataru calls for help. Reviving and what to do next is left to you. The count starts over with every new duty or loop.".Loc());

            if(ImGui.Checkbox("Draw next steps in Path".Loc(), ref Configuration.PathDrawEnabled))
                Configuration.Save();
            ImGui.PopItemWidth();
            if (Configuration.PathDrawEnabled)
            {
                ImGui.Indent();
                ImGui.PushItemWidth(150f.Scale());
                if (ImGui.InputInt("Drawing X steps".Loc() + "##PathDrawStepCount", ref Configuration.PathDrawStepCount, 1))
                {
                    Configuration.PathDrawStepCount = Math.Max(1, Configuration.PathDrawStepCount);
                    Configuration.Save();
                }
                ImGui.PopItemWidth();
                ImGui.Unindent();
            }



            ImGuiHelper.SectionHeaderAlwaysEnabled("> ?? Config <".Loc(PathIdentifiers.W2W), ref w2wSettingHeaderSelected);

            if (w2wSettingHeaderSelected)
            {
                if(ImGui.Checkbox("Treat Unsync as W2W".Loc(), ref Configuration.TreatUnsyncAsW2W))
                    Configuration.Save();
                ImGuiHelper.HelpMarkerAllowWhenDisabled("Only works in paths with W2W tags on steps".Loc());

                if (ImGui.Checkbox("解限模式:交戰中繼續走到定點", ref Configuration.UnsyncedKeepMovingInCombat))
                    Configuration.Save();
                ImGuiHelper.HelpMarkerAllowWhenDisabled("只在「解除限制」生效時(隨機任務/討伐戰/大型任務)才有作用。\n" +
                                                        "開啟後交戰不會停在原地等打完,而是繼續走到下一個定點,技能沿路交給輪替插件(BossMod / Wrath)自己放。\n" +
                                                        "走位權會交給 vnavmesh 獨佔,BossMod 的自動移動在交戰期間會被關掉,離開戰鬥再還原。\n\n" +
                                                        "頭目戰、以及需要確實清怪才能推進的關卡不建議開。");


                // 清單整個在可見範圍外時 BeginListBox 回 false,這時不可以再呼叫 EndListBox:
                // 它會對目前這個不是子視窗的視窗做 EndChild/EndGroup,把 ImGui 的視窗與群組堆疊弄亂。
                using (ImRaii.IEndObject w2wList = ImRaii.ListBox("##W2WConfig", new System.Numerics.Vector2(ImGui.GetContentRegionAvail().X, 300)))
                    if (w2wList)
                        JobWithRoleHelper.DrawCategory(JobWithRole.All, ref Configuration.W2WJobs);
            }

            if (ImGui.Checkbox("Override Party Validation".Loc(), ref Configuration.OverridePartyValidation))
                Configuration.Save();
            ImGuiHelper.HelpMarkerAllowWhenDisabled("AutoDuty will ignore your party makeup when queueing for duties\nThis is for Multi-Boxing Only\n*AutoDuty is not recommended to be used with other players*".Loc());


            ImGuiHelper.SectionHeaderAlwaysEnabled("Advanced Config Options".Loc(), ref advModeHeaderSelected);

            if (advModeHeaderSelected == true)
            {
                if (ImGui.Checkbox("Using Alternative Rotation Plugin".Loc(), ref Configuration.UsingAlternativeRotationPlugin))
                    Configuration.Save();
                ImGuiHelper.HelpMarkerAllowWhenDisabled("You are deciding to use a plugin other than Wrath Combo, Rotation Solver or BossMod AutoRotation.".Loc());

                if (ImGui.Checkbox("Using Alternative Movement Plugin".Loc(), ref Configuration.UsingAlternativeMovementPlugin))
                    Configuration.Save();
                ImGuiHelper.HelpMarkerAllowWhenDisabled("You are deciding to use a plugin other than Vnavmesh.".Loc());

                if (ImGui.Checkbox("Using Alternative Boss Plugin".Loc(), ref Configuration.UsingAlternativeBossPlugin))
                    Configuration.Save();
                ImGuiHelper.HelpMarkerAllowWhenDisabled("You are deciding to use a plugin other than BossMod/BMR.".Loc());
            }
        }

        //Start of Pre-Loop Settings
        ImGui.Spacing();
        ImGui.Separator();
        ImGuiHelper.SectionHeaderAlwaysEnabled("Pre-Loop Initialization Settings".Loc(), ref preLoopHeaderSelected);

        if (preLoopHeaderSelected == true)
        {
            if (ImGui.Checkbox("Enable".Loc() + "###PreLoopEnable", ref Configuration.EnablePreLoopActions))
                Configuration.Save();

            using (ImRaii.Disabled(!Configuration.EnablePreLoopActions))
            {
                ImGui.Separator();
                MakeCommands("Execute commands on start of all loops",
                             ref Configuration.ExecuteCommandsPreLoop, ref Configuration.CustomCommandsPreLoop, ref preLoopCommand);

                ImGui.Separator();

                ImGui.TextColored(ImGuiHelper.VersionColor,
                                  "The following are also done between loop, if Between Loop is enabled (currently ??)".Loc(Configuration.EnableBetweenLoopActions ? "enabled".Loc() : "disabled".Loc()));

                if (ImGui.Checkbox("Retire To ".Loc(), ref Configuration.RetireMode))
                    Configuration.Save();

                using (ImRaii.Disabled(!Configuration.RetireMode))
                {
                    ImGui.SameLine(0, 5);
                    ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X);
                    if (ImGui.BeginCombo("##RetireLocation", Configuration.RetireLocationEnum.ToCustomString()))
                    {
                        foreach (RetireLocation retireLocation in Enum.GetValues(typeof(RetireLocation)))
                        {
                            if (ImGui.Selectable(retireLocation.ToCustomString()))
                            {
                                Configuration.RetireLocationEnum = retireLocation;
                                Configuration.Save();
                            }
                        }

                        ImGui.EndCombo();
                    }

                    if (Configuration is { RetireMode: true, RetireLocationEnum: RetireLocation.Personal_Home })
                    {
                        if (ImGui.Button("Add Current Position".Loc()))
                        {
                            Configuration.PersonalHomeEntrancePath.Add(Player.Position);
                            Configuration.Save();
                        }

                        ImGuiHelper
                           .HelpMarkerAllowWhenDisabled("For most houses where the door is a straight shot from teleport location this is not needed, in the rare situations where the door needs a path to get to it, you can create that path here, or if your door seems to be further away from the teleport location than your neighbors, simply goto your door and hit Add Current Position".Loc());

                        using (ImRaii.ListBox("##PersonalHomeVector3List", new System.Numerics.Vector2(ImGui.GetContentRegionAvail().X,
                                                                                                       (ImGui.GetTextLineHeightWithSpacing() * Configuration.PersonalHomeEntrancePath.Count) + 5)))
                        {
                            var removeItem = false;
                            var removeAt   = 0;

                            foreach (var item in Configuration.PersonalHomeEntrancePath.Select((Value, Index) => (Value, Index)))
                            {
                                ImGui.Selectable($"{item.Value}");
                                if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                                {
                                    removeItem = true;
                                    removeAt   = item.Index;
                                }
                            }

                            if (removeItem)
                            {
                                Configuration.PersonalHomeEntrancePath.RemoveAt(removeAt);
                                Configuration.Save();
                            }
                        }
                    }

                    if (Configuration is { RetireMode: true, RetireLocationEnum: RetireLocation.FC_Estate })
                    {
                        if (ImGui.Button("Add Current Position".Loc()))
                        {
                            Configuration.FCEstateEntrancePath.Add(Player.Position);
                            Configuration.Save();
                        }

                        ImGuiHelper
                           .HelpMarkerAllowWhenDisabled("For most houses where the door is a straight shot from teleport location this is not needed, in the rare situations where the door needs a path to get to it, you can create that path here, or if your door seems to be further away from the teleport location than your neighbors, simply goto your door and hit Add Current Position".Loc());

                        using (ImRaii.ListBox("##FCEstateVector3List", new System.Numerics.Vector2(ImGui.GetContentRegionAvail().X,
                                                                                                   (ImGui.GetTextLineHeightWithSpacing() * Configuration.FCEstateEntrancePath.Count) + 5)))
                        {
                            var removeItem = false;
                            var removeAt   = 0;

                            foreach (var item in Configuration.FCEstateEntrancePath.Select((Value, Index) => (Value, Index)))
                            {
                                ImGui.Selectable($"{item.Value}");
                                if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                                {
                                    removeItem = true;
                                    removeAt   = item.Index;
                                }
                            }

                            if (removeItem)
                            {
                                Configuration.FCEstateEntrancePath.RemoveAt(removeAt);
                                Configuration.Save();
                            }
                        }
                    }
                }

                if (ImGui.Checkbox("Auto Equip Recommended Gear".Loc(), ref Configuration.AutoEquipRecommendedGear))
                    Configuration.Save();

                ImGuiHelper.HelpMarkerAllowWhenDisabled("Uses Gear from Armory Chest Only".Loc());


                if (Configuration.AutoEquipRecommendedGear)
                {
                    ImGui.Indent();
                    using (ImRaii.Disabled(!Gearsetter_IPCSubscriber.IsEnabled))
                    {
                        if (ImGui.Checkbox("Consider items outside of armoury chest".Loc(), ref Configuration.AutoEquipRecommendedGearGearsetter))
                            Configuration.Save();

                        if (Configuration.AutoEquipRecommendedGearGearsetter)
                        {
                            ImGui.Indent();
                            if (ImGui.Checkbox("Move old items to inventory".Loc(), ref Configuration.AutoEquipRecommendedGearGearsetterOldToInventory))
                                Configuration.Save();
                            ImGuiHelper.HelpMarkerAllowWhenDisabled("Except for weapons, this will move the gear to be replaced to the inventory.".Loc());
                            ImGui.Unindent();
                        }
                    }

                    if (!Gearsetter_IPCSubscriber.IsEnabled)
                    {
                        if (Configuration.AutoEquipRecommendedGearGearsetter)
                        {
                            Configuration.AutoEquipRecommendedGearGearsetter = false;
                            Configuration.Save();
                        }

                        ImGui.Text("* Items outside the armoury chest requires Gearsetter plugin".Loc());
                        ImGui.Text("Get @ ".Loc());
                        ImGui.SameLine(0, 0);
                        ImGuiEx.TextCopy(ImGuiHelper.LinkColor, @"https://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json");
                    }

                    ImGui.Unindent();
                }

                if (ImGui.Checkbox("Auto Repair".Loc(), ref Configuration.AutoRepair))
                    Configuration.Save();

                if (Configuration.AutoRepair)
                {
                    ImGui.SameLine();

                    if (ImGui.RadioButton("Self".Loc(), Configuration.AutoRepairSelf))
                    {
                        Configuration.AutoRepairSelf = true;
                        Configuration.Save();
                    }

                    ImGui.SameLine();
                    ImGuiHelper.HelpMarkerAllowWhenDisabled("Will use DarkMatter to Self Repair (Requires Leveled Crafters!)".Loc());
                    ImGui.SameLine();

                    if (ImGui.RadioButton("CityNpc".Loc(), !Configuration.AutoRepairSelf))
                    {
                        Configuration.AutoRepairSelf = false;
                        Configuration.Save();
                    }

                    ImGui.SameLine();
                    ImGuiHelper.HelpMarkerAllowWhenDisabled("Will use preferred repair npc to repair.".Loc());
                    ImGui.Indent();
                    ImGui.Text("Trigger @".Loc());
                    ImGui.SameLine();
                    ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X);
                    int autoRepairPct = (int)Configuration.AutoRepairPct;
                    if (ImGui.SliderInt("##Repair@", ref autoRepairPct, 0, 99, "%d%%"))
                        Configuration.AutoRepairPct = Math.Clamp((uint)autoRepairPct, 0, 99);
                    if (ImGui.IsItemDeactivatedAfterEdit())
                        Configuration.Save();

                    ImGui.PopItemWidth();
                    if (!Configuration.AutoRepairSelf)
                    {
                        ImGui.Text("Preferred Repair NPC: ".Loc());
                        ImGuiHelper.HelpMarkerAllowWhenDisabled("It's a good idea to match the Repair NPC with Summoning Bell and if possible Retire Location".Loc());
                        ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X);
                        if (ImGui.BeginCombo("##PreferredRepair",
                                             Configuration.PreferredRepairNPC != null ?
                                                 $"{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Configuration.PreferredRepairNPC.Name.ToLowerInvariant())} ({Svc.Data.GetExcelSheet<TerritoryType>()?.GetRowOrDefault(Configuration.PreferredRepairNPC.TerritoryType)?.PlaceName.ValueNullable?.Name.ToString()})  ({MapHelper.ConvertWorldXZToMap(Configuration.PreferredRepairNPC.Position.ToVector2(), Svc.Data.GetExcelSheet<TerritoryType>().GetRow(Configuration.PreferredRepairNPC.TerritoryType).Map.Value!).X.ToString("0.0", CultureInfo.InvariantCulture)}, {MapHelper.ConvertWorldXZToMap(Configuration.PreferredRepairNPC.Position.ToVector2(), Svc.Data.GetExcelSheet<TerritoryType>().GetRow(Configuration.PreferredRepairNPC.TerritoryType).Map.Value).Y.ToString("0.0", CultureInfo.InvariantCulture)})" :
                                                 "Grand Company Inn".Loc()))
                        {
                            if (ImGui.Selectable("Grand Company Inn".Loc()))
                            {
                                Configuration.PreferredRepairNPC = null;
                                Configuration.Save();
                            }

                            foreach (RepairNpcData repairNPC in RepairNPCs)
                            {
                                if (repairNPC.TerritoryType <= 0)
                                {
                                    ImGui.Text(CultureInfo.InvariantCulture.TextInfo.ToTitleCase(repairNPC.Name.ToLowerInvariant()));
                                    continue;
                                }

                                var territoryType = Svc.Data.GetExcelSheet<TerritoryType>()?.GetRow(repairNPC.TerritoryType);

                                if (territoryType == null) continue;

                                if
                                    (ImGui.Selectable($"{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(repairNPC.Name.ToLowerInvariant())} ({territoryType.Value.PlaceName.ValueNullable?.Name.ToString()})  ({MapHelper.ConvertWorldXZToMap(repairNPC.Position.ToVector2(), territoryType.Value.Map.Value!).X.ToString("0.0", CultureInfo.InvariantCulture)}, {MapHelper.ConvertWorldXZToMap(repairNPC.Position.ToVector2(), territoryType.Value.Map.Value!).Y.ToString("0.0", CultureInfo.InvariantCulture)})"))
                                {
                                    Configuration.PreferredRepairNPC = repairNPC;
                                    Configuration.Save();
                                }
                            }

                            ImGui.EndCombo();
                        }

                        ImGui.PopItemWidth();
                    }

                    ImGui.Unindent();
                }

                if (ImGui.Checkbox("Auto Consume".Loc(), ref Configuration.AutoConsume))
                    Configuration.Save();

                ImGuiHelper.HelpMarkerAllowWhenDisabled("AutoDuty will consume these items on run and between each loop (if status does not exist)".Loc());
                if (Configuration.AutoConsume)
                {
                    ImGui.SameLine();
                    ImGui.Columns(3, "##AutoConsumeColumns");
                    //ImGui.SameLine(0, 5);
                    ImGui.NextColumn();
                    if (ImGui.Checkbox("Ignore Status".Loc(), ref Configuration.AutoConsumeIgnoreStatus))
                        Configuration.Save();

                    ImGuiHelper.HelpMarkerAllowWhenDisabled("AutoDuty will consume these items on run and between each loop every time (even if status does exists)".Loc());
                    ImGui.NextColumn();
                    //ImGui.SameLine(0, 5);

                    ImGui.PushItemWidth(80f.Scale());

                    using (ImRaii.Disabled(Configuration.AutoConsumeIgnoreStatus))
                    {
                        if (ImGui.InputInt("Min time remaining".Loc(), ref Configuration.AutoConsumeTime))
                        {
                            Configuration.AutoConsumeTime = Math.Clamp(Configuration.AutoConsumeTime, 0, 59);
                            Configuration.Save();
                        }

                        ImGuiHelper.HelpMarkerAllowWhenDisabled("If the status has less than this amount of time remaining (in minutes), it will consume these items".Loc());
                    }

                    ImGui.PopItemWidth();
                    ImGui.Columns(1);
                    ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X - 115f.Scale());
                    if (ImGui.BeginCombo("##SelectAutoConsumeItem", consumableItemsSelectedItem.Name))
                    {
                        ImGui.InputTextWithHint("Item Name".Loc(), "Start typing item name to search".Loc(), ref consumableItemsItemNameInput, 1000);
                        foreach (var item in ConsumableItems.Where(x => x.Name.Contains(consumableItemsItemNameInput, StringComparison.InvariantCultureIgnoreCase))!)
                        {
                            if (ImGui.Selectable($"{item.Name}"))
                            {
                                consumableItemsSelectedItem = item;
                            }
                        }

                        ImGui.EndCombo();
                    }

                    ImGui.PopItemWidth();

                    ImGui.SameLine(0, 5);
                    using (ImRaii.Disabled(consumableItemsSelectedItem == null))
                    {
                        if (ImGui.Button("Add Item".Loc()))
                        {
                            if (Configuration.AutoConsumeItemsList.Any(x => x.Key == consumableItemsSelectedItem!.StatusId))
                                Configuration.AutoConsumeItemsList.RemoveAll(x => x.Key == consumableItemsSelectedItem!.StatusId);

                            Configuration.AutoConsumeItemsList.Add(new(consumableItemsSelectedItem!.StatusId, consumableItemsSelectedItem));
                            Configuration.Save();
                        }
                    }

                    using (ImRaii.ListBox("##ConsumableItemList", new System.Numerics.Vector2(ImGui.GetContentRegionAvail().X,
                                                                                              (ImGui.GetTextLineHeightWithSpacing() * Configuration.AutoConsumeItemsList.Count) + 5)))
                    {
                        var                                  boolRemoveItem = false;
                        KeyValuePair<ushort, ConsumableItem> removeItem     = new();
                        foreach (var item in Configuration.AutoConsumeItemsList)
                        {
                            ImGui.Selectable($"{item.Value.Name}");
                            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                            {
                                boolRemoveItem = true;
                                removeItem     = item;
                            }
                        }

                        if (boolRemoveItem)
                        {
                            Configuration.AutoConsumeItemsList.Remove(removeItem);
                            Configuration.Save();
                        }
                    }
                }
            }
        }

        //Between Loop Settings
        ImGui.Spacing();
        ImGui.Separator();
        ImGuiHelper.SectionHeaderAlwaysEnabled("Between Loop Settings".Loc(), ref betweenLoopHeaderSelected);

        if (betweenLoopHeaderSelected == true)
        {
            ImGui.Columns(2, "##BetweenLoopHeaderColumns");

            if (ImGui.Checkbox("Enable".Loc() + "###BetweenLoopEnable", ref Configuration.EnableBetweenLoopActions))
                Configuration.Save();

            using (ImRaii.Disabled(!Configuration.EnableBetweenLoopActions))
            {
                ImGui.NextColumn();

                if (ImGui.Checkbox("Run on last Loop".Loc() + "###BetweenLoopEnableLastLoop", ref Configuration.ExecuteBetweenLoopActionLastLoop))
                    Configuration.Save();

                ImGui.Columns(1);

                ImGui.Separator();
                ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X - ImGui.CalcItemWidth());
                if (ImGui.InputInt("(s) Wait time between loops".Loc(), ref Configuration.WaitTimeBeforeAfterLoopActions))
                {
                    if (Configuration.WaitTimeBeforeAfterLoopActions < 0) Configuration.WaitTimeBeforeAfterLoopActions = 0;
                    Configuration.Save();
                }
                ImGui.PopItemWidth();
                ImGuiHelper.HelpMarkerAllowWhenDisabled("Will delay all AutoDuty between-loop Processes for X seconds.".Loc());
                ImGui.Separator();

                MakeCommands("Execute commands in between of all loops",
                             ref Configuration.ExecuteCommandsBetweenLoop,    ref Configuration.CustomCommandsBetweenLoop, ref betweenLoopCommand);

                if (ImGui.Checkbox("Auto Extract".Loc(), ref Configuration.AutoExtract))
                    Configuration.Save();

                if (Configuration.AutoExtract)
                {
                    ImGui.SameLine(0, 10);
                    if (ImGui.RadioButton("Equipped".Loc(), !Configuration.autoExtractAll))
                    {
                        Configuration.AutoExtractAll = false;
                        Configuration.Save();
                    }
                    ImGui.SameLine(0, 5);
                    if (ImGui.RadioButton("All".Loc(), Configuration.autoExtractAll))
                    {
                        Configuration.AutoExtractAll = true;
                        Configuration.Save();
                    }
                }

                if (ImGui.Checkbox("Auto open gear coffers".Loc(), ref Configuration.AutoOpenCoffers))
                    Configuration.Save();

                ImGuiHelper.HelpMarkerAllowWhenDisabled("AutoDuty will open gear coffers (like paladin arms) between each loop".Loc());
                if (Configuration.AutoOpenCoffers)
                {
                    unsafe
                    {
                        ImGui.Indent();
                        ImGui.Text("Open Coffers with Gearset: ".Loc());
                        ImGui.AlignTextToFramePadding();
                        ImGui.SameLine();

                        // RaptureGearsetModule.Instance() 是 FFXIVClientStructs 裡手寫的取得子,UIModule 尚未建立時會回 null,
                        // 而設定視窗是每幀重畫的。判空後同幀即用;為 null 時整列改顯示「尚未就緒」——
                        // 不畫成空白,也不畫成看起來合法的「目前配裝」,免得使用者誤以為設定已生效。
                        RaptureGearsetModule* module = RaptureGearsetModule.Instance();

                        if (module == null)
                        {
                            ImGui.TextDisabled("Gearset data not ready".Loc());
                        }
                        else
                        {
                            if (Configuration.AutoOpenCoffersGearset != null && !module->IsValidGearset((int) Configuration.AutoOpenCoffersGearset))
                            {
                                Configuration.AutoOpenCoffersGearset = null;
                                Configuration.Save();
                            }

                            // GetGearset() 是原生 MemberFunction,查無此 id 會回 null。
                            // 預覽字串與清單列都先判空:取不到就退回「目前配裝」(預覽)或跳過該列(清單)。
                            RaptureGearsetModule.GearsetEntry* selectedGearset =
                                Configuration.AutoOpenCoffersGearset != null ? module->GetGearset(Configuration.AutoOpenCoffersGearset.Value) : null;

                            if (ImGui.BeginCombo("##CofferGearsetSelection", selectedGearset != null ? selectedGearset->NameString : "Current Gearset".Loc()))
                            {
                                if (ImGui.Selectable("Current Gearset".Loc()))
                                {
                                    Configuration.AutoOpenCoffersGearset = null;
                                    Configuration.Save();
                                }

                                for (int i = 0; i < module->NumGearsets; i++)
                                {
                                    RaptureGearsetModule.GearsetEntry* gearset = module->GetGearset(i);
                                    if (gearset == null)
                                        continue;

                                    if(ImGui.Selectable(gearset->NameString))
                                    {
                                        Configuration.AutoOpenCoffersGearset = gearset->Id;
                                        Configuration.Save();
                                    }
                                }

                                ImGui.EndCombo();
                            }
                        }

                        if (ImGui.Checkbox("Use Blacklist".Loc(), ref Configuration.AutoOpenCoffersBlacklistUse))
                            Configuration.Save();

                        ImGuiHelper.HelpMarkerAllowWhenDisabled("Option to disable some coffers from being opened automatically.".Loc());
                        if (Configuration.AutoOpenCoffersBlacklistUse)
                        {
                            if (ImGui.BeginCombo("Select Coffer".Loc(), autoOpenCoffersSelectedItem.Value))
                            {
                                ImGui.InputTextWithHint("Coffer Name".Loc(), "Start typing coffer name to search".Loc(), ref autoOpenCoffersNameInput, 1000);
                                foreach (var item in Items.Where(x => CofferHelper.ValidCoffer(x.Value) && x.Value.Name.ToString().Contains(autoOpenCoffersNameInput, StringComparison.InvariantCultureIgnoreCase)))
                                {
                                    if (ImGui.Selectable($"{item.Value.Name.ToString()}"))
                                        autoOpenCoffersSelectedItem = new KeyValuePair<uint, string>(item.Key, item.Value.Name.ToString());
                                }
                                ImGui.EndCombo();
                            }

                            ImGui.SameLine(0, 5);
                            using (ImRaii.Disabled(autoOpenCoffersSelectedItem.Value.IsNullOrEmpty()))
                            {
                                if (ImGui.Button("Add Coffer".Loc()))
                                {
                                    if (!Configuration.AutoOpenCoffersBlacklist.TryAdd(autoOpenCoffersSelectedItem.Key, autoOpenCoffersSelectedItem.Value))
                                    {
                                        Configuration.AutoOpenCoffersBlacklist.Remove(autoOpenCoffersSelectedItem.Key);
                                        Configuration.AutoOpenCoffersBlacklist.Add(autoOpenCoffersSelectedItem.Key, autoOpenCoffersSelectedItem.Value);
                                    }
                                    autoOpenCoffersSelectedItem = new(0, "");
                                    Configuration.Save();
                                }
                            }
                            
                            if (!ImGui.BeginListBox("##CofferBlackList", new System.Numerics.Vector2(ImGui.GetContentRegionAvail().X, (ImGui.GetTextLineHeightWithSpacing() * Configuration.AutoOpenCoffersBlacklist.Count) + 5))) return;

                            foreach (var item in Configuration.AutoOpenCoffersBlacklist)
                            {
                                ImGui.Selectable($"{item.Value}");
                                if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                                {
                                    Configuration.AutoOpenCoffersBlacklist.Remove(item);
                                    Configuration.Save();
                                }
                            }
                            ImGui.EndListBox();
                        }
                        
                        ImGui.Unindent();
                    }
                }

                using (ImRaii.Disabled(!DiscardHelper_IPCSubscriber.IsEnabled))
                {
                    if (ImGui.Checkbox("Discard Items".Loc(), ref Configuration.DiscardItems))
                    {
                        Configuration.Save();
                    }
                }
                if (!DiscardHelper_IPCSubscriber.IsEnabled)
                {
                    if (Configuration.DiscardItems)
                    {
                        Configuration.DiscardItems = false;
                        Configuration.Save();
                    }
                    ImGui.SameLine();
                    ImGui.Text("* Discarding Items Requires DiscardHelper plugin!".Loc());
                    ImGui.SameLine();
                    ImGui.Text("Get @ ".Loc());
                    ImGui.SameLine(0, 0);
                    // 這條仍是國際服的庫，因為台服艦隊沒有 DiscardHelper 的移植版可指。
                    ImGuiEx.TextCopy(ImGuiHelper.LinkColor, @"https://puni.sh/api/repository/vera");
                }


                ImGui.Columns(2, "##DesynthColumns");

                if (ImGui.Checkbox("Auto Desynth".Loc(), ref Configuration.autoDesynth))
                {
                    Configuration.AutoDesynth = Configuration.autoDesynth;
                    Configuration.Save();
                }
                ImGui.NextColumn();
                //ImGui.SameLine(0, 5);
                using (ImRaii.Disabled(!AutoRetainer_IPCSubscriber.IsEnabled))
                {
                    if (ImGui.Checkbox("Auto GC Turnin".Loc(), ref Configuration.autoGCTurnin))
                    {
                        Configuration.AutoGCTurnin = Configuration.autoGCTurnin;
                        Configuration.Save();
                    }
                    ImGuiHelper.HelpMarkerAllowWhenDisabled("Runs Grand Company expert delivery at the end of each loop. AutoDuty travels to your Grand Company, then AutoRetainer hands in the gear in your inventory.\n\nIt never switches character and never starts AutoRetainer's multi-character loop.\n\nHeads up: AutoRetainer runs its full Deliver Items flow, which visits the quartermaster and spends your Grand Company seals on ventures before handing anything in. To keep a reserve, set 'Seals to keep' in AutoRetainer's Grand Company exchange plan.".Loc());
                    
                    ImGui.NextColumn();

                    //slightly cursed
                    // Enabled() 會解開外層所有 ImRaii 停用層，連整頁的鎖（別的外掛暫時覆寫中／「純淨」設定檔）也一起；
                    // 這裡本意只跳過上面 AutoRetainer 那層，所以整頁的鎖重套一次。
                    // 刻意不重套「輪間設定」啟用開關那層：維持沒勾也能先調這兩項的原本行為。
                    using (ImRaii.Enabled())
                    using (ImRaii.Disabled(overridesActive || bareProfile))
                    {
                        if (Configuration.AutoDesynth)
                        {
                            ImGui.Indent();
                            if (ImGui.Checkbox("Only Skill Ups".Loc(), ref Configuration.autoDesynthSkillUp))
                            {
                                Configuration.AutoDesynthSkillUp = Configuration.autoDesynthSkillUp;
                                Configuration.Save();
                            }
                            if (Configuration.AutoDesynthSkillUp)
                            {
                                ImGui.Indent();
                                ImGui.Text("Item Level Limit".Loc());
                                ImGuiHelper.HelpMarkerAllowWhenDisabled("Stops desynthesising an item once your desynthesis skill reaches the Item Level + this limit.".Loc());
                                ImGui.SameLine();
                                ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X);
                                if (ImGui.SliderInt("##AutoDesynthSkillUpLimit", ref Configuration.AutoDesynthSkillUpLimit, 0, 50))
                                    Configuration.AutoDesynthSkillUpLimit = Math.Clamp(Configuration.AutoDesynthSkillUpLimit, 0, 50);
                                if (ImGui.IsItemDeactivatedAfterEdit())
                                    Configuration.Save();
                                ImGui.PopItemWidth();
                                ImGui.Unindent();
                            }
                            ImGui.Unindent();
                        }
                    }

                    if (Configuration.AutoGCTurnin)
                    {
                        ImGui.NextColumn();

                        ImGui.Indent();
                        if (ImGui.Checkbox("Inventory Slots Left @".Loc(), ref Configuration.AutoGCTurninSlotsLeftBool))
                            Configuration.Save();
                        ImGui.SameLine(0);
                        using (ImRaii.Disabled(!Configuration.AutoGCTurninSlotsLeftBool))
                        {
                            ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X);
                            if (Configuration.UseSliderInputs)
                            {
                                if (ImGui.SliderInt("##Slots", ref Configuration.AutoGCTurninSlotsLeft, 0, 140))
                                    Configuration.AutoGCTurninSlotsLeft = Math.Clamp(Configuration.AutoGCTurninSlotsLeft, 0, 140);
                                if (ImGui.IsItemDeactivatedAfterEdit())
                                    Configuration.Save();
                            }
                            else
                            {
                                Configuration.AutoGCTurninSlotsLeft = Math.Clamp(Configuration.AutoGCTurninSlotsLeft, 0, 140);

                                if (ImGui.InputInt("##Slots", ref Configuration.AutoGCTurninSlotsLeft))
                                {
                                    Configuration.AutoGCTurninSlotsLeft = Math.Clamp(Configuration.AutoGCTurninSlotsLeft, 0, 140);
                                    Configuration.Save();
                                }
                            }
                            ImGui.PopItemWidth();
                        }
                        if (ImGui.Checkbox("Use GC Aetheryte Ticket".Loc(), ref Configuration.AutoGCTurninUseTicket))
                            Configuration.Save();
                        ImGui.Unindent();
                    }
                }
                ImGui.Columns(1);

                if (!AutoRetainer_IPCSubscriber.IsEnabled)
                {
                    if (Configuration.AutoGCTurnin)
                    {
                        Configuration.AutoGCTurnin = false;
                        Configuration.Save();
                    }
                    ImGui.Text("* Auto GC Turnin Requires AutoRetainer plugin".Loc());
                    ImGui.Text("Get @ ".Loc());
                    ImGui.SameLine(0, 0);
                    ImGuiEx.TextCopy(ImGuiHelper.LinkColor, @"https://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json");
                }

                if(ImGui.Checkbox("Triple Triad".Loc(), ref Configuration.TripleTriadEnabled))
                    Configuration.Save();
                ImGui.SameLine();
                ImGui.TextColored(Configuration.TripleTriadEnabled ? GradientColor.Get(ImGuiHelper.ExperimentalColor, ImGuiHelper.ExperimentalColor2, 500) : ImGuiHelper.ExperimentalColor, "EXPERIMENTAL".Loc());
                if (Configuration.TripleTriadEnabled)
                {
                    ImGui.Indent();
                    if (ImGui.Checkbox("Register Triple Triad Cards".Loc(), ref Configuration.TripleTriadRegister))
                        Configuration.Save();
                    if (ImGui.Checkbox("Sell Triple Triad Cards".Loc(), ref Configuration.TripleTriadSell))
                        Configuration.Save();
                    ImGui.Unindent();
                }

                using (ImRaii.Disabled(!AutoRetainer_IPCSubscriber.IsEnabled))
                {
                    if (ImGui.Checkbox("Enable AutoRetainer Integration".Loc(), ref Configuration.EnableAutoRetainer))
                        Configuration.Save();
                }
                if (Configuration.EnableAutoRetainer)
                {
                    ImGui.Text("Preferred Summoning Bell Location: ".Loc());
                    ImGuiHelper.HelpMarkerAllowWhenDisabled("No matter what location is chosen, if there is a summoning bell in the location you are in when this is invoked it will go there instead".Loc());
                    if (ImGui.BeginCombo("##PreferredBell", Configuration.PreferredSummoningBellEnum.ToCustomString()))
                    {
                        foreach (SummoningBellLocations summoningBells in Enum.GetValues(typeof(SummoningBellLocations)))
                        {
                            if (ImGui.Selectable(summoningBells.ToCustomString()))
                            {
                                Configuration.PreferredSummoningBellEnum = summoningBells;
                                Configuration.Save();
                            }
                        }
                        ImGui.EndCombo();
                    }
                }
                if (!AutoRetainer_IPCSubscriber.IsEnabled)
                {
                    if (Configuration.EnableAutoRetainer)
                    {
                        Configuration.EnableAutoRetainer = false;
                        Configuration.Save();
                    }
                    ImGui.Text("* AutoRetainer requires a plugin".Loc());
                    ImGui.Text("Visit ".Loc());
                    ImGui.SameLine(0, 0);
                    ImGuiEx.TextCopy(ImGuiHelper.LinkColor, @"https://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json");
                }
            }
        }

        //Loop Termination Settings
        ImGui.Spacing();
        ImGui.Separator();
        ImGuiHelper.SectionHeaderAlwaysEnabled("Loop Termination Settings".Loc(), ref terminationHeaderSelected);
        if (terminationHeaderSelected == true)
        {
            if (ImGui.Checkbox("Enable".Loc() + "###TerminationEnable", ref Configuration.EnableTerminationActions))
                Configuration.Save();

            // 刻意放在 ImRaii.Disabled 之外:自己停下來的通知不該被「終止動作」總開關牽連,
            // 因為失敗停止的那兩條路徑根本不經過終止動作。
            if (ImGui.Checkbox("Notify when AutoDuty stops on its own".Loc(), ref Configuration.NotifyWhenStoppedItself))
                Configuration.Save();
            ImGuiHelper.HelpMarkerAllowWhenDisabled("Shows a Dalamud notification when AutoDuty stops because it finished all loops or hit an error. Stopping it yourself never notifies.".Loc());

            // 與上面那個勾選框刻意分開：桌面通知與語音通知是兩件事。
            if (ImGui.Checkbox("Ask Tataru to speak when AutoDuty stops on its own (requires TataruPraise)".Loc(), ref Configuration.TataruPraiseOnStoppedItself))
                Configuration.Save();
            ImGuiHelper.HelpMarkerAllowWhenDisabled("Needs the TataruPraise plugin installed and its own master switch turned on. Without it this option does nothing at all - no error, no sound. Stopping AutoDuty yourself never speaks.".Loc());

            using (ImRaii.Disabled(!Configuration.EnableTerminationActions))
            {
                ImGui.Separator();

                if (ImGui.Checkbox("Stop Looping @ Level".Loc(), ref Configuration.StopLevel))
                    Configuration.Save();

                if (Configuration.StopLevel)
                {
                    ImGui.SameLine(0, 10);
                    ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X);
                    if (Configuration.UseSliderInputs)
                    {
                        if (ImGui.SliderInt("##Level", ref Configuration.StopLevelInt, 1, 100))
                            Configuration.StopLevelInt = Math.Clamp(Configuration.StopLevelInt, 1, 100);
                        if (ImGui.IsItemDeactivatedAfterEdit())
                            Configuration.Save();
                    }
                    else
                    {
                        if (ImGui.InputInt("##Level", ref Configuration.StopLevelInt))
                        {
                            Configuration.StopLevelInt = Math.Clamp(Configuration.StopLevelInt, 1, 100);
                            Configuration.Save();
                        }
                    }
                    ImGui.PopItemWidth();
                }
                ImGuiHelper.HelpMarkerAllowWhenDisabled("Looping will stop when these conditions are reached, so long as an adequate number of loops have been allocated.".Loc());
                if (ImGui.Checkbox("Stop When No Rested XP".Loc(), ref Configuration.StopNoRestedXP))
                    Configuration.Save();

                ImGuiHelper.HelpMarkerAllowWhenDisabled("Looping will stop when these conditions are reached, so long as an adequate number of loops have been allocated.".Loc());
                if (ImGui.Checkbox("Stop Looping When Reach Item Qty".Loc(), ref Configuration.StopItemQty))
                    Configuration.Save();

                ImGuiHelper.HelpMarkerAllowWhenDisabled("Looping will stop when these conditions are reached, so long as an adequate number of loops have been allocated.".Loc());
                if (Configuration.StopItemQty)
                {
                    ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X - 125f.Scale());
                    if (ImGui.BeginCombo("Select Item".Loc(), stopItemQtySelectedItem.Value))
                    {
                        ImGui.InputTextWithHint("Item Name".Loc(), "Start typing item name to search".Loc(), ref stopItemQtyItemNameInput, 1000);
                        foreach (var item in Items.Where(x => x.Value.Name.ToString().Contains(stopItemQtyItemNameInput, StringComparison.InvariantCultureIgnoreCase))!)
                        {
                            if (ImGui.Selectable($"{item.Value.Name.ToString()}"))
                                stopItemQtySelectedItem = new KeyValuePair<uint, string>(item.Key, item.Value.Name.ToString());
                        }
                        ImGui.EndCombo();
                    }
                    ImGui.PopItemWidth();
                    ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X - 220f.Scale());
                    if (ImGui.InputInt("Quantity".Loc(), ref Configuration.StopItemQtyInt))
                        Configuration.Save();

                    ImGui.SameLine(0, 5);
                    using (ImRaii.Disabled(stopItemQtySelectedItem.Value.IsNullOrEmpty()))
                    {
                        if (ImGui.Button("Add Item".Loc()))
                        {
                            if (!Configuration.StopItemQtyItemDictionary.TryAdd(stopItemQtySelectedItem.Key, new(stopItemQtySelectedItem.Value, Configuration.StopItemQtyInt)))
                            {
                                Configuration.StopItemQtyItemDictionary.Remove(stopItemQtySelectedItem.Key);
                                Configuration.StopItemQtyItemDictionary.Add(stopItemQtySelectedItem.Key, new(stopItemQtySelectedItem.Value, Configuration.StopItemQtyInt));
                            }
                            Configuration.Save();
                        }
                    }
                    ImGui.PopItemWidth();
                    if (!ImGui.BeginListBox("##ItemList", new System.Numerics.Vector2(ImGui.GetContentRegionAvail().X, (ImGui.GetTextLineHeightWithSpacing() * Configuration.StopItemQtyItemDictionary.Count) + 5))) return;

                    foreach (var item in Configuration.StopItemQtyItemDictionary)
                    {
                        ImGui.Selectable($"{item.Value.Key} ({"Qty: ".Loc()}{item.Value.Value})");
                        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                        {
                            Configuration.StopItemQtyItemDictionary.Remove(item);
                            Configuration.Save();
                        }
                    }
                    ImGui.EndListBox();
                    if (ImGui.Checkbox("Stop Looping Only When All Items Obtained".Loc(), ref Configuration.StopItemAll))
                        Configuration.Save();
                }

                MakeCommands("Execute commands on termination of all loops",
                             ref Configuration.ExecuteCommandsTermination,  ref Configuration.CustomCommandsTermination, ref terminationCommand);

                if (ImGui.Checkbox("Play Sound on Completion of All Loops: ".Loc(), ref Configuration.PlayEndSound)) //Heavily Inspired by ChatAlerts
                        Configuration.Save();
                if (Configuration.PlayEndSound)
                {
                    if (ImGuiEx.IconButton(FontAwesomeIcon.Play, "##ConfigSoundTest", new Vector2(ImGui.GetItemRectSize().Y)))
                        SoundHelper.StartSound(Configuration.PlayEndSound, Configuration.CustomSound, Configuration.SoundEnum);
                    ImGui.SameLine();
                    DrawGameSound();
                }

                ImGui.Text("On Completion of All Loops: ".Loc());
                ImGui.SameLine(0, 10);
                ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X);
                if (ImGui.BeginCombo("##ConfigTerminationMethod", Configuration.TerminationMethodEnum.ToCustomString()))
                {
                    foreach (TerminationMode terminationMode in Enum.GetValues(typeof(TerminationMode)))
                    {
                        if (terminationMode != TerminationMode.Kill_PC || OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
                            if (ImGui.Selectable(terminationMode.ToCustomString()))
                            {
                                Configuration.TerminationMethodEnum = terminationMode;
                                Configuration.Save();
                            }
                    }
                    ImGui.EndCombo();
                }

                if (Configuration.TerminationMethodEnum is TerminationMode.Kill_Client or TerminationMode.Kill_PC or TerminationMode.Logout)
                {
                    ImGui.Indent();
                    if (ImGui.Checkbox("Keep Termination option after execution ".Loc(), ref Configuration.TerminationKeepActive))
                        Configuration.Save();
                    ImGui.Unindent();
                }
            }
        }

        void MakeCommands(string checkbox, ref bool execute, ref List<string> commands, ref string curCommand)
        {
            if (ImGui.Checkbox($"{checkbox.Loc()}{(execute ? ":" : string.Empty)} ", ref execute))
                Configuration.Save();

            ImGuiHelper.HelpMarkerAllowWhenDisabled("??.\nFor example, /echo test".Loc(checkbox));

            if (execute)
            {
                ImGui.Indent();
                ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X - 185f.Scale());
                if (ImGui.InputTextWithHint($"##Commands{checkbox}", "enter command starting with /".Loc(), ref curCommand, 500, ImGuiInputTextFlags.EnterReturnsTrue))
                {
                    if (!curCommand.IsNullOrEmpty() && curCommand[0] == '/' && (ImGui.IsKeyDown(ImGuiKey.Enter) || ImGui.IsKeyDown(ImGuiKey.KeypadEnter)))
                    {
                        Configuration.CustomCommandsPreLoop.Add(curCommand);
                        curCommand = string.Empty;
                        Configuration.Save();
                    }
                }
                ImGui.PopItemWidth();

                ImGui.SameLine(0, 5);
                using (ImRaii.Disabled(curCommand.IsNullOrEmpty() || curCommand[0] != '/'))
                {
                    if (ImGui.Button("Add Command".Loc() + $"##CommandButton{checkbox}"))
                    {
                        commands.Add(curCommand);
                        Configuration.Save();
                    }
                }
                if (!ImGui.BeginListBox($"##CommandList{checkbox}", new System.Numerics.Vector2(ImGui.GetContentRegionAvail().X, (ImGui.GetTextLineHeightWithSpacing() * commands.Count) + 5))) 
                    return;

                var removeItem = false;
                var removeAt   = 0;

                foreach (var item in commands.Select((Value, Index) => (Value, Index)))
                {
                    ImGui.Selectable($"{item.Value}##Selectable{checkbox}");
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    {
                        removeItem = true;
                        removeAt   = item.Index;
                    }
                }
                if (removeItem)
                {
                    commands.RemoveAt(removeAt);
                    Configuration.Save();
                }
                ImGui.EndListBox();
                ImGui.Unindent();
            }
        }
    }

    private static void DrawGameSound()
    {
        ImGui.SameLine(0, 10);
        ImGui.PushItemWidth(150f.Scale());
        if (ImGui.BeginCombo("##ConfigEndSoundMethod", Configuration.SoundEnum.ToName()))
        {
            foreach (var sound in _validSounds)
            {
                if (ImGui.Selectable(sound.ToName()))
                {
                    Configuration.SoundEnum = sound;
                    UIGlobals.PlaySoundEffect((uint)sound);
                    Configuration.Save();
                }
            }
            ImGui.EndCombo();
        }
    }
}
