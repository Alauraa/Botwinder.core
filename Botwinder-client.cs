﻿using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Botwinder.entities;
using Discord;
using Discord.WebSocket;
using guid = System.UInt64;

namespace Botwinder.core
{
	public partial class BotwinderClient : IBotwinderClient, IDisposable
	{
		public readonly DbConfig DbConfig;
		private GlobalContext GlobalDb;
		private ServerContext ServerDb;
		public GlobalConfig GlobalConfig{ get; set; }
		public List<guid> SupportTeam{ get; set; }
		public Shard CurrentShard{ get; set; }

		public DiscordSocketClient DiscordClient;
		public Events Events;
		public string DbConnectionString;

		public DateTime TimeStarted{ get; private set; }
		private DateTime TimeConnected = DateTime.MaxValue;

		private bool IsInitialized = false;

		public bool IsConnected{
			get => this.DiscordClient.LoginState == LoginState.LoggedIn &&
			       this.DiscordClient.ConnectionState == ConnectionState.Connected &&
			       this._Connected;
			set => this._Connected = value;
		}

		private bool _Connected = false;

		private CancellationTokenSource MainUpdateCancel;
		private Task MainUpdateTask = null;

		public readonly List<IModule> Modules = new List<IModule>();

		private const string GameStatusConnecting = "Connecting...";
		private const string GameStatusUrl = "at http://botwinder.info";
		private readonly Regex RegexCommandParams = new Regex("\"[^\"]+\"|\\S+", RegexOptions.Compiled);
		private readonly Regex RegexEveryone = new Regex("(@everyone)|(@here)", RegexOptions.Compiled);

		public readonly ConcurrentDictionary<guid, Server> Servers = new ConcurrentDictionary<guid, Server>();
		public readonly Dictionary<string, Command> Commands = new Dictionary<string, Command>();
		public Dictionary<guid, Subscriber> Subscribers = new Dictionary<guid, Subscriber>();
		public Dictionary<guid, PartneredServer> PartneredServers = new Dictionary<guid, PartneredServer>();
		public List<Operation> CurrentOperations{ get; set; } = new List<Operation>();
		public Object OperationsLock{ get; set; } = new Object();
		public Object DbLock{ get; set; } = new Object();

		private bool ValidSubscribers = false;
		private readonly List<guid> LeaveNotifiedOwners = new List<guid>();
		private DateTime LastMessageAverageTime = DateTime.UtcNow;
		private int MessagesThisMinute = 0;

		public ConcurrentDictionary<guid, List<guid>> ClearedMessageIDs = new ConcurrentDictionary<guid, List<guid>>();
		public List<guid> AntispamMessageIDs = new List<guid>();


		public BotwinderClient(int shardIdOverride = -1)
		{
			this.TimeStarted = DateTime.UtcNow;
			this.DbConfig = DbConfig.Load();
			this.DbConnectionString = this.DbConfig.GetDbConnectionString();
			this.GlobalDb = GlobalContext.Create(this.DbConnectionString);
			this.ServerDb = ServerContext.Create(this.DbConnectionString);
			if( ++shardIdOverride > 0 )
				this.DbConfig.ForceShardId = shardIdOverride; //db shards count from one
		}

		public void Dispose()
		{
			Console.WriteLine("Disposing of the client.");

			if( this.CurrentShard != null )
			{
				GlobalContext dbContext = GlobalContext.Create(this.DbConnectionString);
				Shard shard = dbContext.Shards.FirstOrDefault(s => s.Id == this.CurrentShard.Id);
				if( shard != null )
				{
					shard.IsTaken = false;
					shard.IsConnecting = false;
					dbContext.SaveChanges();
				}
				dbContext.Dispose();
			}

			if( this.GlobalDb != null )
				this.GlobalDb.Dispose();
			this.GlobalDb = null;

			if( this.ServerDb != null )
				this.ServerDb.Dispose();
			this.ServerDb = null;

			//todo

			Console.WriteLine("Disposed of the client.");
		}

