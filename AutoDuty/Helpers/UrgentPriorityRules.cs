using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoDuty.Helpers
{
    /// <summary>首領戰限時必殺目標依 BMR 優先度挑目標的純邏輯（不碰原生），ActionsManager 與離線探針共用。</summary>
    internal static class UrgentPriorityRules
    {
        /// <summary>
        /// 手上那隻（清單索引 <paramref name="current"/>）之外，優先度嚴格更高且 <paramref name="usable"/> 的最高那隻；沒有回 -1。
        /// 同優先度不換（兩隻同類小怪之間不來回切），同為最高時取清單前面那隻。
        /// </summary>
        internal static int HigherThanCurrent(int[] prios, int current, Func<int, bool> usable)
        {
            var best = -1;
            for (var i = 0; i < prios.Length; ++i)
            {
                if (prios[i] <= prios[current] || best >= 0 && prios[i] <= prios[best])
                    continue;
                if (usable(i))
                    best = i;
            }
            return best;
        }

        /// <summary>下手順序：優先度由高到低、同優先度照清單順序；沒有優先度就是清單順序。</summary>
        internal static IEnumerable<int> Order(int count, int[]? prios)
        {
            var order = Enumerable.Range(0, count);
            return prios == null ? order : order.OrderByDescending(i => prios[i]);
        }
    }
}
