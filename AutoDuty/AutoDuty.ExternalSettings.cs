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
    private void SetPandoraChests(bool on)
    {
        if (_pandoraChestsOriginal is null)
        {
            try
            {
                _pandoraChestsOriginal = PandorasBox_IPCSubscriber.GetFeatureEnabled(PandoraChestsFeature);
                Svc.Log.Information($"PandorasBox「自動開寶箱」：開跑前為 {_pandoraChestsOriginal}，本次設為 {on}，停止時還原");
            }
            catch (Exception ex)
            {
                // 讀不到原值就不記（停止時不寫回＝原本的行為），寫入照舊。
                Svc.Log.Information($"PandorasBox「自動開寶箱」：讀不到開跑前的值，停止時不還原（{ex.GetType().Name}）");
            }
        }
        _pandoraChestsLastWritten = on;
        PandorasBox_IPCSubscriber.SetFeatureEnabled(PandoraChestsFeature, on);
    }

    /// <summary>停止時要寫回的值；null＝本次沒寫過，或最後寫入的就是原值（等於沒改）。</summary>
    internal static bool? PandoraChestsRestoreValue(bool? original, bool lastWritten) =>
        original is bool o && o != lastWritten ? o : null;

    private void RestorePandoraChests()
    {
        bool? restore = PandoraChestsRestoreValue(_pandoraChestsOriginal, _pandoraChestsLastWritten);
        _pandoraChestsOriginal = null;
        if (restore is not bool value)
            return;

        if (!PandorasBox_IPCSubscriber.IsEnabled)
        {
            if (Svc.Framework.IsFrameworkUnloading)
                Svc.Log.Warning($"PandorasBox「自動開寶箱」：遊戲正在關閉且 PandorasBox 已卸載，無法還原成 {value}");
            else
                Svc.Log.Information($"PandorasBox「自動開寶箱」：PandorasBox 沒有載入，無法還原成 {value}");
            return;
        }

        void Write()
        {
            Svc.Log.Information($"PandorasBox「自動開寶箱」：還原成開跑前的 {value}");
            PandorasBox_IPCSubscriber.SetFeatureEnabled(PandoraChestsFeature, value);
        }

        if (Svc.Framework.IsInFrameworkUpdateThread)
            Write();
        else if (Svc.Framework.IsFrameworkUnloading)
            Svc.Log.Warning($"PandorasBox「自動開寶箱」：遊戲正在關閉且不在框架執行緒上，略過還原成 {value}");
        else
            _ = Svc.Framework.Run(Write);
    }

    private void GetGeneralSettings()
    {
        // 下面註解掉的兩行是舊政策的擷取端:「使用者本來開著 → 進場先關掉 → 結束再開回來」,旗標 Vnav_Align_Camera_Off。
        // 不可以直接取消註解:現行政策方向相反(StartNavigation 進導航時「打開」),那會變成兩個相反的政策同時存在。
        // 現行政策的擷取端在 StartNavigation(),還原端在 SetGeneralSettings() 裡的 Vnav_Align_Camera_On 分支。
        /*
        if (Configuration.AutoManageVnavAlignCamera && VNavmesh_IPCSubscriber.IsEnabled && VNavmesh_IPCSubscriber.Path_GetAlignCamera())
            _settingsActive |= SettingsActive.Vnav_Align_Camera_Off;
        */
        // 🔴 第二個條件必須是 IsPluginEnabled():只判「YesAlready 裝著」就設旗標的話,AutoDuty 進場把它關掉、
        //    結束再打開,本來刻意關著的 YesAlready 會被 AutoDuty 打開。
        if (YesAlready_IPCSubscriber.IsEnabled && YesAlready_IPCSubscriber.IsPluginEnabled())
            _settingsActive |= SettingsActive.YesAlready;

        if (PandorasBox_IPCSubscriber.IsEnabled && PandorasBox_IPCSubscriber.GetFeatureEnabled("Auto-interact with Objects in Instances"))
            _settingsActive |= SettingsActive.Pandora_Interact_Objects;

        Svc.Log.Debug($"General Settings Active: {_settingsActive}");
    }

    internal void SetGeneralSettings(bool on)
    {
        if (!on)
            GetGeneralSettings();

        if (Configuration.AutoManageVnavAlignCamera && _settingsActive.HasFlag(SettingsActive.Vnav_Align_Camera_Off))
        {
            Svc.Log.Debug($"Setting VnavAlignCamera: {on}");
            VNavmesh_IPCSubscriber.Path_SetAlignCamera(on);
        }

        // 上面那條是舊政策的還原端:設它的旗標那段在 GetGeneralSettings() 裡是註解掉的,實際是死碼;與上游一致,原樣保留。
        // 下面這條才是現行政策的還原端:StartNavigation() 把「本來關著」的對齊鏡頭打開了,這裡在整趟真的結束時關回去。
        // 🔴 額外擋 Looping/Navigating:四個呼叫點裡只有 StopAndResetALL() 代表「整趟結束」(它先把 States 清成 None);
        //    另外三個是單機流程的收尾,IPC 的 Start() / 指令 "start" 走進「Navigating 有、Looping 沒有」的狀態時會提早打進來。
        // 🔴 旗標只在「真的還原到了」才清:被閘門擋下時留著,等真正結束時再還原,不會漏掉。
        if (on
            && Configuration.AutoManageVnavAlignCamera
            && _settingsActive.HasFlag(SettingsActive.Vnav_Align_Camera_On)
            && !States.HasAnyFlag(PluginState.Looping, PluginState.Navigating))
        {
            _settingsActive &= ~SettingsActive.Vnav_Align_Camera_On;
            Svc.Log.Information("還原 vnavmesh 對齊鏡頭:關回進導航前的狀態(關閉)");
            if (VNavmesh_IPCSubscriber.IsEnabled)
                VNavmesh_IPCSubscriber.Path_SetAlignCamera(false);
        }
        // 🔴 還原之後要把旗標清掉,下一趟的 GetGeneralSettings() 才會重新評估當下的實際狀態;不清的話,
        //    使用者之後手動關掉的功能會在下一趟結束時被 AutoDuty 照著過期的旗標重新打開。
        //    ⚠️ 只在 on==true（還原那一側）清；on==false 是剛設起旗標的那一側，不能清。
        if (PandorasBox_IPCSubscriber.IsEnabled && _settingsActive.HasFlag(SettingsActive.Pandora_Interact_Objects))
        {
            Svc.Log.Debug($"Setting PandorasBos Auto-interact with Objects in Instances: {on}");
            PandorasBox_IPCSubscriber.SetFeatureEnabled("Auto-interact with Objects in Instances", on);
            if (on) _settingsActive &= ~SettingsActive.Pandora_Interact_Objects;
        }
        if (YesAlready_IPCSubscriber.IsEnabled && _settingsActive.HasFlag(SettingsActive.YesAlready))
        {
            Svc.Log.Debug($"Setting YesAlready Enabled: {on}");
            YesAlready_IPCSubscriber.SetState(on);
            if (on) _settingsActive &= ~SettingsActive.YesAlready;
        }
    }

    internal void SetRotationPluginSettings(bool on, bool ignoreConfig = false, bool ignoreTimer = false)
    {
        // Only try to set the rotation state every few seconds
        if (on && (DateTime.Now - _lastRotationSetTime).TotalSeconds < 5 && !ignoreTimer)
            return;
        
        if(on)
            _lastRotationSetTime = DateTime.Now;

        if (!ignoreConfig && !this.Configuration.AutoManageRotationPluginState)
            return;
        _rotationEngaged = on;
        bool bmEnabled     = BossMod_IPCSubscriber.IsEnabled;
        bool foundRotation = false;

        if (Wrath_IPCSubscriber.IsEnabled)
        {
            bool wrathRotationReady = true;
            if (on)
                wrathRotationReady = Wrath_IPCSubscriber.IsCurrentJobAutoRotationReady() ||
                                     this.Configuration.Wrath_AutoSetupJobs && Wrath_IPCSubscriber.SetJobAutoReady();

            if (!on || wrathRotationReady)
            {
                Svc.Log.Debug("Wrath rotation enabled");
                Wrath_IPCSubscriber.SetAutoMode(on);
                foundRotation = true;
            }
        }

        if (ReflectionHelper.RotationSolver_Reflection.RotationSolverEnabled)
        {
            if (on && !foundRotation)
            {
                Svc.Log.Debug("RSR enabled");
                if (ReflectionHelper.RotationSolver_Reflection.GetStateType != ReflectionHelper.RotationSolver_Reflection.StateTypeEnum.Auto)
                    ReflectionHelper.RotationSolver_Reflection.RotationAuto();
                foundRotation = true;
            }
            else
            {
                if (ReflectionHelper.RotationSolver_Reflection.GetStateType != ReflectionHelper.RotationSolver_Reflection.StateTypeEnum.Off)
                    ReflectionHelper.RotationSolver_Reflection.RotationStop();
            }
        }


        if (bmEnabled)
        {
            if (on)
            {
                BossMod_IPCSubscriber.SetRange(Plugin.Configuration.MaxDistanceToTargetFloat);
                if (!foundRotation)
                {
                    BossMod_IPCSubscriber.SetPreset("AutoDuty", Resources.AutoDutyPreset);
                }
                else if(this.Configuration.AutoManageBossModAISettings)
                {
                    BossMod_IPCSubscriber.SetPreset("AutoDuty Passive", Resources.AutoDutyPassivePreset);
                }
            } 
            else if(!foundRotation || this.Configuration.AutoManageBossModAISettings)
            {
                BossMod_IPCSubscriber.DisablePresets();
            }
        }
    }

    internal void SetBMSettings(bool defaults = false)
    {
        BMRoleChecks();

        if (defaults)
        {
            Configuration.MaxDistanceToTargetRoleBased = true;
            Configuration.PositionalRoleBased = true;
        }

        BossMod_IPCSubscriber.SetMovement(true);
        BossMod_IPCSubscriber.SetRange(Plugin.Configuration.MaxDistanceToTargetFloat);
    }

    internal void BMRoleChecks()
    {
        //RoleBased Positional
        if (PlayerHelper.IsValid && Configuration.PositionalRoleBased && Configuration.PositionalEnum != (Player.Object.ClassJob.Value.GetJobRole() == JobRole.Melee ? Positional.Rear : Positional.Any))
        {
            Configuration.PositionalEnum = (Player.Object.ClassJob.Value.GetJobRole() == JobRole.Melee ? Positional.Rear : Positional.Any);
            Configuration.Save();
        }

        //RoleBased MaxDistanceToTarget
        float maxDistanceToTarget = (Player.Object.ClassJob.Value.GetJobRole() is JobRole.Melee or JobRole.Tank ? 
                                         Plugin.Configuration.MaxDistanceToTargetRoleMelee : Plugin.Configuration.MaxDistanceToTargetRoleRanged);
        if (PlayerHelper.IsValid && Configuration.MaxDistanceToTargetRoleBased && Math.Abs(this.Configuration.MaxDistanceToTargetFloat - maxDistanceToTarget) > 0.01f)
        {
            Configuration.MaxDistanceToTargetFloat = maxDistanceToTarget;
            Configuration.Save();
        }

        //RoleBased MaxDistanceToTargetAoE
        float maxDistanceToTargetAoE = (Player.Object.ClassJob.Value!.GetJobRole() is JobRole.Melee or JobRole.Tank or JobRole.Ranged_Physical ?
                                            Plugin.Configuration.MaxDistanceToTargetRoleMelee : Plugin.Configuration.MaxDistanceToTargetRoleRanged);
        if (PlayerHelper.IsValid && Configuration.MaxDistanceToTargetRoleBased && Math.Abs(this.Configuration.MaxDistanceToTargetAoEFloat - maxDistanceToTargetAoE) > 0.01f)
        {
            Configuration.MaxDistanceToTargetAoEFloat = maxDistanceToTargetAoE;
            Configuration.Save();
        }
    }
}
