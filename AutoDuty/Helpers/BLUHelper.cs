namespace AutoDuty.Helpers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using ECommons.DalamudServices;
    using FFXIVClientStructs.FFXIV.Client.Game;
    using FFXIVClientStructs.FFXIV.Client.Game.UI;
    using Lumina.Excel;
    using Lumina.Excel.Sheets;

    /// <summary>
    ///     青魔道士的技能欄位操作（把某個「青魔法書」編號的魔法換進／換出當前配置）。
    ///     路徑檔的 <c>BLULoad</c> 步驟會用到。
    ///     <para>
    ///     ⚠️ 這裡完全不施放技能,只做技能欄位的配置替換 —— 與遊戲內「青魔法書」介面上
    ///     手動拖曳的效果相同。
    ///     </para>
    ///     <para>
    ///     🔴 初始化刻意做成惰性 + 例外隔離:AozAction / AozActionTransient 兩張表在
    ///     台服的內容未經實機驗證,若讀表出問題只讓 BLU 相關步驟變成「什麼都不做」,
    ///     不能讓靜態建構子丟 TypeInitializationException 把整個外掛拖下水。
    ///     </para>
    /// </summary>
    internal static class BLUHelper
    {
        internal record BLUSpell(uint ID, byte Entry, string Name, uint Unlock, uint ActionId);

        private static readonly Dictionary<uint, BLUSpell> spellsById    = [];
        private static readonly Dictionary<byte, BLUSpell> spellsByEntry = [];

        private static ExcelSheet<AozAction>? aozActions;

        private static bool initialized;
        private static bool initFailed;

        private static bool EnsureInitialized()
        {
            if (initialized)
                return true;
            if (initFailed)
                return false;

            try
            {
                aozActions = Svc.Data.GetExcelSheet<AozAction>();
                ExcelSheet<AozActionTransient> aozActionsData = Svc.Data.GetExcelSheet<AozActionTransient>();

                foreach (AozAction aozAction in aozActions)
                {
                    if (aozAction.Rank == 0)
                        continue;

                    if (!aozActionsData.TryGetRow(aozAction.RowId, out AozActionTransient transient) || transient.Number == 0)
                        continue;

                    if (aozAction.Action.ValueNullable is not { } action)
                        continue;

                    BLUSpell spell = new(aozAction.RowId, transient.Number, action.Name.ToString(), action.UnlockLink.RowId, action.RowId);
                    spellsById[spell.ID]       = spell;
                    spellsByEntry[spell.Entry] = spell;
                }

                Svc.Log.Information($"[BLUHelper] 青魔法對照表建立完成:{spellsById.Count} 個技能");
                initialized = spellsById.Count > 0;
                initFailed  = !initialized;
                return initialized;
            }
            catch (Exception ex)
            {
                // 台服資料表對不上時只讓 BLU 步驟變成 no-op,不要往外拋。
                Svc.Log.Information($"[BLUHelper] 無法建立青魔法對照表,BLULoad 步驟將不執行:{ex}");
                initFailed = true;
                return false;
            }
        }

        private static bool TryNormalToAoz(uint actionId, out BLUSpell? spell)
        {
            spell = null;
            if (actionId == 0 || aozActions == null)
                return false;

            // 上游這裡是 spellsById[NormalToAoz(u)],查不到會丟 KeyNotFoundException。
            // 改成走已建好的字典做反查,查不到就回 false。
            spell = spellsById.Values.FirstOrDefault(s => s.ActionId == actionId);
            return spell != null;
        }

        private static unsafe bool SpellUnlocked(BLUSpell spell) =>
            UIState.Instance()->IsUnlockLinkUnlockedOrQuestCompleted(spell.Unlock);

        /// <summary>讀出目前 24 個青魔法欄位的內容,空欄位為 null。</summary>
        private static unsafe List<BLUSpell?> GetCurrentBluSpells()
        {
            List<BLUSpell?> spellList = [];

            foreach (uint actionId in ActionManager.Instance()->BlueMageActions)
                spellList.Add(TryNormalToAoz(actionId, out BLUSpell? spell) ? spell : null);

            return spellList;
        }

        /// <summary>把青魔法書編號 <paramref name="entry"/> 的魔法從當前配置移除。</summary>
        public static unsafe void SpellLoadoutOut(byte entry)
        {
            if (!EnsureInitialized())
                return;

            List<BLUSpell?> bluSpells = GetCurrentBluSpells();
            int             index     = bluSpells.FindIndex(sp => sp?.Entry == entry);

            Svc.Log.Debug($"[BLUHelper] 移除青魔法書 #{entry},欄位索引 {index}");

            if (index != -1)
                ActionManager.Instance()->AssignBlueMageActionToSlot(index, 0);
        }

        /// <summary>把青魔法書編號 <paramref name="entry"/> 的魔法放進第一個空欄位。</summary>
        public static unsafe void SpellLoadoutIn(byte entry)
        {
            if (!EnsureInitialized())
                return;

            List<BLUSpell?> bluSpells = GetCurrentBluSpells();

            if (bluSpells.Any(sp => sp?.Entry == entry))
            {
                Svc.Log.Debug($"[BLUHelper] 青魔法書 #{entry} 已在配置中,略過");
                return;
            }

            int index = bluSpells.FindIndex(sp => sp == null);
            if (index == -1)
            {
                Svc.Log.Debug($"[BLUHelper] 沒有空欄位可以放入青魔法書 #{entry}");
                return;
            }

            if (!spellsByEntry.TryGetValue(entry, out BLUSpell? bluSpell))
            {
                Svc.Log.Debug($"[BLUHelper] 找不到青魔法書 #{entry} 對應的魔法");
                return;
            }

            if (!SpellUnlocked(bluSpell))
            {
                Svc.Log.Debug($"[BLUHelper] 青魔法書 #{entry}({bluSpell.Name})尚未習得,不放入");
                return;
            }

            Svc.Log.Debug($"[BLUHelper] 把 {bluSpell.Name} 放入欄位索引 {index}");
            ActionManager.Instance()->AssignBlueMageActionToSlot(index, bluSpell.ActionId);
        }
    }
}
