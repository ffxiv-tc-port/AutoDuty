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
    /// 多開協調(Multibox)設定區塊。
    ///
    /// 🔴 這裡是**唯一**的啟用入口:整個外掛沒有任何其他地方會把 MultiBox 打開。
    ///    開關本身不落地(MultiboxConfiguration.MultiBox 標了 [JsonIgnore]),
    ///    所以每次重載外掛都是關的。
    /// </summary>
    private static void DrawMultiboxSection()
    {
        MultiboxUtility.MultiboxConfiguration mb = MultiboxUtility.Config;

        ImGui.Separator();
        ImGui.Spacing();
        ImGuiHelper.SectionHeaderAlwaysEnabled("Multibox Settings".Loc(), ref multiboxHeaderSelected);

        if (!multiboxHeaderSelected)
            return;

        ImGui.Indent();

        // ── 開關(手動觸發,預設關,不落地) ────────────────────────────────
        bool enabled = mb.MultiBox;
        if (ImGui.Checkbox("Enable Multibox".Loc(), ref enabled))
            mb.MultiBox = enabled;
        ImGuiHelper.HelpMarkerAllowWhenDisabled("Coordinates several game clients running AutoDuty together.\n\nWhat currently works: the host invites the clients to its party, the host's queue makes the clients accept their duty pop, and deaths are reported to the host.\n\nNot yet wired up: per-step lockstep along the path and sending the host's path to the clients. Clients still walk their own path on their own timing.\n\nThis switch is never saved - AutoDuty always starts with Multibox off, and nothing turns it on by itself. You must tick it manually on every client each time.".Loc());

        // ── 狀態:一律畫在列上,「不知道」也要看得見(不要畫成 0) ──────────
        ImGui.SameLine();
        if (!mb.MultiBox)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "(off)".Loc());
        }
        else if (mb.Host)
        {
            if (MultiboxUtility.Server.Running)
                ImGui.TextColored(ImGuiColors.HealerGreen, $"{"host".Loc()}: {MultiboxUtility.Server.ConnectedCount}/{MultiboxUtility.Server.MAX_SERVERS}");
            else
                ImGui.TextColored(ImGuiColors.DalamudRed, $"{"host".Loc()}: {"not started".Loc()}");
        }
        else
        {
            if (MultiboxUtility.Client.Connected)
                ImGui.TextColored(ImGuiColors.HealerGreen, $"{"client".Loc()}: {"connected".Loc()}");
            else if (MultiboxUtility.Client.Connecting)
                ImGui.TextColored(ImGuiColors.DalamudYellow, $"{"client".Loc()}: {"connecting...".Loc()}");
            else
                ImGui.TextColored(ImGuiColors.DalamudRed, $"{"client".Loc()}: {"not connected".Loc()}");
        }

        // 連線參數在連線期間不給改:改了也不會套用到已建立的連線,只會讓 UI 說謊。
        using (ImRaii.Disabled(mb.MultiBox))
        {
            bool host = mb.Host;
            if (ImGui.Checkbox("This client is the host".Loc(), ref host))
            {
                mb.Host = host;
                Configuration.Save();
            }
            ImGuiHelper.HelpMarkerAllowWhenDisabled("Exactly one game client must be the host. The host runs the path and drives everyone else; the others follow.".Loc());

            bool syncPath = mb.SynchronizePath;
            if (ImGui.Checkbox("Synchronize path from host".Loc(), ref syncPath))
            {
                mb.SynchronizePath = syncPath;
                Configuration.Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudGrey, "(not active yet)".Loc());
            ImGuiHelper.HelpMarkerAllowWhenDisabled("Intended to make the host send its loaded path to every client. The transport for this exists but nothing calls it yet, so this setting currently has no effect - each client still uses its own path file.".Loc());

            TransportType transport = mb.TransportType;
            ImGui.SetNextItemWidth(200f.Scale());
            if (ImGuiEx.EnumCombo("Transport".Loc(), ref transport))
            {
                mb.TransportType = transport;
                Configuration.Save();
            }

            if (mb.TransportType == TransportType.NamedPipe)
            {
                string pipeName = mb.PipeName;
                ImGui.SetNextItemWidth(200f.Scale());
                if (ImGui.InputText("Pipe name".Loc(), ref pipeName, 64))
                {
                    mb.PipeName = pipeName;
                    Configuration.Save();
                }

                if (!mb.Host)
                {
                    string serverName = mb.ServerName;
                    ImGui.SetNextItemWidth(200f.Scale());
                    if (ImGui.InputText("Server name".Loc(), ref serverName, 64))
                    {
                        mb.ServerName = serverName;
                        Configuration.Save();
                    }
                    ImGuiHelper.HelpMarkerAllowWhenDisabled("Use . for game clients running on this same computer.".Loc());
                }
            }
            else
            {
                // 主機端也要顯示：它決定主機監聽的位址。
                string serverAddress = mb.ServerAddress;
                ImGui.SetNextItemWidth(200f.Scale());
                if (ImGui.InputText("Server address".Loc(), ref serverAddress, 64))
                {
                    mb.ServerAddress = serverAddress;
                    Configuration.Save();
                }
                ImGuiHelper.HelpMarkerAllowWhenDisabled("Host: the address this computer listens on. 127.0.0.1 only accepts game clients on this same computer. With this computer's LAN address, clients on other computers can connect, but clients on this same computer must also enter that LAN address (127.0.0.1 will not reach the host). Enter 0.0.0.0 to accept both.\nClient: the host's address.".Loc());

                int port = mb.ServerPort;
                ImGui.SetNextItemWidth(200f.Scale());
                if (ImGui.InputInt("Server port".Loc(), ref port))
                {
                    mb.ServerPort = Math.Clamp(port, 1, 65535);
                    Configuration.Save();
                }
            }
        }

        if (mb.MultiBox)
            ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, "Turn Multibox off to change the connection settings.".Loc());

        ImGui.Unindent();
    }
}
