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
            // VVDFinder 這兩發同樣不沿用上一步抓到的指標(中間還隔了 500 毫秒的 DelayNext),
            // 每一步自己重新取窗、自己驗就緒。
            // 🔴 這四發 callback 原本都是 Enqueue(Action)。ECommons LegacyTaskManager 的 Action 多載
            //    一律包成「{ task(); return true; }」(TaskManager@Enqueue.cs:63)⇒ AddonPressGuard 擋下時
            //    回的 false 被吞掉,這一步照樣算「做完」。這條路徑不像 SquadronManager 後面有 int.MaxValue
            //    的下游等待,所以不會卡死 —— 但外層 AutoDuty.Queue()(AutoDuty.cs:1362-1363)那兩道等待
            //    一道都攔不住:「Queue-WaitNotValid」用預設 10 秒逾時且 AbortOnTimeout=false(逾時放行,
            //    而且 TimeoutSilently=true 只寫 Verbose ⇒ 使用者的 LogLevel 2 完全看不到),
            //    「Queue-WaitValid」等的是「玩家有效」—— 按鈕沒送出去時玩家本來就有效 ⇒ 立刻通過。
            //    ⇒ 靜默誤判成「已經排隊完成」繼續往下跑,而玩家還站在 VVDFinder 前面。
            //    改成回傳 TryFireCallBack 的結果(綁 Func<bool?> 多載):擋下就回 false、下一幀再來;
            //    守衛最多擋 AddonPressGuard.DefaultEscapeFrames(90 幀),遠早於這一步的 10 秒逾時。
            //    🔴 回 false 不可能變成回 null —— null 才會讓 TaskManager.Abort() 清掉整條佇列。
            _taskManager.Enqueue(() =>
                                 {
                                     if (!GenericHelpers.TryGetAddonByName("VVDFinder", out AtkUnitBase* addonVvdFinder)
                                         || !GenericHelpers.IsAddonReady(addonVvdFinder))
                                         return true; // 窗根本不在:維持原本「做一次就過」的語意
                                     return AddonHelper.TryFireCallBack(addonVvdFinder, true, 12, content.VVDIndex + 1);
                                 }, "RegisterVariantDuty-SelectDuty");
            _taskManager.DelayNext("RegisterVariantDuty", 500);
            _taskManager.Enqueue(() =>
                                 {
                                     if (!GenericHelpers.TryGetAddonByName("VVDFinder", out AtkUnitBase* addonVvdFinder)
                                         || !GenericHelpers.IsAddonReady(addonVvdFinder))
                                         return true;
                                     return AddonHelper.TryFireCallBack(addonVvdFinder, true, 11, 1);
                                 }, "RegisterVariantDuty-ClickQueue");
            _taskManager.DelayNext("RegisterVariantDuty", 500);
            _taskManager.Enqueue(() => GenericHelpers.TryGetAddonByName("SelectYesno", out yesno), "RegisterVariantDuty");
            _taskManager.Enqueue(() => GenericHelpers.TryGetAddonByName("SelectYesno", out yesno) && GenericHelpers.IsAddonReady(yesno), "RegisterVariantDuty");
            // 🔴 發射的那一幀重新取窗,不要沿用上一步(上一幀)抓到的 yesno:
            //    TaskManager 的每一步各在不同的幀執行,上一幀確認過「在而且就緒」不代表這一幀還在,
            //    而確認框關閉中的那幾幀連 IsAddonReady 三關都會過 —— 對它送 callback 就是
            //    攔不到的 AccessViolation。重新取到窗才送,送不送得出去由 AddonPressGuard 決定。
            _taskManager.Enqueue(() =>
                                 {
                                     if (!GenericHelpers.TryGetAddonByName("SelectYesno", out AtkUnitBase* addonSelectYesno)
                                         || !GenericHelpers.IsAddonReady(addonSelectYesno))
                                         return true;
                                     return AddonHelper.TryFireCallBack(addonSelectYesno, true, 0, 1);
                                 }, "RegisterVariantDuty-SelectYesno");
            _taskManager.Enqueue(() => GenericHelpers.TryGetAddonByName("ContentsFinderConfirm", out addon) && GenericHelpers.IsAddonReady(addon), "RegisterVariantDuty");
            // 與上面的 SelectYesno 同一個形狀:上一步是上一幀跑的,發射的這一幀要自己重新取窗。
            _taskManager.Enqueue(() =>
                                 {
                                     if (!GenericHelpers.TryGetAddonByName("ContentsFinderConfirm", out AtkUnitBase* addonConfirm)
                                         || !GenericHelpers.IsAddonReady(addonConfirm))
                                         return true;
                                     return AddonHelper.TryFireCallBack(addonConfirm, true, 8);
                                 }, "RegisterVariantDuty-ConfirmDuty");
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
