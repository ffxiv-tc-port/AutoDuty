using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace AutoDuty.Helpers
{
    /// <summary>
    /// 「同一扇視窗的同一個按法,按過就不要再按,直到它真的收掉」的共用閘門。
    /// </summary>
    /// <remarks>
    /// 🔴🔴 <b>存在的唯一理由是原生 AccessViolation</b>:<c>SelectYesno</c> 這類確認框被按下之後
    /// 有<b>「正在關閉中」的幾幀</b>,這段期間 <c>TryGetAddonByName</c> 仍然回得到實例、
    /// <c>IsVisible</c> 與 <c>UldManager.LoadedState == Loaded</c> 也都還成立 ——
    /// 也就是說 <c>GenericHelpers.IsAddonReady</c> <b>三關全過、擋不住這個窗口</b>。
    /// 此時再對它 <c>FireCallback</c>／送 <c>ReceiveEvent</c> 就是原生 AccessViolationException。
    /// AVE 在 .NET Core 是 corrupted-state exception,<c>try</c>/<c>catch</c> 完全攔不到,
    /// 遊戲當場關閉 —— <b>唯一的防護是「不要送第二次」,不是「送了再接住」</b>。
    /// <para>
    /// ⚠️ 呼叫端原有的 <c>EzThrottler.Throttle(..., 250)</c> <b>不是</b>防護:
    /// 它記的是「上一次動作在哪個時刻」而不是「這扇窗已經按過」,
    /// <b>第一次呼叫必定放行</b>,而且每個呼叫點各用各的 key —— 兩個呼叫點接力按同一扇窗時
    /// 兩邊的節流都會放行。<c>ExitDutyHelper</c> 更是連節流都沒有(掛在
    /// <c>Svc.Framework.Update</c> 上每幀執行)。
    /// </para>
    /// <para>
    /// 🔑 <b>做法</b>:按下之前先登記「哪個名字的哪一個實例(窗名＋位址)、被送過哪一種 callback」,
    /// 在觀察到那扇窗真的走完生命週期之前不准再送同一種。同名兩扇並存時各記各的。
    /// 🔴 全程只做<b>位址等值比較,永遠不解參</b> —— 被記下的那個位址隨時可能已經失效。
    /// </para>
    /// <para>
    /// 📌 <b>為什麼 key 要含「按法」而不是只看實例位址</b>:AutoDuty 有多處<b>刻意</b>在同一扇
    /// 還開著的窗上連送不同的 callback(<c>TrustHelper</c> 在同一幀對 Dawn 送
    /// <c>(true, 16, id)</c> 逐一切換隊員讀等級、<c>DesynthHelper</c> 對 SalvageItemSelector
    /// 逐件送 <c>(true, 12, i)</c>、<c>QueueHelper</c> 對 ContentsFinder 送 12/3/12)。
    /// 只看位址會把這些正常流程一起擋掉(TrustHelper 那個更會<b>靜默讀到同一個等級</b>),
    /// 所以擋的粒度是「同一扇窗 ＋ 同一組參數」= 真正的「重按」。
    /// </para>
    /// <para>
    /// <b>解除點</b>:登記時在同名清單裡找得到、之後從清單消失(每個 framework tick 輪詢);同位址 PostSetup
    /// (這一幀才登記的不清);常駐窗連續隱藏 <see cref="HiddenReleaseFrames"/> 幀;子視窗收掉(見 <c>opensDialog</c>);逃生口。
    /// PreFinalize 是<b>上鎖點</b>:鎖到位址消失或同位址重建。
    /// ⚠️ 刻意<b>不</b>把 <c>PostRefresh</c> 也當解除點:它有可能在「關閉中」那幾幀觸發。
    /// </para>
    /// <para>
    /// 🔴 <b>逃生口是刻意的</b>(<see cref="DefaultEscapeFrames"/>):萬一某扇窗既不 finalize
    /// 也不重新 setup(例如上一次的 callback 根本沒生效、視窗就是還開著),
    /// 沒有逃生口的話呼叫端會<b>永遠</b>按不下去,等於把崩潰換成靜默失效。
    /// 用<b>幀數</b>而不是毫秒:危險窗口的長度本來就是以幀計的,遊戲卡頓時兩者一起拉長。
    /// </para>
    /// <para>
    /// 📌 第一次看到某扇窗的某個按法一律當場按下去;
    /// 被擋下時各呼叫端本來就是輪詢重試(回傳值語意也沒動),下個 tick 再來。任何不確定一律不放行。
    /// </para>
    /// <para>⚠️ 只在主執行緒使用(與呼叫端的 <c>EzThrottler</c> 同一個前提)。</para>
    /// </remarks>
    internal static unsafe class AddonPressGuard
    {
        /// <summary>
        /// 已經按過、那扇窗卻既沒消失也沒重建時,最多再等這麼多幀才允許補按一次。
        /// </summary>
        /// <remarks>
        /// 🔑 這不是節流 —— 真正的防護是「同一扇窗的同一個按法只按一次」,這個值只是防死鎖的逃生口。
        /// 90 幀(60fps 下約 1.5 秒)遠遠大於「關閉中的那幾幀」,補按永遠不會落在危險窗口內。
        /// </remarks>
        internal const int DefaultEscapeFrames = 90;

        /// <summary>
        /// 給「按一次翻一頁、窗不會因為被按而消失」的多次互動窗(<c>Talk</c> 是代表)用的短逃生口(15 幀)。
        /// </summary>
        /// <remarks>
        /// 對話是一頁一頁推的,那扇窗整段都不關也不重建,輪詢與生命週期兩條解除點都不會觸發,
        /// 走逃生口是<b>常態</b>而不是異常 —— 所以放行 log 寫 Debug 不洗版。
        /// 關閉中的危險窗口 &lt; 10 幀,15 幀不落在裡面;每頁多等 0.25 秒幾乎無感。
        /// ⚠️ 刻意<b>不</b>用「文字變了」當翻頁證據:關閉中的窗文字會讀壞(U+FFFD)。
        /// </remarks>
        internal const int RoutineRePressEscapeFrames = 15;

        /// <summary>
        /// <see cref="TryBeginClose"/> 登記用的按法名。<b>它是萬用鍵</b>:對某扇窗送過 <c>Close(true)</c> 之後、
        /// 還沒觀察到它收掉之前,<see cref="TryBeginPress"/> 對同一位址的<b>任何</b>按法都會被擋。
        /// </summary>
        internal const string ClosePressKey = "Close";

        /// <summary>輪詢同名實例的上限(遊戲同名實例上限);掃到第一個空的就提早停。</summary>
        private const int MaxAddonIndex = 256;

        /// <summary>常駐窗按下後,要<b>連續</b>觀察到「還在清單裡、但已隱藏」這麼多幀才解除。</summary>
        /// <remarks>
        /// 🔴 這個數字不承重:真正的防護是 <see cref="TryBeginPress"/> 對常駐窗多要求的「送出前必須可見」,
        /// 它與這裡的解除條件(連續不可見)互為邏輯反面 ⇒ 解除之後要再送,中間必須有一次遊戲自己把窗重新 Show 起來,
        /// 而正在拆除的窗不會被重新 Show。N 只決定要等多久。
        /// </remarks>
        internal const int HiddenReleaseFrames = 20;

        /// <summary>每隔幾幀回收一次「已經不可能再擋住任何人」的冷紀錄。</summary>
        private const int SweepIntervalFrames = 60;

        /// <summary>冷紀錄回收的年齡下限(幀),遠大於任何逃生口:回收不改變任何一次「按／不按」的判定。</summary>
        private const int SweepMinAgeFrames = 1800;

        /// <summary>
        /// 「一扇窗一生只回答一次」的視窗:這些名字底下的按法一律併成同一個 key。
        /// </summary>
        /// <remarks>
        /// 🔴 這一組是<b>必要的</b>,不是保守起見:同一扇確認框在 AutoDuty 裡會被<b>兩種機制</b>按到 ——
        /// <c>AddonHelper.FireCallBack</c>(送 callback)與 <c>AddonMaster.SelectYesno.Yes()</c>
        /// (直接送 <c>ReceiveEvent</c>);<c>SquadronManager</c> 更是同一幀對同一扇 SelectString
        /// 先 <c>FireCallBack(true, 0)</c> 再 <c>ClickSelectString(0)</c>。
        /// 這些按法的參數字串本來各不相同,不併 key 就會出現「兩條路徑接力按同一扇關閉中的窗」。
        /// <para>
        /// ⚠️ 只放<b>回答一次就結束</b>的窗。像 ContentsFinder／Dawn／Materialize／SalvageItemSelector
        /// 這種「窗一直開著、刻意連送不同 callback」的<b>絕對不能</b>放進來 ——
        /// 那會把正常流程一起擋掉(例如 <c>TrustHelper</c> 逐一切換隊員讀等級會靜默讀到同一個值)。
        /// </para>
        /// <para>
        /// 📌 <c>SelectString</c>／<c>SelectIconString</c> 選一項就關:本 repo 每個呼叫點對一扇選單只選一項,
        /// 下一層選單都是重新互動開出來的新實例。真的遇到同實例換內容時,只會等到逃生口(慢,不會崩)。
        /// </para>
        /// </remarks>
        private static readonly HashSet<string> SingleAnswerAddons = new(StringComparer.Ordinal)
        {
            "SelectYesno",
            "SelectString",
            "SelectIconString",
            "DifficultySelectYesNo",
            "ContentsFinderConfirm",
            "MaterializeDialog",
            "SalvageDialog",
            "JournalResult",
        };

        /// <summary>
        /// 已經<b>實機證實</b>是「常駐」的視窗名:關閉只是被設成不可見,位址永遠留在清單裡,
        /// PreFinalize 與 PostSetup 都不會再發生。只有這些窗名套用「連續隱藏才解除」。
        /// </summary>
        /// <remarks>
        /// 🔴 <b>這份名單同時是「送出前必須可見」的名單</b>(與「連續隱藏才解除」互為反面)。
        /// 把會被銷毀的窗誤收進來,等於在它拆除途中提早解除封鎖。
        /// </remarks>
        private static readonly HashSet<string> PersistentAddons = new(StringComparer.Ordinal)
        {
            "ContextMenu",
            "ContextIconMenu",
            "Talk",
        };

        /// <summary>紀錄身分:同名實例各自一筆。位址只做等值比較。</summary>
        private readonly record struct PressSlot(string AddonName, nint Address);

        /// <param name="Frame">按下時的 framework tick 序號(<see cref="CurrentFrame"/>,<b>刻意不是繪製幀</b>)。</param>
        /// <param name="EscapeFrames">登記當時呼叫端給的逃生口;<see cref="TryBeginClose"/> 判「這筆還熱著」用它。</param>
        private readonly record struct PressRecord(long Frame, int EscapeFrames);

        /// <summary>一扇窗(窗名＋位址)底下的全部按法與解除狀態。刻意是 class:欄位要就地改。</summary>
        private sealed class SlotRecords
        {
            public Dictionary<string, PressRecord> ByKey { get; } = new(StringComparer.Ordinal);

            public long LastFrame;

            /// <summary>連續觀察到「還在清單裡、但已隱藏」的幀數;只有常駐窗會累加,看得見或重新按下就歸零。</summary>
            public int HiddenFrames;

            /// <summary>登記時在同名清單裡找得到這個位址;只有它成立才准用「從清單消失」解除。</summary>
            public bool Listed;

            /// <summary>看過 PreFinalize:在位址消失或同位址重建之前,任何按法都不准,也沒有逃生口。</summary>
            public bool Finalizing;
        }

        private static readonly Dictionary<PressSlot, SlotRecords> Pressed = new();

        /// <summary>
        /// 「子視窗收掉時要提早解除的那一筆父視窗按下紀錄」。
        /// </summary>
        /// <param name="ParentAddonName">父視窗名稱。</param>
        /// <param name="ParentAddress">父視窗那個實例的位址,<b>只做等值比較,永遠不解參</b>。</param>
        /// <param name="ParentPressKey">
        /// 父視窗上的<b>那一個</b>按法。只解除這一個 —— 尤其<b>不會</b>碰到同位址的
        /// <see cref="ClosePressKey"/>(那是「已經對這扇窗送過 <c>Close(true)</c>」的鎖)。
        /// </param>
        /// <param name="RegisteredFrame">登記的那一幀;只認在它<b>之後</b>才收掉的子視窗。</param>
        private readonly record struct ChildDialogRelease(string ParentAddonName, nint ParentAddress, string ParentPressKey, long RegisteredFrame);

        /// <summary>子視窗名 → 「等它收掉就提早解除」的那一筆父視窗按下紀錄。</summary>
        /// <remarks>
        /// 🔑 <b>這是正面證據,不是計時器</b>:子視窗能被建立起來又走完生命週期,就代表父視窗
        /// 在收下那一發 callback 的當下是活著的、而且那一發<b>確實生效了</b> ——
        /// 這正好否定「上一發把父視窗按關了、現在正在關閉中」這個守衛要防的情境。
        /// <para>
        /// ⚠️ 這是守衛裡<b>唯一一條跨視窗耦合</b>。父子關係由呼叫端在按下時逐次宣告
        /// (<c>AddonHelper.TryFireCallBackOpeningDialog</c>),守衛本身<b>不寫死任何視窗名稱</b>;
        /// 宣告錯的後果是提早解除永遠不發生 ＝<b>靜默退回 90 幀</b>(慢,不會崩)。
        /// </para>
        /// </remarks>
        private static readonly Dictionary<string, ChildDialogRelease> PendingChildReleases =
            new(StringComparer.Ordinal);

        /// <summary>這一幀看到 PreFinalize 的子視窗名與幀號;下一次輪詢才拿去解除父視窗(PreFinalize 處理常式只上鎖)。</summary>
        private static readonly List<(string Child, long Frame)> FinalizedChildrenBuf = [];

        private static readonly Dictionary<string, IAddonLifecycle.AddonEventDelegate> Watchers =
            new(StringComparer.Ordinal);

        private static readonly List<PressSlot> ReleaseBuf = [];
        private static readonly List<string> StaleKeysBuf = [];

        /// <summary>「這個窗名第一次走隱藏解除」只寫一行 Information,之後不再寫。</summary>
        private static readonly HashSet<string> HiddenReleaseReported = new(StringComparer.Ordinal);

        /// <summary>
        /// 守衛自己的幀時鐘:由 <see cref="IFramework.Update"/> 每個 tick 推進一次。
        /// </summary>
        /// <remarks>
        /// 🔴🔴 <b>刻意不用 <c>Svc.PluginInterface.UiBuilder.FrameCount</c></b>:那個計數器是加在
        /// <c>UiBuilder.OnDraw()</c> 的<b>最後面</b>,而 <c>OnDraw</c> 在「外掛 UI 被隱藏」時會<b>提早 return</b>,
        /// 根本走不到遞增那一行。三種隱藏條件裡有一條是<b>過場動畫</b>
        /// (<c>ToggleUiHideDuringCutscenes</c>,Dalamud 端<b>預設開啟</b>,而本外掛沒有設 <c>DisableCutsceneUiHide</c>)
        /// —— 也就是說<b>整段過場期間那個計數器完全不動</b>。
        /// <para>
        /// 🔴 對 AutoDuty 特別致命:副本流程<b>大量伴隨過場</b>(進本、Boss 前、劇情本)。
        /// 用繪製幀當時鐘的話,所有逃生口(<see cref="RoutineRePressEscapeFrames"/> 15 幀、
        /// <see cref="DefaultEscapeFrames"/> 90 幀,以及 <c>ExitDutyHelper</c> 隔 30 幀回頭補關窗的觀察期)
        /// 在過場期間<b>永遠不會到期</b> —— 防死鎖用的逃生口自己死鎖,
        /// 而 <c>ExitDutyHelper</c> 那個「隔幾幀之後才補送 <c>(false, -2)</c>」的關窗動作會<b>永遠不發生</b>。
        /// </para>
        /// <para>
        /// <see cref="IFramework.Update"/> 是掛在遊戲自己的 update hook 上的,與繪製、與 UI 隱藏都無關,
        /// 過場中照樣每個 tick 進來一次。速率與繪製幀實質相同(遊戲每一幀跑一次 update),
        /// 所以 15／30／90 這些既有幀數<b>不需要跟著調整</b>,只是換了一個不會停的來源。
        /// </para>
        /// </remarks>
        private static ulong frameTicks;

        /// <summary>0 ＝時鐘還沒掛上,1 ＝已掛上;用 CAS 交換,保證<b>只會訂閱一次</b>。</summary>
        /// <remarks>
        /// ⚠️ 重複訂閱不是「沒效果」,而是計數器<b>一個 tick 前進 2</b> ＝ 所有逃生口<b>對半砍</b>,
        /// 那會把補按往危險窗口推 —— 所以這裡用 <see cref="Interlocked"/> 而不是單純的 bool。
        /// </remarks>
        private static int frameClockState;

        /// <summary>現在是第幾個 framework tick。<b>讀它就會順便把時鐘掛起來</b>(冪等)。</summary>
        /// <remarks>
        /// 🔴 掛時鐘刻意<b>不</b>放在 <see cref="EnsureWatching"/> 裡:那支只有在「真的要按某個 addon」時才被呼叫,
        /// 而 <see cref="IsHeld"/> 與 <c>ExitDutyHelper</c> 在還沒有任何按下紀錄時就會先讀時鐘。
        /// 把掛載放在<b>讀取點</b>上,就不會留下「某條路徑忘了啟動時鐘」的縫。
        /// </remarks>
        internal static long CurrentFrame
        {
            get
            {
                EnsureFrameClock();
                return (long)frameTicks;
            }
        }

        /// <summary>掛上幀時鐘(冪等)。</summary>
        private static void EnsureFrameClock()
        {
            if (Volatile.Read(ref frameClockState) != 0) return;
            if (Interlocked.CompareExchange(ref frameClockState, 1, 0) != 0) return;

            Svc.Framework.Update += OnFrameworkUpdate;
        }

        /// <summary>
        /// 🔴 <b>遞增必須是第一行、前面不准有任何條件</b>:一旦讓遞增變成有條件的,時鐘就會在某些狀態下停住
        /// (繪製幀計數器就是被 <c>OnDraw</c> 的提早 return 擋在遞增之前,才不能拿來當時鐘)。
        /// </summary>
        private static void OnFrameworkUpdate(IFramework framework)
        {
            frameTicks++;
            ProcessFinalizedChildren();
            PollRecords();
            if (frameTicks % SweepIntervalFrames == 0) SweepColdRecords();
        }

        /// <summary>
        /// 登記「即將對這扇視窗送出這一個 callback」。<b>回 <see langword="false"/> ＝這一幀絕對不能送。</b>
        /// </summary>
        /// <param name="addonName">視窗名稱(解除封鎖的監聽器與輪詢都以它為準)。空字串一律不放行。</param>
        /// <param name="addon">目標實例。<b>只當作識別用的位址,本方法不解參。</b></param>
        /// <param name="pressKey">
        /// 這一次的「按法」。同一扇窗上不同的按法互不干擾;要擋的是<b>同一個按法重複送</b>。
        /// 傳空字串代表「整扇窗只有一種按法」。
        /// </param>
        /// <param name="escapeFrames">逃生口幀數,見 <see cref="DefaultEscapeFrames"/>。</param>
        /// <param name="opensDialog">
        /// 選填:這一發<b>成功的話</b>會開出來的那扇子視窗名稱。給了它,那扇子視窗
        /// <see cref="AddonEvent.PreFinalize"/> 之後本筆紀錄會被<b>提早解除</b>
        /// (見 <see cref="ReleaseParentOfClosedDialog"/>) —— 這是給「按一次開一扇子視窗、
        /// 自己卻不關也不重建」的常駐清單窗用的,讓它不必為了跑得動而縮短逃生口。
        /// </param>
        /// <remarks>
        /// 呼叫點要放在<b>緊接著送出動作之前</b> —— 這支一回 <see langword="true"/> 就已經把
        /// 「按過了」記下去,登記完卻不按的話會白白封鎖到逃生口為止。
        /// </remarks>
        internal static bool TryBeginPress(string addonName, AtkUnitBase* addon, string pressKey = "",
                                           int escapeFrames = DefaultEscapeFrames, string? opensDialog = null)
        {
            if (addon == null || string.IsNullOrEmpty(addonName)) return false;

            // 回答一次就結束的窗:不管是哪一條路徑、送的是什麼參數,一律算同一次按。
            if (SingleAnswerAddons.Contains(addonName))
                pressKey = string.Empty;

            nint address = (nint)addon;
            // 🔴 常駐窗送出前必須可見:與「連續隱藏才解除」互為反面,兩者一組才是防護。
            if (PersistentAddons.Contains(addonName) && !PersistentAddonVisible(addonName, address))
            {
                LogPersistentHidden(addonName, address);
                return false;
            }

            EnsureWatching(addonName);

            long frame = CurrentFrame;
            var  slot  = new PressSlot(addonName, address);

            if (Pressed.TryGetValue(slot, out SlotRecords? records))
            {
                if (records.Finalizing)
                {
                    LogHold(addonName, address, pressKey + "←PreFinalize");
                    return false;
                }

                if (records.ByKey.TryGetValue(pressKey, out PressRecord pressed))
                {
                    long waited = frame - pressed.Frame;
                    if (waited < escapeFrames)
                    {
                        // 🔴 這就是崩潰的那一幀。
                        LogHold(addonName, address, pressKey);
                        return false;
                    }

                    // Talk 類的多次互動窗走逃生口是常態(每一頁都會走到),寫 Debug 不洗版;
                    // 單答終結窗走到這裡才是異常,寫 Information(要使用者回報的診斷)。
                    if (escapeFrames <= RoutineRePressEscapeFrames)
                    {
                        if (EzThrottler.Throttle($"AddonPressGuard-RoutineRelease-{addonName}", 10000))
                            Svc.Log.Debug($"[AddonPressGuard] 「{addonName}」(實例 0x{address:X},按法「{pressKey}」)" +
                                          $"按下後 {waited} 幀窗還在(多次互動窗的常態),放行下一次。");
                    }
                    else if (EzThrottler.Throttle($"AddonPressGuard-Release-{addonName}", 10000))
                    {
                        Svc.Log.Information($"[AddonPressGuard] 「{addonName}」(實例 0x{address:X},按法「{pressKey}」)" +
                                            $"按下後 {waited} 幀既沒有被銷毀也沒有重新建立,判定為「上一次按下沒生效」" +
                                            "而不是「正在關閉」,解除封鎖讓呼叫端重試。");
                    }
                }

                // 🔴 Close 是萬用鍵:對這扇窗送過 Close(true) 之後、還沒觀察到它收掉之前,任何按法都不准 ——
                //    Close(true) 會走 callback,那扇窗這時候多半已經在關閉流程裡。
                if (IsCloseHot(records, frame))
                {
                    LogHold(addonName, address, pressKey);
                    return false;
                }
            }
            else
            {
                records       = new SlotRecords();
                Pressed[slot] = records;
            }

            Register(slot, records, pressKey, frame, escapeFrames);
            // 跨外掛重按診斷:全艦隊每個外掛各有一份本守衛,只擋得住自己按過的位址。
            // 格式必須與其他外掛逐字一致才能交叉比對;不節流(尖峰每分鐘數十次)、不解參考位址。
            Svc.Log.Verbose($"[按窗診斷] plugin=AutoDuty addon={addonName} addr=0x{address:X} key={pressKey}");

            // 呼叫端宣告「這一發成功的話會開出一扇子視窗」⇒ 登記「那扇子視窗收掉時提早解除本筆」。
            // 🔴 監聽器在「按下的這一刻」就掛上:RegisterListener 要下一個 tick 才真的進監聽清單,
            //    晚掛就可能整個錯過子視窗的 PreFinalize,提早解除永遠不會發生(退回 90 幀,不會崩)。
            if (!string.IsNullOrEmpty(opensDialog))
            {
                EnsureWatching(opensDialog);
                PendingChildReleases[opensDialog] = new ChildDialogRelease(addonName, address, pressKey, frame);
            }

            return true;
        }

        /// <summary>
        /// 只<b>看</b>不登記:這扇視窗的這一個按法現在是不是被擋著。
        /// </summary>
        /// <remarks>
        /// 給「按之前要先讀窗上的文字來決定按哪個鈕」的呼叫端用(<c>MultiboxUtility</c> 讀 SelectYesno 的提示),
        /// 順序是 <see cref="IsHeld"/> → 讀文字 → <see cref="TryBeginPress"/> → 按:
        /// 被擋的那幾幀連文字都不去讀(那正是視窗記憶體變動中的幾幀),
        /// 而讀完決定不按時也不會留下一筆「登記了卻沒按」的紀錄白白封鎖到逃生口。
        /// 判準與 <see cref="TryBeginPress"/> 完全相同,逃生口用登記當時存下來的那個值。
        /// <para>⚠️ 回 <see langword="true"/> ＝ 這一幀不要碰。<paramref name="addon"/> 為 null 也算不要碰。</para>
        /// </remarks>
        internal static bool IsHeld(string addonName, AtkUnitBase* addon, string pressKey = "")
        {
            if (addon == null || string.IsNullOrEmpty(addonName)) return true;

            if (SingleAnswerAddons.Contains(addonName))
                pressKey = string.Empty;

            nint address = (nint)addon;
            if (!Pressed.TryGetValue(new PressSlot(addonName, address), out SlotRecords? records))
                return false;

            long frame = CurrentFrame;

            bool held = records.Finalizing
                        || (records.ByKey.TryGetValue(pressKey, out PressRecord pressed)
                            && frame - pressed.Frame < pressed.EscapeFrames)
                        || IsCloseHot(records, frame);

            if (held)
                LogHold(addonName, address, pressKey);

            return held;
        }

        /// <summary>
        /// 登記「即將對這扇視窗呼叫 <c>AtkUnitBase.Close(true)</c>」。<b>回 <see langword="false"/> ＝這一幀不要關它。</b>
        /// </summary>
        /// <remarks>
        /// <c>Close(true)</c> 的 <c>true</c> 就是 fireCallback:它<b>也會對那扇窗送 callback</b>,
        /// 所以跟 <see cref="TryBeginPress"/> 擋的是同一種存取違規。差別在判準:
        /// <para>
        /// 這扇窗(同位址)<b>任何</b>按法只要還在它自己的逃生口內,就不准關 ——
        /// 按下去的那一發本來就可能正在把窗關掉,這時候補一發 Close 正好落在危險窗口裡
        /// (<c>ActiveHelperBase.HelperStopUpdate</c> 每幀對 <c>AddonsToClose</c> 清單 <c>Close(true)</c>,
        /// 而它前一幀才剛透過 <c>ClickSelectYesno</c>/<c>ClickTalk</c> 按過同一扇窗)。
        /// 登記在 <see cref="ClosePressKey"/> 底下,之後對同位址的任何按法都會被 <see cref="TryBeginPress"/> 擋到它收掉為止。
        /// </para>
        /// <para>被擋的呼叫端一律照原本的「還沒關完,下一幀再來」路徑走(<c>CloseAddons</c> 回 false),控制流不變。</para>
        /// </remarks>
        internal static bool TryBeginClose(string addonName, AtkUnitBase* addon, int escapeFrames = DefaultEscapeFrames)
        {
            if (addon == null || string.IsNullOrEmpty(addonName)) return false;

            nint address = (nint)addon;
            if (PersistentAddons.Contains(addonName) && !PersistentAddonVisible(addonName, address))
            {
                LogPersistentHidden(addonName, address);
                return false;
            }

            EnsureWatching(addonName);

            long frame = CurrentFrame;
            var  slot  = new PressSlot(addonName, address);

            if (Pressed.TryGetValue(slot, out SlotRecords? records))
            {
                if (records.Finalizing)
                {
                    LogHold(addonName, address, ClosePressKey + "←PreFinalize");
                    return false;
                }

                foreach ((string pressKey, PressRecord pressed) in records.ByKey)
                {
                    long waited = frame - pressed.Frame;
                    if (waited < pressed.EscapeFrames)
                    {
                        LogHold(addonName, address, ClosePressKey + "←" + pressKey);
                        return false;
                    }
                }

                if (records.ByKey.TryGetValue(ClosePressKey, out PressRecord closed)
                    && EzThrottler.Throttle($"AddonPressGuard-Release-{addonName}", 10000))
                {
                    Svc.Log.Information($"[AddonPressGuard] 「{addonName}」(實例 0x{address:X})Close(true) 之後 {frame - closed.Frame} 幀" +
                                        "既沒有被銷毀也沒有重新建立,判定為「上一次沒關成」,解除封鎖讓呼叫端再關一次。");
                }
            }
            else
            {
                records       = new SlotRecords();
                Pressed[slot] = records;
            }

            Register(slot, records, ClosePressKey, frame, escapeFrames);
            // 跨外掛重按診斷:全艦隊每個外掛各有一份本守衛,只擋得住自己按過的位址。
            // 格式必須與其他外掛逐字一致才能交叉比對;不節流(尖峰每分鐘數十次)、不解參考位址。
            Svc.Log.Verbose($"[按窗診斷] plugin=AutoDuty addon={addonName} addr=0x{address:X} key={ClosePressKey}");
            return true;
        }

        /// <summary>
        /// 讀窗上的文字來做判定的站,讀到 U+FFFD 就代表視窗記憶體正在變動(多半是關閉中),<b>這一幀不碰</b>。
        /// </summary>
        /// <returns><see langword="true"/> ＝ 文字讀壞了,呼叫端這一幀什麼都不要做。</returns>
        /// <remarks>
        /// 這是崩潰的旁證而不是防護本體(防護是 <see cref="TryBeginPress"/>):實機崩潰前 log 裡的 prompt 就是這種亂碼。
        /// 寫 Information 讓使用者回報時看得到。
        /// </remarks>
        internal static bool IsTextCorrupt(string addonName, string? text)
        {
            if (string.IsNullOrEmpty(text) || !text.Contains('�')) return false;

            if (EzThrottler.Throttle($"AddonPressGuard-Corrupt-{addonName}", 1000))
                Svc.Log.Information($"[AddonPressGuard] 「{addonName}」的文字讀到 U+FFFD 亂碼(視窗記憶體正在變動,多半是關閉中),這一幀不碰它。");

            return true;
        }

        private static void Register(PressSlot slot, SlotRecords records, string pressKey, long frame, int escapeFrames)
        {
            if (!records.Listed) records.Listed = IsListed(slot);
            records.ByKey[pressKey] = new PressRecord(frame, escapeFrames);
            records.LastFrame       = frame;
            // 🔴 承重:隱藏解除是「從最後一次按下起算」連續隱藏幾幀。
            records.HiddenFrames = 0;
        }

        /// <summary>同位址的 <see cref="ClosePressKey"/> 紀錄還在它的逃生口內。</summary>
        private static bool IsCloseHot(SlotRecords records, long frame)
            => records.ByKey.TryGetValue(ClosePressKey, out PressRecord closed)
               && frame - closed.Frame < closed.EscapeFrames;

        /// <summary>被擋那一幀的診斷:寫 Information(要使用者回報的診斷),每扇窗 1 秒節流免得洗版。</summary>
        private static void LogHold(string addonName, nint address, string pressKey)
        {
            if (EzThrottler.Throttle($"AddonPressGuard-Hold-{addonName}", 1000))
                Svc.Log.Information($"[AddonPressGuard] 「{addonName}」(實例 0x{address:X},按法「{pressKey}」)" +
                                    "按過之後還沒觀察到它收掉,這一幀不再碰它 —— " +
                                    "對關閉中的視窗送 callback 是攔不到的存取違規。");
        }

        private static void LogPersistentHidden(string addonName, nint address)
        {
            if (EzThrottler.Throttle($"AddonPressGuard-PersistentHidden-{addonName}", 1000))
                Svc.Log.Information($"[AddonPressGuard] 「{addonName}」是常駐窗,實例 0x{address:X} 這一幀不可見" +
                                    "(或不在同名清單裡),不送 —— 它與「連續隱藏才解除」互為反面,兩者一組才是防護。");
        }

        /// <summary>
        /// 從 <see cref="AtkUnitBase"/> 讀出視窗名稱,讓防護可以下沉到只拿得到指標的共用層。
        /// </summary>
        /// <remarks>
        /// ⚠️ 刻意不用產生器給的 <c>NameString</c>:那支是
        /// <c>MemoryMarshal.CreateReadOnlySpanFromNullTerminated</c>,欄位<b>剛好塞滿沒有結尾 0</b>
        /// 時會一路往後掃。這裡自己做<b>有界</b>的讀取,找不到 0 就整段當名字用。
        /// <para>
        /// 📌 這是對「本幀剛從 <c>GetAddonByName</c> 拿到、而且下一步就要送 callback 的實例」
        /// 讀它自己結構裡偏移 0x8 的固定長度欄位 —— 不解任何二級指標,風險嚴格低於後面那個 callback。
        /// </para>
        /// </remarks>
        internal static string ResolveAddonName(AtkUnitBase* addon)
        {
            if (addon == null) return string.Empty;

            Span<byte> span   = addon->Name;
            int        length = span.IndexOf((byte)0);
            if (length < 0) length = span.Length;
            return length == 0 ? string.Empty : Encoding.UTF8.GetString(span[..length]);
        }

        /// <summary>
        /// 把 <c>Callback.Fire</c> 的參數組壓成穩定的「按法」字串。
        /// </summary>
        /// <remarks>
        /// 用不變文化格式化,免得數字在別的地區設定下變成不同的字串(那會讓同一個按法被當成兩種)。
        /// </remarks>
        internal static string BuildPressKey(bool boolValue, object[]? args)
        {
            if (args == null || args.Length == 0) return boolValue ? "T" : "F";

            StringBuilder sb = new(boolValue ? "T" : "F");
            foreach (object? arg in args)
            {
                sb.Append('|');
                sb.Append(arg switch
                          {
                              null              => "null",
                              IFormattable form => form.ToString(null, CultureInfo.InvariantCulture),
                              _                 => arg.ToString() ?? string.Empty
                          });
            }

            return sb.ToString();
        }

        /// <summary>外掛卸載時硬拆所有監聽器(不留指向本組件的委派)。</summary>
        internal static void ForceTeardown()
        {
            foreach ((string addonName, IAddonLifecycle.AddonEventDelegate handler) in Watchers)
            {
                Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup,   addonName, handler);
                Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, addonName, handler);
            }

            // 幀時鐘的委派同樣指向本組件,不拆掉就是留在 Svc.Framework 上的懸空委派。
            if (Interlocked.Exchange(ref frameClockState, 0) != 0)
                Svc.Framework.Update -= OnFrameworkUpdate;

            Watchers.Clear();
            Pressed.Clear();
            PendingChildReleases.Clear();
            FinalizedChildrenBuf.Clear();
        }

        /// <summary>這一幀在同名清單裡對某個位址的觀察。</summary>
        /// <remarks>🔴 <b>零值必須是「不解除」的答案</b>:查詢本身不成立時維持封鎖。</remarks>
        private enum AddonPresence
        {
            /// <summary>查詢不成立(名稱空、位址 0、同名實例超過掃描上限)。<b>這是零值</b>。</summary>
            Unknown = 0,

            /// <summary>掃完同名清單都沒有這個位址＝那扇窗已經收乾淨。</summary>
            NotListed = 1,

            Visible = 2,

            Hidden = 3,
        }

        /// <summary>在 <paramref name="addonName"/> 的<b>所有</b>同名實例裡找 <paramref name="address"/>。</summary>
        /// <remarks>
        /// 🔴 位址只用來做等值比較;解參考的是 <c>GetAddonByName</c> 這一幀交回來的指標,
        /// 而且只在 <paramref name="readVisibility"/> 時才讀。不讀可見性時,找到一律回 <see cref="AddonPresence.Visible"/>。
        /// </remarks>
        private static AddonPresence LookUp(string addonName, nint address, bool readVisibility)
        {
            if (string.IsNullOrEmpty(addonName) || address == 0) return AddonPresence.Unknown;

            for (int i = 1; i <= MaxAddonIndex; i++)
            {
                AtkUnitBase* unit = Svc.GameGui.GetAddonByName<AtkUnitBase>(addonName, i);
                if (unit == null) return AddonPresence.NotListed;
                if ((nint)unit != address) continue;

                if (!readVisibility) return AddonPresence.Visible;
                return unit->IsVisible ? AddonPresence.Visible : AddonPresence.Hidden;
            }

            return AddonPresence.Unknown;
        }

        private static bool IsListed(PressSlot slot) =>
            LookUp(slot.AddonName, slot.Address, false) is AddonPresence.Visible or AddonPresence.Hidden;

        /// <summary>找不到就回 <see langword="false"/>(＝不准送):這支只給放行端用,查歪一律表現成「這一輪不送」。</summary>
        private static bool PersistentAddonVisible(string addonName, nint address)
            => LookUp(addonName, address, readVisibility: true) == AddonPresence.Visible;

        /// <summary>
        /// 每幀的解除路徑:①登記時找得到、現在從同名清單消失 ⇒ 解除(含 PreFinalize 鎖);
        /// ②常駐窗連續隱藏 <see cref="HiddenReleaseFrames"/> 幀 ⇒ 解除。
        /// </summary>
        /// <remarks>
        /// ⚠️ 判準刻意<b>不</b>用「視窗看起來還 ready 嗎」(常駐窗例外):關閉中的那幾幀三關全過,
        /// 拿那個當「窗不見了」會在最危險的那幾幀把封鎖解除掉,等於沒有這道防線。
        /// </remarks>
        private static void PollRecords()
        {
            if (Pressed.Count == 0) return;

            ReleaseBuf.Clear();
            foreach ((PressSlot slot, SlotRecords records) in Pressed)
            {
                bool          persistent = PersistentAddons.Contains(slot.AddonName);
                AddonPresence presence   = LookUp(slot.AddonName, slot.Address, persistent);
                bool          present    = presence != AddonPresence.NotListed;
                if (!present)
                {
                    if (records.Listed) ReleaseBuf.Add(slot);
                    else records.HiddenFrames = 0;
                    continue;
                }

                if (!persistent || records.Finalizing) continue;
                if (presence != AddonPresence.Hidden)
                {
                    records.HiddenFrames = 0;
                    continue;
                }

                if (++records.HiddenFrames < HiddenReleaseFrames) continue;

                ReleaseBuf.Add(slot);
                if (HiddenReleaseReported.Add(slot.AddonName))
                    Svc.Log.Information($"[AddonPressGuard] 「{slot.AddonName}」是常駐視窗(隱藏而不銷毀)," +
                                        $"按下記號改由「連續隱藏 {HiddenReleaseFrames} 幀」解除。這一行每個視窗名每次遊戲只寫一次。");
            }

            foreach (PressSlot slot in ReleaseBuf) Pressed.Remove(slot);
            ReleaseBuf.Clear();
        }

        /// <summary>回收「已經不可能再擋住任何人」的紀錄(不改變任何一次「按／不按」的判定);上鎖的不動。</summary>
        private static void SweepColdRecords()
        {
            if (Pressed.Count == 0) return;

            long frame = (long)frameTicks;
            ReleaseBuf.Clear();
            foreach ((PressSlot slot, SlotRecords records) in Pressed)
            {
                if (records.Finalizing || frame - records.LastFrame < SweepMinAgeFrames) continue;
                ReleaseBuf.Add(slot);
            }

            foreach (PressSlot slot in ReleaseBuf) Pressed.Remove(slot);
            ReleaseBuf.Clear();
        }

        private static void ProcessFinalizedChildren()
        {
            if (FinalizedChildrenBuf.Count == 0) return;

            foreach ((string child, long frame) in FinalizedChildrenBuf)
                ReleaseParentOfClosedDialog(child, frame);
            FinalizedChildrenBuf.Clear();
        }

        /// <summary>
        /// 子視窗收掉(<see cref="AddonEvent.PreFinalize"/>)之後,把「等它收掉才解除」的那一筆父視窗按下紀錄放掉。
        /// </summary>
        /// <remarks>
        /// 🔑 子視窗<b>出現過又收掉</b>,就是父視窗當時活著、那一發確實被收下的正面證據 ——
        /// 直接否定了「上一發把父視窗按關了」這個情境,所以不必為了跑得動而縮短逃生口。
        /// <para>
        /// 🔴 <b>四重比對才解除</b>:父視窗名、<b>位址</b>、按法都要對得上,而且那筆宣告要早於子視窗收掉的那一幀
        /// (同一幀才宣告的不算,那不可能是它開出來的子視窗)。對不上就什麼都不做,封鎖留給輪詢或逃生口處理。
        /// 只解除登記時的那一個按法,尤其不動同位址的 <see cref="ClosePressKey"/>。
        /// </para>
        /// </remarks>
        private static void ReleaseParentOfClosedDialog(string childAddonName, long finalizedFrame)
        {
            if (!PendingChildReleases.TryGetValue(childAddonName, out ChildDialogRelease pending)) return;
            if (pending.RegisteredFrame >= finalizedFrame) return;

            PendingChildReleases.Remove(childAddonName);
            var slot = new PressSlot(pending.ParentAddonName, pending.ParentAddress);
            if (!Pressed.TryGetValue(slot, out SlotRecords? records) || records.Finalizing) return;
            if (!records.ByKey.Remove(pending.ParentPressKey)) return;

            if (records.ByKey.Count == 0)
                Pressed.Remove(slot);
        }

        /// <summary>
        /// 第一次守護某個 addon 名稱時掛上監聽器:PostSetup 清該位址「不是這一幀才登記的」紀錄,PreFinalize 把該位址上鎖。
        /// </summary>
        /// <remarks>
        /// 掛上去之後就不再拆(只在 <see cref="ForceTeardown"/> 拆):動態掛／拆比較容易留下懸空的監聽器。
        /// 🔴🔴 <b>粒度必須是位址,不能是名字</b>:#A 關閉中的同一幀第二扇同名窗 #B 被建立時,不能連坐清掉 #A 的紀錄。
        /// 🔴 PostSetup 的同幀豁免:監聽器之間的呼叫順序不保證,在 PostSetup 處理常式裡按下的紀錄不能被當場清掉。
        /// 📌 PreFinalize 另外記下「這個名字的子視窗收掉了」,父視窗的提早解除留到下一次輪詢做。
        /// </remarks>
        private static void EnsureWatching(string addonName)
        {
            if (Watchers.ContainsKey(addonName)) return;

            IAddonLifecycle.AddonEventDelegate handler = (eventType, args) =>
            {
                nint address = args.Addon.Address;
                if (address == 0) return;
                var slot = new PressSlot(addonName, address);

                if (eventType == AddonEvent.PreFinalize)
                {
                    LockFinalizing(slot);
                    if (PendingChildReleases.ContainsKey(addonName))
                        FinalizedChildrenBuf.Add((addonName, (long)frameTicks));
                    return;
                }

                if (!Pressed.TryGetValue(slot, out SlotRecords? records)) return;

                records.Finalizing = false;
                StaleKeysBuf.Clear();
                foreach ((string pressKey, PressRecord record) in records.ByKey)
                {
                    if (record.Frame == CurrentFrame) continue;
                    StaleKeysBuf.Add(pressKey);
                }

                foreach (string pressKey in StaleKeysBuf) records.ByKey.Remove(pressKey);
                StaleKeysBuf.Clear();
                if (records.ByKey.Count == 0) Pressed.Remove(slot);
            };

            Watchers[addonName] = handler;
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   addonName, handler);
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, addonName, handler);
        }

        /// <summary>該位址進入銷毀:鎖到它從清單消失或同位址重建。守護過的窗名即使沒按過也鎖。</summary>
        private static void LockFinalizing(PressSlot slot)
        {
            if (Pressed.TryGetValue(slot, out SlotRecords? records))
            {
                records.Finalizing = true;
                return;
            }

            Pressed[slot] = new SlotRecords { Listed = true, Finalizing = true, LastFrame = (long)frameTicks };
        }
    }
}
