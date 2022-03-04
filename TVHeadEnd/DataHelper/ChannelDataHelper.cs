using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Logging;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TVHeadEnd.HTSP;
using System.Collections.Concurrent;
using System.Globalization;

namespace TVHeadEnd.DataHelper
{
    public class ChannelDataHelper
    {
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<int, HTSMessage> _data = new ConcurrentDictionary<int, HTSMessage>();

        public ChannelDataHelper(ILogger logger)
        {
            _logger = logger;
        }

        public void Add(HTSMessage message)
        {
            try
            {
                int channelID = message.getInt("channelId");
                if (_data.TryGetValue(channelID, out HTSMessage storedMessage))
                {
                    if (storedMessage != null)
                    {
                        foreach (KeyValuePair<string, object> entry in message)
                        {
                            if (storedMessage.containsField(entry.Key))
                            {
                                storedMessage.removeField(entry.Key);
                            }
                            storedMessage.putField(entry.Key, entry.Value);
                        }
                    }
                    else
                    {
                        _logger.Error("[TVHclient] ChannelDataHelper: update for channelID '" + channelID + "' but no initial data found!");
                    }
                }
                else
                {
                    if (message.containsField("channelNumber") && message.getInt("channelNumber") > 0) // use only channels with number > 0
                    {
                        _data.TryAdd(channelID, message);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("[TVHclient] ChannelDataHelper.Add caught exception: " + ex.Message + "\nHTSmessage=" + message);
            }
        }

        public List<ChannelInfo> BuildChannelInfos()
        {
            var result = new List<ChannelInfo>();

            var allChannels = _data.ToArray();

            foreach (KeyValuePair<int, HTSMessage> entry in allChannels)
            {
                HTSMessage m = entry.Value;

                try
                {
                    var ci = new ChannelInfo();
                    ci.Id = entry.Key.ToString(CultureInfo.InvariantCulture);

                    if (m.containsField("channelIcon"))
                    {
                        string channelIcon = m.getString("channelIcon");
                        Uri uriResult;
                        bool uriCheckResult = Uri.TryCreate(channelIcon, UriKind.Absolute, out uriResult) && uriResult.Scheme == Uri.UriSchemeHttp;
                        if (uriCheckResult)
                        {
                            ci.ImageUrl = channelIcon;
                        }
                        else
                        {
                            //ci.ImageUrl = "http://" + _userName + ":" + _password + "@" + _tvhServerName + ":" + _httpPort + _webRoot + "/" + channelIcon; 
                        }
                    }
                    if (m.containsField("channelName"))
                    {
                        string name = m.getString("channelName");
                        if (string.IsNullOrEmpty(name))
                        {
                            continue;
                        }
                        ci.Name = m.getString("channelName");
                    }

                    if (m.containsField("channelNumber"))
                    {
                        int channelNumber = m.getInt("channelNumber");
                        ci.Number = "" + channelNumber;
                        if (m.containsField("channelNumberMinor"))
                        {
                            int channelNumberMinor = m.getInt("channelNumberMinor");
                            ci.Number = ci.Number + "." + channelNumberMinor;
                        }
                    }

                    Boolean serviceFound = false;
                    if (m.containsField("services"))
                    {
                        IList tunerInfoList = m.getList("services");
                        if (tunerInfoList != null && tunerInfoList.Count > 0)
                        {
                            HTSMessage firstServiceInList = (HTSMessage)tunerInfoList[0];
                            if (firstServiceInList.containsField("type"))
                            {
                                string type = firstServiceInList.getString("type").ToLower();
                                switch (type)
                                {
                                    case "radio":
                                        ci.ChannelType = ChannelType.Radio;
                                        serviceFound = true;
                                        break;
                                    case "sdtv":
                                    case "hdtv":
                                    case "uhdtv":
                                    case "fhdtv":
                                        ci.ChannelType = ChannelType.TV;
                                        serviceFound = true;
                                        break;
                                    default:
                                        _logger.Info("[TVHclient] ChannelDataHelper: unkown service tag '" + type + "' - will be ignored.");
                                        break;
                                }
                            }
                        }
                    }
                    if (!serviceFound)
                    {
                        _logger.Info("[TVHclient] ChannelDataHelper: unable to detect service-type (tvheadend tag!!!) from service list:" + m.ToString());
                        continue;
                    }

                    _logger.Info("[TVHclient] ChannelDataHelper: Adding channel \n" + m.ToString());

                    result.Add(ci);
                }
                catch (Exception ex)
                {
                    _logger.Error("[TVHclient] ChannelDataHelper.BuildChannelInfos caught exception: " + ex.Message + "\nHTSmessage=" + m);
                }
            }
            return result;
        }
    }
}
