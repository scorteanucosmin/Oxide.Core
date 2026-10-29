using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Oxide.Core.Plugins;

namespace Oxide.Core.Libraries
{
    /// <summary>
    /// Request methods for web requests
    /// </summary>
    public enum RequestMethod
    {
        DELETE,
        GET,
        PATCH,
        POST,
        PUT
    };

    /// <summary>
    /// The WebRequests library
    /// </summary>
    public class WebRequests : Library
    {
        private static readonly Covalence.Covalence covalence = Interface.Oxide.GetLibrary<Covalence.Covalence>();

        /// <summary>
        /// Specifies the HTTP request timeout in seconds
        /// </summary>
        public static float Timeout = 30f;

        /// <summary>
        /// Specifies the HTTP request decompression support
        /// </summary>
        [Obsolete("AllowDecompression is deprecated, use Enqueue with DecompressionMode instead")]

        public static bool AllowDecompression = false;

        /// <summary>
        /// Represents a single WebRequest instance
        /// </summary>
        public class WebRequest
        {
            /// <summary>
            /// Gets the callback delegate
            /// </summary>
            public Action<int, string> Callback { get; }

            /// <summary>
            /// Overrides the default request timeout
            /// </summary>
            public float Timeout { get; set; }

            /// <summary>
            /// Gets the web request method
            /// </summary>
            public string Method { get; set; } = "GET";

            /// <summary>
            /// Gets the destination URL
            /// </summary>
            public string Url { get; }

            /// <summary>
            /// Gets or sets the request body
            /// </summary>
            public string? Body { get; set; }

            /// <summary>
            /// Gets the response code
            /// </summary>
            public int ResponseCode { get; protected set; }

            /// <summary>
            /// Gets the response text
            /// </summary>
            public string ResponseText { get; protected set; } = string.Empty;

            /// <summary>
            /// Gets the plugin to which this web request belongs, if any
            /// </summary>
            public Plugin? Owner { get; protected set; }

            /// <summary>
            /// Gets the web request headers
            /// </summary>
            public Dictionary<string, string>? RequestHeaders { get; set; }

            /// <summary>
            /// Gets the decompression type for web requests
            /// </summary>
            public DecompressionMethods DecompressionMethod { get; set; } = DecompressionMethods.None;

            private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
            private Event.Callback<Plugin, PluginManager>? removedFromManager;
            private bool ownerRemoved;

            /// <summary>
            /// Initializes a new instance of the WebRequest class
            /// </summary>
            /// <param name="url"></param>
            /// <param name="callback"></param>
            /// <param name="owner"></param>
            public WebRequest(string url, Action<int, string> callback, Plugin? owner)
            {
                Url = url;
                Callback = callback;
                Owner = owner;
                removedFromManager = Owner?.OnRemovedFromManager.Add(owner_OnRemovedFromManager);
            }

            /// <summary>
            /// </summary>
            public void Start() => _ = Run();

