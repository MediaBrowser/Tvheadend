using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using TVHeadEnd.Helper;
using TVHeadEnd.HTSP_Responses;

namespace TVHeadEnd.HTSP
{
    public class HTSConnectionAsync
    {
        private volatile Boolean _needsRestart = false;
        private volatile Boolean _connected;
        private volatile int _seq = 0;

        private readonly HTSConnectionListener _listener;
        private readonly String _clientName;
        private readonly String _clientVersion;
        private readonly ILogger _logger;

        private readonly ByteList _buffer;
        private readonly Dictionary<int, TaskCompletionSource<HTSMessage>> _responseHandlers = new Dictionary<int, TaskCompletionSource<HTSMessage>>();

        private Thread _receiveHandlerThread;
        private Thread _messageBuilderThread;
        private Thread _messageDistributorThread;

        private CancellationTokenSource _receiveHandlerThreadTokenSource;
        private CancellationTokenSource _messageBuilderThreadTokenSource;
        private CancellationTokenSource _messageDistributorThreadTokenSource;

        private Socket _socket = null;

        public HTSConnectionAsync(HTSConnectionListener listener, String clientName, String clientVersion, ILogger logger)
        {
            _logger = logger;

            _connected = false;

            _listener = listener;
            _clientName = clientName;
            _clientVersion = clientVersion;

            _buffer = new ByteList();

            _receiveHandlerThreadTokenSource = new CancellationTokenSource();
            _messageBuilderThreadTokenSource = new CancellationTokenSource();
            _messageDistributorThreadTokenSource = new CancellationTokenSource();
        }

        public void stop()
        {
            try
            {
                if (_receiveHandlerThread != null && _receiveHandlerThread.IsAlive)
                {
                    _receiveHandlerThreadTokenSource.Cancel();
                }
                if (_messageBuilderThread != null && _messageBuilderThread.IsAlive)
                {
                    _messageBuilderThreadTokenSource.Cancel();
                }
                if (_messageDistributorThread != null && _messageDistributorThread.IsAlive)
                {
                    _messageDistributorThreadTokenSource.Cancel();
                }
            }
            catch
            {

            }

            try
            {
                if (_socket != null && _socket.Connected)
                {
                    _socket.Close();
                }
            }
            catch
            {

            }
            _needsRestart = true;
            _connected = false;
        }

        public Boolean needsRestart()
        {
            return _needsRestart;
        }

