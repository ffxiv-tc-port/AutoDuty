using AutoDuty.Windows;
using ECommons.DalamudServices;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Networking.Http;

namespace AutoDuty.Updater
{
    internal static class GitHubHelper
    {
        const string CLIENT_ID = "Iv23liWV5R21nasKaQjP";

        /// <summary>
        /// 副本路徑檔與其 MD5 清單的來源。
        /// <para>
        /// 🔴 路徑檔**不隨外掛出貨** —— <c>Plugin.PathsDirectory</c> 是
        /// <c>&lt;pluginConfigs&gt;/AutoDuty/paths</c>,啟動時只會被建成空目錄,
        /// 內容唯一的來源就是「路徑」分頁那顆手動更新按鈕從這個位址下載。
        /// 也就是說 <b>這個常數決定了使用者實際跑的是誰的路徑資料</b>。
        /// </para>
        /// <para>
        /// 指向本 fork 的 tc-7.20:別的來源列的是國際服的路徑檔。
        /// </para>
        /// </summary>
        internal const string PathRepoBaseUrl = "https://raw.githubusercontent.com/ffxiv-tc-port/AutoDuty/refs/heads/tc-7.20/";

        // OAuth 裝置流程打的是 github.com/login/*,不是本 org 的 raw/api,
        // 認證用戶端對它沒有作用 ⇒ 維持原本的一般用戶端。
        private static readonly SocketsHttpHandler _oauthHandler = new() { AutomaticDecompression = DecompressionMethods.All, ConnectCallback = new HappyEyeballsCallback().ConnectCallback };

        private static readonly HttpClient _oauthClient = new(_oauthHandler) { Timeout = TimeSpan.FromSeconds(20) };

        // 抓 ffxiv-tc-port 的 raw/api 一律走這個:它的 handler 會帶上使用者在 Dalamud
        // 設定的 GitHub token,org 轉 private 之後路徑檔與 md5s 才抓得到。
        // 契約:DefaultRequestHeaders 要保持乾淨 —— 呼叫端自己設的 Authorization
        // 會蓋掉 token,所以逐次請求的標頭一律掛在 HttpRequestMessage 上。
        private static readonly Lazy<HttpClient> _gitHubClient = new(() =>
        {
            HttpClient client = Svc.PluginInterface.CreateGitHubHttpClient();
            client.Timeout = TimeSpan.FromSeconds(20);
            return client;
        });

        private static int _loggedFirstSuccess;

        private static void LogFirstSuccess(string url)
        {
            if (Interlocked.Exchange(ref _loggedFirstSuccess, 1) == 0)
                Svc.Log.Information($"GitHub authenticated client: first successful request to {new Uri(url).Host}");
        }

        internal static async Task<bool> DownloadFileAsync(string url, string localPath)
        {
            try
            {
                HttpResponseMessage response = await _gitHubClient.Value.GetAsync(url);
                response.EnsureSuccessStatusCode();
                LogFirstSuccess(url);

                string content = await response.Content.ReadAsStringAsync();
                await File.WriteAllTextAsync(localPath, content);
                return true;
            }
            catch (Exception ex)
            {
                Svc.Log.Error($"{ex}");
                return false;
            }
        }

        internal static async Task<Dictionary<string, string>?> GetPathFileListAsync()
        {
            try
            {
                // 🔴 指向本 fork:別的來源列的是國際服的路徑檔,
                //    會把我方為台服客製過的路徑靜默覆蓋掉。
                string md5Url = PathRepoBaseUrl + "AutoDuty/Resources/md5s.json";
                var md5List = await _gitHubClient.Value.GetFromJsonAsync<Dictionary<string, string>>(md5Url);
                LogFirstSuccess(md5Url);
                return md5List ?? [];
            }
            catch (Exception ex)
            {
                Svc.Log.Error($"{ex}");
                return null;
            }
        }

        internal static async Task<UserCode?> GetUserCode()
        {
            try
            {
                var uri = new Uri("https://github.com/login/device/code");
                var parameters = new FormUrlEncodedContent([new KeyValuePair<string, string>("client_id", CLIENT_ID)]);
                if (!_oauthClient.DefaultRequestHeaders.Accept.Any(h => h.MediaType == "application/json"))
                    _oauthClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                var response = await _oauthClient.PostAsync(uri, parameters);
                var jsonString = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<UserCode>(jsonString, BuildTab.jsonSerializerOptions);
            }
            catch (Exception ex)
            {
                Svc.Log.Error($"{ex}");
                return null;
            }
        }

        internal static async Task<PollResponseClass?> PollResponse(UserCode userCode)
        {
            try
            {
                var uri = new Uri("https://github.com/login/oauth/access_token");
                var parameters = new FormUrlEncodedContent(
                [
                    new KeyValuePair<string, string>("client_id", CLIENT_ID),
                    new KeyValuePair<string, string>("device_code", userCode.Device_Code),
                    new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:device_code")
                ]);
                if (!_oauthClient.DefaultRequestHeaders.Accept.Any(h => h.MediaType == "application/json"))
                    _oauthClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                var response = await _oauthClient.PostAsync(uri, parameters);
                var jsonString = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<PollResponseClass>(jsonString, BuildTab.jsonSerializerOptions);
            }
            catch (Exception ex)
            {
                Svc.Log.Error($"{ex}");
                return null;
            }
        }

        internal static async Task<string?> FileIssue(string title, string whatHappened, string reproSteps, string accessToken)
        {
            try
            {
                var body = $"What Happened?\n\n{whatHappened}\n\nVersion Number\n\n{GitHubIssue.Version}\n\nSteps to reproduce the error\n\n{reproSteps}\n\nRelevant log output\n\n{GitHubIssue.LogFile}\n\nOther relevant plugins installed\n\n{GitHubIssue.InstalledPlugins}\n\nConfig file\n\n{GitHubIssue.ConfigFile}";

                var issue = new GitHubIssue()
                {
                    Title = title,
                    Body = body
                };

                var json = JsonSerializer.Serialize(issue, BuildTab.jsonSerializerOptions);
                Svc.Log.Info(json);
                var content = new StringContent(json, Encoding.UTF8, "application/vnd.github+json");

                // 🔴 指向本 fork 的 issue,不送去上游。
                var url = $"https://api.github.com/repos/ffxiv-tc-port/AutoDuty/issues";

                // 標頭掛在請求上而不是 client 上:使用者的 OAuth token 若留在
                // DefaultRequestHeaders,之後抓路徑檔時會蓋掉 Dalamud 帶的 token。
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                request.Headers.UserAgent.ParseAdd("AutoDuty");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

                var response = await _gitHubClient.Value.SendAsync(request);
                if (response.IsSuccessStatusCode)
                    LogFirstSuccess(url);

                var responseString = await response.Content.ReadAsStringAsync();
                return responseString;
            }
            catch (Exception ex)
            {
                Svc.Log.Error($"{ex}");
                return null;
            }
        }

        internal static void Dispose()
        {
            _oauthClient.Dispose();
            if (_gitHubClient.IsValueCreated)
                _gitHubClient.Value.Dispose();
        }
    }
}
