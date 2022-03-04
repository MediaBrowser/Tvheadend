using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TVHeadEnd.DataHelper;
using TVHeadEnd.HTSP;


namespace TVHeadEnd
{
    class HTSConnectionHandler : HTSConnectionListener
    {
        private readonly ILogger _logger;

        private HTSConnectionAsync _htsConnection;
        private string _httpBaseUrl;
        private string _tvhServerName;
        private int _htspPort;
        private string _userName;
        private string _password;

        // Data helpers
        private readonly ChannelDataHelper _channelDataHelper;

        private Dictionary<string, string> _headers = new Dictionary<string, string>();

        private List<TaskCompletionSource<bool>> connectionOpenListeners = new List<TaskCompletionSource<bool>>();

        private bool _connected;

        private SemaphoreSlim ConnectionSemaphore = new SemaphoreSlim(1, 1);

        public HTSConnectionHandler(ILogger logger, string baseUrl, TvHeadEndProviderOptions config)
        {
            _logger = logger;

            //System.Diagnostics.StackTrace t = new System.Diagnostics.StackTrace();
            _logger.Info("[TVHclient] HTSConnectionHandler()");

            _channelDataHelper = new ChannelDataHelper(logger);

            init(baseUrl, config);
        }

        private void init(string baseUrl, TvHeadEndProviderOptions config)
        {
            _tvhServerName = new Uri(baseUrl).Host;
            _htspPort = config.HTSP_Port;

            _userName = (config.Username ?? "").Trim();
            _password = (config.Password ?? "").Trim();

            _httpBaseUrl = baseUrl;

            string authInfo = _userName + ":" + _password;
            authInfo = Convert.ToBase64String(Encoding.Default.GetBytes(authInfo));
            _headers["Authorization"] = "Basic " + authInfo;
        }

        public Dictionary<string, string> GetHeaders()
        {
            return new Dictionary<string, string>(_headers);
        }

        public async Task EnsureConnection(CancellationToken cancellationToken)
        {
            await ConnectionSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                //_logger.Info("[TVHclient] HTSConnectionHandler.ensureConnection()");
                if (_htsConnection == null || _htsConnection.needsRestart())
                {
                    _connected = false;

                    _logger.Info("[TVHclient] HTSConnectionHandler.ensureConnection() : create new HTS-Connection");
                    Version version = Assembly.GetEntryAssembly().GetName().Version;
                    var connection = new HTSConnectionAsync(this, "TVHclient4Emby-" + version.ToString(), "" + HTSMessage.HTSP_VERSION, _logger);

                    _logger.Info("[TVHclient] HTSConnectionHandler.ensureConnection: Used connection parameters: " +
                        "TVH Server = '" + _httpBaseUrl + "'; " +
                        "HTSP Port = '" + _htspPort + "'; " +
                        "User = '" + _userName + "'; " +
                        "Password set = '" + (_password.Length > 0) + "'");

                    await connection.open(_tvhServerName, _htspPort, cancellationToken).ConfigureAwait(false);
                    await connection.Authenticate(_userName, _password, cancellationToken).ConfigureAwait(false);

                    _htsConnection = connection;
                }
                else if (_connected)
                {
                    return;
                }

                var taskCompletionSource = new TaskCompletionSource<bool>();

                lock (connectionOpenListeners)
                {
                    connectionOpenListeners.Add(taskCompletionSource);
                }

                await taskCompletionSource.Task.ConfigureAwait(false);
            }
            finally
            {
                ConnectionSemaphore.Release();
            }
        }

        public async Task<HTSMessage> SendMessage(HTSMessage message, CancellationToken cancellationToken)
        {
            await EnsureConnection(cancellationToken).ConfigureAwait(false);

            return await _htsConnection.SendMessage(message, cancellationToken).ConfigureAwait(false);
        }

        public async Task<T> SendMessage<T>(HTSMessage message, Func<HTSMessage, T> responseHandler, CancellationToken cancellationToken)
        {
            var response = await SendMessage(message, cancellationToken).ConfigureAwait(false);

            return responseHandler(response);
        }

        public List<ChannelInfo> BuildChannelInfos()
        {
            var uri = new Uri(_httpBaseUrl);
            var builder = new UriBuilder(uri);

            builder.UserName = _userName;
            builder.Password = _password;

            var urlWithAuth = builder.ToString();

            return _channelDataHelper.BuildChannelInfos(urlWithAuth);
        }

        public String GetHttpBaseUrl()
        {
            return _httpBaseUrl;
        }

        public void onError(Exception ex)
        {
            _logger.ErrorException("[TVHclient] HTSConnectionHandler recorded a HTSP error: " + ex.Message, ex);
            _htsConnection.stop();
            _htsConnection = null;
        }

        private void OnInitialDataLoadComplete()
        {
            _connected = true;

            var list = new List<TaskCompletionSource<bool>>();

            lock (connectionOpenListeners)
            {
                list.AddRange(connectionOpenListeners);
                connectionOpenListeners.Clear();
            }

            foreach (var t in list)
            {
                t.TrySetResult(true);
            }
        }

        public void onMessage(HTSMessage response)
        {
            if (response != null)
            {
                switch (response.Method)
                {
                    case "tagAdd":
                    case "tagUpdate":
                    case "tagDelete":
                        //_logger.Fatal("[TVHclient] tad add/update/delete" + response.ToString());
                        break;

                    case "channelAdd":
                    case "channelUpdate":
                        _channelDataHelper.Add(response);
                        break;

                    case "eventAdd":
                    case "eventUpdate":
                    case "eventDelete":
                        // should not happen as we don't subscribe for this events.
                        break;

                    //case "subscriptionStart":
                    //case "subscriptionGrace":
                    //case "subscriptionStop":
                    //case "subscriptionSkip":
                    //case "subscriptionSpeed":
                    //case "subscriptionStatus":
                    //    _logger.Fatal("[TVHclient] subscription events " + response.ToString());
                    //    break;

                    //case "queueStatus":
                    //    _logger.Fatal("[TVHclient] queueStatus event " + response.ToString());
                    //    break;

                    //case "signalStatus":
                    //    _logger.Fatal("[TVHclient] signalStatus event " + response.ToString());
                    //    break;

                    //case "timeshiftStatus":
                    //    _logger.Fatal("[TVHclient] timeshiftStatus event " + response.ToString());
                    //    break;

                    //case "muxpkt": // streaming data
                    //    _logger.Fatal("[TVHclient] muxpkt event " + response.ToString());
                    //    break;

                    case "initialSyncCompleted":
                        OnInitialDataLoadComplete();
                        break;

                    default:
                        //_logger.Fatal("[TVHclient] Method '" + response.Method + "' not handled in LiveTvService.cs");
                        break;
                }
            }
        }
    }
}
