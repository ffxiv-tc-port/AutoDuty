using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AutoDuty.Windows;
using ECommons;
using ECommons.DalamudServices;

namespace AutoDuty.Helpers
{
    /// <summary>
    /// 「跳過目前步驟」按鈕的路徑檔寫回。
    /// 跳過的是「Wait(等待 N 毫秒)」步驟時,把已經等掉的時間寫回路徑檔,下次跑到這一步就不用再等那麼久。
    /// </summary>
    internal static class StepSkipHelper
    {
        private const string ChatTag = "AutoDuty";

        /// <summary>
        /// 把 <paramref name="elapsedMs"/> 寫回路徑檔中對應的 Wait 步驟。
        /// 找不到唯一對應就整個不寫,只在聊天視窗說明原因 —— 寧可不改檔,也不要改錯步。
        /// </summary>
        internal static void WriteBackWaitTime(int skippedIndex, PathAction step, int configuredMs, int elapsedMs)
        {
            // 照實寫回,不加緩衝、不取整;只做 1ms 下限與「不超過原值」的上限。
            int newMs = Math.Clamp(elapsedMs, 1, configuredMs);

            try
            {
                string filePath = Plugin.PathFile;

                if (filePath.IsNullOrEmpty() || !File.Exists(filePath))
                {
                    Fail("找不到目前的路徑檔");
                    return;
                }

                string json;
                using (StreamReader reader = new(filePath, Encoding.UTF8))
                    json = reader.ReadToEnd();

                PathFile? pathFile = JsonSerializer.Deserialize<PathFile>(json, BuildTab.jsonSerializerOptions);

                if (pathFile == null || pathFile.Actions.Count == 0)
                {
                    Fail("路徑檔裡讀不到任何步驟");
                    return;
                }

                if (!TryLocateOnDisk(pathFile.Actions, Plugin.Actions, skippedIndex, step, out int diskIndex, out string reason))
                {
                    Fail(reason);
                    return;
                }

                string newValue = newMs.ToString(CultureInfo.InvariantCulture);

                pathFile.Actions[diskIndex].Arguments[0] = newValue;

                File.WriteAllText(filePath, JsonSerializer.Serialize(pathFile, BuildTab.jsonSerializerOptions));

                // 記憶體裡的那一份也要跟著改。
                // step 就是 Plugin.Actions[skippedIndex] 這個物件本身,而 LoadPath 只複製了 list
                // (Actions = [.. path.Actions]),元素還是 ContentPathsManager 快取的 DutyPath.Actions
                // 裡的同一批 PathAction ⇒ 改這一個物件,執行中的清單與路徑快取會同時生效,
                // 這一輪 session 內再跑同一條路徑就是新值。
                // (另外,改寫路徑檔也會讓 FileHelper 的 FileSystemWatcher 重建 DictionaryPaths,
                //  之後讀取時本來就會從磁碟重新反序列化 —— 兩條路都指向同一個新值。)
                if (step.Arguments.Count > 0)
                    step.Arguments[0] = newValue;

                string msg = "已跳過等待並把該步等待改為 " + Seconds(newMs) + " 秒（原 " + Seconds(configuredMs) + " 秒）";
                Svc.Log.Information("[跳過步驟] " + msg + "，寫回檔案:" + filePath + "(步驟索引 執行中=" + skippedIndex + " 檔案=" + diskIndex + ")");
                Svc.Chat.Print(msg, ChatTag);
            }
            catch (Exception ex)
            {
                Svc.Log.Information("[跳過步驟] 寫回等待時間時發生例外:" + ex);
                Fail("寫入時發生例外，詳見 /xllog");
            }

            void Fail(string reason)
            {
                Svc.Log.Information("[跳過步驟] 已跳過但寫回失敗:" + reason);
                Svc.Chat.Print("已跳過但寫回失敗：" + reason, ChatTag);
            }
        }

        private static string Seconds(int ms) => (ms / 1000f).ToString("0.0", CultureInfo.InvariantCulture);

        /// <summary>
        /// 在磁碟上的步驟清單裡找出「執行清單第 <paramref name="memoryIndex"/> 步」對應的那一筆。
        /// </summary>
        /// <remarks>
        /// 🔴 不能直接把 Indexer 當成磁碟檔的索引:執行中的 <c>Plugin.Actions</c> 是載入當下的複本,
        /// 使用者可能在建置分頁增刪過步驟,多重路徑也可能選到別的檔。
        /// 先走「兩邊步驟數相同且同一索引身分相符」的快路徑;不成立時退回「數這是第幾個相同身分的步驟」。
        /// 兩邊相同身分的總數對不起來就判定歧義,不寫。
        /// </remarks>
        private static bool TryLocateOnDisk(List<PathAction> disk, List<PathAction> memory, int memoryIndex, PathAction step, out int diskIndex, out string reason)
        {
            diskIndex = -1;
            reason    = string.Empty;

            if (memoryIndex < 0 || memoryIndex >= memory.Count || !ReferenceEquals(memory[memoryIndex], step))
            {
                reason = "執行中的步驟清單已經變了";
                return false;
            }

            if (disk.Count == memory.Count && SameStep(disk[memoryIndex], step))
            {
                diskIndex = memoryIndex;
                return true;
            }

            int ordinal     = 0;   // step 是執行清單裡第幾個同身分的步驟(1 起算)
            int memoryTotal = 0;

            for (int i = 0; i < memory.Count; i++)
            {
                if (!SameStep(memory[i], step))
                    continue;

                memoryTotal++;
                if (i <= memoryIndex)
                    ordinal = memoryTotal;
            }

            int diskTotal = 0;
            int nth       = -1;

            for (int i = 0; i < disk.Count; i++)
            {
                if (!SameStep(disk[i], step))
                    continue;

                diskTotal++;
                if (diskTotal == ordinal)
                    nth = i;
            }

            if (diskTotal == 0)
            {
                reason = "路徑檔裡找不到這一步";
                return false;
            }

            if (diskTotal != memoryTotal || ordinal <= 0 || nth < 0)
            {
                reason = "路徑檔與執行中的清單對不起來（檔案 " + diskTotal + " 筆、執行中 " + memoryTotal + " 筆相同步驟）";
                return false;
            }

            diskIndex = nth;
            return true;
        }

        /// <summary>步驟身分:名稱、座標、第一個參數(等待毫秒)全部相同才算同一步。</summary>
        private static bool SameStep(PathAction a, PathAction b) =>
            a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase) &&
            a.Position == b.Position &&
            a.Arguments.Count > 0 && b.Arguments.Count > 0 &&
            a.Arguments[0] == b.Arguments[0];
    }
}