            private async Task Run()
            {
                Interlocked.Increment(ref pending);
                try
                {
                    float timeout = Timeout.Equals(0f) ? WebRequests.Timeout : Timeout;
                    cancellation.CancelAfter(TimeSpan.FromSeconds(timeout));

                    using HttpRequestMessage request = new HttpRequestMessage(new HttpMethod(Method), Url);
                    if (Body != null)
                    {
                        request.Content = new StringContent(Body, Encoding.UTF8);
                        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
                    }

                    if (RequestHeaders != null)
                    {
                        foreach (KeyValuePair<string, string> header in RequestHeaders)
                        {
                            if (request.Content != null && header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                            {
                                request.Content.Headers.Remove(header.Key);
                                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                            }
                            else
                            {
                                request.Headers.Remove(header.Key);
                                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                            }
                        }
                    }

#pragma warning disable CS0618
                    DecompressionMethods decompression = AllowDecompression && DecompressionMethod == DecompressionMethods.None
                        ? DecompressionMethods.GZip | DecompressionMethods.Deflate
                        : DecompressionMethod;
#pragma warning restore CS0618

                    using HttpResponseMessage response = await GetClient(decompression).SendAsync(request, cancellation.Token).ConfigureAwait(false);
                    ResponseCode = (int)response.StatusCode;
                    ResponseText = await response.Content.ReadAsStringAsync(cancellation.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
                {
                    ResponseText = FormatWebException(ex, ResponseText);
                }
                catch (Exception ex)
                {
                    ResponseText = FormatWebException(ex, ResponseText);
                    string message = $"Web request produced exception (Url: {Url})";
                    if (Owner)
                    {
                        message += $" in '{Owner!.Name} v{Owner.Version}' plugin";
                    }

                    Interface.Oxide.LogException(message, ex);
                }
                finally
                {
                    Interlocked.Decrement(ref pending);
                }

                OnComplete();
            }

            private void OnComplete()
            {
                Event.Remove(ref removedFromManager);
                Interface.Oxide.NextTick(() =>
                {
                    cancellation.Dispose();

                    if (ownerRemoved)
                    {
                        return;
                    }

                    Owner?.TrackStart();
                    try
                    {
                        Callback(ResponseCode, ResponseText);
                    }
                    catch (Exception ex)
                    {
                        string message = "Web request callback raised an exception";
                        if (Owner)
                        {
                            message += $" in '{Owner!.Name} v{Owner.Version}' plugin";
                        }

                        Interface.Oxide.LogException(message, ex);
                    }
                    Owner?.TrackEnd();
                    Owner = null;
                });
            }

            /// <summary>
            /// Called when the owner plugin was unloaded
            /// </summary>
            /// <param name="sender"></param>
            /// <param name="manager"></param>
            private void owner_OnRemovedFromManager(Plugin sender, PluginManager manager)
            {
                ownerRemoved = true;
                Owner = null;
                cancellation.Cancel();
            }
        }

        private static readonly ConcurrentDictionary<DecompressionMethods, HttpClient> clients = new ConcurrentDictionary<DecompressionMethods, HttpClient>();
        private static int pending;

        private static HttpClient GetClient(DecompressionMethods decompression) => clients.GetOrAdd(decompression, methods =>
        {
            SocketsHttpHandler handler = new SocketsHttpHandler
            {
                AutomaticDecompression = methods,
                UseProxy = false,
                MaxConnectionsPerServer = 200,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
                ConnectCallback = Connect
            };
            HttpClient client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"Oxide Mod (v{OxideMod.Version}; https://umod.org)");
            client.DefaultRequestHeaders.ExpectContinue = false;
            return client;
        });

        private static async ValueTask<Stream> Connect(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
            Socket socket;
            if (IPAddress.TryParse(Interface.Oxide.Config.Options.WebRequestIP, out IPAddress? local) && !local.Equals(IPAddress.Any)
                && !addresses.Any(IPAddress.IsLoopback))
            {
                socket = new Socket(local.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                socket.Bind(new IPEndPoint(local, 0));
                addresses = addresses.Where(a => a.AddressFamily == local.AddressFamily).ToArray();
            }
            else
            {
                socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            }

            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Formats given WebException to string
        /// </summary>
        /// <param name="exception"></param>
        /// <param name="response"></param>
        /// <returns></returns>
        public static string FormatWebException(Exception exception, string response)
        {
            if (!string.IsNullOrEmpty(response))
            {
                response += Environment.NewLine;
            }

            response += exception.Message;

            if (exception.InnerException != null)
            {
                response = FormatWebException(exception.InnerException, response);
            }

            return response;
        }

        /// <summary>
        /// Enqueues a get request
        /// </summary>
        /// <param name="url"></param>
        /// <param name="callback"></param>
        /// <param name="owner"></param>
        /// <param name="headers"></param>
        /// <param name="timeout"></param>
        [LibraryFunction("EnqueueGet")]
        [Obsolete("EnqueueGet is deprecated, use Enqueue instead")]
        public void EnqueueGet(string url, Action<int, string> callback, Plugin owner, Dictionary<string, string> headers = null, float timeout = 0f)
        {
            Enqueue(url, null, callback, owner, RequestMethod.GET, headers, timeout);
        }

        /// <summary>
        /// Enqueues a post request
        /// </summary>
        /// <param name="url"></param>
        /// <param name="body"></param>
        /// <param name="callback"></param>
        /// <param name="owner"></param>
        /// <param name="headers"></param>
        /// <param name="timeout"></param>
        [LibraryFunction("EnqueuePost")]
        [Obsolete("EnqueuePost is deprecated, use Enqueue instead")]
        public void EnqueuePost(string url, string body, Action<int, string> callback, Plugin owner, Dictionary<string, string> headers = null, float timeout = 0f)
        {
            Enqueue(url, body, callback, owner, RequestMethod.POST, headers, timeout);
        }

        /// <summary>
        /// Enqueues a put request
        /// </summary>
        /// <param name="url"></param>
        /// <param name="body"></param>
        /// <param name="callback"></param>
        /// <param name="owner"></param>
        /// <param name="headers"></param>
        /// <param name="timeout"></param>
        [LibraryFunction("EnqueuePut")]
        [Obsolete("EnqueuePut is deprecated, use Enqueue instead")]
        public void EnqueuePut(string url, string body, Action<int, string> callback, Plugin owner, Dictionary<string, string> headers = null, float timeout = 0f)
        {
            Enqueue(url, body, callback, owner, RequestMethod.PUT, headers, timeout);
        }

        /// <summary>
        /// Enqueues a DELETE, GET, PATCH, POST, or PUT web request
        /// </summary>
        /// <param name="url"></param>
        /// <param name="body"></param>
        /// <param name="callback"></param>
        /// <param name="owner"></param>
        /// <param name="method"></param>
        /// <param name="headers"></param>
        /// <param name="timeout"></param>
        /// <param name="decompressionMethod"></param>
        [LibraryFunction("Enqueue")]
        public void Enqueue(string url, string body, Action<int, string> callback, Plugin owner, RequestMethod method = RequestMethod.GET, Dictionary<string, string> headers = null, float timeout = 0f, DecompressionMethods decompressionMethod = DecompressionMethods.None)
        {
            WebRequest request = new WebRequest(url, callback, owner) { Method = method.ToString(), RequestHeaders = headers, Timeout = timeout, Body = body, DecompressionMethod = decompressionMethod };
            request.Start();
        }

        /// <summary>
        /// Returns the current queue length
        /// </summary>
        /// <returns></returns>
        [LibraryFunction("GetQueueLength")]
        public int GetQueueLength() => pending;
    }

    // HttpWebRequest extensions to add raw header support
    [Obsolete("HttpWebRequest is obsolete; WebRequests uses HttpClient")]
    public static class HttpWebRequestExtensions
    {
        /// <summary>
        /// Headers that require modification via a property
        /// </summary>
        private static readonly string[] RestrictedHeaders = {
            "Accept",
            "Connection",
            "Content-Length",
            "Content-Type",
            "Date",
            "Expect",
            "Host",
            "If-Modified-Since",
            "Keep-Alive",
            "Proxy-Connection",
            "Range",
            "Referer",
            "Transfer-Encoding",
            "User-Agent"
        };

        /// <summary>
        /// Dictionary of all of the header properties
        /// </summary>
        private static readonly Dictionary<string, PropertyInfo> HeaderProperties = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Initialize the HeaderProperties dictionary
        /// </summary>
        static HttpWebRequestExtensions()
        {
            Type type = typeof(HttpWebRequest);
            foreach (string header in RestrictedHeaders)
            {
                HeaderProperties[header] = type.GetProperty(header.Replace("-", ""));
            }
        }

        /// <summary>
        /// Sets raw HTTP request headers
        /// </summary>
        /// <param name="request">Request object</param>
        /// <param name="headers">Dictionary of headers to set</param>
        public static void SetRawHeaders(this WebRequest request, Dictionary<string, string> headers)
        {
            foreach (KeyValuePair<string, string> keyValPair in headers)
            {
                request.SetRawHeader(keyValPair.Key, keyValPair.Value);
            }
        }

        /// <summary>
        /// Sets a raw HTTP request header
        /// </summary>
        /// <param name="request">Request object</param>
        /// <param name="name">Name of the header</param>
        /// <param name="value">Value of the header</param>
        public static void SetRawHeader(this WebRequest request, string name, string value)
        {
            if (HeaderProperties.ContainsKey(name))
            {
                PropertyInfo property = HeaderProperties[name];
                if (property.PropertyType == typeof(DateTime))
                {
                    property.SetValue(request, DateTime.Parse(value), null);
                }
                else if (property.PropertyType == typeof(bool))
                {
                    property.SetValue(request, bool.Parse(value), null);
                }
                else if (property.PropertyType == typeof(long))
                {
                    property.SetValue(request, long.Parse(value), null);
                }
                else
                {
                    property.SetValue(request, value, null);
                }
            }
            else
            {
                request.Headers[name] = value;
            }
        }
    }
}