		public async Task Connect()
		{
			LoadConfig();

			lock(this.DbLock)
			{
				if( (this.DbConfig.ForceShardId == 0 && this.GlobalConfig.TotalShards > this.GlobalDb.Shards.Count()) ||
				    this.DbConfig.ForceShardId > this.GlobalDb.Shards.Count() + 1 ) //they start at 1...
				{
					Console.WriteLine("BotwinderClient: TotalShards (or forceId) exceeds the Shards count!!!");
					Dispose();
					Environment.Exit(1);
				}
			}

			//Find a shard for grabs.
			if( this.DbConfig.ForceShardId > 0 )
				this.CurrentShard = this.GlobalDb.Shards.FirstOrDefault(s => s.Id == this.DbConfig.ForceShardId);
			else
			{
				Shard GetShard()
				{
					lock(this.DbLock)
						this.CurrentShard = this.GlobalDb.Shards.FirstOrDefault(s => s.IsTaken == false);
					return this.CurrentShard;
				}

				Console.WriteLine("BotwinderClient: Waiting for a shard...");
				while( GetShard() == null )
				{
					await Task.Delay(Utils.Random.Next(5000, 10000));
				}

				this.CurrentShard.IsTaken = true;
				lock(this.DbLock)
					this.GlobalDb.SaveChanges();
			}
			Console.WriteLine($"BotwinderClient: Shard {this.CurrentShard.Id - 1} taken.");

			this.CurrentShard.ResetStats(this.TimeStarted);

			DiscordSocketConfig config = new DiscordSocketConfig();
			config.ShardId = (int) this.CurrentShard.Id - 1; //Shard's start at one in the database.
			config.TotalShards = (int) this.GlobalConfig.TotalShards;
			config.LogLevel = this.GlobalConfig.LogDebug ? LogSeverity.Debug : LogSeverity.Warning;
			config.DefaultRetryMode = RetryMode.Retry502 & RetryMode.RetryRatelimit & RetryMode.RetryTimeouts;
			config.AlwaysDownloadUsers = this.DbConfig.DownloadUsers;
			config.LargeThreshold = 100;
			config.HandlerTimeout = null;
			config.MessageCacheSize = 100;
			config.ConnectionTimeout = 300000;

			this.DiscordClient = new DiscordSocketClient(config);

			/*if( this.GlobalConfig.LogDebug )
			{
				this.DiscordClient.Log += message => {
					Console.WriteLine($"[${message.Severity}] ${message.Message}\n  Source: ${message.Source}");
					return Task.CompletedTask;
				};
			}*/

			this.DiscordClient.Connecting += OnConnecting;
			this.DiscordClient.Connected += OnConnected;
			this.DiscordClient.Ready += OnReady;
			this.DiscordClient.Disconnected += OnDisconnected;
			this.Events = new Events(this.DiscordClient);
			this.Events.MessageReceived += OnMessageReceived;
			this.Events.MessageUpdated += OnMessageUpdated;
			this.Events.LogEntryAdded += Log;
			this.Events.Exception += Log;
			this.Events.Connected += async () => await this.DiscordClient.SetGameAsync(GameStatusUrl);
			this.Events.Initialize += InitCommands;
			this.Events.Initialize += InitModules;
			this.Events.GuildAvailable += OnGuildAvailable;
			this.Events.JoinedGuild += OnGuildJoined;
			this.Events.LeftGuild += OnGuildLeft;
			this.Events.GuildUpdated += OnGuildUpdated;
			this.Events.UserJoined += OnUserJoined;
			this.Events.UserUpdated += OnUserUpdated;
			this.Events.GuildMemberUpdated += OnGuildMemberUpdated;
			this.Events.GuildMembersDownloaded += OnGuildMembersDownloaded;

			await this.DiscordClient.LoginAsync(TokenType.Bot, this.GlobalConfig.DiscordToken);
			await this.DiscordClient.StartAsync();
		}

		private void LoadConfig()
		{
			Console.WriteLine("BotwinderClient: Loading configuration...");

			lock(this.DbLock)
			{
				bool save = false;
				if( !this.GlobalDb.GlobalConfigs.Any() )
				{
					this.GlobalDb.GlobalConfigs.Add(new GlobalConfig());
					save = true;
				}
				if( !this.GlobalDb.Shards.Any() )
				{
					this.GlobalDb.Shards.Add(new Shard(){Id = 0});
					save = true;
				}

				if( save )
					this.GlobalDb.SaveChanges();

				this.GlobalConfig = this.GlobalDb.GlobalConfigs.FirstOrDefault(c => c.ConfigName == this.DbConfig.ConfigName);
				if( this.GlobalConfig == null )
				{
					this.GlobalConfig = new GlobalConfig(){ConfigName = this.DbConfig.ConfigName};
					this.GlobalDb.GlobalConfigs.Add(this.GlobalConfig);
					this.GlobalDb.SaveChanges();
					Console.WriteLine("BotwinderClient: Configuration created.");
					Environment.Exit(0);
				}

				this.SupportTeam = this.GlobalDb.SupportTeam.Select(u => u.UserId).ToList();
			}

			Console.WriteLine("BotwinderClient: Configuration loaded.");
		}

//Events
		private async Task OnConnecting()
		{
			//Some other node is already connecting, wait.
			if( this.DbConfig.UseShardLock )
			{
				bool IsAnyShardConnecting()
				{
					lock(this.DbLock)
						return this.GlobalDb.Shards.Any(s => s.IsConnecting);
				}

				bool awaited = false;
				while( IsAnyShardConnecting() )
				{
					if( !awaited )
						Console.WriteLine("BotwinderClient: Waiting for other shards to connect...");

					awaited = true;
					await Task.Delay(Utils.Random.Next(5000, 10000));
				}

				this.CurrentShard.IsConnecting = true;
				lock(this.DbLock)
					this.GlobalDb.SaveChanges();
				if( awaited )
					await Task.Delay(5000); //Ensure sufficient delay between connecting shards.
			}

			Console.WriteLine("BotwinderClient: Connecting...");
		}

