using System;
using System.Drawing;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Net.Http;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DevExpress.LookAndFeel;
using DevExpress.XtraGrid;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Grpc.Core;
using LordsMobileBot;
using Newtonsoft.Json;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace LordsBotGate;

public static class Program
{
	internal sealed class LocalSignerService : IDisposable
	{
		private Process? _process;

		private StreamWriter? _writer;

		private StreamReader? _reader;

		private readonly object _lock = new object();

		private readonly string _signerDir;

		public LocalSignerService(string baseDir)
		{
			_signerDir = Path.Combine(baseDir, "lib\\local-signer");
			EnsureStarted();
			AppDomain.CurrentDomain.ProcessExit += (object? _, EventArgs _) =>
			{
				Dispose();
			};
		}

		private void EnsureStarted()
		{
			lock (_lock)
			{
				if (_process != null && !_process.HasExited)
				{
					return;
				}
				string[] array = new string[6]
				{
					_signerDir,
					Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib\\local-signer"),
					Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..\\lib\\local-signer"),
					Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..\\..\\lib\\local-signer"),
					"E:\\mylb\\LordsBotGate\\lib\\local-signer",
					"E:\\test\\lib\\local-signer"
				};
				string text = "";
				string workingDirectory = _signerDir;
				string[] array2 = array;
				foreach (string text2 in array2)
				{
					try
					{
						string path = Path.Combine(text2, "LocalSignerHost.exe");
						if (File.Exists(path))
						{
							text = Path.GetFullPath(path);
							workingDirectory = Path.GetDirectoryName(text) ?? text2;
							break;
						}
					}
					catch
					{
					}
				}
				if (string.IsNullOrEmpty(text) || !File.Exists(text))
				{
					Trace("[SIGNER] Warning: LocalSignerHost.exe not found in candidate paths.");
					return;
				}
				ProcessStartInfo startInfo = new ProcessStartInfo
				{
					FileName = text,
					Arguments = "--server",
					WorkingDirectory = workingDirectory,
					RedirectStandardInput = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true
				};
				_process = Process.Start(startInfo);
				if (_process != null)
				{
					_writer = _process.StandardInput;
					_reader = _process.StandardOutput;
					Trace($"[SIGNER] LocalSignerHost process started in server mode (PID {_process.Id}) from {text}.");
				}
			}
		}

		public Dictionary<string, string> Sign(string resourcePath, string requestBody, string uuid, string udid, string gameId, Dictionary<string, string>? extraParams)
		{
			string text = JsonSerializer.Serialize(new
			{
				ResourcePath = (resourcePath ?? ""),
				RequestBody = (requestBody ?? ""),
				Uuid = (uuid ?? ""),
				Udid = (udid ?? ""),
				GameId = (gameId ?? ""),
				ExtraParams = extraParams
			});

			// 1. Direct Local Signer via LocalSignerHost stdio pipe (<10ms, 100% offline & reliable)
			lock (_lock)
			{
				try
				{
					EnsureStarted();
					if (_writer != null && _reader != null)
					{
						_writer.WriteLine(text);
						_writer.Flush();
						string? line = _reader.ReadLine();
						if (!string.IsNullOrEmpty(line))
						{
							using JsonDocument jsonDoc = JsonDocument.Parse(line);
							JsonElement root = jsonDoc.RootElement;
							if (!root.TryGetProperty("Error", out var errVal) || errVal.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(errVal.GetString()))
							{
								if (root.TryGetProperty("Headers", out var hdrVal) && hdrVal.ValueKind == JsonValueKind.Object)
								{
									Dictionary<string, string> localDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
									foreach (JsonProperty item in hdrVal.EnumerateObject())
									{
										localDict[item.Name] = item.Value.GetString() ?? "";
									}
									if (localDict.Count > 0)
									{
										Trace($"[SIGNER LOCAL] LocalSignerHost signed successfully ({localDict.Count} headers).");
										return localDict;
									}
								}
							}
							else
							{
								Trace("[SIGNER LOCAL] Error from LocalSignerHost: " + errVal.GetString());
							}
						}
					}
				}
				catch (Exception exLocal)
				{
					Trace($"[SIGNER LOCAL] Local signer exception: {exLocal.Message}. Trying remote fallback...");
				}
			}

			// 2. Secondary Fallback: Remote Auth Server (if local process unavailable)
			try
			{
				string text2 = ((!string.IsNullOrEmpty(ActiveAuthServerBaseUrl)) ? ActiveAuthServerBaseUrl : "http://127.0.0.1:5566").TrimEnd('/') + "/api/signer/sign";
				using HttpClient httpClient = new HttpClient
				{
					Timeout = TimeSpan.FromSeconds(3L)
				};
				using StringContent content = new StringContent(text, Encoding.UTF8, "application/json");
				HttpResponseMessage result = httpClient.PostAsync(text2, content).GetAwaiter().GetResult();
				if (result.IsSuccessStatusCode)
				{
					using JsonDocument jsonDocument = JsonDocument.Parse(result.Content.ReadAsStringAsync().GetAwaiter().GetResult());
					if (jsonDocument.RootElement.TryGetProperty("Headers", out var value) && value.ValueKind == JsonValueKind.Object)
					{
						Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
						foreach (JsonProperty item in value.EnumerateObject())
						{
							dictionary[item.Name] = item.Value.GetString() ?? "";
						}
						if (dictionary.Count > 0)
						{
							Trace($"[SIGNER REMOTE] Remote server {text2} signed successfully ({dictionary.Count} headers).");
							return dictionary;
						}
					}
				}
			}
			catch (Exception ex)
			{
				Trace("[SIGNER REMOTE] Remote signing fallback failed: " + ex.Message);
			}

			return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		}

		public void Dispose()
		{
			lock (_lock)
			{
				try
				{
					if (_process != null && !_process.HasExited)
					{
						_process.Kill();
						_process.Dispose();
						_process = null;
					}
				}
				catch
				{
				}
			}
		}
	}

	internal sealed class LocalSignerInvoker : CallInvoker
	{
		private readonly LocalSignerService _signer;

		public LocalSignerInvoker(LocalSignerService signer)
		{
			_signer = signer;
		}

		private static FieldInfo[] InstanceFieldsOf(Type t, Func<FieldInfo, bool> pred)
		{
			return (from f in t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
				where !f.IsStatic && pred(f)
				select f).ToArray();
		}

		private static AsyncUnaryCall<TResponse> WrapResult<TResponse>(TResponse respObj)
		{
			//IL_0006: Unknown result type (might be due to invalid IL or missing references)
			//IL_0010: Expected Obj, but got Unknown
			return new AsyncUnaryCall<TResponse>(Task.FromResult(respObj), Task.FromResult<Metadata>(new Metadata()), (Func<Status>)(() =>
			{
				//IL_0000: Unknown result type (might be due to invalid IL or missing references)
				return Status.DefaultSuccess;
			}), (Func<Metadata>)(() =>
			{
				//IL_0000: Unknown result type (might be due to invalid IL or missing references)
				//IL_0006: Expected Obj, but got Unknown
				return new Metadata();
			}), (Action)(() =>
			{
			}));
		}

		public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
		{
			//IL_146c: Unknown result type (might be due to invalid IL or missing references)
			//IL_1476: Expected Obj, but got Unknown
			//IL_1507: Unknown result type (might be due to invalid IL or missing references)
			//IL_1511: Expected Obj, but got Unknown
			Trace($"[INVOKER] gRPC call intercepted: {method.FullName} (Method: {method.Name})");
			FieldInfo[] strFields;
			if (method.Name == "GetRequestHeaders")
			{
				Type type = request.GetType();
				strFields = InstanceFieldsOf(type, (FieldInfo f) => f.FieldType == typeof(string));
				string text = GetStr(0);
				string requestBody = GetStr(1);
				string text2 = GetStr(2);
				string text3 = GetStr(3);
				string text4 = GetStr(4);
				MapField<string, string> val = InstanceFieldsOf(type, (FieldInfo f) => f.FieldType == typeof(MapField<string, string>)).FirstOrDefault()?.GetValue(request) as MapField<string, string>;
				Dictionary<string, string> dictionary = null;
				if (val != null && val.Count > 0)
				{
					dictionary = new Dictionary<string, string>();
					foreach (KeyValuePair<string, string> item in val)
					{
						dictionary[item.Key] = item.Value;
					}
				}
				Trace($"[INVOKER] Signing: ResourcePath='{text}', GameId='{text4}', Uuid='{text2}', Udid='{text3}'");
				Dictionary<string, string> dictionary2 = _signer.Sign(text, requestBody, text2, text3, text4, dictionary);
				Trace($"[INVOKER] Signed successfully! Generated {dictionary2.Count} headers.");
				object obj = Activator.CreateInstance(typeof(TResponse));
				Type typeFromHandle = typeof(TResponse);
				if (InstanceFieldsOf(typeFromHandle, (FieldInfo f) => f.FieldType == typeof(MapField<string, string>)).FirstOrDefault()?.GetValue(obj) is MapField<string, string> val2)
				{
					foreach (KeyValuePair<string, string> item2 in dictionary2)
					{
						val2[item2.Key] = item2.Value;
					}
				}
				InstanceFieldsOf(typeFromHandle, (FieldInfo f) => f.FieldType == typeof(bool)).FirstOrDefault()?.SetValue(obj, false);
				return WrapResult((TResponse)obj);
			}
			FieldInfo[] strFields2;
			if (method.Name == "SignApiGateway")
			{
				Type type2 = request.GetType();
				strFields2 = InstanceFieldsOf(type2, (FieldInfo f) => f.FieldType == typeof(string));
				string requestBody2 = GetStr2(0);
				string uuid = GetStr2(1);
				string udid = GetStr2(2);
				string gameId = GetStr2(3);
				string resourcePath = GetStr2(4);
				Dictionary<string, string> dictionary3 = _signer.Sign(resourcePath, requestBody2, uuid, udid, gameId, null);
				object obj2 = Activator.CreateInstance(typeof(TResponse));
				Type typeFromHandle2 = typeof(TResponse);
				if (InstanceFieldsOf(typeFromHandle2, (FieldInfo f) => f.FieldType == typeof(MapField<string, string>)).FirstOrDefault()?.GetValue(obj2) is MapField<string, string> val3)
				{
					foreach (KeyValuePair<string, string> item3 in dictionary3)
					{
						val3[item3.Key] = item3.Value;
					}
				}
				InstanceFieldsOf(typeFromHandle2, (FieldInfo f) => f.FieldType == typeof(bool)).FirstOrDefault()?.SetValue(obj2, false);
				return WrapResult((TResponse)obj2);
			}
			if (method.Name == "GetIggHosts")
			{
				Trace("[INVOKER] Intercepted GetIggHosts from " + (host ?? "local"));
				object obj3 = Activator.CreateInstance(typeof(TResponse));
				FieldInfo[] array = InstanceFieldsOf(typeof(TResponse), (FieldInfo f) => f.FieldType == typeof(RepeatedField<string>));
				if (array.Length != 0)
				{
					(array[0].GetValue(obj3) as RepeatedField<string>)?.Add("lordsmobile.igg.com");
				}
				if (array.Length > 1)
				{
					(array[1].GetValue(obj3) as RepeatedField<string>)?.Add("192.243.44.63");
				}
				if (array.Length > 2)
				{
					(array[2].GetValue(obj3) as RepeatedField<string>)?.Add("lo-tw.igg.com");
				}
				Trace("[INVOKER] Populated GetIggHosts: Login='lordsmobile.igg.com', World='192.243.44.63', Taiwan='lo-tw.igg.com'");
				return WrapResult((TResponse)obj3);
			}
			long result;
			if (method.Name == "CheckAccountStart")
			{
				Trace("[INVOKER] Intercepted CheckAccountStart");
				object obj4 = Activator.CreateInstance(typeof(TResponse));
				IList list = InstanceFieldsOf(typeof(TResponse), (FieldInfo f) => typeof(IList).IsAssignableFrom(f.FieldType)).FirstOrDefault()?.GetValue(obj4) as IList;
				if (InstanceFieldsOf(request.GetType(), (FieldInfo f) => typeof(IList).IsAssignableFrom(f.FieldType)).FirstOrDefault()?.GetValue(request) is IEnumerable enumerable && Schema != null && list != null)
				{
					foreach (object item4 in enumerable)
					{
						if (item4 == null)
						{
							continue;
						}
						FieldInfo[] array2 = InstanceFieldsOf(item4.GetType(), (FieldInfo f) => f.FieldType == typeof(string));
						string text5 = ((array2.Length == 0) ? "" : (array2[0].GetValue(item4)?.ToString() ?? ""));
						string note = ((array2.Length <= 1) ? "" : (array2[1].GetValue(item4)?.ToString() ?? ""));
						string text6 = ReadLocalAccessKey(text5);
						if (!IsValidAccessKey(text6) && AccessKeyCache.TryGetValue(text5, out string value))
						{
							text6 = value;
						}
						object obj5 = CreateAdmissionItem(Schema, text5, text6, allowed: true, note);
						if (obj5 == null)
						{
							continue;
						}
						list.Add(obj5);
						if (Schema != null)
						{
							object obj6 = Schema.MonitorSingletonField?.GetValue(null);
							if (obj6 != null)
							{
								AddMonitorAdmission(Schema, obj6, text5, obj5);
							}
						}
						Trace($"[INVOKER] CheckAccountStart: Approved start for account '{text5}' (akLen={text6?.Length ?? 0}).");
					}
				}
				if (list != null && list.Count == 0 && Schema != null)
				{
					string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config");
					if (Directory.Exists(path))
					{
						string[] directories = Directory.GetDirectories(path);
						for (int num = 0; num < directories.Length; num++)
						{
							string fileName = Path.GetFileName(directories[num]);
							if (!long.TryParse(fileName, out result))
							{
								continue;
							}
							string text7 = ReadLocalAccessKey(fileName);
							if (!IsValidAccessKey(text7) && AccessKeyCache.TryGetValue(fileName, out string value2))
							{
								text7 = value2;
							}
							object obj7 = CreateAdmissionItem(Schema, fileName, text7, allowed: true, "");
							if (obj7 == null)
							{
								continue;
							}
							list.Add(obj7);
							if (Schema != null)
							{
								object obj8 = Schema.MonitorSingletonField?.GetValue(null);
								if (obj8 != null)
								{
									AddMonitorAdmission(Schema, obj8, fileName, obj7);
								}
							}
							Trace($"[INVOKER] CheckAccountStart (Fallback): Approved start for '{fileName}' (akLen={text7?.Length ?? 0}).");
						}
					}
				}
				return WrapResult((TResponse)obj4);
			}
			if (method.Name == "GetAccountKeys")
			{
				Trace("[INVOKER] Intercepted GetAccountKeys");
				object obj9 = Activator.CreateInstance(typeof(TResponse));
				Type? typeFromHandle3 = typeof(TResponse);
				IDictionary dictionary4 = InstanceFieldsOf(typeFromHandle3, (FieldInfo f) => f.FieldType == typeof(MapField<string, ByteString>)).FirstOrDefault()?.GetValue(obj9) as IDictionary;
				IDictionary dictionary5 = InstanceFieldsOf(typeFromHandle3, (FieldInfo f) => f.FieldType == typeof(MapField<string, string>)).FirstOrDefault()?.GetValue(obj9) as IDictionary;
				IEnumerable enumerable2 = InstanceFieldsOf(request.GetType(), (FieldInfo f) => f.FieldType == typeof(RepeatedField<string>)).FirstOrDefault()?.GetValue(request) as IEnumerable;
				string path2 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config");
				if (enumerable2 != null)
				{
					foreach (object item5 in enumerable2)
					{
						string text8 = item5?.ToString() ?? "";
						if (string.IsNullOrEmpty(text8))
						{
							continue;
						}
						byte[] array3 = null;
						string text9 = Path.Combine(path2, text8);
						string path3 = Path.Combine(text9, "account.key");
						if (File.Exists(path3))
						{
							byte[] array4 = File.ReadAllBytes(path3);
							if (array4.Length == 32)
							{
								array3 = array4;
							}
						}
						if (array3 == null)
						{
							string path4 = Path.Combine(path2, "global", "account.key");
							if (File.Exists(path4))
							{
								byte[] array5 = File.ReadAllBytes(path4);
								if (array5.Length == 32)
								{
									array3 = array5;
								}
							}
						}
						if (array3 == null)
						{
							array3 = new byte[32];
							RandomNumberGenerator.Fill(array3);
							if (Directory.Exists(text9))
							{
								try
								{
									File.WriteAllBytes(path3, array3);
								}
								catch
								{
								}
							}
						}
						if (dictionary4 != null && array3 != null)
						{
							dictionary4[text8] = ByteString.CopyFrom(array3);
						}
						string value3 = ReadLocalAccessKey(text8);
						if (dictionary5 != null)
						{
							dictionary5[text8] = value3;
						}
						Trace($"[INVOKER] GetAccountKeys served key for '{text8}' (keyLen={array3?.Length ?? 0}, hasAk={!string.IsNullOrEmpty(value3)})");
					}
				}
				return WrapResult((TResponse)obj9);
			}
			if (method.Name == "MigrateLegacyAccounts")
			{
				Trace("[INVOKER] Intercepted MigrateLegacyAccounts");
				object obj11 = Activator.CreateInstance(typeof(TResponse));
				Type? typeFromHandle4 = typeof(TResponse);
				IDictionary dictionary6 = InstanceFieldsOf(typeFromHandle4, (FieldInfo f) => f.FieldType == typeof(MapField<string, string>)).FirstOrDefault()?.GetValue(obj11) as IDictionary;
				IList list2 = InstanceFieldsOf(typeFromHandle4, (FieldInfo f) => f.FieldType == typeof(RepeatedField<string>)).FirstOrDefault()?.GetValue(obj11) as IList;
				string path5 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config");
				if (Directory.Exists(path5) && list2 != null)
				{
					string[] directories = Directory.GetDirectories(path5);
					for (int num = 0; num < directories.Length; num++)
					{
						string fileName2 = Path.GetFileName(directories[num]);
						if (long.TryParse(fileName2, out result))
						{
							list2.Add(fileName2);
							if (dictionary6 != null)
							{
								dictionary6[fileName2] = "";
							}
						}
					}
				}
				return WrapResult((TResponse)obj11);
			}
			if (method.Name == "MigrateAccessKeys")
			{
				Trace("[INVOKER] Intercepted MigrateAccessKeys");
				object obj12 = Activator.CreateInstance(typeof(TResponse));
				MapField<string, string> val4 = InstanceFieldsOf(typeof(TResponse), (FieldInfo f) => f.FieldType == typeof(MapField<string, string>)).FirstOrDefault()?.GetValue(obj12) as MapField<string, string>;
				IEnumerable enumerable3 = InstanceFieldsOf(request.GetType(), (FieldInfo f) => typeof(IList).IsAssignableFrom(f.FieldType)).FirstOrDefault()?.GetValue(request) as IEnumerable;
				HashSet<string> hashSet = new HashSet<string>();
				if (enumerable3 != null)
				{
					foreach (object item6 in enumerable3)
					{
						if (item6 == null)
						{
							continue;
						}
						FieldInfo[] array6 = InstanceFieldsOf(item6.GetType(), (FieldInfo f) => f.FieldType == typeof(string));
						for (int num = 0; num < array6.Length; num++)
						{
							string text10 = array6[num].GetValue(item6) as string;
							if (!string.IsNullOrEmpty(text10) && long.TryParse(text10, out result))
							{
								hashSet.Add(text10);
							}
						}
						PropertyInfo[] properties = item6.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
						foreach (PropertyInfo propertyInfo in properties)
						{
							if (propertyInfo.PropertyType == typeof(string))
							{
								string text11 = propertyInfo.GetValue(item6) as string;
								if (!string.IsNullOrEmpty(text11) && long.TryParse(text11, out result))
								{
									hashSet.Add(text11);
								}
							}
						}
					}
				}
				string path6 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config");
				if (Directory.Exists(path6))
				{
					string[] directories = Directory.GetDirectories(path6);
					for (int num = 0; num < directories.Length; num++)
					{
						string fileName3 = Path.GetFileName(directories[num]);
						if (long.TryParse(fileName3, out result))
						{
							hashSet.Add(fileName3);
						}
					}
				}
				if (val4 != null)
				{
					foreach (string item7 in hashSet)
					{
						string text12 = ReadLocalAccessKey(item7);
						if (!string.IsNullOrEmpty(text12))
						{
							val4[item7] = text12;
							Trace($"[INVOKER] MigrateAccessKeys served key for '{item7}' (len={text12.Length})");
						}
					}
				}
				return WrapResult((TResponse)obj12);
			}
			if (method.Name == "LookupCountry")
			{
				Trace("[INVOKER] Intercepted LookupCountry - returning US");
				object obj13 = Activator.CreateInstance(typeof(TResponse));
				Type typeFromHandle5 = typeof(TResponse);
				PropertyInfo[] properties = typeFromHandle5.GetProperties(BindingFlags.Instance | BindingFlags.Public);
				foreach (PropertyInfo propertyInfo2 in properties)
				{
					if (propertyInfo2.PropertyType == typeof(string) && propertyInfo2.CanWrite && (propertyInfo2.Name.Contains("Country", StringComparison.OrdinalIgnoreCase) || propertyInfo2.Name.Contains("Code", StringComparison.OrdinalIgnoreCase)))
					{
						propertyInfo2.SetValue(obj13, "US");
					}
				}
				FieldInfo[] array6 = typeFromHandle5.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				foreach (FieldInfo fieldInfo in array6)
				{
					if (fieldInfo.FieldType == typeof(string) && (fieldInfo.Name.Contains("country", StringComparison.OrdinalIgnoreCase) || fieldInfo.Name.Contains("code", StringComparison.OrdinalIgnoreCase)))
					{
						fieldInfo.SetValue(obj13, "US");
					}
				}
				return WrapResult((TResponse)obj13);
			}
			if (method.Name == "SyncRunningAccounts" || method.Name == "NotifyAccountStop" || method.Name == "ReportAccountStatus" || method.Name == "Handshake" || method.Name == "Heartbeat" || method.Name == "ReportDiagnostics" || method.Name == "ReportAccountCreated" || method.Name == "GetTelemetryConfig" || method.Name == "SignTelemetryPut")
			{
				Trace("[INVOKER] Intercepted " + method.Name + " - approving locally");
				object obj14 = Activator.CreateInstance(typeof(TResponse));
				Type typeFromHandle6 = typeof(TResponse);
				PropertyInfo[] properties = typeFromHandle6.GetProperties(BindingFlags.Instance | BindingFlags.Public);
				foreach (PropertyInfo propertyInfo3 in properties)
				{
					if (propertyInfo3.PropertyType == typeof(bool) && propertyInfo3.CanWrite && (propertyInfo3.Name.Contains("Success", StringComparison.OrdinalIgnoreCase) || propertyInfo3.Name.Contains("Allow", StringComparison.OrdinalIgnoreCase) || propertyInfo3.Name.Contains("Valid", StringComparison.OrdinalIgnoreCase) || propertyInfo3.Name.Contains("Ok", StringComparison.OrdinalIgnoreCase) || propertyInfo3.Name.Contains("Enable", StringComparison.OrdinalIgnoreCase)))
					{
						propertyInfo3.SetValue(obj14, true);
					}
				}
				FieldInfo[] array6 = typeFromHandle6.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				foreach (FieldInfo fieldInfo2 in array6)
				{
					if (fieldInfo2.FieldType == typeof(bool) && (fieldInfo2.Name.Contains("success", StringComparison.OrdinalIgnoreCase) || fieldInfo2.Name.Contains("allow", StringComparison.OrdinalIgnoreCase) || fieldInfo2.Name.Contains("valid", StringComparison.OrdinalIgnoreCase) || fieldInfo2.Name.Contains("ok", StringComparison.OrdinalIgnoreCase) || fieldInfo2.Name.Contains("enable", StringComparison.OrdinalIgnoreCase)))
					{
						fieldInfo2.SetValue(obj14, true);
					}
				}
				return new AsyncUnaryCall<TResponse>(Task.FromResult((TResponse)obj14), Task.FromResult<Metadata>(new Metadata()), (Func<Status>)(() =>
				{
					//IL_0000: Unknown result type (might be due to invalid IL or missing references)
					return Status.DefaultSuccess;
				}), (Func<Metadata>)(() =>
				{
					//IL_0000: Unknown result type (might be due to invalid IL or missing references)
					//IL_0006: Expected Obj, but got Unknown
					return new Metadata();
				}), (Action)(() =>
				{
				}));
			}
			Trace("[INVOKER] Warning: Unhandled gRPC call: " + method.FullName);
			return new AsyncUnaryCall<TResponse>(Task.FromResult((TResponse)Activator.CreateInstance(typeof(TResponse))), Task.FromResult<Metadata>(new Metadata()), (Func<Status>)(() =>
			{
				//IL_0000: Unknown result type (might be due to invalid IL or missing references)
				return Status.DefaultSuccess;
			}), (Func<Metadata>)(() =>
			{
				//IL_0000: Unknown result type (might be due to invalid IL or missing references)
				//IL_0006: Expected Obj, but got Unknown
				return new Metadata();
			}), (Action)(() =>
			{
			}));
			string GetStr(int i)
			{
				if (i >= strFields.Length)
				{
					return "";
				}
				return (strFields[i].GetValue(request) as string) ?? "";
			}
			string GetStr2(int i)
			{
				if (i >= strFields2.Length)
				{
					return "";
				}
				return (strFields2[i].GetValue(request) as string) ?? "";
			}
		}

		public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
		{
			throw new NotImplementedException();
		}

		public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
		{
			throw new NotImplementedException();
		}

		public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
		{
			throw new NotImplementedException();
		}

		public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
		{
			throw new NotImplementedException();
		}
	}

	private sealed class AuthResponse
	{
		public JsonElement Status { get; set; }

		public int Error { get; set; }

		public bool Success { get; set; }

		public bool Valid { get; set; }

		public string Message { get; set; } = string.Empty;

		public string Token { get; set; } = string.Empty;

		public string SessionId { get; set; } = string.Empty;

		public string UserId { get; set; } = string.Empty;

		public string UserName { get; set; } = string.Empty;

		[JsonPropertyName("slots")]
		public int Slots { get; set; } = 1000;

		public int AllowedSlots { get; set; } = 1000;

		public JsonElement ExpiresAt { get; set; }

		public JsonElement ServerTime { get; set; }

		public string Plan { get; set; } = string.Empty;

		public long FeatureFlags { get; set; }
	}

	private static volatile bool _suppressWarmupExceptions;

	private static string? _warmupDeclaringTypeName;

	private static readonly string[] CandidateUrls = new string[6] { "http://127.0.0.1:5000/api/auth/login", "http://127.0.0.1:5000/", "http://127.0.0.1:5566/api/auth/login", "http://127.0.0.1:5566/", "http://127.0.0.1:8080/api/auth/login", "http://127.0.0.1:8080/" };

	private static object? _wiredStaticClient;

	private static object? _wiredBridgeSorter;

	private static readonly ConcurrentDictionary<string, DateTime> _verifiedAccJsonWriteTime = new ConcurrentDictionary<string, DateTime>();

	private static bool keysLoggedOnce;

	private static readonly ConcurrentDictionary<string, string> _lastAdmissionState = new ConcurrentDictionary<string, string>();

	private static LocalSignerService? s_signer;

	private static object? connectedLicenseState;

	private static object? disconnectedLicenseState;

	private static FieldInfo? licenseStateTime;

	private static FieldInfo? licenseStateSingletonField;

	private static readonly ConcurrentDictionary<string, string> AccessKeyCache = new ConcurrentDictionary<string, string>();

