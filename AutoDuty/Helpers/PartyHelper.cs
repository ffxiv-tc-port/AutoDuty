namespace AutoDuty.Helpers
{
    using System.Linq;
    using ECommons.Automation;
    using ECommons.PartyFunctions;

    /// <summary>
    /// 上游 erdelf/AutoDuty 的 PartyHelper 最小子集。
    ///
    /// 📌 只搬 Multibox 真正會用到的兩個成員。上游原檔另有 PartyInCombat()/PartyDead()/
    /// GetPartyMembers()/PartyMember1~8,那些在本 fork 沒有任何呼叫端,而且會透過
    /// IBattleChara.Struct()-&gt;IsDead() 做原生解參考 —— 搬進來等於憑空多出一批
    /// 沒人用、卻可能在跨幀情境下解到已釋放物件的程式碼。要用到時再補。
    /// </summary>
    public static class PartyHelper
    {
        public static bool IsPartyMember(ulong? cid)
        {
            if (cid == null || !PlayerHelper.IsReady)
                return false;

            return UniversalParty.Members.Any(upm => upm.ContentID == cid);
        }

        public static void LeaveParty() =>
            Chat.ExecuteCommand("/partycmd leave");
    }
}
