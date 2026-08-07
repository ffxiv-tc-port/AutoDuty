using ECommons;
using ECommons.ImGuiMethods;
using ECommons.LanguageHelpers;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using System.Diagnostics;
using System.Linq;

namespace AutoDuty.Windows
{
    internal static class InfoTab
    {
        /// <summary>
        /// AutoDuty 會透過 IPC 使用的外掛,對應到**本艦隊 feed 裡的**版本。
        ///
        /// 🔴 這裡刻意**不做自動安裝**(上游 Helpers/PluginInstaller.cs 用
        /// DalamudReflector.AddPlugin 直接裝)。原因:上游那份清單指的是國際服 API15 的
        /// 儲存庫,而其中 AutoRetainer / WrathCombo / Lifestream / BossMod 與本艦隊的
        /// fork **同 InternalName** ⇒ 自動安裝會把使用者的台服 fork 換成 API15 版本,
        /// 直接壞掉。改成只開啟網頁,由使用者自己決定裝什麼。
        ///
        /// ⚠️ 本清單只列本 feed 真的有的。上游清單裡的 RotationSolver / Stylist /
        /// AntiAfkKick / PandorasBox / GlamourLog 本 feed 沒有,不列,避免把人導去國際服版本。
        /// </summary>
        private static readonly (string internalName, string display, string repoUrl)[] fleetPlugins =
        [
            ("vnavmesh",      "vnavmesh",      "https://github.com/ffxiv-tc-port/vnavmesh"),
            ("BossModReborn", "Bossmod Reborn", "https://github.com/ffxiv-tc-port/BossmodReborn"),
            ("WrathCombo",    "Wrath Combo",   "https://github.com/ffxiv-tc-port/WrathCombo"),
            ("AutoRetainer",  "AutoRetainer",  "https://github.com/ffxiv-tc-port/AutoRetainer"),
            ("Lifestream",    "Lifestream",    "https://github.com/ffxiv-tc-port/Lifestream"),
            ("Gearsetter",    "Gearsetter",    "https://github.com/ffxiv-tc-port/Gearsetter"),
            ("Avarice",       "Avarice",       "https://github.com/ffxiv-tc-port/Avarice"),
            ("YesAlready",    "YesAlready",    "https://github.com/ffxiv-tc-port/YesAlready"),
            ("Marketbuddy",   "Marketbuddy",   "https://github.com/ffxiv-tc-port/Marketbuddy"),
        ];

        private static void DrawFleetPluginList()
        {
            ImGui.NewLine();
            ImGuiEx.TextWrapped("AutoDuty works with the plugins below. These links point at this fleet's own Traditional Chinese forks - AutoDuty will not install anything for you, because installing the international builds would replace your fork of the same plugin with a version that does not work here.".Loc());
            ImGui.NewLine();

            if (!ImGui.BeginTable("##FleetPlugins", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
                return;

            try
            {
                ImGui.TableSetupColumn("##Name",   ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("##Status", ImGuiTableColumnFlags.WidthFixed, 130 * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("##Open",   ImGuiTableColumnFlags.WidthFixed, 90 * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);

                foreach ((string internalName, string display, string repoUrl) in fleetPlugins)
                {
                    ImGui.TableNextRow();

                    ImGui.TableSetColumnIndex(0);
                    ImGui.AlignTextToFramePadding();
                    ImGui.Text(display);

                    // 狀態一律畫在列上:使用者要能掃視就看出誰沒裝。
                    ImGui.TableSetColumnIndex(1);
                    ImGui.AlignTextToFramePadding();
                    var installed = PluginInterface.InstalledPlugins.FirstOrDefault(p => p.InternalName == internalName);
                    if (installed == null)
                        ImGui.TextColored(ImGuiColors.DalamudGrey, "not installed".Loc());
                    else if (installed.IsLoaded)
                        ImGui.TextColored(ImGuiColors.HealerGreen, "installed".Loc());
                    else
                        ImGui.TextColored(ImGuiColors.DalamudYellow, "installed, off".Loc());

                    ImGui.TableSetColumnIndex(2);
                    if (ImGui.Button($"{"Open".Loc()}##open{internalName}"))
                        GenericHelpers.ShellStart(repoUrl);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(repoUrl);
                }
            }
            finally
            {
                ImGui.EndTable();
            }
        }

        static string infoUrl = "https://docs.google.com/spreadsheets/d/151RlpqRcCpiD_VbQn6Duf-u-S71EP7d0mx3j1PDNoNA";
        // 🔴 指向本 fork 的 issues:原上游 ffxivcode/AutoDuty 已於 2026-01 封存,
        // 送去那裡的回報不會有人處理。
        static string gitIssueUrl = "https://github.com/ffxiv-tc-port/AutoDuty/issues";
        static string punishDiscordUrl = "https://discord.com/channels/1001823907193552978/1236757595738476725";
        static string ffxivcodeDiscordUrl = "https://discord.com/channels/1241050921732014090/1273374407653462017";
        private static Configuration Configuration = Plugin.Configuration;

        public static void Draw()
        {
            if (MainWindow.CurrentTabName != "Info")
                MainWindow.CurrentTabName = "Info";
            ImGui.NewLine();
            ImGuiEx.TextWrapped("For assistance with general setup for both AutoDuty and it's dependencies, be sure to check out the setup guide below for more information:".Loc());
            ImGui.NewLine();
            ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize("Information and Setup".Loc()).X) / 2);
            if (ImGui.Button("Information and Setup".Loc()))
                Process.Start("explorer.exe", infoUrl);
            ImGui.NewLine();
            ImGuiEx.TextWrapped("The above guide also has information on the status of each path, such as Path maturity, module maturity, and general consistency of each path. You can also review additional notes or considerations, that may need to be made on your part for successful looping. For requests, issues, or contributions to AD, please use the AutoDuty Github to open an issue:".Loc());
            ImGui.NewLine();
            ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize("GitHub Issues".Loc()).X) / 2);
            if (ImGui.Button("GitHub Issues".Loc()))
                Process.Start("explorer.exe", gitIssueUrl);
            ImGui.NewLine();
            ImGuiEx.TextCentered("For everything else, join the discord!".Loc());
            ImGui.NewLine();
            ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize("Punish Discord".Loc()).X) / 2);
            if (ImGui.Button("Punish Discord".Loc()))
                Process.Start("explorer.exe", punishDiscordUrl);
            ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize("FFXIVCode Discord".Loc()).X) / 2);
            if (ImGui.Button("FFXIVCode Discord".Loc()))
                Process.Start("explorer.exe", ffxivcodeDiscordUrl);

            ImGui.NewLine();
            ImGui.Separator();
            DrawFleetPluginList();
        }
    }
}
