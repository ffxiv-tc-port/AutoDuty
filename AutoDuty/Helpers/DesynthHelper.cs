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

            // Conditions / ActionManager / InventoryManager 三者的 Instance() 都標
            // [StaticAddress("…", 3)] —— 第二個位置參數是 relativeFollowOffset,isPointer 用預設的
            // false。InteropGenerator 對 isPointer:false 產生的實作逐字是:
            //     if (StaticAddressPointers.pInstance is null)
            //         InteropGenerator.Runtime.ThrowHelper.ThrowNullAddress(名稱, 特徵碼);
            //     return StaticAddressPointers.pInstance;
            // 而 ThrowNullAddress 標了 [DoesNotReturn] 且真的 throw InvalidOperationException。
            // ⇒ 這種 Instance() 只有兩種結局:回非 null,或丟例外。**永遠不會回 null。**
            // 台服特徵碼失配時走的是「丟例外」那條,不是靜默回 null(舊註解在這一點上是錯的),
            // 所以呼叫之後再判一次空是永遠不成立的死碼,已移除。
            // ⚠️ 這個結論**只對 isPointer:false 成立**:isPointer:true 的版本判的是雙重指標、
            //    回傳的是再解參考一次的值,那種 Instance() 真的會回 null,判空不可刪。
            //    刪任何 Instance() 判空之前都要回去讀該型別的宣告確認是哪一種。
            if (Conditions.Instance()->Mounted)
            {
                ActionManager.Instance()->UseAction(ActionType.GeneralAction, 23);
                return;
            }

            Plugin.Action = "Desynthing Inventory";

            // 取進區域變數是為了底下拆解道具時的 GetInventorySlot 重用同一個實例
            // (同一幀內,不跨幀保存),不是為了判空 —— 理由同上。
            InventoryManager* inventoryManager = InventoryManager.Instance();

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
            
            // 🔴 這一個和上面那三個**不是同一種形狀,判空不可刪**:AgentSalvage 標的是
            // [Agent(AgentId.Salvage)],Instance() 由 AgentGetterGenerator 產生,逐字是
            //     var agentModule = AgentModule.Instance();
            //     return agentModule == null ? null : (AgentSalvage*)agentModule->GetAgentByInternalId(…);
            // 而 AgentModule.Instance() 是手寫的 `uiModule == null ? null : uiModule->GetAgentModule()`
            // —— UIModule 尚未建立(登入畫面／跳圖載入中)時整條就是 null。
            // 原本這個方法裡有五處無條件解參考(Show / ItemListRefresh / SelectedCategory /
            // ItemCount / ItemList),底下兩個分支都要用到,所以在分岔前取一次、判空後同幀即用。
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
