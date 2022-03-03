using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using System.IO;
using MediaBrowser.Model.Drawing;
using System.Linq;

namespace TVHeadEnd
{
    /// <summary>
    /// Class Plugin
    /// </summary>
    public class Plugin : BasePlugin, IHasWebPages, IHasThumbImage, IHasTranslations
    {
        public static string StaticName = "Tvheadend";

        /// <summary>
        /// Gets the name of the plugin
        /// </summary>
        /// <value>The name.</value>
        public override string Name
        {
            get { return StaticName; }
        }

        /// <summary>
        /// Gets the description.
        /// </summary>
        /// <value>The description.</value>
        public override string Description
        {
            get
            {
                return "Provides live TV using Tvheadend as a back-end.";
            }
        }

        private Guid _id = new Guid("95732bbe-15ed-4293-bab2-e056ccc50159");
        public override Guid Id
        {
            get { return _id; }
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new PluginPageInfo[]
            {
                new PluginPageInfo
                {
                    Name = "tvheadend",
                    EmbeddedResourcePath = GetType().Namespace + ".web.tvheadend.html",
                    IsMainConfigPage = false
                },
                new PluginPageInfo
                {
                    Name = "tvheadendjs",
                    EmbeddedResourcePath = GetType().Namespace + ".web.tvheadend.js"
                }
            };
        }

        public TranslationInfo[] GetTranslations()
        {
            var basePath = GetType().Namespace + ".strings.";

            return GetType()
                .Assembly
                .GetManifestResourceNames()
                .Where(i => i.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
                .Select(i => new TranslationInfo
                {
                    Locale = Path.GetFileNameWithoutExtension(i.Substring(basePath.Length)),
                    EmbeddedResourcePath = i

                }).ToArray();
        }
        public Stream GetThumbImage()
        {
            var type = GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".Images.thumb.png");
        }

        public ImageFormat ThumbImageFormat
        {
            get
            {
                return ImageFormat.Png;
            }
        }
    }

}
