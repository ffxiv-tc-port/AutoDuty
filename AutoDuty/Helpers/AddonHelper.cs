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
        /// <para>
        /// 📌 2026-09-03 更新:上面說的那些 <c>Enqueue</c> 呼叫點<b>已經一個都不剩</b> ——
        /// SquadronManager 三處與 VariantManager 四處都改成 statement lambda 回傳
        /// <see cref="TryFireCallBack"/> 的結果(那種寫法綁的必然是
        /// <c>Enqueue(Func&lt;bool?&gt;, string)</c>,編譯期就定死)。
        /// ⚠️ <b>但這不代表現在可以放心改簽章</b>:實測(2026-09-03,net9 編譯器,
        /// 對照 ECommons 兩個 <c>(lambda, string)</c> 多載)<c>() =&gt; 回 void 的方法()</c> 綁
        /// <c>Action</c>、<c>() =&gt; 回 bool 的方法()</c> 綁 <c>Func&lt;bool?&gt;</c> ——
        /// 只要日後再寫出一個 <c>Enqueue(() =&gt; FireCallBack(...))</c>,同一個陷阱就回來了。
        /// 要動這支的簽章之前<b>自己重新枚舉一次呼叫點</b>,不要採信這段話裡的數量。
        /// </para>
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
        /// 與 <see cref="TryFireCallBack"/> 完全一樣(逃生口照樣是
        /// <see cref="AddonPressGuard.DefaultEscapeFrames"/> 90 幀),只是<b>額外告訴守衛</b>:
        /// 這一發成功的話會開出一扇名為 <paramref name="opensDialog"/> 的子視窗。
        /// </summary>
        /// <remarks>
        /// 📌 給「按一次開出一扇子視窗,<b>自己卻不會因此關掉也不會重建</b>」的常駐清單窗用。
        /// <c>TripleTriadCoinExchange</c>(賣卡)是代表:那扇窗全程開著 ⇒ 守衛的兩條解除點
        /// (位址從清單消失 / PreFinalize+PostSetup)一條都不會觸發,而賣掉一張之後下一張又遞補
        /// 成 entry 0、參數組完全一樣 ⇒ 守衛會把<b>每一張卡</b>都看成「對同一位址重按同一個 key」,
        /// 每張卡都得等滿逃生口(0.5 秒變約 1.5 秒)而且整段過程每秒寫一行 Information。
        /// <para>
        /// 🔑 <b>解法不是把逃生口縮短</b> —— 那要押注「按它不會把它關掉」這個<b>沒有離線證據</b>的假設,
        /// 假設不成立就是對關閉中的窗重按 ＝ 攔不到的原生 AccessViolation(遊戲當場關閉)。
        /// 這一支改成給守衛一個<b>正面證據</b>:子視窗出現過又收掉,就代表上一發確實被一扇活著的窗
        /// 處理掉了,那一刻才解除封鎖(<c>AddonPressGuard.ReleaseParentOfClosedDialog</c>)。
        /// 子視窗沒出現時什麼都不會解除,防護完整留到 90 幀逃生口為止。
        /// </para>
        /// <para>
        /// ⚠️ <paramref name="opensDialog"/> 寫錯的後果是<b>提早解除永遠不發生 ＝ 靜默退回 90 幀</b>
        /// (慢,不會崩) —— 這一支<b>不會</b>比 <see cref="TryFireCallBack"/> 更危險。
        /// </para>
        /// </remarks>
        /// <param name="opensDialog">這一發成功的話會開出來的那扇子視窗名稱。</param>
        internal static unsafe bool TryFireCallBackOpeningDialog(AtkUnitBase* addon, string opensDialog, bool boolValue, params object[] args)
            => FireCallBackCore(addon, AddonPressGuard.DefaultEscapeFrames, boolValue, args, opensDialog);

        /// <inheritdoc cref="TryFireCallBack"/>
        /// <remarks>
        /// ⚠️ 逃生口幀數刻意<b>不</b>做成公開多載的參數:多載簽章裡多一個 <c>int</c>,
        /// <c>TryFireCallBack(addon, true, 0, 0u)</c> 這種呼叫會靜默改綁到新多載
        /// (第一個 <c>0</c> 被當成逃生口幀數)。要換逃生口就用<b>另一個名字</b>的進入點。
        /// </remarks>
        private static unsafe bool FireCallBackCore(AtkUnitBase* addon, int escapeFrames, bool boolValue, object[] args,
                                                    string? opensDialog = null)
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
            else if (!AddonPressGuard.TryBeginPress(addonName, addon, AddonPressGuard.BuildPressKey(boolValue, args), escapeFrames,
                                                    opensDialog))
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
