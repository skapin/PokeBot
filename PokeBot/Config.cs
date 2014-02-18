using System;

namespace PokeBot
{
    public class Config
    {
        public string Network, Nick, User, Password;
        public string[] Channels;
        public string[] TrustedMasks;
        public int WaitTime;
        public int VoteThreshold;

        public Config()
        {
            WaitTime = 20;
            VoteThreshold = 10;
        }
    }
}