		private async Task OnConnected()
		{
			Console.WriteLine("BotwinderClient: Connected.");

			try
			{
				if( this.DbConfig.UseShardLock )
				{
					this.CurrentShard.IsConnecting = false;
					lock(this.DbLock)
						this.GlobalDb.SaveChanges();
				}

				this.TimeConnected = DateTime.Now;
				await this.DiscordClient.SetGameAsync(GameStatusConnecting);
			}
			catch(Exception e)
			{
				await LogException(e, "--OnConnected");
			}
		}

		private Task OnReady()
		{
			if( this.GlobalConfig.LogDebug )
				Console.WriteLine("BotwinderClient: Ready.");

			if( this.MainUpdateTask == null )
			{
				this.MainUpdateCancel = new CancellationTokenSource();
				this.MainUpdateTask = Task.Factory.StartNew(MainUpdate, this.MainUpdateCancel.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
				this.MainUpdateTask.Start();
			}

			return Task.CompletedTask;
		}

		private async Task OnDisconnected(Exception exception)
		{
			Console.WriteLine("BotwinderClient: Disconnected.");
			this.IsConnected = false;
			this.CurrentShard.Disconnects++;

			if( exception.Message != "WebSocket connection was closed" ) //hack to not spam my logs
				await LogException(exception, "--D.NET Client Disconnected");

			try
			{
				if( this.Events.Disconnected != null )
					await this.Events.Disconnected(exception);
			}
			catch(Exception e)
			{
				await LogException(e, "--Events.Disconnected");
			}

			Dispose();
			Console.WriteLine("Shutting down.");
			Environment.Exit(0); //HACK - The library often reconnects in really shitty way and no longer works
		}

// Message events
		private async Task OnMessageReceived(SocketMessage message)
		{
			if( !this.IsConnected )
				return;

			try
			{
				if( this.GlobalConfig.LogDebug )
					Console.WriteLine("BotwinderClient: MessageReceived on thread " + Thread.CurrentThread.ManagedThreadId);

				this.CurrentShard.MessagesTotal++;
				this.MessagesThisMinute++;

				if( !(message.Channel is SocketTextChannel channel) )
				{
					await LogMessage(LogType.Pm, null, message);
					return;
				}

				Server server;
				if( !this.Servers.ContainsKey(channel.Guild.Id) || (server = this.Servers[channel.Guild.Id]) == null )
					return;
				if( server.Config.IgnoreBots && message.Author.IsBot || server.Config.IgnoreEveryone && this.RegexEveryone.IsMatch(message.Content) )
					return;

				bool commandExecuted = false;
				string prefix;
				if( (!string.IsNullOrWhiteSpace(server.Config.CommandPrefix) && message.Content.StartsWith(prefix = server.Config.CommandPrefix)) ||
				    (!string.IsNullOrWhiteSpace(server.Config.CommandPrefixAlt) && message.Content.StartsWith(prefix = server.Config.CommandPrefixAlt)) )
					commandExecuted = await HandleCommand(server, channel, message, prefix);

				if( !commandExecuted && message.MentionedUsers.Any(u => u.Id == this.DiscordClient.CurrentUser.Id) )
					await HandleMentionResponse(server, channel, message);
			}
			catch(Exception exception)
			{
				await LogException(exception, "--OnMessageReceived");
			}
		}

		private async Task OnMessageUpdated(SocketMessage originalMessage, SocketMessage updatedMessage, ISocketMessageChannel iChannel)
		{
			if( !this.IsConnected || originalMessage.Content == updatedMessage.Content )
				return;

			try
			{
				Server server;
				if( !(iChannel is SocketTextChannel channel) || updatedMessage?.Author == null || !this.Servers.ContainsKey(channel.Guild.Id) || (server = this.Servers[channel.Guild.Id]) == null || server.Config == null)
					return;
				if( server.Config.IgnoreBots && updatedMessage.Author.IsBot || server.Config.IgnoreEveryone && this.RegexEveryone.IsMatch(updatedMessage.Content) )
					return;

				bool commandExecuted = false;
				if( server.Config.ExecuteOnEdit )
				{
					string prefix;
					if( (!string.IsNullOrWhiteSpace(server.Config.CommandPrefix) && updatedMessage.Content.StartsWith(prefix = server.Config.CommandPrefix)) ||
					    (!string.IsNullOrWhiteSpace(server.Config.CommandPrefixAlt) && updatedMessage.Content.StartsWith(prefix = server.Config.CommandPrefixAlt)) )
						commandExecuted = await HandleCommand(server, channel, updatedMessage, prefix);
				}
			}
			catch(Exception exception)
			{
				await LogException(exception, "--OnMessageUpdated");
			}
		}

		private Task Log(ExceptionEntry exceptionEntry)
		{
			try
			{
				GlobalContext dbContext = GlobalContext.Create(this.DbConnectionString);
				dbContext.Exceptions.Add(exceptionEntry);
				dbContext.SaveChanges();
				dbContext.Dispose();
			}
			catch(Exception exception)
			{
				Console.WriteLine(exception.Message);
				Console.WriteLine(exception.StackTrace);
				Console.WriteLine($"--Failed to log exceptionEntry: {exceptionEntry.Message}");
				Console.WriteLine($"--Failed to log exceptionEntry: {exceptionEntry.Data} | ServerId:{exceptionEntry.ServerId}");
				if( exception.InnerException != null && exception.Message != exception.InnerException.Message )
				{
					Console.WriteLine(exception.InnerException.Message);
					Console.WriteLine(exception.InnerException.StackTrace);
					Console.WriteLine($"--Failed to log exceptionEntry: {exceptionEntry.Message}");
					Console.WriteLine($"--Failed to log exceptionEntry: {exceptionEntry.Data} | ServerId:{exceptionEntry.ServerId}");
				}
			}

			return Task.CompletedTask;
		}

		private Task Log(LogEntry logEntry)
		{
			GlobalContext dbContext = GlobalContext.Create(this.DbConnectionString);
			dbContext.Log.Add(logEntry);
			dbContext.SaveChanges();
			dbContext.Dispose();

			return Task.CompletedTask;
		}

//Update
		private async Task MainUpdate()
		{
			if( this.GlobalConfig.LogDebug )
				Console.WriteLine("BotwinderClient: MainUpdate started.");

			while( !this.MainUpdateCancel.IsCancellationRequested )
			{
				if( this.GlobalConfig.LogDebug )
					Console.WriteLine("BotwinderClient: MainUpdate loop triggered at: " + Utils.GetTimestamp(DateTime.UtcNow));

				DateTime frameTime = DateTime.UtcNow;

				if( !this.IsInitialized )
				{
					if( this.GlobalConfig.LogDebug )
						Console.WriteLine("BotwinderClient: Initialized.");
					try
					{
						this.IsInitialized = true;
						await this.Events.Initialize();
					}
					catch(Exception exception)
					{
						await LogException(exception, "--Events.Initialize");
					}
				}

				if( this.DiscordClient.ConnectionState != ConnectionState.Connected ||
				    this.DiscordClient.LoginState != LoginState.LoggedIn ||
				    DateTime.Now - this.TimeConnected < TimeSpan.FromSeconds(this.GlobalConfig.InitialUpdateDelay) )
				{
					await Task.Delay(10000);
					continue;
				}

				if( !this.IsConnected )
				{
					try
					{
						this.IsConnected = true;
						await this.Events.Connected();
					}
					catch(Exception exception)
					{
						await LogException(exception, "--Events.Connected");
					}

					continue; //Don't run update in the same loop as init.
				}

				try
				{
					if( this.GlobalConfig.LogDebug )
						Console.WriteLine("BotwinderClient: Update loop triggered at: " + Utils.GetTimestamp(DateTime.UtcNow));

					await Update();
				}
				catch(Exception exception)
				{
					await LogException(exception, "--Update");
				}

				TimeSpan deltaTime = DateTime.UtcNow - frameTime;
				if( this.GlobalConfig.LogDebug )
					Console.WriteLine($"BotwinderClient: MainUpdate loop took: {deltaTime.TotalMilliseconds} ms");
				await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(this.GlobalConfig.TotalShards * 1000, (TimeSpan.FromSeconds(1f / this.GlobalConfig.TargetFps) - deltaTime).TotalMilliseconds)));
			}
		}

