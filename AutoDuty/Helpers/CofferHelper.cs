using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using System.Collections.Generic;
using System.Linq;
using ECommons.Throttlers;

namespace AutoDuty.Helpers
{
    using FFXIVClientStructs.FFXIV.Client.UI.Misc;
    using Lumina.Excel.Sheets;

    internal class CofferHelper : ActiveHelperBase<CofferHelper>
    {
        private readonly Dictionary<uint, int> doneItems = [];
        private          int           initialGearset;

        internal override unsafe void Start()
        {
            base.Start();
            // RaptureGearsetModule.Instance() 是手寫取得子,UIModule 尚未建立時會回 null。
            // 取不到就記成 -1(未知),讓下面的「換回原本裝備組」整段不執行 —— 絕不能拿 0 當備援,
            // 那會在收工時把使用者切到第 0 組裝備。
            RaptureGearsetModule* startModule = RaptureGearsetModule.Instance();
            this.initialGearset = startModule == null ? -1 : startModule->CurrentGearsetIndex;
            this.doneItems.Clear();
        }

        protected override string Name        { get; } = nameof(CofferHelper);
        protected override string DisplayName { get; } = "Opening Coffers";

        protected override unsafe void HelperUpdate(IFramework framework)
        {
            if (!this.UpdateBase())
                return;

            if (Conditions.Instance()->Mounted)
            {
                DebugLog("Dismount");
                ActionManager.Instance()->UseAction(ActionType.GeneralAction, 23);
                return;
            }

            if (InventoryManager.Instance()->GetEmptySlotsInBag() < 1)
            {
                this.DebugLog("No empty slots");
                this.Stop();
                return;
            }

            if (PlayerHelper.IsCasting || !PlayerHelper.IsReadyFull || Player.IsBusy)
                return;

            this.DebugLog("Checking items");

            IEnumerable <InventoryItem> items = InventoryHelper.GetInventorySelection(InventoryHelper.Bag)
                                                               .Where(iv =>
                                                                      {
                                                                          Item? excelItem = InventoryHelper.GetExcelItem(iv.ItemId);
                                                                          this.DebugLog($"checking item: {iv.ItemId} in {iv.Container} {iv.Slot}");
                                                                          return iv.ItemId > 0 && (!this.doneItems.ContainsKey(iv.ItemId) || this.doneItems[iv.ItemId] != iv.Quantity) && excelItem.HasValue && ValidCoffer(excelItem.Value);
                                                                      });


            // 原本取出 module 後,底下 if 判的是 items/設定值,module 自己從沒判過空,
            // 卻在 66/69/76/94/100 五處被解參考。判空後同幀即用;為 null 時本 tick 不動作,
            // 下 tick 重試(每幀熱路徑,不寫 log),逾時由 Start() 排的 TimeOut 收尾。
            RaptureGearsetModule* module = RaptureGearsetModule.Instance();
            if (module == null)
                return;

            if (items.Any())
            {
                this.DebugLog("item found");
                if (Plugin.Configuration.AutoOpenCoffersGearset != null && module->CurrentGearsetIndex != Plugin.Configuration.AutoOpenCoffersGearset)
                {
                    this.DebugLog("change gearset");
                    if (!module->IsValidGearset((int)Plugin.Configuration.AutoOpenCoffersGearset))
                    {
                        this.DebugLog("invalid gearset");
                        Plugin.Configuration.AutoOpenCoffersGearset = null;
                        Plugin.Configuration.Save();
                    } else
                    {
                        module->EquipGearset(Plugin.Configuration.AutoOpenCoffersGearset.Value);
                        return;
                    }
                }

                InventoryItem item = items.First();

                InventoryHelper.UseItem(item.ItemId);

                if (!PlayerHelper.IsCasting)
                {
                    this.DebugLog("failed to use item");
                    return;
                }

                this.DebugLog("item used");
                this.doneItems[item.ItemId] = item.Quantity;

            } else if (this.initialGearset >= 0 && this.initialGearset != module->CurrentGearsetIndex)
            {
                if (!EzThrottler.Throttle("CofferChangeBack", 1000))
                    return;

                this.DebugLog("change back to original gearset");
                module->EquipGearset(this.initialGearset);
            }
            else
            {
                this.DebugLog("no items found");
                this.Stop();
            }
        }

        internal static bool ValidCoffer(Item item) => // Miscellany
            item.ItemAction.RowId is 1085 or 388 && item.ItemUICategory.RowId is 61 && (!Plugin.Configuration.AutoOpenCoffersBlacklistUse || !Plugin.Configuration.AutoOpenCoffersBlacklist.ContainsKey(item.RowId));
    }
}