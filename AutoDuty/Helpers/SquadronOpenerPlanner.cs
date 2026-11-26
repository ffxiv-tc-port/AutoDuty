using System.Collections.Generic;

namespace AutoDuty.Helpers
{
    /// <summary>先發與被圍拉回共用的號令時鐘：任兩次號令的最小間隔一起算。</summary>
    internal sealed class SquadronCommandClock
    {
        /// <summary>任兩次號令的最小間隔（號令冷卻未知，與 BMR 號令元件同值）。</summary>
        internal const long AnyGapMs = 2500;

        internal long? LastMs { get; private set; }

        internal bool GapOk(long nowMs) => LastMs == null || nowMs - LastMs.Value >= AnyGapMs;

        internal void Note(long nowMs) => LastMs = nowMs;

        internal void Reset() => LastMs = null;
    }

    /// <summary>
    /// 分隊先發的時機與節流（純邏輯，不碰原生、不寫 log），<see cref="SquadronOpener"/> 與離線探針共用。
    /// 一次先發：路徑 A＝玩家不選目標、號令帶目標參數；路徑 B＝號令目標就是玩家目前目標（玩家目標被別處設成敵人時立刻走 B）。
    /// 等到分隊跟上或逾時；逾時而只下過 A ⇒ 先選目標再下一次令並放手。
    /// 時間一律是呼叫端給的毫秒；一個領地一份，換領地呼叫 <see cref="ResetZone"/>。
    /// </summary>
    internal sealed class SquadronOpenerPlanner(SquadronCommandClock? clock = null)
    {
        internal const long AnyGapMs = SquadronCommandClock.AnyGapMs;

        /// <summary>A 連續這麼多次沒人跟上 ⇒ 本領地改成直接走 B；B 再連續這麼多次沒人跟上 ⇒ 本領地停用先發。</summary>
        internal const int FailStreakLimit = 3;

        /// <summary>等待中玩家血量比例低於這個值就不再等。</summary>
        internal const float LowHpRatio = 0.5f;

        /// <summary>等待上限之外再給的寬限；超過就無條件放手（換目標來回跳也不會無限等）。</summary>
        internal const long HardSlackMs = 1500;

        /// <summary>IssueB＝號令目標是玩家目標（呼叫端必要時先選它）、繼續等；Retargeted＝玩家目標被別處換成另一隻敵人，改等那隻。</summary>
        internal enum Step { Wait, IssueA, IssueB, IssueBAndRelease, Release, Abandon, Retargeted }

        internal enum Reason { None, Followed, FollowedAfterB, FollowedWithoutCommand, Timeout, LowHp, CommandUnavailable, BOnly, TargetGone, TargetSetElsewhere, PlayerTarget, BossStarted, Stopped }

        internal readonly struct Inputs(bool targetValid, ulong playerTarget, bool playerTargetHostile, bool bossActive, bool stopped, bool squadFollowed,
                                        float hpRatio, uint commandStatus, bool busy, long maxWaitMs)
        {
            internal readonly bool  TargetValid         = targetValid;
            internal readonly ulong PlayerTarget        = playerTarget;
            internal readonly bool  PlayerTargetHostile = playerTargetHostile;
            internal readonly bool  BossActive          = bossActive;
            internal readonly bool  Stopped             = stopped;
            internal readonly bool  SquadFollowed       = squadFollowed;
            internal readonly float HpRatio             = hpRatio;
            internal readonly uint  CommandStatus       = commandStatus;
            internal readonly bool  Busy                = busy;
            internal readonly long  MaxWaitMs           = maxWaitMs;
        }

        private readonly HashSet<ulong> _handled = [];
        private int  _aFailStreak;
        private int  _bFailStreak;

        internal SquadronCommandClock Clock { get; } = clock ?? new SquadronCommandClock();
        internal ulong Pending      { get; private set; }
        internal long  StartMs      { get; private set; }
        internal long? AIssuedMs    { get; private set; }
        internal long? BIssuedMs    { get; private set; }
        internal uint  LastStatus   { get; private set; }
        internal bool  BOnly        { get; private set; }
        internal bool  GaveUp       { get; private set; }

        // 本領地彙總
        internal int Started, AlreadyOn, ACommands, AFollowed, BCommands, BFollowed, Timeouts, LowHp, Unavailable, Abandoned, Retargets;
        internal double AFollowSecondsSum, BFollowSecondsSum;

        internal void ResetZone()
        {
            _handled.Clear();
            Clock.Reset();
            _aFailStreak = _bFailStreak = 0;
            Pending = 0;
            AIssuedMs = BIssuedMs = null;
            LastStatus = 0;
            BOnly = GaveUp = false;
            Started = AlreadyOn = ACommands = AFollowed = BCommands = BFollowed = Timeouts = LowHp = Unavailable = Abandoned = Retargets = 0;
            AFollowSecondsSum = BFollowSecondsSum = 0;
        }

        internal bool Handled(ulong target) => _handled.Contains(target);

