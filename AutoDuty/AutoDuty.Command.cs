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
    private unsafe void OnCommand(string command, string args)
    {
        // in response to the slash command
        Match        match   = RegexHelper.ArgumentParserRegex().Match(args.ToLower());
        List<string> matches = [];

        while (match.Success)
        {
            matches.Add(match.Groups[match.Groups[1].Length > 0 ? 1 : 0].Value);
            match = match.NextMatch();
        }

        string[] argsArray = matches.Count > 0 ? matches.ToArray() : [string.Empty];

        switch (argsArray[0])
        {
            case "config" or "cfg":
                if (argsArray.Length < 2)
                    OpenConfigUI();
                else if (argsArray[1].Equals("list"))
                    ConfigHelper.ListConfig();
                else
                    ConfigHelper.ModifyConfig(argsArray[1], argsArray[2..]);
                break;
            case "start":
                StartNavigation();
                break;
            case "stop":
                Plugin.Stage = Stage.Stopped;
                break;
            case "pause":
                Plugin.Stage = Stage.Paused;
                break;
            case "resume":
                Plugin.Resume();
                break;
            case "goto":
                switch (argsArray[1])
                {
                    case "inn":
                        GotoInnHelper.Invoke(argsArray.Length > 2 ? Convert.ToUInt32(argsArray[2]) : PlayerHelper.GetGrandCompany());
                        break;
                    case "barracks":
                        GotoBarracksHelper.Invoke();
                        break;
                    case "gcsupply":
                        GotoHelper.Invoke(PlayerHelper.GetGrandCompanyTerritoryType(PlayerHelper.GetGrandCompany()), [GCTurninHelper.GCSupplyLocation], 0.25f, 2f, false);
                        break;
                    case "summoningbell":
                        SummoningBellHelper.Invoke(Configuration.PreferredSummoningBellEnum);
                        break;
                    case "apartment":
                        GotoHousingHelper.Invoke(Housing.Apartment);
                        break;
                    case "personalhome":
                        GotoHousingHelper.Invoke(Housing.Personal_Home);
                        break;
                    case "fcestate":
                        GotoHousingHelper.Invoke(Housing.FC_Estate);
                        break;
                    default:
                        break;
                }
                //GotoAction(args.Replace("goto ", ""));
                break;
            case "turnin":
                if (PlayerHelper.GetGrandCompanyRank() > 5)
                    GCTurninHelper.Invoke();
                else
                    Svc.Log.Info("GC Turnin requires GC Rank 6 or Higher");
                break;
            case "desynth":
                DesynthHelper.Invoke();
                break;
            case "repair":
                if (InventoryHelper.CanRepair(100))
                    RepairHelper.Invoke();
                break;
            case "autoretainer":
            case "ar":
                AutoRetainerHelper.Invoke();
                break;
            case "equiprec":
                AutoEquipHelper.Invoke();
                break;
            case "extract":
                if (QuestManager.IsQuestComplete(66174))
                    ExtractHelper.Invoke();
                else
                    Svc.Log.Info("Materia Extraction requires having completed quest: Forging the Spirit");
                break;
            case "dataid":
                IGameObject? obj = null;
                if (argsArray.Length == 2)
                    obj = Svc.Objects[int.TryParse(argsArray[1], out int index) ? index : -1] ?? null;
                else
                    obj = ObjectHelper.GetObjectByName(Svc.Targets.Target?.Name.TextValue ?? "");

                Svc.Log.Info($"{obj?.BaseId}");
                ImGui.SetClipboardText($"{obj?.BaseId}");
                break;
            case "moveto":
                {
                    var argss = args.Replace("moveto ", "").Split("|");
                    var cul = System.Globalization.CultureInfo.InvariantCulture;
                    const System.Globalization.NumberStyles fs = System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands;
                    var vs = argss.Length > 1 ? argss[1].Split(", ") : [];
                    float tolerance = 0.25f, lastPointTolerance = 0.25f;
                    if (vs.Length < 3
                        || !uint.TryParse(argss[0], System.Globalization.NumberStyles.Integer, cul, out var territoryId)
                        || !float.TryParse(vs[0], fs, cul, out var mx)
                        || !float.TryParse(vs[1], fs, cul, out var my)
                        || !float.TryParse(vs[2], fs, cul, out var mz)
                        || (argss.Length > 2 && !float.TryParse(argss[2], fs, cul, out tolerance))
                        || (argss.Length > 3 && !float.TryParse(argss[3], fs, cul, out lastPointTolerance)))
                    {
                        Svc.Chat.Print("moveto 參數格式不對，應為：/ad moveto 區域ID|x, y, z[|容許距離[|終點容許距離]]（小數點用「.」）", "AutoDuty");
                        break;
                    }

                    GotoHelper.Invoke(territoryId, [new Vector3(mx, my, mz)], tolerance, lastPointTolerance);
                    break;
                }
            case "exitduty":
                _actions.ExitDuty(new());
                break;
            case "queue":
                QueueHelper.Invoke(ContentHelper.DictionaryContent.FirstOrDefault(x => x.Value.Name!.Equals(args.ToLower().Replace("queue ", ""), StringComparison.InvariantCultureIgnoreCase)).Value ?? null, Configuration.DutyModeEnum);
                break;
            case "overlay":
                if (argsArray.Length == 1)
                {
                    this.Configuration.ShowOverlay = true;
                    this.Overlay.IsOpen            = true;

                    if (!Plugin.States.HasAnyFlag(PluginState.Looping, PluginState.Navigating))
                        this.Configuration.HideOverlayWhenStopped = false;
                }
                else
                {
                    switch (argsArray[1].ToLower())
                    {
                        case "lock":
                            if (Overlay.Flags.HasFlag(ImGuiWindowFlags.NoMove))
                                Overlay.Flags -= ImGuiWindowFlags.NoMove;
                            else
                                Overlay.Flags |= ImGuiWindowFlags.NoMove;
                            break;
                        case "nobg":
                            if (Overlay.Flags.HasFlag(ImGuiWindowFlags.NoBackground))
                                Overlay.Flags -= ImGuiWindowFlags.NoBackground;
                            else
                                Overlay.Flags |= ImGuiWindowFlags.NoBackground;
                            break;
                    }
                }
                break;
            case "skipstep":
                if (States.HasFlag(PluginState.Navigating))
                {
                    Indexer++;
                    Stage = Stage.Reading_Path;
                }
                break;
            case "movetoflag":
                MapHelper.MoveToMapMarker();
                break;
            case "run":
                var failPreMessage = "Run Error: Incorrect usage: ";
                var failPostMessage = "\nCorrect usage: /autoduty run DutyMode TerritoryTypeInteger LoopTimesInteger (optional)BareModeBool\nexample: /autoduty run Support 1036 10 true\nYou can get the TerritoryTypeInteger from /autoduty tt name of territory (will be logged and copied to clipboard)";
                if (argsArray.Length < 4)
                {
                    Svc.Log.Info($"{failPreMessage}Argument count must be at least 3, you inputed {argsArray.Length - 1}{failPostMessage}");
                    return;
                }
                if (!Enum.TryParse(argsArray[1], true, out DutyMode dutyMode))
                {
                    Svc.Log.Info($"{failPreMessage}Argument 1 must be a DutyMode enum Type, you inputed {argsArray[1]}{failPostMessage}");
                    return;
                }
                if (!uint.TryParse(argsArray[2], out uint territoryType))
                {
                    Svc.Log.Info($"{failPreMessage}Argument 2 must be an unsigned integer, you inputed {argsArray[2]}{failPostMessage}");
                    return;
                }
                if (!int.TryParse(argsArray[3], out int loopTimes))
                {
                    Svc.Log.Info($"{failPreMessage}Argument 3 must be an integer, you inputed {argsArray[3]}{failPostMessage}");
                    return;
                }
                if (!ContentHelper.DictionaryContent.TryGetValue(territoryType, out var content))
                {
                    Svc.Log.Info($"{failPreMessage}Argument 2 value was not in our ContentList or has no Path, you inputed {argsArray[2]}{failPostMessage}");
                    return;
                }
                if (!content.DutyModes.HasFlag(dutyMode))
                {
                    Svc.Log.Info($"{failPreMessage}Argument 2 value was not of type {dutyMode}, which you inputed in Argument 1, Argument 2 value was {argsArray[2]}{failPostMessage}");
                    return;
                }
                if (!content.CanRun(trust: dutyMode == DutyMode.Trust))
                {
                    var failReason = !UIState.IsInstanceContentCompleted(content.Id) ? "You dont have it unlocked" : (!ContentPathsManager.DictionaryPaths.ContainsKey(content.TerritoryType) ? "There is no path file" : (PlayerHelper.GetCurrentLevelFromSheet() < content.ClassJobLevelRequired ? $"Your Lvl({PlayerHelper.GetCurrentLevelFromSheet()}) is less than {content.ClassJobLevelRequired}" : (InventoryHelper.CurrentItemLevel < content.ItemLevelRequired ? $"Your iLvl({InventoryHelper.CurrentItemLevel}) is less than {content.ItemLevelRequired}" : "Your trust party is not of correct levels")));
                    Svc.Log.Info($"Unable to run {content.Name}, {failReason} {content.CanTrustRun()}");
                    return;
                }

                Configuration.DutyModeEnum = dutyMode;

                var bareMode = argsArray.Length > 4 && bool.TryParse(argsArray[4], out bool parsedBool) && parsedBool;
                var ctx = BuildCommandRunContext(territoryType, loopTimes, startFromZero: true, bareMode: bareMode, source: RunSource.Command, persistLoopsToConfig: true);
                if (ctx != null)
                    Run(ctx);
                else
                    Run(territoryType, loopTimes, bareMode: bareMode);
                break;
            case "tt":
                var tt = Svc.Data.Excel.GetSheet<TerritoryType>()?.FirstOrDefault(x => x.ContentFinderCondition.ValueNullable != null && x.ContentFinderCondition.Value.Name.ToString().Equals(args.Replace("tt ", ""), StringComparison.InvariantCultureIgnoreCase)) ?? Svc.Data.Excel.GetSheet<TerritoryType>()?.GetRow(1);
                Svc.Log.Info($"{tt?.RowId}");
                ImGui.SetClipboardText($"{tt?.RowId}");
                break;
            case "range":
                if (float.TryParse(argsArray[1], out float newRange))
                    BossMod_IPCSubscriber.SetRange(Math.Clamp(newRange, 1, 30));
                break;
            case "spew":
                IGameObject? spewObj = null;
                if (argsArray.Length == 2)
                    spewObj = ObjectHelper.GetObjectByDataId(uint.TryParse(argsArray[1], out uint dataId) ? dataId : 0);
                else
                    spewObj = ObjectHelper.GetObjectByName(Svc.Targets.Target?.Name.TextValue ?? "");

                if (spewObj == null) return;

                GameObject gObj = *spewObj.Struct();
                try { Svc.Log.Info($"Spewing Object Information for: {gObj.NameString}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"Spewing Object Information for: {gObj.GetName()}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                //DrawObject: {gObj.DrawObject}\n
                //LayoutInstance: { gObj.LayoutInstance}\n
                //EventHandler: { gObj.EventHandler}\n
                //LuaActor: {gObj.LuaActor}\n
                try { Svc.Log.Info($"DefaultPosition: {gObj.DefaultPosition}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"DefaultRotation: {gObj.DefaultRotation}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"EventState: {gObj.EventState}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"EntityId {gObj.EntityId}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"LayoutId: {gObj.LayoutId}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"BaseId {gObj.BaseId}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"OwnerId: {gObj.OwnerId}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"ObjectIndex: {gObj.ObjectIndex}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"ObjectKind {gObj.ObjectKind}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"SubKind: {gObj.SubKind}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"Sex: {gObj.Sex}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"YalmDistanceFromPlayerX: {gObj.YalmDistanceFromPlayerX}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"TargetStatus: {gObj.TargetStatus}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"YalmDistanceFromPlayerZ: {gObj.YalmDistanceFromPlayerZ}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"TargetableStatus: {gObj.TargetableStatus}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"Position: {gObj.Position}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"Rotation: {gObj.Rotation}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"Scale: {gObj.Scale}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"Height: {gObj.Height}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"VfxScale: {gObj.VfxScale}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"HitboxRadius: {gObj.HitboxRadius}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"DrawOffset: {gObj.DrawOffset}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"EventId: {gObj.EventId.Id}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"FateId: {gObj.FateId}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"NamePlateIconId: {gObj.NamePlateIconId}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"RenderFlags: {gObj.RenderFlags}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"GetGameObjectId().ObjectId: {gObj.GetGameObjectId().ObjectId}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"GetGameObjectId().Type: {gObj.GetGameObjectId().Type}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"GetObjectKind: {gObj.GetObjectKind()}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"GetIsTargetable: {gObj.GetIsTargetable()}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"GetName: {gObj.GetName()}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"GetRadius: {gObj.GetRadius()}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"GetHeight: {gObj.GetHeight()}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"GetDrawObject: {*gObj.GetDrawObject()}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"GetNameId: {gObj.GetNameId()}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"IsDead: {gObj.IsDead()}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"IsNotMounted: {gObj.IsNotMounted()}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"IsCharacter: {gObj.IsCharacter()}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"IsReadyToDraw: {gObj.IsReadyToDraw()}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                break;
            default:
                OpenMainUI();
                break;
        }
    }
}
