using ECommons.DalamudServices;
using ECommons.ExcelServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using System;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using ECommons.Throttlers;

namespace AutoDuty.Helpers
{
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Linq;
    using FFXIVClientStructs.FFXIV.Client.UI.Misc;
    using Lumina.Excel.Sheets;

    internal unsafe static class InventoryHelper
    {
        internal static InventoryType[] Bag       => [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];
        internal static uint            SlotsFree => InventoryManager.Instance()->GetEmptySlotsInBag();
        internal static uint            MySeals   => InventoryManager.Instance()->GetCompanySeals(PlayerState.Instance()->GrandCompany);
        internal static uint            MaxSeals  => InventoryManager.Instance()->GetMaxCompanySeals(PlayerState.Instance()->GrandCompany);

        internal static int ItemCount(uint itemId) => InventoryManager.Instance()->GetInventoryItemCount(itemId);

        internal static void UseItem(uint itemId) => ActionManager.Instance()->UseAction(ActionType.Item, itemId, extraParam: 65535);

        internal static bool UseItemUntilStatus(uint itemId, uint statusId, float minTime = 0, bool allowHq = true)
        {
            if (!EzThrottler.Throttle("UseItemUntilStatus", 250) || !PlayerHelper.IsReadyFull || Player.Character->IsCasting)
                return false;

            if (PlayerHelper.HasStatus(statusId, minTime))
                return true;

            UseItemIfAvailable(itemId, allowHq);
            return false;
        }

        internal static bool UseItemUntilAnimationLock(uint itemId, bool allowHq = true)
        {
            if (PlayerHelper.IsAnimationLocked)
                return true;

            if (!EzThrottler.Throttle("UseItemUntilStatus", 250) || !PlayerHelper.IsReady || PlayerHelper.IsCasting)
                return false;

            UseItemIfAvailable(itemId, allowHq);
            return false;
        }

        internal static void UseItemIfAvailable(uint itemId, bool allowHq = true)
        {
            if (allowHq && ItemCount(itemId + 1_000_000) >= 1)
            {
                Svc.Log.Debug($"Using Item: {itemId + 1_000_000}");
                UseItem(itemId + 1_000_000);
            }
            else if (ItemCount(itemId) >= 1)
            {
                UseItem(itemId);
                Svc.Log.Debug($"Using Item: {itemId}");
            }
        }

        internal static bool IsItemAvailable(uint itemId, bool allowHq = true) => (allowHq && ItemCount(itemId + 1_000_000) >= 1) || ItemCount(itemId) >= 1;

        internal static Item? GetExcelItem(uint itemId) => Svc.Data.GetExcelSheet<Item>()?.GetRowOrDefault(itemId);

        internal static RaptureGearsetModule.GearsetItemIndex GetEquippedSlot(Item itemData)
        {
            RaptureGearsetModule.GearsetItemIndex targetSlot = itemData!.EquipSlotCategory.Value switch
            {
                { MainHand: > 0 } => RaptureGearsetModule.GearsetItemIndex.MainHand,
                { OffHand: > 0 } => RaptureGearsetModule.GearsetItemIndex.OffHand,
                { Head: > 0 } => RaptureGearsetModule.GearsetItemIndex.Head,
                { Body: > 0 } => RaptureGearsetModule.GearsetItemIndex.Body,
                { Gloves: > 0 } => RaptureGearsetModule.GearsetItemIndex.Hands,
                { Legs: > 0 } => RaptureGearsetModule.GearsetItemIndex.Legs,
                { Feet: > 0 } => RaptureGearsetModule.GearsetItemIndex.Feet,
                { Ears: > 0 } => RaptureGearsetModule.GearsetItemIndex.Ears,
                { Neck: > 0 } => RaptureGearsetModule.GearsetItemIndex.Neck,
                { Wrists: > 0 } => RaptureGearsetModule.GearsetItemIndex.Wrists,
                { FingerL: > 0 } => RaptureGearsetModule.GearsetItemIndex.RingLeft,
                { FingerR: > 0 } => RaptureGearsetModule.GearsetItemIndex.RingRight,
                _ => throw new ArgumentOutOfRangeException("the heck is " + itemData.RowId)
            };

            return targetSlot;
        }

        internal static void EquipGear(Item item, InventoryType type, int slotIndex, RaptureGearsetModule.GearsetItemIndex targetSlot) => InventoryManager.Instance()->MoveItemSlot(type, (ushort)slotIndex, InventoryType.EquippedItems, (ushort)targetSlot, true);