        public async Task open(String hostname, int port, CancellationToken cancellationToken)
        {
            if (_connected)
            {
                return;
            }

            while (!_connected)
            {
                try
                {
                    // Establish the remote endpoint for the socket.

                    IPAddress ipAddress;
                    if (!IPAddress.TryParse(hostname, out ipAddress))
                    {
                        // no IP --> ask DNS
                        IPHostEntry ipHostInfo = Dns.GetHostEntry(hostname);
                        ipAddress = ipHostInfo.AddressList[0];
                    }

                    if (ipAddress.IsIPv4MappedToIPv6)
                    {
                        ipAddress = ipAddress.MapToIPv4();
                    }

                    IPEndPoint remoteEP = new IPEndPoint(ipAddress, port);

                    _logger.Info("[TVHclient] HTSConnectionAsync.open: " +
                        "IPEndPoint = '" + remoteEP.ToString() + "'; " +
                        "AddressFamily = '" + ipAddress.AddressFamily + "'");

                    // Create a TCP/IP  socket.
                    _socket = new Socket(ipAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                    // connect to server
                    _socket.Connect(remoteEP);

                    _connected = true;
                    _logger.Info("[TVHclient] HTSConnectionAsync.open: socket connected.");
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Error("[TVHclient] HTSConnectionAsync.open: caught exception : {0}", ex.Message);
                }

                await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
            }

            ThreadStart ReceiveHandlerRef = new ThreadStart(ReceiveHandler);
            _receiveHandlerThread = new Thread(ReceiveHandlerRef);
            _receiveHandlerThread.IsBackground = true;
            _receiveHandlerThread.Start();

            ThreadStart MessageBuilderRef = new ThreadStart(MessageBuilder);
            _messageBuilderThread = new Thread(MessageBuilderRef);
            _messageBuilderThread.IsBackground = true;
            _messageBuilderThread.Start();
        }

        public async Task Authenticate(String username, String password, CancellationToken cancellationToken)
        {
            _logger.Info("[TVHclient] HTSConnectionAsync.authenticate: start");

            HTSMessage helloMessage = new HTSMessage();
            helloMessage.Method = "hello";
            helloMessage.putField("clientname", _clientName);
            helloMessage.putField("clientversion", _clientVersion);
            helloMessage.putField("htspversion", HTSMessage.HTSP_VERSION);
            helloMessage.putField("username", username);

            var helloResponse = await SendMessage(helloMessage, cancellationToken).ConfigureAwait(false);
            byte[] salt = null;
            if (helloResponse.containsField("challenge"))
            {
                salt = helloResponse.getByteArray("challenge");
            }
            else
            {
                salt = new byte[0];
                _logger.Info("[TVHclient] HTSConnectionAsync.authenticate: hello don't deliver required field 'challenge' - htsp wrong implemented on tvheadend side.");
            }

            byte[] digest = SHA1helper.GenerateSaltedSHA1(password, salt);
            HTSMessage authMessage = new HTSMessage();
            authMessage.Method = "authenticate";
            authMessage.putField("username", username);
            authMessage.putField("digest", digest);
            var authResponse = await SendMessage(authMessage, cancellationToken).ConfigureAwait(false);

            Boolean auth = authResponse.getInt("noaccess", 0) != 1;
            if (auth)
            {
                HTSMessage enableAsyncMetadataMessage = new HTSMessage();
                enableAsyncMetadataMessage.Method = "enableAsyncMetadata";
                await SendMessage(enableAsyncMetadataMessage, cancellationToken).ConfigureAwait(false);
            }

            _logger.Info("[TVHclient] HTSConnectionAsync.authenticate: authenticated = " + auth);
            if (!auth)
            {
                throw new Exception("TVH authentication failed");
            }
        }

        public Task<HTSMessage> SendMessage(HTSMessage message, CancellationToken cancellationToken)
        {
            // loop the sequence number
            if (_seq == int.MaxValue)
            {
                _seq = int.MinValue;
            }
            else
            {
                _seq++;
            }

            message.putField("seq", _seq);

            var taskCompletionSource = new TaskCompletionSource<HTSMessage>();
            cancellationToken.Register(() => taskCompletionSource.TrySetCanceled());
            _responseHandlers.Add(_seq, taskCompletionSource);

            try
            {
                byte[] data2send = message.BuildBytes();
                int bytesSent = _socket.Send(data2send);
                if (bytesSent != data2send.Length)
                {
                    _logger.Error("[TVHclient] SendingHandler: Sending not complete! \nBytes sent: " + bytesSent + "\nMessage bytes: " +
                        data2send.Length + "\nMessage: " + message.ToString());
                }

                return taskCompletionSource.Task;
            }
            catch (Exception ex)
            {
                _logger.Error("[TVHclient] SendingHandler caught exception : {0}", ex.ToString());
                if (_listener != null)
                {
                    _listener.onError(ex);
                }
                else
                {
                    _logger.ErrorException("[TVHclient] SendingHandler caught exception : {0} but no error listener is configured!!!", ex, ex.ToString());
                }
                throw;
            }
        }

        private void ReceiveHandler()
        {
            Boolean threadOk = true;
            byte[] readBuffer = new byte[1024];
            while (_connected && threadOk)
            {
                if (_receiveHandlerThreadTokenSource.IsCancellationRequested)
                {
                    return;
                }
                try
                {
                    int bytesReveived = _socket.Receive(readBuffer);
                    _buffer.appendCount(readBuffer, bytesReveived);
                }
                catch (Exception ex)
                {
                    threadOk = false;
                    if (_listener != null)
                    {
                        Task.Factory.StartNew(() => _listener.onError(ex));
                    }
                    else
                    {
                        _logger.ErrorException("[TVHclient] ReceiveHandler caught exception : {0} but no error listener is configured!!!", ex, ex.ToString());
                    }
                }
            }
        }

        private void MessageBuilder()
        {
            Boolean threadOk = true;
            while (_connected && threadOk)
            {
                if (_messageBuilderThreadTokenSource.IsCancellationRequested)
                {
                    return;
                }
                try
                {
                    byte[] lengthInformation = _buffer.getFromStart(4);
                    long messageDataLength = HTSMessage.uIntToLong(lengthInformation[0], lengthInformation[1], lengthInformation[2], lengthInformation[3]);
                    byte[] messageData = _buffer.extractFromStart((int)messageDataLength + 4); // should be long !!!
                    HTSMessage response = HTSMessage.parse(messageData, _logger);

                    ProcessResponse(response);
                }
                catch (Exception ex)
                {
                    threadOk = false;
                    _logger.ErrorException("[TVHclient] MessageBuilder caught exception : {0} but no error listener is configured!!!", ex, ex.ToString());
                }
            }
        }

        private void ProcessResponse(HTSMessage response)
        {
            if (response.containsField("seq"))
            {
                int seqNo = response.getInt("seq");
                if (_responseHandlers.TryGetValue(seqNo, out TaskCompletionSource<HTSMessage> currHTSResponseHandler))
                {
                    _responseHandlers.Remove(seqNo);

                    currHTSResponseHandler.TrySetResult(response);
                }
                else
                {
                    _logger.Fatal("[TVHclient] MessageDistributor: HTSResponseHandler for seq = '" + seqNo + "' not found!");
                }
            }
            else
            {
                // auto update messages
                if (_listener != null)
                {
                    _listener.onMessage(response);
                }
            }
        }
    }
}
