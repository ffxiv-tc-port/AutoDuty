using ECommons;
using ECommons.Automation;
using ECommons.Automation.UIInput;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;

namespace AutoDuty.Helpers
{
    internal unsafe static class AddonHelper
    {
        internal static bool SeenAddon = false;

        /// <inheritdoc cref="TryFireCallBack"/>
        /// <remarks>
        /// ⚠️ 這一支<b>刻意維持 void</b>:大量呼叫點寫成
        /// <c>_taskManager.Enqueue(() =&gt; AddonHelper.FireCallBack(...))</c>,
        /// 改成回 <c>bool</c> 會讓那些 lambda 從 <c>Action</c> 重載改綁到
        /// <c>Func&lt;bool&gt;</c> 重載,任務語意會從「做一次」靜默變成「做到回 true 為止」。
        /// 需要知道有沒有真的送出去的呼叫端請改用 <see cref="TryFireCallBack"/>。
        /// </remarks>
        internal static unsafe void FireCallBack(AtkUnitBase* addon, bool boolValue, params object[] args)
            => TryFireCallBack(addon, boolValue, args);

        /// <summary>
        /// 對 <paramref name="addon"/> 送出一組 callback。<b>回 <see langword="true"/> 才代表真的送出去了。</b>
        /// </summary>
        /// <remarks>
        /// 🔴🔴 送出前一定要過 <see cref="AddonPressGuard.TryBeginPress"/>:確認框被按下之後有
        /// 「正在關閉中」的幾幀,這期間 <c>TryGetAddonByName</c> 仍拿得到實例、
        /// <c>IsAddonReady</c> 三關也全過(<b>所以呼叫端那些判斷不是防護</b>),
        /// 再送一次同樣的 callback 就是原生 AccessViolation ——
        /// AVE 在 .NET Core 是 corrupted-state exception,下面那個 <c>try</c>/<c>catch</c>
        /// <b>攔不到</b>(它只擋得住 ECommons 自己丟的受管理例外)。
        /// <para>
        /// 防護下沉在這裡而不是各呼叫端,是因為全 repo 二十幾個送 callback 的點共用這一支,
        /// 漏掉任何一個都等於沒防護。詳見 <see cref="AddonPressGuard"/>。
        /// </para>
        /// <para>
        /// 📌 視窗名稱從實例自己讀(<see cref="AddonPressGuard.ResolveAddonName"/>),
        /// 呼叫端一行都不用改就吃得到防護。
        /// </para>
        /// </remarks>
        internal static unsafe bool TryFireCallBack(AtkUnitBase* addon, bool boolValue, params object[] args)
            => FireCallBackCore(addon, AddonPressGuard.DefaultEscapeFrames, boolValue, args);

        /// <summary>
        /// 與 <see cref="TryFireCallBack"/> 完全一樣,只是改用<b>多次互動窗的短逃生口</b>
        /// (<see cref="AddonPressGuard.RoutineRePressEscapeFrames"/>,15 幀)。
        /// </summary>
        /// <remarks>
        /// 📌 給「按一次開一個子視窗／翻一頁,<b>那扇窗自己不會因此關掉、也不會重建</b>」的窗用
        /// (<c>Talk</c> 是代表,<c>TripleTriadCoinExchange</c> 這種常駐清單窗同形狀)。
        /// 這種窗<b>兩條解除點都不會觸發</b>(位址一直在清單裡 ⇒ 輪詢不放行;不 finalize 也不 setup
        /// ⇒ 生命週期事件不放行),而「對同一個項目再送一次同樣的 callback」本來就是正常流程的下一步 ——
        /// 用預設的 90 幀會把每一次操作拖到約 1.5 秒,而且全程每秒寫一行 Information。
        /// <para>
        /// 🔴 <b>防護沒有被拆掉</b>:同一扇窗的同一個按法在 15 幀內仍然只准送一次,
        /// 而「按下之後正在關閉」的危險窗口 &lt; 10 幀,15 幀不落在裡面
        /// (2026-09-02 艦隊政策:此類多次互動窗一律 15 幀)。
        /// </para>
        /// <para>⚠️ 「按下去那扇窗就會關掉」的窗(確認框族)<b>絕對不能</b>用這一支。</para>
        /// </remarks>
        internal static unsafe bool TryFireCallBackRoutine(AtkUnitBase* addon, bool boolValue, params object[] args)
            => FireCallBackCore(addon, AddonPressGuard.RoutineRePressEscapeFrames, boolValue, args);

