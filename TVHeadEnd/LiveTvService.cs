using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TVHeadEnd.Helper;
using TVHeadEnd.HTSP;
using TVHeadEnd.HTSP_Responses;
using System.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using System.Threading;
using System.Reflection;
using System.Diagnostics;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Model.Dto;
using System.Net;
using System.Xml.Linq;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using System.Collections.Concurrent;

namespace TVHeadEnd
{
    public class LiveTvService : BaseTunerHost, ITunerHost
    {
        private readonly TimeSpan TIMEOUT = TimeSpan.FromMinutes(1);

        private volatile int _subscriptionId = 0;

        private ConcurrentDictionary<string, HTSConnectionHandler> ConnectionHandlers = new ConcurrentDictionary<string, HTSConnectionHandler>(StringComparer.OrdinalIgnoreCase);
        private ILiveTvManager _liveTvManager;

        public LiveTvService(IServerConfigurationManager config, ILogger logger, IJsonSerializer jsonSerializer, IMediaEncoder mediaEncoder, IFileSystem fileSystem, ILiveTvManager liveTvManager)
            : base(config, logger, jsonSerializer, mediaEncoder, fileSystem)
        {
            _liveTvManager = liveTvManager;
        }

        public override string Type => "tvheadend";
        public override string Name => Plugin.StaticName;

        public override string SetupUrl
        {
            get { return Plugin.GetPluginPageUrl("tvheadend"); }
        }

        public override bool SupportsGuideData(TunerHostInfo tuner)
        {
            return true;
        }

        public override TunerHostInfo GetDefaultConfiguration()
        {
            var tuner = base.GetDefaultConfiguration();

            tuner.Url = "http://localhost:9981";

            SetCustomOptions(tuner, new TvHeadEndProviderOptions());

            return tuner;
        }

        private HTSConnectionHandler GetConnectionHandler(TunerHostInfo tuner)
        {
            var id = tuner.Id;

            if (ConnectionHandlers.TryGetValue(id, out HTSConnectionHandler handler))
            {
                return handler;
            }

            var config = GetProviderOptions<TvHeadEndProviderOptions>(tuner);

            handler = new HTSConnectionHandler(Logger, tuner.Url, config);
            ConnectionHandlers.TryAdd(id, handler);

            return handler;
        }

        protected override async Task<List<ChannelInfo>> GetChannelsInternal(TunerHostInfo tuner, CancellationToken cancellationToken)
        {
            var connectionHandler = GetConnectionHandler(tuner);

            cancellationToken = CancellationTokenSource.CreateLinkedTokenSource(new CancellationTokenSource(TIMEOUT).Token, cancellationToken).Token;
            await connectionHandler.EnsureConnection(cancellationToken).ConfigureAwait(false);

            var channels = connectionHandler.BuildChannelInfos();

            foreach (var channel in channels)
            {
                channel.TunerHostId = tuner.Id;
                channel.Id = CreateEmbyChannelId(tuner, channel.TvHeadendId);
            }

            return channels.Cast<ChannelInfo>().ToList();
        }

        protected override async Task<ILiveStream> GetChannelStream(TunerHostInfo tuner, ChannelInfo channel, string streamId, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        {
            var mediaSources = await GetChannelStreamMediaSources(tuner, null, channel, cancellationToken).ConfigureAwait(false);
            var mediaSource = mediaSources.FirstOrDefault();

            return _liveTvManager.CreateLiveStream(new LiveStreamOptions
            {
                MediaSource = mediaSource,
                TunerHost = tuner
            });
        }

        protected override async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(TunerHostInfo tuner, BaseItem dbChannnel, ChannelInfo providerChannel, CancellationToken cancellationToken)
        {
            var connectionHandler = GetConnectionHandler(tuner);

            var channelId = providerChannel.Id;

            HTSMessage getTicketMessage = new HTSMessage();
            getTicketMessage.Method = "getTicket";
            getTicketMessage.putField("channelId", channelId);

            cancellationToken = CancellationTokenSource.CreateLinkedTokenSource(new CancellationTokenSource(TIMEOUT).Token, cancellationToken).Token;
            var getTicketResponse = await connectionHandler.SendMessage(getTicketMessage, cancellationToken).ConfigureAwait(false);

            if (_subscriptionId == int.MaxValue)
            {
                _subscriptionId = 0;
            }
            int currSubscriptionId = _subscriptionId++;

            var mediaSource = new MediaSourceInfo
            {
                Id = "" + currSubscriptionId,
                Path = connectionHandler.GetHttpBaseUrl() + getTicketResponse.getString("path") + "?ticket=" + getTicketResponse.getString("ticket"),
                Protocol = MediaProtocol.Http,
                MediaStreams = new List<MediaStream>
                            {
                                new MediaStream
                                {
                                    Type = MediaStreamType.Video,
                                    // Set the index to -1 because we don't know the exact index of the video stream within the container
                                    Index = -1,
                                    // Set to true if unknown to enable deinterlacing
                                    IsInterlaced = true
                                },
                                new MediaStream
                                {
                                    Type = MediaStreamType.Audio,
                                    // Set the index to -1 because we don't know the exact index of the audio stream within the container
                                    Index = -1
                                }
                            }
            };

            return new List<MediaSourceInfo> { mediaSource };
        }

        public override Task<List<ProgramInfo>> GetProgramsAsync(TunerHostInfo tuner, string channelId, DateTimeOffset startDateUtc, DateTimeOffset endDateUtc, CancellationToken cancellationToken)
        {
            var connectionHandler = GetConnectionHandler(tuner);

            GetEventsResponseHandler currGetEventsResponseHandler = new GetEventsResponseHandler(startDateUtc, endDateUtc, Logger);

            HTSMessage queryEvents = new HTSMessage();
            queryEvents.Method = "getEvents";
            queryEvents.putField("channelId", Convert.ToInt32(channelId));
            queryEvents.putField("maxTime", (endDateUtc).ToUnixTimeSeconds());
            cancellationToken = CancellationTokenSource.CreateLinkedTokenSource(new CancellationTokenSource(TIMEOUT).Token, cancellationToken).Token;

            Logger.Info("[TVHclient] GetProgramsAsync, ask TVH for events of channel '" + channelId + "'.");

            return connectionHandler.SendMessage(queryEvents, currGetEventsResponseHandler.GetResponse, cancellationToken);
        }

        public override Task ValdidateOptions(TunerHostInfo tuner, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

}
