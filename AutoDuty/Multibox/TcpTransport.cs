using System.Net;
using System.Net.Sockets;

namespace AutoDuty.Multibox
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using ECommons.DalamudServices;

    public sealed class TcpTransport : ITransport
    {
        private readonly IPAddress    address;
        private readonly int          port;
        private          TcpListener? listener;

        public TcpTransport(string address, int port)
        {
            this.address = IPAddress.Parse(address);
            this.port    = port;
        }

        private TcpTransport(IPAddress address, int port)
        {
            this.address = address;
            this.port    = port;
        }

        // 主機端只綁設定的位址，不監聽所有介面；要收其他電腦的連線須明確填本機的區網位址。
        public static TcpTransport ForServer(string? listenAddress, int port)
        {
            if (!IPAddress.TryParse(listenAddress?.Trim(), out IPAddress? parsed))
            {
                Svc.Log.Warning($"[Multibox] Listen address \"{listenAddress}\" is not a valid IP address; listening on 127.0.0.1 only.");
                parsed = IPAddress.Loopback;
            }

            return new TcpTransport(parsed, port);
        }

        public string Endpoint => $"{this.address}:{this.port}";

        public void StartServer(int backlog = 3)
        {
            if (this.listener != null)
                return;
            this.listener = new TcpListener(this.address, this.port);
            this.listener.Start(backlog);
        }

        public void StopServer()
        {
            try
            {
                this.listener?.Stop();
            }
            catch (Exception ex)
            {
                DebugLog("Error during tcp socket closure: " + ex);
            }

            this.listener = null;
        }

        public async Task<Stream> AcceptConnectionAsync(CancellationToken ct)
        {
            if (this.listener == null)
                throw new InvalidOperationException("Listener not started");
            TcpClient client = await this.listener.AcceptTcpClientAsync(ct);
            client.NoDelay = true;
            ct.Register(client.Dispose);
            return client.GetStream();
        }

        public async Task<Stream> ConnectToServerAsync(CancellationToken ct)
        {
            TcpClient client      = new();
            ValueTask connectTask = client.ConnectAsync(this.address, this.port, ct);
            await using (ct.Register(() =>
                                     {
                                         try
                                         {
                                             client.Close();
                                         }
                                         catch (Exception ex)
                                         {
                                             DebugLog("Error during tcp socket closure on cancel: " + ex);
                                         }
                                     }))
                await connectTask;

            client.NoDelay = true;
            ct.Register(client.Dispose);
            return client.GetStream();
        }

        public void Dispose() =>
            this.StopServer();

        private static void DebugLog(string message) =>
            Svc.Log.Debug($"TCP Connection: {message}");
    }
}
