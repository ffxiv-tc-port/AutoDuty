using AutoDuty.Windows;
using ECommons.DalamudServices;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AutoDuty.Updater
{
    using System.Collections.Generic;

    public class Patcher
    {
        internal static ActionState PatcherState => PatcherTask != null && !PatcherTask.IsCompleted && !PatcherTask.IsCanceled && !PatcherTask.IsFaulted ? ActionState.Running : ActionState.None;
            
        internal static Task<bool>? PatcherTask = null;

        // 只在卸載時取消;插件重載是新的組件,這個欄位跟著重建。
        private static readonly CancellationTokenSource UnloadCts = new();

        internal static void CancelForUnload() => UnloadCts.Cancel();

        internal static void Patch(bool skipMD5 = false, bool startup = false)
        {
            if (PatcherTask == null && (!startup || ConfigurationMain.Instance.UpdatePathsOnStartup))
            {
                PatcherTask = Task.Run(() => PatchTask(skipMD5));
                PatcherTask.ContinueWith(t => {
                    OnPatcherTaskCompleted(t.IsCompletedSuccessfully);
                });
            }
        }

        private static void OnPatcherTaskCompleted(bool success)
        {
            if (PatcherTask != null && success && PatcherTask.Result)
                Svc.Log.Info("Patching Complete");
            PatcherTask = null;
        }

        public static async Task<bool> PatchTask(bool skipMD5)
        {
            Svc.Log.Info("Patching Started");
            try
            {
                var localFileInfos = Plugin.PathsDirectory.EnumerateFiles("*.json", SearchOption.AllDirectories);
                var localFilesDictionary = localFileInfos.ToDictionary(
                    fileInfo => fileInfo.Name,
                    fileInfo => BitConverter.ToString(FileHelper.CalculateMD5(fileInfo.FullName)).Replace("-", "")
                );
                var list = await GitHubHelper.GetPathFileListAsync();
                if (list == null) return false;

                HashSet<string> doNotUpdatePathFiles = ConfigurationMain.Instance.GetCurrentConfig.DoNotUpdatePathFiles;

                var downloadList = list.Where(kvp => !doNotUpdatePathFiles.Contains(kvp.Key) && (!localFilesDictionary.ContainsKey(kvp.Key) || !localFilesDictionary[kvp.Key].Equals(kvp.Value, StringComparison.OrdinalIgnoreCase)));

                foreach (var file in downloadList)
                {
                    if (UnloadCts.IsCancellationRequested)
                    {
                        Svc.Log.Information("Patching stopped: plugin is unloading");
                        return false;
                    }

                    // 來源與 md5s.json 必須是同一個 repo/分支,否則雜湊永遠對不上、每次都重下載。
                    var result = await GitHubHelper.DownloadFileAsync($"{GitHubHelper.PathRepoBaseUrl}AutoDuty/Paths/{file.Key}",$"{Plugin.PathsDirectory.FullName}/{file.Key}");
                    var logger = result ? $"Succesfully downloaded: {file.Key}" : $"Failed to download: {file.Key}";
                    Svc.Log.Info(logger);
                }
                return true;
            }
            catch (Exception ex)
            {
                Svc.Log.Error($"error patching path files: {ex}");
                return false;
            }
        }
    }
}
