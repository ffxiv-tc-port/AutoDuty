using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using ECommons;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Linq;
using AutoDuty.IPC;
using ECommons.Throttlers;

namespace AutoDuty.Helpers
{
    using Dalamud.Game.ClientState.Objects.Types;
    using System.Numerics;
    using ECommons.UIHelpers.AtkReaderImplementations;
    using FFXIVClientStructs.FFXIV.Client.Game.UI;
    using Lumina.Excel.Sheets;

    internal class TripleTriadCardSellHelper : ActiveHelperBase<TripleTriadCardSellHelper>
    {
        protected override string Name        { get; } = nameof(TripleTriadCardSellHelper);
        protected override string DisplayName { get; } = "Selling TTT Cards";
        protected override string[] AddonsToClose { get; } = ["SelectYesno", "SelectIconString", "TripleTriadCoinExchange", "ShopCardDialog"];

        internal override void Start()
        {
            if (!QuestManager.IsQuestComplete(65970))
            {
                Svc.Log.Info("Gold Saucer requires having completed quest: It Could Happen To You");
            }
            else if(!InventoryHelper.GetInventorySelection(InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4)
                                   .Any(iv =>
                                        {
                                            Item? excelItem = InventoryHelper.GetExcelItem(iv.ItemId);
                                            return excelItem is { ItemUICategory.RowId: 86 };
                                        }))
            {
                Svc.Log.Info("No TTT cards in inventory");
            }
            else if (State != ActionState.Running)
            {
                base.Start();
            }
        }

        public const           int         GoldSaucerTerritoryType       = 144;

        public static readonly Vector3     TripleTriadCardVendorLocation = new(-56.1f, 1.6f, 16.6f);
        private const uint tripleTriadVendorDataId = 1016294u;
        private static IGameObject? tripleTriadVendorGameObject => ObjectHelper.GetObjectByDataId(tripleTriadVendorDataId);

        private static unsafe AtkUnitBase*                   addonExchange         = null;
        private static unsafe ReaderTripleTriadCoinExchange? readerExchange        = null;
        private static unsafe AtkUnitBase*                   addonSelectIconString = null;

