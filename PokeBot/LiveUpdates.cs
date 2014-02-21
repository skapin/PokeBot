using System;

namespace PokeBot
{
    public class LiveUpdates
    {
        public class Item
        {
            public string name { get; set; }
            public int count { get; set; }
        }

        public class Pokemon
        {
            public string species { get; set; }
            public string name { get; set; }
            public string nickname { get; set; }
            public int level { get; set; }
            public string[] moves { get; set; }
        }

        public class Update
        {
            public long time { get; set; }
            public string title { get; set; }
            public string category { get; set; }
            public string description { get; set; }
        }

        public int version { get; set; }
        public int balance { get; set; }
        public string goal { get; set; }
        public string[] badges { get; set; }
        public Item[] inventory { get; set; }
        public Pokemon[] party { get; set; }
        public Update[] updates { get; set; }
    }
}