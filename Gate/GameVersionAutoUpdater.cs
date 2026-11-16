using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LordsBotGate;

public static class GameVersionAutoUpdater
{
	private static readonly object _lock = new object();
	private static string s_currentVersion = "2.208.321";
	private static ushort s_major = 2;
	private static ushort s_minor = 208;
	private static ushort s_build = 321;
	private static DateTime s_lastChecked = DateTime.MinValue;
	private static Action<string>? s_logger;

	public static string CurrentVersion => s_currentVersion;
	public static ushort Major => s_major;
	public static ushort Minor => s_minor;
	public static ushort Build => s_build;

	public static void Initialize(Action<string> logger)
	{
		s_logger = logger;
	}

	private static void Log(string msg)
	{
		if (s_logger != null)
		{
			s_logger(msg);
		}
		else
		{
			Console.WriteLine(msg);
		}
	}

	public static string ResolveVersion(bool forceRefresh = false, int timeoutSeconds = 6)
	{
		lock (_lock)
		{
			if (!forceRefresh && (DateTime.UtcNow - s_lastChecked).TotalMinutes < 15 && !string.IsNullOrEmpty(s_currentVersion))
			{
				return s_currentVersion;
			}

			try
			{
				var task = FetchLiveGameVersionAsync(timeoutSeconds);
				if (task.Wait(TimeSpan.FromSeconds(timeoutSeconds + 2)))
				{
					var fetched = task.Result;
					if (!string.IsNullOrEmpty(fetched) && fetched.Contains("."))
					{
						s_currentVersion = fetched;
						s_lastChecked = DateTime.UtcNow;
						PersistVersionToDisk(s_currentVersion);
						return s_currentVersion;
					}
				}
			}
			catch (Exception ex)
			{
				Log($"[GATE] [AutoVersion] Live fetch error: {ex.Message}");
			}

			// Fallback 1: Read from local game_version.txt
			try
			{
				string baseDir = AppDomain.CurrentDomain.BaseDirectory;
				string gvPath = Path.Combine(baseDir, "game_version.txt");
				if (File.Exists(gvPath))
				{
					string diskVer = File.ReadAllText(gvPath).Trim();
					if (!string.IsNullOrWhiteSpace(diskVer) && diskVer.Contains("."))
					{
						s_currentVersion = diskVer;
						ParseVersionComponents(s_currentVersion, out s_major, out s_minor, out s_build);
						Log($"[GATE] [AutoVersion] Using local disk game version: {s_currentVersion}");
						return s_currentVersion;
					}
				}
			}
			catch { }

			// Fallback 2: Hardcoded safe default
			s_currentVersion = "2.208.321";
			s_major = 2;
			s_minor = 208;
			s_build = 321;
			Log($"[GATE] [AutoVersion] Using fallback game version: {s_currentVersion}");
			return s_currentVersion;
		}
	}

	public static async Task<string> FetchLiveGameVersionAsync(int timeoutSeconds = 6)
	{
		ushort major = s_major;
		ushort minor = s_minor;
		ushort build = s_build;
		bool gotMajorMinor = false;
		bool gotBuild = false;

		using var handler = new SocketsHttpHandler
		{
			UseProxy = false, // Direct connection without proxy interference
			ConnectTimeout = TimeSpan.FromSeconds(timeoutSeconds)
		};
		using var client = new HttpClient(handler)
		{
			Timeout = TimeSpan.FromSeconds(timeoutSeconds)
		};
		client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

		// Source 1: Apple iTunes Storefront CDN (Major.Minor)
		try
		{
			string itunesJson = await client.GetStringAsync("https://itunes.apple.com/lookup?id=1071976327").ConfigureAwait(false);
			var match = Regex.Match(itunesJson, @"""version""\s*:\s*""(\d+)\.(\d+)");
			if (match.Success)
			{
				major = ushort.Parse(match.Groups[1].Value);
				minor = ushort.Parse(match.Groups[2].Value);
				gotMajorMinor = true;
				Log($"[GATE] [AutoVersion] Apple AppStore CDN resolved Major.Minor: {major}.{minor}");
			}
		}
		catch (Exception ex)
		{
			Log($"[GATE] [AutoVersion] Apple AppStore lookup note: {ex.Message}");
		}

		// Source 2: IGG Official Windows Update Plist (Build / Patch)
		try
		{
			string plist = await client.GetStringAsync("http://download-lo-snd.igg.com/win6/Update.plist").ConfigureAwait(false);
			var lines = plist.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
			if (lines.Length > 0)
			{
				var parts = lines[0].Split('/');
				if (parts.Length >= 3 && ushort.TryParse(parts[2], out ushort parsedBuild))
				{
					build = parsedBuild;
					gotBuild = true;
					Log($"[GATE] [AutoVersion] IGG Update Manifest resolved Build: {build}");
				}
			}
		}
		catch (Exception ex)
		{
			Log($"[GATE] [AutoVersion] IGG Plist lookup note: {ex.Message}");
		}

		if (gotMajorMinor || gotBuild)
		{
			s_major = major;
			s_minor = minor;
			s_build = build;
			string combined = $"{major}.{minor}.{build}";
			Log($"[GATE] [AutoVersion] Successfully assembled live game version: {combined}");
			return combined;
		}

		return s_currentVersion;
	}

	public static void ParseVersionComponents(string ver, out ushort major, out ushort minor, out ushort build)
	{
		major = 2; minor = 208; build = 321;
		try
		{
			var parts = ver.Split('.');
			if (parts.Length >= 1) ushort.TryParse(parts[0], out major);
			if (parts.Length >= 2) ushort.TryParse(parts[1], out minor);
			if (parts.Length >= 3) ushort.TryParse(parts[2], out build);
		}
		catch { }
	}

	public static void PersistVersionToDisk(string ver)
	{
		try
		{
			string baseDir = AppDomain.CurrentDomain.BaseDirectory;
			string[] paths = new[]
			{
				Path.Combine(baseDir, "game_version.txt"),
				Path.Combine(baseDir, "local_server", "game_version.txt"),
				Path.Combine(baseDir, "..", "game_version.txt")
			};
			foreach (var p in paths)
			{
				try
				{
					string? dir = Path.GetDirectoryName(p);
					if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
					{
						File.WriteAllText(p, ver);
					}
				}
				catch { }
			}
		}
		catch { }
	}
}