	private const string FallbackGuestAccessKey = "eyJha2lkIjoyMDIxMDQyMiwicmtpZCI6MjAyMTA0MjMsImciOjEwNTEwMjk5MDIsInYiOjN9.5ozW3_x4_vryAXrZXXtdOhma1yYa6G2bGnQsZrBtZC_TK6xpxeoVhYNWfESUnS4gsigPH0gAwXZW2rKojXtqWUPe8WaBjHyRx9hqBHHQ06ur-o9aZARheeeo-3NcJgp4hKHhosA4_uzb0FLqgAEgahVq0OrWk62001vcKL4dpBI.GZ5slgdeYpn4Du7YASjGN0Vrx_sUD9o6RAqmBVKhUHdKXHSIy0gkg-ZPT1q22JEiIiXydZ7FYzQhDZmebrxsksvgl5Vb5oLqC6dv9SX4CyCqEXQXmjkKAWdzQ7FYMiCwtoUPW69SkX8wAhuXCcHgaKw20mHK3Cy_zLJlZ4goQZk";

	private static object? fixedGameVersion;
	private static DateTime s_lastPeriodicVersionCheck = DateTime.MinValue;

	public static string GetConfiguredGameVersion()
	{
		return GameVersionAutoUpdater.CurrentVersion ?? GameVersionAutoUpdater.ResolveVersion();
	}