        /// <inheritdoc cref="TryFireCallBack"/>
        /// <remarks>
        /// ⚠️ 逃生口幀數刻意<b>不</b>做成公開多載的參數:多載簽章裡多一個 <c>int</c>,
        /// <c>TryFireCallBack(addon, true, 0, 0u)</c> 這種呼叫會靜默改綁到新多載
        /// (第一個 <c>0</c> 被當成逃生口幀數)。要換逃生口就用<b>另一個名字</b>的進入點。
        /// </remarks>
        private static unsafe bool FireCallBackCore(AtkUnitBase* addon, int escapeFrames, bool boolValue, object[] args)
        {
            if (addon == null) return false;

            string addonName = AddonPressGuard.ResolveAddonName(addon);
            if (addonName.Length == 0)
            {
                // 名字讀不出來就守不住(監聽器與輪詢都以名稱為鍵)。實務上不會發生 —— 這裡的
                // 呼叫端全都是先用 GetAddonByName 取到的實例。真的發生時維持原本行為照送,
                // 而不是靜默變成永遠不動作。
                if (EzThrottler.Throttle("AddonPressGuard-Unnamed", 10000))
                    Svc.Log.Information($"[AddonPressGuard] 有一扇視窗(實例 0x{(nint)addon:X})讀不出名稱,這一次的 callback 未受重按防護。");
            }
            else if (!AddonPressGuard.TryBeginPress(addonName, addon, AddonPressGuard.BuildPressKey(boolValue, args), escapeFrames))
            {
                return false;
            }

            try
            {
                Callback.Fire(addon, boolValue, args);
                return true;
            }
            catch (Exception ex)
            {
                Svc.Log.Error($"{ex}");
                return false;
            }
        }

        internal static bool ClickSelectString(int index)
        {
            var addonChecker = AddonChecker("SelectString", out AtkUnitBase* addon, out bool seenAddon);

            // 守衛擋下時什麼都不做、照原本的路徑回 false —— 呼叫端本來就是輪詢重試,語意不變。
            // 🔑 按法字串刻意與 FireCallBack 的算法一致:AddonMaster 的 Entry.Select() 逐字就是
            //    Callback.Fire(addon, true, Index),兩條路徑對同一扇窗按同一個項目必須算成同一次
            //    (SquadronManager 就有同一幀先 FireCallBack(true, 0) 再 ClickSelectString(0) 的雙按)。
            if (!addonChecker && seenAddon
                              && AddonPressGuard.TryBeginPress("SelectString", addon, AddonPressGuard.BuildPressKey(true, [index])))
                new AddonMaster.SelectString(addon).Entries[index].Select();

            if (addonChecker && seenAddon)
                return true;

            return false;
        }

        internal static bool ClickSelectIconString(int index)
        {
            var addonChecker = AddonChecker("SelectIconString", out AtkUnitBase* addon, out bool seenAddon);

            // 按法字串同 ClickSelectString。
            if (!addonChecker && seenAddon
                              && AddonPressGuard.TryBeginPress("SelectIconString", addon, AddonPressGuard.BuildPressKey(true, [index])))
                new AddonMaster.SelectIconString(addon).Entries[index].Select();

            if (addonChecker && seenAddon)
                return true;

            return false;
        }

        /// <remarks>
        /// ⚠️ 上面那道 <c>EzThrottler.Throttle("ClickSelectYesno", 500)</c> <b>不是</b>防護:
        /// 第一次呼叫必定放行,而且它記的是時刻不是「這扇窗按過了」;
        /// 更何況 <c>ExitDutyHelper</c>、<c>VariantManager</c>、<c>MultiboxUtility</c> 都用<b>別的</b>
        /// 路徑按同一扇 SelectYesno,各自的節流 key 互相看不見。
        /// 真正擋住「確認框關閉中重按」的是 <see cref="AddonPressGuard"/> ——
        /// SelectYesno 在那裡屬於「一扇窗只回答一次」,不管哪條路徑、送什麼參數都算同一次按。
        /// </remarks>
        internal static bool ClickSelectYesno(bool yes = true)
        {
            if (!EzThrottler.Throttle("ClickSelectYesno", 500)) return false;

            var addonChecker = AddonChecker("SelectYesno", out AtkUnitBase* addon, out bool seenAddon);

            if (!addonChecker && seenAddon)
            {
                if (!AddonPressGuard.TryBeginPress("SelectYesno", addon))
                    return false;

                // ⚠️ AddonMaster.SelectYesno.Yes()/No() 對「停用中」的按鈕會強制翻 NodeFlags 再點,
                //    遊戲自己的防重按被繞過 —— 所以上面那道守衛是這條路徑唯一的防線。
                if (yes)
                    new AddonMaster.SelectYesno(addon).Yes();
                else
                    new AddonMaster.SelectYesno(addon).No();
                return false;
            }

            if (addonChecker && seenAddon)
                return true;

            return false;
        }