        /// <summary>同一隻只先發一次；分隊已經在打它＝記一筆、不先發。回 true＝開始等待。</summary>
        internal bool TryStart(ulong target, long nowMs, bool squadAlreadyOn)
        {
            if (GaveUp || Pending != 0 || target == 0 || !_handled.Add(target))
                return false;
            if (squadAlreadyOn)
            {
                ++AlreadyOn;
                return false;
            }
            Pending = target;
            StartMs = nowMs;
            AIssuedMs = BIssuedMs = null;
            LastStatus = 0;
            ++Started;
            return true;
        }

        internal Step Update(long nowMs, in Inputs i, out Reason reason)
        {
            reason = Reason.None;
            if (Pending == 0)
                return Step.Wait;

            if (!i.TargetValid)
                return Finish(Step.Abandon, reason = Reason.TargetGone);
            if (i.BossActive)
                return Finish(Step.Abandon, reason = Reason.BossStarted);
            if (i.Stopped)
                return Finish(Step.Abandon, reason = Reason.Stopped);

            if (i.SquadFollowed)
            {
                if (BIssuedMs is { } b)
                {
                    ++BFollowed;
                    BFollowSecondsSum += (nowMs - b) / 1000.0;
                    _bFailStreak = 0;
                    reason = Reason.FollowedAfterB;
                }
                else if (AIssuedMs is { } a)
                {
                    ++AFollowed;
                    AFollowSecondsSum += (nowMs - a) / 1000.0;
                    _aFailStreak = 0;
                    reason = Reason.Followed;
                }
                else
                    reason = Reason.FollowedWithoutCommand;
                return Finish(Step.Release, reason);
            }

            if (i.HpRatio < LowHpRatio)
            {
                ++LowHp;
                return Finish(Step.Release, reason = Reason.LowHp);
            }

            if (nowMs - StartMs >= i.MaxWaitMs + HardSlackMs)
            {
                ++Timeouts;
                return Finish(Step.Release, reason = Reason.Timeout);
            }

            if (i.PlayerTarget != 0 && i.PlayerTarget != Pending && i.PlayerTargetHostile)
            {
                _handled.Add(i.PlayerTarget);
                Pending = i.PlayerTarget;
                AIssuedMs = BIssuedMs = null;
                ++Retargets;
                reason = Reason.TargetSetElsewhere;
                return Step.Retargeted;
            }

            LastStatus = i.CommandStatus;
            var timedOut = nowMs - StartMs >= i.MaxWaitMs;
            var usable = i.CommandStatus == 0 && !i.Busy;
            var onPlayerTarget = i.PlayerTarget == Pending;

            if (timedOut)
            {
                if (AIssuedMs == null && BIssuedMs == null)
                {
                    ++Unavailable;
                    return Finish(Step.Release, reason = Reason.CommandUnavailable);
                }
                ++Timeouts;
                if (BIssuedMs != null)
                {
                    NoteBResult(null);
                    return Finish(Step.Release, reason = Reason.Timeout);
                }
                if (++_aFailStreak >= FailStreakLimit)
                    BOnly = true;
                reason = Reason.Timeout;
                return Finish(usable ? Step.IssueBAndRelease : Step.Release, reason);
            }

            if (!usable)
                return Step.Wait;
            // 路徑 A 不保證有效（遊戲可能只認玩家目前目標）：間隔內唯一一次號令是這隻自己的 A 時，B 不必等間隔。
            var gapOk = Clock.GapOk(nowMs) || AIssuedMs is { } ownA && Clock.LastMs == ownA;
            if (BIssuedMs == null && (BOnly || onPlayerTarget) && gapOk)
            {
                reason = onPlayerTarget ? Reason.PlayerTarget : Reason.BOnly;
                return Step.IssueB;
            }
            if (AIssuedMs == null && BIssuedMs == null && Clock.GapOk(nowMs))
                return Step.IssueA;
            return Step.Wait;
        }

        /// <summary>呼叫端真的按下路徑 A 之後呼叫（UseAction 回傳值不論）。</summary>
        internal void NoteAIssued(long nowMs)
        {
            AIssuedMs = nowMs;
            Clock.Note(nowMs);
            ++ACommands;
        }

        /// <summary>呼叫端真的按下路徑 B（等待中的 IssueB 或放手時的 IssueBAndRelease）之後呼叫。</summary>
        internal void NoteBIssued(long nowMs)
        {
            if (Pending != 0)
                BIssuedMs = nowMs;
            Clock.Note(nowMs);
            ++BCommands;
        }

        /// <summary>等待以外（被圍拉回）走路徑 A 的結果；連續沒效果達上限 ⇒ 本領地改走 B。</summary>
        internal void NoteAResult(bool effective)
        {
            if (effective)
                _aFailStreak = 0;
            else if (++_aFailStreak >= FailStreakLimit)
                BOnly = true;
        }

        /// <summary>路徑 B 下令後的驗收結果；連續沒跟上達上限 ⇒ 本領地停用。</summary>
        internal void NoteBResult(double? followSeconds)
        {
            if (followSeconds is { } s)
            {
                ++BFollowed;
                BFollowSecondsSum += s;
                _bFailStreak = 0;
            }
            else if (++_bFailStreak >= FailStreakLimit)
                GaveUp = true;
        }

        private Step Finish(Step step, Reason reason)
        {
            if (step == Step.Abandon)
                ++Abandoned;
            Pending = 0;
            return step;
        }
    }
}
