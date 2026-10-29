using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Oxide.Core.Configuration;
using Oxide.Core.Libraries.Covalence;

namespace Oxide.Core.RemoteConsole
{
    public class RemoteConsole
    {
        #region Initialization

        private const int MaxHandshakeSize = 8192;
        private const int MaxMessageSize = 64 * 1024;

        private readonly Covalence covalence = Interface.Oxide.GetLibrary<Covalence>();
        private readonly OxideConfig.OxideRcon config = Interface.Oxide.Config.Rcon;

        private readonly ConcurrentDictionary<RconConnection, byte> connections = new ConcurrentDictionary<RconConnection, byte>();
        private TcpListener? server;
        private CancellationTokenSource? cancellation;

        /// <summary>
        /// Initalizes the RCON server
        /// </summary>
        public void Initalize()
        {
            if (!config.Enabled || server != null)
            {
                return;
            }

            if (string.IsNullOrEmpty(config.Password))
            {
                Interface.Oxide.LogWarning("[Rcon] Remote console password is not set, disabling");
                return;
            }

            try
            {
                server = new TcpListener(IPAddress.Any, config.Port);
                server.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                server.Start();
                cancellation = new CancellationTokenSource();
                _ = AcceptConnections(server, cancellation.Token);

                Interface.Oxide.LogInfo($"[Rcon] Server started successfully on port {config.Port}");
            }
            catch (Exception ex)
            {
                server = null;
                Interface.Oxide.LogException($"[Rcon] Failed to start server on port {config.Port}", ex);
                RemoteLogger.Exception($"Failed to start RCON server on port {config.Port}", ex);
            }
        }

        /// <summary>
        /// Shuts down the RCON server
        /// </summary>
        public void Shutdown(string reason = "Server shutting down", WebSocketCloseStatus code = WebSocketCloseStatus.NormalClosure)
        {
            if (server == null)
            {
                return;
            }

            cancellation?.Cancel();
            server.Stop();
            server = null;

            foreach (RconConnection connection in connections.Keys)
            {
                connection.Close(code, reason);
            }

            connections.Clear();
            Interface.Oxide.LogInfo($"[Rcon] Service has stopped: {reason} ({code})");
        }

        #endregion Initialization

        #region Message Handling

        /// <summary>
        /// Broadcast a message to all connected clients
        /// </summary>
        /// <param name="message"></param>
        public void SendMessage(RemoteMessage message)
        {
            if (message != null && server != null && !connections.IsEmpty)
            {
                Broadcast(message.ToJSON());
            }
        }

        /// <summary>
        /// Broadcast a message to all connected clients
        /// </summary>
        /// <param name="message"></param>
        /// <param name="identifier"></param>
        public void SendMessage(string message, int identifier)
        {
            if (!string.IsNullOrEmpty(message) && server != null && !connections.IsEmpty)
            {
                Broadcast(RemoteMessage.CreateMessage(message, identifier).ToJSON());
            }
        }

        /// <summary>
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="message"></param>
        /// <param name="identifier"></param>
        public void SendMessage(RconConnection connection, string message, int identifier)
        {
            if (!string.IsNullOrEmpty(message) && server != null)
            {
                connection?.Send(RemoteMessage.CreateMessage(message, identifier).ToJSON());
            }
        }

        private void Broadcast(string text)
        {
            foreach (RconConnection connection in connections.Keys)
            {
                connection.Send(text);
            }
        }

        /// <summary>
        /// </summary>
        /// <param name="connection"></param>
        private void OnMessage(string text, RconConnection connection)
        {
            if (covalence == null)
            {
                Interface.Oxide.LogError("[Rcon] Failed to process command, Covalence is null");
                return;
            }

            RemoteMessage message = RemoteMessage.GetMessage(text);

            if (message == null)
            {
                Interface.Oxide.LogError("[Rcon] Failed to process command, RemoteMessage is null");
                return;
            }

            if (string.IsNullOrEmpty(message.Message))
            {
                Interface.Oxide.LogError("[Rcon] Failed to process command, RemoteMessage.Text is not set");
                return;
            }

            string[] fullCommand = CommandLine.Split(message.Message);
            string command = fullCommand[0].ToLower();
            string[] args = fullCommand.Skip(1).ToArray();

            if (Interface.CallHook("OnRconCommand", connection.EndPoint, command, args) != null)
            {
                return;
            }

            covalence.Server.Command(command, args);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RconPlayer
        {
            private string SteamID { get; }
            private string OwnerSteamID { get; }
            private string DisplayName { get; }
            private string Address { get; }
            private int Ping { get; }
            private int ConnectedSeconds { get; }
            private float VoiationLevel { get; } // Needed for Rust compatability
            private float CurrentLevel { get; } // Needed for Rust compatability
            private float UnspentXp { get; } // Needed for Rust compatability
            private float Health { get; } // Needed for Rust compatability

            public RconPlayer(IPlayer player)
            {
                SteamID = player.Id;
                OwnerSteamID = "0";
                DisplayName = player.Name;
                Address = player.Address;
                Ping = player.Ping;
                ConnectedSeconds = 0; // TODO: Implement when support is added
                VoiationLevel = 0.0f; // Needed for Rust compatability
                CurrentLevel = 0.0f; // Needed for Rust compatability
                UnspentXp = 0.0f; // Needed for Rust compatability
                Health = player.Health; // Needed for Rust compatability
            }
        }

        #endregion Message Handling

        #region Connections

        private async Task AcceptConnections(TcpListener listener, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (SocketException ex)
                {
                    Interface.Oxide.LogException("[Rcon] Failed to accept a connection", ex);
                    continue;
                }

                _ = Serve(client, cancellationToken);
            }
        }

