using AutoDuty.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ECommons;
using ECommons.DalamudServices;
using Serilog.Events;
using AutoDuty.Windows;

namespace AutoDuty.Updater
{
    using static Data.Classes;
    internal static class FileHelper
    {
        internal static readonly FileSystemWatcher FileSystemWatcher = new(Plugin.PathsDirectory.FullName)
        {
            NotifyFilter = NotifyFilters.Attributes
                                                                                        | NotifyFilters.CreationTime
                                                                                        | NotifyFilters.DirectoryName
                                                                                        | NotifyFilters.FileName
                                                                                        | NotifyFilters.LastAccess
                                                                                        | NotifyFilters.LastWrite
                                                                                        | NotifyFilters.Security
                                                                                        | NotifyFilters.Size,

            Filter = "*.json",
            IncludeSubdirectories = true
        };

        internal static readonly FileSystemWatcher FileWatcher = new();

        private static readonly object _updateLock = new();

        // 0＝還沒報過讀 log 失敗；只報第一次，之後的失敗安靜略過。
        private static int _logReadFailureReported;

        public static byte[] CalculateMD5(string filename)
        {
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(filename);
            return md5.ComputeHash(stream);
        }

        internal static void LogInit()
        {
            var path = $"{Plugin.DalamudDirectory}/dalamud.log";
            if (!File.Exists(path)) return;
            var file = new FileInfo(path);
            if (file == null) return;
            var directory = file.DirectoryName;
            var filename = file.Name;
            if (directory.IsNullOrEmpty() || filename.IsNullOrEmpty()) return;
            var lastMaxOffset = file.Length;

            FileWatcher.Path = directory!;
            FileWatcher.Filter = filename;
            FileWatcher.NotifyFilter = NotifyFilters.LastWrite;

            // 跑在執行緒池：處理器擲出的例外沒有人接，會直接結束整個遊戲行程，所以整段包住。
            FileWatcher.Changed += (sender, e) =>
            {
                try
                {
                    using FileStream fs = new(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    // 檔案比上次讀到的位置短＝換成了新檔，從頭讀。
                    if (fs.Length < lastMaxOffset)
                        lastMaxOffset = 0;
                    fs.Seek(lastMaxOffset, SeekOrigin.Begin);
                    using StreamReader sr = new(fs);
                    var x = string.Empty;
                    while ((x = sr.ReadLine()) != null)
                    {
                        if (!x.Contains("[AutoDuty]")) continue;

                        var logEntry = new LogMessage() { Message = x };

                        if (x.Contains("[FTL]"))
                            logEntry.LogEventLevel = LogEventLevel.Fatal;
                        else if (x.Contains("[ERR]"))
                            logEntry.LogEventLevel = LogEventLevel.Error;
                        else if (x.Contains("[WRN]"))
                            logEntry.LogEventLevel = LogEventLevel.Warning;
                        else if (x.Contains("[INF]"))
                            logEntry.LogEventLevel = LogEventLevel.Information;
                        else if (x.Contains("[DBG]"))
                            logEntry.LogEventLevel = LogEventLevel.Debug;
                        else if (x.Contains("[VRB]"))
                            logEntry.LogEventLevel = LogEventLevel.Verbose;
                        LogTab.Add(logEntry);
                    }
                    lastMaxOffset = fs.Position;
                }
                catch (Exception ex)
                {
                    if (Interlocked.Exchange(ref _logReadFailureReported, 1) != 0)
                        return;
                    // 卸載途中進來的事件可能碰到已收掉的 log 服務，這裡也不可以往外擲。
                    try { Svc.Log.Warning($"Reading dalamud.log for the Log tab failed (further failures are not reported): {ex}"); }
                    catch { }
                }
            };
            FileWatcher.EnableRaisingEvents = true;
        }

        internal static void Init()
        {
            FileSystemWatcher.Changed += OnChanged;
            FileSystemWatcher.Created += OnCreated;
            FileSystemWatcher.Deleted += OnDeleted;
            FileSystemWatcher.Renamed += OnRenamed;
            FileSystemWatcher.EnableRaisingEvents = true;
            // 載入時要同步發布：建構式後面（例如 ClientStateOnLogin 的練級副本挑選）馬上就會讀
            // DictionaryPaths，這時還沒有人在並行讀取，直接發布最安全也最貼近原本行為。
            Update(publishImmediately: true);
            LogInit();
        }

        private static void Update(bool publishImmediately = false)
        {
            lock (_updateLock)
            {
                // 目錄只掃一次，依檔名開頭的「(territoryType)」分桶。
                // 不要對每筆 content 各做一次 AllDirectories 遞迴掃描（等於整棵 paths 目錄掃數百遍），
                // FileSystemWatcher 每收到一個檔案事件就會整套重跑（下載路徑時會連續觸發）。
                Dictionary<uint, List<string>> filesByTerritory = [];

                foreach (FileInfo file in Plugin.PathsDirectory.EnumerateFiles("*.json", SearchOption.AllDirectories))
                {
                    if (!TryGetTerritoryType(file.Name, out uint territoryType))
                        continue;

                    if (!filesByTerritory.TryGetValue(territoryType, out List<string>? territoryFiles))
                        filesByTerritory[territoryType] = territoryFiles = [];

                    territoryFiles.Add(file.FullName);
                }

                // 先在區域變數裡建好整份字典，最後才一次換掉。
                // 不可以先把 DictionaryPaths 清空再逐筆 Add：讀取端（多數在主執行緒）
                // 完全不上鎖 —— 這個方法又是從 FileSystemWatcher 的背景執行緒上呼叫的，
                // 讀取端會撞見清空到一半／建到一半的字典。
                Dictionary<uint, ContentPathsManager.ContentPathContainer> newPaths = [];

                foreach ((uint _, Content? content) in ContentHelper.DictionaryContent)
                {
                    if (!filesByTerritory.TryGetValue(content.TerritoryType, out List<string>? territoryFiles))
                        continue;

                    if (!newPaths.TryGetValue(content.TerritoryType, out ContentPathsManager.ContentPathContainer? container))
                        newPaths[content.TerritoryType] = container = new ContentPathsManager.ContentPathContainer(content);

                    foreach (string filePath in territoryFiles)
                        container.AddPath(filePath);
                }

                void Publish()
                {
                    ContentPathsManager.DictionaryPaths = newPaths;

                    MainTab.PathsUpdated();
                    PathsTab.PathsUpdated();
                }

                // FileSystemWatcher 的事件是在背景執行緒上進來的，換字典與清掉選取狀態都推回
                // 框架（主）執行緒做，讓讀取端不會在繪製途中被換掉底下的資料。
                if (publishImmediately || Svc.Framework.IsInFrameworkUpdateThread)
                    Publish();
                else
                    _ = Svc.Framework.RunOnFrameworkThread(Publish);

                Task.Run(() => PreloadPathMeta(newPaths));
            }
        }

        /// <summary>
        /// path 檔名格式固定是「(territoryType) 名稱.json」。
        /// </summary>
        private static bool TryGetTerritoryType(string fileName, out uint territoryType)
        {
            territoryType = 0;

            if (fileName.Length < 3 || fileName[0] != '(')
                return false;

            int close = fileName.IndexOf(')');

            return close > 1 && uint.TryParse(fileName.AsSpan(1, close - 1), out territoryType);
        }

        /// <summary>
        /// 在背景把每個 path 的 Meta（版本、備註）讀好，讓路徑分頁第一幀不必在主執行緒上
        /// 讀取並反序列化全部 path json。
        /// </summary>
        private static void PreloadPathMeta(Dictionary<uint, ContentPathsManager.ContentPathContainer> paths)
        {
            try
            {
                foreach (ContentPathsManager.ContentPathContainer container in paths.Values.ToArray())
                    foreach (ContentPathsManager.DutyPath path in container.Paths.ToArray())
                        path.PreloadMeta();
            }
            catch (Exception ex)
            {
                Svc.Log.Warning($"Preloading duty path metadata failed: {ex}");
            }
        }

        private static void OnChanged(object sender, FileSystemEventArgs e) => Update();

        private static void OnCreated(object sender, FileSystemEventArgs e) => Update();

        private static void OnDeleted(object sender, FileSystemEventArgs e) => Update();

        private static void OnRenamed(object sender, RenamedEventArgs e) => Update();
    }
}
