using AutoDuty.Helpers;
using ECommons;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoDuty.Managers
{
    using static Data.Classes;
    internal class VariantManager(TaskManager _taskManager)
    {
        internal unsafe void RegisterVariantDuty(Content content)
        {
            if (content.VVDIndex < 0)
                return;
            _taskManager.Enqueue(() => Svc.Log.Info($"Queueing Duty: {content.Name}"), "RegisterVariantDuty");
            _taskManager.Enqueue(() => Svc.Log.Info($"Index#: {content.VVDIndex}"), "RegisterVariantDuty");
            _taskManager.Enqueue(() => Plugin.Action = $"Queueing Duty: {content.Name}", "RegisterVariantDuty");
            AtkUnitBase* addon = null;
            AtkUnitBase* yesno = null;

            if (!PlayerHelper.IsValid)
            {
                _taskManager.Enqueue(() => PlayerHelper.IsValid, int.MaxValue, "RegisterVariantDuty");
                _taskManager.DelayNext("RegisterVariantDuty", 2000);
            }
            _taskManager.Enqueue(() => GenericHelpers.TryGetAddonByName("VVDFinder", out addon), "RegisterVariantDuty");
            _taskManager.Enqueue(() => { if (addon == null) OpenVVD(); }, "RegisterVariantDuty");
            _taskManager.Enqueue(() => GenericHelpers.TryGetAddonByName("VVDFinder", out addon) && GenericHelpers.IsAddonReady(addon), "RegisterVariantDuty");
            _taskManager.Enqueue(() => AddonHelper.FireCallBack(addon, true, 12, content.VVDIndex+1), "RegisterVariantDuty");
            _taskManager.DelayNext("RegisterVariantDuty", 500);
            _taskManager.Enqueue(() => AddonHelper.FireCallBack(addon, true, 11, 1), "RegisterVariantDuty");
            _taskManager.DelayNext("RegisterVariantDuty", 500);
            _taskManager.Enqueue(() => GenericHelpers.TryGetAddonByName("SelectYesno", out yesno), "RegisterVariantDuty");
            _taskManager.Enqueue(() => GenericHelpers.TryGetAddonByName("SelectYesno", out yesno) && GenericHelpers.IsAddonReady(yesno), "RegisterVariantDuty");
            _taskManager.Enqueue(() => AddonHelper.FireCallBack(yesno, true, 0, 1), "RegisterVariantDuty");
            _taskManager.Enqueue(() => GenericHelpers.TryGetAddonByName("ContentsFinderConfirm", out addon) && GenericHelpers.IsAddonReady(addon), "RegisterVariantDuty");
            _taskManager.Enqueue(() => AddonHelper.FireCallBack(addon, true, 8), "RegisterVariantDuty");
        }

        // AgentModule.Instance() 是手寫取得子(`uiModule == null ? null : uiModule->GetAgentModule()`),
        // GetAgentByInternalId() 是原生 MemberFunction、代理人尚未建立時同樣回 null ——
        // 原本整條裸解參考。兩層都判空;取不到就不開窗,佇列裡等待 VVDFinder 的那一步會繼續等。
        private unsafe void OpenVVD()
        {
            AgentModule* agentModule = AgentModule.Instance();
            if (agentModule == null)
                return;

            AgentInterface* agentVVDFinder = agentModule->GetAgentByInternalId(AgentId.VVDFinder);
            if (agentVVDFinder == null)
                return;

            agentVVDFinder->Show();
        }
    }
}
