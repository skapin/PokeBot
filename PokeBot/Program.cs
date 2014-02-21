using System;
using System.IO;
using Newtonsoft.Json;
using ChatSharp;
using ChatSharp.Events;
using System.Threading;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;

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

        private class MutePoll
        {
            public string Nick { get; set; }
            public int Votes { get; set; }
            public DateTime Expires { get; set; }
            public List<string> Voters { get; set; }
        }

        static Config Config;
        static IrcClient Client;
        static List<PendingVoice> AwaitingVoice = new List<PendingVoice>();
        static object VoiceLock = new object();
        static bool EnableVoicing = true;
        static Timer VoiceTimer;
        static DateTime UntrustedRateLimit = DateTime.MinValue;
        static string ConfigPath = "config.json";
        static List<MutePoll> MutePolls;
        static string PrimaryChannel;

        static void SaveConfig()
        {
            File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(Config, Formatting.Indented));
        }

        public static void Main(string[] args)
        {
            if (args.Length != 0)
                ConfigPath = args[0];
            Config = new Config();
            if (File.Exists(ConfigPath))
                JsonConvert.PopulateObject(File.ReadAllText(ConfigPath), Config);
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
            Client.Settings.WhoIsOnConnect = false;
            VoiceTimer = new Timer(DoVoicing);
            MutePolls = new List<MutePoll>();
            Client.ConnectAsync();
            while (true) Thread.Sleep(100);
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
                Client.SendRawMessage("NOTICE {0} :{1}", e.User.Nick, "Hi " + e.User.Nick + "! Hang in there, you'll be allowed to talk in a few moments.");
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
                var command = e.PrivateMessage.Message.Substring(1).Trim();
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
                    case "help":
                        Client.SendMessage("https://github.com/SirCmpwn/PokeBot/blob/master/README.md", e.PrivateMessage.Source);
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
                    case "restart":
                        if (isTrusted)
                            Process.GetCurrentProcess().Kill();
                        break;
                    case "source":
                        Client.SendMessage("https://github.com/SirCmpwn/PokeBot", e.PrivateMessage.Source);
                        break;
                    case "votelimit":
                        if (isTrusted && parameters.Length == 1)
                        {
                            if (int.TryParse(parameters[0], out Config.VoteThreshold))
                                Client.SendMessage("Set vote threshold to " + Config.VoteThreshold, e.PrivateMessage.Source);
                        }
                        break;
                    case "queue":
                        if (isTrusted)
                            Client.SendMessage("Current queue length is " + AwaitingVoice.Count, e.PrivateMessage.Source);
                        break;
					case "mute":
						if (isTrusted && parameters.Length == 2)
						{
							int duration;
							if (int.TryParse(parameters[1], out duration))
							{
								var target = Client.Channels[PrimaryChannel].Users.SingleOrDefault(u => u.Nick.Equals(parameters[0], StringComparison.InvariantCultureIgnoreCase));
								if (target == null)
									Client.SendMessage("That person isn't here.", e.PrivateMessage.Source);
								else if (duration < 1)
									Client.SendMessage("1 or more minutes, please.", e.PrivateMessage.Source);
								else
								{
									Client.ChangeMode(PrimaryChannel, "-v " + target.Nick);
		                            lock (VoiceLock)
		                            {
		                                AwaitingVoice.Add(new PendingVoice
		                                {
		                                    User = target,
		                                    ScheduledVoice = DateTime.Now.AddMinutes(duration),
		                                    Channel = PrimaryChannel
		                                });
		                            }
								}
							}
							else
							{
								Client.SendMessage("Usage: .mute <username> <minutes>", e.PrivateMessage.Source);
							}
						}
						break;
                    case "votemute":
                        if (parameters.Length != 1)
                            Client.SendMessage("Usage: .votemute <username>", e.PrivateMessage.Source);
                        else
                        {
                            var target = Client.Channels[PrimaryChannel].Users.SingleOrDefault(u => u.Nick.Equals(parameters[0], StringComparison.InvariantCultureIgnoreCase));
                            if (target == null)
                                Client.SendMessage("That person is not here.", e.PrivateMessage.Source);
                            else if (target.Nick == Client.User.Nick)
                                Client.SendMessage("Hahaha nice try", e.PrivateMessage.Source);
                            else if (AwaitingVoice.Any(v => v.User.Nick == target.Nick))
                                Client.SendMessage("That person is already muted.", e.PrivateMessage.Source);
                            else
                            {
                                var poll = MutePolls.SingleOrDefault(p => p.Nick == target.Nick);
                                if (poll == null)
                                {
                                    poll = new MutePoll { Nick = target.Nick, Expires = DateTime.Now.AddMinutes(10), Voters = new List<string>() };
                                    MutePolls.Add(poll);
                                }
                                if (poll.Voters.Contains(e.PrivateMessage.User.Hostname))
                                    Client.SendMessage("You have already voted to mute this person.", e.PrivateMessage.Source);
                                else
                                {
                                    Client.SendMessage("Your vote has been noted.", e.PrivateMessage.Source);
                                    poll.Voters.Add(e.PrivateMessage.User.Hostname);
                                    if (DateTime.Now > poll.Expires)
                                        poll.Votes = 0;
                                    poll.Votes++;
                                    poll.Expires = DateTime.Now.AddMinutes(10);
                                    if (poll.Votes >= Config.VoteThreshold)
                                    {
                                        Client.ChangeMode(PrimaryChannel, "-v " + poll.Nick);
                                        Client.SendMessage("You have been muted in " + PrimaryChannel +
                                            ". You will be unmuted soon, and I suggest you get your act together in the meantime.", target.Nick);
                                        MutePolls.Remove(poll);
                                        lock (VoiceLock)
                                        {
                                            AwaitingVoice.Add(new PendingVoice
                                            {
                                                User = target,
                                                ScheduledVoice = DateTime.Now.AddMinutes(30),
                                                Channel = PrimaryChannel
                                            });
                                        }
                                    }
                                }
                            }
                        }
                        break;
                }
            }
        }

        static void HandleConnectionComplete(object sender, EventArgs e)
        {
            PrimaryChannel = Config.Channels.FirstOrDefault();
            foreach (var channel in Config.Channels)
                Client.JoinChannel(channel);
            VoiceTimer.Change(1000, 1000);
        }
    }
}
