using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DevExpress.XtraGrid;
using DevExpress.XtraGrid.Views.Base;
using LordsMobileBot;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LordsBotGate;

internal static class WebControlServer
{
	private sealed class AccountLookupResult
	{
		public object Record { get; init; }

		public object? Container { get; init; }

		public object? Settings { get; init; }

		public object? BankSettings { get; init; }

		public object? ManageGuild { get; init; }
	}

	private static int _started;

	private static AccountWindow? _accountWindow;

	private static Action<string>? _trace;

	private static readonly object SaveLock;

	private static readonly JsonSerializerSettings SafePopulateSettings;

	private static readonly JsonSerializerSettings ReadSerializeSettings;

	private static readonly string[] SettingsControlTypeNames;

	public static void Start(AccountWindow accountWindow, Action<string> trace)
	{
		if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
		{
			_accountWindow = accountWindow;
			_trace = trace;
			int port = 5050;
			string environmentVariable = Environment.GetEnvironmentVariable("LORDSBOT_WEB_PORT");
			if (!string.IsNullOrEmpty(environmentVariable) && int.TryParse(environmentVariable, out var result) && result > 0)
			{
				port = result;
			}
			Task.Run(() => RunServerLoopAsync(port));
			if (port != 5556)
			{
				Task.Run(() => RunServerLoopAsync(5556));
			}
			Task.Run((Func<Task?>)RunLiveUpdatesSpoolerAsync);
			Task.Run((Func<Task?>)RunConfigReloadWatcherAsync);
		}
	}

	private static void Log(string message)
	{
		try
		{
			_trace?.Invoke("[WebUI] " + message);
		}
		catch
		{
		}
	}

	private static async Task RunServerLoopAsync(int port)
	{
		while (true)
		{
			HttpListener? listener = null;
			try
			{
				listener = new HttpListener();
				try
				{
					listener.Prefixes.Add($"http://127.0.0.1:{port}/");
					listener.Start();
				}
				catch (Exception exPrefix)
				{
					Log($"Primary prefix 127.0.0.1:{port} failed: {exPrefix.Message}, trying localhost...");
					try { listener.Close(); } catch { }
					listener = new HttpListener();
					listener.Prefixes.Add($"http://localhost:{port}/");
					listener.Start();
				}
				Log($"Listening on http://127.0.0.1:{port}/ (live UI sync active)");
				while (listener.IsListening)
				{
					HttpListenerContext ctx = await listener.GetContextAsync().ConfigureAwait(continueOnCapturedContext: false);
					Task.Run(() =>
					{
						HandleContext(ctx);
					});
				}
			}
			catch (Exception ex)
			{
				Log($"Listener error on port {port}: {ex.Message}; retrying in 2s...");
			}
			finally
			{
				try
				{
					listener?.Close();
				}
				catch
				{
				}
			}
			await Task.Delay(2000).ConfigureAwait(continueOnCapturedContext: false);
		}
	}