		private async Task Update()
		{
			if( this.GlobalConfig.LogDebug )
				Console.WriteLine("BotwinderClient: UpdateSubscriptions loop triggered at: " + Utils.GetTimestamp(DateTime.UtcNow));
			await UpdateSubscriptions();

			if( this.GlobalConfig.EnforceRequirements && this.ValidSubscribers )
			{
				if( this.GlobalConfig.LogDebug )
					Console.WriteLine("BotwinderClient: BailBadServers loop triggered at: " + Utils.GetTimestamp(DateTime.UtcNow));
				await BailBadServers();
			}

			if( this.GlobalConfig.LogDebug )
				Console.WriteLine("BotwinderClient: UpdateShardStats loop triggered at: " + Utils.GetTimestamp(DateTime.UtcNow));
			UpdateShardStats();

			if( this.GlobalConfig.LogDebug )
				Console.WriteLine("BotwinderClient: UpdateServerStats loop triggered at: " + Utils.GetTimestamp(DateTime.UtcNow));
			await UpdateServerStats();

			if( this.GlobalConfig.LogDebug )
				Console.WriteLine("BotwinderClient: UpdateServerConfigs loop triggered at: " + Utils.GetTimestamp(DateTime.UtcNow));
			await UpdateServerConfigs();

			if( this.GlobalConfig.LogDebug )
				Console.WriteLine("BotwinderClient: UpdateModules loop triggered at: " + Utils.GetTimestamp(DateTime.UtcNow));
			await UpdateModules();

			lock(this.DbLock)
			{
				if( this.GlobalConfig.LogDebug )
					Console.WriteLine("BotwinderClient: SaveDatabase loop triggered at: " + Utils.GetTimestamp(DateTime.UtcNow));
				this.GlobalDb.SaveChanges();
				this.ServerDb.SaveChanges();
			}
		}