        /// <summary>
        /// 依序掃過 <paramref name="types"/> 列出的容器，找出第一個空格；全部都沒有空格就回 <see langword="false"/>。
        /// </summary>
        /// <remarks>
        /// 🔴 原本回 <c>(InventoryType, ushort)</c>，用 <c>slot &gt; 0</c> 判「這個容器有空格」——
        /// <b>哨兵值 0 與合法的第 0 格撞值</b>。槽位索引的值域是 <c>0 .. Size-1</c>（<c>Size</c> 是
        /// <c>int</c> 且非負），<b>0 永遠是合法索引</b>，所以 <c>ushort</c> 裡根本沒有可用來當「找不到」的值。
        /// <para>
        /// 實際後果：某個背包的第一個空格剛好是第 0 格時，該背包會被判成「沒有空格」而跳過；
        /// 四個背包都是這種狀況就整組回報失敗。呼叫端 <c>AutoEquipHelper</c> 已經先用
        /// <c>GetEmptySlotsInBag() &gt;= 1</c> 確認過背包有空位，於是走進那句
        /// <c>"no empty inventory slot found.. somehow"</c> —— 那個 <c>somehow</c> 就是這個 bug 的徵狀。
        /// </para>
        /// <para>
        /// 🔑 修法不是把哨兵換成另一個值，而是<b>讓「找不到」根本無法用 slot 表示</b>：
        /// 成功與否走回傳的 <c>bool</c>，<c>out</c> 的槽位只在回 <see langword="true"/> 時有意義。
        /// 這也與本檔既有的 <see cref="TryGetContainer"/>／<see cref="TryGetItem"/> 一致。
        /// </para>
        /// </remarks>
        internal static bool TryGetFirstAvailableSlot(out InventoryType foundType, out ushort foundSlot, params InventoryType[] types)
        {
            foreach (InventoryType type in types)
            {
                if (TryGetFirstAvailableSlot(type, out ushort slot))
                {
                    foundType = type;
                    foundSlot = slot;
                    return true;
                }
            }

            foundType = InventoryType.Invalid;
            foundSlot = 0;
            return false;
        }

        /// <summary>
        /// 依 <paramref name="type"/> 取出背包容器；取不到（或還沒配置好 <c>Items</c>）就回 <see langword="false"/>。
        /// </summary>
        /// <remarks>
        /// 🔴 <c>GetInventoryContainer</c> 是 <c>[MemberFunction]</c>，<b>合法回 <c>null</c></b>：
        /// 傳進去的 <c>InventoryType</c> 不是這個角色現在持有的容器（例如雇員／部隊倉庫沒開、
        /// 或值本身來自別的外掛的 IPC 而不是我們自己算出來的）時就是 <c>null</c>。
        /// <para>
        /// 🔴 <c>Items</c> 是 <c>InventoryContainer</c> 偏移 0x08 的<b>裸指標</b>，容器存在但還沒載入時是 <c>null</c>；
        /// 而 <c>cont-&gt;Size</c>（偏移 0x14）在 <c>cont</c> 為 <c>null</c> 時<b>不會當場崩</b>，
        /// 是靜默去讀位址 0x14 —— AccessViolationException 在 .NET Core 是 corrupted-state exception，
        /// <c>try/catch</c> 攔不到。
        /// </para>
        /// </remarks>
        internal static bool TryGetContainer(InventoryType type, out InventoryContainer* container)
        {
            container = InventoryManager.Instance()->GetInventoryContainer(type);
            if (container == null || container->Items == null)
            {
                container = null;
                return false;
            }
            return true;
        }

        /// <summary>
        /// 讀取 <paramref name="type"/> 容器第 <paramref name="index"/> 格；讀不到回 <see langword="false"/>。
        /// </summary>
        /// <remarks>
        /// 🔴 除了容器判空，這裡還補上 <c>Size</c> 上界：索引有幾個呼叫端是從別的外掛的 IPC
        /// （Gearsetter 的建議清單）拿來的，<b>不是我們自己算的</b>。越界讀到的是相鄰記憶體
        /// 而不是 null，失敗形式完全靜默 —— 這與艦隊裡那個實機爆過兩千多次的「半套邊界檢查」同形。
        /// </remarks>
        internal static bool TryGetItem(InventoryType type, int index, out InventoryItem item)
        {
            item = default;
            if (!TryGetContainer(type, out InventoryContainer* container)) return false;
            if (index < 0 || index >= container->Size) return false;
            item = container->Items[index];
            return true;
        }

