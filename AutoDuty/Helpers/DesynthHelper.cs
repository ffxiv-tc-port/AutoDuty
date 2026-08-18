using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoDuty.Helpers
{
    using Lumina.Excel.Sheets;

    internal class DesynthHelper : ActiveHelperBase<DesynthHelper>
    {
        protected override string Name        => nameof(DesynthHelper);
        protected override string DisplayName => "Desynthing";

        protected override string[] AddonsToClose { get; } = ["Desynth", "SalvageResult", "SalvageDialog", "SalvageItemSelector"];

        internal override void Start()
        {
            _maxDesynthLevel = PlayerHelper.GetMaxDesynthLevel();
            base.Start();
        }

        private float _maxDesynthLevel = 1;

        protected override unsafe void HelperUpdate(IFramework framework)
        {
            if (Plugin.States.HasFlag(PluginState.Navigating) || Plugin.InDungeon)
                Stop();

            if (!EzThrottler.Throttle("Desynth", 250))
                return;

            // Conditions 與 ActionManager 都是 [StaticAddress] 解析出來的靜態實例,台服特徵碼失配時
            // Resolve 會靜默留下 null,原本兩者都無條件解參考。取進區域變數判空後同幀即用;
            // 判空放在原本解參考的位置上,不提前到節流之前(EzThrottler.Throttle 有副作用)。
            Conditions* conditions = Conditions.Instance();
            if (conditions == null)
                return;

            if (conditions->Mounted)
            {
                ActionManager* actionManager = ActionManager.Instance();
                if (actionManager != null)
                    actionManager->UseAction(ActionType.GeneralAction, 23);
                return;
            }

            Plugin.Action = "Desynthing Inventory";

            // InventoryManager 同樣是 [StaticAddress] 靜態實例。這裡取一次,底下拆解道具時的
            // GetInventorySlot 也重用同一個區域變數(同一幀內,不跨幀保存)。
            InventoryManager* inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null)
                return;

            if (inventoryManager->GetEmptySlotsInBag() < 1)
            {
                Stop();
                return;
            }

            if (PlayerHelper.IsOccupied)
                return;

            if (GenericHelpers.TryGetAddonByName("SalvageResult", out AtkUnitBase* addonSalvageResult) && GenericHelpers.IsAddonReady(addonSalvageResult))
            {
                DebugLog("Closing SalvageResult");
                addonSalvageResult->Close(true);
                return;
            }
            else if (GenericHelpers.TryGetAddonByName("SalvageDialog", out AtkUnitBase* addonSalvageDialog) && GenericHelpers.IsAddonReady(addonSalvageDialog))
            {
                DebugLog("Confirming SalvageDialog");
                AddonHelper.FireCallBack(addonSalvageDialog, true, 0, false);
                return;
            }
            
            // AgentSalvage.Instance() 走 AgentModule.Instance(),而後者在 UIModule 尚未建立時回 null
            // (產生器出來的實作逐字是 agentModule == null ? null : ...)。原本這個方法裡有五處
            // 無條件解參考(Show / ItemListRefresh / SelectedCategory / ItemCount / ItemList),
            // 底下兩個分支都要用到,所以在分岔前取一次、判空後同幀即用。
            AgentSalvage* agentSalvage = AgentSalvage.Instance();
            if (agentSalvage == null)
                return;

            if (!GenericHelpers.TryGetAddonByName<AddonSalvageItemSelector>("SalvageItemSelector", out var addonSalvageItemSelector))
            {
                agentSalvage->AgentInterface.Show();
                EzThrottler.Throttle("Desynth", 2000, true);
                return;
            }
            else if (GenericHelpers.IsAddonReady((AtkUnitBase*)addonSalvageItemSelector) && addonSalvageItemSelector->IsReady)
            {
                agentSalvage->ItemListRefresh(true);
                if (agentSalvage->SelectedCategory != AgentSalvage.SalvageItemCategory.InventoryEquipment)
                {
                    DebugLog("Switching Category");
                    AddonHelper.FireCallBack((AtkUnitBase*)addonSalvageItemSelector, true, 11, 0);
                    return;
                }
                else if (addonSalvageItemSelector->ItemCount > 0)
                {
                    // ItemList 是裸指標陣列(AgentSalvage +0x38),清單還沒建好時是 null,而
                    // ItemCount 是獨立欄位、擋不住它。沒有長度欄位可驗,只能以 ItemCount 為界,
                    // 所以先擋掉 null 再進迴圈(失敗形式=這一 tick 不動作,下次節流放行再試)。
                    AgentSalvage.SalvageListItem* itemList = agentSalvage->ItemList;
                    if (itemList == null)
                        return;

                    var foundOne = false;
                    for (int i = 0; i < agentSalvage->ItemCount; i++)
                    {
                        var item = itemList[i];
                        // GetInventorySlot 對不存在的容器/格號會回 null,原本是兩層鏈式裸讀。
                        // 拆開判空,取不到就跳過這一件(沿用同迴圈既有的 continue 慣例)。
                        InventoryItem* inventorySlot = inventoryManager->GetInventorySlot(item.InventoryType, (int)item.InventorySlot);
                        if (inventorySlot == null) continue;

                        var itemId = inventorySlot->ItemId;

                        if (itemId == 10146) continue;

                        var itemSheetRow = Svc.Data.Excel.GetSheet<Item>()?.GetRow(itemId);
                        var itemLevel = itemSheetRow?.LevelItem.ValueNullable?.RowId;
                        var desynthLevel = PlayerHelper.GetDesynthLevel(item.ClassJob);

                        if (itemLevel == null || itemSheetRow == null) continue;

                        if (!Plugin.Configuration.AutoDesynthSkillUp || (desynthLevel < itemLevel + Plugin.Configuration.AutoDesynthSkillUpLimit && desynthLevel < _maxDesynthLevel))
                        {
                            DebugLog($"Salvaging Item({i}): {itemSheetRow.Value.Name.ToString()} with iLvl {itemLevel} because our desynth level is {desynthLevel}");
                            foundOne = true;
                            AddonHelper.FireCallBack((AtkUnitBase*)addonSalvageItemSelector, true, 12, i);
                            return;
                        }
                    }

                    if (!foundOne)
                    {
                        addonSalvageItemSelector->Close(true);
                        DebugLog("Desynth Finished");
                        Stop();
                    }
                }
                else
                {
                    addonSalvageItemSelector->Close(true);
                    DebugLog("Desynth Finished");
                    Stop();
                }
            }
        }
    }
}
