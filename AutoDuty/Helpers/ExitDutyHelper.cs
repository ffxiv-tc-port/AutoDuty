using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using ECommons;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using ECommons.Throttlers;

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
            _exitPressedMenu    = 0;
            _notReadySinceFrame = NotTracking;

            base.Start();

            if (Svc.ClientState.TerritoryType != 0)
            {
                _currentTerritoryType = Svc.ClientState.TerritoryType;
                base.Start();
            }
        }

        private uint _currentTerritoryType = 0;

        /// <summary>
        /// 送出「退出」之後要隔多少幀,才准判定「上一發根本沒生效」而補送關窗 callback。
        /// </summary>
        /// <remarks>
        /// 🔴 存在的理由:<c>(true, 0)</c> 如果<b>真的</b>觸發了退本,那扇選單就進入關閉流程,
        /// 而關閉中的窗 <c>TryGetAddonByName</c> 仍拿得到、<c>IsAddonReady</c> 三關也全過,
        /// 這時候補送 <c>(false, -2)</c> 就是攔不到的原生 AccessViolation(遊戲當場關閉)。
        /// 30 幀(60fps 約 0.5 秒)遠大於「關閉中的那幾幀」,又小於守衛對同一個按法的
        /// 逃生口(<see cref="AddonPressGuard.DefaultEscapeFrames"/> 90 幀),
        /// 所以補關窗一定會發生在「下一次重按退出」之前 —— 原本 <c>(false, -2)</c> 的用途完整保留。
        /// <para>
        /// 🔴 數的是 <b>framework tick</b>(<see cref="AddonPressGuard.CurrentFrame"/>)而不是繪製幀:
        /// 繪製幀計數器在過場動畫期間<b>完全停住</b>,而副本流程大量伴隨過場 ——
        /// 那會讓這個觀察期永遠不到期,補關窗那一發永遠不發生。
        /// </para>
        /// </remarks>
        private const int ExitPressSettleFrames = 30;

        /// <summary>上一次對哪一扇 ContentsFinderMenu 送出過「退出」,以及那是第幾個 framework tick(<see cref="AddonPressGuard.CurrentFrame"/>)。</summary>
        /// <remarks>🔴 位址<b>只做等值比較,永遠不解參</b> —— 記下來的那個實例隨時可能已經失效。</remarks>
        private static nint _exitPressedMenu;

        private static long _exitPressedFrame;

        /// <summary>
        /// 「視窗可見、但 IsAddonReady 仍不過」連續多少個 framework tick 之後,不管怎樣都再送一次 <c>Show()</c>。
        /// </summary>
        /// <remarks>
        /// 🔴 <b>逃生口</b>:「可見但未就緒」時不每幀重複 <c>Show()</c>,是基於<b>懷疑</b>它會打斷視窗自己的載入流程 ——
        /// 那是假設不是證明。假設要是不成立(真正卡住的原因是那扇窗<b>需要</b>再被推一次),完全不送就等於新增一種卡法:
        /// 「可見但永遠不就緒」時那條路徑會一路空轉到 <see cref="TimeOut"/>(60 秒)為止。
        /// <para>
        /// 所以是「絕大多數時間不送、隔夠久還是送一次」:視窗有充裕時間把載入跑完,
        /// 而假設不成立時仍然會週期性重推,不會卡死。這與 <see cref="ExitPressSettleFrames"/> 是同一條原則:
        /// <b>把「未證實的假設」改成「假設不成立也不會卡死」</b>。
        /// </para>
        /// <para>
        /// 📌 換算:約 10 毫秒/幀時 300 幀 ≈ 3 秒,在 60 秒 <see cref="TimeOut"/> 內約可重推 19 次。
        /// 🔴 數的是 framework tick(<see cref="AddonPressGuard.CurrentFrame"/>)不是繪製幀,
        /// 理由同 <see cref="ExitPressSettleFrames"/>:繪製幀在過場動畫期間完全停住。
        /// </para>
        /// </remarks>
        private const int NotReadyEscapeFrames = 300;

        /// <summary><see cref="_notReadySinceFrame"/> 的「目前沒在追蹤」哨兵值。</summary>
        /// <remarks>
        /// 🔴 哨兵值<b>不能用 0</b>:<see cref="AddonPressGuard.CurrentFrame"/> 從 0 起算,
        /// 第一個 framework tick 真的會是 0 —— 用 0 當哨兵會讓逃生口在那一幀被判成「沒在追蹤」而永遠重新起算。
        /// </remarks>
        private const long NotTracking = -1;

        /// <summary>目前這段「視窗可見但未就緒」是從第幾個 framework tick 開始的。</summary>
        private static long _notReadySinceFrame = NotTracking;

        protected override void HelperStopUpdate(IFramework framework)
        {
            base.HelperStopUpdate(framework);
            this._currentTerritoryType = 0;
            _exitPressedMenu           = 0;
            _notReadySinceFrame        = NotTracking;
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
        /// 🔴🔴 零節流、零「按過了」狀態地每幀送 callback 是致命的:確認框「正在關閉中」的那幾幀
        /// <c>TryGetAddonByName</c> 仍拿得到實例、<c>IsAddonReady</c> 三關也全過,再送 callback 就是原生 AccessViolation
        /// (corrupted-state exception,<c>try</c>/<c>catch</c> 攔不到,遊戲當場關閉)。所以順序是:
        /// <para>
        /// ① <b>SelectYesno 最優先、處理完這一幀就結束</b>,不在同一幀又去碰那扇<b>已經進入關閉流程</b>的選單。
        /// ② <b><c>Show()</c> 只在窗還沒開的時候呼叫</b>(可見但未就緒時見 <see cref="NotReadyEscapeFrames"/>)。
        /// ③ <b><c>(false, -2)</c> 關窗那一發「隔幾幀之後、確定上一發沒生效」才送</b>:<c>Callback.Fire</c> 是<b>同步</b>的,
        /// <c>(true, 0)</c>(選「退出」)回來時那扇選單可能已經在關了,同一幀連送第二發就落在危險窗口正中央。
        /// ④ 選「退出」,送出前過 <see cref="AddonPressGuard"/>。
        /// </para>
        /// <para>
        /// 🔴 判「上一發有沒有生效」刻意不依賴「SelectYesno 在同一幀就已經進了 addon 清單」——<b>這件事沒有任何離線證據</b>。
        /// 隔 <see cref="ExitPressSettleFrames"/> 幀之後回頭看,那扇選單<b>還是同一個實例、而且還開著</b>
        /// (這期間任何一幀出現確認框都會先被 ① 接走)才算「上一發什麼都沒發生」。
        /// </para>
        /// 📌 選單真的關不掉也不會留著:<see cref="AddonsToClose"/> 已經列了 ContentsFinderMenu,
        /// <c>ActiveHelperBase.HelperStopUpdate</c> 停止時會用 <c>Close(true)</c> 收掉它。
        /// </remarks>
        private static unsafe void Exit()
        {
            // 🔴 時鐘用守衛的 framework tick,不是 UiBuilder.FrameCount:後者加在 UiBuilder.OnDraw() 最後面,
            //    而 OnDraw 在過場動畫期間會提早 return(ToggleUiHideDuringCutscenes 預設開啟)——
            //    副本流程大量伴隨過場,用繪製幀的話下面那個「隔 ExitPressSettleFrames 幀回頭判」
            //    在整段過場裡永遠不會到期,補送關窗的 (false, -2) 就永遠不會發生。
            long frame = AddonPressGuard.CurrentFrame;

            // ① 確認框在的話最優先,而且做完這一幀就結束。
            if (GenericHelpers.TryGetAddonByName("SelectYesno", out AtkUnitBase* addonSelectYesno)
                && GenericHelpers.IsAddonReady(addonSelectYesno))
            {
                // 確認框出現了 ⇒ 上一發「退出」有生效,那扇選單已經在關閉流程裡,關窗那一發永遠不要補。
                _exitPressedMenu    = 0;
                _notReadySinceFrame = NotTracking;
                AddonHelper.FireCallBack(addonSelectYesno, true, 0);
                return;
            }

            // 這條鏈有兩層都可能回 null:AgentModule.Instance() 是手寫取得子
            // (`uiModule == null ? null : uiModule->GetAgentModule()`),GetAgentByInternalId() 則是原生
            // MemberFunction、代理人尚未建立時同樣回 null。
            // 兩層都判空後同幀即用;為 null 時本 tick 不動作,下 tick 重試(每幀熱路徑,不寫 log)。
            AgentModule* agentModule = AgentModule.Instance();
            if (agentModule == null)
            {
                if (EzThrottler.Throttle("ExitDutyHelper-Diag-NoAgentModule", 5000))
                    Svc.Log.Debug("[ExitDutyHelper][診斷] AgentModule.Instance() 目前是 null,還在等。");
                return;
            }

            AgentInterface* agentContentsFinderMenu = agentModule->GetAgentByInternalId(AgentId.ContentsFinderMenu);
            if (agentContentsFinderMenu == null)
            {
                if (EzThrottler.Throttle("ExitDutyHelper-Diag-NoAgent", 5000))
                    Svc.Log.Debug("[ExitDutyHelper][診斷] ContentsFinderMenu 的 agent 還沒建立(GetAgentByInternalId 回 null),還在等。");
                return;
            }

            // ② 窗還沒開才 Show()。
            bool addonFound = GenericHelpers.TryGetAddonByName("ContentsFinderMenu", out AtkUnitBase* addonContentsFinderMenu);
            if (!addonFound || !GenericHelpers.IsAddonReady(addonContentsFinderMenu))
            {
                // 🔴 視窗其實已經找得到、也可見,只是 IsAddonReady 另外兩個條件(LoadedState/IsFullyLoaded)
                // 還沒過 ⇒ 視窗正在自己的載入/穩定流程中,這時候不要再呼叫 Show():
                // 懷疑對一扇已經可見的窗每一幀重複呼叫 Show() 會打斷它的載入流程,造成「視窗一直開著、卻永遠沒 ready」。
                // 只有「真的還沒開出來」(找不到，或找到了但 IsVisible=false，多半是上一發「退出」
                // 已經把它收掉)才需要再喊一次 Show()。
                bool addonVisible = addonFound && addonContentsFinderMenu->IsVisible;

                if (EzThrottler.Throttle("ExitDutyHelper-Diag-NotReady", 5000))
                {
                    string extra = addonFound
                        ? $", LoadedState={addonContentsFinderMenu->UldManager.LoadedState}, IsFullyLoaded={addonContentsFinderMenu->IsFullyLoaded()}"
                        : "";
                    Svc.Log.Debug($"[ExitDutyHelper][診斷] ContentsFinderMenu 還沒就緒。" +
                        $" 視窗找到={addonFound}, IsVisible={(addonFound ? addonVisible.ToString() : "n/a")}{extra}" +
                        (addonVisible ? "（可見但未就緒，這次不重複呼叫 Show()，等它自己穩定）" : "（不可見，呼叫 Show()）"));
                }

                if (!addonVisible)
                {
                    // 選單已經不在了(多半就是上一發「退出」把它收掉了)⇒ 沒有東西要補關。
                    _exitPressedMenu    = 0;
                    _notReadySinceFrame = NotTracking;
                    agentContentsFinderMenu->Show();
                }
                else if (_notReadySinceFrame == NotTracking)
                {
                    // 「可見但未就緒」的第一幀:只記下起點,先讓它自己穩定,這一輪不介入。
                    _notReadySinceFrame = frame;
                }
                else if (frame - _notReadySinceFrame >= NotReadyEscapeFrames)
                {
                    // 🔴 逃生口(見 NotReadyEscapeFrames 的說明):等這麼久還停在「可見但未就緒」,
                    //    代表「重複 Show() 會打斷載入」的懷疑在這個情境下沒說中 ——
                    //    再推一次,不要讓它空轉到 60 秒逾時。重新起算 ⇒ 之後每 NotReadyEscapeFrames 幀重推一次。
                    if (EzThrottler.Throttle("ExitDutyHelper-NotReadyEscape", 10000))
                        Svc.Log.Information($"[ExitDutyHelper] ContentsFinderMenu 可見但未就緒已持續 {frame - _notReadySinceFrame} 幀" +
                                            $"(約 {(frame - _notReadySinceFrame) * 10.07 / 1000:0.0} 秒),走逃生口再送一次 Show()。");

                    _notReadySinceFrame = frame;
                    agentContentsFinderMenu->Show();
                }

                return;
            }

            // 視窗就緒了 ⇒ 不再處於「可見但未就緒」,逃生口的計時歸零。
            _notReadySinceFrame = NotTracking;

            // 🔴 只當作識別用的位址,底下全程只做等值比較,永遠不解參。
            nint menu = (nint)addonContentsFinderMenu;

            // ③ 上一發「退出」的後續:隔開 ExitPressSettleFrames 幀之後,才判斷它到底有沒有生效。
            if (_exitPressedMenu != 0)
            {
                if (_exitPressedMenu != menu)
                {
                    // 按過的那扇選單已經換成別的實例 ⇒ 那一發生效了,不補關。
                    _exitPressedMenu = 0;
                }
                else if (frame - _exitPressedFrame < ExitPressSettleFrames)
                {
                    // 觀察期內:那扇選單這時候有可能正在關閉中,而關閉中的窗 TryGetAddonByName 仍拿得到、
                    // IsAddonReady 三關也全過(擋不住)—— 所以這幾幀對它什麼都不要送。
                    return;
                }
                else
                {
                    // 隔了這麼多幀,同一扇選單還在、確認框也一直沒出現(① 每一幀都先判過)
                    // ⇒ 上一發「什麼都沒發生」,而且它顯然沒有在關閉流程裡,這時候才照原本的用途補送關窗。
                    _exitPressedMenu = 0;

                    // 確認框只要「存在」就代表上一發生效了(① 要求 IsAddonReady,這裡刻意只問存不存在,
                    // 涵蓋「已經建好但還沒就緒」的那幾幀)。隔幾幀之後才問,
                    // 答案就不依賴「它是不是在送出 callback 的同一幀就進得了 addon 清單」。
                    if (GenericHelpers.TryGetAddonByName("SelectYesno", out AtkUnitBase* _))
                        return;

                    if (EzThrottler.Throttle("ExitDutyHelper-CloseMenu", 10000))
                        Svc.Log.Information($"[ExitDutyHelper] 對副本選單送出「退出」之後 {frame - _exitPressedFrame} 幀" +
                                            "既沒有出現確認框、選單也還開著,判定為「上一發沒生效」,補送關窗 callback。");

                    AddonHelper.FireCallBack(addonContentsFinderMenu, false, -2);
                    return;
                }
            }

            // ④ 選「退出」。守衛擋下(回 false)＝這扇選單的這一發已經按過 ⇒ 這一幀什麼都不再送。
            if (!AddonHelper.TryFireCallBack(addonContentsFinderMenu, true, 0))
            {
                if (EzThrottler.Throttle("ExitDutyHelper-Diag-GuardBlocked", 5000))
                    Svc.Log.Debug("[ExitDutyHelper][診斷] ContentsFinderMenu 已就緒，但 TryFireCallBack 被 AddonPressGuard 擋下，還在重試。");
                return;
            }

            Svc.Log.Debug($"[ExitDutyHelper][診斷] 已對 ContentsFinderMenu 送出「退出」callback。");

            // 記下「按了哪一扇、在第幾幀」—— 關窗那一發交給 ③ 隔幾幀之後再決定要不要補。
            _exitPressedMenu  = menu;
            _exitPressedFrame = frame;
        }
    }
}
