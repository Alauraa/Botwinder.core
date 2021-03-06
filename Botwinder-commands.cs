﻿using System;
using System.Collections;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Botwinder.entities;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using guid = System.UInt64;

namespace Botwinder.core
{
	public partial class BotwinderClient : IBotwinderClient, IDisposable
	{
		private readonly Regex RegexMentionHelp = new Regex(".*(help|commands).*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
		private readonly Regex RegexPrefixHelp = new Regex(".*(command character|prefix).*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
		private readonly Regex RegexHardwareHelp = new Regex(".*(hardware|server).*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
		private const string HardwareString = "I run on Keyra: <http://rhea-ayase.eu/persephone>\n```md\n" +
		                                      "|   [Motherboard][Supermicro X9DRi-LN4F+ Dual LGA2011 C602]\n" +
		                                      "|        2x [CPU][Intel Xeon E5-2690 @3.8GHz, 8 Cores](32t)\n" +
		                                      "|    16x [Memory][Supermicro 4GB DDR3-1600 ECC](64GB)\n" +
		                                      "|    1x [Storage][OCZ Vertex 450 128GB SSD]\n" +
		                                      "|    1x [Storage][Seagate Barracuda 3TB 7200RPM]\n" +
		                                      "|    4x [Storage][Hitachi NAS 4TB 7200RPM](raid5|write1.4GB/s)\n" +
		                                      "| 2x [CPU Cooler][Corsair H80i v2]\n" +
		                                      "|        4x [Fan][Noctua NF-F12]\n" +
		                                      "|        3x [Fan][Noctua NF-A14]\n" +
		                                      "|          [Case][be quiet! Dark Base Pro]\n" +
		                                      "|  [Power Supply][Corsair RM750x Gold]\n" +
		                                      "```\n" +
		                                      "...and I'm connected through an APU2C4 router, running RHEL, with 300/300 Mbps and ISP failover.";

		private async Task HandleMentionResponse(Server server, SocketTextChannel channel, SocketMessage message)
		{
			if( this.GlobalConfig.LogDebug )
				Console.WriteLine("BotwinderClient: MentionReceived");

			string responseString = "";

			if( this.RegexMentionHelp.Match(message.Content).Success )
				responseString = Localisation.SystemStrings.MentionHelp;
			else if( this.RegexPrefixHelp.Match(message.Content).Success )
				responseString = string.IsNullOrEmpty(server.Config.CommandPrefix) ? Localisation.SystemStrings.MentionPrefixEmpty : string.Format(Localisation.SystemStrings.MentionPrefix, server.Config.CommandPrefix);
			else if( this.RegexHardwareHelp.Match(message.Content).Success )
				responseString = HardwareString;
			else
				responseString = "<:BotwinderNomPing:438688419447570442>";

			if( !string.IsNullOrEmpty(responseString) )
				await SendRawMessageToChannel(channel, responseString);
		}

		private Task InitCommands()
		{
			Command newCommand = null;

// !stats
			newCommand = new Command("stats");
			newCommand.Type = CommandType.Standard;
			newCommand.IsCoreCommand = true;
			newCommand.Description = "Display all teh command numbers.";
			newCommand.RequiredPermissions = PermissionType.OwnerOnly;
			newCommand.OnExecute += async e => {
				await e.SendReplySafe("I'm counting! Do not disturb!! >_<");
				StringBuilder message = new StringBuilder("Lifetime Command stats:\n```md\n");

				try
				{
					GlobalContext dbContext = GlobalContext.Create(this.DbConnectionString);
					ServerContext serverContext = ServerContext.Create(this.DbConnectionString);

					string GetSpaces(string txt)
					{
						string spaces = "";
						for( int i = txt.Length; i <= 20; i++ )
							spaces += " ";
						return spaces;
					}

					Dictionary<string, int> count = new Dictionary<string, int>();

					foreach( Command cmd in this.Commands.Values )
					{
						string key, cmdName = key = cmd.Id;
						if( cmd.IsAlias && !string.IsNullOrEmpty(cmd.ParentId) )
							key = cmd.ParentId;

						//Please don't read this query :D

						bool IsCommand(LogEntry log)
						{
							return log.Type == LogType.Command && (log.Message?.StartsWith(
							    (serverContext.ServerConfigurations.First(c => c.ServerId == log.ServerId)?.CommandPrefix ?? "") + cmdName)
							    ?? false);
						}

						int cmdCount = dbContext.Log.Count(IsCommand);

						if( !count.ContainsKey(key) )
							count.Add(key, 0);
						count[key] += cmdCount;
					}

					foreach(KeyValuePair<string, int> pair in count.OrderByDescending(p => p.Value))
					{
						string newMessage = $"[{GetSpaces(pair.Key)}{pair.Key} ][ {pair.Value}{GetSpaces(pair.Value.ToString())}]\n";
						if( message.Length + newMessage.Length >= GlobalConfig.MessageCharacterLimit )
						{
							message.Append("```");
							await e.SendReplySafe(message.ToString());
							message.Clear();
							message.Append("```md\n");
						}
						message.Append(newMessage);
					}

					message.Append("```");
					serverContext.Dispose();
					dbContext.Dispose();
				}
				catch(Exception ex)
				{
					message.Append(ex.Message);
				}
				await e.SendReplySafe(message.ToString());
			};
			this.Commands.Add(newCommand.Id, newCommand);

// !global
			newCommand = new Command("global");
			newCommand.Type = CommandType.Standard;
			newCommand.IsCoreCommand = true;
			newCommand.Description = "Display all teh numbers. Use with `long` arg for more numbers.";
			newCommand.RequiredPermissions = PermissionType.OwnerOnly;
			newCommand.OnExecute += async e => {
				StringBuilder shards = new StringBuilder();
				GlobalContext dbContext = GlobalContext.Create(this.DbConnectionString);
				Shard globalCount = new Shard();
				bool longStats = e.TrimmedMessage == "long";
				foreach( Shard shard in dbContext.Shards )
				{
					globalCount.ServerCount += shard.ServerCount;
					globalCount.UserCount += shard.UserCount;
					globalCount.MemoryUsed += shard.MemoryUsed;
					globalCount.ThreadsActive += shard.ThreadsActive;
					globalCount.MessagesTotal += shard.MessagesTotal;
					globalCount.MessagesPerMinute += shard.MessagesPerMinute;
					globalCount.OperationsRan += shard.OperationsRan;
					globalCount.OperationsActive += shard.OperationsActive;
					globalCount.Disconnects += shard.Disconnects;

					shards.AppendLine(longStats ? shard.GetStatsString() : shard.GetStatsStringShort());
				}

				string message = "Server Status: <http://status.botwinder.info>\n\n" +
				                 $"Global Servers: `{globalCount.ServerCount}`\n" +
				                 $"Global Members `{globalCount.UserCount}`\n" +
				                 $"Global Allocated data Memory: `{globalCount.MemoryUsed} MB`\n" +
				                 $"Global Threads: `{globalCount.ThreadsActive}`\n" +
				                 $"Global Messages received: `{globalCount.MessagesTotal}`\n" +
				                 $"Global Messages per minute: `{globalCount.MessagesPerMinute}`\n" +
				                 $"Global Operations ran: `{globalCount.OperationsRan}`\n" +
				                 $"Global Operations active: `{globalCount.OperationsActive}`\n" +
				                 $"Global Disconnects: `{globalCount.Disconnects}`\n" +
				                 $"\n**Shards: `{dbContext.Shards.Count()}`**\n\n" +
				                 $"{shards.ToString()}";

				dbContext.Dispose();
				await e.SendReplySafe(message);
			};
			this.Commands.Add(newCommand.Id, newCommand);

// !getServer
			newCommand = new Command("getServer");
			newCommand.Type = CommandType.Standard;
			newCommand.IsCoreCommand = true;
			newCommand.IsSupportCommand = true;
			newCommand.Description = "Display some info about specific server with id/name, or owners id/username.";
			newCommand.RequiredPermissions = PermissionType.OwnerOnly;
			newCommand.OnExecute += async e => {
				if( string.IsNullOrEmpty(e.TrimmedMessage) )
				{
					await e.SendReplySafe("Requires parameters.");
					return;
				}

				guid id;
				guid.TryParse(e.TrimmedMessage, out id);
				StringBuilder response = new StringBuilder();
				IEnumerable<ServerStats> foundServers = null;
				if( !(foundServers = ServerContext.Create(this.DbConnectionString).ServerStats.Where(s =>
					    s.ServerId == id || s.OwnerId == id ||
					    s.ServerName.ToLower().Contains($"{e.TrimmedMessage.ToLower()}") ||
					    s.OwnerName.ToLower().Contains($"{e.TrimmedMessage.ToLower()}")
				    )).Any() )
				{
					await e.SendReplySafe("Server not found.");
					return;
				}

				if( foundServers.Count() > 5 )
				{
					response.AppendLine("__**Found more than 5 servers!**__\n");
				}

				foreach(ServerStats server in foundServers.Take(5))
				{
					response.AppendLine(server.ToString());
					response.AppendLine();
				}

				await e.SendReplySafe(response.ToString());
			};
			this.Commands.Add(newCommand.Id, newCommand);

// !getProperty
			newCommand = new Command("getProperty");
			newCommand.Type = CommandType.Standard;
			newCommand.IsCoreCommand = true;
			newCommand.IsSupportCommand = true;
			newCommand.Description = "Get a server config property by its exact name. Defaults to the current server - use with serverid as the first parameter to explicitly specify different one.";
			newCommand.RequiredPermissions = PermissionType.OwnerOnly;
			newCommand.OnExecute += async e => {
				if( e.MessageArgs.Length < 1 )
				{
					await e.SendReplySafe($"{e.Command.Description}");
					return;
				}

				guid serverId = e.Server.Id;
				ServerConfig config = e.Server.Config;
				if( e.MessageArgs.Length > 1 && (!guid.TryParse(e.MessageArgs[0], out serverId) ||
				    (config = this.ServerDb.ServerConfigurations.FirstOrDefault(s => s.ServerId == serverId)) == null) )
				{
					await e.SendReplySafe("Used with two parameters to specify serverId, but I couldn't find a server.");
					return;
				}

				string propertyName = e.MessageArgs.Length == 1 ? e.MessageArgs[0] : e.MessageArgs[1];
				string propertyValue = config.GetPropertyValue(propertyName);

				if( string.IsNullOrEmpty(propertyValue) )
					propertyValue = "Unknown property.";
				else
					propertyValue = $"`{serverId}`.`{propertyName}`: `{propertyValue}`";

				await e.SendReplySafe(propertyValue);
			};
			this.Commands.Add(newCommand.Id, newCommand);

// !setProperty
			newCommand = new Command("setProperty");
			newCommand.Type = CommandType.Standard;
			newCommand.IsCoreCommand = true;
			newCommand.IsSupportCommand = true;
			newCommand.Description = "Set a server config property referenced by its exact name. Use with serverid, the exact property name, and the new value.";
			newCommand.RequiredPermissions = PermissionType.OwnerOnly;
			newCommand.OnExecute += async e => {
				if( e.MessageArgs.Length < 3 )
				{
					await e.SendReplySafe($"{e.Command.Description}");
					return;
				}

				guid serverId = 0;
				ServerConfig config = null;
				ServerContext dbContext = ServerContext.Create(this.DbConnectionString);
				if( !guid.TryParse(e.MessageArgs[0], out serverId) ||
				    (config = dbContext.ServerConfigurations.FirstOrDefault(s => s.ServerId == serverId)) == null )
				{
					await e.SendReplySafe("Server not found in the database. Use with three parameters: serverid, the exact property name, and the new value.");
					return;
				}

				string propertyName = e.MessageArgs[1];
				string propertyValueString = e.MessageArgs[2];
				string propertyValueOld = "";

				object propertyValue = null;
				if( bool.TryParse(propertyValueString, out bool boolie) )
					propertyValue = boolie;
				else if( int.TryParse(propertyValueString, out int number) )
					propertyValue = number;
				else if( guid.TryParse(propertyValueString, out guid id) )
					propertyValue = id;
				else propertyValue = propertyValueString;

				propertyValueOld = config.SetPropertyValue(propertyName, propertyValue);

				if( string.IsNullOrEmpty(propertyValueOld) )
					propertyValueOld = "Unknown property.";
				else
				{
					dbContext.SaveChanges();
					propertyValueOld = $"Property `{serverId}`.`{propertyName}`: `{propertyValueOld}` was changed to `{propertyValueString}`";
				}

				dbContext.Dispose();
				await e.SendReplySafe(propertyValueOld);
			};
			this.Commands.Add(newCommand.Id, newCommand);

// !getInvite
			newCommand = new Command("getInvite");
			newCommand.Type = CommandType.Standard;
			newCommand.IsCoreCommand = true;
			newCommand.IsSupportCommand = true;
			newCommand.Description = "Get an invite url with serverid.";
			newCommand.RequiredPermissions = PermissionType.OwnerOnly;
			newCommand.OnExecute += async e => {
				guid id;
				ServerConfig foundServer = null;
				if( string.IsNullOrEmpty(e.TrimmedMessage) ||
				    !guid.TryParse(e.TrimmedMessage, out id) ||
				    (foundServer = ServerContext.Create(this.DbConnectionString).ServerConfigurations.FirstOrDefault(s => s.ServerId == id)) == null )
				{
					await e.SendReplySafe("Server not found.");
					return;
				}

				if( string.IsNullOrEmpty(foundServer.InviteUrl) )
				{
					await e.SendReplySafe("I don't have permissions to create this InviteUrl.");
					return;
				}

				await e.SendReplySafe(foundServer.InviteUrl);
			};
			this.Commands.Add(newCommand.Id, newCommand);

// !clearInvite
			newCommand = new Command("clearInvite");
			newCommand.Type = CommandType.Standard;
			newCommand.IsCoreCommand = true;
			newCommand.IsSupportCommand = true;
			newCommand.Description = "Clear the Invite url to be re-created, with serverid.";
			newCommand.RequiredPermissions = PermissionType.OwnerOnly;
			newCommand.OnExecute += async e => {
				guid id;
				if( string.IsNullOrEmpty(e.TrimmedMessage) ||
				    !guid.TryParse(e.TrimmedMessage, out id) )
				{
					await e.SendReplySafe("Invalid parameters.");
					return;
				}

				string response = "Server not found.";
				ServerContext dbContext = ServerContext.Create(this.DbConnectionString);
				ServerConfig foundServer = dbContext.ServerConfigurations.FirstOrDefault(s => s.ServerId == id);
				if( foundServer != null )
				{
					response = "Done.";
					foundServer.InviteUrl = "";
					dbContext.SaveChanges();
				}

				await e.SendReplySafe(response);
				dbContext.Dispose();
			};
			this.Commands.Add(newCommand.Id, newCommand);

// !maintenance
			newCommand = new Command("maintenance");
			newCommand.Type = CommandType.Standard;
			newCommand.IsCoreCommand = true;
			newCommand.Description = "Performe maintenance";
			newCommand.RequiredPermissions = PermissionType.OwnerOnly;
			newCommand.OnExecute += async e =>{
				await e.SendReplySafe("Okay, this may take a while...");
				await LogMaintenanceAndExit();
			};
			this.Commands.Add(newCommand.Id, newCommand);

// !restart
			newCommand = new Command("restart");
			newCommand.Type = CommandType.Standard;
			newCommand.IsCoreCommand = true;
			newCommand.Description = "Shut down the bot.";
			newCommand.RequiredPermissions = PermissionType.OwnerOnly;
			newCommand.OnExecute += async e => {
				await e.SendReplySafe("bai");
				await Task.Delay(1000);
				Environment.Exit(0);
			};
			this.Commands.Add(newCommand.Id, newCommand);
			this.Commands.Add("shutdown", newCommand.CreateAlias("shutdown"));

// !getExceptions
			newCommand = new Command("getExceptions");
			newCommand.Type = CommandType.Standard;
			newCommand.IsCoreCommand = true;
			newCommand.Description = "Get a list of exceptions.";
			newCommand.RequiredPermissions = PermissionType.OwnerOnly;
			newCommand.OnExecute += async e => {
				StringBuilder response = new StringBuilder();
				if( string.IsNullOrEmpty(e.TrimmedMessage) || !int.TryParse(e.TrimmedMessage, out int n) || n <= 0 )
					n = 5;

				GlobalContext dbContext = GlobalContext.Create(this.DbConnectionString);
				foreach( ExceptionEntry exception in dbContext.Exceptions.Skip(Math.Max(0, dbContext.Exceptions.Count() - n)) )
				{
					response.AppendLine(exception.GetMessage());
				}

				dbContext.Dispose();
				string responseString = response.ToString();
				if( string.IsNullOrWhiteSpace(responseString) )
					responseString = "I did not record any errors :stuck_out_tongue:";
				await e.SendReplySafe(responseString);
			};
			this.Commands.Add(newCommand.Id, newCommand);

// !getException
			newCommand = new Command("getException");
			newCommand.Type = CommandType.Standard;
			newCommand.IsCoreCommand = true;
			newCommand.Description = "Get an exception stack for specific ID.";
			newCommand.RequiredPermissions = PermissionType.OwnerOnly;
			newCommand.OnExecute += async e => {
				string responseString = "I couldn't find that exception.";
				ExceptionEntry exception = null;
				GlobalContext dbContext = GlobalContext.Create(this.DbConnectionString);
				if( !string.IsNullOrEmpty(e.TrimmedMessage) && int.TryParse(e.TrimmedMessage, out int id) && (exception = dbContext.Exceptions.FirstOrDefault(ex => ex.Id == id)) != null )
					responseString = exception.GetStack();

				dbContext.Dispose();
				await e.SendReplySafe(responseString);
			};
			this.Commands.Add(newCommand.Id, newCommand);

// !blacklist
			newCommand = new Command("blacklist");
			newCommand.Type = CommandType.Standard;
			newCommand.IsCoreCommand = true;
			newCommand.Description = "Add or remove an ID to or from the blacklist.";
			newCommand.RequiredPermissions = PermissionType.OwnerOnly;
			newCommand.OnExecute += async e => {
				guid id = 0;
				string responseString = "Invalid parameters.";
				if( e.MessageArgs == null || e.MessageArgs.Length < 2 || !guid.TryParse(e.MessageArgs[1], out id) )
				{
					if( !e.Message.MentionedUsers.Any() )
					{
						await e.SendReplySafe(responseString);
						return;
					}

					id = e.Message.MentionedUsers.First().Id;
				}

				GlobalContext dbContext = GlobalContext.Create(this.DbConnectionString);
				ServerStats server = ServerContext.Create(this.DbConnectionString).ServerStats
					.FirstOrDefault(s => s.ServerId == id || s.OwnerId == id);
				switch(e.MessageArgs[0])
				{
					case "add":
						if( dbContext.Blacklist.Any(b => b.Id == id) )
						{
							responseString = "That ID is already blacklisted.";
							break;
						}

						dbContext.Blacklist.Add(new BlacklistEntry(){Id = id});
						dbContext.SaveChanges();
						responseString = server == null ? "Done." : server.ServerId == id ?
								$"I'll be leaving `{server.OwnerName}`'s server `{server.ServerName}` shortly." :
								$"All of `{server.OwnerName}`'s servers are now blacklisted.";
						break;
					case "remove":
						BlacklistEntry entry = dbContext.Blacklist.FirstOrDefault(b => b.Id == id);
						if( entry == null )
						{
							responseString = "That ID was not blacklisted.";
							break;
						}

						dbContext.Blacklist.Remove(entry);
						dbContext.SaveChanges();
						responseString = server == null ? "Done." : server.ServerId == id ?
								$"Entry for `{server.OwnerName}`'s server `{server.ServerName}` was removed from the balcklist." :
								$"Entries for all `{server.OwnerName}`'s servers were removed from the blacklist.";
						break;
					default:
						responseString = "Invalid keyword.";
						break;
				}

				dbContext.Dispose();
				await e.SendReplySafe(responseString);
			};
			this.Commands.Add(newCommand.Id, newCommand);

// !subscriber
			newCommand = new Command("subscriber");
			newCommand.Type = CommandType.Standard;
			newCommand.IsCoreCommand = true;
			newCommand.Description = "Add or remove an ID to or from the subscribers, use with optional bonus or premium parameter.";
			newCommand.RequiredPermissions = PermissionType.OwnerOnly;
			newCommand.OnExecute += async e =>{
				guid id = 0;
				string responseString = "Invalid parameters.";
				if( e.MessageArgs == null || e.MessageArgs.Length < 2 ||
				    !guid.TryParse(e.MessageArgs[1], out id) )
				{
					if( !e.Message.MentionedUsers.Any() )
					{
						await e.SendReplySafe(responseString);
						return;
					}

					id = e.Message.MentionedUsers.First().Id;
				}

				GlobalContext dbContext = GlobalContext.Create(this.DbConnectionString);
				Subscriber subscriber = dbContext.Subscribers.FirstOrDefault(s => s.UserId == id);
				switch(e.MessageArgs[0]) //Nope - mentioned users above mean that there is a parameter.
				{
					case "add":
						if( subscriber == null )
							dbContext.Subscribers.Add(subscriber = new Subscriber(){UserId = id});

						for( int i = 2; i < e.MessageArgs.Length; i++ )
						{
							subscriber.HasBonus = subscriber.HasBonus || e.MessageArgs[i] == "bonus";
							subscriber.IsPremium = subscriber.IsPremium || e.MessageArgs[i] == "premium";
						}

						dbContext.SaveChanges();
						responseString = "Done.";
						break;
					case "remove":
						if( subscriber == null )
						{
							responseString = "That ID was not a subscriber.";
							break;
						}

						responseString = "Done.";
						if( e.MessageArgs.Length < 3 )
						{
							dbContext.Subscribers.Remove(subscriber);
							dbContext.SaveChanges();
							break;
						}

						for( int i = 2; i < e.MessageArgs.Length; i++ )
						{
							subscriber.HasBonus = subscriber.HasBonus && e.MessageArgs[i] != "bonus";
							subscriber.IsPremium = subscriber.IsPremium && e.MessageArgs[i] != "premium";
						}

						dbContext.SaveChanges();
						break;
					default:
						responseString = "Invalid keyword.";
						break;
				}

				dbContext.Dispose();
				await e.SendReplySafe(responseString);
			};
			this.Commands.Add(newCommand.Id, newCommand);

// !partner
			newCommand = new Command("partner");
			newCommand.Type = CommandType.Standard;
			newCommand.IsCoreCommand = true;
			newCommand.Description = "Add or remove an ID to or from the partners, use with optional premium parameter.";
			newCommand.RequiredPermissions = PermissionType.OwnerOnly;
			newCommand.OnExecute += async e =>{
				guid id = 0;
				string responseString = "Invalid parameters.";
				if( e.MessageArgs == null || e.MessageArgs.Length < 2 ||
				    !guid.TryParse(e.MessageArgs[1], out id) )
				{
					if( !e.Message.MentionedUsers.Any() )
					{
						await e.SendReplySafe(responseString);
						return;
					}

					id = e.Message.MentionedUsers.First().Id;
				}

				GlobalContext dbContext = GlobalContext.Create(this.DbConnectionString);
				PartneredServer partner = dbContext.PartneredServers.FirstOrDefault(s => s.ServerId == id);
				switch(e.MessageArgs[0]) //Nope - mentioned users above mean that there is a parameter.
				{
					case "add":
						if( partner == null )
							dbContext.PartneredServers.Add(partner = new PartneredServer(){ServerId = id});

						if( e.MessageArgs.Length > 2 )
							partner.IsPremium = partner.IsPremium || e.MessageArgs[2] == "premium";

						dbContext.SaveChanges();
						responseString = "Done.";
						break;
					case "remove":
						if( partner == null )
						{
							responseString = "That ID was not a partner.";
							break;
						}

						responseString = "Done.";
						if( e.MessageArgs.Length < 3 )
						{
							dbContext.PartneredServers.Remove(partner);
							dbContext.SaveChanges();
							break;
						}

						if( e.MessageArgs.Length > 2 )
							partner.IsPremium = partner.IsPremium && e.MessageArgs[2] != "premium";

						dbContext.SaveChanges();
						break;
					default:
						responseString = "Invalid keyword.";
						break;
				}

				dbContext.Dispose();
				await e.SendReplySafe(responseString);
			};
			this.Commands.Add(newCommand.Id, newCommand);

// !operations
			newCommand = new Command("operations");
			newCommand.Type = CommandType.Standard;
			newCommand.IsCoreCommand = true;
			newCommand.Description = "Display info about all queued or running operations on your server.";
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin;
			newCommand.OnExecute += async e => {
				StringBuilder response = new StringBuilder();
				bool allOperations = IsGlobalAdmin(e.Message.Author.Id);

				response.AppendLine($"Total operations in the queue: `{this.CurrentOperations.Count}`");
				if( allOperations )
					response.AppendLine($"Currently allocated data Memory: `{(GC.GetTotalMemory(false) / 1000000f):#0.00} MB`");

				response.AppendLine();
				lock( this.OperationsLock )
				{
					foreach( Operation op in this.CurrentOperations )
					{
						if( !allOperations && op.CommandArgs.Server.Id != e.Server.Id )
							continue;

						response.AppendLine(op.ToString());
						if( allOperations )
							response.AppendLine($"Server: `{op.CommandArgs.Server.Guild.Name}`\n" +
							                    $"ServerID: `{op.CommandArgs.Server.Id}`\n" +
							                    $"Allocated DataMemory: `{op.AllocatedMemoryStarted:#0.00} MB`\n");
					}
				}

				string responseString = response.ToString();
				if( string.IsNullOrEmpty(responseString) )
					responseString = "There are no operations running.";

				await e.SendReplySafe(responseString);
			};
			this.Commands.Add(newCommand.Id, newCommand);

// !cancel
			newCommand = new Command("cancel");
			newCommand.Type = CommandType.Standard;
			newCommand.IsCoreCommand = true;
			newCommand.Description = "Cancel queued or running operation - use in the same channel, and with the name of the command as parameter. (nuke, archive, etc...)";
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin;
			newCommand.OnExecute += async e => {
				string responseString = "Operation not found.";
				Operation operation = null;

				if( !string.IsNullOrEmpty(e.TrimmedMessage) &&
				    (operation = this.CurrentOperations.FirstOrDefault(
					    op => op.CommandArgs.Channel.Id == e.Channel.Id &&
					          op.CommandArgs.Command.Id == e.TrimmedMessage)) != null )
					responseString = "Operation canceled:\n\n" + operation.ToString();

				await e.SendReplySafe(responseString);
			};
			this.Commands.Add(newCommand.Id, newCommand);

// !say
			newCommand = new Command("say");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Make the bot say something!";
			newCommand.RequiredPermissions = PermissionType.SubModerator;
			newCommand.DeleteRequest = true;
			newCommand.IsBonusCommand = true;
			newCommand.IsSupportCommand = true;
			newCommand.OnExecute += async e => {
				if( string.IsNullOrWhiteSpace(e.TrimmedMessage) )
					return;
				await e.SendReplySafe(e.TrimmedMessage);
			};
			this.Commands.Add(newCommand.Id, newCommand);

// !status
			newCommand = new Command("status");
			newCommand.Type = CommandType.Standard;
			newCommand.IsCoreCommand = true;
			newCommand.Description = "Display basic server status.";
			newCommand.RequiredPermissions = PermissionType.Everyone;
			newCommand.OnExecute += async e => {
				TimeSpan time = DateTime.UtcNow - Utils.GetTimeFromId(e.Message.Id);
				StringBuilder shards = new StringBuilder();
				GlobalContext dbContext = GlobalContext.Create(this.DbConnectionString);

				Int64 threads = dbContext.Shards.Sum(s => s.ThreadsActive);
				string[] cpuTemp = Bash.Run("sensors | grep Package | sed 's/Package id [01]:\\s*+//g' | sed 's/\\s*(high = +85.0°C, crit = +95.0°C)//g'").Split('\n');
				string cpuLoad = Bash.Run("grep 'cpu ' /proc/stat | awk '{print ($2+$4)*100/($2+$4+$5)}'");
				string memoryUsed = Bash.Run("free | grep Mem | awk '{print $3/$2 * 100.0}'");
				string message = "Server Status: <http://status.botwinder.info>\n" +
				                 "Server Info: <http://rhea-ayase.eu/persephone>" +
				                 $"```md\n" +
				                 $"[ Memory usage ][ {double.Parse(memoryUsed):#00.00} % ]\n" +
				                 $"[     CPU Load ][ {double.Parse(cpuLoad):#00.00} % ]\n" +
				                 $"[    CPU0 Temp ][ {cpuTemp[0]}  ]\n" +
				                 $"[    CPU1 Temp ][ {cpuTemp[1]}  ]\n" +
				                 $"[      Threads ][ {threads:#000}     ]\n" +
				                 $"```\n<:BlobNomBotwinder:436141463299031040> `{time.TotalMilliseconds:#00}`ms <:BotwinderNomBlob:438688429849706497>";

				await e.SendReplySafe(message);
				dbContext.Dispose();
			};
			this.Commands.Add(newCommand.Id, newCommand);
			this.Commands.Add("ping", newCommand.CreateAlias("ping"));

// !help
			newCommand = new Command("help");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "PMs a list of Custom Commands for the server if used without a parameter. Use with a parameter to search for specific commands.";
			newCommand.RequiredPermissions = PermissionType.Everyone;
			newCommand.OnExecute += async e => {
				StringBuilder response = new StringBuilder("Please refer to the website documentation for the full list of features and commands: <http://botwinder.info/docs>\n\n");
				StringBuilder commandStrings = new StringBuilder();

				bool isSpecific = !string.IsNullOrWhiteSpace(e.TrimmedMessage);
				string prefix = e.Server.Config.CommandPrefix;
				List<string> includedCommandIds = new List<string>();
				int count = 0;

				void AddCustomAlias(string commandId)
				{
					List<CustomAlias> aliases = e.Server.CustomAliases.Values.Where(a => a.CommandId == commandId).ToList();
					int aliasCount = aliases.Count;
					if( aliasCount > 0 )
					{
						commandStrings.Append(aliasCount == 1 ? " **-** Custom Alias: " : " **-** Custom Aliases: ");
						for( int i = 0; i < aliasCount; i++ )
							commandStrings.Append((i == 0 ? "`" : i == aliasCount - 1 ? " and `" : ", `") +
							                      prefix + aliases[i].Alias + "`");
					}
					commandStrings.AppendLine();
				}

				void AddCommand(Command cmd)
				{
					if( includedCommandIds.Contains(cmd.Id) )
						return;
					includedCommandIds.Add(cmd.Id);

					commandStrings.AppendLine($"\n```diff\n{(cmd.CanExecute(this, e.Server, e.Channel, e.Message.Author as SocketGuildUser) ? "+" : "-")}" +
					                          $"  {prefix}{cmd.Id}```" +
					                          $" **-** {cmd.Description}");
					if( cmd.Aliases != null && cmd.Aliases.Any() )
					{
						int aliasCount = cmd.Aliases.Count;
						commandStrings.Append(aliasCount == 1 ? " **-** Alias: " : " **-** Aliases: ");
						for( int i = 0; i < aliasCount; i++ )
							commandStrings.Append((i == 0 ? "`" : i == aliasCount - 1 ? " and `" : ", `") +
							                      prefix + cmd.Aliases[i] + "`");
						commandStrings.AppendLine();
					}

					AddCustomAlias(cmd.Id);
				}

				void AddCustomCommand(CustomCommand cmd)
				{
					if( includedCommandIds.Contains(cmd.CommandId) )
						return;
					includedCommandIds.Add(cmd.CommandId);

					commandStrings.AppendLine($"\n```diff\n{(cmd.CanExecute(this, e.Server, e.Channel, e.Message.Author as SocketGuildUser) ? "+" : "-")}" +
					                          $"  {prefix}{cmd.CommandId}```" +
					                          $" **-** {cmd.Description}");

					AddCustomAlias(cmd.CommandId);
				}

				if( isSpecific )
				{
					string expression = e.TrimmedMessage.Replace(" ", "|") + ")\\w*";
					if( e.MessageArgs.Length > 1 )
						expression += "(" + expression;
					Regex regex = new Regex($"\\w*({expression}", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(10f));

					foreach( Command cmd in e.Server.Commands.Values )
					{
						if( !cmd.IsHidden &&
						    cmd.RequiredPermissions != PermissionType.OwnerOnly &&
						    regex.Match(cmd.Id).Success )
						{
							Command command = cmd;
							if( cmd.IsAlias && e.Server.Commands.ContainsKey(cmd.ParentId) )
								command = e.Server.Commands[cmd.ParentId];

							if( includedCommandIds.Contains(command.Id) )
								continue;

							if( ++count > 5 )
								break;

							AddCommand(cmd);
						}
					}

					foreach( CustomCommand cmd in e.Server.CustomCommands.Values )
					{
						if( regex.Match(cmd.CommandId).Success ) //Chances are that it's gonna fail more often.
						{
							if( ++count > 5 )
								break;

							AddCustomCommand(cmd);
						}
					}

					foreach( CustomAlias alias in e.Server.CustomAliases.Values )
					{
						if( regex.Match(alias.Alias).Success ) //Chances are that it's gonna fail more often.
						{
							if( ++count > 5 )
								break;

							if( e.Server.Commands.ContainsKey(alias.CommandId) )
								AddCommand(e.Server.Commands[alias.CommandId]);
							else if( e.Server.CustomCommands.ContainsKey(alias.CommandId) )
							{
								AddCustomCommand(e.Server.CustomCommands[alias.CommandId]);
							}
						}
					}

					if( count == 0 )
						response.AppendLine("I did not find any commands matching your search expression.");
					else
					{
						if( count > 5 )
							response.AppendLine("I found too many commands matching your search expression. **Here are the first five:**");

						response.Append(commandStrings.ToString());
					}
				}
				else if( e.Server.CustomCommands.Any() ) //Not specific - PM CustomCommands.
				{
					foreach( CustomCommand cmd in e.Server.CustomCommands.Values )
					{
						AddCustomCommand(cmd);
					}

					response.AppendLine("I've PMed you the Custom Commands for this server.");
					await e.Message.Author.SendMessageSafe(commandStrings.ToString());
				}

				await e.SendReplySafe(response.ToString());
			};
			this.Commands.Add(newCommand.Id, newCommand);

// !alias
			newCommand = new Command("alias");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Manage command aliases, use without parameters for more details.";
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin;
			newCommand.OnExecute += async e => {
				if(e.MessageArgs == null || e.MessageArgs.Length == 0 || !(
					   (e.MessageArgs.Length == 1 && e.MessageArgs[0] == "list") ||
					   (e.MessageArgs.Length == 2 && (e.MessageArgs[0] == "remove" || e.MessageArgs[0] == "delete")) ||
					   (e.MessageArgs.Length == 3 && (e.MessageArgs[0] == "add" || e.MessageArgs[0] == "create")) ))
				{
					await e.Message.Channel.SendMessageSafe(string.Format(
						"Use this command with the following parameters:\n" +
						"  `{0}{1} list` - Display the list of your custom aliases.\n" +
						"  `{0}{1} create alias command` - Create a new `alias` to the old `command`.\n" +
						"  `{0}{1} delete alias` - Delete the `alias`.\n",
						e.Server.Config.CommandPrefix,
						e.Command.Id
					));
					return;
				}
				string responseString = "";

				switch(e.MessageArgs[0])
				{
					case "list":
					{
						if( e.Server.CustomAliases == null || !e.Server.CustomAliases.Any() )
						{
							responseString = "There aren't any! O_O";
							break;
						}

						StringBuilder response = new StringBuilder();
						response.AppendLine("Command-Aliases on this server:\n```http\nexampleAlias: command\n---------------------");
						foreach( CustomAlias alias in e.Server.CustomAliases.Values )
						{
							string line = alias.Alias + ": " + alias.CommandId;
							if( line.Length + response.Length + 5 > GlobalConfig.MessageCharacterLimit )
							{
								await e.SendReplySafe(response.ToString() + "\n```");
								response.Clear().AppendLine("```http\nexampleAlias: command\n---------------------");
							}
							response.AppendLine(line);
						}

						response.Append("```");
						responseString = response.ToString();
					}
						break;
					case "create":
					case "add":
					{
						CustomAlias alias = new CustomAlias(){
							Alias = e.MessageArgs[1],
							CommandId = e.MessageArgs[2],
							ServerId = e.Server.Id
						};
						if( e.Server.Commands.ContainsKey(alias.Alias) ||
						    e.Server.CustomCommands.ContainsKey(alias.Alias) ||
						    e.Server.CustomAliases.ContainsKey(alias.Alias) )
						{
							responseString = $"I already have a command with this name (`{alias.Alias}`)";
							break;
						}
						if( !e.Server.Commands.ContainsKey(alias.CommandId) &&
						    !e.Server.CustomCommands.ContainsKey(alias.CommandId) )
						{
							responseString = $"Target command not found (`{alias.CommandId}`)";
							break;
						}

						if( e.Server.Commands.ContainsKey(alias.CommandId) )
						{
							Command cmd = e.Server.Commands[alias.CommandId];
							if( cmd.IsAlias && !string.IsNullOrEmpty(cmd.ParentId) )
								alias.CommandId = cmd.ParentId;
						}

						ServerContext dbContext = ServerContext.Create(this.DbConnectionString);
						dbContext.CustomAliases.Add(alias);
						dbContext.SaveChanges();
						dbContext.Dispose();

						responseString = $"Alias `{e.Server.Config.CommandPrefix}{alias.Alias}` created.";
					}
						break;
					case "delete":
					case "remove":
					{
						if( e.Server.CustomAliases == null || !e.Server.CustomAliases.ContainsKey(e.MessageArgs[1]) )
						{
							responseString = $"Alias not found. (`{e.MessageArgs[1]}`)";
							break;
						}

						ServerContext dbContext = ServerContext.Create(this.DbConnectionString);
						CustomAlias alias = new CustomAlias{ServerId = e.Server.Id, Alias = e.MessageArgs[1]};
						dbContext.CustomAliases.Attach(alias);
						dbContext.CustomAliases.Remove(alias);
						dbContext.SaveChanges();
						dbContext.Dispose();

						responseString = $"RIP `{e.Server.Config.CommandPrefix}{alias.Alias}`.";
					}
						break;
					default:
						responseString = "Unknown property.";
						return;
				}

				await e.SendReplySafe(responseString);
			};
			this.Commands.Add(newCommand.Id, newCommand);

// !permissions
			newCommand = new Command("permissions");
			newCommand.Type = CommandType.Standard;
			newCommand.IsCoreCommand = true;
			newCommand.Description = "Configure permission groups for every command. Use without parameters for help.";
			newCommand.RequiredPermissions = PermissionType.ServerOwner;
			newCommand.OnExecute += async e => {
				string response = string.Format(
					"Use this command with the following parameters:\n" +
					"  `{0}{1} CommandID PermissionGroup` - where `CommandID` is name of the command, and `PermissionGroups` can be:\n" +
					"    `ServerOwner`, `Admins`, `Moderators`, `SubModerators`, `Members`, `Everyone` - Look at the docs for reference: <http://botwinder.info/docs>\n" +
					"    `Nobody` - Block this command from execusion even by Server Owner.\n" +
					"    `Default` - will set default permissions as seen in the docs.\n"+
					"  For example `{0}{1} nuke ServerOwner`",
					e.Server.Config.CommandPrefix,
					e.Command.Id);

				if( e.MessageArgs == null || e.MessageArgs.Length < 2 ||
				    !Enum.TryParse(e.MessageArgs[1], true, out PermissionOverrides permissionOverrides) )
				{
					await e.SendReplySafe(response);
					return;
				}

				string commandId = e.Server.GetCommandOptionsId(e.MessageArgs[0]);
				if( commandId == null )
				{
					await e.SendReplySafe("I'm sorry but you can not restrict this command.");
					return;
				}
				if( commandId == "" )
				{
					await e.SendReplySafe($"Command `{e.MessageArgs[0]}` not found.");
					return;
				}

				ServerContext dbContext = ServerContext.Create(this.DbConnectionString);
				CommandOptions options = dbContext.GetOrAddCommandOptions(e.Server, commandId);

				options.PermissionOverrides = permissionOverrides;

				dbContext.SaveChanges();
				dbContext.Dispose();

				await e.SendReplySafe("All set!");
			};
			this.Commands.Add(newCommand.Id, newCommand);

// !deleteRequest
			newCommand = new Command("deleteRequest");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Set a command to have the issuing request message deleted automatically. Use with `CommandID` and `true` or `false` parameters.";
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin;
			newCommand.OnExecute += async e => {
				if( e.MessageArgs == null || e.MessageArgs.Length < 2 ||
				    !bool.TryParse(e.MessageArgs[1], out bool deleteRequest) )
				{
					await e.SendReplySafe("Invalid parameters...\n" + e.Command.Description);
					return;
				}

				string commandId = e.Server.GetCommandOptionsId(e.MessageArgs[0]);
				if( commandId == null )
				{
					await e.SendReplySafe("I'm sorry but you can not restrict this command.");
					return;
				}
				if( commandId == "" )
				{
					await e.SendReplySafe($"Command `{e.MessageArgs[0]}` not found.");
					return;
				}

				ServerContext dbContext = ServerContext.Create(this.DbConnectionString);
				CommandOptions options = dbContext.GetOrAddCommandOptions(e.Server, commandId);

				options.DeleteRequest = deleteRequest;

				dbContext.SaveChanges();
				dbContext.Dispose();

				await e.SendReplySafe("Okay...");
			};
			this.Commands.Add(newCommand.Id, newCommand);
			this.Commands.Add("removeRequest", newCommand.CreateAlias("removeRequest"));

// !deleteReply
			newCommand = new Command("deleteReply");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Set a command to delete bot's replies in a few seconds. _(Only some commands support this!)_ Use with `CommandID` and `true` or `false` parameters.";
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin;
			newCommand.OnExecute += async e => {
				if( e.MessageArgs == null || e.MessageArgs.Length < 2 ||
				    !bool.TryParse(e.MessageArgs[1], out bool deleteReply) )
				{
					await e.SendReplySafe("Invalid parameters...\n" + e.Command.Description);
					return;
				}

				string commandId = e.Server.GetCommandOptionsId(e.MessageArgs[0]);
				if( commandId == null )
				{
					await e.SendReplySafe("I'm sorry but you can not restrict this command.");
					return;
				}
				if( commandId == "" )
				{
					await e.SendReplySafe($"Command `{e.MessageArgs[0]}` not found.");
					return;
				}

				ServerContext dbContext = ServerContext.Create(this.DbConnectionString);
				CommandOptions options = dbContext.GetOrAddCommandOptions(e.Server, commandId);

				options.DeleteReply = deleteReply;

				dbContext.SaveChanges();
				dbContext.Dispose();

				await e.SendReplySafe("Okay...");
			};
			this.Commands.Add(newCommand.Id, newCommand);
			this.Commands.Add("removeReply", newCommand.CreateAlias("removeReply"));

// !cmdChannelWhitelist
			newCommand = new Command("cmdChannelWhitelist");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Allow a command to be ran only in certain channels. Use with `CommandID`, `add` or `remove`, and `ChannelID` parameters.";
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin;
			newCommand.OnExecute += async e => {
				if( e.MessageArgs == null || e.MessageArgs.Length < 2 ||
				    (e.MessageArgs[1].ToLower() != "add" && e.MessageArgs[1].ToLower() != "remove") ||
				    !guid.TryParse(e.MessageArgs[2].Trim('<', '#', '>'), out guid channelId) || e.Server.Guild.GetChannel(channelId) == null )
				{
					await e.SendReplySafe("Invalid parameters...\n" + e.Command.Description);
					return;
				}

				string commandId = e.Server.GetCommandOptionsId(e.MessageArgs[0]);
				if( commandId == null )
				{
					await e.SendReplySafe("I'm sorry but you can not restrict this command.");
					return;
				}
				if( commandId == "" )
				{
					await e.SendReplySafe($"Command `{e.MessageArgs[0]}` not found.");
					return;
				}

				ServerContext dbContext = ServerContext.Create(this.DbConnectionString);
				CommandChannelOptions commandOptions = dbContext.GetOrAddCommandChannelOptions(e.Server.Id, channelId, commandId);

				string responseString = "Success! \\o/";
				switch(e.MessageArgs[1].ToLower())
				{
					case "add":
						commandOptions.Whitelisted = true;
						dbContext.SaveChanges();
						break;
					case "remove":
						commandOptions.Whitelisted = false;
						dbContext.SaveChanges();
						break;
					default:
						responseString = "Invalid parameters...\n" + e.Command.Description;
						break;
				}

				dbContext.Dispose();
				await e.SendReplySafe(responseString);
			};
			this.Commands.Add(newCommand.Id, newCommand);

// !cmdChannelBlacklist
			newCommand = new Command("cmdChannelBlacklist");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Block a command from certain channels. Use with `CommandID`, `add` or `remove`, and `ChannelID` parameters.";
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin;
			newCommand.OnExecute += async e => {
				if( e.MessageArgs == null || e.MessageArgs.Length < 2 ||
				    (e.MessageArgs[1].ToLower() != "add" && e.MessageArgs[1].ToLower() != "remove") ||
				    !guid.TryParse(e.MessageArgs[2].Trim('<', '#', '>'), out guid channelId) || e.Server.Guild.GetChannel(channelId) == null )
				{
					await e.SendReplySafe("Invalid parameters...\n" + e.Command.Description);
					return;
				}

				string commandId = e.Server.GetCommandOptionsId(e.MessageArgs[0]);
				if( commandId == null )
				{
					await e.SendReplySafe("I'm sorry but you can not restrict this command.");
					return;
				}
				if( commandId == "" )
				{
					await e.SendReplySafe($"Command `{e.MessageArgs[0]}` not found.");
					return;
				}

				ServerContext dbContext = ServerContext.Create(this.DbConnectionString);
				CommandChannelOptions commandOptions = dbContext.GetOrAddCommandChannelOptions(e.Server.Id, channelId, commandId);

				string responseString = "Success! \\o/";
				switch(e.MessageArgs[1].ToLower())
				{
					case "add":
						commandOptions.Blacklisted = true;
						dbContext.SaveChanges();
						break;
					case "remove":
						commandOptions.Blacklisted = false;
						dbContext.SaveChanges();
						break;
					default:
						responseString = "Invalid parameters...\n" + e.Command.Description;
						break;
				}

				dbContext.Dispose();
				await e.SendReplySafe(responseString);
			};
			this.Commands.Add(newCommand.Id, newCommand);

// !cmdResetRestrictions
			newCommand = new Command("cmdResetRestrictions");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Reset restrictions placed on a command by the _cmdChannelWhitelist_ and _cmdChannelBlacklist_ commands. Use with the `CommandID` as parameter.";
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin;
			newCommand.OnExecute += async e => {
				if( e.MessageArgs == null || e.MessageArgs.Length < 1 )
				{
					await e.SendReplySafe("Invalid parameters...\n" + e.Command.Description);
					return;
				}

				string commandId = e.Server.GetCommandOptionsId(e.MessageArgs[0]);
				if( string.IsNullOrEmpty(commandId) )
				{
					await e.SendReplySafe($"Command `{e.MessageArgs[0]}` not found.");
					return;
				}

				ServerContext dbContext = ServerContext.Create(this.DbConnectionString);
				await dbContext.CommandChannelOptions.Where(c => c.ServerId == e.Server.Id && c.CommandId == commandId)
					.ForEachAsync(c => c.Blacklisted = c.Whitelisted = false);

				dbContext.SaveChanges();
				dbContext.Dispose();

				await e.SendReplySafe("As you wish my thane.");
			};
			this.Commands.Add(newCommand.Id, newCommand);

/*
// !command
			newCommand = new Command("command");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "";
			newCommand.RequiredPermissions = PermissionType.OwnerOnly;
			newCommand.OnExecute += async e => {
				string responseString = "";
				await e.SendReplySafe(responseString);
			};
			this.Commands.Add(newCommand.Id, newCommand);

*/

			return Task.CompletedTask;
		}
	}
}
