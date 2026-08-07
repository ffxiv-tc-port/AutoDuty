using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Collections.Generic;
using System.Linq;

namespace AutoDuty.Helpers
{
    using System;
    using global::AutoDuty.Multibox;
    using static Data.Classes;

    internal unsafe class QueueHelper : ActiveHelperBase<QueueHelper>
    {
        /// <summary>
        /// 只等待並接受副本確認視窗,不自己排隊。
        /// 📌 供 Multibox 用戶端使用:排隊是主機端做的,用戶端只負責接下彈出的確認。
        /// </summary>
        internal static void InvokeAcceptOnly()
        {
            _dutyMode = DutyMode.None;
            Svc.Log.Information("Queueing: Accepting only");
            Instance.Start();
            Plugin.Action = "Queueing: Waiting to accept";
        }

        internal static void Invoke(Content? content, DutyMode dutyMode)
        {
            if (State != ActionState.Running && content != null && dutyMode != DutyMode.None)
            {
                _dutyMode = dutyMode;
                _content = content;
                Svc.Log.Info($"Queueing: {dutyMode}: {content.Name}");

                Instance.Start();
                Plugin.Action = $"Queueing {_dutyMode}: {content.Name}";
            }
        }

        protected override string Name        => nameof(QueueHelper);
        protected override string DisplayName => $"Queueing {_dutyMode}: {_content?.Name}";

        internal override void Stop()
        {
            if (State == ActionState.Running)
                Svc.Log.Info($"Done Queueing: {_dutyMode}: {_content?.Name}");
            _content = null;
            _allConditionsMetToJoin = false;
            _turnedOffTrustMembers = false;
            _turnedOnConfigMembers = false;
            _dutyMode = DutyMode.None;

            base.Stop();
        }

        private static Content? _content = null;
        private static DutyMode _dutyMode = DutyMode.None;
        private AddonContentsFinder* _addonContentsFinder = null;
        private bool _allConditionsMetToJoin = false;
        private bool _turnedOffTrustMembers = false;
        private bool _turnedOnConfigMembers = false;

        private static bool ContentsFinderConfirm()
        {
            if (GenericHelpers.TryGetAddonByName("ContentsFinderConfirm", out AtkUnitBase* addonContentsFinderConfirm) && GenericHelpers.IsAddonReady(addonContentsFinderConfirm))
            {
                Svc.Log.Debug("Queue Helper - Confirming DutyPop");
                AddonHelper.FireCallBack(addonContentsFinderConfirm, true, 8);
                return true;
            }
            return false;
        }

