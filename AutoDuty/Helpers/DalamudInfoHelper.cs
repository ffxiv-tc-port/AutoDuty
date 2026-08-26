using ECommons.DalamudServices;
using ECommons.Reflection;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Dalamud.Networking.Http;

namespace AutoDuty.Helpers
{
    using Dalamud.Common;
    using Newtonsoft.Json;

    internal static class DalamudInfoHelper
    {
        private static bool stagingChecked = false;
        private static bool isStaging      = false;
        private static bool checkStarted   = false;

        // MainWindow.Draw() 每一幀都會問這個值，而實際的判斷要做阻塞式網路 I/O
        // （抓 raw.githubusercontent.com，逾時 10 秒）加讀設定檔，絕對不能跑在繪製（主）執行緒上。
        // 改成只在背景啟動一次檢查，結果回來之前一律回報「不是 staging」。
        public static bool IsOnStaging()
        {
            if(Plugin.isDev)
                return false;

            if (stagingChecked)
                return isStaging;

            if (!checkStarted)
            {
                checkStarted = true;
                Task.Run(CheckStaging);
            }

            return false;
        }

        // 注意：一律先寫 isStaging 再寫 stagingChecked，避免主執行緒看到「已檢查完」卻讀到舊結果。
        private static void CheckStaging()
        {
            if (DalamudReflector.TryGetDalamudStartInfo(out DalamudStartInfo? startinfo, Svc.PluginInterface))
            {
                try
                {
                    SocketsHttpHandler httpHandler    = new() { AutomaticDecompression = DecompressionMethods.All, ConnectCallback = new HappyEyeballsCallback().ConnectCallback };
                    HttpClient         client         = new(httpHandler) { Timeout = TimeSpan.FromSeconds(10) };
                    const string       dalDeclarative = "https://raw.githubusercontent.com/goatcorp/dalamud-declarative/refs/heads/main/config.yaml";
                    using Stream       stream         = client.GetStreamAsync(dalDeclarative).Result;
                    using StreamReader reader         = new(stream);

                    for (int i = 0; i <= 4; i++)
                    {
                        string line = reader.ReadLine().Trim();
                        if (i != 4) continue;
                        string version = line.Split(":").Last().Trim().Replace("'", "");
                        if (version != startinfo.GameVersion.ToString())
                        {
                            isStaging      = false;
                            stagingChecked = true;
                            return;
                        }
                    }
                }
                catch
                {
                    // Something has gone wrong with checking the Dalamud github file, just allow plugin load anyway
                    isStaging      = false;
                    stagingChecked = true;
                    return;
                }

                if (File.Exists(startinfo.ConfigurationPath))
                {
                    try
                    {
                        string file = File.ReadAllText(startinfo.ConfigurationPath);
                        var ob = JsonConvert.DeserializeObject<dynamic>(file);
                        string type = ob.DalamudBetaKind;
                        isStaging      = type is not null && !string.IsNullOrEmpty(type) && type != "release";
                        stagingChecked = true;
                    }
                    catch (Exception ex)
                    {
                        Svc.Chat.PrintError($"Unable to determine Dalamud staging due to file being config being unreadable.");
                        Svc.Log.Error(ex.ToString());
                        isStaging      = false;
                        stagingChecked = true;
                    }
                }
                else
                {
                    isStaging      = false;
                    stagingChecked = true;
                }
            }
        }
    }
}
