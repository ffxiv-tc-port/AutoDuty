namespace AutoDuty.Multibox;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows;
using ECommons;
using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.PartyFunctions;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Helpers;
using Lumina.Excel.Sheets;

/// <summary>
/// 多開協調(Multibox)。移植自上游 erdelf/AutoDuty 的 AutoDuty/Multibox/MultiboxUtility.cs。
///
/// 🔴 紅線:一律手動觸發、預設關、沒有任何自動啟動點。
///    <see cref="MultiboxConfiguration.MultiBox"/> 標了 [JsonIgnore] ⇒ **永遠不會寫進設定檔**,
///    所以每次載入外掛都是關的,不存在「上次開著這次自動接上」的路徑。唯一的開關是
///    ConfigTab 裡使用者自己按的核取方塊。
///
/// 📌 與上游的差異(本 fork 結構較舊,不是照抄):
///  1. 上游 Plugin 的成員是 indexer/action 小寫,本 fork 是 Indexer/Action,且 InDungeon
///     是實例成員 ⇒ 一律走 Plugin 這個靜態單例。
///  2. 上游用 Newtonsoft(ConfigurationMain.JsonSerializerSettings)序列化路徑,本 fork 的
///     PathAction 掛的是 System.Text.Json 的 [JsonPropertyName] ⇒ 改用 BuildTab.jsonSerializerOptions,
///     否則欄名對不上會靜默序列化成空物件。
///  3. 上游 Stage 列舉有 Idle = 11,本 fork 沒有。上游的 `Stage = Idle; Stage = Reading_Path;`
///     在本 fork 的 Stage setter 裡兩者都不命中任何 case ⇒ 等價於直接設 Reading_Path。
///     🔴 **不可以拿 Stage.Stopped 代替 Idle** —— 本 fork 的 setter 對 Stopped 會呼叫
///     StopAndResetALL(),那會直接把整趟跑停掉。
///  4. 本 pin 的 CStringPointer **沒有** string → CStringPointer 的隱式轉換(只有反向),
///     上游的 InviteToParty(cid, client.CName, worldId) 在這裡編不過 ⇒ 自行 marshal 成 byte*。
///  5. InfoProxyPartyInvite.Instance() 一律判空後才解參考(上游直接解)。
///  6. World 查表改用 TryGetRow:WorldId 來自連線對端,是不可信輸入,GetRow 查無此列會擲
///     ArgumentOutOfRangeException。
/// </summary>
public static class MultiboxUtility
{
    public class MultiboxConfiguration
    {
        // 🔴 [JsonIgnore]:多開開關是「執行期狀態」不是「設定」,絕不落地。
        // 這正是「預設關且無自動啟動點」的保證 —— 外掛每次載入都是 false。
        private bool multiBox = false;

        [Newtonsoft.Json.JsonIgnore]
        public bool MultiBox
        {
            get => this.multiBox;
            set
            {
                if (this.multiBox == value)
                    return;
                this.multiBox = value;

                Set(this.multiBox);
            }
        }

        public bool          SynchronizePath { get; set; } = true;
        public bool          Host            { get; set; } = false;
        public string        PipeName        { get; set; } = "AutoDutyPipe";
        public string        ServerName      { get; set; } = ".";
        public TransportType TransportType   { get; set; } = TransportType.NamedPipe;
        public string        ServerAddress   { get; set; } = "127.0.0.1";
        public int           ServerPort      { get; set; } = 1716;
    }

    public static MultiboxConfiguration Config => ConfigurationMain.Instance.multibox;

    private const string SERVER_AUTH_KEY = "AD_Server_Auth!";
    private const string CLIENT_AUTH_KEY = "AD_Client_Auth!";
    private const string CLIENT_CID_KEY  = "CLIENT_CID";
    private const string PARTY_INVITE    = "PARTY_INVITE";

    private const string KEEPALIVE_KEY          = "KEEP_ALIVE";
    private const string KEEPALIVE_RESPONSE_KEY = "KEEP_ALIVE received";