        private async Task Serve(TcpClient client, CancellationToken cancellationToken)
        {
            IPEndPoint endPoint = (IPEndPoint)client.Client.RemoteEndPoint!;
            RconConnection? connection = null;
            try
            {
                NetworkStream stream = client.GetStream();
                using (CancellationTokenSource handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    if (!await Handshake(stream, handshakeTimeout.Token).ConfigureAwait(false))
                    {
                        return;
                    }
                }

                WebSocket socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
                {
                    IsServer = true,
                    KeepAliveInterval = TimeSpan.FromSeconds(30)
                });
                connection = new RconConnection(socket, endPoint);
                connections[connection] = 0;
                Interface.Oxide.LogInfo($"[Rcon] New connection from {endPoint.Address}");

                await Receive(connection, cancellationToken).ConfigureAwait(false);
                Interface.Oxide.LogInfo($"[Rcon] Connection from {endPoint.Address} closed: {socket.CloseStatusDescription ?? "Unknown"} ({socket.CloseStatus})");
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException)
            {
                Interface.Oxide.LogInfo($"[Rcon] Connection from {endPoint.Address} closed: {ex.Message}");
            }
            catch (Exception ex)
            {
                Interface.Oxide.LogException($"[Rcon] Connection from {endPoint.Address} failed", ex);
            }
            finally
            {
                if (connection != null)
                {
                    connections.TryRemove(connection, out _);
                }

                client.Dispose();
            }
        }

        private async Task Receive(RconConnection connection, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[4096];
            using MemoryStream message = new MemoryStream();
            while (connection.Socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await connection.Socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    connection.Close(WebSocketCloseStatus.NormalClosure, string.Empty);
                    return;
                }

                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageSize)
                {
                    connection.Close(WebSocketCloseStatus.MessageTooBig, "Message too big");
                    return;
                }

                if (!result.EndOfMessage)
                {
                    continue;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    Interface.Oxide.NextTick(() => OnMessage(text, connection));
                }

                message.SetLength(0);
            }
        }

        private async Task<bool> Handshake(NetworkStream stream, CancellationToken cancellationToken)
        {
            byte[] request = new byte[MaxHandshakeSize];
            int length = 0;
            while (length < 4 || request[length - 4] != '\r' || request[length - 3] != '\n' || request[length - 2] != '\r' || request[length - 1] != '\n')
            {
                if (length == request.Length || await stream.ReadAsync(request.AsMemory(length, 1), cancellationToken).ConfigureAwait(false) == 0)
                {
                    return false;
                }

                length++;
            }

            string[] lines = Encoding.ASCII.GetString(request, 0, length).Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            string[] requestLine = lines[0].Split(' ');
            Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0)
                {
                    headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
                }
            }

            if (requestLine.Length < 3 || requestLine[0] != "GET" || !headers.TryGetValue("Sec-WebSocket-Key", out string? key))
            {
                await Respond(stream, "400 Bad Request", cancellationToken).ConfigureAwait(false);
                return false;
            }

            byte[] path = Encoding.UTF8.GetBytes(Uri.UnescapeDataString(requestLine[1]));
            byte[] expected = Encoding.UTF8.GetBytes("/" + config.Password);
            if (!CryptographicOperations.FixedTimeEquals(path, expected))
            {
                await Respond(stream, "401 Unauthorized", cancellationToken).ConfigureAwait(false);
                return false;
            }

            string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await Respond(stream, $"101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}", cancellationToken).ConfigureAwait(false);
            return true;
        }

        private static Task Respond(NetworkStream stream, string status, CancellationToken cancellationToken) =>
            stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n\r\n"), cancellationToken).AsTask();

        public sealed class RconConnection
        {
            private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);

            public WebSocket Socket { get; }

            public IPEndPoint EndPoint { get; }

            internal RconConnection(WebSocket socket, IPEndPoint endPoint)
            {
                Socket = socket;
                EndPoint = endPoint;
            }

            public void Send(string text) => _ = SendAsync(text);

            private async Task SendAsync(string text)
            {
                await sendLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (Socket.State == WebSocketState.Open)
                    {
                        await Socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is IOException or WebSocketException or ObjectDisposedException)
                {
                }
                finally
                {
                    sendLock.Release();
                }
            }

            internal void Close(WebSocketCloseStatus status, string reason)
            {
                if (Socket.State == WebSocketState.Open || Socket.State == WebSocketState.CloseReceived)
                {
                    _ = Socket.CloseOutputAsync(status, reason, CancellationToken.None);
                }
            }
        }

        #endregion Connections
    }
}
