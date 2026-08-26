using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using ECommons;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;

namespace AutoDuty.Helpers
{
    internal class ExitDutyHelper : ActiveHelperBase<ExitDutyHelper>
    {
        protected override string Name        => nameof(ExitDutyHelper);
        protected override string DisplayName => "Exiting Duty";

        protected override int TimeOut { get; set; } = 60_000;

        protected override string[] AddonsToClose { get; } = ["ContentsFinderMenu"];

        internal override void Start()
        {
            base.Start();

            if (Svc.ClientState.TerritoryType != 0)
            {
                _currentTerritoryType = Svc.ClientState.TerritoryType;
                base.Start();
            }
        }

        private uint _currentTerritoryType = 0;

        protected override void HelperStopUpdate(IFramework framework)
        {
            base.HelperStopUpdate(framework);
            this._currentTerritoryType = 0;
        }

        protected override void HelperUpdate(IFramework framework)
        {
            if (!PlayerHelper.IsReady || PlayerHelper.InCombat)
                return;

            if (Svc.ClientState.TerritoryType != _currentTerritoryType || !Plugin.InDungeon || Svc.ClientState.TerritoryType == 0)
            {
                Stop();
                return;
            }

            Exit();
        }

        private static unsafe void Exit()
        {
            // 這條鏈有兩層都可能回 null:AgentModule.Instance() 是手寫取得子
            // (`uiModule == null ? null : uiModule->GetAgentModule()`),GetAgentByInternalId() 則是原生
            // MemberFunction、代理人尚未建立時同樣回 null。原本整條裸解參考。
            // 兩層都判空後同幀即用;為 null 時本 tick 不動作,下 tick 重試(每幀熱路徑,不寫 log)。
            AgentModule* agentModule = AgentModule.Instance();
            if (agentModule == null)
                return;

            AgentInterface* agentContentsFinderMenu = agentModule->GetAgentByInternalId(AgentId.ContentsFinderMenu);
            if (agentContentsFinderMenu == null)
                return;

            agentContentsFinderMenu->Show();
            if (GenericHelpers.TryGetAddonByName("ContentsFinderMenu", out AtkUnitBase* addonContentsFinderMenu))
            {
                AddonHelper.FireCallBack(addonContentsFinderMenu, true, 0);
                AddonHelper.FireCallBack(addonContentsFinderMenu, false, -2);
                GenericHelpers.TryGetAddonByName("SelectYesno", out AtkUnitBase* addonSelectYesno);
                AddonHelper.FireCallBack(addonSelectYesno, true, 0);
            }
        }
    }
}