    private const string DUTY_QUEUE_KEY = "DUTY_QUEUE";
    private const string DUTY_EXIT_KEY  = "DUTY_EXIT";

    private const string DEATH_KEY       = "DEATH";
    private const string UNDEATH_KEY     = "UNDEATH";
    private const string DEATH_RESET_KEY = "DEATH_RESET";

    private const string PATH_STEPS = "PATH_STEPS";

    private const string STEP_COMPLETED = "STEP_COMPLETED";
    private const string STEP_START     = "STEP_START";

    internal static bool stepBlock = false;

    public static bool MultiboxBlockingNextStep
    {
        get
        {
            if (!Config.MultiBox)
                return false;

            return stepBlock;
        }
        set
        {
            DebugLog($"blocking step: {stepBlock} to {value}");
            if (!Config.MultiBox)
                return;

            if (!value)
                if (Config.Host)
                    Server.SendStepStart();

            if (stepBlock == value)
                return;

            stepBlock = value;

            if (stepBlock)
                if (Config.Host)
                {
                    Plugin.Action = "Waiting for clients";
                    Server.CheckStepProgress();
                }
                else
                {
                    Client.SendStepCompleted();
                }
        }
    }

    public static void IsDead(bool dead)
    {
        // 🔴 與上游不同:上游這裡寫的是 `if (Config.MultiBox) return;`,那會讓死亡同步在
        // 多開「開啟」時整個失效(明顯是筆誤,判斷方向反了)。這裡改成未開啟才 return。
        if (!Config.MultiBox)
            return;

        if (!Config.Host)
            Client.SendDeath(dead);
        else
            Server.CheckDeaths();
    }

    public static void Set(bool on)
    {
        if (on)
            ConfigurationMain.Instance.GetCurrentConfig.DutyModeEnum = DutyMode.Regular;

        if (Config.Host)
            Server.Set(on);
        else
            Client.Set(on);
    }

    internal static class Server
    {
        public const             int             MAX_SERVERS   = 3;
        private static readonly  StreamString?[] streams       = new StreamString?[MAX_SERVERS];
        internal static readonly ClientInfo?[]   clients       = new ClientInfo?[MAX_SERVERS];
        private static readonly  Queue<string>[] messageQueues = [new(), new(), new()];

        internal static readonly DateTime[] keepAlives    = new DateTime[MAX_SERVERS];
        internal static readonly bool[]     stepConfirms  = new bool[MAX_SERVERS];
        private static readonly  bool[]     deathConfirms = new bool[MAX_SERVERS];

        private static ITransport?              transport;
        private static CancellationTokenSource? serverCts;

        /// <summary>伺服器是否真的起來了(供 UI 顯示,不參與任何決策)。</summary>
        internal static bool Running => transport != null;

        /// <summary>目前已認證連上的用戶端數量(供 UI 顯示)。</summary>
        internal static int ConnectedCount => clients.Count(c => c != null);

        public static void Set(bool on)
        {
            try
            {
                if (on)
                    StartServer();
                else
                    StopServer();
            }
            catch (Exception ex)
            {
                ErrorLog(ex.ToString());
            }
        }

        private static void StartServer()
        {
            try
            {
                if (transport != null) return;

                transport = Config.TransportType switch
                {
                    TransportType.NamedPipe => new NamedPipeTransport(Config.PipeName),
                    TransportType.Tcp       => new TcpTransport(Config.ServerPort),
                    _                       => throw new NotImplementedException(Config.TransportType.ToString()),
                };

                transport.StartServer(MAX_SERVERS);
                serverCts = new CancellationTokenSource();
                Task.Run(() => AcceptLoop(serverCts.Token), serverCts.Token);
                Svc.Log.Information($"[Multibox] 主機端已啟動,傳輸方式 {Config.TransportType}");
            }
            catch (Exception ex)
            {
                ErrorLog($"StartServer error: {ex}");
            }
        }

