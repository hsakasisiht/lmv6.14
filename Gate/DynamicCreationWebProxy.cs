using System;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using LordsMobileBot;
using System.Threading;

namespace LordsBotGate;

public sealed record ParsedProxyInfo(string Host, int Port, string? Username, string? Password);

public sealed class DynamicCreationWebProxy : IWebProxy
{
    public static DynamicCreationWebProxy Instance { get; } = new();

    private static readonly AsyncLocal<string?> s_asyncActiveProxy = new();
    private static volatile string? s_globalActiveProxy;

    public static string? ActiveProxyString
    {
        get => s_asyncActiveProxy.Value ?? s_globalActiveProxy;
        set
        {
            s_asyncActiveProxy.Value = value;
            s_globalActiveProxy = value;
        }
    }

    public ICredentials? Credentials
    {
        get
        {
            var info = ResolveCurrentProxy();
            if (info != null && !string.IsNullOrEmpty(info.Username))
            {
                return new NetworkCredential(info.Username, info.Password ?? string.Empty);
            }
            return null;
        }
        set { }
    }

    public Uri? GetProxy(Uri destination)
    {
        if (IsBypassed(destination))
        {
            return destination;
        }

        var info = ResolveCurrentProxy();
        if (info != null)
        {
            string scheme = "http";
            string? act = ActiveProxyString;
            if (!string.IsNullOrEmpty(act) && act.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase))
            {
                scheme = "socks5";
            }
            return new Uri($"{scheme}://{info.Host}:{info.Port}");
        }