		private void UpdateShardStats()
		{
			GlobalContext dbContext = GlobalContext.Create(this.DbConnectionString);
			this.CurrentShard = dbContext.Shards.FirstOrDefault(s => s.Id == this.CurrentShard.Id);

			if( DateTime.UtcNow - this.LastMessageAverageTime > TimeSpan.FromMinutes(1) )
			{
				this.CurrentShard.MessagesPerMinute = this.MessagesThisMinute;
				this.MessagesThisMinute = 0;
				this.LastMessageAverageTime = DateTime.UtcNow;
			}

			this.CurrentShard.TimeStarted = this.TimeStarted;
			this.CurrentShard.OperationsActive = this.CurrentOperations.Count;
			this.CurrentShard.ThreadsActive = Process.GetCurrentProcess().Threads.Count;
			this.CurrentShard.MemoryUsed = GC.GetTotalMemory(false) / 1000000;
			this.CurrentShard.ServerCount = this.Servers.Count;
			this.CurrentShard.UserCount = this.DiscordClient.Guilds.Sum(s => s.MemberCount);

			dbContext.SaveChanges();
			dbContext.Dispose();
		}

		private async Task UpdateServerStats()
		{
			foreach( KeyValuePair<guid, Server> pair in this.Servers )
			{
				ServerStats stats = null;
				lock(this.DbLock)
				if( (stats = this.ServerDb.ServerStats.FirstOrDefault(s => s.ServerId == pair.Key)) == null &&
				    (stats = this.ServerDb.ServerStats.Local.FirstOrDefault(s => s.ServerId == pair.Key)) == null)
				{
					stats = new ServerStats();
					stats.ServerId = pair.Value.Id;
					this.ServerDb.ServerStats.Add(stats);
				}

				DateTime joinedAt = DateTime.UtcNow;
				if( pair.Value.Guild.CurrentUser?.JoinedAt != null )
					joinedAt = pair.Value.Guild.CurrentUser.JoinedAt.Value.UtcDateTime;
					//Although D.NET lists this as nullable, D.API always provides the value. It is safe to assume that it's always there.

				if( stats.JoinedTimeFirst == DateTime.MaxValue ) //This is the first time that we joined the server.
				{
					stats.JoinedTimeFirst = joinedAt;
					stats.JoinedCount = 1;
				}

				if( stats.JoinedTime != joinedAt )
				{
					stats.JoinedTime = joinedAt;
					stats.JoinedCount++;
				}

				stats.ShardId = this.CurrentShard.Id - 1;
				stats.ServerName = pair.Value.Guild.Name;
				stats.OwnerId = pair.Value.Guild.OwnerId;
				if( pair.Value.Guild.Owner != null )
					stats.OwnerName = pair.Value.Guild.Owner.GetUsername();
				stats.IsDiscordPartner = pair.Value.Guild.VoiceRegionId.StartsWith("vip");
				stats.UserCount = pair.Value.Guild.MemberCount;

				if( string.IsNullOrEmpty(pair.Value.Config.InviteUrl) )
				{
					ServerContext dbContext = ServerContext.Create(this.DbConnectionString);

					try
					{
						dbContext.ServerConfigurations.First(s => s.ServerId == pair.Value.Id).InviteUrl =
							(await pair.Value.Guild.DefaultChannel.CreateInviteAsync(0)).Url;
					}
					catch(Exception) { }

					dbContext.SaveChanges();
					dbContext.Dispose();
				}
			}
		}