        private static void StopServer()
        {
            try
            {
                serverCts?.Cancel();
                transport?.StopServer();
                transport?.Dispose();
                transport = null;
                serverCts = null;

                for (int i = 0; i < MAX_SERVERS; i++)
                {
                    streams[i] = null;
                    clients[i] = null;
                    messageQueues[i].Clear();
                    keepAlives[i]   = DateTime.MinValue;
                    stepConfirms[i] = false;
                }

                if (Plugin is { InDungeon: false })
                {
                    Chat.ExecuteCommand("/partycmd breakup");

                    SchedulerHelper.ScheduleAction("MultiboxServer PartyBreakup Accept", () =>
                                                                                         {
                                                                                             unsafe
                                                                                             {
                                                                                                 InfoProxyPartyInvite* invite = InfoProxyPartyInvite.Instance();
                                                                                                 if (invite == null)
                                                                                                 {
                                                                                                     SchedulerHelper.DescheduleAction("MultiboxServer PartyBreakup Accept");
                                                                                                     return;
                                                                                                 }

                                                                                                 Utf8String inviterName = invite->InviterName;

                                                                                                 if (UniversalParty.Length <= 1)
                                                                                                 {
                                                                                                     SchedulerHelper.DescheduleAction("MultiboxServer PartyBreakup Accept");
                                                                                                     return;
                                                                                                 }

                                                                                                 if (GenericHelpers.TryGetAddonByName("SelectYesno", out AtkUnitBase* addonSelectYesno) &&
                                                                                                     GenericHelpers.IsAddonReady(addonSelectYesno))
                                                                                                 {
                                                                                                     AddonMaster.SelectYesno yesno = new(addonSelectYesno);
                                                                                                     if (yesno.Text.Contains(inviterName.ToString()))
                                                                                                         yesno.Yes();
                                                                                                     else
                                                                                                         yesno.No();
                                                                                                 }

                                                                                                 if (GenericHelpers.TryGetAddonByName("Social", out AtkUnitBase* addonSocial) &&
                                                                                                     GenericHelpers.IsAddonReady(addonSocial))
                                                                                                 {
                                                                                                     ErrorLog("/partycmd breakup opened the party menu instead");
                                                                                                     SchedulerHelper.DescheduleAction("MultiboxServer PartyBreakup Accept");
                                                                                                     return;
                                                                                                 }
                                                                                             }
                                                                                         }, 500, false);
                }

                Svc.Log.Information("[Multibox] 主機端已停止");
            }
            catch (Exception ex)
            {
                ErrorLog($"StopServer error: {ex}");
            }
        }

