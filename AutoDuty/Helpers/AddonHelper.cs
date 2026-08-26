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

        internal static unsafe void FireCallBack(AtkUnitBase* addon, bool boolValue, params object[] args)
        {
            var addonPtr = addon;
            if (addon == null || addonPtr is null) return;
            try
            {
                Callback.Fire(addonPtr, boolValue, args);
            }
            catch (Exception ex) 
            { 
                Svc.Log.Error($"{ex}");
            }
        }

        internal static bool ClickSelectString(int index)
        {
            var addonChecker = AddonChecker("SelectString", out AtkUnitBase* addon, out bool seenAddon);

            if (!addonChecker && seenAddon)
                new AddonMaster.SelectString(addon).Entries[index].Select();

            if (addonChecker && seenAddon)
                return true;

            return false;
        }

        internal static bool ClickSelectIconString(int index)
        {
            var addonChecker = AddonChecker("SelectIconString", out AtkUnitBase* addon, out bool seenAddon);

            if (!addonChecker && seenAddon)
                new AddonMaster.SelectIconString(addon).Entries[index].Select();

            if (addonChecker && seenAddon)
                return true;

            return false;
        }

        internal static bool ClickSelectYesno(bool yes = true)
        {
            if (!EzThrottler.Throttle("ClickSelectYesno", 500)) return false;

            var addonChecker = AddonChecker("SelectYesno", out AtkUnitBase* addon, out bool seenAddon);

            if (!addonChecker && seenAddon)
            {
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

            if (!addonChecker && seenAddon)
                new AddonMaster.Repair(addon).RepairAll();

            if (addonChecker && seenAddon)
                return true;

            return false;
        }

        internal static bool ClickTalk()
        {
            if (!EzThrottler.Throttle("ClickTalk", 500)) return false;

            var addonChecker = AddonChecker("Talk", out AtkUnitBase* addon, out bool seenAddon);

            if (!addonChecker && seenAddon)
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
