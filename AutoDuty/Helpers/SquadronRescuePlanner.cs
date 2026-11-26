using System;
using System.Collections.Generic;

namespace AutoDuty.Helpers
{
    /// <summary>
    /// 冒險者小隊小怪路段「被圍拉回」的挑選與節流（純邏輯，不碰原生、不寫 log），<see cref="SquadronOpener"/> 與離線探針共用。
    /// 敵人的目標是玩家、分隊坦克沒在打它、持續 <see cref="DwellMs"/>（玩家血量過低時不等）⇒ 對它下號令。
    /// 一次只處理一隻：下令後等它離開玩家（或坦克打上它）或 <see cref="HoldMs"/> 到期才換下一隻。
    /// </summary>
    internal sealed class SquadronRescuePlanner(SquadronCommandClock clock)
    {
        internal const long DwellMs = 1200;
        internal const long HoldMs = 4000;
        /// <summary>同一隻兩次號令的最小間隔：避免坦克在兩隻之間來回換目標。</summary>
        internal const long SameTargetCooldownMs = 8000;
        internal const int MaxPerTarget = 3;

        internal readonly struct Attacker(ulong id, bool tankOn, bool isPlayerTarget)
        {
            internal readonly ulong Id             = id;
            internal readonly bool  TankOn         = tankOn;
            internal readonly bool  IsPlayerTarget = isPlayerTarget;
        }

        private sealed class Issued
        {
            internal int  Count;
            internal long LastMs;
        }

        private readonly Dictionary<ulong, long> _since = [];
        private readonly Dictionary<ulong, Issued> _issued = [];
        private readonly List<ulong> _drop = [];

        internal ulong Current   { get; private set; }
        internal long  CurrentMs { get; private set; }

        // 本領地彙總
        internal int Commands, TankSwitched, MonsterSwitched, GaveUpTargets;
        internal double TankSwitchSecondsSum, MonsterSwitchSecondsSum;

        internal void ResetZone()
        {
            _since.Clear();
            _issued.Clear();
            Current = 0;
            CurrentMs = 0;
            Commands = TankSwitched = MonsterSwitched = GaveUpTargets = 0;
            TankSwitchSecondsSum = MonsterSwitchSecondsSum = 0;
        }

        /// <summary>這隻已經連續打玩家多久（毫秒）；不在名單上回 0。</summary>
        internal long DwellOf(ulong id, long nowMs) => _since.TryGetValue(id, out var s) ? nowMs - s : 0;

        /// <summary>
        /// 每個節拍呼叫一次，<paramref name="attackers"/>＝目前目標是玩家的敵人。回傳要下令的敵人，0＝這拍不下。
        /// 挑選：玩家目前目標優先（玩家的傷害會把它一直留在玩家身上，也是路徑 A／B 都蓋得到的那隻），其次打玩家最久的。
        /// </summary>
        internal ulong Choose(long nowMs, ReadOnlySpan<Attacker> attackers, bool hpLow, ulong exclude)
        {
            _drop.Clear();
            foreach (var id in _since.Keys)
                if (!Contains(attackers, id, out var a) || a.TankOn)
                    _drop.Add(id);
            foreach (var id in _drop)
                _since.Remove(id);
            foreach (var a in attackers)
                if (!a.TankOn)
                    _since.TryAdd(a.Id, nowMs);

            if (Current != 0)
            {
                var stillOnPlayer = Contains(attackers, Current, out var cur) && !cur.TankOn;
                if (stillOnPlayer && nowMs - CurrentMs < HoldMs)
                    return 0;
                Current = 0;
            }
            if (!clock.GapOk(nowMs))
                return 0;

            ulong best = 0;
            var bestPlayerTarget = false;
            long bestSince = long.MaxValue;
            foreach (var a in attackers)
            {
                if (a.TankOn || a.Id == exclude || !_since.TryGetValue(a.Id, out var since))
                    continue;
                if (!hpLow && nowMs - since < DwellMs)
                    continue;
                if (_issued.TryGetValue(a.Id, out var iss) && (iss.Count >= MaxPerTarget || nowMs - iss.LastMs < SameTargetCooldownMs))
                    continue;
                if (best == 0 || a.IsPlayerTarget && !bestPlayerTarget || a.IsPlayerTarget == bestPlayerTarget && since < bestSince)
                {
                    best = a.Id;
                    bestPlayerTarget = a.IsPlayerTarget;
                    bestSince = since;
                }
            }
            return best;
        }

        /// <summary>呼叫端真的按下號令之後呼叫（UseAction 回傳值不論）。</summary>
        internal void NoteIssued(ulong id, long nowMs)
        {
            if (!_issued.TryGetValue(id, out var iss))
                _issued[id] = iss = new Issued();
            if (++iss.Count == MaxPerTarget)
                ++GaveUpTargets;
            iss.LastMs = nowMs;
            Current = id;
            CurrentMs = nowMs;
            clock.Note(nowMs);
            ++Commands;
        }

        /// <summary>下令後的驗收結果（秒；null＝驗收期間沒發生）。</summary>
        internal void NoteResult(double? tankSwitchSeconds, double? monsterSwitchSeconds)
        {
            if (tankSwitchSeconds is { } t)
            {
                ++TankSwitched;
                TankSwitchSecondsSum += t;
            }
            if (monsterSwitchSeconds is { } m)
            {
                ++MonsterSwitched;
                MonsterSwitchSecondsSum += m;
            }
        }

        private static bool Contains(ReadOnlySpan<Attacker> attackers, ulong id, out Attacker found)
        {
            foreach (var a in attackers)
                if (a.Id == id)
                {
                    found = a;
                    return true;
                }
            found = default;
            return false;
        }
    }
}