        /// <summary>
        /// 找出 <paramref name="container"/> 裡第一個空格；容器讀不到、或掃完都沒有空格就回 <see langword="false"/>。
        /// </summary>
        /// <remarks>
        /// 🔴 原本回 <c>ushort</c> 並拿 <b>0</b> 當「找不到」，但 0 同時是合法的第 0 格 ——
        /// 三種結果（容器讀不到／沒有空格／第 0 格是空的）壓在同一個值上，呼叫端分不出來。
        /// 現在「找不到」由回傳的 <c>bool</c> 表示，<paramref name="slot"/> 只在回
        /// <see langword="true"/> 時有意義，第 0 格因此能被正常回報。
        /// <para>
        /// fail-closed 的方向不變：容器取不到就當成「這個容器沒有可用空格」，
        /// 讓呼叫端跳過而不是對著讀不到的資料搬東西。
        /// </para>
        /// </remarks>
        internal static bool TryGetFirstAvailableSlot(InventoryType container, out ushort slot)
        {
            slot = 0;
            if (!TryGetContainer(container, out InventoryContainer* cont)) return false;
            for (int i = 0; i < cont->Size; i++)
            {
                if (cont->Items[i].ItemId == 0)
                {
                    slot = (ushort)i;
                    return true;
                }
            }
            return false;
        }

        // 🔴 原本是手寫指標算術的裸讀:*(ushort*)((nint)(AgentStatus.Instance()) + 48)。
        //    它繞過了 ->,所以「有沒有判空」在原始碼上完全看不出來,但危險程度更高:
        //    AgentStatus.Instance() 是 [Agent] 產生器產出的取得子,本體即
        //    「agentModule == null ? null : GetAgentByInternalId(...)」,兩層都能合法回 null,
        //    而 null 時這一行會去讀位址 0x30(0 + 48)—— AccessViolationException,
        //    corrupted-state exception,try/catch 攔不到,遊戲直接被帶走。
        // fail-closed:讀不到就回 0。三個呼叫端對 0 的反應都是「裝備等級不足」——
        //    ContentHelper.cs:173 會把該副本濾掉、MainTab 的 < 370 判斷會成立,
        //    也就是保守地不去報名,而不是在未知狀態下宣稱夠格。
        internal static ushort CurrentItemLevel
        {
            get
            {
                AgentStatus* agentStatus = AgentStatus.Instance();
                if (agentStatus == null)
                    return 0;
                return *(ushort*)((nint)agentStatus + 48);
            }
        }

        /*internal unsafe static uint CurrentItemLevelUI()
        {
            if (GenericHelpers.TryGetAddonByName("Character", out AddonCharacter* addonCharacter) && GenericHelpers.IsAddonReady((AtkUnitBase*)addonCharacter))
            {
                if (addonCharacter->GetTextNodeById(71)->GetAsAtkTextNode()->NodeText.ExtractText().IsNullOrEmpty())
                    return 0;
                var iLvl = Convert.ToUInt32(addonCharacter->GetTextNodeById(71)->GetAsAtkTextNode()->NodeText.ExtractText());
                addonCharacter->Close(true);
                return iLvl;
            }
            else
            {
                if (EzThrottler.Throttle("AgentStatus", 250))
                    AgentStatus.Instance()->Show();
                return 0;
            }
        }*/
        

        /*internal static uint CurrentItemLevelCalc()
        {
            var equipedItems = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);
            uint itemLevelTotal = 0;
            uint itemLevelOfMainHand = 0;
            bool offhandIsEquipped = false;

            for (int i = 0; i < 13; i++)
            {
                var slot = equipedItems->Items[i].Slot;
                var itemId = equipedItems->Items[i].ItemId;
                var item = Svc.Data.GetExcelSheet<Item>()?.FirstOrDefault(item => item.RowId == itemId);
                var itemLevel = item?.LevelItem.Value?.RowId ?? 0;
                var itemName = item?.Name.RawString ?? "";

                if (slot == 0)
                    itemLevelOfMainHand = itemLevel;

                if (slot == 1 && itemId > 0)
                    offhandIsEquipped = true;

                itemLevelTotal += itemLevel;
            }

            if (!offhandIsEquipped)
                itemLevelTotal += itemLevelOfMainHand;

            return itemLevelTotal / 12;
        }*/