		private Task UpdateServerConfigs()
		{
			ServerContext dbContext = ServerContext.Create(this.DbConnectionString);
			foreach( KeyValuePair<guid, Server> pair in this.Servers )
			{
				pair.Value.ReloadConfig(this.DbConnectionString, dbContext, this.Commands);
			}
			dbContext.Dispose();
			return Task.CompletedTask;
		}

		private Task UpdateSubscriptions()
		{
			GlobalContext dbContext = GlobalContext.Create(this.DbConnectionString);

			this.Subscribers?.Clear();
			this.Subscribers = dbContext.Subscribers.ToDictionary(s => s.UserId);

			this.PartneredServers?.Clear();
			this.PartneredServers = dbContext.PartneredServers.ToDictionary(s => s.ServerId);

			this.ValidSubscribers = this.Subscribers.Any() && this.PartneredServers.Any();
			return Task.CompletedTask;
		}

//Modules
		private async Task InitModules()
		{
			List<Command> newCommands;
			foreach( IModule module in this.Modules )
			{
				try
				{
					module.HandleException += async (e, d, id) =>
						await LogException(e, "--Module." + module.ToString() + " | " + d, id);
					newCommands = module.Init(this);

					foreach( Command cmd in newCommands )
					{
						if( this.Commands.ContainsKey(cmd.Id) )
						{
							this.Commands[cmd.Id] = cmd;
							continue;
						}

						this.Commands.Add(cmd.Id, cmd);
					}
				}
				catch(Exception exception)
				{
					await LogException(exception, "--ModuleInit." + module.ToString());
				}
			}
		}

		private async Task UpdateModules()
		{
			IEnumerable<IModule> modules = this.Modules.Where(m => m.DoUpdate);
			foreach( IModule module in modules )
			{
				if( this.GlobalConfig.LogDebug )
					Console.WriteLine($"BotwinderClient: ModuleUpdate.{module.ToString()} triggered at: " + Utils.GetTimestamp(DateTime.UtcNow));

				DateTime frameTime = DateTime.UtcNow;

				if( this.DiscordClient.ConnectionState != ConnectionState.Connected ||
					this.DiscordClient.LoginState != LoginState.LoggedIn )
					break;

				try
				{
					await module.Update(this);
				}
				catch(Exception exception)
				{
					await LogException(exception, "--ModuleUpdate." + module.ToString());
				}

				if( this.GlobalConfig.LogDebug )
					Console.WriteLine($"BotwinderClient: ModuleUpdate.{module.ToString()} took: {(DateTime.UtcNow - frameTime).TotalMilliseconds} ms");
			}
		}

//Commands
		private void GetCommandAndParams(string message, out string commandString, out string trimmedMessage, out string[] parameters)
		{
			trimmedMessage = "";
			parameters = null;

			MatchCollection regexMatches = this.RegexCommandParams.Matches(message);
			if( regexMatches.Count == 0 )
			{
				commandString = message.Trim();
				return;
			}

			commandString = regexMatches[0].Value;

			if( regexMatches.Count > 1 )
			{
				trimmedMessage = message.Substring(regexMatches[1].Index).Trim('\"', ' ', '\n');
				Match[] matches = new Match[regexMatches.Count];
				regexMatches.CopyTo(matches, 0);
				parameters = matches.Skip(1).Select(p => p.Value).ToArray();
				for( int i = 0; i < parameters.Length; i++ )
					parameters[i] = parameters[i].Trim('"');
			}
		}

		private async Task<bool> HandleCommand(Server server, SocketTextChannel channel, SocketMessage message, string prefix)
		{
			GetCommandAndParams(message.Content.Substring(prefix.Length), out string commandString, out string trimmedMessage, out string[] parameters);
			string originalCommandString = commandString;

			if( this.GlobalConfig.LogDebug )
				Console.WriteLine($"Command: {commandString} | {trimmedMessage}");

			CommandOptions commandOptions = server.GetCommandOptions(commandString);

			if( server.Commands.ContainsKey(commandString) ||
			    (server.CustomAliases.ContainsKey(commandString) &&
			     server.Commands.ContainsKey(commandString = server.CustomAliases[commandString].CommandId)) )
			{
				Command command = server.Commands[commandString];
				if( command.IsAlias && !string.IsNullOrEmpty(command.ParentId) ) //Internal, not-custom alias.
					command = server.Commands[command.ParentId];

				CommandArguments args = new CommandArguments(this, command, server, channel, message, originalCommandString, trimmedMessage, parameters, commandOptions);

				if( command.CanExecute(this, server, channel, message.Author as SocketGuildUser) )
					return await command.Execute(args);
			}
			else if( server.CustomCommands.ContainsKey(commandString) ||
			         (server.CustomAliases.ContainsKey(commandString) &&
			          server.CustomCommands.ContainsKey(commandString = server.CustomAliases[commandString].CommandId)) )
			{
				if( server.CustomCommands[commandString].CanExecute(this, server, channel, message.Author as SocketGuildUser) )
					return await HandleCustomCommand(server, server.CustomCommands[commandString], commandOptions, channel, message);
			}

			return false;
		}