        protected override unsafe void HelperUpdate(IFramework framework)
        {
            if (Plugin.States.HasFlag(PluginState.Navigating) || Plugin.InDungeon)
            {
                Stop();
                return;
            }

            if (!EzThrottler.Throttle("TTT", 250))
                return;

            if (GotoHelper.State == ActionState.Running)
            {
                //Svc.Log.Debug("Goto Running");
                return;
            }

            if (Svc.ClientState.TerritoryType != GoldSaucerTerritoryType)
            {
                Svc.Log.Debug("Moving to Gold Saucer");
                GotoHelper.Invoke(GoldSaucerTerritoryType, [TripleTriadCardVendorLocation], 0.25f, 2f, false);

                return;
            }

            if (ObjectHelper.GetDistanceToPlayer(TripleTriadCardVendorLocation) > 4 && PlayerHelper.IsReady && VNavmesh_IPCSubscriber.Nav_IsReady() && !VNavmesh_IPCSubscriber.SimpleMove_PathfindInProgress() &&
                VNavmesh_IPCSubscriber.Path_NumWaypoints()         == 0)
            {
                Svc.Log.Debug("Setting Move to Triple Triad Card Trader");
                MovementHelper.Move(TripleTriadCardVendorLocation, 0.25f, 4f);
                return;
            }
            else if (ObjectHelper.GetDistanceToPlayer(TripleTriadCardVendorLocation) > 4 && VNavmesh_IPCSubscriber.Path_NumWaypoints() > 0)
            {
                Svc.Log.Debug("Moving to Triple Triad Card Trader");
                return;
            }
            else if (ObjectHelper.GetDistanceToPlayer(TripleTriadCardVendorLocation) <= 4 && VNavmesh_IPCSubscriber.Path_NumWaypoints() > 0)
            {
                Svc.Log.Debug("Stopping Path");
                VNavmesh_IPCSubscriber.Path_Stop();
                return;
            }
            else if (ObjectHelper.GetDistanceToPlayer(TripleTriadCardVendorLocation) <= 4 && VNavmesh_IPCSubscriber.Path_NumWaypoints() == 0)
            {
                // ⚠️ 這裡原本的順序是反的:先 `IsAddonReady(addonExchange)`(會解參考上一個
                // tick 存下來的 static 指標),而唯一會重新解析它的 TryGetAddonByName 被擋在
                // 這個檢查之後的分支裡。addonExchange 是 static 且 Stop() 不歸零,舊指標會跨
                // session、跨換區、跨副本存活,下一個 tick 就對已關閉的 addon 解參考 →
                // 攔不到的 AccessViolation。
                // 修法:每個 tick 先無條件重新解析。TryGetAddonByName 找不到時會把 out 參數
                // 設成 null(ECommons AddonHelpers),所以指標會自動歸零——這正是同 repo 的
                // RepairHelper / QueueHelper 之所以用同樣的 static 欄位卻安全的原因。
                if (!GenericHelpers.TryGetAddonByName("TripleTriadCoinExchange", out addonExchange))
                    addonExchange = null;

                if (addonExchange == null || !GenericHelpers.IsAddonReady(addonExchange))
                {
                    readerExchange = null;
                    if (GenericHelpers.TryGetAddonByName("SelectIconString", out addonSelectIconString) && GenericHelpers.IsAddonReady(addonSelectIconString))
                    {
                        Svc.Log.Debug($"Clicking SelectIconString");
                        AddonHelper.ClickSelectIconString(1);
                    }
                    else if (addonExchange == null && tripleTriadVendorGameObject != null)
                    {
                        Svc.Log.Debug("Interacting with TTT");
                        ObjectHelper.InteractWithObject(tripleTriadVendorGameObject);
                    }
                }
                else
                {
                    // 不用 ??=:reader 會把 addon 指標存進自己的欄位跨 tick 持有。
                    // addonExchange 現在每 tick 重新解析,reader 也必須跟著重建。
                    readerExchange = new ReaderTripleTriadCoinExchange(addonExchange);

                    if (readerExchange.Entries.Count <= 0)
                    {
                        Stop();
                        return;
                    }

                    if (GenericHelpers.TryGetAddonByName("ShopCardDialog", out AtkUnitBase* shopCardDialog) && GenericHelpers.IsAddonReady(shopCardDialog))
                    {
                        AddonHelper.FireCallBack(shopCardDialog, true, 0, readerExchange.Entries.First().Count);
                        return;
                    }
                    // 📌 這一發是「點清單上的第一張卡」,按下去之後開的是 ShopCardDialog ——
                    //    TripleTriadCoinExchange 這扇窗「全程不關也不重建」,所以守衛的兩條解除點
                    //    (位址從清單消失 / PreFinalize+PostSetup)一條都不會觸發;而賣掉一張之後
                    //    下一張卡又會遞補成 entry 0,參數組完全一樣 ⇒ 對守衛來說每一張卡都長得像「重按」。
                    //    用預設的 90 幀逃生口會讓每張卡從約 0.5 秒變成約 1.5 秒,而且整段賣卡過程
                    //    每秒寫一行 Information。改用多次互動窗的短逃生口(15 幀,與 Talk 同一條艦隊政策):
                    //    同一扇窗的同一個按法在 15 幀內仍然只准送一次(呼叫端本身還有 250 毫秒節流),
                    //    而「按下之後正在關閉」的危險窗口不到 10 幀 —— 防護沒有被拆掉,只是不再誤判。
                    AddonHelper.TryFireCallBackRoutine(addonExchange, true, 0, 0u);
                }
            }
        }
    }
}