        /// <summary>
        /// 點掉「JournalResult」(任務完成/新人任務結算)視窗。accept=true 按完成、false 按拒絕。
        /// 回傳語意與 <see cref="ClickSelectYesno"/> 一致:true＝視窗已經不在了(這一步做完),
        /// false＝還沒做完,呼叫端(TaskManager 檢查式)要再跑一次。
        /// </summary>
        internal static bool SelectJournalResult(bool accept)
        {
            if (!EzThrottler.Throttle("JournalResult", 500)) return false;

            var addonChecker = AddonChecker("JournalResult", out AtkUnitBase* addon, out bool seenAddon);

            if (!addonChecker && seenAddon)
            {
                if (!AddonPressGuard.TryBeginPress("JournalResult", addon, accept ? "Complete" : "Decline"))
                    return false;

                var journalResult = new AddonMaster.JournalResult(addon);
                if (accept)
                    journalResult.Complete();
                else
                    journalResult.Decline();
                return false;
            }

            if (addonChecker && seenAddon)
                return true;

            return false;
        }

        internal static bool ClickRepair()
        {
            var addonChecker = AddonChecker("Repair", out AtkUnitBase* addon, out bool seenAddon);

            if (!addonChecker && seenAddon && AddonPressGuard.TryBeginPress("Repair", addon, "RepairAll"))
                new AddonMaster.Repair(addon).RepairAll();

            if (addonChecker && seenAddon)
                return true;

            return false;
        }

        /// <remarks>
        /// 📌 <c>Talk</c> 用<b>多次互動窗的短逃生口</b>(<see cref="AddonPressGuard.RoutineRePressEscapeFrames"/>,15 幀):
        /// 對話是一頁一頁推的,那扇窗整段都不會關也不會重建,所以輪詢與生命週期兩條解除點都不會觸發,
        /// 只能靠逃生口放行下一頁(走逃生口是常態,守衛那邊寫 Debug)。推對話的節奏仍由上面那道 500 毫秒節流決定,
        /// 15 幀仍大於「關閉中的那幾幀」—— 推到最後一頁把窗關掉的那個危險窗口照樣擋得住。
        /// </remarks>
        internal static bool ClickTalk()
        {
            if (!EzThrottler.Throttle("ClickTalk", 500)) return false;

            var addonChecker = AddonChecker("Talk", out AtkUnitBase* addon, out bool seenAddon);

            if (!addonChecker && seenAddon
                              && AddonPressGuard.TryBeginPress("Talk", addon, "Click", AddonPressGuard.RoutineRePressEscapeFrames))
                new AddonMaster.Talk(addon).Click();
            
            if (addonChecker && seenAddon)
                return true;
                    
            return false;
        }

        private static bool AddonChecker(string addonName, out AtkUnitBase* outAddon, out bool outSeenAddon)
        {
            outSeenAddon = false;
            
            var gotAddon = GenericHelpers.TryGetAddonByName(addonName, out outAddon);
            var addonReady = gotAddon && GenericHelpers.IsAddonReady(outAddon);

            if (gotAddon && addonReady)
            {
                outSeenAddon = true;
                SeenAddon = true;
                return false;
            }

            if (!Player.Character->IsCasting && SeenAddon && (!gotAddon || !addonReady))
            {
                outSeenAddon = true;
                SeenAddon = false;
                return true;
            }
            return false;
        }

        public static void ClickCheckboxButton(this AtkComponentCheckBox target, AtkComponentBase* addon, uint which, EventType type = EventType.CHANGE)
        => ClickHelper.ClickAddonComponent(addon, target.OwnerNode, which, type);
    }
}