        private void QueueTrust()
        {
            if (TrustHelper.State == ActionState.Running) return;

            AgentDawn* agentDawn = AgentDawn.Instance();
            if (!agentDawn->IsAddonReady())
            {
                if (!EzThrottler.Throttle("OpenDawn", 5000) || !AgentHUD.Instance()->IsMainCommandEnabled(82)) return;

                Svc.Log.Debug("Queue Helper - Opening Dawn");
                RaptureAtkModule.Instance()->OpenDawn(_content.RowId);
                return;
            }

            if (agentDawn->Data->ContentData.ExpansionCount < (_content!.ExVersion - 2))
            {
                Svc.Log.Debug($"Queue Helper - You do not have expansion: {_content.ExVersion} unlocked stopping");
                Stop();
                return;
            }

            if ((byte) agentDawn->SelectedContentId != _content.DawnRowId)
            {
                Svc.Log.Debug($"Queue Helper - Clicking: {_content.EnglishName} at {_content.RowId} with dawn {_content.DawnRowId} instead of {agentDawn->SelectedContentId}");
                RaptureAtkModule.Instance()->OpenDawn(_content.RowId);
            }
            else if (!_turnedOffTrustMembers)
            {
                if (EzThrottler.Throttle("_turnedOffTrustMembers", 500))
                {
                    agentDawn->Data->PartyData.ClearParty();
                    agentDawn->UpdateAddon();
                    SchedulerHelper.ScheduleAction("_turnedOffTrustMembers", () => _turnedOffTrustMembers = true, 250);
                }
            }
            else if (!_turnedOnConfigMembers)
            {
                if (EzThrottler.Throttle("_turnedOnConfigMembers", 500))
                {
                    AgentDawnInterface.DawnMemberEntry* curMembers = agentDawn->Data->MemberData.GetMembers(agentDawn->Data->MemberData.CurrentMembersIndex);
                    var                                 members    = Plugin.Configuration.SelectedTrustMembers;
                    if (members.Count(x => x is not null) == 3)
                        members.OrderBy(x => TrustHelper.Members[(TrustMemberName)x!].Role)
                               .Each(member =>
                                     {
                                         if (member != null)
                                         {
                                             byte                               index       = TrustHelper.Members[(TrustMemberName)member].Index;
                                             AgentDawnInterface.DawnMemberEntry memberEntry = curMembers[index];

                                             agentDawn->Data->PartyData.AddMember(index, &memberEntry);
                                         }
                                     });
                    agentDawn->UpdateAddon();
                    SchedulerHelper.ScheduleAction("_turnedOnConfigMembers", () => _turnedOnConfigMembers = true, 250);
                }
            }
            else if(EzThrottler.Throttle("ClickRegisterButton", 10000))
            {
                Svc.Log.Debug($"Queue Helper - Clicking: Register For Duty");
                agentDawn->RegisterForDuty();
            }
        }

        private void QueueSupport()
        {
            AgentDawnStory* agentDawnStory = AgentDawnStory.Instance();
            if (!agentDawnStory->IsAddonReady())
            {
                if (!EzThrottler.Throttle("OpenDawnStory", 5000) || !AgentHUD.Instance()->IsMainCommandEnabled(91)) return;
                
                Svc.Log.Debug("Queue Helper - Opening DawnStory");
                RaptureAtkModule.Instance()->OpenDawnStory(_content.Id);
                return;
            }

            if (agentDawnStory->Data->ContentData.ExpansionCount <= _content!.ExVersion)
            {
                Svc.Log.Debug($"Queue Helper - You do not have expansion: {_content.ExVersion} unlocked. stopping");
                Stop();
                return;
            }

            if (agentDawnStory->Data->ContentData.ContentEntries[agentDawnStory->Data->ContentData.SelectedContentEntry].ContentFinderConditionId != _content.RowId)
            {
                Svc.Log.Debug($"Queue Helper - Clicking: {_content.EnglishName} {_content.RowId}");// instead of {agentDawnStory->Data->ContentData.ContentEntries[agentDawnStory->Data->ContentData.SelectedContentEntry].ContentFinderConditionId}");

                RaptureAtkModule.Instance()->OpenDawnStory(_content.RowId);
            }
            else if(EzThrottler.Throttle("ClickRegisterButton", 10000))
            {
                Svc.Log.Debug($"Queue Helper - Clicking: Register For Duty");
                AgentDawnStory.Instance()->RegisterForDuty();
            }
        }

        /// <summary>
        /// 取出副本列表目前選取項目的顯示名稱;取不到就回 "?"。
        ///
        /// 這是純診斷用途,原本整條鏈零檢查:
        ///   items[(int)DutyList->SelectedItemIndex].Renderer->GetTextNodeById(5)->GetAsAtkTextNode()->NodeText
        /// 三個問題:
        ///  ① SelectedItemIndex 沿用 AtkComponentList 的語意,**沒有選取時是 -1**,
        ///     而且沒有任何上界比對 → 直接丟 ArgumentOutOfRangeException。
        ///  ② Renderer 可能是 null,而 GetTextNodeById 是 [MemberFunction] 原生呼叫,
        ///     對 null 呼叫即 AccessViolationException(corrupted-state,try/catch 攔不到)。
        ///  ③ GetTextNodeById 找不到節點時回 null,原本直接再接 GetAsAtkTextNode()。
        /// 回 "?" 而不是空字串,是為了讓診斷訊息看得出「不知道」而不是「名字是空的」。
        /// </summary>
        private string GetSelectedDutyListItemName(List<AtkComponentTreeListItem> items)
        {
            if (_addonContentsFinder == null)
                return "?";

            var dutyList = _addonContentsFinder->DutyList;
            if (dutyList == null)
                return "?";

            var index = dutyList->SelectedItemIndex;
            if (index < 0 || index >= items.Count)
                return "?";

            var renderer = items[index].Renderer;
            if (renderer == null)
                return "?";

            var textNode = renderer->GetTextNodeById(5);
            if (textNode == null || textNode->AtkResNode.Type != NodeType.Text)
                return "?";

            return textNode->NodeText.ToString().Replace("...", "");
        }

