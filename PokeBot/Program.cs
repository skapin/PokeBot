using System;
using System.IO;
using Newtonsoft.Json;
using ChatSharp;
using ChatSharp.Events;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;

namespace PokeBot
{
    static class Program
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
            while (true)
                Client.SendRawMessage(Console.ReadLine());
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
                    parameters = command.Substring(command.IndexOf(" ") + 1).SafeSplit(' ').Select(s => s.Trim('\"')).ToArray();
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
                    case "livehelp":
                        if (isTrusted)
                            Client.SendMessage("http://tpp.aninext.tv/commands.html", e.PrivateMessage.Source);
                        break;
                    case "money":
                        if (isTrusted)
                        {
                            if (parameters.Length != 1)
                                Client.SendMessage("RTFM", e.PrivateMessage.Source);
                            else
                            {
                                int balance;
                                if (!int.TryParse(parameters[0], out balance))
                                    Client.SendMessage("Numbers only, moron", e.PrivateMessage.Source);
                                else
                                {
                                    var updates = GetUpdates();
                                    updates.balance = balance;
                                    SaveUpdates(updates, e.PrivateMessage.User.Nick);
                                    Client.SendMessage("Updated.", e.PrivateMessage.Source);
                                }
                            }
                        }
                        break;
                    case "update":
                        if (isTrusted)
                        {
                            if (parameters.Length < 2 || parameters.Length > 4)
                                Client.SendMessage("RTFM", e.PrivateMessage.Source);
                            else
                            {
                                var updates = GetUpdates();
                                var update = new LiveUpdates.Update();
                                var categories = new string[] { "Travel", "Roster", "Items", "Battle", "Commentary", "Meta" };
                                var category = categories.SingleOrDefault(s => s.Equals(parameters[0], StringComparison.InvariantCultureIgnoreCase));
                                if (category == null)
                                    Client.SendMessage("Valid categories: " + string.Join(", ", categories), e.PrivateMessage.Source);
                                update.category = category;
                                update.title = parameters[1];
                                if (parameters.Length > 2)
                                    update.description = parameters[2];
                                try
                                {
                                    if (parameters.Length > 3)
                                        update.time = GetTime(parameters[3]);
                                    else
                                        update.time = GetTime(DateTime.UtcNow);
                                }
                                catch
                                {
                                    Client.SendMessage("Invalid time.", e.PrivateMessage.Source);
                                    break;
                                }
                                updates.updates = updates.updates.Concat(new[] { update }).OrderByDescending(u => u.time).ToArray();
                                SaveUpdates(updates, e.PrivateMessage.User.Nick);
                                Client.SendMessage("Site updated. Use .undo " + update.time + " to undo that.", e.PrivateMessage.Source);
                            }
                        }
                        break;
                    case "undo":
                        if (isTrusted)
                        {
                            if (parameters.Length != 1)
                                Client.SendMessage("RTFM", e.PrivateMessage.Source);
                            else
                            {
                                int time;
                                if (!int.TryParse(parameters[0], out time))
                                    Client.SendMessage("RTFM", e.PrivateMessage.Source);
                                else
                                {
                                    var updates = GetUpdates();
                                    updates.updates = updates.updates.Where(u => u.time != time).OrderByDescending(u => u.time).ToArray();
                                    SaveUpdates(updates, e.PrivateMessage.User.Nick);
                                    Client.SendMessage("Undone. Try to screw up less, please.", e.PrivateMessage.Source);
                                }
                            }
                        }
                        break;
                    case "levelup":
                        if (isTrusted)
                        {
                            if (parameters.Length != 1)
                                Client.SendMessage("RTFM", e.PrivateMessage.Source);
                            else
                            {
                                var updates = GetUpdates();
                                var pokemon = updates.party.SingleOrDefault(p => 
                                    p.name.Equals(parameters[0], StringComparison.InvariantCultureIgnoreCase) ||
                                    p.nickname.Equals(parameters[0], StringComparison.InvariantCultureIgnoreCase) ||
                                    p.species.Equals(parameters[0], StringComparison.InvariantCultureIgnoreCase));
                                if (pokemon == null)
                                    Client.SendMessage("That Pokemon isn't in our party, you moron", e.PrivateMessage.Source);
                                else
                                {
                                    pokemon.level++;
                                    SaveUpdates(updates, e.PrivateMessage.User.Nick);
                                    Client.SendMessage("Done. " + parameters[0] + " is level " + pokemon.level + " now.", e.PrivateMessage.Source);
                                }
                            }
                        }
                        break;
                    case "goal":
                        if (isTrusted)
                        {
                            if (parameters.Length != 1)
                                Client.SendMessage("RTFM", e.PrivateMessage.Source);
                            else
                            {
                                var updates = GetUpdates();
                                updates.goal = parameters[0];
                                SaveUpdates(updates, e.PrivateMessage.User.Nick);
                                Client.SendMessage("Updated goal.", e.PrivateMessage.Source);
                            }
                        }
                        break;
                    case "catch":
                        if (isTrusted)
                        {
                            if (parameters.Length != 4)
                                Client.SendMessage("RTFM", e.PrivateMessage.Source);
                            else
                            {
                                var updates = GetUpdates();
                                try
                                {
                                    var mon = new LiveUpdates.Pokemon();
                                    mon.species = parameters[0];
                                    mon.level = int.Parse(parameters[1]);
                                    mon.name = parameters[2];
                                    mon.nickname = parameters[3];
                                    updates.party = updates.party.Concat(new[] { mon }).ToArray();
                                }
                                catch
                                {
                                    Client.SendMessage("RTFM", e.PrivateMessage.Source);
                                    break;
                                }
                                SaveUpdates(updates, e.PrivateMessage.User.Nick);
                                Client.SendMessage("Added Pokemon.", e.PrivateMessage.Source);
                            }
                        }
                        break;
                    case "release":
                        if (isTrusted)
                        {
                            if (parameters.Length != 1)
                                Client.SendMessage("RTFM", e.PrivateMessage.Source);
                            else
                            {
                                var updates = GetUpdates();
                                var pokemon = updates.party.SingleOrDefault(p => 
                                                                            p.name.Equals(parameters[0], StringComparison.InvariantCultureIgnoreCase) ||
                                                                            p.nickname.Equals(parameters[0], StringComparison.InvariantCultureIgnoreCase) ||
                                                                            p.species.Equals(parameters[0], StringComparison.InvariantCultureIgnoreCase));
                                if (pokemon == null)
                                    Client.SendMessage("That Pokemon isn't in our party, you moron", e.PrivateMessage.Source);
                                else
                                {
                                    updates.party = updates.party.Where(p => p != pokemon).ToArray();
                                    SaveUpdates(updates, e.PrivateMessage.User.Nick);
                                    Client.SendMessage("Removed Pokemon.", e.PrivateMessage.Source);
                                }
                            }
                        }
                        break;
                    case "learn":
                        if (isTrusted)
                        {
                            if (parameters.Length != 2)
                                Client.SendMessage("RTFM", e.PrivateMessage.Source);
                            else
                            {
                                var updates = GetUpdates();
                                var pokemon = updates.party.SingleOrDefault(p => 
                                                                            p.name.Equals(parameters[0], StringComparison.InvariantCultureIgnoreCase) ||
                                                                            p.nickname.Equals(parameters[0], StringComparison.InvariantCultureIgnoreCase) ||
                                                                            p.species.Equals(parameters[0], StringComparison.InvariantCultureIgnoreCase));
                                if (pokemon == null)
                                    Client.SendMessage("That Pokemon isn't in our party, you moron", e.PrivateMessage.Source);
                                else
                                {
                                    if (pokemon.moves == null)
                                        pokemon.moves = new string[0];
                                    pokemon.moves = pokemon.moves.Concat(new[] { parameters[1] }).ToArray();
                                    SaveUpdates(updates, e.PrivateMessage.User.Nick);
                                    Client.SendMessage("Done.", e.PrivateMessage.Source);
                                }
                            }
                        }
                        break;
                    case "pickup":
                        if (isTrusted)
                        {
                            if (parameters.Length != 1)
                                Client.SendMessage("RTFM", e.PrivateMessage.Source);
                            else
                            {
                                var updates = GetUpdates();
                                var item = updates.inventory.SingleOrDefault(i => i.name.Equals(parameters[0], StringComparison.InvariantCultureIgnoreCase));
                                if (item == null)
                                {
                                    item = new LiveUpdates.Item { count = 0, name = parameters[0] };
                                    updates.inventory = updates.inventory.Concat(new[] { item }).ToArray();
                                }
                                item.count++;
                                SaveUpdates(updates, e.PrivateMessage.User.Nick);
                                Client.SendMessage("Done.", e.PrivateMessage.Source);
                            }
                        }
                        break;
                    case "drop":
                        if (isTrusted)
                        {
                            if (parameters.Length != 1)
                                Client.SendMessage("RTFM", e.PrivateMessage.Source);
                            else
                            {
                                var updates = GetUpdates();
                                var item = updates.inventory.SingleOrDefault(i => i.name.Equals(parameters[0], StringComparison.InvariantCultureIgnoreCase));
                                if (item == null)
                                {
                                    Client.SendMessage("We don't have that item.", e.PrivateMessage.Source);
                                    break;
                                }
                                item.count--;
                                if (item.count == 0)
                                    updates.inventory = updates.inventory.Where(i => i != item).ToArray();
                                SaveUpdates(updates, e.PrivateMessage.User.Nick);
                                Client.SendMessage("Done.", e.PrivateMessage.Source);
                            }
                        }
                        break;
                    case "forget":
                        if (isTrusted)
                        {
                            if (parameters.Length != 2)
                                Client.SendMessage("RTFM", e.PrivateMessage.Source);
                            else
                            {
                                var updates = GetUpdates();
                                var pokemon = updates.party.SingleOrDefault(p => 
                                                                            p.name.Equals(parameters[0], StringComparison.InvariantCultureIgnoreCase) ||
                                                                            p.nickname.Equals(parameters[0], StringComparison.InvariantCultureIgnoreCase) ||
                                                                            p.species.Equals(parameters[0], StringComparison.InvariantCultureIgnoreCase));
                                if (pokemon == null)
                                    Client.SendMessage("That Pokemon isn't in our party, you moron", e.PrivateMessage.Source);
                                else
                                {
                                    if (pokemon.moves == null)
                                        pokemon.moves = new string[0];
                                    var move = pokemon.moves.SingleOrDefault(p => p.Equals(parameters[1], StringComparison.InvariantCultureIgnoreCase));
                                    if (move == null)
                                    {
                                        Client.SendMessage(pokemon.name + " does not know " + parameters[1], e.PrivateMessage.Source);
                                        break;
                                    }
                                    pokemon.moves = pokemon.moves.Where(m => m != move).ToArray();
                                    SaveUpdates(updates, e.PrivateMessage.User.Nick);
                                    Client.SendMessage("Done.", e.PrivateMessage.Source);
                                }
                            }
                        }
                        break;
                    case "badge":
                        if (isTrusted)
                        {
                            if (parameters.Length != 1)
                                Client.SendMessage("RTFM", e.PrivateMessage.Source);
                            else
                            {
                                var updates = GetUpdates();
                                updates.badges = updates.badges.Concat(new [] { parameters[0].ToLower() }).ToArray();
                                SaveUpdates(updates, e.PrivateMessage.User.Nick);
                                Client.SendMessage("Done.", e.PrivateMessage.Source);
                            }
                        }
                        break;
                }
            }
        }

        static readonly int TPPStart = 1392254507;

        static int GetTime(DateTime when)
        {
            return (int)(when - new DateTime(1970, 1, 1, 0, 0, 0, 0)).TotalSeconds;
        }

        static int GetTime(string when)
        {
            int seconds = 0;
            var parts = when.Split('d', 'h', 'm', 's');
            seconds += int.Parse(parts[0]) * (60 * 60 * 24);
            seconds += int.Parse(parts[1]) * (60 * 60);
            seconds += int.Parse(parts[2]) * 60;
            seconds += int.Parse(parts[3]);
            return seconds + TPPStart;
        }

        static LiveUpdates GetUpdates()
        {
            var info = new ProcessStartInfo("git", "pull");
            info.WorkingDirectory = Config.UpdatePath;
            Process.Start(info).WaitForExit();
            return JsonConvert.DeserializeObject<LiveUpdates>(File.ReadAllText(Path.Combine(Config.UpdatePath, "feed.json")));
        }

        static void SaveUpdates(LiveUpdates foo, string user)
        {
            File.WriteAllText(Path.Combine(Config.UpdatePath, "feed.json"), JsonConvert.SerializeObject(foo, Formatting.Indented));
            Task.Factory.StartNew(() =>
            {
                var info = new ProcessStartInfo("git", "commit -am \"Update on behalf of " + user + "\"");
                info.WorkingDirectory = Config.UpdatePath;
                Process.Start(info).WaitForExit();
                info = new ProcessStartInfo("git", "push");
                info.WorkingDirectory = Config.UpdatePath;
                Process.Start(info).WaitForExit();
            });
        }

        static void HandleConnectionComplete(object sender, EventArgs e)
        {
            PrimaryChannel = Config.Channels.FirstOrDefault();
            foreach (var channel in Config.Channels)
                Client.JoinChannel(channel);
            VoiceTimer.Change(1000, 1000);
        }

        public static string[] SafeSplit(this string value, params char[] characters)
        {
            string[] result = new string[1];
            result[0] = "";
            bool inString = false, inChar = false;
            for (int i = 0; i < value.Length; i++)
            {
                bool foundChar = false;
                if (!inString && !inChar)
                {
                    foreach (char haystack in characters)
                    {
                        if (value[i] == haystack)
                        {
                            foundChar = true;
                            result = result.Concat(new [] { "" }).ToArray();
                            break;
                        }
                    }
                }
                if (!foundChar)
                {
                    result[result.Length - 1] += value[i];
                    if (value[i] == '"' && !inChar)
                        inString = !inString;
                    if (value[i] == '\'' && !inString)
                        inChar = !inChar;
                    if (value[i] == '\\')
                        result[result.Length - 1] += value[++i];
                }
            }
            return result;
        }
    }
}
