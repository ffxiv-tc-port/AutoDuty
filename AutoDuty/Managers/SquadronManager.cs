using AutoDuty.Helpers;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoDuty.Managers
{
    using static Data.Classes;
    //on Rewrite need to check for sufficient seals
    internal class SquadronManager(TaskManager _taskManager)
    {

        internal bool InteractedWithSergeant = false;
        internal bool OpeningMissions = false;
        internal bool ViewingMissions = false;
        internal unsafe void RegisterSquadron(Content content)
        {
            if (content.GCArmyIndex < 0)
            {
                _taskManager.Enqueue(() => Svc.Log.Info("GCArmyIndex was < than 0"), "RegisterSquadron");
                return;
            }
            _taskManager.Enqueue(() => Svc.Log.Info($"Queueing Squadron: {content.Name}"), "RegisterSquadron");
            _taskManager.Enqueue(() => Plugin.Action = $"Queueing Squadron: {content.Name}", "RegisterSquadron");

            AtkUnitBase* addon = null;

            //Check if player is valid
            if (!PlayerHelper.IsValid)
            {
                Svc.Log.Info("player was invalid, waiting for it to be valid.");
                _taskManager.Enqueue(() => PlayerHelper.IsValid, int.MaxValue, "RegisterSquadron");
                Svc.Log.Info("Delaying next Enqueue by 2s");
                _taskManager.DelayNext("RegisterSquadron", 2000);
            }

            //Defining the GUI for the squadron duty finder
            _taskManager.Enqueue(() => GenericHelpers.TryGetAddonByName("GcArmyCapture", out addon), "RegisterSquadron");
            
            // Run logic to open the squadron duty finder
            _taskManager.Enqueue(() => OpenSquadron(addon), "RegisterSquadron");

            // Check if we're viewing missions to select (dungeons)
            _taskManager.Enqueue(() => GenericHelpers.TryGetAddonByName("GcArmyCapture", out addon) && GenericHelpers.IsAddonReady(addon), "RegisterSquadron");
            
            // Select Mission
            // 🔴 這一組(選任務 / 按 OK / 確認框)原本都是 Enqueue(Action)。ECommons LegacyTaskManager
            //    的 Action 多載一律包成「{ task(); return true; }」(TaskManager@Enqueue.cs:63)——
            //    AddonPressGuard 擋下時 FireCallBack 回的 false 被吞掉,這一步照樣算「做完」。
            //    而下面那一步是 int.MaxValue 的無限等待,等的又正好是「這一發有沒有真的送出去」的
            //    下游結果(TerritoryType 有沒有變成該副本)⇒ 守衛擋一次就是佇列永遠卡在那裡、
            //    TaskManager.IsBusy 恆真、自動化整條停住而且不會自己恢復。
            //    改成回傳 TryFireCallBack 的結果(綁 Func<bool?> 多載):擋下就回 false、下一幀再來;
            //    守衛最多擋 AddonPressGuard.DefaultEscapeFrames(90 幀),遠早於這一步的 10 秒逾時
            //    (TaskManager.TimeLimitMS 預設 10000,AutoDuty 的實例 AbortOnTimeout=false)。
            //    🔴 回 false 不可能變成回 null —— null 才會讓 TaskManager.Abort() 清掉整條佇列。
            //    🔴 一併把跨幀指標拿掉:原本沿用上一步(上一幀)抓到的 addon,一旦會重試,
            //    那個指標就從「1 幀前」變成「數百幀前」,而 FireCallBackCore 第一件事就是
            //    ResolveAddonName(addon) 解參 —— 所以每一幀自己重新取窗。
            _taskManager.Enqueue(() =>
                                 {
                                     if (!GenericHelpers.TryGetAddonByName("GcArmyCapture", out AtkUnitBase* addonCapture)
                                         || !GenericHelpers.IsAddonReady(addonCapture))
                                         return true; // 窗根本不在:維持原本「做一次就過」的語意,不要製造新的卡點
                                     return AddonHelper.TryFireCallBack(addonCapture, true, 11, content.GCArmyIndex);
                                 }, "RegisterSquadron-SelectMission");

            // click ok(同上:回傳守衛結果 + 每一幀自己重新取窗)
            _taskManager.Enqueue(() =>
                                 {
                                     if (!GenericHelpers.TryGetAddonByName("GcArmyCapture", out AtkUnitBase* addonCapture)
                                         || !GenericHelpers.IsAddonReady(addonCapture))
                                         return true;
                                     return AddonHelper.TryFireCallBack(addonCapture, true, 13);
                                 }, "RegisterSquadron-ClickOk");
            
            // retrieve the ContentsFinderConfirm addon
            _taskManager.Enqueue(() => GenericHelpers.TryGetAddonByName("ContentsFinderConfirm", out addon) && GenericHelpers.IsAddonReady(addon), "RegisterSquadron");

            // Confirm Duty
            // 🔴 發射的那一幀重新取窗,不沿用上一步(上一幀)抓到的指標:TaskManager 每一步各在不同的幀
            //    執行,而確認框「關閉中」的那幾幀 TryGetAddonByName 與 IsAddonReady 三關全過 ——
            //    對它送 callback 就是攔不到的 AccessViolation。
            // 🔴 這一步同樣要把守衛結果回傳出去(理由見上面「Select Mission」那段):
            //    它後面緊接著就是 int.MaxValue 的無限等待。
            _taskManager.Enqueue(() =>
                                 {
                                     if (!GenericHelpers.TryGetAddonByName("ContentsFinderConfirm", out AtkUnitBase* addonConfirm)
                                         || !GenericHelpers.IsAddonReady(addonConfirm))
                                         return true;
                                     return AddonHelper.TryFireCallBack(addonConfirm, true, 8);
                                 }, "RegisterSquadron-ConfirmDuty");

            // Check if we're in a valid map for the dungeon / paths
            // ⚠️ int.MaxValue ＝無限等待,而且等的正是上面那三發 callback 的下游結果 ——
            //    上面任何一發被守衛擋下又被吞掉,佇列就永遠停在這一行,而且不會自己恢復。
            _taskManager.Enqueue(() => Svc.ClientState.TerritoryType == content.TerritoryType, int.MaxValue, "RegisterSquadron");

            _taskManager.Enqueue(() => {
                if (Svc.ClientState.TerritoryType == content.TerritoryType)
                {
                    // Reset states because we queued the correct duty, this is for looping
                    Svc.Log.Info("Resetting states for loop.");
                    InteractedWithSergeant = false;
                    OpeningMissions = false;
                    ViewingMissions = false;
                    return true; // Return true to continue the task sequence
                }
                return false; // Return false if we are not in correct duty
            }, "RegisterSquadron");
        }
        

        // Try to open the squadron menu by finding the squadron manager until specific GUI window checks are passed
        internal unsafe bool OpenSquadron(AtkUnitBase* aub)
        {
            ViewingMissions = false;
            OpeningMissions = false;
            InteractedWithSergeant = false;
            AtkUnitBase* sergeantListMenu = null;
            AtkUnitBase* expeditionResultScreen = null;

            if (aub != null)
            {
                return true;
            }

            if (GenericHelpers.TryGetAddonByName("Talk", out AtkUnitBase* addonTalk) && GenericHelpers.IsAddonReady(addonTalk))
            {
                // Talk window up ClickIt
                AddonHelper.ClickTalk();
                Svc.Log.Info("Clicking Talk");
                return false;
            }

            if (GenericHelpers.TryGetAddonByName("GcArmyCapture", out AtkUnitBase* _))
            {
                // Viewing missions, move on to the next step for registering
                ViewingMissions = true;
                Svc.Log.Info("ViewingMissions: TRUE");
                return true;
            }

            // Attempt to get the squadron sergeant once and reuse the result --- This still sets this every call I will change this to one and done later.
            IGameObject? gameObject = ObjectHelper.GetObjectByDataId(1016924u) ?? ObjectHelper.GetObjectByDataId(1016986u) ?? ObjectHelper.GetObjectByDataId(1016987u);
            if (gameObject == null || !MovementHelper.Move(gameObject, 0.25f, 6f))
            {
                return false;
            }

            // Check if the GcArmyExpeditionResult addon is open
            if (GenericHelpers.TryGetAddonByName("GcArmyExpeditionResult", out expeditionResultScreen))
            {
                Svc.Log.Info("Viewing expedition result");
                // Close the expedition result menu
                AddonHelper.FireCallBack(expeditionResultScreen, true, 0);
                // Reset states so we try to open the squadron view again until we hit the squadron duty GUI
                OpeningMissions = false;
                InteractedWithSergeant = false;
                ViewingMissions = false;
                return false; // Exit to retry interaction
            }

            // Check if the SelectString addon is open (List Menu for "Command Missions", "Squadron Missions", etc.)
            if (GenericHelpers.TryGetAddonByName("SelectString", out sergeantListMenu))
            {
                // Successfully interacted with the Sergeant
                InteractedWithSergeant = true;
                AddonHelper.FireCallBack(sergeantListMenu, true, 0);
                AddonHelper.ClickSelectString(0);
                OpeningMissions = true; // Set the opened missions state to true
                return false;
            }

            // Continuously check if we've interacted with the sergeant until we open up the SelectString list menu
            // Check if we have interacted with the sergeant
            if (!InteractedWithSergeant)
            {
                ObjectHelper.InteractWithObject(gameObject);
                if (GenericHelpers.TryGetAddonByName("GcArmyCapture", out AtkUnitBase* _))
                {
                    InteractedWithSergeant = true;
                }
                    
                return false;
            }

            return false;
        }

    }
}
