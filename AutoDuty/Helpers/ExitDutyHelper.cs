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

        /// <summary>
        /// 退本的一次嘗試。<b>這支掛在 <see cref="IFramework.Update"/> 上,每一幀都會跑一次</b>
        /// (<see cref="HelperUpdate"/> 刻意繞過 <c>UpdateBase()</c> 的 500 毫秒節流,
        /// 因為 <c>UpdateBase()</c> 在副本裡會直接 <c>Stop()</c>)。
        /// </summary>
        /// <remarks>
        /// 🔴🔴 <b>原本的寫法是本外掛最危險的一段</b>:同一幀裡對 ContentsFinderMenu 連送兩個 callback、
        /// 再對 SelectYesno 送一個,而且<b>零節流、零「按過了」狀態</b> ——
        /// 退本確認按下之後、伺服器回應之前的<b>每一幀</b>都會再按一次。
        /// 確認框「正在關閉中」的那幾幀 <c>TryGetAddonByName</c> 仍拿得到實例、
        /// <c>IsAddonReady</c> 三關也全過,再送 callback 就是原生 AccessViolation
        /// (corrupted-state exception,<c>try</c>/<c>catch</c> 攔不到,遊戲當場關閉)。
        /// <para>改動有四點,正常路徑的第一次動作與原本逐一相同:</para>
        /// <list type="number">
        /// <item>
        /// <b>SelectYesno 最優先、處理完這一幀就結束。</b>原本是先動 ContentsFinderMenu 再回頭按確認框;
        /// 現在確認框一出現就只做這件事,不會在同一幀又去碰那扇<b>已經進入關閉流程</b>的選單。
        /// </item>
        /// <item>
        /// <b>補上 <c>TryGetAddonByName</c> 的回傳值檢查與 <c>IsAddonReady</c>。</b>
        /// 原本 SelectYesno 那次取窗<b>回傳值連接都沒接</b>,取不到時 out 參數是 null 就直接往下送
        /// (靠 <c>FireCallBack</c> 內部判空才沒炸),而且完全沒驗窗就緒。
        /// </item>
        /// <item>
        /// <b><c>Show()</c> 只在窗還沒開的時候呼叫。</b>原本每一幀都無條件推同一扇已經開著的窗。
        /// </item>
        /// <item>
        /// <b><c>(false, -2)</c> 關窗那一發改成有條件。</b>
        /// 原本 <c>(true, 0)</c>(選「退出」)與 <c>(false, -2)</c>(關窗)是同一幀無條件連送 ——
        /// 而 <c>Callback.Fire</c> 是<b>同步</b>的,第一發回來時那扇選單已經在關了,
        /// 第二發就落在危險窗口正中央。現在只有在第一發<b>真的送出去了</b>
        /// (守衛沒擋)<b>而且送完之後確認框沒出現</b>(＝什麼都沒發生)時才補送關窗 ——
        /// 這正是原本 <c>(false, -2)</c> 唯一有意義的情境。
        /// </item>
        /// </list>
        /// 📌 選單真的關不掉也不會留著:<see cref="AddonsToClose"/> 已經列了 ContentsFinderMenu,
        /// <c>ActiveHelperBase.HelperStopUpdate</c> 停止時會用 <c>Close(true)</c> 收掉它。
        /// </remarks>
        private static unsafe void Exit()
        {
            // ① 確認框在的話最優先,而且做完這一幀就結束。
            if (GenericHelpers.TryGetAddonByName("SelectYesno", out AtkUnitBase* addonSelectYesno)
                && GenericHelpers.IsAddonReady(addonSelectYesno))
            {
                AddonHelper.FireCallBack(addonSelectYesno, true, 0);
                return;
            }

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

            // ② 窗還沒開才 Show()。
            if (!GenericHelpers.TryGetAddonByName("ContentsFinderMenu", out AtkUnitBase* addonContentsFinderMenu)
                || !GenericHelpers.IsAddonReady(addonContentsFinderMenu))
            {
                agentContentsFinderMenu->Show();
                return;
            }

            // ③ 選「退出」。守衛擋下(回 false)＝這扇選單的這一發已經按過 ⇒ 這一幀什麼都不再送。
            if (!AddonHelper.TryFireCallBack(addonContentsFinderMenu, true, 0))
                return;

            // Callback.Fire 是同步的:上面那一發如果真的觸發了退本,確認框在這一幀就已經建好、
            // 選單也已經進入關閉流程 —— 這時候再送關窗 callback 正好落在危險窗口裡。
            // 只有在「什麼都沒發生」時才照原本的行為補送關窗(保留原本 :69 的用途)。
            if (GenericHelpers.TryGetAddonByName("SelectYesno", out AtkUnitBase* _))
                return;

            AddonHelper.FireCallBack(addonContentsFinderMenu, false, -2);
        }
    }
}