        private void QueueRegular()
        {
            if (ContentsFinder.Instance()->IsUnrestrictedParty != Plugin.Configuration.Unsynced)
            {
                Svc.Log.Debug("Queue Helper - Setting UnrestrictedParty");
                ContentsFinder.Instance()->IsUnrestrictedParty = Plugin.Configuration.Unsynced;
                return;
            }

            GenericHelpers.TryGetAddonByName("ContentsFinder", out _addonContentsFinder);
            if (!_allConditionsMetToJoin && (_addonContentsFinder == null || !GenericHelpers.IsAddonReady((AtkUnitBase*)_addonContentsFinder)))
            {
                if (!AgentHUD.Instance()->IsMainCommandEnabled(33))
                    return;
                Svc.Log.Debug($"Queue Helper - Opening ContentsFinder to {_content!.Name}");
                AgentContentsFinder.Instance()->OpenRegularDuty(_content.ContentFinderCondition);
                return;
            }

            // 上面那個 && 在 _allConditionsMetToJoin 為 true 時會短路,addon 的 null 檢查整個被跳過;
            // 而 TryGetAddonByName 找不到 addon 時會把 out 參數設成 null ——
            // 也就是排隊條件都符合之後,ContentsFinder 一關閉,下一 tick 就會對空指標取 DutyList。
            // 這裡補一道與 _allConditionsMetToJoin 無關的閘,失敗形式是「這一 tick 不動作」。
            if (_addonContentsFinder == null || _addonContentsFinder->DutyList == null)
                return;

            if (_addonContentsFinder->DutyList->Items.LongCount == 0)
                return;

            var vectorDutyListItems = _addonContentsFinder->DutyList->Items;
            List<AtkComponentTreeListItem> listAtkComponentTreeListItems = [];
            if (vectorDutyListItems.Count == 0)
                return;
            
            // 向量裡的項目指標可能是空的,解參考前先擋掉(原本是無條件 *p.Value)。
            vectorDutyListItems.ForEach(pointAtkComponentTreeListItem =>
            {
                if (pointAtkComponentTreeListItem.Value != null)
                    listAtkComponentTreeListItems.Add(*(pointAtkComponentTreeListItem.Value));
            });

            if (!_allConditionsMetToJoin && AgentContentsFinder.Instance()->SelectedDuty.Id != _content!.ContentFinderCondition)
            {
                // 原本這行把整條原生解參考鏈寫在字串插值裡 —— 插值一律先求值,
                // 所以不管記錄等級開到多低都會執行。先取進區域變數,插值只用區域變數。
                var wrongSelectionName = GetSelectedDutyListItemName(listAtkComponentTreeListItems);
                Svc.Log.Debug($"Queue Helper - Opening ContentsFinder to {_content.Name} because we have the wrong selection of {wrongSelectionName}");
                AgentContentsFinder.Instance()->OpenRegularDuty(_content.ContentFinderCondition);
                EzThrottler.Throttle("QueueHelper", 500, true);
                return;
            }

            // AtkValues 是原生指標陣列,沒有 Length 可以靠 —— 索引 18 必須先比對 AtkValuesCount。
            // 越界讀到的是垃圾型別 + 垃圾指標,而 GetValueAsString() 會照那個型別把它當字串指標解參考。
            // 取不到時視為「目前沒有選取任何副本」,走既有的 SelectDuty 分支(與原本空字串的行為一致)。
            var selectedDutyName = string.Empty;
            if (_addonContentsFinder->AtkValues != null && _addonContentsFinder->AtkValuesCount > 18)
                selectedDutyName = _addonContentsFinder->AtkValues[18].GetValueAsString().Replace("\u0002\u001a\u0002\u0002\u0003", string.Empty).Replace("\u0002\u001a\u0002\u0001\u0003", string.Empty).Replace("\u0002\u001f\u0001\u0003", "\u2013");
            if (selectedDutyName != _content!.Name && !string.IsNullOrEmpty(selectedDutyName))
            {
                Svc.Log.Debug($"Queue Helper - We have {selectedDutyName} selected, not {_content.Name}, Clearing.");
                AddonHelper.FireCallBack((AtkUnitBase*)_addonContentsFinder, true, 12, 1);
                return;
            }

            if (string.IsNullOrEmpty(selectedDutyName))
            {
                Svc.Log.Debug("Queue Helper - Checking Duty");
                SelectDuty(_addonContentsFinder);
                return;
            }

            if (selectedDutyName == _content.Name)
            {
                _allConditionsMetToJoin = true;
                Svc.Log.Debug("Queue Helper - All Conditions Met, Clicking Join");
                AddonHelper.FireCallBack((AtkUnitBase*)_addonContentsFinder, true, 12, 0);

                // 主機端按下報名的同一刻,通知各用戶端準備接受彈出的確認視窗。
                // 未啟用多開 / 非主機端時整段跳過。
                if (MultiboxUtility.Config is { MultiBox: true, Host: true })
                    MultiboxUtility.Server.Queue();
                return;
            }
            Svc.Log.Debug("end");
        }

