using System;
using System.IO;
using Newtonsoft.Json;
using ChatSharp;
using ChatSharp.Events;
using System.Threading;
using System.Linq;
using System.Collections.Generic;

namespace PokeBot
{
    class Program
    {
        private class PendingVoice
        {
            public IrcUser User { get; set; }
            public DateTime ScheduledVoice { get; set; }
            public string Channel { get; set; }
        }

        static Config Config;
        static IrcClient Client;
        static List<PendingVoice> AwaitingVoice = new List<PendingVoice>();
        static object VoiceLock = new object();
        static bool EnableVoicing = true;
        static Timer VoiceTimer;
        static DateTime UntrustedRateLimit = DateTime.MinValue;

        static void SaveConfig()
        {
            File.WriteAllText("config.json", JsonConvert.SerializeObject(Config, Formatting.Indented));
        }

        public static void Main(string[] args)
        {
            Config = new Config();
            if (File.Exists("config.json"))
                JsonConvert.PopulateObject(File.ReadAllText("config.json"), Config);
            else
            {
                SaveConfig();
                Console.WriteLine("Config created, populate it and restart");
                return;
            }
            SaveConfig();
            Client = new IrcClient(Config.Network, new IrcUser(Config.Nick, Config.User, Config.Password));
            Client.ConnectionComplete += HandleConnectionComplete;
            Client.PrivateMessageRecieved += HandleChannelMessageRecieved;
            Client.RawMessageRecieved += (sender, e) => Console.WriteLine("<< {0}", e.Message);
            Client.RawMessageSent += (sender, e) => Console.WriteLine(">> {0}", e.Message);
            Client.ModeChanged += HandleModeChanged;
            Client.UserJoinedChannel += HandleUserJoinedChannel;
            Client.UserKicked += (sender, e) => Client.JoinChannel(e.Channel.Name);
            Client.UserPartedChannel += HandleUserPartedChannel;
            VoiceTimer = new Timer(DoVoicing);
            Client.ConnectAsync();
            while (true) Thread.Sleep(100);
        }

        static void HandleUserPartedChannel(object sender, ChannelUserEventArgs e)
        {
            lock (VoiceLock)
            {
                var user = AwaitingVoice.SingleOrDefault(a => a.User.Nick == e.User.Nick);
                if (user != null)
                    AwaitingVoice.Remove(user);
            }
        }

        static void DoVoicing(object discarded)
        {
            lock (VoiceLock)
            {
                List<PendingVoice> toRemove = new List<PendingVoice>();
                foreach (var user in AwaitingVoice.Where(u => u.ScheduledVoice < DateTime.Now))
                {
                    toRemove.Add(user);
                    Client.ChangeMode(user.Channel, "+v " + user.User.Nick);
                }
                foreach (var user in toRemove)
                    AwaitingVoice.Remove(user);
            }
        }

        static void HandleUserJoinedChannel(object sender, ChannelUserEventArgs e)
        {
            if (e.User.Nick == Client.User.Nick)
                return;
            lock (VoiceLock)
            {
                AwaitingVoice.Add(new PendingVoice
                {
                    User = e.User,
                    Channel = e.Channel.Name,
                    ScheduledVoice = DateTime.Now.AddSeconds(Config.WaitTime)
                });
            }
        }

        static void HandleModeChanged(object sender, ModeChangeEventArgs e)
        {
            if (e.Change == "-o " + Client.User.Nick)
            {
                Client.SendMessage("op " + e.Target + " " + Client.User.Nick, "ChanServ");
            }
        }

        static void HandleChannelMessageRecieved(object sender, PrivateMessageEventArgs e)
        {
            if (e.PrivateMessage.Message.StartsWith("."))
            {
                var command = e.PrivateMessage.Message.Substring(1);
                string[] parameters = new string[0];
                if (command.Contains(" "))
                {
                    parameters = command.Substring(command.IndexOf(" ") + 1).Split(' ');
                    command = command.Remove(command.IndexOf(" "));
                }
                command = command.ToLower();
                bool isTrusted = false;
                if (Config.TrustedMasks.Any(m => e.PrivateMessage.User.Match(m)))
                    isTrusted = true;
                if (!isTrusted && (DateTime.Now - UntrustedRateLimit).TotalSeconds < 3)
                    return;
                UntrustedRateLimit = DateTime.Now;
                switch (command)
                {
                    case "ping":
                        Client.SendMessage(e.PrivateMessage.User.Nick + ": Pong!", e.PrivateMessage.Source);
                        break;
                    case "voice":
                        if (isTrusted)
                        {
                            EnableVoicing = !EnableVoicing;
                            Client.SendMessage("Set auto-voicing to " + EnableVoicing, e.PrivateMessage.Source);
                        }
                        break;
                    case "wait":
                        if (isTrusted && parameters.Length == 1)
                        {
                            if (int.TryParse(parameters[0], out Config.WaitTime))
                            {
                                lock (VoiceLock)
                                {
                                    Client.SendMessage(string.Format("Wait time set to {0} seconds. Dropped {1} users from the queue.", 
                                        Config.WaitTime, AwaitingVoice.Count), e.PrivateMessage.Source);
                                    AwaitingVoice.Clear();
                                }
                                SaveConfig();
                            }
                        }
                        break;
                    case "part":
                        if (isTrusted && e.PrivateMessage.IsChannelMessage)
                        {
                            Client.PartChannel(e.PrivateMessage.Source);
                            Config.Channels = Config.Channels.Where(c => c != e.PrivateMessage.Source).ToArray();
                            SaveConfig();
                        }
                        break;
                    case "join":
                        if (isTrusted && parameters.Length == 1)
                        {
                            Client.JoinChannel(parameters[0]);
                            Config.Channels = Config.Channels.Concat(new[] { parameters[0] }).ToArray();
                            SaveConfig();
                        }
                        break;
                    case "voiceall":
                        if (isTrusted && e.PrivateMessage.IsChannelMessage)
                        {
                            lock (VoiceLock)
                            {
                                AwaitingVoice.Clear();
                                VoiceTimer.Change(Timeout.Infinite, Timeout.Infinite);
                                var users = Client.Channels[e.PrivateMessage.Source].Users.ToArray();
                                for (int i = 0; i < users.Length; i += 4)
                                {
                                    string change = string.Empty;
                                    var round = users.Skip(i).Take(4).ToArray();
                                    while (change.Length < round.Length)
                                        change += "v";
                                    Client.ChangeMode(e.PrivateMessage.Source, "+" + change + " " + string.Join(" ", round.Select(u => u.Nick)));
                                }
                            }
                        }
                        break;
                    case "devoiceall":
                        if (isTrusted && e.PrivateMessage.IsChannelMessage)
                        {
                            lock (VoiceLock)
                            {
                                AwaitingVoice.Clear();
                                VoiceTimer.Change(Timeout.Infinite, Timeout.Infinite);
                                var users = Client.Channels[e.PrivateMessage.Source].Users.ToArray();
                                for (int i = 0; i < users.Length; i += 4)
                                {
                                    string change = string.Empty;
                                    var round = users.Skip(i).Take(4).ToArray();
                                    while (change.Length < round.Length)
                                        change += "v";
                                    Client.ChangeMode(e.PrivateMessage.Source, "-" + change + " " + string.Join(" ", round.Select(u => u.Nick)));
                                }
                            }
                        }
                        break;
                }
            }
        }

        static void HandleConnectionComplete(object sender, EventArgs e)
        {
            foreach (var channel in Config.Channels)
                Client.JoinChannel(channel);
            VoiceTimer.Change(1000, 1000);
        }
    }
}