        /// <summary>
        /// 找出目前裝備中耐久最低的一件；裝備容器讀不到就回 <see langword="false"/>。
        /// </summary>
        /// <remarks>
        /// 🔑 刻意做成 Try 版而不是「讀不到就回 <c>default</c>」：<c>default(InventoryItem)</c> 的
        /// <c>Condition</c> 是 <b>0</b>，而 <see cref="CanRepair(uint)"/> 的判斷是
        /// <c>Condition / 300f &lt;= percent</c> —— 回 <c>default</c> 等於<b>斷言「裝備全損、該去修了」</b>，
        /// 會讓修理流程照著根本沒讀到的資料動作。呼叫端要分得出「讀不到」與「真的很低」。
        /// <para>
        /// 🔴 迴圈上界原本寫死 13，沒有對 <c>Size</c> 做任何檢查。這裡夾成 <c>min(13, Size)</c>：
        /// 正常情況下裝備容器就是 14 格，行為與原本逐字相同；只有在容器還沒配置滿時才會少讀，
        /// 而那正是原本會讀到相鄰記憶體的情況。
        /// </para>
        /// </remarks>
        internal static bool TryGetLowestEquippedItem(out InventoryItem lowest)
        {
            lowest = default;
            if (!TryGetContainer(InventoryType.EquippedItems, out InventoryContainer* equipedItems)) return false;

            uint itemLowestCondition = 60000;
            uint itemLowest = 0;

            Svc.Log.Verbose("Lowest Equipped Item checks:");

            uint count = (uint)Math.Min(13, equipedItems->Size);
            for (uint i = 0; i < count; i++)
            {
                InventoryItem item = equipedItems->Items[i];
                Svc.Log.Verbose($"{i}: {item.ItemId} {item.Condition}");
                if (itemLowestCondition > item.Condition)
                {
                    Svc.Log.Verbose($"lower");
                    itemLowest = i;
                    itemLowestCondition = item.Condition;
                }
            }

            Svc.Log.Verbose($"lowest Index {itemLowest}");

            if (itemLowest >= (uint)equipedItems->Size) return false;
            lowest = equipedItems->Items[itemLowest];
            return true;
        }

        internal static InventoryItem LowestEquippedItem()
            => TryGetLowestEquippedItem(out InventoryItem lowest) ? lowest : default;

        public static IEnumerable<InventoryItem> GetInventorySelection(params InventoryType[] types)
        {
            IEnumerable<InventoryItem> items = [];
            foreach (InventoryType type in types)
            {
                // 🔴 原本是 `InventoryContainer container = *InventoryManager...GetInventoryContainer(type);`
                //    —— **前置的 `*` 解參考**，取得器合法回 null 時就是直接讀位址 0。
                //    這個形狀特別陰：它沒有 `->`，所以所有以 `->` 為軸的判空掃描都看不到它。
                //    取不到就跳過這個容器，與下面 `IsLoaded == false` 走的是同一條路（行為不變）。
                if (!TryGetContainer(type, out InventoryContainer* containerPtr)) continue;
                InventoryContainer container = *containerPtr;
                if(container.IsLoaded)
                {
                    for (uint i = 0; i < container.Size; i++)
                        items = items.Append(container.Items[i]);
                }
            }
            
            return items.Where(item => item.ItemId > 0);
        }

        internal static bool CanRepair() => CanRepair(Plugin.Configuration.AutoRepairPct);// && (!Plugin.Configuration.AutoRepairSelf || CanRepairItem(LowestEquippedItem().GetItemId()));
        // 🔑 fail-closed 的方向是「這一輪不修」：讀不到裝備容器時回 false，讓下一輪重判，
        //    而不是在未知狀態下宣稱該去修理（那會讓自動化真的跑去修理 NPC、花掉暗物質）。
        //    容器讀得到時的算式與原本逐字相同，既有行為沒有改變。
        internal static bool CanRepair(uint percent) => TryGetLowestEquippedItem(out InventoryItem lowest) && (lowest.Condition / 300f) <= percent;// && (!Plugin.Configuration.AutoRepairSelf || CanRepairItem(LowestEquippedItem().GetItemId()));

        //artisan
        internal static bool CanRepairItem(uint itemID)
        {
            var item = Svc.Data.Excel.GetSheet<Item>()?.GetRow(itemID);

            if (item == null)
                return false;

            if (item.Value.ClassJobRepair.RowId > 0)
            {
                var actualJob = (Job)(item.Value.ClassJobRepair.RowId);
                var repairItem = item.Value.ItemRepair.ValueNullable?.Item;

                if (repairItem == null)
                    return false;

                if (!HasDarkMatterOrBetter(repairItem.Value.RowId))
                    return false;

                var jobLevel = PlayerHelper.GetCurrentLevelFromSheet(actualJob);
                if (Math.Max(item.Value.LevelEquip - 10, 1) <= jobLevel)
                    return true;
            }

            return false;
        }

        //artisan
        internal static bool HasDarkMatterOrBetter(uint darkMatterID)
        {
            var repairResources = Svc.Data.Excel.GetSheet<ItemRepairResource>();
            foreach (var dm in repairResources!)
            {
                if (dm.Item.RowId < darkMatterID)
                    continue;

                if (InventoryManager.Instance()->GetInventoryItemCount(dm.Item.RowId) > 0)
                    return true;
            }
            return false;
        }
    }
}