	private static void HandleContext(HttpListenerContext ctx)
	{
		HttpListenerRequest request = ctx.Request;
		HttpListenerResponse response = ctx.Response;
		response.AddHeader("Access-Control-Allow-Origin", "*");
		response.AddHeader("Access-Control-Allow-Methods", "GET, POST, PUT, PATCH, OPTIONS");
		response.AddHeader("Access-Control-Allow-Headers", "Content-Type");
		response.AddHeader("Cache-Control", "no-store");
		try
		{
			if (request.HttpMethod == "OPTIONS")
			{
				response.StatusCode = 204;
				response.Close();
				return;
			}
			string text = (request.Url?.AbsolutePath ?? "/").TrimEnd('/');
			if (string.IsNullOrEmpty(text))
			{
				text = "/";
			}
			if (request.HttpMethod == "GET" && (text == "/" || text == "/index.html"))
			{
				ServeDashboardHtml(response);
				return;
			}
			if (request.HttpMethod == "GET" && text == "/api/accounts")
			{
				List<object> list = RunOnUi(GetAccountsSnapshot);
				WriteJson(response, 200, JsonConvert.SerializeObject((object)list, ReadSerializeSettings));
				return;
			}
			switch (text)
			{
			case "/api/accounts/reload-all":
			case "/api/reload-all":
			{
				List<long> reloaded = ReloadAllAccountsFromDisk();
				WriteJson(response, 200, JsonConvert.SerializeObject((object)new
				{
					success = true,
					reloaded = reloaded
				}));
				return;
			}
			case "/api/accounts/start-all":
				StartAllAccounts(response);
				return;
			case "/api/accounts/create":
			{
				bool res = RunOnUi(() =>
				{
					Program.BypassCreateNewAccountClick(_accountWindow!, null!, null!);
					return true;
				});
				WriteJson(response, 200, JsonConvert.SerializeObject(new { success = res }));
				return;
			}
			case "/api/accounts/bulk-create":
			{
				bool res = RunOnUi(() =>
				{
					Program.BypassBulkCreateAccountsClick(_accountWindow!, null!, null!);
					return true;
				});
				WriteJson(response, 200, JsonConvert.SerializeObject(new { success = res }));
				return;
			}
			case "/api/accounts/bulk-create/start":
			{
				string reqBody = "";
				try { using var rdr = new StreamReader(request.InputStream, request.ContentEncoding); reqBody = rdr.ReadToEnd(); } catch { }
				int targetCount = 0;
				if (!string.IsNullOrEmpty(reqBody))
				{
					try
					{
						var match = System.Text.RegularExpressions.Regex.Match(reqBody, @"\d+");
						if (match.Success && int.TryParse(match.Value, out int cnt) && cnt > 0)
						{
							targetCount = cnt;
						}
					}
					catch { }
				}
				object resultData = RunOnUi<object>(() =>
				{
					var awType = _accountWindow!.GetType();
					var formField = awType.GetField("createAccForm", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
					var bulkForm = formField?.GetValue(null) as Form;
					if (bulkForm == null || bulkForm.IsDisposed)
					{
						Program.BypassBulkCreateAccountsClick(_accountWindow!, null!, null!);
						bulkForm = formField?.GetValue(null) as Form;
					}
					if (bulkForm != null && !bulkForm.IsDisposed)
					{
						var ft = bulkForm.GetType();
						var BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
						Program.PrepareBulkFormForExecution(bulkForm, targetCount);
						var btn = ft.GetField("bulkCreateAccBtn", BF)?.GetValue(bulkForm) as Control;
						if (btn != null && btn.Enabled)
						{
							var clickMethod = btn.GetType().GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic);
							clickMethod?.Invoke(btn, new object[] { EventArgs.Empty });
							return new { success = true, target = targetCount };
						}
						return new { success = false, message = "bulkCreateAccBtn is disabled or not found" };
					}
					return new { success = false, message = "bulkCreateAccountForm could not be opened" };
				});
				WriteJson(response, 200, JsonConvert.SerializeObject(resultData));
				return;
			}
			case "/api/accounts/bulk-create/status":
			{
				object statusData = RunOnUi<object>(() =>
				{
					var awType = _accountWindow?.GetType();
					var formField = awType?.GetField("createAccForm", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
					var bulkForm = formField?.GetValue(null) as Form;
					if (bulkForm != null && !bulkForm.IsDisposed)
					{
						var ft = bulkForm.GetType();
						var BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
						int succ = Convert.ToInt32(ft.GetField("successAmount", BF)?.GetValue(null) ?? 0);
						int fail = Convert.ToInt32(ft.GetField("failedAmount", BF)?.GetValue(null) ?? 0);
						int cur = Math.Max(Convert.ToInt32(ft.GetField("currentAccount", BF)?.GetValue(null) ?? 0), succ + fail);
						int target = Convert.ToInt32(ft.GetField("accountsToMake", BF)?.GetValue(null) ?? 0);
						int curProxy = Convert.ToInt32(ft.GetField("currentProxy", BF)?.GetValue(null) ?? 0);
						var selProxies = ft.GetField("selectedProxies", BF)?.GetValue(bulkForm) as System.Collections.IList;
						var allProxies = ft.GetField("proxyList", BF)?.GetValue(bulkForm) as System.Collections.IList;
						var wrk = ft.GetField("accountWorker", BF)?.GetValue(bulkForm) as System.ComponentModel.BackgroundWorker;
						bool isBusy = wrk?.IsBusy ?? false;
						return new
						{
							isOpen = true,
							title = bulkForm.Text,
							isBusy = isBusy,
							success = succ,
							failed = fail,
							current = cur,
							target = target,
							currentProxy = curProxy,
							selectedProxiesCount = selProxies?.Count ?? 0,
							proxyListCount = allProxies?.Count ?? 0
						};
					}
					return new { isOpen = false, isBusy = false };
				});
				WriteJson(response, 200, JsonConvert.SerializeObject(statusData));
				return;
			}
			}
			if (text.StartsWith("/api/accounts/", StringComparison.OrdinalIgnoreCase))
			{
				string[] array = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
				if (array.Length >= 4 && long.TryParse(array[2], out var result))
				{
					string action = string.Join("/", array.Skip(3)).ToLowerInvariant();
					HandleAccountAction(request, response, result, action);
					return;
				}
			}
			WriteError(response, 404, "Endpoint not found");
		}
		catch (Exception ex)
		{
			Log($"Request error ({request.HttpMethod} {request.Url?.AbsolutePath}): {ex.Message}");
			try
			{
				WriteError(response, 500, ex.Message);
			}
			catch
			{
			}
		}
	}

	private static void HandleAccountAction(HttpListenerRequest req, HttpListenerResponse resp, long iggId, string action)
	{
		string text = req.HttpMethod.ToUpperInvariant();
		object obj;
		switch (text)
		{
		default:
			obj = null;
			break;
		case "POST":
		case "PUT":
		case "PATCH":
			obj = ReadBody(req);
			break;
		}
		string body = (string)obj;
		(int, string) tuple;
		if (action == "reload" && (text == "POST" || text == "GET"))
		{
			tuple = ReloadAccountFromDisk(iggId);
		}
		else if ((text == "PUT" || text == "PATCH") && action == "settings")
		{
			tuple = HandleSettingsPost(iggId, body, !string.Equals(req.QueryString["persist"], "false", StringComparison.OrdinalIgnoreCase));
		}
		else if (!(text == "GET"))
		{
			if (!(text == "POST"))
			{
				goto IL_053a;
			}
			switch (action)
			{
			case "start":
				break;
			case "stop":
				goto IL_02d4;
			case "settings":
				goto IL_02e5;
			default:
				goto IL_053a;
			case "bank":
				goto IL_054d;
			case "refresh-gui":
			case "sync-hero-priority":
				goto IL_05a2;
			case "save":
				goto IL_05cf;
			case "reset-stats":
				goto IL_0628;
			case "army-arena":
			case "army-colosseum":
				goto IL_0639;
			case "solo-battlefield":
				goto IL_06a7;
			case "arena":
				goto IL_06c8;
			case "bank/adjust-balance":
				goto IL_0719;
			}
			tuple = StartSingleAccount(iggId);
		}
		else
		{
			switch (action)
			{
			case "start":
				break;
			case "stop":
				goto IL_04b2;
			case "settings":
				goto IL_04c3;
			case "solo-battlefield":
				goto IL_04f3;
			case "arena":
				goto IL_0520;
			default:
				goto IL_053a;
			case "army-arena":
			case "army-colosseum":
				goto IL_055c;
			case "refresh-gui":
				goto IL_05a2;
			case "bank":
				goto IL_05fc;
			case "gear-sets":
				goto IL_065a;
			case "heroes":
				goto IL_0677;
			case "activity-currencies":
				goto IL_06e9;
			}
			tuple = StartSingleAccount(iggId);
		}
		goto IL_0587;
		IL_0719:
		tuple = HandleBankAdjustBalance(iggId, body);
		goto IL_0587;
		IL_0587:
		var (statusCode, json) = tuple;
		WriteJson(resp, statusCode, json);
		return;
		IL_02d4:
		tuple = StopSingleAccount(iggId);
		goto IL_0587;
		IL_05cf:
		tuple = RunWithAccount(iggId, (AccountLookupResult acc) =>
		{
			if (acc.Settings == null)
			{
				return NotFound("Settings not loaded");
			}
			SanitizeSettingsDefaults(acc.Settings);
			InvokeSave(acc.Settings);
			return Ok("Settings saved to disk");
		});
		goto IL_0587;
		IL_065a:
		tuple = RunWithAccount(iggId, (AccountLookupResult acc) => (200, JsonConvert.SerializeObject((object)ExtractGearSets(iggId, acc.Settings))));
		goto IL_0587;
		IL_05fc:
		tuple = RunWithAccount(iggId, HandleBankGet);
		goto IL_0587;
		IL_05a2:
		tuple = RunWithAccount(iggId, (AccountLookupResult acc) =>
		{
			if (acc.Container == null || acc.Settings == null)
			{
				return NotFound("Account not initialized");
			}
			bool flag = DispatchLiveGuiRefreshOnUi(acc.Container, acc.Settings);
			return Ok(flag ? "Bot UI refreshed live" : "Account matched; botControlForm is closed", new
			{
				uiRefreshed = flag
			});
		});
		goto IL_0587;
		IL_06e9:
		tuple = RunWithAccount(iggId, (AccountLookupResult _) => (200, JsonConvert.SerializeObject(BuildEmptyCurrencies())));
		goto IL_0587;
		IL_0677:
		tuple = RunWithAccount(iggId, (AccountLookupResult acc) => (200, JsonConvert.SerializeObject(BuildHeroesResponse(acc.Settings))));
		goto IL_0587;
		IL_053a:
		tuple = NotFound("Unknown action: " + action);
		goto IL_0587;
		IL_02e5:
		tuple = HandleSettingsPost(iggId, body, !string.Equals(req.QueryString["persist"], "false", StringComparison.OrdinalIgnoreCase));
		goto IL_0587;
		IL_04b2:
		tuple = StopSingleAccount(iggId);
		goto IL_0587;
		IL_04c3:
		tuple = RunWithAccount(iggId, (AccountLookupResult acc) => (acc.Settings == null) ? NotFound("Settings not loaded") : (200, JsonConvert.SerializeObject(acc.Settings, ReadSerializeSettings)));
		goto IL_0587;
		IL_06c8:
		tuple = HandleSubObjectPost(iggId, body, "arenaSettings", "arenaSettings", "Colosseum settings updated live");
		goto IL_0587;
		IL_06a7:
		tuple = HandleEventSubPost(iggId, body, "SoloBattlefield", "settings", "Solo Battlefield settings updated live");
		goto IL_0587;
		IL_04f3:
		tuple = RunWithAccount(iggId, (AccountLookupResult acc) => ((int, string))HandleEventSubGet(acc.Settings, "SoloBattlefield"));
		goto IL_0587;
		IL_0639:
		tuple = HandleEventSubPost(iggId, body, "ArmyArena", "settings", "Army Arena settings updated live");
		goto IL_0587;
		IL_0628:
		tuple = HandleResetStats(iggId);
		goto IL_0587;
		IL_0520:
		tuple = RunWithAccount(iggId, (AccountLookupResult acc) => ((int, string))HandleArenaGet(iggId, acc.Settings));
		goto IL_0587;
		IL_055c:
		tuple = RunWithAccount(iggId, (AccountLookupResult acc) => ((int, string))HandleEventSubGet(acc.Settings, "ArmyArena"));
		goto IL_0587;
		IL_054d:
		tuple = HandleBankPost(iggId, body);
		goto IL_0587;
	}

	private static (int status, string json) HandleSettingsPost(long iggId, string? body, bool persist)
	{
		if (string.IsNullOrWhiteSpace(body))
		{
			return BadRequest("Empty JSON body");
		}
		JObject.Parse(body);
		return RunWithAccount(iggId, (AccountLookupResult acc) =>
		{
			if (acc.Container == null || acc.Settings == null)
			{
				return NotFound($"Account {iggId} or settings not loaded");
			}
			JsonConvert.PopulateObject(body, acc.Settings, SafePopulateSettings);
			SanitizeSettingsDefaults(acc.Settings);
			if (persist)
			{
				InvokeSave(acc.Settings);
			}
			bool flag = DispatchLiveGuiRefreshOnUi(acc.Container, acc.Settings);
			Log($"Updated settings for {iggId} (uiOpen={flag}, persist={persist})");
			return Ok(flag ? "Applied live to bot memory, disk & Gate UI" : "Applied live to bot memory & disk (botControlForm currently closed)", new
			{
				uiRefreshed = flag,
				persisted = persist
			});
		});
	}

	private static (int status, string json) HandleBankGet(AccountLookupResult acc)
	{
		if (acc.BankSettings == null)
		{
			return NotFound("Bank settings not loaded");
		}
		JObject val = JObject.FromObject(acc.BankSettings, JsonSerializer.Create(ReadSerializeSettings));
		bool flag = Convert.ToBoolean(ReadMemberValue(acc.ManageGuild, "enableAutoAccept") ?? ((object)false));
		bool flag2 = Convert.ToBoolean(ReadMemberValue(acc.ManageGuild, "enableAutoRank") ?? ((object)false));
		bool flag3 = Convert.ToBoolean(ReadMemberValue(acc.ManageGuild, "enableBlackList") ?? ((object)false));
		val["enableAutoAccept"] = flag;
		val["enableAutoRank"] = flag2;
		val["enableWhiteList"] = (flag & flag2);
		val["enableBlackList"] = flag3;
		val["keepAuthorized"] = Convert.ToBoolean(ReadMemberValue(acc.BankSettings, "keepAuthorized") ?? ((object)false));
		return (status: 200, json: ((JToken)val).ToString((Formatting)1));
	}

	private static (int status, string json) HandleBankPost(long iggId, string? body)
	{
		//IL_006b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0071: Expected Obj, but got Unknown
		if (string.IsNullOrWhiteSpace(body))
		{
			return BadRequest("Empty JSON body");
		}
		JObject incoming = JObject.Parse(body);
		incoming.Remove("outgoingData");
		incoming.Remove("creditedReports");
		JToken val = incoming["accountData"];
		JArray val2 = (JArray)((val is JArray) ? val : null);
		if (val2 != null && ((JContainer)val2).Count == 0)
		{
			incoming.Remove("accountData");
		}
		return RunWithAccount(iggId, (AccountLookupResult acc) =>
		{
			//IL_0062: Unknown result type (might be due to invalid IL or missing references)
			//IL_0069: Invalid comparison between Unknown and I4
			//IL_00a7: Unknown result type (might be due to invalid IL or missing references)
			//IL_00ae: Invalid comparison between Unknown and I4
			//IL_00e6: Unknown result type (might be due to invalid IL or missing references)
			//IL_00ed: Invalid comparison between Unknown and I4
			//IL_0126: Unknown result type (might be due to invalid IL or missing references)
			//IL_012d: Invalid comparison between Unknown and I4
			if (acc.Container == null || acc.BankSettings == null)
			{
				return NotFound("Account or bank settings not loaded");
			}
			JsonConvert.PopulateObject(((JToken)incoming).ToString((Formatting)0), acc.BankSettings, SafePopulateSettings);
			InvokeSave(acc.BankSettings);
			if (acc.ManageGuild != null)
			{
				JToken val3 = incoming["enableWhiteList"];
				bool? flag = ((val3 != null && (int)val3.Type == 9) ? new bool?(Extensions.Value<bool>((IEnumerable<JToken>)incoming["enableWhiteList"])) : ((bool?)null));
				JToken val4 = incoming["enableAutoAccept"];
				bool? flag2 = ((val4 != null && (int)val4.Type == 9) ? new bool?(Extensions.Value<bool>((IEnumerable<JToken>)incoming["enableAutoAccept"])) : flag);
				JToken val5 = incoming["enableAutoRank"];
				bool? flag3 = ((val5 != null && (int)val5.Type == 9) ? new bool?(Extensions.Value<bool>((IEnumerable<JToken>)incoming["enableAutoRank"])) : flag);
				JToken val6 = incoming["enableBlackList"];
				bool? flag4 = ((val6 != null && (int)val6.Type == 9) ? new bool?(Extensions.Value<bool>((IEnumerable<JToken>)incoming["enableBlackList"])) : ((bool?)null));
				if (flag2.HasValue)
				{
					SetMemberValue(acc.ManageGuild, "enableAutoAccept", flag2.Value);
				}
				if (flag3.HasValue)
				{
					SetMemberValue(acc.ManageGuild, "enableAutoRank", flag3.Value);
				}
				if (flag4.HasValue)
				{
					SetMemberValue(acc.ManageGuild, "enableBlackList", flag4.Value);
				}
				InvokeSave(acc.ManageGuild);
			}
			if (acc.Settings != null)
			{
				DispatchLiveGuiRefreshOnUi(acc.Container, acc.Settings);
			}
			Log($"Updated bank settings for {iggId} (Bank Shield active)");
			return Ok("Bank settings updated live with Bank Shield active!");
		});
	}

	private static (int status, string json) HandleBankAdjustBalance(long iggId, string? body)
	{
		//IL_015c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0163: Expected Obj, but got Unknown
		if (string.IsNullOrWhiteSpace(body))
		{
			return BadRequest("Empty JSON body");
		}
		JObject val = JObject.Parse(body);
		JToken val2 = val["userId"];
		long? num = ((val2 != null) ? Extensions.Value<long?>((IEnumerable<JToken>)val2) : ((long?)null));
		long valueOrDefault;
		if (!num.HasValue)
		{
			JToken val3 = val["UserID"];
			valueOrDefault = ((val3 != null) ? Extensions.Value<long?>((IEnumerable<JToken>)val3) : ((long?)null)).GetValueOrDefault();
		}
		else
		{
			valueOrDefault = num.GetValueOrDefault();
		}
		long targetUserId = valueOrDefault;
		JToken val4 = val["accountName"];
		object obj = ((val4 != null) ? Extensions.Value<string>((IEnumerable<JToken>)val4) : null);
		if (obj == null)
		{
			JToken val5 = val["AccountName"];
			obj = ((val5 != null) ? Extensions.Value<string>((IEnumerable<JToken>)val5) : null);
			if (obj == null)
			{
				JToken val6 = val["name"];
				obj = ((val6 != null) ? Extensions.Value<string>((IEnumerable<JToken>)val6) : null) ?? "";
			}
		}
		string memberName = (string)obj;
		JToken val7 = val["setExact"];
		bool setExact = ((val7 != null) ? Extensions.Value<bool?>((IEnumerable<JToken>)val7) : ((bool?)null)) == true;
		long[] deltas = new long[5];
		JToken val8 = val["rssDeltas"];
		JArray val9 = (JArray)((val8 is JArray) ? val8 : null);
		if (val9 != null && ((JContainer)val9).Count >= 5)
		{
			for (int i = 0; i < 5; i++)
			{
				long[] array = deltas;
				int num2 = i;
				JToken val10 = val9[i];
				array[num2] = ((val10 != null) ? Extensions.Value<long?>((IEnumerable<JToken>)val10) : ((long?)null)).GetValueOrDefault();
			}
		}
		else
		{
			string[] array2 = new string[5] { "food", "stone", "wood", "ore", "gold" };
			for (int j = 0; j < 5; j++)
			{
				long[] array3 = deltas;
				int num3 = j;
				JToken val11 = val[array2[j]];
				array3[num3] = ((val11 != null) ? Extensions.Value<long?>((IEnumerable<JToken>)val11) : ((long?)null)).GetValueOrDefault();
			}
		}
		return RunWithAccount(iggId, (AccountLookupResult acc) =>
		{
			if (acc.Container == null || acc.BankSettings == null)
			{
				return NotFound("Bank settings not loaded");
			}
			if (!(ReadMemberValue(acc.BankSettings, "accountData") is IList list))
			{
				return NotFound("accountData missing");
			}
			object obj2 = null;
			foreach (object item in list)
			{
				if (item != null)
				{
					long num4 = Convert.ToInt64(ReadMemberValue(item, "UserID") ?? ((object)0));
					string text = (ReadMemberValue(item, "AccountName") ?? ReadMemberValue(item, "Name") ?? "").ToString() ?? "";
					if ((targetUserId > 0 && num4 == targetUserId) || (!string.IsNullOrEmpty(memberName) && text.Equals(memberName, StringComparison.OrdinalIgnoreCase)))
					{
						obj2 = item;
						break;
					}
				}
			}
			if (obj2 == null)
			{
				return NotFound("Bank member not found");
			}
			if (ReadMemberValue(obj2, "accountBalance") is long[] array4 && array4.Length >= 5)
			{
				for (int k = 0; k < 5; k++)
				{
					array4[k] = Math.Max(0L, setExact ? deltas[k] : (array4[k] + deltas[k]));
				}
			}
			InvokeSave(acc.BankSettings);
			if (acc.Settings != null)
			{
				DispatchLiveGuiRefreshOnUi(acc.Container, acc.Settings);
			}
			return Ok("Adjusted balance for " + (string.IsNullOrEmpty(memberName) ? targetUserId.ToString() : memberName));
		});
	}

	private static (int status, string json) HandleArenaGet(long iggId, object? settings)
	{
		object obj = ReadMemberValue(settings, "arenaSettings");
		if (obj == null)
		{
			return NotFound("Arena settings not loaded");
		}
		return (status: 200, json: JsonConvert.SerializeObject((object)new
		{
			success = true,
			iggId = iggId,
			arenaSettings = obj,
			accountHeroes = ExtractSimpleHeroes(settings)
		}, ReadSerializeSettings));
	}

	private static (int status, string json) HandleSubObjectPost(long iggId, string? body, string propertyName, string wrapperKey, string msg)
	{
		//IL_0049: Unknown result type (might be due to invalid IL or missing references)
		if (string.IsNullOrWhiteSpace(body))
		{
			return BadRequest("Empty JSON body");
		}
		JObject val = JObject.Parse(body);
		JToken val2 = val[wrapperKey];
		string targetJson = ((JToken)(((object)((val2 is JObject) ? val2 : null)) ?? ((object)val))).ToString((Formatting)0);
		return RunWithAccount(iggId, (AccountLookupResult acc) =>
		{
			object obj = ReadMemberValue(acc.Settings, propertyName);
			if (acc.Container == null || acc.Settings == null || obj == null)
			{
				return NotFound(propertyName + " not loaded");
			}
			JsonConvert.PopulateObject(targetJson, obj, SafePopulateSettings);
			SanitizeSettingsDefaults(acc.Settings);
			InvokeSave(acc.Settings);
			bool uiRefreshed = DispatchLiveGuiRefreshOnUi(acc.Container, acc.Settings);
			return Ok(msg, new { uiRefreshed });
		});
	}

	private static (int status, string json) HandleEventSubGet(object? settings, string eventField)
	{
		object obj = ReadMemberValue(ReadMemberValue(settings, "eventSettings"), eventField);
		if (obj == null)
		{
			return NotFound(eventField + " settings not loaded");
		}
		return (status: 200, json: JsonConvert.SerializeObject((object)new
		{
			success = true,
			settings = obj,
			statusRows = Array.Empty<object>()
		}, ReadSerializeSettings));
	}

	private static (int status, string json) HandleEventSubPost(long iggId, string? body, string eventField, string wrapperKey, string msg)
	{
		//IL_0049: Unknown result type (might be due to invalid IL or missing references)
		if (string.IsNullOrWhiteSpace(body))
		{
			return BadRequest("Empty JSON body");
		}
		JObject val = JObject.Parse(body);
		JToken val2 = val[wrapperKey];
		string targetJson = ((JToken)(((object)((val2 is JObject) ? val2 : null)) ?? ((object)val))).ToString((Formatting)0);
		return RunWithAccount(iggId, (AccountLookupResult acc) =>
		{
			object obj = ReadMemberValue(ReadMemberValue(acc.Settings, "eventSettings"), eventField);
			if (acc.Container == null || acc.Settings == null || obj == null)
			{
				return NotFound(eventField + " not loaded");
			}
			JsonConvert.PopulateObject(targetJson, obj, SafePopulateSettings);
			SanitizeSettingsDefaults(acc.Settings);
			InvokeSave(acc.Settings);
			bool uiRefreshed = DispatchLiveGuiRefreshOnUi(acc.Container, acc.Settings);
			return Ok(msg, new { uiRefreshed });
		});
	}

	private static (int status, string json) HandleResetStats(long iggId)
	{
		return RunWithAccount(iggId, (AccountLookupResult acc) =>
		{
			if (acc.Container == null || acc.Settings == null)
			{
				return NotFound("Account not loaded");
			}
			object obj = ReadMemberValue(acc.Settings, "monsterSettings");
			if (obj != null)
			{
				SetMemberValue(obj, "killsToday", 0);
			}
			InvokeSave(acc.Settings);
			DispatchLiveGuiRefreshOnUi(acc.Container, acc.Settings);
			return Ok("Guild & hunt statistics reset live in Gate");
		});
	}

	private static List<object> ExtractGearSets(long iggId, object? settings)
	{
		List<object> list = new List<object>
		{
			new
			{
				value = -1,
				label = "Current gear",
				hint = "Equip currently active gear set"
			}
		};
		if (ReadMemberValue(ReadMemberValue(settings, "gearSettings"), "_gearSet") is IList { Count: >0 } list2)
		{
			for (int i = 0; i < list2.Count; i++)
			{
				object obj = list2[i];
				if (obj != null)
				{
					string label = ReadMemberValue(obj, "setName")?.ToString() ?? $"Gear Set {i + 1}";
					int num = Convert.ToInt32(ReadMemberValue(obj, "SerialNO") ?? ((object)i));
					list.Add(new
					{
						value = num,
						label = label,
						hint = $"Quick Swap Slot #{num + 1}"
					});
				}
			}
			return list;
		}
		string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", iggId.ToString(), "gears.json");
		if (File.Exists(path))
		{
			try
			{
				List<object> list3 = JsonConvert.DeserializeObject<List<object>>(File.ReadAllText(path));
				if (list3 != null && list3.Count > 0)
				{
					return list3;
				}
			}
			catch
			{
			}
		}
		for (int j = 0; j < 8; j++)
		{
			list.Add(new
			{
				value = j,
				label = $"Gear Set {j + 1}",
				hint = $"Preset formation {j + 1}"
			});
		}
		return list;
	}

	private static object BuildHeroesResponse(object? settings)
	{
		object target = ReadMemberValue(settings, "heroSettings");
		return new
		{
			success = true,
			heroes = ExtractSimpleHeroes(settings),
			heroPriority = (ReadMemberValue(target, "heroPriority") ?? Array.Empty<int>()),
			travelStatus = "Loaded from live Gate memory",
			items = new
			{
				hornOfStrength = 0,
				featherOfDexterity = 0,
				eyeOfIntelligence = 0,
				sagaCompass = 0,
				silverDragonscale = 0,
				goldDragonscale = 0,
				infantryRecastDice = 0,
				rangedRecastDice = 0,
				cavalryRecastDice = 0,
				siegeEngineRecastDice = 0
			}
		};
	}

	private static List<object> ExtractSimpleHeroes(object? settings)
	{
		List<object> list = new List<object>();
		if (!(ReadMemberValue(settings, "heroData") is IEnumerable enumerable))
		{
			return list;
		}
		foreach (object item in enumerable)
		{
			if (item != null)
			{
				int num = Convert.ToInt32(ReadMemberValue(item, "HeroID") ?? ((object)0));
				if (num > 0)
				{
					int num2 = Convert.ToInt32(ReadMemberValue(item, "Enhanced") ?? ((object)0));
					int num3 = Convert.ToInt32(ReadMemberValue(item, "Medals") ?? ((object)0));
					list.Add(new
					{
						heroId = num,
						level = Convert.ToInt32(ReadMemberValue(item, "Level") ?? ((object)0)),
						exp = Convert.ToInt32(ReadMemberValue(item, "Exp") ?? ((object)0)),
						rank = Convert.ToInt32(ReadMemberValue(item, "Rank") ?? ((object)0)),
						enhance = num2,
						star = num2,
						medals = num3,
						medalsDisplay = ((num2 >= 5) ? "0" : $"{num3} / 20")
					});
				}
			}
		}
		return list;
	}

	private static object BuildEmptyCurrencies()
	{
		return new
		{
			stars = 0,
			dazzlingStars = 0,
			tycoonTokens = 0,
			marvelousTokens = 0,
			powerRings = 0,
			raw = new
			{
				stars = "Stars: 0",
				dazzlingStars = "Dazzling Star: 0",
				tycoonTokens = "Tokens: 0",
				marvelousTokens = "Marvelous Token: 0",
				powerRings = "Power Ring: 0"
			}
		};
	}

	private static (int status, string json) RunWithAccount(long iggId, Func<AccountLookupResult, (int, string)> action)
	{
		return RunOnUi(() =>
		{
			AccountLookupResult accountLookupResult = FindAccountByIgg(iggId);
			return (accountLookupResult == null) ? NotFound($"Account {iggId} not found") : action(accountLookupResult);
		});
	}

	private static string? ReadBody(HttpListenerRequest req)
	{
		using StreamReader streamReader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
		return streamReader.ReadToEnd();
	}

	private static T RunOnUi<T>(Func<T> func)
	{
		AccountWindow accountWindow = _accountWindow;
		if (accountWindow == null || ((Control)(object)accountWindow).IsDisposed || !((Control)(object)accountWindow).IsHandleCreated || !((Control)(object)accountWindow).InvokeRequired)
		{
			return func();
		}
		return ((Control)(object)accountWindow).Invoke(func);
	}

	private static List<object> GetAccountsSnapshot()
	{
		List<object> list = new List<object>();
		HashSet<object> hashSet = (from c in GetOpenBotControlForms().Select(GetCurrentAcc)
			where c != null
			select c).ToHashSet();
		foreach (object item in EnumerateGridAccounts())
		{
			long num = ReadIgg(item);
			if (num > 0)
			{
				object obj = ReadMemberValue(item, "Container");
				bool hasSettings = ReadMemberValue(obj, "Settings") != null;
				list.Add(new
				{
					iggId = num,
					name = ((ReadMemberValue(item, "Name") ?? ReadMemberValue(item, "_Name") ?? ((object)num)).ToString() ?? num.ToString()),
					status = (ReadMemberValue(item, "Status")?.ToString() ?? ReadMemberValue(item, "_Status")?.ToString() ?? ""),
					action = (ReadMemberValue(item, "Action")?.ToString() ?? ReadMemberValue(item, "_Action")?.ToString() ?? ""),
					guild = (ReadMemberValue(item, "Guild")?.ToString() ?? ReadMemberValue(item, "_Guild")?.ToString() ?? ""),
					might = (ReadMemberValue(item, "Might")?.ToString() ?? ReadMemberValue(item, "_Might")?.ToString() ?? ""),
					gems = (ReadMemberValue(item, "Gems")?.ToString() ?? ReadMemberValue(item, "_Gems")?.ToString() ?? ""),
					kingdom = (ReadMemberValue(item, "Kingdom")?.ToString() ?? ReadMemberValue(item, "_Kingdom")?.ToString() ?? ""),
					@base = (ReadMemberValue(item, "Base")?.ToString() ?? ReadMemberValue(item, "_Base")?.ToString() ?? ""),
					hasSettings = hasSettings,
					isOpenInBotControlForm = (obj != null && hashSet.Contains(obj))
				});
			}
		}
		return list;
	}

	private static AccountLookupResult? FindAccountByIgg(long iggId)
	{
		foreach (object item in EnumerateGridAccounts())
		{
			if (ReadIgg(item) == iggId)
			{
				object obj = ReadMemberValue(item, "Container");
				return new AccountLookupResult
				{
					Record = item,
					Container = obj,
					Settings = ReadMemberValue(obj, "Settings"),
					BankSettings = ReadFieldByType(obj, "GBSettings", Program.Schema?.ContainerGBSettingsField),
					ManageGuild = ReadFieldByType(obj, "MGSettings", Program.Schema?.ContainerMGSettingsField)
				};
			}
		}
		return null;
	}

	private static IEnumerable<object> EnumerateGridAccounts()
	{
		if (_accountWindow == null || ((Control)(object)_accountWindow).IsDisposed)
		{
			yield break;
		}
		object obj = typeof(AccountWindow).GetField("accountControl", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(_accountWindow);
		GridControl val = (GridControl)((obj is GridControl) ? obj : null);
		if (val == null || !(val.DataSource is IEnumerable enumerable))
		{
			yield break;
		}
		foreach (object item in enumerable)
		{
			if (item != null)
			{
				yield return item;
			}
		}
	}

	private static long ReadIgg(object record)
	{
		object obj = ReadMemberValue(record, "IGG") ?? ReadMemberValue(record, "_IGG") ?? ReadMemberValue(record, "IGG_S");
		if (obj == null || !long.TryParse(obj.ToString(), out var result))
		{
			return 0L;
		}
		return result;
	}

	private static object? ReadFieldByType(object? target, string fieldTypeName, FieldInfo? resolved)
	{
		if (target == null)
		{
			return null;
		}
		if (resolved != null)
		{
			return resolved.GetValue(target);
		}
		return target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((FieldInfo fi) => fi.FieldType.Name == fieldTypeName)?.GetValue(target);
	}

	private static object? ReadMemberValue(object? target, string name)
	{
		if (target == null)
		{
			return null;
		}
		Type type = target.GetType();
		PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		if (property != null)
		{
			return property.GetValue(target);
		}
		return type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target);
	}

	private static void SetMemberValue(object target, string name, object? value)
	{
		Type type = target.GetType();
		PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		if (property != null && property.CanWrite)
		{
			property.SetValue(target, value);
		}
		else
		{
			type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(target, value);
		}
	}

	private static void InvokeSave(object target)
	{
		lock (SaveLock)
		{
			target.GetType().GetMethod("Save", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)?.Invoke(target, null);
		}
	}

	internal static bool SanitizeSettingsDefaults(object? settings)
	{
		if (settings == null)
		{
			return false;
		}
		bool result = false;
		try
		{
			object obj = ReadMemberValue(settings, "connectionSettings");
			if (obj != null && ReadMemberValue(obj, "savedProxy") == null)
			{
				SetMemberValue(obj, "savedProxy", "");
				result = true;
			}
			object obj2 = ReadMemberValue(settings, "supplySettings");
			if (obj2 != null)
			{
				if (ReadMemberValue(obj2, "reservedRss") == null)
				{
					SetMemberValue(obj2, "reservedRss", new long[5] { 1000000L, 1000000L, 1000000L, 1000000L, 1000000L });
					result = true;
				}
				if (ReadMemberValue(obj2, "supplyMin") == null)
				{
					SetMemberValue(obj2, "supplyMin", new long[5] { 3000000L, 3000000L, 3000000L, 3000000L, 3000000L });
					result = true;
				}
				if (ReadMemberValue(obj2, "reservedBagRss") == null)
				{
					SetMemberValue(obj2, "reservedBagRss", new long[5]);
					result = true;
				}
				if (ReadMemberValue(obj2, "typesToSend") == null)
				{
					SetMemberValue(obj2, "typesToSend", new bool[5]);
					result = true;
				}
				if (ReadMemberValue(obj2, "bagTypesToSend") == null)
				{
					SetMemberValue(obj2, "bagTypesToSend", new bool[5]);
					result = true;
				}
			}
			object obj3 = ReadMemberValue(settings, "monsterSettings");
			if (obj3 != null)
			{
				if (ReadMemberValue(obj3, "huntLevels") == null)
				{
					SetMemberValue(obj3, "huntLevels", new bool[5] { true, true, false, false, false });
					result = true;
				}
				if (ReadMemberValue(obj3, "monsterTypes") == null)
				{
					SetMemberValue(obj3, "monsterTypes", new bool[3] { true, true, true });
					result = true;
				}
				if (ReadMemberValue(obj3, "selectedHerosMP") == null)
				{
					SetMemberValue(obj3, "selectedHerosMP", new ushort[5]);
					result = true;
				}
				if (ReadMemberValue(obj3, "selectedHerosM") == null)
				{
					SetMemberValue(obj3, "selectedHerosM", new ushort[5]);
					result = true;
				}
				if (ReadMemberValue(obj3, "monstersToHunt_") == null)
				{
					Dictionary<int, bool> dictionary = new Dictionary<int, bool>();
					for (int i = 0; i <= 75; i++)
					{
						dictionary[i] = true;
					}
					SetMemberValue(obj3, "monstersToHunt_", dictionary);
					result = true;
				}
			}
			object obj4 = ReadMemberValue(settings, "eventSettings");
			if (obj4 != null)
			{
				object obj5 = ReadMemberValue(obj4, "guildFest");
				if (obj5 != null)
				{
					Assembly assembly = obj5.GetType().Assembly;
					object obj6 = ReadMemberValue(obj5, "gfMissionComplete");
					if (obj6 == null)
					{
						Type type = assembly.GetType("LordsMobileBot.AppClasses.Settings.GuildFestMissionComplete");
						if (type != null)
						{
							obj6 = Activator.CreateInstance(type);
							SetMemberValue(obj5, "gfMissionComplete", obj6);
							result = true;
						}
					}
					if (obj6 != null && ReadMemberValue(obj6, "missionsToComplete_") == null)
					{
						Type type2 = assembly.GetType("LordsMobileBot.AppClasses.Settings.toComplete");
						if (type2 != null)
						{
							Type type3 = typeof(Dictionary<, >).MakeGenericType(typeof(ushort), type2);
							SetMemberValue(obj6, "missionsToComplete_", Activator.CreateInstance(type3));
							result = true;
						}
					}
					object obj7 = ReadMemberValue(obj5, "gfMissionRemove");
					if (obj7 == null)
					{
						Type type4 = assembly.GetType("LordsMobileBot.AppClasses.Settings.GuildFestMissionRemove");
						if (type4 != null)
						{
							obj7 = Activator.CreateInstance(type4);
							SetMemberValue(obj5, "gfMissionRemove", obj7);
							result = true;
						}
					}
					if (obj7 != null && ReadMemberValue(obj7, "missionsToRemove_") == null)
					{
						Type type5 = assembly.GetType("LordsMobileBot.AppClasses.Settings.toRemove");
						if (type5 != null)
						{
							Type type6 = typeof(Dictionary<, >).MakeGenericType(typeof(ushort), type5);
							SetMemberValue(obj7, "missionsToRemove_", Activator.CreateInstance(type6));
							result = true;
						}
					}
					if (ReadMemberValue(obj5, "rewardPriority") == null)
					{
						SetMemberValue(obj5, "rewardPriority", new List<ushort>());
						result = true;
					}
				}
				object obj8 = ReadMemberValue(obj4, "Exchange");
				if (obj8 != null)
				{
					Assembly assembly2 = obj8.GetType().Assembly;
					if (ReadMemberValue(obj8, "MissionFilter") == null)
					{
						Type type7 = assembly2.GetType("LordsMobileBot.AppClasses.Settings.ExchangeRemove");
						if (type7 != null)
						{
							Type type8 = typeof(Dictionary<, >).MakeGenericType(typeof(ushort), type7);
							SetMemberValue(obj8, "MissionFilter", Activator.CreateInstance(type8));
							result = true;
						}
					}
				}
				if (ReadMemberValue(obj4, "questTypes") == null)
				{
					Type type9 = obj4.GetType().Assembly.GetType("LordsMobileBot.AppClasses.Settings.QuestTypeSetting");
					if (type9 != null)
					{
						Type type10 = typeof(Dictionary<, >).MakeGenericType(typeof(ushort), type9);
						SetMemberValue(obj4, "questTypes", Activator.CreateInstance(type10));
						result = true;
					}
				}
			}
		}
		catch
		{
		}
		return result;
	}

	private static List<Form> GetOpenBotControlForms()
	{
		List<Form> list = new List<Form>();
		foreach (Form openForm in Application.OpenForms)
		{
			if (openForm != null && !openForm.IsDisposed && openForm.GetType().Name == "botControlForm")
			{
				list.Add(openForm);
			}
		}
		if (typeof(AccountWindow).Assembly.GetType("LordsMobileBot.botControlForm", throwOnError: false)?.GetField("_Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) is Form { IsDisposed: false } form2 && !list.Contains(form2))
		{
			list.Add(form2);
		}
		return list;
	}

	private static object? GetCurrentAcc(Form bcf)
	{
		return ReadMemberValue(bcf, "currentAcc");
	}

	private static bool DispatchLiveGuiRefreshOnUi(object container, object settings)
	{
		try
		{
			(typeof(GridControl).Assembly.GetType("DevExpress.Data.CurrencyDataController") ?? (from a in AppDomain.CurrentDomain.GetAssemblies()
				select a.GetType("DevExpress.Data.CurrencyDataController", throwOnError: false)).FirstOrDefault((Type t) => t != null))?.GetProperty("DisableThreadingProblemsDetection", BindingFlags.Static | BindingFlags.Public)?.SetValue(null, true);
		}
		catch
		{
		}
		List<Form> list = (from f in GetOpenBotControlForms()
			where GetCurrentAcc(f) == container
			select f).ToList();
		if (list.Count == 0)
		{
			return false;
		}
		Assembly assembly = typeof(AccountWindow).Assembly;
		HashSet<Control> hashSet = new HashSet<Control>();
		foreach (Form item in list)
		{
			try
			{
				item.GetType().GetMethod("reloadUISettings", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)?.Invoke(item, null);
			}
			catch (Exception ex)
			{
				Log("reloadUISettings note: " + ex.Message);
			}
			foreach (Control item2 in CollectSettingsControls(item, assembly))
			{
				if (!item2.IsDisposed && hashSet.Add(item2))
				{
					RefreshSettingsControl(item2, container, settings);
				}
			}
			try
			{
				MethodInfo methodInfo = assembly.GetType("LordsMobileBot.settingForm", throwOnError: false)?.GetMethod("refreshPage", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				if (methodInfo != null)
				{
					ParameterInfo[] parameters = methodInfo.GetParameters();
					methodInfo.Invoke(null, (parameters.Length != 1) ? null : new object[1] { true });
				}
				Type? type = assembly.GetType("LordsMobileBot.mainTabForm", throwOnError: false);
				type?.GetMethod("forceRefresh", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(null, null);
				type?.GetMethod("refreshPage", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(null, null);
			}
			catch
			{
			}
			try
			{
				item.Invalidate(invalidateChildren: true);
				item.Refresh();
			}
			catch
			{
			}
			Log($"Dispatched live UI refresh (controls={hashSet.Count})");
		}
		return true;
	}

	private static IEnumerable<Control> CollectSettingsControls(Form bcf, Assembly botAsm)
	{
		foreach (Control item in EnumerateControlTree(bcf))
		{
			yield return item;
		}
		string[] array = new string[2] { "LordsMobileBot.settingForm", "LordsMobileBot.mainTabForm" };
		string[] array2 = array;
		foreach (string name in array2)
		{
			if (!(botAsm.GetType(name, throwOnError: false)?.GetMethod("GetAllPages", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(null, null) is IEnumerable enumerable))
			{
				continue;
			}
			foreach (object item2 in enumerable)
			{
				if (!(item2 is Control root))
				{
					continue;
				}
				foreach (Control item3 in EnumerateControlTree(root))
				{
					yield return item3;
				}
			}
		}
		array = SettingsControlTypeNames;
		array2 = array;
		foreach (string name2 in array2)
		{
			Type type = botAsm.GetType(name2, throwOnError: false);
			if (!(type == null) && (type.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) ?? type.GetField("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) ?? type.GetField("_instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) ?? type.GetField("_Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null)) is Control control)
			{
				yield return control;
			}
		}
	}

	private static IEnumerable<Control> EnumerateControlTree(Control root)
	{
		yield return root;
		if (root.GetType().GetProperty("Pages", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(root) is IEnumerable enumerable)
		{
			foreach (object item in enumerable)
			{
				if (!(item is Control root2))
				{
					continue;
				}
				foreach (Control item2 in EnumerateControlTree(root2))
				{
					yield return item2;
				}
			}
		}
		foreach (Control control in root.Controls)
		{
			foreach (Control item3 in EnumerateControlTree(control))
			{
				yield return item3;
			}
		}
	}

	private static void RefreshSettingsControl(Control ctrl, object container, object settings)
	{
		//IL_0137: Unknown result type (might be due to invalid IL or missing references)
		//IL_013d: Expected Obj, but got Unknown
		try
		{
			Type type = ctrl.GetType();
			FieldInfo field = type.GetField("botInstance", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field != null)
			{
				object value = field.GetValue(ctrl);
				if (value != null && value != container)
				{
					return;
				}
				if (value == null)
				{
					field.SetValue(ctrl, container);
				}
			}
			MethodInfo method = type.GetMethod("LoadExtraControls", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[1] { settings.GetType() }, null);
			if (method != null)
			{
				method.Invoke(ctrl, new object[1] { settings });
			}
			else
			{
				type.GetMethod("LoadExtraControls", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[2]
				{
					container.GetType(),
					settings.GetType()
				}, null)?.Invoke(ctrl, new object[2] { container, settings });
			}
			string[] array = new string[3] { "ShowAutoBuyCount", "LoadGuildCoinOverflowSettings", "LoadQuestTypes" };
			foreach (string name in array)
			{
				type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[1] { settings.GetType() }, null)?.Invoke(ctrl, new object[1] { settings });
			}
			GridControl val = (GridControl)((ctrl is GridControl) ? ctrl : null);
			if (val != null)
			{
				val.RefreshDataSource();
				BaseView mainView = val.MainView;
				if (mainView != null)
				{
					mainView.RefreshData();
				}
			}
			ctrl.Invalidate(invalidateChildren: true);
			ctrl.Update();
		}
		catch
		{
		}
	}

	private static void ServeDashboardHtml(HttpListenerResponse resp)
	{
		string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web", "index.html");
		string s = (File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "<!DOCTYPE html><html><body style='font-family:sans-serif;background:#111;color:#eee;padding:2rem'><h2>LordsBot Gate Web UI</h2><p>Missing file: <code>web/index.html</code></p></body></html>");
		byte[] bytes = Encoding.UTF8.GetBytes(s);
		resp.ContentType = "text/html; charset=utf-8";
		resp.StatusCode = 200;
		resp.ContentLength64 = bytes.Length;
		resp.OutputStream.Write(bytes, 0, bytes.Length);
		resp.Close();
	}

	private static (int, string) Ok(string message, object? extra = null)
	{
		string item = ((extra == null) ? JsonConvert.SerializeObject((object)new
		{
			success = true,
			message = message
		}) : JsonConvert.SerializeObject((object)new
		{
			success = true,
			message = message,
			data = extra
		}));
		return (200, item);
	}

	private static (int, string) BadRequest(string message)
	{
		return (400, JsonConvert.SerializeObject((object)new
		{
			success = false,
			message = message
		}));
	}

	private static (int, string) NotFound(string message)
	{
		return (404, JsonConvert.SerializeObject((object)new
		{
			success = false,
			message = message
		}));
	}

	private static void WriteError(HttpListenerResponse resp, int statusCode, string message)
	{
		WriteJson(resp, statusCode, JsonConvert.SerializeObject((object)new
		{
			success = false,
			message = message
		}));
	}

	private static void WriteJson(HttpListenerResponse resp, int statusCode, string json)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(json);
		resp.ContentType = "application/json; charset=utf-8";
		resp.StatusCode = statusCode;
		resp.ContentLength64 = bytes.Length;
		resp.OutputStream.Write(bytes, 0, bytes.Length);
		resp.Close();
	}

	private static (int status, string json) ReloadAccountFromDisk(long iggId)
	{
		string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", iggId.ToString(), "settings.json");
		if (!File.Exists(path))
		{
			return NotFound($"settings.json not found for {iggId}");
		}
		try
		{
			string body = File.ReadAllText(path);
			return HandleSettingsPost(iggId, body, persist: false);
		}
		catch (Exception ex)
		{
			return (status: 500, json: JsonConvert.SerializeObject((object)new
			{
				success = false,
				message = ex.Message
			}));
		}
	}

	private static List<long> ReloadAllAccountsFromDisk()
	{
		List<long> list = new List<long>();
		try
		{
			foreach (object item in RunOnUi(GetAccountsSnapshot))
			{
				if (item != null)
				{
					long num = ReadIgg(item);
					if (num > 0 && ReloadAccountFromDisk(num).status == 200)
					{
						list.Add(num);
					}
				}
			}
		}
		catch
		{
		}
		return list;
	}

	private static (int status, string json) StartSingleAccount(long iggId)
	{
		return RunOnUi(() =>
		{
			try
			{
				if (FindAccountByIgg(iggId)?.Record == null)
				{
					return NotFound($"Account {iggId} not found");
				}
				foreach (object item in EnumerateGridAccounts())
				{
					PropertyInfo? property = item.GetType().GetProperty("IsSelected");
					long num = ReadIgg(item);
					property?.SetValue(item, num == iggId);
				}
				MethodInfo methodInfo = ((object)_accountWindow)?.GetType().GetMethod("startAccBtn_ButtonClick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				object obj = ((object)_accountWindow)?.GetType().GetField("startAccBtn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(_accountWindow);
				if (methodInfo != null && _accountWindow != null)
				{
					methodInfo.Invoke(_accountWindow, new object[2]
					{
						obj ?? _accountWindow,
						null
					});
					Log($"Invoked start for account {iggId}");
					return Ok($"Started account {iggId}");
				}
				return (500, "{\"error\":\"startAccBtn_ButtonClick not found\"}");
			}
			catch (Exception ex)
			{
				Log($"Error starting account {iggId}: {ex}");
				return (500, JsonConvert.SerializeObject((object)new
				{
					error = ex.Message
				}));
			}
		});
	}

	private static (int status, string json) StopSingleAccount(long iggId)
	{
		return RunOnUi(() =>
		{
			try
			{
				if (FindAccountByIgg(iggId)?.Record == null)
				{
					return NotFound($"Account {iggId} not found");
				}
				foreach (object item in EnumerateGridAccounts())
				{
					PropertyInfo? property = item.GetType().GetProperty("IsSelected");
					long num = ReadIgg(item);
					property?.SetValue(item, num == iggId);
				}
				MethodInfo methodInfo = ((object)_accountWindow)?.GetType().GetMethod("stopAccBtn_ButtonClick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				object obj = ((object)_accountWindow)?.GetType().GetField("stopAccBtn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(_accountWindow);
				if (methodInfo != null && _accountWindow != null)
				{
					methodInfo.Invoke(_accountWindow, new object[2]
					{
						obj ?? _accountWindow,
						null
					});
					Log($"Invoked stop for account {iggId}");
					return Ok($"Stopped account {iggId}");
				}
				return (500, "{\"error\":\"stopAccBtn_ButtonClick not found\"}");
			}
			catch (Exception ex)
			{
				Log($"Error stopping account {iggId}: {ex}");
				return (500, JsonConvert.SerializeObject((object)new
				{
					error = ex.Message
				}));
			}
		});
	}

	private static void StartAllAccounts(HttpListenerResponse resp)
	{
		if (_accountWindow == null)
		{
			WriteError(resp, 500, "AccountWindow not available");
			return;
		}
		RunOnUi(() =>
		{
			try
			{
				MethodInfo? method = ((object)_accountWindow).GetType().GetMethod("startAllToolStripMenuItem_Click", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				object obj = ((object)_accountWindow).GetType().GetField("startAllToolStripMenuItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(_accountWindow);
				method?.Invoke(_accountWindow, new object[2]
				{
					obj ?? _accountWindow,
					null
				});
				Log("Invoked startAllToolStripMenuItem_Click");
				return true;
			}
			catch (Exception ex)
			{
				Log("Error starting all accounts: " + ex.Message);
				return false;
			}
		});
		WriteJson(resp, 200, "{\"success\":true,\"message\":\"Start-all invoked\"}");
	}

	private static async Task RunLiveUpdatesSpoolerAsync()
	{
		var dirs = new List<string> { Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "live_updates") };
		string? envSpool = Environment.GetEnvironmentVariable("LIVE_UPDATES_DIR");
		if (!string.IsNullOrEmpty(envSpool) && !dirs.Contains(envSpool, StringComparer.OrdinalIgnoreCase))
		{
			dirs.Add(envSpool);
		}

		foreach (var dir in dirs)
		{
			try { Directory.CreateDirectory(dir); } catch { }
		}

		while (true)
		{
			try
			{
				foreach (var spoolDir in dirs)
				{
					if (Directory.Exists(spoolDir))
					{
						string[] files = Directory.GetFiles(spoolDir, "update_*.json");
						foreach (string path in files)
						{
							try
							{
								string text = File.ReadAllText(path);
								if (string.IsNullOrWhiteSpace(text))
								{
									File.Delete(path);
									continue;
								}
								foreach (JProperty item in JObject.Parse(text).Properties())
								{
									string name = item.Name;
									if (long.TryParse(name, out var result))
									{
										string body = item.Value.ToString((Formatting)0);
										HandleSettingsPost(result, body, persist: true);
										Log($"Spooler applied settings for account {result}");
									}
									else
									{
										if (!name.Equals("ALL", StringComparison.OrdinalIgnoreCase))
										{
											continue;
										}
										string body2 = item.Value.ToString((Formatting)0);
										foreach (object item2 in RunOnUi(GetAccountsSnapshot))
										{
											long num = ReadIgg(item2);
											if (num > 0)
											{
												HandleSettingsPost(num, body2, persist: true);
											}
										}
										Log("Spooler applied settings to ALL accounts");
									}
								}
								File.Delete(path);
								Log("Successfully processed and removed spool file: " + Path.GetFileName(path));
							}
							catch (IOException)
							{
							}
							catch (Exception ex2)
							{
								Log("Error reading spool file " + Path.GetFileName(path) + ": " + ex2.Message);
								try
								{
									File.Delete(path);
								}
								catch
								{
								}
							}
						}
					}
				}
			}
			catch (Exception ex3)
			{
				Log("Spooler error: " + ex3.Message);
			}
			await Task.Delay(500).ConfigureAwait(continueOnCapturedContext: false);
		}
	}

	private static async Task RunConfigReloadWatcherAsync()
	{
		var dirs = new List<string> { Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config") };
		string? envConfig = Environment.GetEnvironmentVariable("CONFIG_DIR");
		if (!string.IsNullOrEmpty(envConfig) && !dirs.Contains(envConfig, StringComparer.OrdinalIgnoreCase))
		{
			dirs.Add(envConfig);
		}

		foreach (var dir in dirs)
		{
			try { Directory.CreateDirectory(dir); } catch { }
		}

		while (true)
		{
			try
			{
				foreach (var configDir in dirs)
				{
					if (Directory.Exists(configDir))
					{
						string[] files = Directory.GetFiles(configDir, ".reload_*");
						foreach (string path in files)
						{
							try
							{
								if (long.TryParse(Path.GetFileName(path).Substring(".reload_".Length).Trim(), out var result))
								{
									ReloadAccountFromDisk(result);
									File.Delete(path);
									try
									{
										File.WriteAllText(Path.Combine(configDir, $".success_{result}"), DateTime.UtcNow.ToString("o"));
									}
									catch
									{
									}
									Log($"Applied .reload trigger for {result}");
								}
							}
							catch (Exception ex)
							{
								Log("Error handling reload trigger " + Path.GetFileName(path) + ": " + ex.Message);
								try
								{
									File.Delete(path);
								}
								catch
								{
								}
							}
						}
					}
				}
			}
			catch (Exception ex2)
			{
				Log("Config watcher error: " + ex2.Message);
			}
			await Task.Delay(1000).ConfigureAwait(continueOnCapturedContext: false);
		}
	}

	static WebControlServer()
	{
		//IL_000a: Unknown result type (might be due to invalid IL or missing references)
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0016: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Expected Obj, but got Unknown
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		//IL_0027: Unknown result type (might be due to invalid IL or missing references)
		//IL_002e: Unknown result type (might be due to invalid IL or missing references)
		//IL_003a: Expected Obj, but got Unknown
		SaveLock = new object();
		SafePopulateSettings = new JsonSerializerSettings
		{
			ObjectCreationHandling = (ObjectCreationHandling)1,
			NullValueHandling = (NullValueHandling)1
		};
		ReadSerializeSettings = new JsonSerializerSettings
		{
			Formatting = (Formatting)1,
			ReferenceLoopHandling = (ReferenceLoopHandling)1
		};
		SettingsControlTypeNames = new string[26]
		{
			"LordsMobileBot.generalSettings", "LordsMobileBot.gatherSettings", "LordsMobileBot.huntSettings", "LordsMobileBot.heroSettings", "LordsMobileBot.buildingSettings", "LordsMobileBot.researchSettings", "LordsMobileBot.rallySettings", "LordsMobileBot.guildFestSettings", "LordsMobileBot.exchangeSettings", "LordsMobileBot.armySettings",
			"LordsMobileBot.artifactSettings", "LordsMobileBot.cargoSettings", "LordsMobileBot.equipmentSettings", "LordsMobileBot.eventSettings", "LordsMobileBot.familiarSettings", "LordsMobileBot.guildBankSettings", "LordsMobileBot.manageGuildSettings", "LordsMobileBot.protectionSettings", "LordsMobileBot.realmSettings", "LordsMobileBot.scheduleSettings",
			"LordsMobileBot.spendingSettings", "LordsMobileBot.supplySettings", "LordsMobileBot.talentSettings", "LordsMobileBot.otherSettings", "LordsMobileBot.QuickSettings", "LordsMobileBot.Forms.Bot_Control.Settings.chaosArenaSettings"
		};
	}
}