		private async Task<bool> HandleCustomCommand(Server server, CustomCommand cmd, CommandOptions commandOptions, SocketTextChannel channel, SocketMessage message)
		{
			try
			{
				if( commandOptions != null && commandOptions.DeleteRequest &&
				    channel.Guild.CurrentUser.GuildPermissions.ManageMessages && !message.Deleted )
					await message.DeleteAsync();
			}catch( Exception ) { }

			//todo - rewrite using string builder...
			string msg = cmd.Response;

			if( msg.Contains("{sender}") || msg.Contains("{{sender}}") )
			{
				msg = msg.Replace("{{sender}}", "<@{0}>").Replace("{sender}", "<@{0}>");
				msg = string.Format(msg, message.Author.Id);
			}

			if( (msg.Contains("{mentioned}") || msg.Contains("{{mentioned}}")) && message.MentionedUsers != null )
			{
				string mentions = "";
				SocketUser[] mentionedUsers = message.MentionedUsers.ToArray();
				for( int i = 0; i < mentionedUsers.Length; i++ )
				{
					if( i != 0 )
						mentions += (i == mentionedUsers.Length - 1) ? " and " : ", ";

					mentions += "<@" + mentionedUsers[i].Id + ">";
				}

				if( string.IsNullOrEmpty(mentions) )
				{
					msg = msg.Replace("{{mentioned}}", "Nobody").Replace("{mentioned}", "Nobody");
				}
				else
				{
					msg = msg.Replace("{{mentioned}}", "{0}").Replace("{mentioned}", "{0}");
					msg = string.Format(msg, mentions);
				}
			}

			if( server.Config.IgnoreEveryone )
				msg = msg.Replace("@everyone", "@-everyone").Replace("@here", "@-here");
			await SendRawMessageToChannel(channel, msg);
			return true;
		}


// Guild events
		private async Task OnGuildJoined(SocketGuild guild)
		{
			try
			{
				await OnGuildAvailable(guild);

				string msg = Localisation.SystemStrings.GuildJoined;
				if( !IsPartner(guild.Id) && !IsSubscriber(guild.OwnerId) )
					msg += Localisation.SystemStrings.GuildJoinedTrial;

				try
				{
					await guild.Owner.SendMessageSafe(msg);
				}
				catch(Exception) { }
			}
			catch(Exception exception)
			{
				await LogException(exception, "--OnGuildJoined", guild.Id);
			}
		}

#pragma warning disable 1998
		private async Task OnGuildUpdated(SocketGuild originalGuild, SocketGuild updatedGuild)
#pragma warning restore 1998
		{
			if( !this.Servers.ContainsKey(originalGuild.Id) )
				return;

			this.Servers[originalGuild.Id].Guild = updatedGuild;
		}

		private async Task OnGuildLeft(SocketGuild guild)
		{
			try
			{
				if( !this.Servers.ContainsKey(guild.Id) )
					return;

				for(int i = this.CurrentOperations.Count -1; i >= 0; i--)
				{
					if( this.CurrentOperations[i].CommandArgs.Server.Id == guild.Id )
						this.CurrentOperations[i].Cancel();
				}

				this.Servers.Remove(guild.Id);
			}
			catch(Exception exception)
			{
				await LogException(exception, "--OnGuildLeft", guild.Id);
			}
		}

		private async Task OnGuildAvailable(SocketGuild guild)
		{
			ServerContext dbContext = null;
			try
			{
				while( !this.IsInitialized )
					await Task.Delay(1000);

				Server server;
				dbContext = ServerContext.Create(this.DbConnectionString);
				if( this.Servers.ContainsKey(guild.Id) )
				{
					server = this.Servers[guild.Id];
					server.ReloadConfig(this.DbConnectionString, dbContext, this.Commands);
				}
				else
				{
					server = new Server(guild);
					server.LoadConfig(this.DbConnectionString, dbContext, this.Commands);
					server.Localisation = GlobalContext.Create(this.DbConnectionString).Localisations.FirstOrDefault(l => l.Id == server.Config.LocalisationId);
					this.Servers.Add(server.Id, server);
				}
			}
			catch(Exception exception)
			{
				await LogException(exception, "--OnGuildAvailable", guild.Id);
			}
			finally
			{
				dbContext?.Dispose();
			}
		}