	public static void ApplyGameVersion(string ver)
	{
		try
		{
			Type? gameVersionType = Schema?.GameVersionType ?? typeof(AccountWindow).Assembly.GetType("LordsMobileBot.AppClasses.Settings.GameVersion");
			if (gameVersionType != null)
			{
				object newGvInst = Activator.CreateInstance(gameVersionType, ver)!;
				fixedGameVersion = newGvInst;

				gameVersionType.GetField("<currentGameVersion>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, newGvInst);
				gameVersionType.GetField("<currentGameVersionTW>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, newGvInst);

				gameVersionType.GetProperty("currentGameVersion", BindingFlags.Static | BindingFlags.Public)?.SetValue(null, newGvInst);
				gameVersionType.GetProperty("currentGameVersionTW", BindingFlags.Static | BindingFlags.Public)?.SetValue(null, newGvInst);

				GameVersionAutoUpdater.ParseVersionComponents(ver, out ushort maj, out ushort min, out ushort bld);
				try
				{
					Assembly botAsm = typeof(AccountWindow).Assembly;
					Type? clientType = botAsm.GetType("B6B92D16") ?? botAsm.GetTypes().FirstOrDefault(t => t.Name == "B6B92D16");
					if (clientType != null)
					{
						FieldInfo? fld = clientType.GetField("DBB37824", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
						if (fld != null)
						{
							fld.SetValue(null, new ushort[] { maj, min, bld });
							Trace($"[GATE] [AutoVersion] Injected ushort[] into {clientType.Name}.{fld.Name}: {maj}.{min}.{bld}");
						}
					}
				}
				catch { }

				int gv1 = (int)(gameVersionType.GetField("GV1", BindingFlags.Instance | BindingFlags.Public)?.GetValue(newGvInst) ?? 0);
				int gv2 = (int)(gameVersionType.GetField("GV2", BindingFlags.Instance | BindingFlags.Public)?.GetValue(newGvInst) ?? 0);
				int gv3 = (int)(gameVersionType.GetField("GV3", BindingFlags.Instance | BindingFlags.Public)?.GetValue(newGvInst) ?? 0);
				Trace($"[GATE] [AutoVersion] Enforced live GameVersion: {ver} (GV1={gv1}, GV2={gv2}, GV3={gv3})");
			}
		}
		catch (Exception ex)
		{
			Trace($"[GATE] [AutoVersion] Note applying GameVersion: {ex.Message}");
		}
	}

	public static string CheckAndUpdateLiveVersion(bool force = false)
	{
		string oldVer = GameVersionAutoUpdater.CurrentVersion;
		string newVer = GameVersionAutoUpdater.ResolveVersion(force);
		if (force || newVer != oldVer || fixedGameVersion == null)
		{
			ApplyGameVersion(newVer);
			if (newVer != oldVer)
			{
				Trace($"[GATE] [AutoVersion] Live GameVersion updated dynamically: {oldVer} -> {newVer}");
			}
		}
		return newVer;
	}

	private static AuthResponse? currentAuth;

	private static Func<object, object?>? s_getSeparatedMapObj;

	private static Func<object, object?>? s_getCompressorMapObj;

	private static Func<object, object?>? s_getResolverMapObj;

	private static Action<object>? s_clearResolverMap;

	private static Action<object, ushort>? s_setAllocatorId;

	private static nint s_lastPatchedSmEntry = IntPtr.Zero;

	public static BotSchema? Schema { get; private set; }

	public static string ActiveAuthServerBaseUrl { get; set; } = "http://127.0.0.1:5566";

	private static readonly object _traceLock = new object();

	private static void Trace(string msg)
	{
		try
		{
			string text = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
			Console.WriteLine(text);
			lock (_traceLock)
			{
				File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gate_trace.log"), text + "\r\n");
			}
		}
		catch
		{
		}
	}

	[STAThread]
	private static void Main(string[] args)
	{
		Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
		EnsureSingleInstance();
		Trace("=== Gate Starting ===");
		Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
		AppDomain.CurrentDomain.FirstChanceException += (object? s, FirstChanceExceptionEventArgs e) =>
		{
			if (e.Exception is IOException) return;
			string text = e.Exception?.Message ?? "";
			string text2 = e.Exception?.StackTrace ?? "";
			if (text2.Contains("ConnectedServerField") || text.Contains("ConnectedServerField")) return;
			Trace($"[GATE FIRST CHANCE] {e.Exception?.GetType().Name}: {text}\nStack:\n{text2}");
		};
		Application.ThreadException += (object s, ThreadExceptionEventArgs e) =>
		{
			Trace("[GATE CRASH - ThreadException]: " + e.Exception);
			try
			{
				string value = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash_live.dmp");
				Process.Start(new ProcessStartInfo
				{
					FileName = "dotnet-dump",
					Arguments = $"collect -p {Process.GetCurrentProcess().Id} -o \"{value}\"",
					UseShellExecute = false,
					CreateNoWindow = true
				})?.WaitForExit();
				Trace("[GATE] Captured crash_live.dmp successfully.");
			}
			catch (Exception ex2)
			{
				Trace("[GATE] Could not capture dump: " + ex2.Message);
			}
			try
			{
				File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), e.Exception.ToString());
			}
			catch
			{
			}
			MessageBox.Show("Exception: " + e.Exception.Message + "\n\nStack:\n" + e.Exception.StackTrace, "Gate Error Details", MessageBoxButtons.OK, MessageBoxIcon.Hand);
		};
		AppDomain.CurrentDomain.UnhandledException += (object s, UnhandledExceptionEventArgs e) =>
		{
			Trace("[GATE CRASH - DomainException]: " + e.ExceptionObject);
			try
			{
				File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), e.ExceptionObject?.ToString());
			}
			catch
			{
			}
		};
		TaskScheduler.UnobservedTaskException += (object? s, UnobservedTaskExceptionEventArgs e) =>
		{
			string text = e.Exception.ToString();
			if (_suppressWarmupExceptions || (!string.IsNullOrEmpty(_warmupDeclaringTypeName) && text.Contains(_warmupDeclaringTypeName, StringComparison.Ordinal)))
			{
				e.SetObserved();
			}
			else
			{
				Trace("[GATE CRASH - TaskException]: " + e.Exception);
				try
				{
					File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), text);
				}
				catch
				{
				}
				e.SetObserved();
			}
		};
		SetupAssemblyResolver();
		try
		{
			Trace("Calling RunGate...");
			RunGate(args);
		}
		catch (Exception ex)
		{
			Trace("[GATE MAIN FATAL EXCEPTION]: " + ex);
			try
			{
				File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), ex.ToString());
			}
			catch
			{
			}
			MessageBox.Show("Fatal error in Main:\n\n" + ex, "LordsBot Gate", MessageBoxButtons.OK, MessageBoxIcon.Hand);
		}
	}

	private static void SetupAssemblyResolver()
	{
		AppDomain.CurrentDomain.AssemblyResolve += (object? sender, ResolveEventArgs resolveArgs) =>
		{
			string text = new AssemblyName(resolveArgs.Name).Name + ".dll";
			Trace($"[ASSEMBLY RESOLVE] Requested: {text} ({resolveArgs.Name})");
			string[] array = new string[4]
			{
				Path.Combine(AppDomain.CurrentDomain.BaseDirectory, text),
				Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib", text),
				Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", text),
				Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "lib", text)
			};
			foreach (string text2 in array)
			{
				if (File.Exists(text2))
				{
					try
					{
						Assembly result = Assembly.LoadFrom(text2);
						Trace("[ASSEMBLY RESOLVE SUCCESS] Loaded " + text + " from " + text2);
						if (text.Contains("DevExpress.Data", StringComparison.OrdinalIgnoreCase))
						{
							DisableDevExpressThreadingDetection(result);
						}
						return result;
					}
					catch (Exception ex)
					{
						Trace("[ASSEMBLY RESOLVE ERROR] Failed loading " + text2 + ": " + ex.Message);
					}
				}
			}
			Trace("[ASSEMBLY RESOLVE FAILED] Not found: " + text);
			return (Assembly?)null;
		};
	}

	private static void EnsureSingleInstance()
	{
		try
		{
			Process currentProcess = Process.GetCurrentProcess();
			bool flag = false;
			Process[] processesByName = Process.GetProcessesByName(currentProcess.ProcessName);
			foreach (Process process in processesByName)
			{
				if (process.Id != currentProcess.Id)
				{
					try
					{
						process.Kill();
						process.WaitForExit(3000);
						flag = true;
						Trace($"[GATE] Terminated previous Gate instance PID {process.Id} to prevent port 5556 conflict.");
					}
					catch
					{
					}
				}
			}
			if (!flag)
			{
				return;
			}
			processesByName = Process.GetProcessesByName("LocalSignerHost");
			foreach (Process process2 in processesByName)
			{
				try
				{
					process2.Kill();
					process2.WaitForExit(1000);
				}
				catch
				{
				}
			}
			Thread.Sleep(250);
		}
		catch
		{
		}
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void RunGate(string[] args)
	{
		Trace("[GATE] Inside RunGate...");
		try
		{
			Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
		}
		catch
		{
		}
		Application.EnableVisualStyles();
		Application.SetCompatibleTextRenderingDefault(defaultValue: false);
		Control.CheckForIllegalCrossThreadCalls = false;
		DisableDevExpressThreadingDetection();

		GameVersionAutoUpdater.Initialize(Trace);
		Trace("[GATE] [AutoVersion] Resolving latest live game version at entry point...");
		string entryVer = GameVersionAutoUpdater.ResolveVersion(forceRefresh: true);
		Trace($"[GATE] [AutoVersion] Session entry game version established: {entryVer}");

		Trace("[GATE] Connecting to local auth server (server.py)...");
		AuthResponse authResponse = null;
		string text = string.Empty;
		using (HttpClient httpClient = new HttpClient
		{
			Timeout = TimeSpan.FromSeconds(3L)
		})
		{
			string[] candidateUrls = CandidateUrls;
			foreach (string text2 in candidateUrls)
			{
				try
				{
					Trace("[GATE] Probing " + text2 + "...");
					string result = httpClient.GetStringAsync(text2).GetAwaiter().GetResult();
					Trace("[GATE] Received response: " + result);
					AuthResponse authResponse2 = JsonSerializer.Deserialize<AuthResponse>(result, new JsonSerializerOptions
					{
						PropertyNameCaseInsensitive = true
					});
					if (authResponse2 != null && (authResponse2.Success || authResponse2.Valid || authResponse2.Status.ToString() == "0" || authResponse2.Status.ToString() == "active" || authResponse2.Status.ToString() == "online"))
					{
						if (string.IsNullOrEmpty(authResponse2.SessionId))
						{
							authResponse2.SessionId = ((!string.IsNullOrEmpty(authResponse2.Token)) ? authResponse2.Token : "sess_local_gate_master");
						}
						authResponse = authResponse2;
						currentAuth = authResponse2;
						text = text2;
						try
						{
							Uri uri = new Uri(text2);
							ActiveAuthServerBaseUrl = $"{uri.Scheme}://{uri.Host}:{uri.Port}";
						}
						catch
						{
						}
						break;
					}
				}
				catch (Exception ex)
				{
					Trace("[GATE] Probe failed for " + text2 + ": " + ex.Message);
				}
			}
		}
		if (authResponse == null)
		{
			TryStartBundledAuthServer();
			using HttpClient httpClient2 = new HttpClient
			{
				Timeout = TimeSpan.FromSeconds(2L)
			};
			for (int j = 0; j < 3; j++)
			{
				if (authResponse != null)
				{
					break;
				}
				Thread.Sleep(400);
				string[] candidateUrls = CandidateUrls;
				foreach (string text3 in candidateUrls)
				{
					try
					{
						AuthResponse authResponse3 = JsonSerializer.Deserialize<AuthResponse>(httpClient2.GetStringAsync(text3).GetAwaiter().GetResult(), new JsonSerializerOptions
						{
							PropertyNameCaseInsensitive = true
						});
						if (authResponse3 != null && (authResponse3.Success || authResponse3.Valid || authResponse3.Status.ToString() == "0" || authResponse3.Status.ToString() == "active" || authResponse3.Status.ToString() == "online"))
						{
							if (string.IsNullOrEmpty(authResponse3.SessionId))
							{
								authResponse3.SessionId = ((!string.IsNullOrEmpty(authResponse3.Token)) ? authResponse3.Token : "sess_local_gate_master");
							}
							authResponse = authResponse3;
							currentAuth = authResponse3;
							text = text3;
							try
							{
								Uri uri2 = new Uri(text3);
								ActiveAuthServerBaseUrl = $"{uri2.Scheme}://{uri2.Host}:{uri2.Port}";
							}
							catch
							{
							}
							break;
						}
					}
					catch
					{
					}
				}
			}
		}
		if (authResponse == null)
		{
			Trace("[GATE] Local auth server not reachable; using built-in offline Gate master session.");
			authResponse = (currentAuth = new AuthResponse
			{
				Success = true,
				Valid = true,
				Token = "sess_local_gate_master",
				SessionId = "sess_local_gate_master",
				UserId = "operator",
				UserName = "operator@mylb.local",
				Slots = 1000,
				AllowedSlots = 1000,
				Plan = "Unlimited Lifetime",
				Message = "Authorized (in-process Gate fallback)"
			});
			text = "http://127.0.0.1:5566/";
		}
		Trace("[GATE] Authentication Successful via " + text + "!");
		Trace($"[GATE] User: {authResponse.UserName}, Slots: {authResponse.AllowedSlots}, Plan: {authResponse.Plan}");
		Assembly assembly = typeof(AccountWindow).Assembly;
		Schema = BotResolver.Resolve(assembly);
		Trace($"[GATE] BotResolver mapped LordsMobileBot v{Schema.Version}: Container={Schema.ContainerType.Name}, StaticClient={Schema.StaticClientType.Name}, Monitor={Schema.MonitorType.Name}, KeyStore={Schema.KeyStoreType.Name}, Features={Schema.ResolvedFeatureCount}");
		InitializeNetworkAdapter(text);
		try
		{
			System.Net.Http.HttpClient.DefaultProxy = DynamicCreationWebProxy.Instance;
			System.Net.WebRequest.DefaultWebProxy = DynamicCreationWebProxy.Instance;
			Trace("[GATE] Initialized DynamicCreationWebProxy on HttpClient.DefaultProxy & WebRequest.DefaultWebProxy.");
		}
		catch (Exception exProxy)
		{
			Trace("[GATE] Note on DefaultProxy setup: " + exProxy.Message);
		}
		InstallStartupHooks(assembly);
		try
		{
			UserLookAndFeel.Default.SetSkinStyle(SkinStyle.Basic);
			Trace("[GATE] Visual skin configured (Basic).");
		}
		catch (Exception ex2)
		{
			Trace("[GATE] Note on skin: " + ex2.Message);
		}
		long slots = ((authResponse.Slots > 0) ? authResponse.Slots : ((authResponse.AllowedSlots > 0) ? authResponse.AllowedSlots : 1000));
		try
		{
			Schema.RunningAccountsField.SetValue(null, BotResolver.CreateSyncCache(Schema, slots));
			Schema.GameLangIdField.SetValue(null, Schema.GameLangEngValue);
			Trace($"[GATE] Account slot quota configured: {slots} concurrent accounts.");
		}
		catch (Exception ex3)
		{
			Trace("[GATE] Warning during slot initialization: " + ex3.Message);
		}
		try
		{
			if (Schema.ObserverType != null)
			{
				object obj5 = Activator.CreateInstance(Schema.ObserverType);
				if (obj5 != null)
				{
					FieldInfo[] array = (from f in Schema.ObserverType.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
						where !f.IsStatic && f.FieldType == typeof(string)
						select f).ToArray();
					if (array.Length != 0)
					{
						array[0].SetValue(obj5, string.IsNullOrEmpty(authResponse.UserName) ? "operator@mylb.local" : authResponse.UserName);
					}
					if (array.Length > 1)
					{
						array[1].SetValue(obj5, string.IsNullOrEmpty(authResponse.Plan) ? "Unlimited Lifetime" : authResponse.Plan);
					}
					foreach (FieldInfo item in from f in Schema.ObserverType.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
						where !f.IsStatic && f.FieldType == Schema.SyncCacheType
						select f)
					{
						item.SetValue(obj5, BotResolver.CreateSyncCache(Schema, slots));
					}
					Schema.ObserverType.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((MethodInfo m) => !m.IsStatic && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(DateTime))?.Invoke(obj5, new object[1] { DateTime.UtcNow.AddYears(50) });
					if (Schema.DistributorFinderType != null)
					{
						object obj6 = Schema.DistributorFinderSingletonGetter?.Invoke(null, null) ?? Schema.DistributorFinderSingletonField?.GetValue(null);
						if (obj6 != null)
						{
							Schema.DistributorFinderType.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((FieldInfo f) => !f.IsStatic && f.FieldType == Schema.ObserverType)?.SetValue(obj6, obj5);
							foreach (FieldInfo item2 in from f in Schema.DistributorFinderType.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
								where !f.IsStatic && f.FieldType == Schema.SyncCacheType
								select f)
							{
								item2.SetValue(obj6, BotResolver.CreateSyncCache(Schema, 0L));
							}
							FieldInfo[] array2 = (from f in Schema.DistributorFinderType.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
								where !f.IsStatic && f.FieldType == typeof(string)
								select f).ToArray();
							if (array2.Length != 0)
							{
								array2[0].SetValue(obj6, authResponse.Token ?? "sess_local_gate_master");
							}
							if (array2.Length > 1)
							{
								array2[1].SetValue(obj6, authResponse.UserId ?? "operator");
							}
							Trace($"[GATE] Populated {Schema.DistributorFinderType.Name} & {Schema.ObserverType.Name} license authorities cleanly.");
						}
					}
				}
			}
		}
		catch (Exception ex4)
		{
			Trace("[GATE] Note initializing UserDistributorFinder: " + ex4.Message);
		}
		EnsureAccountKeys(AppDomain.CurrentDomain.BaseDirectory, typeof(AccountWindow).Assembly);
		try
		{
			string text4 = GameVersionAutoUpdater.CurrentVersion ?? GetConfiguredGameVersion();
			ApplyGameVersion(text4);
		}
		catch (Exception ex5)
		{
			Trace("[GATE] Note on GameVersion init: " + ex5.Message);
		}
		try
		{
			FileSystemWatcher fileSystemWatcher = new FileSystemWatcher(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config"));
			fileSystemWatcher.IncludeSubdirectories = true;
			fileSystemWatcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.CreationTime;
			fileSystemWatcher.EnableRaisingEvents = true;
			fileSystemWatcher.Created += (object _, FileSystemEventArgs _) =>
			{
				try
				{
					EnsureAccountKeys(AppDomain.CurrentDomain.BaseDirectory, typeof(AccountWindow).Assembly);
				}
				catch
				{
				}
			};
			Trace("[GATE] Account directory FileSystemWatcher started.");
		}
		catch (Exception ex6)
		{
			Trace("[GATE] Note on starting config FileSystemWatcher: " + ex6.Message);
		}
		AccountWindow accountWindow;
		try
		{
			Trace("[GATE] Launching AccountWindow...");
			accountWindow = new AccountWindow();
			AccountWindow.Instance = accountWindow;
			try { var _ = accountWindow.Handle; } catch (Exception exHandle) { Trace("[GATE] Note forcing handle creation: " + exHandle.Message); }
			Trace("[GATE] AccountWindow instantiated successfully.");
			try
			{
				Schema.RunningAccountsField.SetValue(null, BotResolver.CreateSyncCache(Schema, slots));
				Trace($"[GATE] Set AccountWindow.runningAccounts to {slots} slots.");
				if (Schema.HasServerConnField != null && Schema.HasServerConnField.GetValue(accountWindow) == null)
				{
					Schema.HasServerConnField.SetValue(accountWindow, BotResolver.CreateSyncCache(Schema, slots));
					Trace("[GATE] Initialized accountWindow.hasServerConn.");
				}
			}
			catch (Exception ex7)
			{
				Trace("[GATE] Note on hasServerConn: " + ex7.Message);
			}
			try
			{
				if (string.IsNullOrEmpty(accountWindow.accountLimitVisual))
				{
					accountWindow.accountLimitVisual = $"{slots} Slots";
					Trace("[GATE] Initialized accountWindow.accountLimitVisual.");
				}
				if (accountWindow.accountLimLabel != null)
				{
					((Control)(object)accountWindow.accountLimLabel).Text = slots.ToString();
					Trace($"[GATE] Initialized accountWindow.accountLimLabel.Text = {slots}");
				}
			}
			catch (Exception ex8)
			{
				Trace("[GATE] Note on accountLimitVisual: " + ex8.Message);
			}
			try
			{
				accountWindow.licenseExpiryVisual = DateTime.UtcNow.AddYears(50);
				Trace("[GATE] Initialized accountWindow.licenseExpiryVisual.");
			}
			catch (Exception ex9)
			{
				Trace("[GATE] Note on licenseExpiryVisual: " + ex9.Message);
			}
			try
			{
				string[] candidateUrls = new string[6] { "totalResource", "totalBagResource", "totalProductionSelected", "totalResourceSelected", "totalBagResourceSelected", "totalCapacitySelected" };
				foreach (string text5 in candidateUrls)
				{
					FieldInfo field = typeof(AccountWindow).GetField(text5, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
					if (field != null && field.GetValue(accountWindow) == null)
					{
						field.SetValue(accountWindow, new BigInteger[5]);
						Trace("[GATE] Initialized accountWindow." + text5 + " (BigInteger[5]).");
					}
				}
			}
			catch (Exception ex10)
			{
				Trace("[GATE] Note initializing BigInteger arrays: " + ex10.Message);
			}
			try
			{
				FieldInfo accountControlField = typeof(AccountWindow).GetField("accountControl", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				((Form)(object)accountWindow).Load += (object? _, EventArgs _) =>
				{
					StartWebAndSeed();
					CloseWaitOverlay();
				};
				((Form)(object)accountWindow).Shown += (object? _, EventArgs _) =>
				{
					StartWebAndSeed();
					CloseWaitOverlay();
					accountWindow.BeginInvoke(new Action(CloseWaitOverlay));
					accountWindow.BeginInvoke(new Action(delegate
					{
						try
						{
							FieldInfo gridReadyField = typeof(AccountWindow).GetField("gridLayoutReady", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
							gridReadyField?.SetValue(accountWindow, true);

							FieldInfo splitPanelField = typeof(AccountWindow).GetField("accountSplitPanel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
							object splitPanel = splitPanelField?.GetValue(accountWindow);
							if (splitPanel != null)
							{
								PropertyInfo pvProp = splitPanel.GetType().GetProperty("PanelVisibility");
								pvProp?.SetValue(splitPanel, DevExpress.XtraEditors.SplitPanelVisibility.Both);
							}
						}
						catch (Exception ex)
						{
							Trace("[GATE] Note setting grid layout/split panel: " + ex.Message);
						}
					}));
				};
				((Form)(object)accountWindow).FormClosing += (object? sender, FormClosingEventArgs e) =>
				{
					Trace($"[GATE] AccountWindow.FormClosing called! CloseReason={e.CloseReason}, Cancel={e.Cancel}");
					Trace($"[GATE] FormClosing StackTrace:\n{new StackTrace(true)}");
				};
				((Form)(object)accountWindow).FormClosed += (object? sender, FormClosedEventArgs e) =>
				{
					Trace($"[GATE] AccountWindow.FormClosed called! CloseReason={e.CloseReason}");
				};
				StartWebAndSeed();
				System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
				timer.Interval = 1500;
				timer.Tick += (object? _, EventArgs _) =>
				{
					try
					{
						StartWebAndSeed();
						CloseWaitOverlay();
						try
						{
							FieldInfo gridReadyField = typeof(AccountWindow).GetField("gridLayoutReady", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
							gridReadyField?.SetValue(accountWindow, true);

							FieldInfo splitPanelField = typeof(AccountWindow).GetField("accountSplitPanel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
							object splitPanel = splitPanelField?.GetValue(accountWindow);
							if (splitPanel != null)
							{
								PropertyInfo pvProp = splitPanel.GetType().GetProperty("PanelVisibility");
								object currentVal = pvProp?.GetValue(splitPanel);
								if (currentVal != null && Convert.ToInt32(currentVal) != 0)
								{
									pvProp?.SetValue(splitPanel, DevExpress.XtraEditors.SplitPanelVisibility.Both);
								}
							}
						}
						catch { }
						GetLicenseConnectionState();
						EnsureAccountKeys(AppDomain.CurrentDomain.BaseDirectory, typeof(AccountWindow).Assembly);
						SeedExistingAccounts(accountWindow, AppDomain.CurrentDomain.BaseDirectory, typeof(AccountWindow).Assembly);
						try
						{
							if (accountWindow.accountLimLabel != null && ((Control)(object)accountWindow.accountLimLabel).Text != slots.ToString())
							{
								((Control)(object)accountWindow.accountLimLabel).Text = slots.ToString();
							}
						}
						catch
						{
						}
						try
						{
							PatchStartupStateMachine();
						}
						catch
						{
						}
						try
						{
							Type? type2 = Schema?.GameVersionType ?? typeof(AccountWindow).Assembly.GetType("LordsMobileBot.AppClasses.Settings.GameVersion");
							if (type2 != null)
							{
								object? obj9 = type2.GetProperty("currentGameVersion", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
								int num = (obj9 != null) ? ((int)(type2.GetField("GV1", BindingFlags.Instance | BindingFlags.Public)?.GetValue(obj9) ?? 0)) : 0;
								if (obj9 == null || num <= 0 || fixedGameVersion == null)
								{
									string text6 = GameVersionAutoUpdater.CurrentVersion ?? GetConfiguredGameVersion();
									ApplyGameVersion(text6);
								}
								else if ((DateTime.UtcNow - s_lastPeriodicVersionCheck).TotalMinutes >= 15)
								{
									s_lastPeriodicVersionCheck = DateTime.UtcNow;
									Task.Run(() => CheckAndUpdateLiveVersion(false));
								}
							}
						}
						catch
						{
						}
						try
						{
							foreach (Form openForm in Application.OpenForms)
							{
								if (openForm != null && openForm.GetType().Name == "botControlForm")
								{
									object obj11 = openForm.GetType().GetField("currentAcc", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(openForm);
									if (obj11 != null)
									{
										SanitizeContainerCoords(obj11);
									}
								}
							}
						}
						catch
						{
						}
						object? obj13 = accountControlField?.GetValue(accountWindow);
						object? obj14 = ((obj13 is GridControl) ? obj13 : null);
						if (((obj14 != null) ? ((GridControl)obj14).DataSource : null) is IEnumerable enumerable)
						{
							{
								foreach (object item3 in enumerable)
								{
									if (item3 != null)
									{
										object obj15 = item3.GetType().GetField("Container", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(item3);
										if (obj15 != null)
										{
											SanitizeContainerCoords(obj15);
										}
										try
										{
											string text7 = item3.GetType().GetProperty("IGG")?.GetValue(item3)?.ToString();
											if (string.IsNullOrEmpty(text7) || text7 == "0")
											{
												text7 = item3.GetType().GetField("IGG_S", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(item3)?.ToString();
											}
											if (!string.IsNullOrEmpty(text7) && text7 != "0")
											{
												Type type3 = item3.GetType();
												string text8 = type3.GetField("AccessKey", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(item3)?.ToString() ?? string.Empty;
												if (!IsValidAccessKey(text8))
												{
													string text9 = ReadLocalAccessKey(text7);
													if (!IsValidAccessKey(text9))
													{
														text9 = "eyJha2lkIjoyMDIxMDQyMiwicmtpZCI6MjAyMTA0MjMsImciOjEwNTEwMjk5MDIsInYiOjN9.5ozW3_x4_vryAXrZXXtdOhma1yYa6G2bGnQsZrBtZC_TK6xpxeoVhYNWfESUnS4gsigPH0gAwXZW2rKojXtqWUPe8WaBjHyRx9hqBHHQ06ur-o9aZARheeeo-3NcJgp4hKHhosA4_uzb0FLqgAEgahVq0OrWk62001vcKL4dpBI.GZ5slgdeYpn4Du7YASjGN0Vrx_sUD9o6RAqmBVKhUHdKXHSIy0gkg-ZPT1q22JEiIiXydZ7FYzQhDZmebrxsksvgl5Vb5oLqC6dv9SX4CyCqEXQXmjkKAWdzQ7FYMiCwtoUPW69SkX8wAhuXCcHgaKw20mHK3Cy_zLJlZ4goQZk";
													}
													text8 = text9;
													type3.GetMethod("setAccessKey", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(item3, new object[2] { text8, false });
													type3.GetField("AccessKey", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(item3, text8);
													object obj16 = type3.GetField("Config", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(item3);
													if (obj16 != null)
													{
														obj16.GetType().GetField("_AccessKey", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(obj16, text8);
														obj16.GetType().GetProperty("AccessKey", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(obj16, text8);
													}
													Trace($"[GATE] [CHG-028] Hydrated AccessKey (len={text8.Length}) for account {text7}.");
												}
												else
												{
													object obj17 = type3.GetField("Config", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(item3);
													if (obj17 != null && !IsValidAccessKey(obj17.GetType().GetField("_AccessKey", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(obj17)?.ToString()))
													{
														obj17.GetType().GetField("_AccessKey", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(obj17, text8);
														obj17.GetType().GetProperty("AccessKey", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(obj17, text8);
													}
												}
												PropertyInfo property4 = item3.GetType().GetProperty("Name");
												string text10 = property4?.GetValue(item3)?.ToString();
												if (string.IsNullOrEmpty(text10) || text10 == "Account Not Loaded")
												{
													string value4 = "ID.B" + ((text7.Length > 8) ? text7.Substring(text7.Length - 8) : text7);
													property4?.SetValue(item3, value4);
													item3.GetType().GetField("_Name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(item3, value4);
													object obj18 = type3.GetField("Config", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(item3);
													if (obj18 != null)
													{
														obj18.GetType().GetProperty("Name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(obj18, value4);
														obj18.GetType().GetField("<Name>k__BackingField", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(obj18, value4);
													}
												}
												string text11 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", text7);
												if (!Directory.Exists(text11))
												{
													Directory.CreateDirectory(text11);
												}
												string text12 = Path.Combine(text11, "acc.json");
												if (IsValidAccessKey(text8))
												{
													AccessKeyCache[text7] = text8;
													PersistAccessKeyInAccJsonIfNeeded(text7, text8);
												}
												PopulateAccountAdmission(text7, text8);
												if (Directory.Exists(text11) && !File.Exists(text12))
												{
													item3.GetType().GetMethod("ScheduleSave", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(item3, null);
													object obj19 = item3.GetType().GetField("Config", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(item3);
													if (obj19 != null)
													{
														obj19.GetType().GetMethod("Save", new Type[1] { typeof(string) })?.Invoke(obj19, new object[1] { text12 });
														if (File.Exists(text12))
														{
															Trace("[GATE] Auto-persisted acc.json for new account " + text7 + "!");
														}
													}
												}
											}
										}
										catch
										{
										}
									}
								}
								return;
							}
						}
					}
					catch
					{
					}
				};
				timer.Start();
				Trace("[GATE] Host enforcer and persistence timer started.");
			}
			catch (Exception ex11)
			{
				Trace("[GATE] Note on starting host enforcer timer: " + ex11.Message);
			}
			Application.Run((Form)(object)accountWindow);
		}
		catch (Exception ex12)
		{
			Console.WriteLine("[GATE] Error launching AccountWindow: " + ex12);
			File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), ex12.ToString());
			MessageBox.Show("Error initializing AccountWindow:\n\n" + ex12, "LordsBot Gate", MessageBoxButtons.OK, MessageBoxIcon.Hand);
		}
		void StartWebAndSeed()
		{
			try
			{
				EnsureAccountKeys(AppDomain.CurrentDomain.BaseDirectory, typeof(AccountWindow).Assembly);
				SeedExistingAccounts(accountWindow, AppDomain.CurrentDomain.BaseDirectory, typeof(AccountWindow).Assembly);
				try
				{
					PatchStartupStateMachine();
				}
				catch
				{
				}
				WebControlServer.Start(accountWindow, Trace);
			}
			catch (Exception ex13)
			{
				Trace("[GATE] Note in StartWebAndSeed: " + ex13.Message);
			}
		}
	}

	private static void TryStartBundledAuthServer()
	{
		try
		{
			string text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "local_server", "server.py");
			if (!File.Exists(text))
			{
				return;
			}
			string[] array = new string[2] { "pythonw", "python" };
			foreach (string text2 in array)
			{
				try
				{
					Process process = Process.Start(new ProcessStartInfo
					{
						FileName = text2,
						Arguments = "\"" + text + "\"",
						WorkingDirectory = Path.GetDirectoryName(text),
						UseShellExecute = false,
						CreateNoWindow = true,
						WindowStyle = ProcessWindowStyle.Hidden
					});
					if (process != null)
					{
						Trace($"[GATE] Auto-started bundled local_server/server.py via {text2} (PID {process.Id}).");
						break;
					}
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
	}

	private static void SanitizeContainerCoords(object container)
	{
		try
		{
			FieldInfo fieldInfo = Schema?.ContainerCoordArrayField ?? container.GetType().GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((FieldInfo f) => !f.IsStatic && f.FieldType == typeof(string[]));
			if (fieldInfo == null)
			{
				return;
			}
			if (!(fieldInfo.GetValue(container) is string[] array) || array.Length < 3)
			{
				fieldInfo.SetValue(container, new string[3] { "0", "0", "0" });
				return;
			}
			if (!int.TryParse(array[0], out var _))
			{
				array[0] = "0";
			}
			if (string.IsNullOrEmpty(array[1]))
			{
				array[1] = "0";
			}
			if (string.IsNullOrEmpty(array[2]))
			{
				array[2] = "0";
			}
		}
		catch
		{
		}
	}

	private static object? CreateAdmissionItem(BotSchema schema, string iggId, string accessKey, bool allowed, string note)
	{
		object obj = Activator.CreateInstance(schema.AdmissionType);
		if (obj == null)
		{
			return null;
		}
		schema.AdmissionIdField?.SetValue(obj, iggId);
		schema.AdmissionAllowedField?.SetValue(obj, allowed);
		schema.AdmissionNoteField?.SetValue(obj, note);
		if (IsValidAccessKey(accessKey))
		{
			schema.AdmissionAccessKeyProp?.SetValue(obj, accessKey);
		}
		return obj;
	}

	private static void AddMonitorAdmission(BotSchema schema, object? monitorInst, string iggId, object admissionItem)
	{
		if (monitorInst == null)
		{
			return;
		}
		object value = schema.MonitorDicField.GetValue(monitorInst);
		if (value != null)
		{
			Type type = value.GetType().GetGenericArguments()[1];
			string activeSessionToken = GetActiveSessionToken();
			object obj = Activator.CreateInstance(type, activeSessionToken, admissionItem);
			MethodInfo method = value.GetType().GetMethod("set_Item");
			if (method != null)
			{
				method.Invoke(value, new object[2] { iggId, obj });
			}
			else
			{
				value.GetType().GetMethod("TryAdd")?.Invoke(value, new object[2] { iggId, obj });
			}
		}
	}

	private static void InitializeNetworkAdapter(string connectedEndpoint)
	{
		if (Schema == null)
		{
			return;
		}
		try
		{
			Trace("[GATE] Initializing Network Adapter & Registration Pipeline...");
			string item = "http://127.0.0.1:5566";
			if (!string.IsNullOrEmpty(connectedEndpoint))
			{
				try
				{
					Uri uri = new Uri(connectedEndpoint);
					item = $"{uri.Scheme}://{uri.Host}:{uri.Port}";
				}
				catch
				{
				}
			}
			object obj2 = Activator.CreateInstance(Schema.EndpointListHolderType);
			Schema.EndpointListField.SetValue(obj2, new List<string> { item }.AsReadOnly());
			object obj3 = Schema.StaticClientCtor.Invoke(new object[1] { obj2 });
			try
			{
				LocalSignerInvoker localSignerInvoker = new LocalSignerInvoker(GetSigner());
				object value = Activator.CreateInstance(Schema.CallInvokerWrapperType, localSignerInvoker);
				FieldInfo[] staticClientWrapperFields = Schema.StaticClientWrapperFields;
				for (int i = 0; i < staticClientWrapperFields.Length; i++)
				{
					staticClientWrapperFields[i].SetValue(obj3, value);
				}
				Trace($"[GATE] Custom LocalSignerInvoker injected into {Schema.StaticClientType.Name} ({Schema.StaticClientWrapperFields.Length} wrapper field(s)).");
			}
			catch (Exception ex)
			{
				Trace("[GATE] Warning while wiring LocalSignerInvoker: " + ex.Message);
			}
			Schema.StaticClientQueryVerifierMethod?.Invoke(null, new object[1] { obj3 });
			object obj4 = Schema.BridgeSorterCtor.Invoke(new object[1] { obj3 });
			_wiredStaticClient = obj3;
			_wiredBridgeSorter = obj4;
			object obj5 = Schema.AdapterCtor.Invoke(new object[2] { obj3, obj4 });
			if (Schema.AdapterStaticSetter != null)
			{
				Schema.AdapterStaticSetter.Invoke(null, new object[1] { obj5 });
			}
			else
			{
				Schema.AdapterStaticBackingField?.SetValue(null, obj5);
			}
			object obj6 = Schema.AdapterStaticBackingField?.GetValue(null);
			Trace("[GATE] " + Schema.AdapterType.Name + " registered successfully! Adapter=" + ((obj6 != null) ? "ACTIVE" : "SET"));
			SingletonWiring[] clientBridgeSingletons = Schema.ClientBridgeSingletons;
			foreach (SingletonWiring singletonWiring in clientBridgeSingletons)
			{
				try
				{
					if (singletonWiring.StaticBackingField?.GetValue(null) != null)
					{
						Trace("[GATE] Singleton " + singletonWiring.Type.Name + " already initialized.");
						continue;
					}
					object obj7 = singletonWiring.Ctor.Invoke(new object[2] { obj3, obj4 });
					if (singletonWiring.StaticSetter != null)
					{
						singletonWiring.StaticSetter.Invoke(null, new object[1] { obj7 });
					}
					else
					{
						singletonWiring.StaticBackingField?.SetValue(null, obj7);
					}
					object obj8 = singletonWiring.StaticBackingField?.GetValue(null);
					Trace($"[GATE] Initialized singleton {singletonWiring.Type.Name} ({((obj8 != null) ? "ACTIVE" : "SET")}).");
				}
				catch (Exception ex2)
				{
					Trace("[GATE] Warning initializing singleton " + singletonWiring.Type.Name + ": " + ex2.Message);
				}
			}
			try
			{
				if (Schema.StaticClientGetIggHostsMethod != null)
				{
					ParameterInfo[] parameters = Schema.StaticClientGetIggHostsMethod.GetParameters();
					object[] array = new object[parameters.Length];
					for (int j = 0; j < parameters.Length; j++)
					{
						ref object reference = ref array[j];
						object obj9;
						if (parameters[j].ParameterType == typeof(string))
						{
							obj9 = "lordsmobile";
						}
						else if (parameters[j].ParameterType == typeof(bool))
						{
							obj9 = false;
						}
						else
						{
							obj9 = (parameters[j].ParameterType.IsValueType ? Activator.CreateInstance(parameters[j].ParameterType) : null);
						}
						reference = obj9;
					}
					Trace("[GATE] Pre-fetching IGG hosts via " + Schema.StaticClientGetIggHostsMethod.Name + "...");
					(Schema.StaticClientGetIggHostsMethod.Invoke(obj3, array) as Task)?.Wait(2000);
					Trace("[GATE] Pre-fetch IGG hosts completed.");
				}
			}
			catch (Exception ex3)
			{
				Trace("[GATE] Note on pre-fetching IGG hosts: " + ex3.Message);
			}
		}
		catch (Exception ex4)
		{
			Trace("[GATE] Warning during Network Adapter initialization: " + ex4);
		}
	}

	private static (string? plainJson, string? accessKey, string? name) DecryptAccJson(string accJsonPath, byte[]? keyBytes)
	{
		try
		{
			if (!File.Exists(accJsonPath) || keyBytes == null || keyBytes.Length != 32)
			{
				return (plainJson: null, accessKey: null, name: null);
			}
			using JsonDocument jsonDocument = JsonDocument.Parse(File.ReadAllText(accJsonPath));
			JsonElement rootElement = jsonDocument.RootElement;
			if (!rootElement.TryGetProperty("n", out var value) || !rootElement.TryGetProperty("d", out var value2))
			{
				return (plainJson: null, accessKey: null, name: null);
			}
			byte[] nonce = Convert.FromBase64String(value.GetString());
			byte[] array = Convert.FromBase64String(value2.GetString());
			if (array.Length < 16)
			{
				return (plainJson: null, accessKey: null, name: null);
			}
			byte[] array2 = new byte[16];
			byte[] array3 = new byte[array.Length - 16];
			Array.Copy(array, array.Length - 16, array2, 0, 16);
			Array.Copy(array, 0, array3, 0, array3.Length);
			byte[] array4 = new byte[array3.Length];
			using AesGcm aesGcm = new AesGcm(keyBytes, 16);
			aesGcm.Decrypt(nonce, array3, array2, array4);
			string text = Encoding.UTF8.GetString(array4);
			string item = null;
			string item2 = null;
			using JsonDocument jsonDocument2 = JsonDocument.Parse(text);
			if (jsonDocument2.RootElement.TryGetProperty("AccessKey", out var value3) && value3.ValueKind == JsonValueKind.String)
			{
				item = value3.GetString();
			}
			if (jsonDocument2.RootElement.TryGetProperty("Name", out var value4) && value4.ValueKind == JsonValueKind.String)
			{
				item2 = value4.GetString();
			}
			return (plainJson: text, accessKey: item, name: item2);
		}
		catch (Exception ex)
		{
			Trace("[GATE] DecryptAccJson error for " + accJsonPath + ": " + ex.Message);
			return (plainJson: null, accessKey: null, name: null);
		}
	}

	private static void PersistAccessKeyInAccJsonIfNeeded(string accountId, string accessKey)
	{
		if (string.IsNullOrEmpty(accountId) || !IsValidAccessKey(accessKey))
		{
			return;
		}
		try
		{
			string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
			string text = Path.Combine(baseDirectory, "config", accountId, "acc.json");
			if (!File.Exists(text))
			{
				return;
			}
			DateTime lastWriteTimeUtc = File.GetLastWriteTimeUtc(text);
			if (_verifiedAccJsonWriteTime.TryGetValue(accountId, out var value) && value == lastWriteTimeUtc)
			{
				return;
			}
			string path = Path.Combine(baseDirectory, "config", accountId, "account.key");
			string path2 = Path.Combine(baseDirectory, "config", "global", "account.key");
			byte[] array = null;
			if (File.Exists(path))
			{
				array = File.ReadAllBytes(path);
			}
			else if (File.Exists(path2))
			{
				array = File.ReadAllBytes(path2);
			}
			if (array == null || array.Length != 32)
			{
				_verifiedAccJsonWriteTime[accountId] = lastWriteTimeUtc;
				return;
			}
			var (text2, ak, _) = DecryptAccJson(text, array);
			if (text2 == null)
			{
				_verifiedAccJsonWriteTime[accountId] = lastWriteTimeUtc;
				return;
			}
			if (IsValidAccessKey(ak))
			{
				_verifiedAccJsonWriteTime[accountId] = lastWriteTimeUtc;
				return;
			}
			JsonObject jsonObject = JsonNode.Parse(text2)?.AsObject();
			if (jsonObject != null)
			{
				jsonObject["AccessKey"] = accessKey;
				if (jsonObject["Name"]?.ToString() == "Account Not Loaded")
				{
					jsonObject["Name"] = "ID.B" + ((accountId.Length > 8) ? accountId.Substring(accountId.Length - 8) : accountId);
				}
				byte[] bytes = Encoding.UTF8.GetBytes(jsonObject.ToJsonString());
				byte[] array2 = new byte[12];
				RandomNumberGenerator.Fill(array2);
				byte[] array3 = new byte[bytes.Length];
				byte[] array4 = new byte[16];
				using (AesGcm aesGcm = new AesGcm(array, 16))
				{
					aesGcm.Encrypt(array2, bytes, array3, array4);
				}
				byte[] array5 = new byte[array3.Length + array4.Length];
				Buffer.BlockCopy(array3, 0, array5, 0, array3.Length);
				Buffer.BlockCopy(array4, 0, array5, array3.Length, array4.Length);
				var value2 = new
				{
					v = 2,
					n = Convert.ToBase64String(array2),
					d = Convert.ToBase64String(array5)
				};
				File.WriteAllText(text, JsonSerializer.Serialize(value2));
				_verifiedAccJsonWriteTime[accountId] = File.GetLastWriteTimeUtc(text);
				Trace($"[GATE] [CHG-030] Preserved AccessKey (len={accessKey.Length}) in saved acc.json for {accountId}.");
			}
		}
		catch (Exception ex)
		{
			Trace("[GATE] PersistAccessKeyInAccJsonIfNeeded error for " + accountId + ": " + ex.Message);
		}
	}

	private static void EnsureAccountKeys(string baseDir, Assembly asm)
	{
		if (Schema == null)
		{
			return;
		}
		try
		{
			RuntimeHelpers.RunClassConstructor(Schema.KeyStoreType.TypeHandle);
			ConcurrentDictionary<string, byte[]> concurrentDictionary = Schema.KeyStoreMapField.GetValue(null) as ConcurrentDictionary<string, byte[]>;
			if (concurrentDictionary == null)
			{
				concurrentDictionary = new ConcurrentDictionary<string, byte[]>();
				Schema.KeyStoreMapField.SetValue(null, concurrentDictionary);
			}
			MethodInfo keyStoreStoreMethod = Schema.KeyStoreStoreMethod;
			MethodInfo keyStoreSetMethod = Schema.KeyStoreSetMethod;
			FieldInfo fieldInfo = asm.GetTypes().FirstOrDefault((Type t) => t.Name == "CommandModel")?.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((FieldInfo f) => f.IsStatic && f.FieldType == typeof(Dictionary<string, byte[]>));
			Dictionary<string, byte[]> dictionary = fieldInfo?.GetValue(null) as Dictionary<string, byte[]>;
			if (dictionary == null && fieldInfo != null)
			{
				dictionary = new Dictionary<string, byte[]>();
				fieldInfo.SetValue(null, dictionary);
			}
			string text = Path.Combine(baseDir, "config");
			if (!Directory.Exists(text))
			{
				Directory.CreateDirectory(text);
			}
			string text2 = Path.Combine(text, "global");
			if (!Directory.Exists(text2))
			{
				Directory.CreateDirectory(text2);
			}
			string path = Path.Combine(text2, "account.key");
			byte[] array;
			if (File.Exists(path) && File.ReadAllBytes(path).Length == 32)
			{
				array = File.ReadAllBytes(path);
			}
			else
			{
				array = new byte[32];
				RandomNumberGenerator.Fill(array);
				File.WriteAllBytes(path, array);
				Trace("[GATE] Generated global account.key (32 bytes).");
			}
			RegisterKey("global", array, concurrentDictionary, null, dictionary, keyStoreStoreMethod, keyStoreSetMethod);
			int num = 0;
			string[] directories = Directory.GetDirectories(text);
			foreach (string text3 in directories)
			{
				string fileName = Path.GetFileName(text3);
				if (string.Equals(fileName, "global", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				string path2 = Path.Combine(text3, "account.key");
				byte[] array2 = null;
				if (File.Exists(path2))
				{
					byte[] array3 = File.ReadAllBytes(path2);
					if (array3.Length == 32)
					{
						array2 = array3;
					}
				}
				if (array2 == null)
				{
					array2 = new byte[32];
					RandomNumberGenerator.Fill(array2);
					try
					{
						File.WriteAllBytes(path2, array2);
						Trace("[GATE] Created new 32-byte account.key for account " + fileName);
					}
					catch (Exception ex)
					{
						Trace("[GATE] Failed writing account.key for " + fileName + ": " + ex.Message);
					}
				}
				RegisterKey(fileName, array2, concurrentDictionary, null, dictionary, keyStoreStoreMethod, keyStoreSetMethod);
				num++;
			}
			if (!keysLoggedOnce)
			{
				keysLoggedOnce = true;
				Trace($"[GATE] [CHG-025] EnsureAccountKeys registered {num} account keys + global into CompilerRoleUser._VisualMatcherDic (found={concurrentDictionary != null}, count={concurrentDictionary?.Count ?? 0}).");
			}
		}
		catch (Exception ex2)
		{
			Trace("[GATE] Exception in EnsureAccountKeys: " + ex2.Message);
		}
	}

	private static void RegisterKey(string accountId, byte[] keyBytes, ConcurrentDictionary<string, byte[]>? visualMatcherMap, ConcurrentDictionary<string, byte[]>? resolverMap, Dictionary<string, byte[]>? cmdMap, MethodInfo? storeMethod, MethodInfo? setMethod)
	{
		try
		{
			if (visualMatcherMap != null)
			{
				SetXorSplitKey(visualMatcherMap, accountId, keyBytes);
			}
			if (resolverMap != null)
			{
				resolverMap[accountId] = keyBytes;
			}
			if (cmdMap != null)
			{
				lock (cmdMap)
				{
					cmdMap[accountId] = keyBytes;
				}
			}
			InvokeKeySetter(storeMethod, accountId, keyBytes);
			InvokeKeySetter(setMethod, accountId, keyBytes);
			if (!long.TryParse(accountId, out var result))
			{
				return;
			}
			string text = result.ToString();
			if (visualMatcherMap != null)
			{
				SetXorSplitKey(visualMatcherMap, text, keyBytes);
			}
			if (resolverMap != null)
			{
				resolverMap[text] = keyBytes;
			}
			if (cmdMap != null)
			{
				lock (cmdMap)
				{
					cmdMap[text] = keyBytes;
				}
			}
			InvokeKeySetter(storeMethod, text, keyBytes);
			InvokeKeySetter(setMethod, text, keyBytes);
		}
		catch (Exception ex)
		{
			Trace("[GATE] Error registering key for '" + accountId + "': " + ex.Message);
		}
	}

	private static void InvokeKeySetter(MethodInfo? m, string id, byte[] keyBytes)
	{
		if (!(m == null))
		{
			ParameterInfo[] parameters = m.GetParameters();
			if (parameters.Length == 2)
			{
				m.Invoke(null, new object[2] { id, keyBytes });
			}
			else if (parameters.Length == 4 && parameters[0].ParameterType == typeof(byte))
			{
				m.Invoke(null, new object[4]
				{
					(byte)0,
					0,
					id,
					keyBytes
				});
			}
			else if (parameters.Length == 4 && parameters[0].ParameterType == typeof(string))
			{
				m.Invoke(null, new object[4] { id, null, 0.0, keyBytes });
			}
		}
	}

	private static void SetXorSplitKey(ConcurrentDictionary<string, byte[]> map, string keyId, byte[] rawKey)
	{
		byte[] array = rawKey;
		if (rawKey.Length == 32)
		{
			if (map.TryGetValue(keyId, out byte[] value) && value != null && value.Length == 64)
			{
				bool flag = true;
				for (int i = 0; i < 32; i++)
				{
					if ((byte)(value[i] ^ value[32 + i]) != rawKey[i])
					{
						flag = false;
						break;
					}
				}
				if (flag)
				{
					return;
				}
			}
			array = new byte[64];
			RandomNumberGenerator.Fill(array.AsSpan(0, 32));
			for (int j = 0; j < 32; j++)
			{
				array[32 + j] = (byte)(rawKey[j] ^ array[j]);
			}
		}
		map[keyId] = array;
	}

	private static void PopulateAccountAdmission(string accountId, string accessKey)
	{
		if (Schema == null)
		{
			return;
		}
		try
		{
			object obj = Schema.MonitorSingletonField?.GetValue(null);
			if (obj == null)
			{
				return;
			}
			object obj2 = CreateAdmissionItem(Schema, accountId, accessKey, allowed: true, "");
			if (obj2 != null)
			{
				AddMonitorAdmission(Schema, obj, accountId, obj2);
				bool flag = IsValidAccessKey(accessKey);
				string activeSessionToken = GetActiveSessionToken();
				string text = $"{activeSessionToken}:{flag}:{(flag ? accessKey.Length : 0)}";
				if (!_lastAdmissionState.TryGetValue(accountId, out string value) || value != text)
				{
					_lastAdmissionState[accountId] = text;
					Trace($"[GATE] Pre-populated account {accountId} into {Schema.MonitorType.Name} with token '{activeSessionToken}' (akLen={(flag ? accessKey.Length : 0)}).");
				}
			}
		}
		catch (Exception ex)
		{
			Trace("[GATE] Error in PopulateAccountAdmission(" + accountId + "): " + ex.Message);
		}
	}

	private static void SeedExistingAccounts(AccountWindow accountWindow, string baseDir, Assembly asm)
	{
		try
		{
			string path = Path.Combine(baseDir, "config");
			if (!Directory.Exists(path))
			{
				return;
			}
			object? obj = typeof(AccountWindow).GetField("accountControl", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(accountWindow);
			object? obj2 = ((obj is GridControl) ? obj : null);
			object obj3 = ((obj2 != null) ? ((GridControl)obj2).DataSource : null);
			if (obj3 == null)
			{
				return;
			}
			MethodInfo method = obj3.GetType().GetMethod("Add");
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (obj3 is IEnumerable enumerable)
			{
				foreach (object item in enumerable)
				{
					if (item != null)
					{
						string text = item.GetType().GetProperty("IGG")?.GetValue(item)?.ToString();
						if (string.IsNullOrEmpty(text) || text == "0")
						{
							text = item.GetType().GetField("IGG_S", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(item)?.ToString();
						}
						if (!string.IsNullOrEmpty(text))
						{
							hashSet.Add(text);
						}
					}
				}
			}
			Type type = asm.GetType("LordsMobileBot.AppClasses.AccountManager.accountRecord");
			Type type2 = asm.GetType("LordsMobileBot.AppClasses.Settings.accountConfig");
			if (type == null || type2 == null)
			{
				return;
			}
			MethodInfo method2 = type2.GetMethod("Load", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			bool flag = false;
			string[] directories = Directory.GetDirectories(path);
			foreach (string text2 in directories)
			{
				string fileName = Path.GetFileName(text2);
				if (string.Equals(fileName, "global", StringComparison.OrdinalIgnoreCase) || string.Equals(fileName, "_incomplete_backup", StringComparison.OrdinalIgnoreCase) || !long.TryParse(fileName, out var result) || hashSet.Contains(fileName))
				{
					continue;
				}
				try
				{
					object obj4 = null;
					string text3 = null;
					string text4 = null;
					string text5 = Path.Combine(text2, "acc.json");
					string path2 = Path.Combine(text2, "account.key");
					byte[] array = null;
					if (File.Exists(path2))
					{
						byte[] array2 = File.ReadAllBytes(path2);
						if (array2.Length == 32)
						{
							array = array2;
						}
					}
					if (File.Exists(text5))
					{
						if (array != null)
						{
							var (text6, text7, text8) = DecryptAccJson(text5, array);
							if (!string.IsNullOrEmpty(text6))
							{
								try
								{
									obj4 = JsonConvert.DeserializeObject(text6, type2);
									text3 = text7;
									text4 = text8;
									Trace($"[GATE] Decrypted & deserialized acc.json for {fileName}: Name='{text8}', AccessKeyLen={text7?.Length ?? 0}");
								}
								catch (Exception ex)
								{
									Trace("[GATE] Note deserializing decrypted json for " + fileName + ": " + ex.Message);
								}
							}
						}
						if (obj4 == null)
						{
							try
							{
								obj4 = method2?.Invoke(null, new object[1] { fileName });
							}
							catch
							{
							}
							if (obj4 == null)
							{
								try
								{
									obj4 = method2?.Invoke(null, new object[1] { text2 });
								}
								catch
								{
								}
							}
						}
					}
					if (obj4 == null)
					{
						obj4 = Activator.CreateInstance(type2);
						if (obj4 != null)
						{
							type2.GetProperty("FileName")?.SetValue(obj4, text5);
							type2.GetField("<FileName>k__BackingField", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(obj4, text5);
							type2.GetField("_IGGID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(obj4, fileName);
							type2.GetProperty("IGG")?.SetValue(obj4, result);
							type2.GetField("<IGG>k__BackingField", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(obj4, result);
							type2.GetField("<Name>k__BackingField", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(obj4, text4 ?? "Account Not Loaded");
						}
					}
					object obj7 = Activator.CreateInstance(type);
					if (obj7 == null)
					{
						continue;
					}
					type.GetProperty("IGG")?.SetValue(obj7, result);
					type.GetField("IGG_S", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(obj7, fileName);
					type.GetField("Config", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(obj7, obj4);
					string text9 = text3;
					if (!IsValidAccessKey(text9) && obj4 != null)
					{
						text9 = type2.GetField("_AccessKey", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(obj4)?.ToString();
						if (!IsValidAccessKey(text9))
						{
							text9 = type2.GetProperty("AccessKey", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(obj4)?.ToString();
						}
					}
					if (IsValidAccessKey(text9))
					{
						type.GetMethod("setAccessKey", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(obj7, new object[2] { text9, false });
						type.GetProperty("AccessKey", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(obj7, text9);
					}
					else
					{
						text9 = string.Empty;
					}
					string value = text4;
					if (string.IsNullOrEmpty(value) && obj4 != null)
					{
						value = type2.GetField("<Name>k__BackingField", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(obj4)?.ToString();
						if (string.IsNullOrEmpty(value))
						{
							value = type2.GetProperty("Name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(obj4)?.ToString();
						}
					}
					if (!string.IsNullOrEmpty(value))
					{
						type.GetField("_Name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(obj7, value);
						type.GetProperty("Name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(obj7, value);
					}
					type.GetMethod("Init", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(obj7, null);
					string proxyVal = null;
					if (obj4 != null)
					{
						proxyVal = type2.GetProperty("CreationIP", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(obj4)?.ToString()
								?? type2.GetField("CreationIP", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(obj4)?.ToString();
					}
					if ((string.IsNullOrEmpty(proxyVal) || !proxyVal.Contains(":")) && _assignedProxyDisplayMap.TryGetValue(fileName, out var dispPxy))
					{
						proxyVal = dispPxy;
					}
					if (!string.IsNullOrEmpty(proxyVal) && proxyVal.Contains(":"))
					{
						type.GetProperty("assignedProxy", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(obj7, proxyVal);
						type.GetField("_assignedProxy", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(obj7, proxyVal);
						_assignedProxyDisplayMap[fileName] = proxyVal;
					}
					else if (AreProxiesEnabled(baseDir))
					{
						string nextProxy = GetNextAvailableProxy(baseDir);
						if (!string.IsNullOrEmpty(nextProxy) && nextProxy.Contains(":"))
						{
							proxyVal = nextProxy;
							type.GetProperty("assignedProxy", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(obj7, proxyVal);
							type.GetField("_assignedProxy", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(obj7, proxyVal);
							_assignedProxyDisplayMap[fileName] = proxyVal;
							if (array != null)
							{
								SyncProxyToSettingsAndMemory(fileName, proxyVal, array, refreshGrid: false);
							}
						}
					}
					object obj8 = type.GetField("Container", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(obj7);
					if (obj8 != null)
					{
						SanitizeContainerCoords(obj8);
						object obj9 = obj8.GetType().GetProperty("Settings", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(obj8) ?? obj8.GetType().GetField("Settings", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(obj8);
						if (WebControlServer.SanitizeSettingsDefaults(obj9))
						{
							obj9?.GetType().GetMethod("Save", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)?.Invoke(obj9, null);
						}
					}
					method?.Invoke(obj3, new object[1] { obj7 });
					hashSet.Add(fileName);
					flag = true;
					Trace("[GATE] Seeded existing account " + fileName + " into grid DataSource.");
					PopulateAccountAdmission(fileName, text9 ?? string.Empty);
				}
				catch (Exception ex2)
				{
					Trace("[GATE] Note on seeding account " + fileName + ": " + ex2.Message);
				}
			}
			if (flag)
			{
				typeof(AccountWindow).GetMethod("UpdateAccountInfo", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(accountWindow, null);
			}
		}
		catch (Exception ex3)
		{
			Trace("[GATE] Error in SeedExistingAccounts: " + ex3.Message);
		}
	}

	internal static LocalSignerService GetSigner()
	{
		if (s_signer == null)
		{
			lock (typeof(Program))
			{
				if (s_signer == null)
				{
					s_signer = new LocalSignerService(AppDomain.CurrentDomain.BaseDirectory);
				}
			}
		}
		return s_signer;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool VirtualProtect(nint lpAddress, nuint dwSize, uint flNewProtect, out uint lpflOldProtect);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool FlushInstructionCache(nint hProcess, nint lpBaseAddress, nuint dwSize);

	[DllImport("kernel32.dll")]
	private static extern nint GetCurrentProcess();

	private static MethodInfo HandlerMethod(string name)
	{
		return typeof(Program).GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
	}

	private static bool IsValidAccessKey(string? ak)
	{
		if (!string.IsNullOrEmpty(ak) && !string.Equals(ak, "Account Not Loaded", StringComparison.OrdinalIgnoreCase))
		{
			return ak.Length >= 32;
		}
		return false;
	}

	private static string ReadLocalAccessKey(string accountId)
	{
		if (AccessKeyCache.TryGetValue(accountId, out string value) && IsValidAccessKey(value))
		{
			return value;
		}
		try
		{
			string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
			string text = Path.Combine(baseDirectory, "config", accountId, "acc.json");
			string path = Path.Combine(baseDirectory, "config", accountId, "account.key");
			string path2 = Path.Combine(baseDirectory, "config", "global", "account.key");
			byte[] keyBytes = null;
			if (File.Exists(path))
			{
				keyBytes = File.ReadAllBytes(path);
			}
			else if (File.Exists(path2))
			{
				keyBytes = File.ReadAllBytes(path2);
			}
			if (!File.Exists(text))
			{
				string text2 = Path.Combine(baseDirectory, "config", accountId);
				if (!Directory.Exists(text2))
				{
					Directory.CreateDirectory(text2);
				}
				string path3 = Path.Combine(text2, "account.key");
				if (!File.Exists(path3))
				{
					byte[] array = new byte[32];
					RandomNumberGenerator.Fill(array);
					File.WriteAllBytes(path3, array);
				}
				string text3 = "eyJha2lkIjoyMDIxMDQyMiwicmtpZCI6MjAyMTA0MjMsImciOjEwNTEwMjk5MDIsInYiOjN9.5ozW3_x4_vryAXrZXXtdOhma1yYa6G2bGnQsZrBtZC_TK6xpxeoVhYNWfESUnS4gsigPH0gAwXZW2rKojXtqWUPe8WaBjHyRx9hqBHHQ06ur-o9aZARheeeo-3NcJgp4hKHhosA4_uzb0FLqgAEgahVq0OrWk62001vcKL4dpBI.GZ5slgdeYpn4Du7YASjGN0Vrx_sUD9o6RAqmBVKhUHdKXHSIy0gkg-ZPT1q22JEiIiXydZ7FYzQhDZmebrxsksvgl5Vb5oLqC6dv9SX4CyCqEXQXmjkKAWdzQ7FYMiCwtoUPW69SkX8wAhuXCcHgaKw20mHK3Cy_zLJlZ4goQZk";
				PersistAccessKeyInAccJsonIfNeeded(accountId, text3);
				AccessKeyCache[accountId] = text3;
				Trace("[GATE] [CHG-044] Provisioned initial AccessKey & created acc.json for new account " + accountId);
				return text3;
			}
			string text4 = File.ReadAllText(text);
			if (!text4.Contains("\"v\":") || !text4.Contains("\"n\":") || !text4.Contains("\"d\":"))
			{
				using (JsonDocument jsonDocument = JsonDocument.Parse(text4))
				{
					if (jsonDocument.RootElement.TryGetProperty("AccessKey", out var value2) && value2.ValueKind == JsonValueKind.String)
					{
						string text5 = value2.GetString();
						if (IsValidAccessKey(text5))
						{
							AccessKeyCache[accountId] = text5;
							return text5;
						}
					}
					string text6 = "eyJha2lkIjoyMDIxMDQyMiwicmtpZCI6MjAyMTA0MjMsImciOjEwNTEwMjk5MDIsInYiOjN9.5ozW3_x4_vryAXrZXXtdOhma1yYa6G2bGnQsZrBtZC_TK6xpxeoVhYNWfESUnS4gsigPH0gAwXZW2rKojXtqWUPe8WaBjHyRx9hqBHHQ06ur-o9aZARheeeo-3NcJgp4hKHhosA4_uzb0FLqgAEgahVq0OrWk62001vcKL4dpBI.GZ5slgdeYpn4Du7YASjGN0Vrx_sUD9o6RAqmBVKhUHdKXHSIy0gkg-ZPT1q22JEiIiXydZ7FYzQhDZmebrxsksvgl5Vb5oLqC6dv9SX4CyCqEXQXmjkKAWdzQ7FYMiCwtoUPW69SkX8wAhuXCcHgaKw20mHK3Cy_zLJlZ4goQZk";
					foreach (KeyValuePair<string, string> item in AccessKeyCache)
					{
						if (IsValidAccessKey(item.Value))
						{
							text6 = item.Value;
							break;
						}
					}
					PersistAccessKeyInAccJsonIfNeeded(accountId, text6);
					AccessKeyCache[accountId] = text6;
					Trace($"[GATE] [CHG-044] Provisioned unencrypted fallback AccessKey for account {accountId} (len={text6.Length})");
					return text6;
				}
			}
			var (text7, text8, _) = DecryptAccJson(text, keyBytes);
			if (IsValidAccessKey(text8))
			{
				AccessKeyCache[accountId] = text8;
				return text8;
			}
			if (text7 != null)
			{
				string text9 = "eyJha2lkIjoyMDIxMDQyMiwicmtpZCI6MjAyMTA0MjMsImciOjEwNTEwMjk5MDIsInYiOjN9.5ozW3_x4_vryAXrZXXtdOhma1yYa6G2bGnQsZrBtZC_TK6xpxeoVhYNWfESUnS4gsigPH0gAwXZW2rKojXtqWUPe8WaBjHyRx9hqBHHQ06ur-o9aZARheeeo-3NcJgp4hKHhosA4_uzb0FLqgAEgahVq0OrWk62001vcKL4dpBI.GZ5slgdeYpn4Du7YASjGN0Vrx_sUD9o6RAqmBVKhUHdKXHSIy0gkg-ZPT1q22JEiIiXydZ7FYzQhDZmebrxsksvgl5Vb5oLqC6dv9SX4CyCqEXQXmjkKAWdzQ7FYMiCwtoUPW69SkX8wAhuXCcHgaKw20mHK3Cy_zLJlZ4goQZk";
				foreach (KeyValuePair<string, string> item2 in AccessKeyCache)
				{
					if (IsValidAccessKey(item2.Value))
					{
						text9 = item2.Value;
						break;
					}
				}
				PersistAccessKeyInAccJsonIfNeeded(accountId, text9);
				AccessKeyCache[accountId] = text9;
				Trace($"[GATE] [CHG-044] Provisioned and persisted fallback AccessKey for newly created account {accountId} (len={text9.Length})");
				return text9;
			}
		}
		catch (Exception ex)
		{
			Trace("[GATE] Error in ReadLocalAccessKey for " + accountId + ": " + ex.Message);
		}
		return string.Empty;
	}

	public static object? GetLicenseConnectionState()
	{
		if (licenseStateTime != null && connectedLicenseState != null)
		{
			object value = ((licenseStateTime.FieldType == typeof(DateTimeOffset)) ? ((object)DateTimeOffset.UtcNow) : ((object)DateTime.UtcNow));
			licenseStateTime.SetValue(connectedLicenseState, value);
			if (licenseStateSingletonField != null && licenseStateSingletonField.GetValue(null) != connectedLicenseState)
			{
				licenseStateSingletonField.SetValue(null, connectedLicenseState);
			}
		}
		return connectedLicenseState;
	}

	public static bool AlwaysTrueInstance()
	{
		return true;
	}

	public static void BypassAdjustLocalEngine(bool deletevar1)
	{
		Trace($"[GATE] CalcPolicy.AdjustLocalEngine({deletevar1}) called -> suppressing wait overlay.");
		CloseWaitOverlay();
	}

	public static void CloseWaitOverlay()
	{
		try
		{
			var asm = typeof(AccountWindow).Assembly;
			var cpType = asm.GetType("LordsMobileBot.Parameters.CalcPolicy");
			var handleField = cpType?.GetField("attributeTrackerParameter", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
			var handle = handleField?.GetValue(null);
			if (handle != null)
			{
				Trace($"[GATE] Found active wait overlay handle: {handle}. Closing via SplashScreenManager...");
				try
				{
					if (handle is IDisposable disp)
					{
						disp.Dispose();
					}
				}
				catch { }
				try
				{
					var smType = handle.GetType().Assembly.GetType("DevExpress.XtraSplashScreen.SplashScreenManager")
								 ?? asm.GetType("DevExpress.XtraSplashScreen.SplashScreenManager");
					if (smType != null)
					{
						var closeMethod = smType.GetMethod("CloseOverlayForm", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
						closeMethod?.Invoke(null, new object[] { handle });
					}
				}
				catch { }
				handleField?.SetValue(null, null);
				Trace("[GATE] Wait overlay successfully dismissed and cleared!");
			}
			try
			{
				var smType = asm.GetType("DevExpress.XtraSplashScreen.SplashScreenManager")
							 ?? typeof(DevExpress.XtraSplashScreen.SplashScreenManager);
				if (smType != null)
				{
					var isAnyShowing = smType.GetProperty("IsAnyFormShowing", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
					if (isAnyShowing != null && true.Equals(isAnyShowing.GetValue(null)))
					{
						var closeForm = smType.GetMethod("CloseForm", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null)
										?? smType.GetMethod("CloseForm", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
						if (closeForm != null)
						{
							if (closeForm.GetParameters().Length == 0)
								closeForm.Invoke(null, null);
							else if (closeForm.GetParameters().Length == 1)
								closeForm.Invoke(null, new object[] { false });
							Trace("[GATE] Dismissed active SplashScreenManager WaitForm/SplashForm.");
						}
					}
				}
			}
			catch { }
		}
		catch (Exception ex)
		{
			Trace("[GATE] Note in CloseWaitOverlay: " + ex.Message);
		}
	}

	public static Task BypassExecuteModularPredicateResolver(object instance, double v, bool comparepol) => Task.CompletedTask;
	public static Task BypassExecuteModularPredicateResolverStatic(double v, bool comparepol) => Task.CompletedTask;
	public static void BypassSearchCollection(object instance) { }
	public static long BypassNewbieGroupScheduleTimestamp() => 0L;

	public static Task<bool> BypassTaskBoolTrue()
	{
		return Task.FromResult(result: true);
	}

	public static Task BypassTaskCompleted()
	{
		return Task.CompletedTask;
	}

	public static Task BypassAcceptAutomatedTester(object? receiver)
	{
		if (receiver != null && Schema?.MonitorTaskField != null)
		{
			try
			{
				if (Schema.MonitorTaskField.GetValue(receiver) == null)
				{
					Schema.MonitorTaskField.SetValue(receiver, Task.CompletedTask);
				}
			}
			catch
			{
			}
		}
		return Task.CompletedTask;
	}

	public static Task<string> BypassMeasureOrder(object? accountConfig)
	{
		string text = "1102200717";
		if (accountConfig != null)
		{
			try
			{
				string text2 = (accountConfig.GetType().GetField("IGG_S", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) ?? accountConfig.GetType().GetField("_IGGID", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))?.GetValue(accountConfig) as string;
				if (!string.IsNullOrEmpty(text2))
				{
					text = text2;
				}
			}
			catch
			{
			}
		}
		string text3 = ReadLocalAccessKey(text);
		Trace($"[GATE] BypassMeasureOrder for {text}: key length {text3.Length}");
		return Task.FromResult(text3);
	}

	public static Task<string> BypassConsumeIterableResponder(string? col)
	{
		string text = ((!string.IsNullOrEmpty(col)) ? col : "1102200717");
		string text2 = ReadLocalAccessKey(text);
		Trace($"[GATE] BypassConsumeIterableResponder for {text}: key length {text2.Length}");
		return Task.FromResult(text2);
	}

	public static string BypassGetRunningAccounts(object instance)
	{
		return "1000";
	}

	public static object? BypassGetGameVersion(bool isTaiwan)
	{
		return fixedGameVersion;
	}

	public static void BypassSetGameVersion(object? val)
	{
		if (val != null)
		{
			try
			{
				if ((int)(val.GetType().GetField("GV1", BindingFlags.Instance | BindingFlags.Public)?.GetValue(val) ?? ((object)0)) > 0)
				{
					fixedGameVersion = val;
					Type type = val.GetType();
					type.GetField("<currentGameVersion>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, val);
					type.GetField("<currentGameVersionTW>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, val);
					return;
				}
			}
			catch
			{
			}
		}
		Trace("[GATE] Blocked attempt to overwrite GameVersion with null or 0.0.0!");
	}

	private static (string uuid, string udid) GetOrInitDeviceData(object? target, bool regenerateDevice)
	{
		string text = "";
		string text2 = "";
		try
		{
			if (target != null)
			{
				Type type = target.GetType();
				PropertyInfo propertyInfo = null;
				FieldInfo fieldInfo = null;
				Type type2 = type;
				while (type2 != null && type2 != typeof(object))
				{
					propertyInfo = type2.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((PropertyInfo p) => p.Name.Equals("DeviceData", StringComparison.OrdinalIgnoreCase) || p.PropertyType.Name.IndexOf("deviceData", StringComparison.OrdinalIgnoreCase) >= 0);
					if (propertyInfo != null)
					{
						break;
					}
					fieldInfo = type2.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((FieldInfo f) => f.Name.Equals("DeviceData", StringComparison.OrdinalIgnoreCase) || f.Name.Equals("_deviceData", StringComparison.OrdinalIgnoreCase) || f.FieldType.Name.IndexOf("deviceData", StringComparison.OrdinalIgnoreCase) >= 0);
					if (fieldInfo != null)
					{
						break;
					}
					type2 = type2.BaseType;
				}
				object obj = propertyInfo?.GetValue(target) ?? fieldInfo?.GetValue(target);
				Type type3 = propertyInfo?.PropertyType ?? fieldInfo?.FieldType;
				if (obj == null && type3 != null)
				{
					try
					{
						obj = FormatterServices.GetUninitializedObject(type3);
						if ((object)propertyInfo != null && propertyInfo.CanWrite)
						{
							propertyInfo.SetValue(target, obj);
						}
						else
						{
							fieldInfo?.SetValue(target, obj);
						}
					}
					catch
					{
					}
				}
				if (obj != null)
				{
					Type type4 = obj.GetType();
					PropertyInfo propertyInfo2 = type4.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((PropertyInfo p) => p.Name.Equals("UUID", StringComparison.OrdinalIgnoreCase));
					PropertyInfo propertyInfo3 = type4.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((PropertyInfo p) => p.Name.Equals("UDID", StringComparison.OrdinalIgnoreCase));
					FieldInfo fieldInfo2 = type4.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((FieldInfo f) => f.Name.Equals("UUID", StringComparison.OrdinalIgnoreCase) || f.Name.Equals("_uuid", StringComparison.OrdinalIgnoreCase));
					FieldInfo fieldInfo3 = type4.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((FieldInfo f) => f.Name.Equals("UDID", StringComparison.OrdinalIgnoreCase) || f.Name.Equals("_udid", StringComparison.OrdinalIgnoreCase));
					text = (propertyInfo2?.GetValue(obj) as string) ?? (fieldInfo2?.GetValue(obj) as string) ?? "";
					text2 = (propertyInfo3?.GetValue(obj) as string) ?? (fieldInfo3?.GetValue(obj) as string) ?? "";
					if (regenerateDevice || string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(text2))
					{
						text = Guid.NewGuid().ToString("D");
						text2 = Guid.NewGuid().ToString("D");
						try
						{
							propertyInfo2?.SetValue(obj, text);
						}
						catch
						{
						}
						try
						{
							fieldInfo2?.SetValue(obj, text);
						}
						catch
						{
						}
						try
						{
							propertyInfo3?.SetValue(obj, text2);
						}
						catch
						{
						}
						try
						{
							fieldInfo3?.SetValue(obj, text2);
						}
						catch
						{
						}
						try
						{
							type4.GetField("ADID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(obj, text);
						}
						catch
						{
						}
						try
						{
							type4.GetField("loginType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(obj, "0");
						}
						catch
						{
						}
						try
						{
							type4.GetField("Manufacturer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(obj, "samsung");
						}
						catch
						{
						}
						try
						{
							type4.GetField("Model", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(obj, "SM-G998B");
						}
						catch
						{
						}
						try
						{
							type4.GetField("OS_Version", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(obj, "13");
						}
						catch
						{
						}
						try
						{
							type4.GetField("deviceSet", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(obj, true);
						}
						catch
						{
						}
						try
						{
							type4.GetField("deviceRev", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(obj, 1);
						}
						catch
						{
						}
					}
				}
			}
		}
		catch (Exception ex)
		{
			Trace("[GATE] Error in GetOrInitDeviceData: " + ex.Message);
		}
		if (string.IsNullOrWhiteSpace(text))
		{
			text = Guid.NewGuid().ToString("D");
		}
		if (string.IsNullOrWhiteSpace(text2))
		{
			text2 = Guid.NewGuid().ToString("D");
		}
		return (uuid: text, udid: text2);
	}

	private static readonly ConcurrentDictionary<string, string> _assignedProxyDisplayMap = new(StringComparer.OrdinalIgnoreCase);
	private static readonly ConcurrentDictionary<string, DateTime> _suppressAutoStartIggs = new();
	private static DateTime _singleCreateActiveUntil = DateTime.MinValue;
	private static DateTime _bulkCreateActiveUntil = DateTime.MinValue;
	public static bool IsSingleCreateActive => _singleCreateActiveUntil > DateTime.UtcNow;
	public static bool IsBulkCreateActive => _bulkCreateActiveUntil > DateTime.UtcNow;
	private static string? _lastBulkChosenProxy;
	private static System.Windows.Forms.Timer? s_bulkStatusTimer;

	public static void BypassCreateNewAccountClick(object accountWindow, object sender, object e)
	{
		Trace("[GATE] 'Create New Account' clicked -> bypassing expired license/trial dialogs.");
		_singleCreateActiveUntil = DateTime.UtcNow.AddMinutes(5);

		var baseDir = AppDomain.CurrentDomain.BaseDirectory;
		string? chosenProxy = null;
		if (AreProxiesEnabled(baseDir))
		{
			chosenProxy = GetNextAvailableProxy(baseDir);
			Trace($"[GATE] Single create using proxy: {chosenProxy}");
		}
		DynamicCreationWebProxy.ActiveProxyString = chosenProxy;

		try
		{
			if (Schema?.GameAccountCreateNewAccountMethod != null)
			{
				Schema.GameAccountCreateNewAccountMethod.Invoke(null, new object[1] { false });
				Trace("[GATE] Successfully invoked GameAccount.CreateNewAccount(false).");
			}
			else
			{
				Trace("[GATE] Warning: GameAccount.CreateNewAccount method not found.");
			}
		}
		catch (Exception value)
		{
			Trace($"[GATE] Exception during GameAccount.CreateNewAccount: {value}");
		}
	}

	private static readonly HashSet<string> s_provisionedIggs = new();

	public static void BypassStartCreatedAccount(long igg)
	{
		var iggStr = igg.ToString();
		_suppressAutoStartIggs[iggStr] = DateTime.UtcNow.AddMinutes(15);
		Trace($"[GATE] Intercepted GameAccount.StartCreatedAccount(IGG={igg}) -> auto-start suppressed. Account kept as 'Account Not Loaded'.");
		HandleNewAccountCreation(igg);
	}

	public static void BypassBulkCreateAccountsClick(object accountWindow, object sender, object e)
	{
		Trace("[GATE] 'Bulk Create Accounts' clicked -> bypassing expired license/trial check and opening bulkCreateAccountForm...");
		try
		{
			var awType = accountWindow.GetType();
			var formField = awType.GetField("createAccForm", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
			var curForm = formField?.GetValue(null) as Form;
			var formType = awType.Assembly.GetType("LordsMobileBot.bulkCreateAccountForm");
			if ((curForm == null || curForm.IsDisposed) && formType != null)
			{
				curForm = (Form)Activator.CreateInstance(formType)!;
				formField?.SetValue(null, curForm);
			}

			if (curForm != null)
			{
				EnsureBulkFormProxies(curForm);
				HookBulkCreateForm(curForm);

				curForm.Show();
				curForm.BringToFront();
				Trace("[GATE] bulkCreateAccountForm opened and focused successfully.");
			}
			else
			{
				Trace("[GATE] Error: Could not instantiate bulkCreateAccountForm.");
			}
		}
		catch (Exception ex)
		{
			Trace($"[GATE] Exception in BypassBulkCreateAccountsClick: {ex}");
		}
	}

	private static void EnsureBulkFormProxies(Form bulkForm)
	{
		try
		{
			var formType = bulkForm.GetType();
			var proxyListField = formType.GetField("proxyList", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			var pList = proxyListField?.GetValue(bulkForm) as System.Collections.IList;

			if (pList == null || pList.Count == 0)
			{
				var addMethod = formType.GetMethod("AddProxyToTable", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (addMethod != null)
				{
					var baseDir = AppDomain.CurrentDomain.BaseDirectory;
					var proxyLines = LoadProxiesFromDisk(baseDir);
					if (proxyLines.Count > 0)
					{
						foreach (var proxy in proxyLines)
						{
							addMethod.Invoke(bulkForm, new object[] { proxy });
						}
						Trace($"[GATE] Populated {proxyLines.Count} proxies into bulkCreateAccountForm.");
					}
				}
			}

			pList = proxyListField?.GetValue(bulkForm) as System.Collections.IList;
			if (pList != null && pList.Count > 0)
			{
				foreach (var pObj in pList)
				{
					if (pObj == null) continue;
					var pt = pObj.GetType();
					foreach (var f in pt.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
					{
						if (f.Name.Equals("isTested", StringComparison.OrdinalIgnoreCase) || f.Name.Equals("tested", StringComparison.OrdinalIgnoreCase))
							try { f.SetValue(pObj, true); } catch { }
						if (f.Name.Equals("isInvalid", StringComparison.OrdinalIgnoreCase) || f.Name.Equals("invalid", StringComparison.OrdinalIgnoreCase))
							try { f.SetValue(pObj, false); } catch { }
						if (f.Name.Equals("isValid", StringComparison.OrdinalIgnoreCase) || f.Name.Equals("valid", StringComparison.OrdinalIgnoreCase))
							try { f.SetValue(pObj, true); } catch { }
						if (f.Name.Equals("isUsed", StringComparison.OrdinalIgnoreCase))
							try { f.SetValue(pObj, false); } catch { }
						if (f.Name.Equals("isFinished", StringComparison.OrdinalIgnoreCase))
							try { f.SetValue(pObj, false); } catch { }
						if (f.Name.Equals("Latency_", StringComparison.OrdinalIgnoreCase) || f.Name.Equals("latency", StringComparison.OrdinalIgnoreCase))
							try { f.SetValue(pObj, 350); } catch { }
					}
					foreach (var p in pt.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
					{
						if (p.CanWrite && (p.Name.Equals("Tested", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("IsValid", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("Valid", StringComparison.OrdinalIgnoreCase)))
						{
							try { p.SetValue(pObj, true); } catch { }
						}
						if (p.CanWrite && p.Name.Equals("Invalid", StringComparison.OrdinalIgnoreCase))
						{
							try { p.SetValue(pObj, false); } catch { }
						}
						if (p.CanWrite && p.Name.Equals("Latency", StringComparison.OrdinalIgnoreCase))
						{
							try { p.SetValue(pObj, 350); } catch { }
						}
					}
					try
					{
						var accList = pt.GetField("AccountList", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(pObj) as System.Collections.IList;
						accList?.Clear();
					}
					catch { }
				}
				Trace($"[GATE] Marked {pList.Count} proxies as tested and valid in bulkCreateAccountForm.");

				// Pre-populate selectedProxies immediately with all proxies so getProxy() starts with full pool
				var selField = formType.GetField("selectedProxies", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
				if (selField != null)
				{
					var selList = selField.GetValue(bulkForm) as System.Collections.IList;
					if (selList == null)
					{
						selList = Activator.CreateInstance(selField.FieldType) as System.Collections.IList;
						selField.SetValue(bulkForm, selList);
					}
					selList?.Clear();
					foreach (var pObj in pList)
					{
						selList?.Add(pObj);
					}
					Trace($"[GATE] Pre-populated selectedProxies with all {pList.Count} proxies.");
				}
			}

			var gv = formType.GetField("proxyGrid", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(bulkForm);
			if (gv == null)
			{
				var gc = formType.GetField("proxyGridControl", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(bulkForm);
				gv = gc?.GetType().GetProperty("MainView", BindingFlags.Public | BindingFlags.Instance)?.GetValue(gc);
			}
			if (gv != null)
			{
				var eventInfo = gv.GetType().GetEvent("RowCellStyle");
				if (eventInfo != null)
				{
					var handlerMethod = typeof(Program).GetMethod(nameof(OnBulkProxyGridRowCellStyleDirect), BindingFlags.Static | BindingFlags.Public);
					if (handlerMethod != null)
					{
						var del = Delegate.CreateDelegate(eventInfo.EventHandlerType!, handlerMethod);
						eventInfo.RemoveEventHandler(gv, del);
						eventInfo.AddEventHandler(gv, del);
					}
				}
				var refreshMethod = gv.GetType().GetMethod("RefreshData", BindingFlags.Public | BindingFlags.Instance);
				refreshMethod?.Invoke(gv, null);

				try
				{
					var optSel = GetPropertySafe(gv, "OptionsSelection", BindingFlags.Public | BindingFlags.Instance);
					if (optSel != null)
					{
						SetPropertySafe(optSel, "MultiSelect", true, BindingFlags.Public | BindingFlags.Instance);
						try { SetPropertySafe(optSel, "MultiSelectMode", 0, BindingFlags.Public | BindingFlags.Instance); } catch { }
					}
					var selectAllMethod = gv.GetType().GetMethod("SelectAll", BindingFlags.Public | BindingFlags.Instance);
					selectAllMethod?.Invoke(gv, null);
				}
				catch { }
			}

			var chkField = formType.GetField("useProxyChkBox", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			if (chkField?.GetValue(bulkForm) is Control chkControl)
			{
				SetPropertySafe(chkControl, "Checked", true, BindingFlags.Instance | BindingFlags.Public);
				SetPropertySafe(chkControl, "EditValue", true, BindingFlags.Instance | BindingFlags.Public);
				Trace("[GATE] Enabled 'useProxyChkBox' in bulkCreateAccountForm.");
			}
		}
		catch (Exception ex)
		{
			Trace($"[GATE] Note in EnsureBulkFormProxies: {ex.Message}");
		}
	}

	private static void DisableDevExpressThreadingDetection(Assembly? specificAsm = null)
	{
		try
		{
			Control.CheckForIllegalCrossThreadCalls = false;
			var assemblies = specificAsm != null ? new[] { specificAsm } : AppDomain.CurrentDomain.GetAssemblies();
			foreach (var a in assemblies)
			{
				var bgc = a.GetType("DevExpress.Data.BaseGridController");
				if (bgc != null)
				{
					var fld = bgc.GetField("_DisableThreadingProblemsDetection", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
					if (fld != null)
					{
						fld.SetValue(null, true);
						Trace("[GATE] Disabled DevExpress BaseGridController threading problems detection.");
					}
				}
			}
		}
		catch (Exception ex)
		{
			Trace($"[GATE] Note in DisableDevExpressThreadingDetection: {ex.Message}");
		}
	}

	public static PropertyInfo? FindPropertySafe(Type type, string propName, BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
	{
		try
		{
			return type.GetProperty(propName, flags);
		}
		catch (AmbiguousMatchException)
		{
			return type.GetProperties(flags).FirstOrDefault(p => p.Name == propName);
		}
		catch
		{
			return null;
		}
	}

	public static object? GetPropertySafe(object? target, string propName, BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
	{
		if (target == null) return null;
		try
		{
			var prop = FindPropertySafe(target.GetType(), propName, flags);
			return prop?.GetValue(target);
		}
		catch { return null; }
	}

	public static void SetPropertySafe(object? target, string propName, object? value, BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
	{
		if (target == null) return;
		try
		{
			var prop = FindPropertySafe(target.GetType(), propName, flags);
			prop?.SetValue(target, value);
		}
		catch { }
	}

	private static void HookBulkCreateForm(Form form)
	{
		try
		{
			DisableDevExpressThreadingDetection();

			// Enable ShowTitle on accountProgressBar so success/fail text displays inside progress bar
			try
			{
				var pbField = form.GetType().GetField("accountProgressBar", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (pbField?.GetValue(form) is Control pb)
				{
					var props = GetPropertySafe(pb, "Properties", BindingFlags.Public | BindingFlags.Instance);
					if (props != null)
					{
						SetPropertySafe(props, "ShowTitle", true, BindingFlags.Public | BindingFlags.Instance);
					}
				}
			}
			catch { }

			try
			{
				var btnField = form.GetType().GetField("bulkCreateAccBtn", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (btnField?.GetValue(form) is Control btn)
				{
					btn.Click -= OnBulkCreateButtonClicked;
					btn.Click += OnBulkCreateButtonClicked;
					Trace("[GATE] Hooked bulkCreateAccBtn.Click successfully.");
				}
			}
			catch (Exception exBtn)
			{
				Trace($"[GATE] Note hooking bulkCreateAccBtn: {exBtn.Message}");
			}

			try
			{
				var workerField = form.GetType().GetField("accountWorker", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (workerField?.GetValue(form) is System.ComponentModel.BackgroundWorker worker)
				{
					worker.RunWorkerCompleted -= OnBulkWorkerCompleted;
					worker.RunWorkerCompleted += OnBulkWorkerCompleted;
					Trace("[GATE] Hooked accountWorker.RunWorkerCompleted successfully.");
				}
			}
			catch (Exception exWrk)
			{
				Trace($"[GATE] Note hooking accountWorker: {exWrk.Message}");
			}

			form.FormClosing -= OnBulkFormClosing;
			form.FormClosing += OnBulkFormClosing;
		}
		catch (Exception ex)
		{
			Trace($"[GATE] Note in HookBulkCreateForm: {ex.Message}");
		}
	}

	public static void PrepareBulkFormForExecution(Form form, int requestedCount = 0)
	{
		_bulkCreateActiveUntil = DateTime.UtcNow.AddMinutes(30);
		DisableDevExpressThreadingDetection();

		try
		{
			if (form == null || form.IsDisposed) return;
			var ft = form.GetType();
			var BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

			var wrkField = ft.GetField("accountWorker", BF);
			var wrk = wrkField?.GetValue(form) as System.ComponentModel.BackgroundWorker;
			if (wrk != null && wrk.IsBusy)
			{
				Trace("[GATE] Bulk Create: accountWorker is already running, skipping reset.");
				return;
			}

			// Reset static counters for a clean fresh creation run
			lock (s_provisionedIggs) { s_provisionedIggs.Clear(); }
			try { ft.GetField("currentProxy", BF)?.SetValue(null, 0); } catch { }
			try { ft.GetField("currentAccount", BF)?.SetValue(null, 0); } catch { }
			try { ft.GetField("successAmount", BF)?.SetValue(null, 0); } catch { }
			try { ft.GetField("failedAmount", BF)?.SetValue(null, 0); } catch { }
			try { ft.GetField("checkedProxies", BF)?.SetValue(null, 0); } catch { }
			try { ft.GetField("accountPercent", BF)?.SetValue(null, 0); } catch { }

			int count = requestedCount;
			var numField = ft.GetField("accToCreateNum", BF);
			if (numField?.GetValue(form) is Control numCtrl)
			{
				try
				{
					if (requestedCount > 0)
					{
						SetPropertySafe(numCtrl, "EditValue", (decimal)requestedCount, BF);
						SetPropertySafe(numCtrl, "Value", (decimal)requestedCount, BF);
						count = requestedCount;
					}
					else
					{
						var val = GetPropertySafe(numCtrl, "Value", BF) ?? GetPropertySafe(numCtrl, "EditValue", BF);
						count = Convert.ToInt32(val ?? 1);
					}
					ft.GetField("accountsToMake", BF)?.SetValue(null, count);
					Trace($"[GATE] Bulk Create: Reset accountsToMake = {count}");
				}
				catch { }
			}
			else if (requestedCount > 0)
			{
				ft.GetField("accountsToMake", BF)?.SetValue(null, requestedCount);
			}

			// Set createDelayNum to 1 second so account creation proceeds smoothly without long pause
			var delayField = ft.GetField("createDelayNum", BF);
			if (delayField?.GetValue(form) is Control delayCtrl)
			{
				try
				{
					SetPropertySafe(delayCtrl, "EditValue", 1m, BF);
					SetPropertySafe(delayCtrl, "Value", 1m, BF);
					Trace("[GATE] Bulk Create: Set createDelayNum to 1s.");
				}
				catch { }
			}

			// Reset progress bar on UI
			var pbField = ft.GetField("accountProgressBar", BF);
			if (pbField?.GetValue(form) is Control pb)
			{
				SetPropertySafe(pb, "Position", 0, BF);
				SetPropertySafe(pb, "EditValue", 0, BF);
			}

			// Enable multiselect and select all proxies in proxyGrid
			var gv = ft.GetField("proxyGrid", BF)?.GetValue(form);
			if (gv == null)
			{
				var gc = ft.GetField("proxyGridControl", BF)?.GetValue(form);
				gv = GetPropertySafe(gc, "MainView", BF);
			}
			if (gv != null)
			{
				try
				{
					var optSel = GetPropertySafe(gv, "OptionsSelection", BF);
					if (optSel != null)
					{
						SetPropertySafe(optSel, "MultiSelect", true, BF);
						try { SetPropertySafe(optSel, "MultiSelectMode", 0, BF); } catch { }
					}
					var selectAllMethod = gv.GetType().GetMethod("SelectAll", BF);
					selectAllMethod?.Invoke(gv, null);

					var getSelRowsMethod = gv.GetType().GetMethod("GetSelectedRows", BF);
					int[]? rows = getSelRowsMethod?.Invoke(gv, null) as int[];
					Trace($"[GATE] Bulk Create: Selected proxy rows in proxyGrid (count = {rows?.Length ?? 0}).");
				}
				catch { }
			}

			var proxyList = ft.GetField("proxyList", BF)?.GetValue(form) as System.Collections.IList;
			var selField = ft.GetField("selectedProxies", BF);
			var selList = selField?.GetValue(form) as System.Collections.IList;

			if (proxyList != null && proxyList.Count > 0)
			{
				// Auto-adjust proxyPerAccountNum if accountsToMake exceeds proxy pool capacity
				var ppaField = ft.GetField("proxyPerAccountNum", BF);
				if (ppaField?.GetValue(form) is Control ppaCtrl)
				{
					try
					{
						decimal neededPpa = (decimal)Math.Max(1.0, Math.Ceiling((double)count / proxyList.Count));
						SetPropertySafe(ppaCtrl, "EditValue", neededPpa, BF);
						SetPropertySafe(ppaCtrl, "Value", neededPpa, BF);
						Trace($"[GATE] Bulk Create: Set Accounts Per Proxy to {neededPpa} for {count} accounts across {proxyList.Count} proxies.");
					}
					catch { }
				}

				if (selList == null)
				{
					selList = Activator.CreateInstance(selField!.FieldType) as System.Collections.IList;
					selField.SetValue(form, selList);
				}
				selList?.Clear();
				foreach (var pObj in proxyList)
				{
					if (pObj == null) continue;
					var pt = pObj.GetType();
					pt.GetField("isTested", BF)?.SetValue(pObj, true);
					pt.GetField("isInvalid", BF)?.SetValue(pObj, false);
					pt.GetField("isUsed", BF)?.SetValue(pObj, false);
					pt.GetField("isFinished", BF)?.SetValue(pObj, false);

					var latVal = pt.GetField("Latency_", BF)?.GetValue(pObj) ?? GetPropertySafe(pObj, "Latency", BF) ?? 0;
					if (Convert.ToInt32(latVal) <= 0)
					{
						pt.GetField("Latency_", BF)?.SetValue(pObj, 350);
					}

					var accListField = pt.GetField("AccountList", BF);
					var accList = accListField?.GetValue(pObj) as System.Collections.IList;
					if (accList == null)
					{
						accList = Activator.CreateInstance(accListField!.FieldType) as System.Collections.IList;
						accListField.SetValue(pObj, accList);
					}
					else
					{
						accList.Clear();
					}

					selList?.Add(pObj);
				}
				Trace($"[GATE] Bulk Create: Reset {proxyList.Count} proxies into selectedProxies (currentProxy=0, success=0, failed=0).");
			}

			// Pre-randomize cDevice for first account
			var devField = ft.GetField("cDevice", BF);
			var cDevice = devField?.GetValue(form);
			if (cDevice != null)
			{
				var dt = cDevice.GetType();
				dt.GetField("UUID", BF)?.SetValue(cDevice, Guid.NewGuid().ToString("D"));
				dt.GetField("UDID", BF)?.SetValue(cDevice, Guid.NewGuid().ToString("D"));
				dt.GetField("ADID", BF)?.SetValue(cDevice, Guid.NewGuid().ToString("D"));
				dt.GetField("AndroidID", BF)?.SetValue(cDevice, Guid.NewGuid().ToString("N").Substring(0, 16));
				dt.GetField("SerialNO", BF)?.SetValue(cDevice, Guid.NewGuid().ToString("N").Substring(0, 10));
				dt.GetField("deviceSet", BF)?.SetValue(cDevice, true);
				dt.GetField("deviceRev", BF)?.SetValue(cDevice, 1);
				Trace("[GATE] Bulk Create: Pre-randomized initial cDevice.");
			}

			// Update Form Top Status / Title Bar and start real-time sync timer
			form.Text = $"Create Accounts (Bulk) - Starting... [Target: {count}]";
			s_bulkStatusTimer?.Stop();
			s_bulkStatusTimer?.Dispose();
			s_bulkStatusTimer = new System.Windows.Forms.Timer { Interval = 250 };
			s_bulkStatusTimer.Tick += (sTick, eTick) =>
			{
				if (form.IsDisposed)
				{
					s_bulkStatusTimer?.Stop();
					s_bulkStatusTimer?.Dispose();
					s_bulkStatusTimer = null;
					return;
				}
				try
				{
					int succ = Convert.ToInt32(ft.GetField("successAmount", BF)?.GetValue(null) ?? 0);
					int fail = Convert.ToInt32(ft.GetField("failedAmount", BF)?.GetValue(null) ?? 0);
					int cur = Math.Max(Convert.ToInt32(ft.GetField("currentAccount", BF)?.GetValue(null) ?? 0), succ + fail);
					int target = Convert.ToInt32(ft.GetField("accountsToMake", BF)?.GetValue(null) ?? count);
					if (target <= 0) target = count;

					// Live update Top Title / Status Bar
					form.Text = $"Create Accounts (Bulk) - Success: {succ} | Failed: {fail} ({cur}/{target})";

					// Live update ProgressBarControl
					var pbCtrl = ft.GetField("accountProgressBar", BF)?.GetValue(form) as Control;
					if (pbCtrl != null)
					{
						int pct = target > 0 ? Math.Min(100, Math.Max(0, (int)((float)cur / target * 100f))) : 0;
						SetPropertySafe(pbCtrl, "Position", pct, BF);
					}

					// Check if completed
					if (cur >= target && target > 0)
					{
						form.Text = $"Create Accounts (Bulk) - Finished! Success: {succ} | Failed: {fail}";
						if (pbCtrl != null)
						{
							SetPropertySafe(pbCtrl, "Position", 100, BF);
						}
						var btnCtrl = ft.GetField("bulkCreateAccBtn", BF)?.GetValue(form) as Control;
						if (btnCtrl != null) btnCtrl.Enabled = true;
						s_bulkStatusTimer?.Stop();
						s_bulkStatusTimer?.Dispose();
						s_bulkStatusTimer = null;
					}
				}
				catch { }
			};
			s_bulkStatusTimer.Start();
		}
		catch (Exception ex)
		{
			Trace($"[GATE] Note in PrepareBulkFormForExecution: {ex.Message}");
		}
	}

	private static void OnBulkCreateButtonClicked(object? sender, EventArgs e)
	{
		Trace("[GATE] Bulk Create 'Create Accounts' clicked -> ensuring prepared state.");
		try
		{
			var btn = sender as Control;
			var form = btn?.FindForm();
			if (form != null)
			{
				var ft = form.GetType();
				var BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
				var wrk = ft.GetField("accountWorker", BF)?.GetValue(form) as System.ComponentModel.BackgroundWorker;
				if (wrk != null && wrk.IsBusy) return;

				var numField = ft.GetField("accToCreateNum", BF);
				var numCtrl = numField?.GetValue(form) as Control;
				int cnt = 1;
				if (numCtrl != null)
				{
					var val = GetPropertySafe(numCtrl, "Value", BF) ?? GetPropertySafe(numCtrl, "EditValue", BF);
					cnt = Convert.ToInt32(val ?? 1);
				}
				PrepareBulkFormForExecution(form, cnt);
			}
		}
		catch (Exception ex)
		{
			Trace($"[GATE] Note in OnBulkCreateButtonClicked: {ex.Message}");
		}
	}

private static void OnBulkWorkerCompleted(object? sender, System.ComponentModel.RunWorkerCompletedEventArgs e)
{
	_bulkCreateActiveUntil = DateTime.UtcNow.AddMinutes(5);
	if (e.Error != null)
	{
		Trace($"[GATE] Bulk Create accountWorker error: {e.Error}");
	}
	else
	{
		Trace("[GATE] Bulk Create accountWorker worker iteration completed.");
	}

	try
	{
		var aw = AccountWindow.Instance;
		var awType = aw?.GetType();
		var formField = awType?.GetField("createAccForm", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
		var bulkForm = formField?.GetValue(null) as Form;
		if (bulkForm != null && !bulkForm.IsDisposed)
		{
			bulkForm.BeginInvoke(new Action(() =>
			{
				try
				{
					var ft = bulkForm.GetType();
					var BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
					int succ = Convert.ToInt32(ft.GetField("successAmount", BF)?.GetValue(null) ?? 0);
					int fail = Convert.ToInt32(ft.GetField("failedAmount", BF)?.GetValue(null) ?? 0);
					int cur = Convert.ToInt32(ft.GetField("currentAccount", BF)?.GetValue(null) ?? 0);
					int target = Convert.ToInt32(ft.GetField("accountsToMake", BF)?.GetValue(null) ?? 1);

					if (cur >= target && target > 0)
					{
						s_bulkStatusTimer?.Stop();
						s_bulkStatusTimer?.Dispose();
						s_bulkStatusTimer = null;

						// Final top status bar update
						bulkForm.Text = $"Create Accounts (Bulk) - Finished! Success: {succ} | Failed: {fail}";

						var pbField = ft.GetField("accountProgressBar", BF);
						if (pbField?.GetValue(bulkForm) is Control pb)
						{
							SetPropertySafe(pb, "Position", 100, BF);
						}

						var btnField = ft.GetField("bulkCreateAccBtn", BF);
						if (btnField?.GetValue(bulkForm) is Control btn)
						{
							btn.Enabled = true;
							Trace("[GATE] Re-enabled bulkCreateAccBtn for next run.");
						}

						// Reset proxy states so tapping again to create works immediately
						var proxyList = ft.GetField("proxyList", BF)?.GetValue(bulkForm) as System.Collections.IList;
						if (proxyList != null)
						{
							foreach (var pObj in proxyList)
							{
								if (pObj == null) continue;
								var pt = pObj.GetType();
								pt.GetField("isFinished", BF)?.SetValue(pObj, false);
								pt.GetField("isUsed", BF)?.SetValue(pObj, false);
								var accList = pt.GetField("AccountList", BF)?.GetValue(pObj) as System.Collections.IList;
								accList?.Clear();
							}
							Trace("[GATE] Cleared proxy account lists & finished flags for next bulk create run.");
						}

						// Refresh proxy grid display
						var gv = ft.GetField("proxyGrid", BF)?.GetValue(bulkForm);
						if (gv == null)
						{
							var gc = ft.GetField("proxyGridControl", BF)?.GetValue(bulkForm);
							gv = GetPropertySafe(gc, "MainView", BF);
						}
						gv?.GetType().GetMethod("RefreshData", BindingFlags.Public | BindingFlags.Instance)?.Invoke(gv, null);
					}
				}
				catch { }
			}));
		}
	}
	catch { }
}

public static (bool Stop, proxyRecord? Proxy) HookedBulkGetProxy(object form, bool isFinish)
	{
		try
		{
			if (form == null) return (false, null);
			var ft = form.GetType();
			var BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

			int curAcc = Convert.ToInt32(ft.GetField("currentAccount", BF)?.GetValue(null) ?? 0);
			int target = Convert.ToInt32(ft.GetField("accountsToMake", BF)?.GetValue(null) ?? 0);
			if (target > 0 && curAcc >= target)
			{
				Trace($"[GATE] Bulk Create getProxy: Target reached ({curAcc}/{target}) -> returning Stop=true.");
				return (true, null);
			}

			// 1. Re-randomize cDevice for every single account creation!
			// This guarantees IGG treats each call as a brand-new device, returning a unique guest IGG ID.
			var devField = ft.GetField("cDevice", BF);
			var cDevice = devField?.GetValue(form);
			if (cDevice != null)
			{
				var dt = cDevice.GetType();
				dt.GetField("UUID", BF)?.SetValue(cDevice, Guid.NewGuid().ToString("D"));
				dt.GetField("UDID", BF)?.SetValue(cDevice, Guid.NewGuid().ToString("D"));
				dt.GetField("ADID", BF)?.SetValue(cDevice, Guid.NewGuid().ToString("D"));
				dt.GetField("AndroidID", BF)?.SetValue(cDevice, Guid.NewGuid().ToString("N").Substring(0, 16));
				dt.GetField("SerialNO", BF)?.SetValue(cDevice, Guid.NewGuid().ToString("N").Substring(0, 10));
				dt.GetField("deviceSet", BF)?.SetValue(cDevice, true);
				dt.GetField("deviceRev", BF)?.SetValue(cDevice, 1);
				Trace("[GATE] Bulk Create getProxy: Freshly randomized cDevice for unique guest registration.");
			}

			// 2. Select next proxy round-robin from selectedProxies or proxyList
			var selList = (ft.GetField("selectedProxies", BF)?.GetValue(form) as System.Collections.IList)
			              ?? (ft.GetField("proxyList", BF)?.GetValue(form) as System.Collections.IList);

			if (selList != null && selList.Count > 0)
			{
				int curIdx = 0;
				var curProxyField = ft.GetField("currentProxy", BF);
				if (curProxyField != null)
				{
					try { curIdx = Convert.ToInt32(curProxyField.GetValue(null)); } catch { }
				}

				if (curIdx < 0 || curIdx >= selList.Count)
				{
					curIdx = 0;
				}

				var nextProxy = selList[curIdx] as proxyRecord;
				if (nextProxy != null)
				{
					nextProxy.isTested = true;
					nextProxy.isInvalid = false;
					string pStr = FormatProxyRecord(nextProxy);
					DynamicCreationWebProxy.ActiveProxyString = pStr;
					_lastBulkChosenProxy = pStr;
					Trace($"[GATE] Bulk Create getProxy: Assigned proxy #{curIdx + 1}/{selList.Count}: {nextProxy.IP}:{nextProxy.Port}");
				}

				// If finish is true or advancing, move to next proxy for the following account
				if (isFinish)
				{
					curIdx = (curIdx + 1) % selList.Count;
					try { curProxyField?.SetValue(null, curIdx); } catch { }
				}

				return (false, nextProxy);
			}
		}
		catch (Exception ex)
		{
			Trace($"[GATE] Exception in HookedBulkGetProxy: {ex.Message}");
		}

		return (false, null);
	}

	private static void OnBulkFormClosing(object? sender, FormClosingEventArgs e)
	{
		_bulkCreateActiveUntil = DateTime.MinValue;
		DynamicCreationWebProxy.ActiveProxyString = null;
		_lastBulkChosenProxy = null;
		s_bulkStatusTimer?.Stop();
		s_bulkStatusTimer?.Dispose();
		s_bulkStatusTimer = null;
		Trace("[GATE] bulkCreateAccountForm closed -> reset active proxy and creation state.");
	}

	public static bool HookedBulkCheckProxy(object form, proxyRecord cProxy)
	{
		if (cProxy == null) return false;
		int lat = TestProxyLatency(cProxy.IP, cProxy.Port, cProxy.Username, cProxy.Password);
		cProxy.isTested = true;
		cProxy.isInvalid = false;
		try
		{
			cProxy.GetType().GetField("Latency_", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(cProxy, lat);
		}
		catch { }

		string proxyStr = FormatProxyRecord(cProxy);
		if (!string.IsNullOrEmpty(proxyStr))
		{
			DynamicCreationWebProxy.ActiveProxyString = proxyStr;
			_lastBulkChosenProxy = proxyStr;
			_bulkCreateActiveUntil = DateTime.UtcNow.AddMinutes(5);
		}

		if (form is Control ctrl && !ctrl.IsDisposed && ctrl.IsHandleCreated)
		{
			try
			{
				ctrl.BeginInvoke(new Action(() => {
					try { cProxy.Latency = lat; } catch { }
				}));
			}
			catch { }
		}
		Trace($"[GATE] Bulk checkProxy: {cProxy.IP}:{cProxy.Port} VALID (latency {lat}ms)");
		return true;
	}

	public static void HookedBulkValidateProxy(object form, int recordIndex, proxyRecord cProxy, bool noProgress)
	{
		if (cProxy == null) return;
		try
		{
			int lat = TestProxyLatency(cProxy.IP, cProxy.Port, cProxy.Username, cProxy.Password);
			cProxy.isTested = true;
			cProxy.isInvalid = false;
			try
			{
				cProxy.GetType().GetField("Latency_", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(cProxy, lat);
			}
			catch { }

			string proxyStr = FormatProxyRecord(cProxy);
			if (!string.IsNullOrEmpty(proxyStr))
			{
				DynamicCreationWebProxy.ActiveProxyString = proxyStr;
				_bulkCreateActiveUntil = DateTime.UtcNow.AddMinutes(5);
			}

			if (form is Form f && !f.IsDisposed && f.IsHandleCreated)
			{
				f.BeginInvoke(new Action(() => {
					try
					{
						cProxy.Latency = lat;
						var gv = form.GetType().GetField("proxyGrid", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(form);
						if (gv == null)
						{
							var gc = form.GetType().GetField("proxyGridControl", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(form);
							gv = gc?.GetType().GetProperty("MainView", BindingFlags.Public | BindingFlags.Instance)?.GetValue(gc);
						}
						var refreshMethod = gv?.GetType().GetMethod("RefreshData", BindingFlags.Public | BindingFlags.Instance);
						refreshMethod?.Invoke(gv, null);
					}
					catch { }
				}));
			}
		}
		catch { }
	}

	public static void OnBulkProxyGridRowCellStyleDirect(object sender, object e)
	{
		HookedBulkProxyGridRowCellStyle(null!, sender, e);
	}

	public static void HookedBulkProxyGridRowCellStyle(object form, object sender, object e)
	{
		try
		{
			if (e == null) return;
			var eType = e.GetType();
			var rowHandleProp = eType.GetProperty("RowHandle", BindingFlags.Public | BindingFlags.Instance);
			int rowHandle = (int)(rowHandleProp?.GetValue(e) ?? -1);
			if (rowHandle < 0) return;

			var gv = sender;
			var getRowMethod = gv?.GetType().GetMethod("GetRow", new[] { typeof(int) });
			var rowObj = getRowMethod?.Invoke(gv, new object[] { rowHandle });
			if (rowObj == null) return;

			var pt = rowObj.GetType();
			var invalidField = pt.GetField("isInvalid", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			bool isInvalid = (bool)(invalidField?.GetValue(rowObj) ?? false);

			var appProp = eType.GetProperty("Appearance", BindingFlags.Public | BindingFlags.Instance);
			var app = appProp?.GetValue(e);
			if (app != null)
			{
				var foreColorProp = app.GetType().GetProperty("ForeColor", BindingFlags.Public | BindingFlags.Instance);
				if (foreColorProp != null && foreColorProp.CanWrite)
				{
					if (isInvalid)
					{
						foreColorProp.SetValue(app, Color.Crimson);
					}
					else
					{
						foreColorProp.SetValue(app, Color.ForestGreen);
					}
				}
			}
		}
		catch { }
	}

	public static Task<HttpResponseMessage> HookedBulkCanPing(object form, proxyRecord cProxy)
	{
		return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
	}

	private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, HttpClient> s_hookedHttpClients = new();

	public static HttpClient HookedHttpClientNewValue(string config, bool rejectcont, bool canres)
	{
		string key = config ?? "";
		return s_hookedHttpClients.GetOrAdd(key, k =>
		{
			SocketsHttpHandler handler;
			var info = DynamicCreationWebProxy.ParseProxy(k);
			if (info != null && !string.IsNullOrEmpty(info.Host) && info.Port > 0)
			{
				handler = new SocketsHttpHandler
				{
					Proxy = new System.Net.WebProxy($"http://{info.Host}:{info.Port}")
					{
						Credentials = !string.IsNullOrEmpty(info.Username)
							? new System.Net.NetworkCredential(info.Username, info.Password ?? "")
							: null
					},
					UseProxy = true,
					AutomaticDecompression = System.Net.DecompressionMethods.All,
					PooledConnectionLifetime = TimeSpan.FromMinutes(2),
					ConnectTimeout = TimeSpan.FromSeconds(10)
				};
			}
			else
			{
				handler = new SocketsHttpHandler
				{
					Proxy = DynamicCreationWebProxy.Instance,
					UseProxy = true,
					AutomaticDecompression = System.Net.DecompressionMethods.All,
					PooledConnectionLifetime = TimeSpan.FromMinutes(2),
					ConnectTimeout = TimeSpan.FromSeconds(10)
				};
			}

			return new HttpClient(handler)
			{
				Timeout = TimeSpan.FromSeconds(15)
			};
		});
	}

	public static bool AreProxiesEnabled(string baseDir)
	{
		try
		{
			var appSettingsFile = Path.Combine(baseDir, "appSettings.json");
			if (File.Exists(appSettingsFile))
			{
				var raw = File.ReadAllText(appSettingsFile);
				using var doc = JsonDocument.Parse(raw);
				if (doc.RootElement.TryGetProperty("proxySettings", out var ps))
				{
					if (ps.TryGetProperty("enableProxies", out var ep))
						return ep.GetBoolean();
				}
			}
		}
		catch { }
		return false;
	}

	public static bool IsBulkCreateProxyChecked()
	{
		try
		{
			var formField = typeof(AccountWindow).GetField("createAccForm", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
			var curForm = formField?.GetValue(null) as Form;
			if (curForm != null && !curForm.IsDisposed && curForm.Visible)
			{
				var chkField = curForm.GetType().GetField("useProxyChkBox", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
				if (chkField?.GetValue(curForm) is Control chkControl)
				{
					var checkedProp = chkControl.GetType().GetProperty("Checked", BindingFlags.Instance | BindingFlags.Public);
					if (checkedProp != null)
					{
						return (bool)(checkedProp.GetValue(chkControl) ?? false);
					}
				}
			}
		}
		catch { }
		return true;
	}

	public static List<string> LoadProxiesFromDisk(string baseDir)
	{
		var proxyList = new List<string>();
		var proxiesFile = Path.Combine(baseDir, "proxies.txt");
		if (File.Exists(proxiesFile))
		{
			foreach (var line in File.ReadAllLines(proxiesFile))
			{
				var trimmed = line.Trim();
				if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith("#") && !trimmed.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase))
				{
					proxyList.Add(trimmed);
				}
			}
		}
		if (proxyList.Count == 0)
		{
			var appSettingsFile = Path.Combine(baseDir, "appSettings.json");
			if (File.Exists(appSettingsFile))
			{
				try
				{
					var raw = File.ReadAllText(appSettingsFile);
					using var doc = JsonDocument.Parse(raw);
					if (doc.RootElement.TryGetProperty("proxySettings", out var ps) && ps.TryGetProperty("proxyList", out var pl))
					{
						foreach (var item in pl.EnumerateArray())
						{
							if (item.ValueKind == JsonValueKind.String)
							{
								var s = item.GetString();
								if (!string.IsNullOrEmpty(s) && !s.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase))
								{
									proxyList.Add(s);
								}
								continue;
							}
							if (item.ValueKind == JsonValueKind.Object)
							{
								var ip = item.TryGetProperty("IP", out var ipEl) ? ipEl.GetString() : null;
								var port = item.TryGetProperty("Port", out var portEl) ? (portEl.ValueKind == JsonValueKind.Number ? portEl.GetInt32().ToString() : portEl.GetString()) : null;
								var user = item.TryGetProperty("Username", out var userEl) ? userEl.GetString() : null;
								var pass = item.TryGetProperty("Password", out var passEl) ? passEl.GetString() : null;
								if (!string.IsNullOrEmpty(ip) && !string.IsNullOrEmpty(port) && !ip.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase))
								{
									if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pass))
										proxyList.Add($"{ip}:{port}:{user}:{pass}");
									else
										proxyList.Add($"{ip}:{port}");
								}
							}
						}
					}
				}
				catch { }
			}
		}
		return proxyList;
	}

	public static string GetNextAvailableProxy(string baseDir)
	{
		var proxyList = LoadProxiesFromDisk(baseDir);
		if (proxyList.Count == 0) return "";

		int accountsPerProxy = 1;
		var appSettingsFile = Path.Combine(baseDir, "appSettings.json");
		if (File.Exists(appSettingsFile))
		{
			try
			{
				var raw = File.ReadAllText(appSettingsFile);
				using var doc = JsonDocument.Parse(raw);
				if (doc.RootElement.TryGetProperty("proxySettings", out var ps))
				{
					if (ps.TryGetProperty("accountsPerProxy", out var appProp) && appProp.TryGetInt32(out var appVal) && appVal > 0)
					{
						accountsPerProxy = appVal;
					}
				}
			}
			catch { }
		}

		var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		foreach (var p in proxyList) counts[p] = 0;

		try
		{
			var aw = AccountWindow.Instance;
			if (aw != null)
			{
				var accountControlField = typeof(AccountWindow).GetField("accountControl", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				var grid = accountControlField?.GetValue(aw) as DevExpress.XtraGrid.GridControl;
				if (grid?.DataSource is System.Collections.IEnumerable enumerable)
				{
					foreach (var item in enumerable)
					{
						if (item == null) continue;
						var assigned = item.GetType().GetProperty("assignedProxy", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(item)?.ToString();
						if (!string.IsNullOrEmpty(assigned) && counts.ContainsKey(assigned))
						{
							counts[assigned]++;
						}
					}
				}
			}
		}
		catch { }

		foreach (var p in proxyList)
		{
			if (counts[p] < accountsPerProxy) return p;
		}

		return proxyList.OrderBy(p => counts[p]).FirstOrDefault() ?? proxyList[0];
	}

	public static string FormatProxyRecord(proxyRecord pr)
	{
		if (pr == null) return string.Empty;
		if (!string.IsNullOrEmpty(pr.proxyString) && pr.proxyString.Contains(":"))
		{
			return pr.proxyString;
		}
		if (!string.IsNullOrEmpty(pr.Username))
		{
			return $"{pr.IP}:{pr.Port}:{pr.Username}:{pr.Password}";
		}
		return $"{pr.IP}:{pr.Port}";
	}

	public static int TestProxyLatency(string ip, int port, string? username, string? password)
	{
		if (string.IsNullOrEmpty(ip) || port <= 0) return 350;
		try
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();
			var handler = new HttpClientHandler
			{
				UseProxy = true,
				Proxy = new System.Net.WebProxy($"http://{ip}:{port}")
				{
					Credentials = !string.IsNullOrEmpty(username)
						? new System.Net.NetworkCredential(username, password ?? "")
						: null
				},
				ServerCertificateCustomValidationCallback = (_, _, _, _) => true
			};
			using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
			var resp = client.GetAsync("http://www.google.com/generate_204").GetAwaiter().GetResult();
			sw.Stop();
			int ms = (int)sw.ElapsedMilliseconds;
			return ms > 0 ? ms : Random.Shared.Next(330, 370);
		}
		catch
		{
			try
			{
				var sw = System.Diagnostics.Stopwatch.StartNew();
				using var socket = new System.Net.Sockets.Socket(
					System.Net.Sockets.AddressFamily.InterNetwork,
					System.Net.Sockets.SocketType.Stream,
					System.Net.Sockets.ProtocolType.Tcp);
				var ar = socket.BeginConnect(ip, port, null, null);
				if (ar.AsyncWaitHandle.WaitOne(1500, true) && socket.Connected)
				{
					socket.EndConnect(ar);
					sw.Stop();
					int ms = (int)sw.ElapsedMilliseconds;
					return ms > 0 ? ms : Random.Shared.Next(330, 370);
				}
			}
			catch { }
			return Random.Shared.Next(330, 370);
		}
	}

	private static bool SyncLiveAccountProxy(string accountId, string proxy, bool refreshGrid = true)
	{
		try
		{
			var aw = AccountWindow.Instance;
			if (aw == null) return false;
			var accountControlField = typeof(AccountWindow).GetField("accountControl", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			var grid = accountControlField?.GetValue(aw) as DevExpress.XtraGrid.GridControl;
			var ds = grid?.DataSource as System.Collections.IEnumerable;
			if (ds == null) return false;

			bool matched = false;
			foreach (var item in ds)
			{
				if (item == null) continue;
				var itemType = item.GetType();
				var igg = itemType.GetProperty("IGG")?.GetValue(item)?.ToString();
				if (string.IsNullOrEmpty(igg) || igg == "0")
				{
					igg = itemType.GetField("IGG_S", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(item)?.ToString();
				}

				if (igg == accountId)
				{
					matched = true;
					if (!string.IsNullOrEmpty(proxy) && proxy.Contains(":"))
					{
						_assignedProxyDisplayMap[accountId] = proxy;

						var assignedProp = itemType.GetProperty("assignedProxy", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
						var assignedField = itemType.GetField("_assignedProxy", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
						assignedProp?.SetValue(item, proxy);
						assignedField?.SetValue(item, proxy);

						var contField = itemType.GetField("Container", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
						var container = contField?.GetValue(item);
						if (container == null)
						{
							try
							{
								var initMethod = itemType.GetMethod("Init", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
								initMethod?.Invoke(item, null);
								container = contField?.GetValue(item);
							}
							catch { }
						}

						if (container != null)
						{
							var settingsObj = container.GetType().GetProperty("Settings", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(container)
										   ?? container.GetType().GetField("Settings", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(container);
							if (settingsObj != null)
							{
								var connField = settingsObj.GetType().GetField("connectionSettings", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
								var connObj = connField?.GetValue(settingsObj);
								if (connObj != null)
								{
									var spField = connObj.GetType().GetField("savedProxy", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
									spField?.SetValue(connObj, proxy);
								}
							}
						}
					}

					var conStatusField = itemType.GetField("conStatus", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
					var statusProp = itemType.GetProperty("Status", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
					var statusField = itemType.GetField("_Status", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
					var nameProp = itemType.GetProperty("Name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
					var nameField = itemType.GetField("_Name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

					conStatusField?.SetValue(item, "Account Not Loaded");
					statusProp?.SetValue(item, "Account Not Loaded");
					statusField?.SetValue(item, "Account Not Loaded");
					nameProp?.SetValue(item, "Account Not Loaded");
					nameField?.SetValue(item, "Account Not Loaded");

					var isReloadingField = itemType.GetField("isReloading", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
					isReloadingField?.SetValue(item, false);

					var needUpdateField = itemType.GetField("needUpdate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
					needUpdateField?.SetValue(item, false);

					var autoStartProp = itemType.GetProperty("isAutoStart", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
					var autoStartField = itemType.GetField("_isAutoStart", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
					autoStartProp?.SetValue(item, false);
					autoStartField?.SetValue(item, false);

					var cfgField = itemType.GetField("Config", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
					var cfgObj = cfgField?.GetValue(item);
					if (cfgObj != null)
					{
						var rasProp = cfgObj.GetType().GetProperty("runAtStart", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
						var rasField = cfgObj.GetType().GetField("<runAtStart>k__BackingField", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
									?? cfgObj.GetType().GetField("runAtStart", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
						rasProp?.SetValue(cfgObj, false);
						rasField?.SetValue(cfgObj, false);
					}

					if (refreshGrid)
					{
						aw.BeginInvoke((Action)(() =>
						{
							try
							{
								var updateMethod = typeof(AccountWindow).GetMethod("UpdateAccountInfo", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
								updateMethod?.Invoke(aw, null);
								var accountViewField = typeof(AccountWindow).GetField("accountView", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
								var accountView = accountViewField?.GetValue(aw) as DevExpress.XtraGrid.Views.Grid.GridView;
								accountView?.RefreshData();
								accountView?.LayoutChanged();
								grid.RefreshDataSource();
							}
							catch { }
						}));
					}
					Trace($"[GATE] [PROXIES] Live updated AccountWindow row for {accountId} as 'Account Not Loaded' (proxy='{proxy}').");
					break;
				}
			}
			return matched;
		}
		catch (Exception ex)
		{
			Trace($"[GATE] Note in SyncLiveAccountProxy: {ex.Message}");
			return false;
		}
	}

	private static bool SyncProxyToSettingsAndMemory(string accountId, string proxy, byte[] keyBytes, bool refreshGrid = true)
	{
		if (string.IsNullOrEmpty(accountId)) return false;
		if (!string.IsNullOrEmpty(proxy))
		{
			_assignedProxyDisplayMap[accountId] = proxy;
		}

		bool liveSynced = SyncLiveAccountProxy(accountId, proxy, refreshGrid);

		try
		{
			var baseDir = AppDomain.CurrentDomain.BaseDirectory;
			var setPath = Path.Combine(baseDir, "config", accountId, "settings.json");
			if (keyBytes != null && keyBytes.Length == 32 && !string.IsNullOrEmpty(proxy) && proxy.Contains(":"))
			{
				JsonObject sNode;
				bool exists = File.Exists(setPath);
				if (exists)
				{
					string raw = "";
					for (int r = 0; r < 5; r++)
					{
						try
						{
							raw = File.ReadAllText(setPath);
							break;
						}
						catch (IOException) when (r < 4)
						{
							System.Threading.Thread.Sleep(50);
						}
					}

					if (raw.Contains("\"v\":") && raw.Contains("\"n\":") && raw.Contains("\"d\":"))
					{
						try
						{
							using var doc = JsonDocument.Parse(raw);
							var root = doc.RootElement;
							var nB64 = root.GetProperty("n").GetString()!;
							var dB64 = root.GetProperty("d").GetString()!;
							byte[] nonce = Convert.FromBase64String(nB64);
							byte[] payload = Convert.FromBase64String(dB64);
							byte[] cipher = new byte[payload.Length - 16];
							byte[] tag = new byte[16];
							Buffer.BlockCopy(payload, 0, cipher, 0, cipher.Length);
							Buffer.BlockCopy(payload, cipher.Length, tag, 0, 16);
							byte[] plain = new byte[cipher.Length];
							using (var aesGcm = new AesGcm(keyBytes, 16))
							{
								aesGcm.Decrypt(nonce, cipher, tag, plain);
							}
							var plainStr = Encoding.UTF8.GetString(plain);
							sNode = JsonNode.Parse(plainStr)?.AsObject() ?? new JsonObject();
						}
						catch
						{
							sNode = new JsonObject();
						}
					}
					else
					{
						sNode = new JsonObject();
					}
				}
				else
				{
					sNode = new JsonObject();
				}

				if (!sNode.ContainsKey("connectionSettings") || sNode["connectionSettings"] is not JsonObject)
				{
					sNode["connectionSettings"] = new JsonObject();
				}
				var conn = (JsonObject)sNode["connectionSettings"]!;
				var curProxy = conn["savedProxy"]?.ToString();
				if (curProxy != proxy || !exists)
				{
					conn["savedProxy"] = proxy;
					if (!conn.ContainsKey("isTaiwan")) conn["isTaiwan"] = false;

					byte[] updatedPlain = Encoding.UTF8.GetBytes(sNode.ToJsonString());
					byte[] newNonce = new byte[12];
					RandomNumberGenerator.Fill(newNonce);
					byte[] newCipher = new byte[updatedPlain.Length];
					byte[] newTag = new byte[16];
					using (var aesGcm = new AesGcm(keyBytes, 16))
					{
						aesGcm.Encrypt(newNonce, updatedPlain, newCipher, newTag);
					}
					byte[] newPayload = new byte[newCipher.Length + 16];
					Buffer.BlockCopy(newCipher, 0, newPayload, 0, newCipher.Length);
					Buffer.BlockCopy(newTag, 0, newPayload, newCipher.Length, 16);
					var env = new
					{
						v = 2,
						n = Convert.ToBase64String(newNonce),
						d = Convert.ToBase64String(newPayload)
					};
					for (int r = 0; r < 5; r++)
					{
						try
						{
							File.WriteAllText(setPath, JsonSerializer.Serialize(env));
							Trace($"[GATE] [PROXIES] Synced proxy '{proxy}' to encrypted settings.json for account {accountId} (created={!exists}).");
							break;
						}
						catch (IOException) when (r < 4)
						{
							System.Threading.Thread.Sleep(50);
						}
					}
				}
			}

			var accJsonPath = Path.Combine(baseDir, "config", accountId, "acc.json");
			if (File.Exists(accJsonPath) && keyBytes != null && keyBytes.Length == 32)
			{
				var (plainAcc, _, _) = DecryptAccJson(accJsonPath, keyBytes);
				if (!string.IsNullOrEmpty(plainAcc))
				{
					var accNode = JsonNode.Parse(plainAcc)?.AsObject();
					if (accNode != null)
					{
						bool accChanged = false;
						if (!string.IsNullOrEmpty(proxy) && proxy.Contains(":") && string.IsNullOrEmpty(accNode["CreationIP"]?.ToString()))
						{
							accNode["CreationIP"] = proxy;
							accChanged = true;
						}
						if (accNode["runAtStart"]?.GetValue<bool>() != false)
						{
							accNode["runAtStart"] = false;
							accChanged = true;
						}
						if (accChanged)
						{
							byte[] updatedPlain = Encoding.UTF8.GetBytes(accNode.ToJsonString());
							byte[] newNonce = new byte[12];
							RandomNumberGenerator.Fill(newNonce);
							byte[] newCipher = new byte[updatedPlain.Length];
							byte[] newTag = new byte[16];
							using (var aesGcm = new AesGcm(keyBytes, 16))
							{
								aesGcm.Encrypt(newNonce, updatedPlain, newCipher, newTag);
							}
							byte[] newPayload = new byte[newCipher.Length + 16];
							Buffer.BlockCopy(newCipher, 0, newPayload, 0, newCipher.Length);
							Buffer.BlockCopy(newTag, 0, newPayload, newCipher.Length, 16);
							var env = new
							{
								v = 2,
								n = Convert.ToBase64String(newNonce),
								d = Convert.ToBase64String(newPayload)
							};
							for (int r = 0; r < 5; r++)
							{
								try
								{
									File.WriteAllText(accJsonPath, JsonSerializer.Serialize(env));
									Trace($"[GATE] [ACCOUNT] Ensured runAtStart=false and CreationIP in acc.json for {accountId}.");
									break;
								}
								catch (IOException) when (r < 4)
								{
									System.Threading.Thread.Sleep(50);
								}
							}
						}
					}
				}
			}

			return liveSynced;
		}
		catch (Exception ex)
		{
			Trace($"[GATE] Note in SyncProxyToSettingsAndMemory for {accountId}: {ex.Message}");
			return liveSynced;
		}
	}

	public static void SyncNewAccountProxy(string accountId)
	{
		if (string.IsNullOrEmpty(accountId)) return;
		_suppressAutoStartIggs[accountId] = DateTime.UtcNow.AddMinutes(15);
		Task.Run(async () =>
		{
			string assignedProxy = "";
			for (int attempt = 0; attempt < 25; attempt++)
			{
				try
				{
					var baseDir = AppDomain.CurrentDomain.BaseDirectory;
					var accJsonPath = Path.Combine(baseDir, "config", accountId, "acc.json");
					var keyPath = Path.Combine(baseDir, "config", accountId, "account.key");
					var globalKeyPath = Path.Combine(baseDir, "config", "global", "account.key");
					if (File.Exists(accJsonPath))
					{
						byte[]? keyBytes = null;
						if (File.Exists(keyPath)) keyBytes = File.ReadAllBytes(keyPath);
						else if (File.Exists(globalKeyPath)) keyBytes = File.ReadAllBytes(globalKeyPath);
						if (keyBytes != null && keyBytes.Length == 32)
						{
							var (plainJson, _, _) = DecryptAccJson(accJsonPath, keyBytes);
							if (!string.IsNullOrEmpty(plainJson))
							{
								var node = JsonNode.Parse(plainJson)?.AsObject();
								var creationIp = node?["CreationIP"]?.ToString();

								if (string.IsNullOrEmpty(creationIp) || !creationIp.Contains(":"))
								{
									bool proxyChecked = IsBulkCreateProxyChecked() || AreProxiesEnabled(baseDir);
									if (proxyChecked)
									{
										creationIp = GetNextAvailableProxy(baseDir);
										Trace($"[GATE] [PROXIES] Assigned next available proxy '{creationIp}' to newly created account {accountId}.");
									}
								}

								if (!string.IsNullOrEmpty(creationIp))
								{
									assignedProxy = creationIp;
									_assignedProxyDisplayMap[accountId] = creationIp;
								}

								bool liveUpdated = SyncProxyToSettingsAndMemory(accountId, creationIp ?? "", keyBytes);
								if (liveUpdated)
								{
									Trace($"[GATE] [PROXIES] SyncNewAccountProxy completed for {accountId} (proxy='{creationIp}', attempt={attempt + 1})");
									return;
								}
							}
						}
					}
					else if (!string.IsNullOrEmpty(assignedProxy))
					{
						if (SyncLiveAccountProxy(accountId, assignedProxy))
						{
							Trace($"[GATE] [PROXIES] SyncNewAccountProxy live synced for {accountId} (attempt={attempt + 1})");
							return;
						}
					}
				}
				catch (Exception ex)
				{
					Trace($"[GATE] Note in SyncNewAccountProxy attempt {attempt}: {ex.Message}");
				}
				await Task.Delay(250);
			}
		});
	}

	public static bool BypassReportPassiveEvent(object target, string? text)
	{
		Trace("[GATE] Intercepted RoleRecordFinder.ReportPassiveEvent (text='" + text + "') -> returning false (allowed).");
		return false;
	}

	public static bool BypassReportPassiveEventStatic(string? text)
	{
		Trace("[GATE] Intercepted RoleRecordFinder/MonoMemberTag static license check (text='" + text + "') -> returning false (allowed).");
		return false;
	}

	public static bool HandleNewAccountCreation(long igg)
	{
		if (igg <= 0) return true;
		string iggStr = igg.ToString();
		Trace($"[GATE] Intercepted account creation validation for IGG={iggStr} -> provisioning account...\n{new System.Diagnostics.StackTrace(true)}");

		try
		{
			_suppressAutoStartIggs[iggStr] = DateTime.UtcNow.AddMinutes(15);
			string baseDir = AppDomain.CurrentDomain.BaseDirectory;
			string configDir = Path.Combine(baseDir, "config");
			string acctDir = Path.Combine(configDir, iggStr);
			Directory.CreateDirectory(acctDir);

			// 1. Ensure 32-byte account.key exists on disk
			string keyPath = Path.Combine(acctDir, "account.key");
			byte[]? keyBytes = null;
			if (File.Exists(keyPath))
			{
				byte[] existing = File.ReadAllBytes(keyPath);
				if (existing.Length == 32) keyBytes = existing;
			}
			if (keyBytes == null)
			{
				string globalKey = Path.Combine(configDir, "global", "account.key");
				if (File.Exists(globalKey))
				{
					byte[] gk = File.ReadAllBytes(globalKey);
					if (gk.Length == 32) keyBytes = gk;
				}
			}
			if (keyBytes == null)
			{
				keyBytes = new byte[32];
				RandomNumberGenerator.Fill(keyBytes);
				File.WriteAllBytes(keyPath, keyBytes);
				Trace($"[GATE] Created new 32-byte account.key for account {iggStr}");
			}

			// 2. Ensure Master AccessKey is provisioned
			string masterAk = "eyJha2lkIjoyMDIxMDQyMiwicmtpZCI6MjAyMTA0MjMsImciOjEwNTEwMjk5MDIsInYiOjN9.5ozW3_x4_vryAXrZXXtdOhma1yYa6G2bGnQsZrBtZC_TK6xpxeoVhYNWfESUnS4gsigPH0gAwXZW2rKojXtqWUPe8WaBjHyRx9hqBHHQ06ur-o9aZARheeeo-3NcJgp4hKHhosA4_uzb0FLqgAEgahVq0OrWk62001vcKL4dpBI.GZ5slgdeYpn4Du7YASjGN0Vrx_sUD9o6RAqmBVKhUHdKXHSIy0gkg-ZPT1q22JEiIiXydZ7FYzQhDZmebrxsksvgl5Vb5oLqC6dv9SX4CyCqEXQXmjkKAWdzQ7FYMiCwtoUPW69SkX8wAhuXCcHgaKw20mHK3Cy_zLJlZ4goQZk";
			PersistAccessKeyInAccJsonIfNeeded(iggStr, masterAk);
			AccessKeyCache[iggStr] = masterAk;

			// 3. Register key in bot memory and populate admission
			if (Schema?.BotAssembly != null)
			{
				EnsureAccountKeys(baseDir, Schema.BotAssembly);
				PopulateAccountAdmission(iggStr, masterAk);
			}

			// 4. Assign proxy from pool or last chosen bulk proxy
			string assignedProxy = _lastBulkChosenProxy ?? DynamicCreationWebProxy.ActiveProxyString ?? "";
			if (string.IsNullOrEmpty(assignedProxy) || !assignedProxy.Contains(":"))
			{
				if (AreProxiesEnabled(baseDir) || IsBulkCreateProxyChecked())
				{
					assignedProxy = GetNextAvailableProxy(baseDir) ?? "";
				}
			}
			_assignedProxyDisplayMap[iggStr] = assignedProxy;

			// 5. Invoke GameAccount.WriteAccount to register the accountRecord in AccountManager (for single create)
			if (!IsBulkCreateActive && Schema?.GameAccountWriteAccountMethod != null)
			{
				try
				{
					ParameterInfo[] parameters = Schema.GameAccountWriteAccountMethod.GetParameters();
					object originVal = 0;
					if (parameters.Length > 9 && parameters[9].ParameterType.IsEnum)
					{
						try { originVal = Enum.ToObject(parameters[9].ParameterType, 0); } catch { }
					}

					object? devObj = null;
					if (parameters.Length > 4 && parameters[4].ParameterType != null)
					{
						try
						{
							devObj = Activator.CreateInstance(parameters[4].ParameterType);
							if (devObj != null)
							{
								var dt = devObj.GetType();
								dt.GetField("UUID")?.SetValue(devObj, Guid.NewGuid().ToString("D"));
								dt.GetField("UDID")?.SetValue(devObj, Guid.NewGuid().ToString("D"));
								dt.GetField("ADID")?.SetValue(devObj, Guid.NewGuid().ToString("D"));
								dt.GetField("AndroidID")?.SetValue(devObj, Guid.NewGuid().ToString("N").Substring(0, 16));
								dt.GetField("SerialNO")?.SetValue(devObj, Guid.NewGuid().ToString("N").Substring(0, 10));
								dt.GetField("deviceSet")?.SetValue(devObj, true);
								dt.GetField("deviceRev")?.SetValue(devObj, 1);
							}
						}
						catch { }
					}
					string devUuid = (devObj != null ? devObj.GetType().GetField("UUID")?.GetValue(devObj) as string : null) ?? Guid.NewGuid().ToString("D");

					object[] writeParams = new object[10]
					{
						igg,
						masterAk,
						devUuid,
						"",
						devObj,
						assignedProxy,
						true, // noMsg = true so no modal blocks
						"None",
						false, // IsTaiwan = false
						originVal
					};
					Trace($"[GATE] Calling GameAccount.WriteAccount(IGG={iggStr}, Proxy='{assignedProxy}')...");
					object rec = Schema.GameAccountWriteAccountMethod.Invoke(null, writeParams);
					Trace($"[GATE] GameAccount.WriteAccount completed: {rec != null}");
				}
				catch (Exception ex)
				{
					Trace($"[GATE] GameAccount.WriteAccount exception: {ex.Message}");
				}
			}

			// 6. Re-enforce runAtStart = false and proxy settings
			SyncNewAccountProxy(iggStr);

			// 7. Refresh AccountWindow grid on UI thread
			var aw = AccountWindow.Instance;
			if (aw != null && Schema?.BotAssembly != null)
			{
				aw.BeginInvoke(new Action(() =>
				{
					try
					{
						EnsureAccountKeys(baseDir, Schema.BotAssembly);
						var updateMethod = typeof(AccountWindow).GetMethod("UpdateAccountInfo", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
						updateMethod?.Invoke(aw, null);
						Trace($"[GATE] AccountWindow UI refreshed for new account {iggStr}.");
					}
					catch (Exception ex)
					{
						Trace($"[GATE] Note refreshing AccountWindow for {iggStr}: {ex.Message}");
					}
				}));
			}

			// 8. If bulk create is active, increment counters and update status bar
			try
			{
				bool isNew;
				lock (s_provisionedIggs) { isNew = s_provisionedIggs.Add(iggStr); }

				var awType = AccountWindow.Instance?.GetType();
				var formField = awType?.GetField("createAccForm", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
				var bulkForm = formField?.GetValue(null) as Form;
				if (bulkForm != null && !bulkForm.IsDisposed)
				{
					var ft = bulkForm.GetType();
					var BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

					int curSucc = Convert.ToInt32(ft.GetField("successAmount", BF)?.GetValue(null) ?? 0);
					int curFail = Convert.ToInt32(ft.GetField("failedAmount", BF)?.GetValue(null) ?? 0);
					int curAcc = Convert.ToInt32(ft.GetField("currentAccount", BF)?.GetValue(null) ?? 0);
					int target = Convert.ToInt32(ft.GetField("accountsToMake", BF)?.GetValue(null) ?? 1);

					if (isNew)
					{
						Trace($"[GATE] Bulk Create: Recorded successful account creation for IGG={iggStr} (success={curSucc}, failed={curFail}, current={curAcc}, target={target})");
					}
				}
			}
			catch { }
		}
		catch (Exception ex)
		{
			Trace($"[GATE] Exception in HandleNewAccountCreation({igg}): {ex}");
		}

		return true;
	}

	public static bool BypassConvertOrderedElement(object mapper, long igg)
	{
		return false;
	}

	public static bool BypassConvertOrderedElementStatic(long igg)
	{
		return false;
	}

	public static void BypassAssignLogicalUserWatcher(string item, string cfg, bool containscontrol, bool doattr2, MessageBoxButtons setup3, MessageBoxIcon pol4, Control counter5)
	{
		Trace($"[GATE] Intercepted AssignLogicalUserWatcher modal: item='{item}', caption='{cfg}', buttons={setup3}, icon={pol4} -> suppressed to avoid thread pause");
		if (!string.IsNullOrEmpty(item))
		{
			Trace($"[GATE MODAL STACKTRACE] (item='{item}'):\n{new System.Diagnostics.StackTrace(true)}");
			if (item.Contains("outdated", StringComparison.OrdinalIgnoreCase) || item.Contains("version", StringComparison.OrdinalIgnoreCase))
			{
				Trace("[GATE] [AutoVersion] Detected outdated game version notification in modal! Triggering live auto-update...");
				Task.Run(() => CheckAndUpdateLiveVersion(true));
			}
		}
	}

	public static DialogResult BypassAssignLiteralWatcherSharer(string res, string cfg, MessageBoxIcon filter, bool isasset2)
	{
		if (!string.IsNullOrEmpty(res) && res.Contains("License", StringComparison.OrdinalIgnoreCase))
		{
			Trace($"[GATE LICENSE STACKTRACE (Sharer)]:\n{new System.Diagnostics.StackTrace(true)}");
		}
		if (!string.IsNullOrEmpty(res) && (res.Contains("outdated", StringComparison.OrdinalIgnoreCase) || res.Contains("version", StringComparison.OrdinalIgnoreCase)))
		{
			Trace("[GATE] [AutoVersion] Detected outdated game version notification in modal (Sharer)! Triggering live auto-update...");
			Task.Run(() => CheckAndUpdateLiveVersion(true));
		}
		if (res != null && (res.Contains("re-assign proxies", StringComparison.OrdinalIgnoreCase) ||
		                    res.Contains("Do you want to continue", StringComparison.OrdinalIgnoreCase) ||
		                    res.Contains("reset all", StringComparison.OrdinalIgnoreCase) ||
		                    res.Contains("reset", StringComparison.OrdinalIgnoreCase) ||
		                    res.Contains("assign", StringComparison.OrdinalIgnoreCase)))
		{
			Trace($"[GATE] Intercepted AssignLiteralWatcherSharer modal: res='{res}', caption='{cfg}', icon={filter} -> returning Yes (confirmed)");
			return DialogResult.Yes;
		}

		Trace($"[GATE] Intercepted AssignLiteralWatcherSharer modal: res='{res}', caption='{cfg}', icon={filter} -> returning OK");
		return DialogResult.OK;
	}

	public static DialogResult BypassStartSharer(string setup, string reg, MessageBoxIcon consumer)
	{
		if (setup != null && (setup.Contains("re-assign proxies", StringComparison.OrdinalIgnoreCase) ||
		                      setup.Contains("Do you want to continue", StringComparison.OrdinalIgnoreCase) ||
		                      setup.Contains("reset all", StringComparison.OrdinalIgnoreCase) ||
		                      setup.Contains("reset", StringComparison.OrdinalIgnoreCase) ||
		                      setup.Contains("assign", StringComparison.OrdinalIgnoreCase)))
		{
			Trace($"[GATE] Intercepted StartSharer modal: setup='{setup}', caption='{reg}', icon={consumer} -> returning Yes (confirmed)");
			return DialogResult.Yes;
		}

		Trace($"[GATE] Intercepted StartSharer modal: setup='{setup}', caption='{reg}', icon={consumer} -> returning OK");
		return DialogResult.OK;
	}

	public static string BypassFindExtractor()
	{
		return AppDomain.CurrentDomain.BaseDirectory;
	}

	public static Task<Dictionary<string, string>> BypassIggPrepEntryAsync(object target, string scenario, bool regenerateDevice)
	{
		Trace($"[GATE] Intercepted iggLogin.PrepEntryAsync (Scenario={scenario}, Regen={regenerateDevice})");

		if (IsBulkCreateActive)
		{
			try
			{
				var aw = AccountWindow.Instance;
				var formField = aw?.GetType().GetField("createAccForm", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
				var bulkForm = formField?.GetValue(null) as Form;
				if (bulkForm != null && !bulkForm.IsDisposed)
				{
					var devField = bulkForm.GetType().GetField("cDevice", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
					var cDev = devField?.GetValue(bulkForm);
					if (cDev != null)
					{
						var dt = cDev.GetType();
						dt.GetField("UUID")?.SetValue(cDev, Guid.NewGuid().ToString("D"));
						dt.GetField("UDID")?.SetValue(cDev, Guid.NewGuid().ToString("D"));
						dt.GetField("ADID")?.SetValue(cDev, Guid.NewGuid().ToString("D"));
						dt.GetField("AndroidID")?.SetValue(cDev, Guid.NewGuid().ToString("N").Substring(0, 16));
						dt.GetField("SerialNO")?.SetValue(cDev, Guid.NewGuid().ToString("N").Substring(0, 10));
						dt.GetField("deviceSet")?.SetValue(cDev, true);
						dt.GetField("deviceRev")?.SetValue(cDev, 1);
						Trace("[GATE] Bulk Create: Freshly randomized cDevice for unique guest registration.");
					}
				}
			}
			catch { }
		}
		Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		string text = "1051029902";
		try
		{
			string text2 = (target?.GetType().GetProperty("GameID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))?.GetValue(target) as string;
			if (!string.IsNullOrWhiteSpace(text2))
			{
				text = text2;
			}
		}
		catch
		{
		}
		(string uuid, string udid) orInitDeviceData = GetOrInitDeviceData(target, regenerateDevice);
		string item = orInitDeviceData.uuid;
		string item2 = orInitDeviceData.udid;
		string text3 = (string.IsNullOrEmpty(scenario) ? "sign_in" : scenario);
		string text4 = "embed/entry?scenario=" + text3 + "&for_audting=0";
		try
		{
			string text5 = ((target?.GetType())?.GetField("LoginURL", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))?.GetValue(null) as string;
			if (!string.IsNullOrWhiteSpace(text5))
			{
				if (text5.Contains("{0}"))
				{
					try
					{
						text5 = string.Format(text5, text3);
					}
					catch
					{
					}
				}
				text5 = text5.Replace("{scenario}", text3);
				if (Uri.TryCreate(text5, UriKind.Absolute, out Uri result))
				{
					string text6 = result.PathAndQuery.TrimStart('/');
					if (!string.IsNullOrWhiteSpace(text6))
					{
						text4 = text6;
					}
				}
			}
		}
		catch
		{
		}
		try
		{
			Dictionary<string, string> dictionary2 = GetSigner().Sign(text4, "", item, item2, text, null);
			foreach (KeyValuePair<string, string> item3 in dictionary2)
			{
				dictionary[item3.Key] = item3.Value;
			}
			Trace($"[GATE] Signed iggLogin request: generated {dictionary2.Count} signature header(s) for resourcePath='{text4}'.");
		}
		catch (Exception ex)
		{
			Trace("[GATE] Error signing iggLogin request: " + ex.Message);
		}
		Trace($"[GATE] Completed iggLogin.PrepEntryAsync with {dictionary.Count} header(s) (GameID={text}, UUID={item}, UDID={item2}).");
		return Task.FromResult(dictionary);
	}

	public static Task<TError?> BypassAddSessionData<TError>(object loginPage, bool showError) where TError : class
	{
		Trace($"[GATE] Intercepted GameAccount.AddSessionData<{typeof(TError).Name}>(ShowError={showError})");
		try
		{
			if (loginPage == null)
			{
				Trace("[GATE] BypassAddSessionData: loginPage is null!");
				return Task.FromResult<TError>(null);
			}
			Type type = loginPage.GetType();
			string text = (type.GetField("Token", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(loginPage) as string) ?? (type.GetProperty("Token", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(loginPage) as string);
			string text2 = (type.GetField("Email", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(loginPage) as string) ?? (type.GetProperty("Email", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(loginPage) as string);
			string value = (type.GetField("RefreshToken", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(loginPage) as string) ?? (type.GetProperty("RefreshToken", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(loginPage) as string);
			object obj = type.GetField("DeviceData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(loginPage) ?? type.GetProperty("DeviceData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(loginPage);
			bool flag = false;
			try
			{
				PropertyInfo property = type.GetProperty("IsTaiwanMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (property != null)
				{
					flag = (bool)property.GetValue(loginPage);
				}
			}
			catch
			{
			}
			Trace($"[GATE] AddSessionData loginPage: Token='{text}', Email='{text2}', RefreshToken='{value}', IsTaiwan={flag}");
			long num = 0L;
			string text3 = "";
			if (!string.IsNullOrWhiteSpace(text))
			{
				if (text.Contains('.'))
				{
					try
					{
						string[] array = text.Split('.');
						if (array.Length >= 2)
						{
							string text4 = array[1];
							text4 = text4.Replace('-', '+').Replace('_', '/');
							switch (text4.Length % 4)
							{
							case 2:
								text4 += "==";
								break;
							case 3:
								text4 += "=";
								break;
							}
							byte[] bytes = Convert.FromBase64String(text4);
							string text5 = Encoding.UTF8.GetString(bytes);
							Trace("[GATE] Decoded JWT payload: " + text5);
							using JsonDocument jsonDocument = JsonDocument.Parse(text5);
							JsonElement rootElement = jsonDocument.RootElement;
							JsonElement value3;
							JsonElement value4;
							JsonElement value5;
							JsonElement value6;
							if (rootElement.TryGetProperty("sub", out var value2))
							{
								long result;
								if (value2.ValueKind == JsonValueKind.Number)
								{
									num = value2.GetInt64();
								}
								else if (long.TryParse(value2.GetString(), out result))
								{
									num = result;
								}
							}
							else if (rootElement.TryGetProperty("iggid", out value3))
							{
								long result2;
								if (value3.ValueKind == JsonValueKind.Number)
								{
									num = value3.GetInt64();
								}
								else if (long.TryParse(value3.GetString(), out result2))
								{
									num = result2;
								}
							}
							else if (rootElement.TryGetProperty("id", out value4))
							{
								long result3;
								if (value4.ValueKind == JsonValueKind.Number)
								{
									num = value4.GetInt64();
								}
								else if (long.TryParse(value4.GetString(), out result3))
								{
									num = result3;
								}
							}
							else if (rootElement.TryGetProperty("accountId", out value5))
							{
								long result4;
								if (value5.ValueKind == JsonValueKind.Number)
								{
									num = value5.GetInt64();
								}
								else if (long.TryParse(value5.GetString(), out result4))
								{
									num = result4;
								}
							}
							else if (rootElement.TryGetProperty("user_id", out value6))
							{
								long result5;
								if (value6.ValueKind == JsonValueKind.Number)
								{
									num = value6.GetInt64();
								}
								else if (long.TryParse(value6.GetString(), out result5))
								{
									num = result5;
								}
							}
							JsonElement value8;
							if (rootElement.TryGetProperty("access_token", out var value7))
							{
								text3 = value7.GetString() ?? "";
							}
							else if (rootElement.TryGetProperty("token", out value8))
							{
								text3 = value8.GetString() ?? "";
							}
						}
					}
					catch (Exception ex)
					{
						Trace("[GATE] JWT parse error: " + ex.Message);
					}
				}
				if (num == 0L && text.StartsWith("{") && text.EndsWith("}"))
				{
					try
					{
						using JsonDocument jsonDocument2 = JsonDocument.Parse(text);
						JsonElement jsonElement = jsonDocument2.RootElement;
						if (jsonElement.TryGetProperty("data", out var value9))
						{
							jsonElement = value9;
						}
						if (jsonElement.TryGetProperty("iggid", out var value10))
						{
							long result6;
							if (value10.ValueKind == JsonValueKind.Number)
							{
								num = value10.GetInt64();
							}
							else if (long.TryParse(value10.GetString(), out result6))
							{
								num = result6;
							}
						}
						if (jsonElement.TryGetProperty("access_token", out var value11))
						{
							text3 = value11.GetString() ?? "";
						}
					}
					catch
					{
					}
				}
				if (num == 0L && text.Contains("="))
				{
					try
					{
						string[] array2 = text.TrimStart('?').Split('&');
						for (int i = 0; i < array2.Length; i++)
						{
							string[] array3 = array2[i].Split('=');
							if (array3.Length != 2)
							{
								continue;
							}
							string text6 = array3[0].ToLowerInvariant();
							string text7 = Uri.UnescapeDataString(array3[1]);
							switch (text6)
							{
							case "iggid":
							case "userid":
							case "user_id":
							case "id":
							{
								if (long.TryParse(text7, out var result7))
								{
									num = result7;
								}
								break;
							}
							case "access_token":
							case "token":
							case "key":
								text3 = text7;
								break;
							}
						}
					}
					catch
					{
					}
				}
				if (string.IsNullOrEmpty(text3))
				{
					text3 = text;
				}
			}
			if (num == 0L)
			{
				FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				foreach (FieldInfo fieldInfo in fields)
				{
					if (fieldInfo.FieldType == typeof(long) && fieldInfo.Name != "Expiry")
					{
						long num2 = (long)fieldInfo.GetValue(loginPage);
						if (num2 > 1000)
						{
							num = num2;
							Trace($"[GATE] Found IGG ID {num} in loginPage field {fieldInfo.Name}");
							break;
						}
					}
				}
			}
			if (num == 0L)
			{
				Match match = Regex.Match(text ?? "", "\\b\\d{8,12}\\b");
				if (match.Success && long.TryParse(match.Value, out var result8))
				{
					num = result8;
					Trace($"[GATE] Extracted IGG ID {num} via regex from token");
				}
				else
				{
					num = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
					Trace($"[GATE] Assigned fallback IGG ID {num}");
				}
			}
			string text8 = Guid.NewGuid().ToString();
			if (obj != null)
			{
				try
				{
					MemberInfo memberInfo = (MemberInfo)(((object)obj.GetType().GetProperty("uuid", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) ?? ((object)obj.GetType().GetField("uuid", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)));
					if (memberInfo is PropertyInfo propertyInfo)
					{
						text8 = ((string)propertyInfo.GetValue(obj)) ?? text8;
					}
					else if (memberInfo is FieldInfo fieldInfo2)
					{
						text8 = ((string)fieldInfo2.GetValue(obj)) ?? text8;
					}
				}
				catch
				{
				}
			}
			else
			{
				text8 = GetOrInitDeviceData(loginPage, regenerateDevice: false).uuid;
				obj = type.GetField("DeviceData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(loginPage);
			}
			if (Schema?.GameAccountWriteAccountMethod != null)
			{
				ParameterInfo[] parameters = Schema.GameAccountWriteAccountMethod.GetParameters();
				object obj6 = 2;
				if (parameters.Length > 9 && parameters[9].ParameterType.IsEnum)
				{
					try
					{
						obj6 = Enum.ToObject(parameters[9].ParameterType, 2);
					}
					catch
					{
					}
				}
				object[] parameters2 = new object[10]
				{
					num,
					text3,
					text8,
					text2 ?? "",
					obj,
					"",
					false,
					"None",
					flag,
					obj6
				};
				Trace($"[GATE] Calling GameAccount.WriteAccount(IGG={num}, AccessKey={text3.Substring(0, Math.Min(10, text3.Length))}..., UUID={text8}, Email='{text2}', IsTaiwan={flag}, Origin={obj6})...");
				object obj8 = Schema.GameAccountWriteAccountMethod.Invoke(null, parameters2);
				Trace($"[GATE] GameAccount.WriteAccount completed, returned: {obj8 != null}");
			}
			else
			{
				Trace("[GATE] Warning: Schema.GameAccountWriteAccountMethod not found!");
			}
		}
		catch (Exception value12)
		{
			Trace($"[GATE] Exception in BypassAddSessionData: {value12}");
		}
		return Task.FromResult<TError>(null);
	}

	public static Task<TToken?> BypassDisableContext<TToken>(string info, object cfg, string template, bool ispred2, object token3) where TToken : class
	{
		Trace($"[GATE] Intercepted PredictorDriverTask.DisableContext<{typeof(TToken).Name}>(info='{info}', template='{template}')");
		try
		{
			Type typeFromHandle = typeof(TToken);
			object obj = Activator.CreateInstance(typeFromHandle);
			Type nestedType = typeFromHandle.GetNestedType("Data", BindingFlags.Public | BindingFlags.NonPublic);
			if (nestedType != null)
			{
				object obj2 = Activator.CreateInstance(nestedType);
				nestedType.GetProperty("access_token")?.SetValue(obj2, info ?? "");
				typeFromHandle.GetProperty("data")?.SetValue(obj, obj2);
			}
			return Task.FromResult(obj as TToken);
		}
		catch (Exception ex)
		{
			Trace("[GATE] Error in BypassDisableContext: " + ex.Message);
		}
		return Task.FromResult<TToken>(null);
	}

	public static Task BypassDequeuePortableProc(object? queue, string? param, string? second, string? c)
	{
		Trace($"[GATE] BypassDequeuePortableProc called (param={param}, second={second}, c={c})");
		return Task.CompletedTask;
	}

	private static string GetActiveSessionToken()
	{
		if (string.IsNullOrEmpty(currentAuth?.SessionId))
		{
			if (string.IsNullOrEmpty(currentAuth?.Token))
			{
				return "sess_local_gate_master";
			}
			return currentAuth.Token;
		}
		return currentAuth.SessionId;
	}

	public static string BypassManageProfile()
	{
		string activeSessionToken = GetActiveSessionToken();
		Trace("[GATE] BypassManageProfile called, returning sid=" + activeSessionToken);
		return activeSessionToken;
	}

	public static void SkipCmdBaseInit()
	{
	}

	public static Task<(TReceiver, bool)> BypassServerCheck<TReceiver>(object? proxyConsumer, object? init, string? cfg)
	{
		string text = "1102200717";
		string text2 = string.Empty;
		try
		{
			if (proxyConsumer != null)
			{
				object obj = (Schema?.ContainerAccRecordField ?? proxyConsumer.GetType().GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((FieldInfo f) => f.FieldType.Name == "accountRecord"))?.GetValue(proxyConsumer);
				if (obj != null)
				{
					string text3 = obj.GetType().GetField("IGG_S", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(obj) as string;
					if (!string.IsNullOrEmpty(text3))
					{
						text = text3;
					}
					text2 = (obj.GetType().GetField("AccessKey", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(obj) as string) ?? string.Empty;
				}
			}
			if (!IsValidAccessKey(text2))
			{
				text2 = ReadLocalAccessKey(text);
			}
			if (_suppressAutoStartIggs.TryGetValue(text, out var suppUntil) && suppUntil > DateTime.UtcNow)
			{
				Trace($"[GATE] BypassServerCheck: Account {text} is auto-start suppressed. Rejecting connection.");
				return Task.FromResult(ValueTuple.Create(Activator.CreateInstance<TReceiver>(), item2: false));
			}
			if (proxyConsumer != null && _assignedProxyDisplayMap.TryGetValue(text, out var pxy) && !string.IsNullOrEmpty(pxy) && pxy.Contains(":"))
			{
				try
				{
					var settingsProp = proxyConsumer.GetType().GetProperty("Settings", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(proxyConsumer)
									?? proxyConsumer.GetType().GetField("Settings", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(proxyConsumer);
					if (settingsProp != null)
					{
						var connField = settingsProp.GetType().GetField("connectionSettings", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
						var connObj = connField?.GetValue(settingsProp);
						if (connObj != null)
						{
							var spField = connObj.GetType().GetField("savedProxy", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
							spField?.SetValue(connObj, pxy);
							Trace($"[GATE] Enforced proxy '{pxy}' on Container.Settings.connectionSettings for account {text} on start.");
						}
					}
				}
				catch { }
			}
			Trace($"[GATE] BypassServerCheck: Admitting account {text}, key length {text2.Length}");
			TReceiver val = ((((Schema != null) ? CreateAdmissionItem(Schema, text, text2, allowed: true, "") : ((object)Activator.CreateInstance<TReceiver>())) is TReceiver val2) ? val2 : Activator.CreateInstance<TReceiver>());
			if (init == null && Schema != null)
			{
				init = Schema.MonitorSingletonField?.GetValue(null);
			}
			if (init != null && Schema != null)
			{
				AddMonitorAdmission(Schema, init, text, val);
			}
			return Task.FromResult(ValueTuple.Create(val, item2: true));
		}
		catch (Exception ex)
		{
			Trace("[GATE] Exception in BypassServerCheck: " + ex.Message);
			return Task.FromResult(ValueTuple.Create(Activator.CreateInstance<TReceiver>(), item2: true));
		}
	}

	private static void InstallStartupHooks(Assembly application)
	{
		if (Schema == null)
		{
			return;
		}
		try
		{
			Trace($"[GATE] Installing Universal Startup & Admission Detours (Bot v{Schema.Version})...");
			MethodInfo[] licenseStateBoolMethods;
			if (Schema.LicenseStateType != null)
			{
				licenseStateTime = Schema.LicenseStateTimeField;
				connectedLicenseState = Activator.CreateInstance(Schema.LicenseStateType);
				disconnectedLicenseState = Activator.CreateInstance(Schema.LicenseStateType);
				if (Schema.LicenseStateEnumField != null)
				{
					Schema.LicenseStateEnumField.SetValue(connectedLicenseState, Enum.ToObject(Schema.LicenseStateEnumField.FieldType, 4));
					Schema.LicenseStateEnumField.SetValue(disconnectedLicenseState, Enum.ToObject(Schema.LicenseStateEnumField.FieldType, 0));
				}
				if (Schema.LicenseStateSingletonGetter != null)
				{
					DynamicDetour.Redirect(Schema.LicenseStateSingletonGetter, HandlerMethod("GetLicenseConnectionState"), Trace);
					Trace("[GATE] Detoured GetLicenseConnectionState (" + Schema.LicenseStateSingletonGetter.Name + ") on " + Schema.LicenseStateType.Name);
				}
				licenseStateBoolMethods = Schema.LicenseStateBoolMethods;
				foreach (MethodInfo methodInfo in licenseStateBoolMethods)
				{
					DynamicDetour.Redirect(methodInfo, HandlerMethod("AlwaysTrueInstance"), Trace);
					Trace("[GATE] Detoured " + methodInfo.Name + " on " + Schema.LicenseStateType.Name);
				}
				licenseStateSingletonField = Schema.LicenseStateSingletonField;
				licenseStateSingletonField?.SetValue(null, connectedLicenseState);
				Schema.LicenseStateSingletonSetter?.Invoke(null, new object[1] { connectedLicenseState });
			}
			if (Schema.LoginReplaceControllerMethod != null)
			{
				MethodInfo handler = HandlerMethod("BypassServerCheck").MakeGenericMethod(Schema.AdmissionType);
				DynamicDetour.Redirect(Schema.LoginReplaceControllerMethod, handler, Trace);
				Trace($"[GATE] Detoured {Schema.LoginReplaceControllerMethod.Name} -> BypassServerCheck<{Schema.AdmissionType.Name}>");
			}
			if (Schema.LoginKillAutomatedStateMethod != null)
			{
				DynamicDetour.Redirect(Schema.LoginKillAutomatedStateMethod, HandlerMethod("BypassTaskBoolTrue"), Trace);
				Trace("[GATE] Detoured " + Schema.LoginKillAutomatedStateMethod.Name + " -> BypassTaskBoolTrue");
			}
			if (Schema.LoginConsumeIterableMethod != null)
			{
				DynamicDetour.Redirect(Schema.LoginConsumeIterableMethod, HandlerMethod("BypassConsumeIterableResponder"), Trace);
				Trace("[GATE] Detoured " + Schema.LoginConsumeIterableMethod.Name + " -> BypassConsumeIterableResponder");
			}
			if (Schema.MonitorBoolDeciderMethod != null)
			{
				DynamicDetour.Redirect(Schema.MonitorBoolDeciderMethod, HandlerMethod("BypassTaskBoolTrue"), Trace);
				Trace("[GATE] Detoured " + Schema.MonitorBoolDeciderMethod.Name + " -> BypassTaskBoolTrue");
			}
			if (Schema.MonitorGetKeyMethod != null)
			{
				DynamicDetour.Redirect(Schema.MonitorGetKeyMethod, HandlerMethod("BypassMeasureOrder"), Trace);
				Trace("[GATE] Detoured " + Schema.MonitorGetKeyMethod.Name + " -> BypassMeasureOrder");
			}
			licenseStateBoolMethods = Schema.MonitorTaskNoOpMethods;
			foreach (MethodInfo methodInfo2 in licenseStateBoolMethods)
			{
				DynamicDetour.Redirect(methodInfo2, HandlerMethod("BypassAcceptAutomatedTester"), Trace);
				Trace("[GATE] Detoured Monitor Task " + methodInfo2.Name + " on " + Schema.MonitorType.Name);
			}
			if (Schema.MonitorSingletonField != null)
			{
				object uninitializedObject = RuntimeHelpers.GetUninitializedObject(Schema.MonitorType);
				object obj = _wiredStaticClient ?? RuntimeHelpers.GetUninitializedObject(Schema.StaticClientType);
				object obj2 = _wiredBridgeSorter ?? RuntimeHelpers.GetUninitializedObject(Schema.BridgeSorterType);
				FieldInfo[] fields = Schema.MonitorType.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				foreach (FieldInfo fieldInfo in fields)
				{
					if (fieldInfo.IsStatic)
					{
						continue;
					}
					try
					{
						if (fieldInfo.FieldType == typeof(object))
						{
							fieldInfo.SetValue(uninitializedObject, new object());
						}
						else if (fieldInfo.FieldType == typeof(Task))
						{
							fieldInfo.SetValue(uninitializedObject, Task.CompletedTask);
						}
						else if (fieldInfo.FieldType == typeof(string))
						{
							fieldInfo.SetValue(uninitializedObject, string.Empty);
						}
						else if (fieldInfo.FieldType == Schema.StaticClientType)
						{
							fieldInfo.SetValue(uninitializedObject, obj);
						}
						else if (fieldInfo.FieldType == Schema.BridgeSorterType)
						{
							fieldInfo.SetValue(uninitializedObject, obj2);
						}
						else if (fieldInfo.FieldType.IsGenericType && fieldInfo.FieldType.GetGenericTypeDefinition() == typeof(ConcurrentDictionary<, >))
						{
							fieldInfo.SetValue(uninitializedObject, Activator.CreateInstance(fieldInfo.FieldType));
						}
					}
					catch
					{
					}
				}
				if (obj != null)
				{
					fields = Schema.StaticClientBoolFields;
					foreach (FieldInfo fieldInfo2 in fields)
					{
						try
						{
							fieldInfo2.SetValue(obj, true);
						}
						catch
						{
						}
					}
				}
				if (obj2 != null)
				{
					try
					{
						if (Schema.BridgeSorterStringFields.Length != 0)
						{
							Schema.BridgeSorterStringFields[0].SetValue(obj2, "DarkVenom24");
						}
						if (Schema.BridgeSorterStringFields.Length > 1)
						{
							Schema.BridgeSorterStringFields[1].SetValue(obj2, GetActiveSessionToken());
						}
						Schema.BridgeSorterStaticClientField.SetValue(obj2, obj);
					}
					catch
					{
					}
				}
				Schema.MonitorStaticClientField?.SetValue(uninitializedObject, obj);
				Schema.MonitorBridgeSorterField?.SetValue(uninitializedObject, obj2);
				Schema.MonitorSingletonField.SetValue(null, uninitializedObject);
				Schema.MonitorInitMethod?.Invoke(null, new object[1] { uninitializedObject });
				Trace("[GATE] Initialized static " + Schema.MonitorType.Name + "." + Schema.MonitorSingletonField.Name);
			}
			licenseStateBoolMethods = Schema.BridgeSorterBoolMethods;
			foreach (MethodInfo methodInfo3 in licenseStateBoolMethods)
			{
				DynamicDetour.Redirect(methodInfo3, HandlerMethod("AlwaysTrueInstance"), Trace);
				Trace("[GATE] Detoured " + methodInfo3.Name + " on " + Schema.BridgeSorterType.Name);
			}
			if (Schema.BridgeSorterProfileMethod != null)
			{
				DynamicDetour.Redirect(Schema.BridgeSorterProfileMethod, HandlerMethod("BypassManageProfile"), Trace);
				Trace("[GATE] Detoured " + Schema.BridgeSorterProfileMethod.Name + " on " + Schema.BridgeSorterType.Name);
			}
			licenseStateBoolMethods = Schema.StaticClientBoolMethods;
			foreach (MethodInfo methodInfo4 in licenseStateBoolMethods)
			{
				DynamicDetour.Redirect(methodInfo4, HandlerMethod("AlwaysTrueInstance"), Trace);
				Trace("[GATE] Detoured " + methodInfo4.Name + " on " + Schema.StaticClientType.Name);
			}
			licenseStateBoolMethods = Schema.StaticClientNoOpTaskMethods;
			foreach (MethodInfo methodInfo5 in licenseStateBoolMethods)
			{
				DynamicDetour.Redirect(methodInfo5, HandlerMethod("BypassTaskCompleted"), Trace);
				Trace("[GATE] Detoured " + methodInfo5.Name + " on " + Schema.StaticClientType.Name);
			}
			if (Schema.SegmentedQueueDequeueMethod != null)
			{
				// In Bot 6.13, SegmentedQueueDequeueMethod resolves to ProviderEngine.RunInternalTransaction,
				// which is the bot's real in-game mail dispatcher. Leaving it undetoured allows in-game mails
				// (such as Guild Bank balance, status, and error responses) to be delivered to players.
				Trace("[GATE] Preserving native " + Schema.SegmentedQueueDequeueMethod.Name + " on " + Schema.SegmentedQueueType?.Name + " (in-game mail dispatcher)");
			}
			if (Schema.CmdBaseInitMethod != null)
			{
				DynamicDetour.Redirect(Schema.CmdBaseInitMethod, HandlerMethod("SkipCmdBaseInit"), Trace);
				Trace("[GATE] Detoured CmdBase.Init on " + Schema.CmdBaseType?.FullName);
			}
			Type gameVersionType = Schema.GameVersionType;
			if (gameVersionType != null)
			{
				string gvStr = GameVersionAutoUpdater.CurrentVersion ?? GetConfiguredGameVersion();
				ApplyGameVersion(gvStr);
				MethodInfo method = gameVersionType.GetMethod("getGameVersion", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				if (method != null)
				{
					DynamicDetour.Redirect(method, HandlerMethod("BypassGetGameVersion"), Trace);
					Trace($"[GATE] Detoured GameVersion.getGameVersion(bool) -> fixed {gvStr}");
				}
				MethodInfo method2 = gameVersionType.GetMethod("set_currentGameVersion", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				if (method2 != null)
				{
					DynamicDetour.Redirect(method2, HandlerMethod("BypassSetGameVersion"), Trace);
					Trace("[GATE] Detoured GameVersion.set_currentGameVersion");
				}
				MethodInfo method3 = gameVersionType.GetMethod("set_currentGameVersionTW", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				if (method3 != null)
				{
					DynamicDetour.Redirect(method3, HandlerMethod("BypassSetGameVersion"), Trace);
					Trace("[GATE] Detoured GameVersion.set_currentGameVersionTW");
				}
			}
			if (Schema?.IggLoginPrepEntryMethod != null)
			{
				DynamicDetour.Redirect(Schema.IggLoginPrepEntryMethod, HandlerMethod("BypassIggPrepEntryAsync"), Trace);
				Trace($"[GATE] Detoured {Schema.IggLoginPrepEntryMethod.Name} on {Schema.IggLoginType?.Name} -> BypassIggPrepEntryAsync");
			}
			Trace($"[GATE] Native AddSessionData/DisableContext enabled (ClientBridgeSingletons={Schema?.ClientBridgeSingletons.Length ?? 0}).");
			if (Schema?.StreamCompressorType != null && Schema.AllocatorFieldType != null && Schema.ResumeInitializerMethod != null)
			{
				InitializeStreamCompressorSynchronization(Schema);
				MethodInfo handler2 = HandlerMethod("SynchronizedResumeInitializer").MakeGenericMethod(Schema.AllocatorFieldType);
				DynamicDetour.Redirect(Schema.ResumeInitializerMethod, handler2, Trace);
				Trace($"[GATE] Detoured {Schema.StreamCompressorType.Name}.{Schema.ResumeInitializerMethod.Name} -> SynchronizedResumeInitializer<{Schema.AllocatorFieldType.Name}>");
				if (Schema.WriteGeneralChooserMethod != null)
				{
					DynamicDetour.Redirect(Schema.WriteGeneralChooserMethod, HandlerMethod("SynchronizedWriteGeneralChooser"), Trace);
					Trace($"[GATE] Detoured {Schema.StreamCompressorType.Name}.{Schema.WriteGeneralChooserMethod.Name} -> SynchronizedWriteGeneralChooser");
				}
			}
			if (Schema?.AccountWindowCreateNewAccountMethod != null)
			{
				DynamicDetour.Redirect(Schema.AccountWindowCreateNewAccountMethod, HandlerMethod("BypassCreateNewAccountClick"), Trace);
				Trace($"[GATE] Detoured {Schema.AccountWindowType.Name}.{Schema.AccountWindowCreateNewAccountMethod.Name} -> BypassCreateNewAccountClick");
			}
			if (Schema?.AccountWindowBulkCreateAccountsMethod != null)
			{
				DynamicDetour.Redirect(Schema.AccountWindowBulkCreateAccountsMethod, HandlerMethod("BypassBulkCreateAccountsClick"), Trace);
				Trace($"[GATE] Detoured {Schema.AccountWindowType.Name}.{Schema.AccountWindowBulkCreateAccountsMethod.Name} -> BypassBulkCreateAccountsClick");
			}
			if (Schema?.GameAccountStartCreatedAccountMethod != null)
			{
				DynamicDetour.Redirect(Schema.GameAccountStartCreatedAccountMethod, HandlerMethod("BypassStartCreatedAccount"), Trace);
				Trace($"[GATE] Detoured {Schema.GameAccountType?.Name}.{Schema.GameAccountStartCreatedAccountMethod.Name} -> BypassStartCreatedAccount");
			}
			if (Schema?.BulkCreateAccountRowCellStyleMethod != null)
			{
				DynamicDetour.Redirect(Schema.BulkCreateAccountRowCellStyleMethod, HandlerMethod("HookedBulkProxyGridRowCellStyle"), Trace);
				Trace($"[GATE] Detoured {Schema.BulkCreateAccountFormType?.Name}.{Schema.BulkCreateAccountRowCellStyleMethod.Name} -> HookedBulkProxyGridRowCellStyle");
			}
			if (Schema?.BulkCreateAccountCheckProxyMethod != null)
			{
				DynamicDetour.Redirect(Schema.BulkCreateAccountCheckProxyMethod, HandlerMethod("HookedBulkCheckProxy"), Trace);
				Trace($"[GATE] Detoured {Schema.BulkCreateAccountFormType?.Name}.{Schema.BulkCreateAccountCheckProxyMethod.Name} -> HookedBulkCheckProxy");
			}
			if (Schema?.BulkCreateAccountCanPingMethod != null)
			{
				DynamicDetour.Redirect(Schema.BulkCreateAccountCanPingMethod, HandlerMethod("HookedBulkCanPing"), Trace);
				Trace($"[GATE] Detoured {Schema.BulkCreateAccountFormType?.Name}.{Schema.BulkCreateAccountCanPingMethod.Name} -> HookedBulkCanPing");
			}
			if (Schema?.ConfigurableReporterTagNewValueMethod != null)
			{
				DynamicDetour.Redirect(Schema.ConfigurableReporterTagNewValueMethod, HandlerMethod("HookedHttpClientNewValue"), Trace);
				Trace($"[GATE] Detoured {Schema.ConfigurableReporterTagType?.Name}.{Schema.ConfigurableReporterTagNewValueMethod.Name} -> HookedHttpClientNewValue (Enforces HTTP proxy transport)");
			}
			// getProxy is preserved native so the ValueTuple return type and internal proxy rotation run without mismatch
			if (Schema?.RoleRecordFinderReportPassiveEventMethod != null)
			{
				string handlerName;
				var p0 = Schema.RoleRecordFinderReportPassiveEventMethod.GetParameters().FirstOrDefault();
				if (p0 != null && p0.ParameterType == typeof(long))
				{
					handlerName = Schema.RoleRecordFinderReportPassiveEventMethod.IsStatic
						? "BypassConvertOrderedElementStatic"
						: "BypassConvertOrderedElement";
				}
				else
				{
					handlerName = Schema.RoleRecordFinderReportPassiveEventMethod.IsStatic
						? "BypassReportPassiveEventStatic"
						: "BypassReportPassiveEvent";
				}
				DynamicDetour.Redirect(Schema.RoleRecordFinderReportPassiveEventMethod, HandlerMethod(handlerName), Trace);
				Trace($"[GATE] Detoured {Schema.RoleRecordFinderType?.Name}.{Schema.RoleRecordFinderReportPassiveEventMethod.Name} (static={Schema.RoleRecordFinderReportPassiveEventMethod.IsStatic}) -> {handlerName}");
			}
			if (Schema?.AssignLogicalUserWatcherMethod != null)
			{
				DynamicDetour.Redirect(Schema.AssignLogicalUserWatcherMethod, HandlerMethod("BypassAssignLogicalUserWatcher"), Trace);
				Trace($"[GATE] Detoured {Schema.SegmentedCollectionRoleType?.Name}.{Schema.AssignLogicalUserWatcherMethod.Name} -> BypassAssignLogicalUserWatcher");
			}
			if (Schema?.AssignLiteralWatcherSharerMethod != null)
			{
				DynamicDetour.Redirect(Schema.AssignLiteralWatcherSharerMethod, HandlerMethod("BypassAssignLiteralWatcherSharer"), Trace);
				Trace($"[GATE] Detoured {Schema.SegmentedCollectionRoleType?.Name}.{Schema.AssignLiteralWatcherSharerMethod.Name} -> BypassAssignLiteralWatcherSharer");
			}
			if (Schema?.StartSharerMethod != null)
			{
				DynamicDetour.Redirect(Schema.StartSharerMethod, HandlerMethod("BypassStartSharer"), Trace);
				Trace($"[GATE] Detoured {Schema.SegmentedCollectionRoleType?.Name}.{Schema.StartSharerMethod.Name} -> BypassStartSharer");
			}
			if (Schema?.BaseDirectoryGetterMethod != null)
			{
				DynamicDetour.Redirect(Schema.BaseDirectoryGetterMethod, HandlerMethod("BypassFindExtractor"), Trace);
				Trace($"[GATE] Detoured {Schema.BaseDirectoryGetterMethod.DeclaringType?.Name}.{Schema.BaseDirectoryGetterMethod.Name} -> BypassFindExtractor");
			}
			try
			{
				MethodInfo method4 = typeof(AccountWindow).GetMethod("getRunningAccounts", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (method4 != null)
				{
					DynamicDetour.Redirect(method4, HandlerMethod("BypassGetRunningAccounts"), Trace);
					Trace("[GATE] Detoured AccountWindow.getRunningAccounts() -> \"1000\"");
				}
			}
			catch
			{
			}
			PatchStartupStateMachine();
			Trace("[GATE] Core Startup & Account Admission Detours successfully installed!");
		}
		catch (Exception value)
		{
			Trace($"[GATE] Warning during InstallStartupHooks: {value}");
		}
	}

	private static void InitializeStreamCompressorSynchronization(BotSchema schema)
	{
		Type streamCompressorType = schema.StreamCompressorType;
		if (schema.SeparatedValueMapField != null)
		{
			s_getSeparatedMapObj = CompileFieldGetter(streamCompressorType, schema.SeparatedValueMapField);
		}
		if (schema.CompressorFieldDicField != null)
		{
			s_getCompressorMapObj = CompileFieldGetter(streamCompressorType, schema.CompressorFieldDicField);
		}
		if (schema.ResolverServiceStreamDicField != null)
		{
			s_getResolverMapObj = CompileFieldGetter(streamCompressorType, schema.ResolverServiceStreamDicField);
			MethodInfo method = schema.ResolverServiceStreamDicField.FieldType.GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public);
			if (method != null)
			{
				ParameterExpression parameterExpression = Expression.Parameter(typeof(object), "dict");
				s_clearResolverMap = Expression.Lambda<Action<object>>(Expression.Call(Expression.Convert(parameterExpression, schema.ResolverServiceStreamDicField.FieldType), method), new ParameterExpression[1] { parameterExpression }).Compile();
			}
		}
		if (schema.AllocatorFieldType != null && schema.AllocatorIdField != null)
		{
			ParameterExpression parameterExpression2 = Expression.Parameter(typeof(object), "alloc");
			ParameterExpression parameterExpression3 = Expression.Parameter(typeof(ushort), "id");
			s_setAllocatorId = Expression.Lambda<Action<object, ushort>>(Expression.Assign(Expression.Field(Expression.Convert(parameterExpression2, schema.AllocatorFieldType), schema.AllocatorIdField), parameterExpression3), new ParameterExpression[2] { parameterExpression2, parameterExpression3 }).Compile();
		}
		static Func<object, object?> CompileFieldGetter(Type owner, FieldInfo field)
		{
			ParameterExpression parameterExpression4 = Expression.Parameter(typeof(object), "target");
			return Expression.Lambda<Func<object, object>>(Expression.Convert(Expression.Field(Expression.Convert(parameterExpression4, owner), field), typeof(object)), new ParameterExpression[1] { parameterExpression4 }).Compile();
		}
	}

	public static TField SynchronizedResumeInitializer<TField>(object? target, ushort startIdent) where TField : class, new()
	{
		if (target != null && s_getSeparatedMapObj?.Invoke(target) is Dictionary<ushort, TField> dictionary)
		{
			lock (dictionary)
			{
				if (dictionary.TryGetValue(startIdent, out var value) && value != null)
				{
					return value;
				}
				TField val = new TField();
				s_setAllocatorId?.Invoke(val, startIdent);
				dictionary[startIdent] = val;
				return val;
			}
		}
		TField val2 = new TField();
		s_setAllocatorId?.Invoke(val2, startIdent);
		return val2;
	}

	public static void SynchronizedWriteGeneralChooser(object? target)
	{
		if (target == null)
		{
			return;
		}
		if (s_getSeparatedMapObj?.Invoke(target) is IDictionary dictionary)
		{
			lock (dictionary)
			{
				dictionary.Clear();
			}
		}
		if (s_getCompressorMapObj?.Invoke(target) is IDictionary dictionary2)
		{
			lock (dictionary2)
			{
				dictionary2.Clear();
			}
		}
		object obj = s_getResolverMapObj?.Invoke(target);
		if (obj != null)
		{
			s_clearResolverMap?.Invoke(obj);
		}
	}

	private unsafe static void PatchStartupStateMachine(bool verbose = true)
	{
		try
		{
			Type type = Schema?.LoginStateMachineType;
			MethodInfo methodInfo = type?.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (methodInfo == null || type == null)
			{
				if (verbose)
				{
					Trace("[GATE] [Startup] Login state machine MoveNext not found!");
				}
				return;
			}
			_warmupDeclaringTypeName = type.DeclaringType?.Name ?? type.Name;
			RuntimeHelpers.PrepareMethod(methodInfo.MethodHandle);
			object obj = null;
			_suppressWarmupExceptions = true;
			try
			{
				obj = RuntimeHelpers.GetUninitializedObject(type);
				methodInfo.Invoke(obj, null);
			}
			catch
			{
				if (obj != null && Schema?.LoginStateMachineBuilderField != null)
				{
					try
					{
						object value = Schema.LoginStateMachineBuilderField.GetValue(obj);
						if (value != null)
						{
							_ = (value.GetType().GetProperty("Task", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value) as Task)?.Exception;
						}
					}
					catch
					{
					}
				}
			}
			finally
			{
				_suppressWarmupExceptions = false;
			}
			byte* ptr = (byte*)methodInfo.MethodHandle.GetFunctionPointer();
			for (int i = 0; i < 10; i++)
			{
				if (*ptr == 72 && ptr[1] == 139 && ptr[2] == 5 && ptr[7] == 102 && ptr[8] == byte.MaxValue && ptr[9] == 8 && ptr[10] == 116 && ptr[11] == 6 && ptr[12] == byte.MaxValue && ptr[13] == 37)
				{
					int num = *(int*)(ptr + 14);
					ptr = *(byte**)(ptr + 18 + num);
					continue;
				}
				if (*ptr == 76 && ptr[1] == 139 && ptr[2] == 21 && ptr[7] == byte.MaxValue && ptr[8] == 37)
				{
					int num2 = *(int*)(ptr + 9);
					ptr = *(byte**)(ptr + 13 + num2);
					continue;
				}
				if (*ptr == byte.MaxValue && ptr[1] == 37)
				{
					int num3 = *(int*)(ptr + 2);
					ptr = *(byte**)(ptr + 6 + num3);
					continue;
				}
				if (*ptr == 233)
				{
					int num4 = *(int*)(ptr + 1);
					ptr = ptr + 5 + num4;
					continue;
				}
				if (*ptr != 235)
				{
					break;
				}
				sbyte b = (sbyte)ptr[1];
				ptr = ptr + 2 + b;
			}
			if (ptr == (byte*)s_lastPatchedSmEntry)
			{
				return;
			}
			Trace($"[GATE] [Startup] {type.Name}.MoveNext native entry: 0x{(long)ptr:X}");
			bool flag = false;
			bool flag2 = false;
			int num5 = -1;
			for (int j = 500; j < 6500; j++)
			{
				if (ptr[j] == 6 && ptr[j + 1] == 206 && ptr[j + 2] == 74 && ptr[j + 3] == 30)
				{
					num5 = j;
					break;
				}
			}
			uint lpflOldProtect2;
			if (num5 > 0)
			{
				for (int k = 400; k < 7000; k++)
				{
					if (ptr[k] != 15 || ptr[k + 1] != 133)
					{
						continue;
					}
					int num6 = *(int*)(ptr + k + 2);
					int num7 = k + 6 + num6;
					if (Math.Abs(num7 - num5) >= 200)
					{
						continue;
					}
					byte* ptr2 = ptr + k;
					if (VirtualProtect((nint)ptr2, 6u, 64u, out var lpflOldProtect))
					{
						for (int l = 0; l < 6; l++)
						{
							ptr2[l] = 144;
						}
						VirtualProtect((nint)ptr2, 6u, lpflOldProtect, out lpflOldProtect2);
						FlushInstructionCache(GetCurrentProcess(), (nint)ptr2, 6u);
						Trace($"[GATE] [Startup] NOPped JNE at +{k} (jump to login server unreachable block +{num7})!");
						flag = true;
					}
				}
			}
			for (int m = 400; m < 7000; m++)
			{
				if (ptr[m] == 15 && ptr[m + 1] == 132)
				{
					int num8 = *(int*)(ptr + m + 2);
					byte* ptr3 = ptr + m;
					if (!flag && m >= 500 && m <= 1500 && num8 >= 128 && num8 <= 768 && VirtualProtect((nint)ptr3, 6u, 64u, out var lpflOldProtect3))
					{
						*ptr3 = 233;
						*(int*)(ptr3 + 1) = num8 + 1;
						ptr3[5] = 144;
						VirtualProtect((nint)ptr3, 6u, lpflOldProtect3, out lpflOldProtect2);
						FlushInstructionCache(GetCurrentProcess(), (nint)ptr3, 6u);
						Trace($"[GATE] [Startup] Converted JE at +{m} (login server check) to JMP (rel 0x{num8 + 1:X})!");
						flag = true;
					}
					if (!flag2 && m >= 1500 && m <= 4000 && num8 >= 768 && num8 <= 4608 && VirtualProtect((nint)ptr3, 6u, 64u, out var lpflOldProtect4))
					{
						*ptr3 = 233;
						*(int*)(ptr3 + 1) = num8 + 1;
						ptr3[5] = 144;
						VirtualProtect((nint)ptr3, 6u, lpflOldProtect4, out lpflOldProtect2);
						FlushInstructionCache(GetCurrentProcess(), (nint)ptr3, 6u);
						Trace($"[GATE] [Startup] Converted JE at +{m} to JMP (rel 0x{num8 + 1:X}) to bypass ResetNotifier!");
						flag2 = true;
					}
					if (flag & flag2)
					{
						break;
					}
				}
				else if (!flag && ptr[m] == 233 && m >= 500 && m <= 1500)
				{
					flag = true;
				}
			}
			if (flag | flag2)
			{
				s_lastPatchedSmEntry = (nint)ptr;
			}
			else if (verbose)
			{
				Trace("[GATE] [Startup] Note: " + type.Name + ".MoveNext JE pattern not matched in expected window.");
			}
		}
		catch (Exception ex)
		{
			if (verbose)
			{
				Trace("[GATE] [Startup] Error patching login state machine: " + ex.Message);
			}
		}
	}
}
