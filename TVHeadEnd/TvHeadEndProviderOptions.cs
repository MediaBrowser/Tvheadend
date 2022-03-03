using System;
using System.Collections.Generic;
using System.Text;

namespace TVHeadEnd
{
    internal class TvHeadEndProviderOptions
    {
        public int HTSP_Port { get; set; } = 9982;
        public string Username { get; set; }
        public string Password { get; set; }
    }
}
