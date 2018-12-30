﻿using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace MultiAdmin
{
	internal class OutputThread
	{
		public static readonly Regex SmodRegex =
			new Regex(@"\[(DEBUG|INFO|WARN|ERROR)\] (\[.*?\]) (.*)", RegexOptions.Compiled);

		public static readonly ConsoleColor DefaultForeground = ConsoleColor.Cyan;
		public static readonly ConsoleColor DefaultBackground = ConsoleColor.Black;

		public static ConsoleColor MapConsoleColor(string color, ConsoleColor def = ConsoleColor.Cyan)
		{
			try
			{
				return (ConsoleColor) Enum.Parse(typeof(ConsoleColor), color);
			}
			catch
			{
				return def;
			}
		}

		public static void Read(Server server)
		{
			string dedicatedDir = "SCPSL_Data" + Path.DirectorySeparatorChar + "Dedicated";
			FileSystemWatcher watcher = new FileSystemWatcher();
			watcher.Path = dedicatedDir;
			watcher.IncludeSubdirectories = true;

			if (Utils.IsUnix)
			{
				ReadLinux(server, watcher);
				return;
			}

			ReadWindows(server, watcher);
		}

		public static void ReadWindows(Server server, FileSystemWatcher watcher)
		{
			watcher.Changed += (sender, eventArgs) => OnDirectoryChanged(eventArgs, server);
			watcher.EnableRaisingEvents = true;
		}

		public static void ReadLinux(Server server, FileSystemWatcher watcher)
		{
			watcher.Created += (sender, eventArgs) => OnMapiCreated(eventArgs, server);
			watcher.Filter = "sl*.mapi";
			watcher.EnableRaisingEvents = true;
		}

		private static void OnDirectoryChanged(FileSystemEventArgs e, Server server)
		{
			if (!Directory.Exists(e.FullPath)) return;

			if (!e.FullPath.Contains(server.SessionId)) return;

			string[] files = Directory.GetFiles(e.FullPath, "sl*.mapi", SearchOption.TopDirectoryOnly).OrderBy(f => f)
				.ToArray();
			foreach (string file in files) ProcessFile(server, file);
		}

		private static void OnMapiCreated(FileSystemEventArgs e, Server server)
		{
			if (!e.FullPath.Contains(server.SessionId)) return;

			Thread.Sleep(15);
			ProcessFile(server, e.FullPath);
		}

		private static void ProcessFile(Server server, string file)
		{
			string stream = string.Empty;
			string command = "open";
			int attempts = 0;
			bool read = false;

			while (attempts < 50 && !read && !server.IsStopping())
				try
				{
					if (!File.Exists(file)) return;

					StreamReader sr = new StreamReader(file);
					stream = sr.ReadToEnd();
					command = "close";
					sr.Close();
					command = "delete";
					File.Delete(file);
					read = true;
				}
				catch
				{
					attempts++;
					if (attempts >= 50)
					{
						server.Write(
							"Message printer warning: Could not " + command + " " + file +
							". Make sure that MultiAdmin.exe has all necessary read-write permissions.");
						server.Write("skipping");
					}
				}

			if (server.IsStopping()) return;

			bool display = true;
			ConsoleColor color = ConsoleColor.Cyan;

			if (!string.IsNullOrEmpty(stream.Trim()))
				if (stream.Contains("LOGTYPE"))
				{
					string type = stream.Substring(stream.IndexOf("LOGTYPE")).Trim();
					stream = stream.Substring(0, stream.IndexOf("LOGTYPE")).Trim();

					switch (type)
					{
						case "LOGTYPE02":
							color = ConsoleColor.Green;
							break;
						case "LOGTYPE-8":
							color = ConsoleColor.DarkRed;
							break;
						case "LOGTYPE14":
							color = ConsoleColor.Magenta;
							break;
						default:
							color = ConsoleColor.Cyan;
							break;
					}
				}

			// Smod3 Color tags

			string[] streamSplit = stream.Split("@#".ToCharArray());

			if (streamSplit.Length > 1)
			{
				ConsoleColor fg = DefaultForeground;
				ConsoleColor bg = DefaultBackground;
				// date
				server.WritePart(string.Empty, DefaultBackground, ConsoleColor.Cyan, true, false);

				foreach (string line in streamSplit)
				{
					string part = line;
					if (part.Length >= 3 && part.Contains(";"))
					{
						string colorTag = part.Substring(3, part.IndexOf(";") - 3);

						if (part.Substring(0, 3).Equals("fg=")) fg = MapConsoleColor(colorTag, DefaultForeground);

						if (line.Substring(0, 3).Equals("bg=")) bg = MapConsoleColor(colorTag, DefaultBackground);

						if (part.Length == line.IndexOf(";"))
							part = string.Empty;
						else
							part = part.Substring(line.IndexOf(";") + 1);
					}

					server.WritePart(part, bg, fg, false, false);
				}

				// end
				server.WritePart(string.Empty, DefaultBackground, ConsoleColor.Cyan, false, true);
				display = false;
			}

			// Smod2 loggers pretty printing

			Match match = SmodRegex.Match(stream);
			if (match.Success)
				if (match.Groups.Count >= 2)
				{
					ConsoleColor levelColor = ConsoleColor.Cyan;
					ConsoleColor tagColor = ConsoleColor.Yellow;
					ConsoleColor msgColor = ConsoleColor.White;
					switch (match.Groups[1].Value.Trim())
					{
						case "[DEBUG]":
							levelColor = ConsoleColor.Gray;
							break;
						case "[INFO]":
							levelColor = ConsoleColor.Green;
							break;
						case "[WARN]":
							levelColor = ConsoleColor.DarkYellow;
							break;
						case "[ERROR]":
							levelColor = ConsoleColor.Red;
							msgColor = ConsoleColor.Red;
							break;
						default:
							color = ConsoleColor.Cyan;
							break;
					}

					server.WritePart(string.Empty, DefaultBackground, ConsoleColor.Cyan, true, false);
					server.WritePart("[" + match.Groups[1].Value + "] ", DefaultBackground, levelColor, false, false);
					server.WritePart(match.Groups[2].Value + " ", DefaultBackground, tagColor, false, false);
					// OLD: server.WritePart(match.Groups[3].Value, msgColor, 0, false, true);
					// The regex.Match was trimming out the new lines and that is why no new lines were created.
					// To be sure this will not happen again:
					streamSplit = stream.Split(new[] {']'}, 3);
					server.WritePart(streamSplit[2], DefaultBackground, msgColor, false, true);
					// This way, it outputs the whole message.
					// P.S. the format is [Info] [courtney.exampleplugin] Something interesting happened
					// That was just an example

					// This return should be here
					return;
				}


			if (stream.Contains("Mod Log:"))
				foreach (Feature f in server.features)
					if (f is IEventAdminAction adminAction)
						adminAction.OnAdminAction(stream.Replace("Mod log:", string.Empty));

			if (stream.Contains("ServerMod - Version"))
			{
				server.hasServerMod = true;
				// This should work fine with older ServerMod versions too
				streamSplit = stream.Replace("ServerMod - Version", string.Empty).Split('-');
				server.serverModVersion = streamSplit[0].Trim();
				server.serverModBuild = (streamSplit.Length > 1 ? streamSplit[1] : "A").Trim();
			}

			if (stream.Contains("Round restarting"))
				foreach (Feature f in server.features)
					if (f is IEventRoundEnd roundEnd)
						roundEnd.OnRoundEnd();

			if (stream.Contains("Waiting for players"))
			{
				if (!server.initialRoundStarted)
				{
					server.initialRoundStarted = true;
					foreach (Feature f in server.features)
						if (f is IEventRoundStart roundStart)
							roundStart.OnRoundStart();
				}

				if (server.ServerModCheck(1, 5, 0) && server.fixBuggedPlayers)
				{
					server.SendMessage("ROUNDRESTART");
					server.fixBuggedPlayers = false;
				}
			}


			if (stream.Contains("New round has been started"))
				foreach (Feature f in server.features)
					if (f is IEventRoundStart)
						((IEventRoundStart) f).OnRoundStart();

			if (stream.Contains("Level loaded. Creating match..."))
				foreach (Feature f in server.features)
					if (f is IEventServerStart)
						((IEventServerStart) f).OnServerStart();


			if (stream.Contains("Server full"))
				foreach (Feature f in server.features)
					if (f is IEventServerFull)
						((IEventServerFull) f).OnServerFull();


			if (stream.Contains("Player connect"))
			{
				display = false;
				server.Log("Player connect event");
				foreach (Feature f in server.features)
					if (f is IEventPlayerConnect)
					{
						string name = stream.Substring(stream.IndexOf(":"));
						((IEventPlayerConnect) f).OnPlayerConnect(name);
					}
			}

			if (stream.Contains("Player disconnect"))
			{
				display = false;
				server.Log("Player disconnect event");
				foreach (Feature f in server.features)
					if (f is IEventPlayerDisconnect)
					{
						string name = stream.Substring(stream.IndexOf(":"));
						((IEventPlayerDisconnect) f).OnPlayerDisconnect(name);
					}
			}

			if (stream.Contains("Player has connected before load is complete"))
				if (server.ServerModCheck(1, 5, 0))
					server.fixBuggedPlayers = true;

			if (display) server.Write(stream.Trim(), color);
		}
	}
}