        return destination;
    }

    public bool IsBypassed(Uri host)
    {
        if (host == null) return true;

        // Loopback / localhost must NEVER be proxied (auth server, local services)
        if (host.IsLoopback ||
            host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check if we should proxy this request
        var info = ResolveCurrentProxy();
        if (info == null)
        {
            return true;
        }

        return false;
    }

    public static ParsedProxyInfo? ResolveCurrentProxy()
    {
        // 1. Check explicit ActiveProxyString (set for the current creation call)
        string? pStr = ActiveProxyString;
        if (!string.IsNullOrEmpty(pStr) && pStr.Contains(":"))
        {
            return ParseProxy(pStr);
        }

        // 2. Check active bulk create form proxy
        if (Program.IsBulkCreateActive)
        {
            try
            {
                var aw = AccountWindow.Instance;
                var formField = aw?.GetType().GetField("createAccForm", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var bulkForm = formField?.GetValue(null);
                if (bulkForm != null)
                {
                    var ft = bulkForm.GetType();
                    var BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
                    var selList = (ft.GetField("selectedProxies", BF)?.GetValue(bulkForm) as System.Collections.IList)
                                  ?? (ft.GetField("proxyList", BF)?.GetValue(bulkForm) as System.Collections.IList);
                    if (selList != null && selList.Count > 0)
                    {
                        int curIdx = 0;
                        try { curIdx = Convert.ToInt32(ft.GetField("currentProxy", BF)?.GetValue(null) ?? 0); } catch { }
                        if (curIdx < 0 || curIdx >= selList.Count) curIdx = 0;
                        var pObj = selList[curIdx];
                        if (pObj != null)
                        {
                            if (pObj is proxyRecord pr)
                            {
                                if (!string.IsNullOrEmpty(pr.IP) && pr.Port > 0)
                                {
                                    string proxyString = string.IsNullOrEmpty(pr.Username) ? $"{pr.IP}:{pr.Port}" : $"{pr.IP}:{pr.Port}:{pr.Username}:{pr.Password}";
                                    return ParseProxy(proxyString);
                                }
                            }
                            var pt = pObj.GetType();
                            string ip = pt.GetField("IP_", BF)?.GetValue(pObj) as string ?? pt.GetProperties(BF).FirstOrDefault(p => p.Name == "IP")?.GetValue(pObj) as string ?? "";
                            int port = Convert.ToInt32(pt.GetField("Port_", BF)?.GetValue(pObj) ?? pt.GetProperties(BF).FirstOrDefault(p => p.Name == "Port")?.GetValue(pObj) ?? 0);
                            string user = pt.GetField("Username_", BF)?.GetValue(pObj) as string ?? pt.GetProperties(BF).FirstOrDefault(p => p.Name == "Username")?.GetValue(pObj) as string ?? "";
                            string pass = pt.GetField("Password_", BF)?.GetValue(pObj) as string ?? pt.GetProperties(BF).FirstOrDefault(p => p.Name == "Password")?.GetValue(pObj) as string ?? "";
                            if (!string.IsNullOrEmpty(ip) && port > 0)
                            {
                                string proxyString = string.IsNullOrEmpty(user) ? $"{ip}:{port}" : $"{ip}:{port}:{user}:{pass}";
                                return ParseProxy(proxyString);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        // 3. Fallback to pool proxy if creation is active
        bool isCreation = Program.IsSingleCreateActive || Program.IsBulkCreateActive;
        if (isCreation)
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string fallback = Program.GetNextAvailableProxy(baseDir);
                if (!string.IsNullOrEmpty(fallback) && fallback.Contains(":"))
                {
                    return ParseProxy(fallback);
                }
            }
            catch { }
        }

        return null;
    }

    public static ParsedProxyInfo? ParseProxy(string proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy)) return null;

        try
        {
            // Remove any prefix like "(0) " or whitespace
            proxy = Regex.Replace(proxy.Trim(), @"^\(\d+\)\s*", "");

            // If URI format: http://user:pass@host:port or host:port
            if (proxy.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                proxy.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                proxy.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(proxy);
                string? user = null;
                string? pass = null;
                if (!string.IsNullOrEmpty(uri.UserInfo))
                {
                    var uParts = uri.UserInfo.Split(':');
                    user = uParts[0];
                    if (uParts.Length > 1) pass = uParts[1];
                }
                int port = uri.Port > 0 ? uri.Port : 8080;
                if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
                {
                    HydrateCredentials(uri.Host, ref port, ref user, ref pass);
                }
                return new ParsedProxyInfo(uri.Host, port, user, pass);
            }

            var parts = proxy.Split(':');
            if (parts.Length >= 2)
            {
                string host = parts[0].Trim();
                if (!int.TryParse(parts[1].Trim(), out int port)) port = 8080;
                string? user = parts.Length > 2 ? parts[2].Trim() : null;
                string? pass = parts.Length > 3 ? parts[3].Trim() : null;
                if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
                {
                    HydrateCredentials(host, ref port, ref user, ref pass);
                }
                return new ParsedProxyInfo(host, port, user, pass);
            }
        }
        catch { }

        return null;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Port, string User, string Pass)> s_knownProxyCredentials = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime s_lastHydrateLoadTime = DateTime.MinValue;

    public static void HydrateCredentials(string host, ref int port, ref string? user, ref string? pass)
    {
        if (string.IsNullOrEmpty(host)) return;

        EnsureProxyCredentialsLoaded();

        // 1. Try exact match by "host:port"
        if (port > 0 && s_knownProxyCredentials.TryGetValue($"{host}:{port}", out var credExact))
        {
            if (string.IsNullOrEmpty(user)) user = credExact.User;
            if (string.IsNullOrEmpty(pass)) pass = credExact.Pass;
            return;
        }

        // 2. Try match by host only
        if (s_knownProxyCredentials.TryGetValue(host, out var credHost))
        {
            if (string.IsNullOrEmpty(user)) user = credHost.User;
            if (string.IsNullOrEmpty(pass)) pass = credHost.Pass;
            if (port <= 0 || port == 8080) port = credHost.Port;
            return;
        }
    }

    public static void EnsureProxyCredentialsLoaded(bool force = false)
    {
        if (!force && (DateTime.UtcNow - s_lastHydrateLoadTime).TotalSeconds < 30 && !s_knownProxyCredentials.IsEmpty)
            return;

        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var list = Program.LoadProxiesFromDisk(baseDir);
            foreach (var p in list)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                var parts = p.Split(':');
                if (parts.Length >= 4)
                {
                    string h = parts[0].Trim();
                    if (int.TryParse(parts[1].Trim(), out int pt))
                    {
                        string u = parts[2].Trim();
                        string pw = parts[3].Trim();
                        s_knownProxyCredentials[$"{h}:{pt}"] = (pt, u, pw);
                        s_knownProxyCredentials[h] = (pt, u, pw);
                    }
                }
            }
            s_lastHydrateLoadTime = DateTime.UtcNow;
        }
        catch { }
    }
}