        private static async void AcceptLoop(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    Stream s   = await transport!.AcceptConnectionAsync(ct);
                    int    idx = -1;
                    for (int i = 0; i < MAX_SERVERS; i++)
                    {
                        if (streams[i] == null)
                        {
                            idx = i;
                            break;
                        }
                    }

                    if (idx == -1)
                    {
                        try
                        {
                            await s.DisposeAsync();
                        }
                        catch (Exception ex)
                        {
                            ErrorLog(ex.ToString());
                        }

                        continue;
                    }

                    streams[idx] = new StreamString(s);

                    int capturedIdx = idx;
                    _ = Task.Run(() => ConnectionHandler(s, capturedIdx, ct), ct);
                }
            }
            catch (OperationCanceledException)
            {
                DebugLog("AcceptLoop ended due to cancellation");
            }
            catch (Exception ex)
            {
                ErrorLog($"AcceptLoop error: {ex}");
            }
        }

        private static async void ConnectionHandler(Stream stream, int index, CancellationToken ct)
        {
            try
            {
                await using Stream s = stream;
                if (streams[index] == null)
                    return;
                StreamString ss = streams[index]!;
                ss.WriteString(SERVER_AUTH_KEY);
                if (ss.ReadString() != CLIENT_AUTH_KEY)
                    return;

                Svc.Log.Information($"[Multibox] 用戶端 {index} 已通過認證");
                keepAlives[index] = DateTime.Now;
                Task sendTask = Task.Run(async () => await ServerSendThread(index, ct), ct);

                while (!ct.IsCancellationRequested && !sendTask.IsCompleted)
                {
                    await Task.Delay(100, ct);
                    string   message = ss.ReadString().Trim();
                    string[] split   = message.Split("|");

                    switch (split[0])
                    {
                        case "" when message.Length == 0:
                            DebugLog($"Client {index} closed the connection.");
                            return;
                        case CLIENT_CID_KEY:
                            // 對端送來的欄位是不可信輸入,格式不對就丟掉不要讓整條連線炸掉。
                            if (split.Length < 4                          ||
                                !ulong.TryParse(split[1], out ulong cid)  ||
                                !ushort.TryParse(split[3], out ushort wid))
                            {
                                ErrorLog($"Malformed {CLIENT_CID_KEY} from {index}: {message}");
                                break;
                            }

                            clients[index] = new ClientInfo(cid, split[2], wid);

                            _ = Svc.Framework.RunOnTick(() =>
                                                        {
                                                            unsafe
                                                            {
                                                                ClientInfo? client = clients[index];
                                                                if (client == null)
                                                                    return;

                                                                Svc.Log.Information($"[Multibox] 收到用戶端識別:{client.CID} {client.CName} {client.WorldId}");

                                                                if (!PartyHelper.IsPartyMember(client.CID))
                                                                {
                                                                    InfoProxyPartyInvite* invite = InfoProxyPartyInvite.Instance();
                                                                    if (invite == null)
                                                                    {
                                                                        ErrorLog("InfoProxyPartyInvite unavailable, skipping invite");
                                                                        return;
                                                                    }

                                                                    // 📌 本 pin 的 ECommons 是 Player.CurrentWorldId(uint),
                                                                    // 不是上游較新版的 Player.CurrentWorld.RowId。
                                                                    if (client.WorldId == Player.CurrentWorldId)
                                                                    {
                                                                        // 📌 本 pin 的 CStringPointer 沒有 string 的隱式轉換,
                                                                        // 必須自己 marshal 成 NUL 結尾的 byte*。
                                                                        byte[] nameBytes = Encoding.UTF8.GetBytes(client.CName + "\0");
                                                                        fixed (byte* pName = nameBytes)
                                                                            invite->InviteToParty(client.CID, pName, client.WorldId);
                                                                    }
                                                                    else
                                                                    {
                                                                        invite->InviteToPartyContentId(client.CID, 0);
                                                                    }

                                                                    ss.WriteString(PARTY_INVITE);
                                                                }

                                                                stepConfirms[index] = false;
                                                            }
                                                        }, cancellationToken: ct);
                            break;
                        case KEEPALIVE_KEY:
                            ss.WriteString(KEEPALIVE_RESPONSE_KEY);
                            break;
                        case KEEPALIVE_RESPONSE_KEY:
                            break;
                        case STEP_COMPLETED:
                            stepConfirms[index] = true;
                            CheckStepProgress();
                            break;
                        case DEATH_KEY:
                            deathConfirms[index] = true;
                            CheckDeaths();
                            break;
                        case UNDEATH_KEY:
                            deathConfirms[index] = false;
                            break;
                        default:
                            ss.WriteString($"Unknown Message from {index}: {message}");
                            continue;
                    }

                    keepAlives[index] = DateTime.Now;
                }
            }
            catch (OperationCanceledException)
            {
                DebugLog($"Connection handler ended due to cancellation {index}");
            }
            catch (Exception e)
            {
                ErrorLog($"ConnectionHandler error {index}: {e.Message}\n{e.StackTrace}");
            }
            finally
            {
                streams[index] = null;
                clients[index] = null;
            }
        }

        private static async Task ServerSendThread(int index, CancellationToken ct)
        {
            try
            {
                DebugLog("SEND Initialized with " + index);

                while (!ct.IsCancellationRequested && streams[index] != null)
                {
                    if (messageQueues[index].Count > 0)
                    {
                        string message = messageQueues[index].Dequeue();
                        streams[index]?.WriteString(message);
                    }
                    else if ((DateTime.Now - keepAlives[index]).TotalSeconds > 15)
                    {
                        // if no messages to send and the connection is stale, send a keepalive to check.
                        // (Usually this is the clients job but the tcp socket doesn't die immediately)
                        streams[index]?.WriteString(KEEPALIVE_KEY);
                        await Task.Delay(1000, ct);
                    }

                    await Task.Delay(100, ct);
                }
            }
            catch (OperationCanceledException)
            {
                DebugLog($"SendLoop ended due to cancellation for {index}");
            }
            catch (Exception e)
            {
                ErrorLog($"SERVER SEND ERROR for {index}: " + e);
            }
        }

        public static bool AllInParty()
        {
            for (int i = 0; i < MAX_SERVERS; i++)
            {
                if (clients[i] == null || !PartyHelper.IsPartyMember(clients[i]!.CID))
                    return false;
            }

            return true;
        }

        public static void CheckDeaths()
        {
            if (deathConfirms.All(x => x) && Player.IsDead)
            {
                for (int i = 0; i < deathConfirms.Length; i++)
                    deathConfirms[i] = false;

                DebugLog("All dead");
                SendToAllClients(DEATH_RESET_KEY);
            }
            else
            {
                DebugLog("Not all clients are dead yet, waiting for more death.");
            }
        }

        public static void CheckStepProgress()
        {
            if ((Plugin.Stage != Stage.Looping && Plugin.Indexer >= 0 && Plugin.Indexer < Plugin.Actions.Count && Plugin.Actions[Plugin.Indexer].Tag == ActionTag.Treasure || stepConfirms.All(x => x)) && stepBlock)
            {
                for (int i = 0; i < stepConfirms.Length; i++)
                    stepConfirms[i] = false;

                DebugLog("All clients completed the step");
                stepBlock = false;
            }
            else
            {
                DebugLog("Not all clients have completed the step yet, waiting for more confirmations.");
            }
        }

        public static void SendStepStart()
        {
            DebugLog("Synchronizing Clients to Server step");
            SendToAllClients($"{STEP_START}|{Plugin.Indexer}");
        }

        public static void ExitDuty()
        {
            DebugLog("exiting duty");
            SendToAllClients(DUTY_EXIT_KEY);
            for (int i = 0; i < stepConfirms.Length; i++)
                stepConfirms[i] = false;
        }

        public static void Queue()
        {
            DebugLog("Queue initiated");
            SendToAllClients(DUTY_QUEUE_KEY);
            for (int i = 0; i < stepConfirms.Length; i++)
                stepConfirms[i] = false;
            stepBlock = false;
        }

        // 📌 本 fork 的 PathAction 掛的是 System.Text.Json 的 [JsonPropertyName],
        // 用 Newtonsoft 序列化欄名會對不上 ⇒ 一律走 BuildTab.jsonSerializerOptions。
        public static void SendPath() =>
            SendToAllClients($"{PATH_STEPS}|{JsonSerializer.Serialize(Plugin.Actions, BuildTab.jsonSerializerOptions)}");

        private static void SendToAllClients(string message)
        {
            DebugLog("Enqueuing to send: " + message);
            foreach (Queue<string> queue in messageQueues)
                queue.Enqueue(message);
        }

        internal record ClientInfo(ulong CID, string CName, ushort WorldId)
        {
            private string? world;

            // 🔴 WorldId 來自連線對端(不可信),GetRow 查無此列會擲 ArgumentOutOfRangeException
            // ⇒ 一律 TryGetRow。
            public string World =>
                this.world ??= Svc.Data.Excel.GetSheet<World>().TryGetRow(this.WorldId, out World row)
                                   ? row.Name.GetText()
                                   : $"#{this.WorldId}";
        }
    }

    internal static class Client
    {
        private static StreamString?            clientSS;
        private static CancellationTokenSource? clientCts;

        /// <summary>用戶端是否已建立串流(供 UI 顯示,不參與任何決策)。</summary>
        internal static bool Connected => clientSS != null;

        /// <summary>已按下開關但還沒連上(供 UI 區分「連線中」與「未啟用」)。</summary>
        internal static bool Connecting => clientCts != null && clientSS == null;

        public static void Set(bool on)
        {
            if (on)
            {
                clientCts = new CancellationTokenSource();
                Task.Run(() => ClientConnectionThread(clientCts.Token), clientCts.Token);
            }
            else
            {
                try
                {
                    clientCts?.Cancel();
                }
                catch (Exception ex)
                {
                    ErrorLog(ex.ToString());
                }

                clientSS  = null;
                clientCts = null;
            }
        }

        private static async void ClientConnectionThread(CancellationToken ct)
        {
            try
            {
                using ITransport transport = Config.TransportType switch
                {
                    TransportType.NamedPipe => new NamedPipeTransport(Config.PipeName, Config.ServerName),
                    TransportType.Tcp       => new TcpTransport(Config.ServerAddress, Config.ServerPort),
                    _                       => throw new NotImplementedException(Config.TransportType.ToString()),
                };

                Svc.Log.Information($"[Multibox] 連線至主機端({Config.TransportType})...");
                await using Stream clientStream = await transport.ConnectToServerAsync(ct);

                clientSS = new StreamString(clientStream);

                if (clientSS.ReadString() == SERVER_AUTH_KEY)
                {
                    clientSS.WriteString(CLIENT_AUTH_KEY);

                    _ = Svc.Framework.RunOnTick(() =>
                                                {
                                                    if (Player.CID != 0)
                                                        clientSS.WriteString($"{CLIENT_CID_KEY}|{Player.CID}|{Player.Name}|{Player.CurrentWorldId}");
                                                }, cancellationToken: ct);

                    _ = Task.Run(() => ClientKeepAliveThread(ct), ct);
                    while (!ct.IsCancellationRequested)
                    {
                        string   message = clientSS.ReadString().Trim();
                        string[] split   = message.Split("|");

                        switch (split[0])
                        {
                            case "" when message.Length == 0:
                                DebugLog("Server closed the connection.");
                                return;
                            case STEP_START:
                                if (split.Length > 1 && int.TryParse(split[1], out int step))
                                {
                                    Plugin.Indexer = step;
                                    stepBlock      = false;
                                    // 📌 上游這裡是 `Stage = Idle; Stage = Reading_Path;`。本 fork 沒有
                                    // Stage.Idle,而兩者在本 fork 的 setter 都不命中任何 case ⇒ 直接設
                                    // Reading_Path 等價。🔴 不可改用 Stage.Stopped(會 StopAndResetALL)。
                                    Plugin.Stage = Stage.Reading_Path;
                                }

                                break;
                            case KEEPALIVE_KEY:
                                clientSS.WriteString(KEEPALIVE_RESPONSE_KEY);
                                break;
                            case KEEPALIVE_RESPONSE_KEY:
                                break;
                            case DUTY_QUEUE_KEY:
                                QueueHelper.InvokeAcceptOnly();
                                break;
                            case DUTY_EXIT_KEY:
                                stepBlock = false;
                                ExitDutyHelper.Invoke();
                                break;
                            case PARTY_INVITE:
                                SchedulerHelper.ScheduleAction("MultiboxClient PartyInvite Accept", () =>
                                                                                                    {
                                                                                                        unsafe
                                                                                                        {
                                                                                                            if (UniversalParty.Length > 1)
                                                                                                            {
                                                                                                                PartyHelper.LeaveParty();
                                                                                                                return;
                                                                                                            }

                                                                                                            InfoProxyPartyInvite* invite = InfoProxyPartyInvite.Instance();
                                                                                                            if (invite == null)
                                                                                                                return;

                                                                                                            Utf8String inviterName = invite->InviterName;
                                                                                                            if (invite->InviterWorldId != 0                                                    &&
                                                                                                                UniversalParty.Length <= 1                                                     &&
                                                                                                                GenericHelpers.TryGetAddonByName("SelectYesno", out AtkUnitBase* addonSelectYesno) &&
                                                                                                                GenericHelpers.IsAddonReady(addonSelectYesno))
                                                                                                            {
                                                                                                                AddonMaster.SelectYesno yesno = new(addonSelectYesno);
                                                                                                                if (yesno.Text.Contains(inviterName.ToString()))
                                                                                                                {
                                                                                                                    yesno.Yes();
                                                                                                                    SchedulerHelper.DescheduleAction("MultiboxClient PartyInvite Accept");
                                                                                                                }
                                                                                                                else
                                                                                                                {
                                                                                                                    yesno.No();
                                                                                                                }
                                                                                                            }
                                                                                                        }
                                                                                                    }, 500, false);
                                break;
                            case PATH_STEPS:
                                List<PathAction>? steps = JsonSerializer.Deserialize<List<PathAction>>(message[(split[0].Length + 1)..], BuildTab.jsonSerializerOptions);
                                if (steps is { Count: > 0 })
                                {
                                    DebugLog("setting steps from host");
                                    Plugin.Actions = steps;
                                }

                                break;
                            default:
                                ErrorLog("Unknown response: " + message);
                                break;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                DebugLog("ClientConnection ended due to cancellation");
            }
            catch (Exception e)
            {
                ErrorLog($"Client ERROR: {e.Message}\n{e.StackTrace}");
            }
            finally
            {
                Config.MultiBox = false;
            }
        }

        private static async void ClientKeepAliveThread(CancellationToken ct)
        {
            try
            {
                await Task.Delay(1000, ct);
                while (!ct.IsCancellationRequested && clientSS != null)
                {
                    clientSS?.WriteString(KEEPALIVE_KEY);
                    await Task.Delay(10000, ct);
                }
            }
            catch (OperationCanceledException)
            {
                DebugLog("ClientKeepalive ended due to cancellation");
            }
            catch (Exception e)
            {
                ErrorLog("Client KEEPALIVE Error: " + e);
            }
        }

        public static void SendStepCompleted()
        {
            if (clientSS == null)
            {
                DebugLog("Client not connected, cannot send step completed.");
                return;
            }

            Plugin.Action = "Waiting for others";
            clientSS.WriteString(STEP_COMPLETED);
            DebugLog("Step completed sent to server.");
        }

        public static void SendDeath(bool dead)
        {
            if (clientSS == null)
            {
                DebugLog("Client not connected, cannot send death.");
                return;
            }

            clientSS.WriteString(dead ? DEATH_KEY : UNDEATH_KEY);
            DebugLog("Death sent to server.");
        }
    }

    private static void DebugLog(string message) =>
        Svc.Log.Debug($"Pipe Connection: {message}");

    private static void ErrorLog(string message) =>
        Svc.Log.Error($"Pipe Connection: {message}");

    private class StreamString(Stream ioStream)
    {
        private readonly UnicodeEncoding streamEncoding = new();

        public string ReadString()
        {
            int b1 = ioStream.ReadByte();
            int b2 = ioStream.ReadByte();

            if (b1 == -1 || b2 == -1)
            {
                DebugLog("End of stream reached.");
                return string.Empty;
            }

            int    len      = b1 * 256 + b2;
            byte[] inBuffer = new byte[len];
            int    n        = 0;
            while (n < len)
            {
                int c = ioStream.Read(inBuffer, n, len - n);
                if (c == 0)
                {
                    ErrorLog("Stream closed unexpectedly");
                    return string.Empty;
                }

                n += c;
            }

            string readString = this.streamEncoding.GetString(inBuffer);

            DebugLog("Reading: " + readString);
            return readString;
        }

        public int WriteString(string outString)
        {
            DebugLog("Writing: " + outString);

            byte[] outBuffer = this.streamEncoding.GetBytes(outString);
            int    len       = outBuffer.Length;
            if (len > ushort.MaxValue)
                throw new ArgumentException("String too long to write to stream");
            ioStream.WriteByte((byte)(len / 256));
            ioStream.WriteByte((byte)(len & 255));
            ioStream.Write(outBuffer, 0, len);
            ioStream.Flush();

            return outBuffer.Length + 2;
        }
    }
}
