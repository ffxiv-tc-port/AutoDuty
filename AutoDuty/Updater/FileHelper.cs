using AutoDuty.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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

            FileWatcher.Changed += (sender, e) =>
            {
                using FileStream fs = new(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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
                // 舊寫法對約 650 筆 content 各做一次 AllDirectories 遞迴掃描 = 整棵 paths 目錄掃 650 遍，
                // 而且 FileSystemWatcher 每收到一個檔案事件就整套重跑（下載路徑時會連續觸發）。
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
                // 舊寫法是先把 DictionaryPaths 清空再逐筆 Add，而讀取端（約 20 處，多數在主執行緒）
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
        /// path 檔名格式固定是「(territoryType) 名稱.json」，等同舊的 "({id})*.json" 過濾條件。
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
        /// 讀取並反序列化全部 271 個 path json。
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