        protected override void HelperUpdate(IFramework framework)
        {
            if (_content == null || Plugin.InDungeon || Svc.ClientState.TerritoryType == _content?.TerritoryType)
                Stop();

            if (!EzThrottler.Throttle("QueueHelper", 250)|| !PlayerHelper.IsReadyFull || ContentsFinderConfirm() || Conditions.Instance()->InDutyQueue) return;

            switch (_dutyMode)
            {
                case DutyMode.Regular:
                case DutyMode.Trial:
                case DutyMode.Raid:
                    try
                    {
                        QueueRegular();
                    }
                    catch (Exception ex)
                    {
                        Svc.Log.Error(ex.ToString());
                    }

                    break;
                case DutyMode.Support:
                    QueueSupport();
                    break;
                case DutyMode.Trust:
                    QueueTrust();
                    break;
            }
        }

        private static uint HeadersCount(int before, List<AtkComponentTreeListItem> list)
        {
            uint count = 0;
            try
            {
                for (int i = 0; i < before; i++)
                {
                    if (list[i].UIntValues[0] == 0 || list[i].UIntValues[0] == 1)
                        count++;
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex.ToString());
            }

            return count;
        }

        private static void SelectDuty(AddonContentsFinder* addonContentsFinder)
        {
            if (addonContentsFinder == null) return;
            
            var vectorDutyListItems = addonContentsFinder->DutyList->Items;
            List<AtkComponentTreeListItem> listAtkComponentTreeListItems = [];
            vectorDutyListItems.ForEach(pointAtkComponentTreeListItem => listAtkComponentTreeListItems.Add(*(pointAtkComponentTreeListItem.Value)));
            AddonHelper.FireCallBack((AtkUnitBase*)addonContentsFinder, true, 3, HeadersCount(addonContentsFinder->DutyList->SelectedItemIndex, listAtkComponentTreeListItems) + 1); // - (HeadersCount(addonContentsFinder->DutyList->SelectedItemIndex, listAtkComponentTreeListItems) + 1));
        }
    }
}