		private async Task BailBadServers()
		{
			try
			{
				List<Server> serversToLeave = new List<Server>();

				foreach( KeyValuePair<guid, Server> pair in this.Servers )
				{
					try
					{
						//Trial count exceeded
						ServerStats stats;
						lock(this.DbLock)
							stats = this.ServerDb.ServerStats.FirstOrDefault(s => s.ServerId == pair.Value.Id);
						bool joinedCountExceeded = stats != null && stats.JoinedCount > this.GlobalConfig.VipTrialJoins;
						bool trialTimeExceeded = pair.Value.Guild.CurrentUser?.JoinedAt != null &&
						                         DateTime.UtcNow - pair.Value.Guild.CurrentUser.JoinedAt.Value.ToUniversalTime() > TimeSpan.FromHours(this.GlobalConfig.VipTrialHours);

						//Partnered servers
						if( !(IsPartner(pair.Value.Id) || IsSubscriber(pair.Value.Guild.OwnerId)) &&
						    (joinedCountExceeded || trialTimeExceeded) )
						{
							if( serversToLeave.Contains(pair.Value) )
								continue;

							serversToLeave.Add(pair.Value);
							if( !this.LeaveNotifiedOwners.Contains(pair.Value.Guild.OwnerId) )
							{
								this.LeaveNotifiedOwners.Add(pair.Value.Guild.OwnerId);
								try
								{
									await pair.Value.Guild.Owner.SendMessageSafe(Localisation.SystemStrings.VipPmLeaving);
								}
								catch(Exception) { }
							}
							continue;
						}

						//Blacklisted servers
						lock(this.DbLock)
						if( !serversToLeave.Contains(pair.Value) &&
						    this.GlobalDb.Blacklist.Any(b => b.Id == pair.Value.Id || b.Id == pair.Value.Guild.OwnerId) )
						{
							serversToLeave.Add(pair.Value);
							continue;
						}
					}
					catch(Exception exception)
					{
						await LogException(exception, "--BailBadServers", pair.Value.Id);
					}
				}

				foreach( Server server in serversToLeave )
				{
					await server.Guild.LeaveAsync();
				}
			}
			catch(Exception exception)
			{
				await LogException(exception, "--BailBadServers");
			}
		}

		private Task OnGuildMembersDownloaded(SocketGuild guild)
		{
			ServerContext dbContext = ServerContext.Create(this.DbConnectionString);
			List<Username> usernames = dbContext.Usernames.Where(u => u.ServerId == guild.Id).ToList();
			List<Nickname> nicknames = dbContext.Nicknames.Where(u => u.ServerId == guild.Id).ToList();

			foreach( SocketGuildUser user in guild.Users )
			{
				if( !usernames.Any(u => u.UserId == user.Id && u.Name == user.Username) )
				{
					Username username = new Username(){
						ServerId = user.Guild.Id,
						UserId = user.Id,
						Name = user.Username
					};
					usernames.Add(username);
					dbContext.Usernames.Add(username);
				}

				if( !string.IsNullOrEmpty(user.Nickname) &&
				    !nicknames.Any(u => u.UserId == user.Id && u.Name == user.Nickname) )
				{
					Nickname nickname = new Nickname(){
						ServerId = user.Guild.Id,
						UserId = user.Id,
						Name = user.Nickname
					};
					nicknames.Add(nickname);
					dbContext.Nicknames.Add(nickname);
				}
			}

			dbContext.SaveChanges();
			dbContext.Dispose();
			return Task.CompletedTask;
		}

// User events
		private Task OnGuildMemberUpdated(SocketGuildUser originalUser, SocketGuildUser updatedUser)
		{
			UpdateUsernames(updatedUser);

			return Task.CompletedTask;
		}

		private Task OnUserJoined(SocketGuildUser user)
		{
			UpdateUsernames(user);

			return Task.CompletedTask;
		}

		private Task OnUserUpdated(SocketUser originalUser, SocketUser updatedUser)
		{
			if( updatedUser is SocketGuildUser )
			{
				UpdateUsernames(updatedUser as SocketGuildUser);
			}

			return Task.CompletedTask;
		}

		private void UpdateUsernames(SocketGuildUser user)
		{
			ServerContext dbContext = ServerContext.Create(this.DbConnectionString);

			if( !dbContext.Usernames.Any(u => u.ServerId == user.Guild.Id && u.UserId == user.Id && u.Name == user.Username) )
			{
				dbContext.Usernames.Add(new Username(){
					ServerId = user.Guild.Id,
					UserId = user.Id,
					Name = user.Username
				});
			}

			if( !string.IsNullOrEmpty(user.Nickname) &&
			    !dbContext.Nicknames.Any(u => u.ServerId == user.Guild.Id && u.UserId == user.Id && u.Name == user.Nickname) )
			{
				dbContext.Nicknames.Add(new Nickname(){
					ServerId = user.Guild.Id,
					UserId = user.Id,
					Name = user.Nickname
				});
			}

			dbContext.SaveChanges();
			dbContext.Dispose();
		}
	}
}
