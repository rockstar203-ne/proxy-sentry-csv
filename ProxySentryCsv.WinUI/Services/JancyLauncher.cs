using System.Net;
using System.Text;
using System.Text.Json;
using ProxySentryCsv.Models;

namespace ProxySentryCsv.Services;

public sealed class JancyLauncher
{
    private const string BridgeFileName = "proxy-sentry-csv.jyl";
    private const string LegacyBridgeFileName = "axs-proxy-launcher.jyl";
    private const string BridgeVersion = "1.14";
    private const string EndpointPath = "proxy-sentry-csv";
    private const string ProfileGroupName = "Proxy Sentry CSV";
    private const string EmailDomain = "proxy-sentry-csv.local";
    private const string WindowNamePrefix = "ProxySentryCSV-";
    private readonly string _appDataPath;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(8) };

    public JancyLauncher(string appDataPath)
    {
        _appDataPath = appDataPath;
    }

    public string InstallBridge()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            throw new InvalidOperationException("Could not resolve %AppData%.");
        }

        var jylDir = Path.Combine(appData, "Jancy", "jyl");
        Directory.CreateDirectory(jylDir);
        var legacyBridgePath = Path.Combine(jylDir, LegacyBridgeFileName);
        if (File.Exists(legacyBridgePath))
        {
            File.Delete(legacyBridgePath);
        }

        var bridgePath = Path.Combine(jylDir, BridgeFileName);
        File.WriteAllText(bridgePath, BridgeJyl, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return bridgePath;
    }

    public string ExportProfiles(IEnumerable<ProxyEntry> proxies, string targetUrl)
    {
        var exportDir = Path.Combine(_appDataPath, "Jancy");
        Directory.CreateDirectory(exportDir);
        var path = Path.Combine(exportDir, "proxy-sentry-csv-profiles.csv");
        var builder = new StringBuilder();
        builder.AppendLine("Profile,Name,Email,Proxy,Persist Cookies,Persist Sessions,URL,Time Zone,Notes");

        foreach (var proxy in proxies)
        {
            builder.Append(Csv(proxy.ProfileKey));
            builder.Append(',');
            builder.Append(Csv(GetProfileName(proxy)));
            builder.Append(',');
            builder.Append(Csv($"{proxy.ProfileKey}@{EmailDomain}"));
            builder.Append(',');
            builder.Append(Csv(ToJancyProxy(proxy)));
            builder.Append(",1,1,");
            builder.Append(Csv(targetUrl));
            builder.Append(',');
            builder.Append(Csv(""));
            builder.Append(',');
            builder.Append(Csv($"Managed by Proxy Sentry CSV; row {proxy.RowNumber}; stable id {proxy.StableId}"));
            builder.AppendLine();
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    public async Task LaunchAsync(ProxyEntry proxy, string targetUrl, string endpointUrl, SyncMonitorOption? monitor = null, int? windowIndex = null, int? windowCount = null)
    {
        var bridgePath = InstallBridge();
        var endpoint = NormalizeEndpoint(endpointUrl);
        await EnsureBridgeLoadedAsync(endpoint, bridgePath);
        var query = new Dictionary<string, string?>
        {
            ["stableId"] = proxy.StableId,
            ["profileKey"] = proxy.ProfileKey,
            ["group"] = ProfileGroupName,
            ["profile"] = proxy.ProfileKey,
            ["name"] = GetProfileName(proxy),
            ["email"] = $"{proxy.ProfileKey}@{EmailDomain}",
            ["url"] = targetUrl,
            ["windowName"] = $"{WindowNamePrefix}{proxy.ProfileKey}",
            ["proxyHost"] = proxy.Host,
            ["proxyPort"] = proxy.Port.ToString(),
            ["proxyUsername"] = proxy.Username,
            ["proxyPassword"] = proxy.Password
        };

        if (monitor is not null && windowIndex.HasValue && windowCount.HasValue)
        {
            var rect = ComputeWindowRect(monitor, windowIndex.Value, windowCount.Value);
            query["x"] = rect.X.ToString();
            query["y"] = rect.Y.ToString();
            query["width"] = rect.Width.ToString();
            query["height"] = rect.Height.ToString();
        }

        await SendAsync(endpoint, "open", query);
    }

    private async Task EnsureBridgeLoadedAsync(string endpoint, string bridgePath)
    {
        JancyBridgeResponse response;
        try
        {
            response = await SendAsync(endpoint, "version", new Dictionary<string, string?>());
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or HttpRequestException)
        {
            throw new InvalidOperationException(
                $"Jancy has not loaded the current bridge yet. The file was installed to {bridgePath}. In Jancy open Settings -> JYL Scripts and press Rescan JYL directory, or restart Jancy. Expected bridge version {BridgeVersion}.",
                ex);
        }

        if (!string.Equals(response.Version, BridgeVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Jancy is still running bridge version '{response.Version}', but this launcher needs '{BridgeVersion}'. Press Rescan JYL directory in Jancy Settings -> JYL Scripts, or restart Jancy.");
        }
    }

    public async Task CloseAsync(ProxyEntry proxy, string endpointUrl)
    {
        var bridgePath = InstallBridge();
        var endpoint = NormalizeEndpoint(endpointUrl);
        await EnsureBridgeLoadedAsync(endpoint, bridgePath);
        await SendAsync(endpoint, "close", new Dictionary<string, string?>
        {
            ["stableId"] = proxy.StableId,
            ["profileKey"] = proxy.ProfileKey,
            ["windowName"] = $"{WindowNamePrefix}{proxy.ProfileKey}"
        });
    }

    public async Task StartSyncAsync(IReadOnlyList<ProxyEntry> proxies, string endpointUrl)
    {
        if (proxies.Count < 2)
        {
            throw new InvalidOperationException("Select at least two opened Jancy rows. The first row is the sync master.");
        }

        var bridgePath = InstallBridge();
        var endpoint = NormalizeEndpoint(endpointUrl);
        await EnsureBridgeLoadedAsync(endpoint, bridgePath);
        await SendAsync(endpoint, "sync-start", new Dictionary<string, string?>
        {
            ["masterStableId"] = proxies[0].StableId,
            ["stableIds"] = string.Join(",", proxies.Select(proxy => proxy.StableId))
        });
    }

    public async Task StopSyncAsync(string endpointUrl)
    {
        var bridgePath = InstallBridge();
        var endpoint = NormalizeEndpoint(endpointUrl);
        await EnsureBridgeLoadedAsync(endpoint, bridgePath);
        await SendAsync(endpoint, "sync-stop", new Dictionary<string, string?>());
    }

    public async Task ArrangeAsync(IReadOnlyList<ProxyEntry> proxies, string endpointUrl, SyncMonitorOption? monitor)
    {
        if (proxies.Count < 2)
        {
            throw new InvalidOperationException("Select at least two opened Jancy rows to align.");
        }

        if (monitor is null)
        {
            throw new InvalidOperationException("Select a sync monitor before aligning windows.");
        }

        var bridgePath = InstallBridge();
        var endpoint = NormalizeEndpoint(endpointUrl);
        await EnsureBridgeLoadedAsync(endpoint, bridgePath);
        var query = new Dictionary<string, string?>
        {
            ["stableIds"] = string.Join(",", proxies.Select(proxy => proxy.StableId))
        };

        for (var index = 0; index < proxies.Count; index++)
        {
            var rect = ComputeWindowRect(monitor, index, proxies.Count);
            query[$"x{index}"] = rect.X.ToString();
            query[$"y{index}"] = rect.Y.ToString();
            query[$"width{index}"] = rect.Width.ToString();
            query[$"height{index}"] = rect.Height.ToString();
        }

        await SendAsync(endpoint, "arrange", query);
    }

    public async Task<IReadOnlyList<string>> ListAsync(string endpointUrl)
    {
        var endpoint = NormalizeEndpoint(endpointUrl);
        var response = await SendAsync(endpoint, "list", new Dictionary<string, string?>());
        return response.Tabs?.Select(tab => tab.StableId).Where(id => !string.IsNullOrWhiteSpace(id)).ToList() ?? [];
    }

    private async Task<JancyBridgeResponse> SendAsync(string endpoint, string action, IReadOnlyDictionary<string, string?> query)
    {
        var uri = BuildUri(endpoint, action, query);
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(uri);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Jancy Web Interface is not reachable at {endpoint}. Enable it in Jancy Advanced settings and rescan/restart Jancy after bridge install.", ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new TimeoutException($"Jancy Web Interface did not respond at {endpoint}.", ex);
        }

        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.NotFound && text.Contains("not a valid endpoint", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Jancy bridge endpoint is not loaded: {uri}. Press Install Jancy bridge, then in Jancy use Rescan JYL directory or restart Jancy.");
            }

            throw new InvalidOperationException($"Jancy bridge returned HTTP {(int)response.StatusCode}: {text}");
        }

        JancyBridgeResponse? payload;
        try
        {
            payload = JsonSerializer.Deserialize<JancyBridgeResponse>(text, AppJsonOptions.CaseInsensitive);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Jancy bridge returned non-JSON response: {text}", ex);
        }

        payload ??= new JancyBridgeResponse();
        if (!payload.Success)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(payload.Message)
                ? $"Jancy bridge reported failure. Raw response: {text}"
                : payload.Message);
        }

        return payload;
    }

    private static string NormalizeEndpoint(string endpointUrl)
    {
        var endpoint = string.IsNullOrWhiteSpace(endpointUrl)
            ? "http://127.0.0.1:43033"
            : endpointUrl.Trim();

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Jancy endpoint must be an http:// or https:// URL, for example http://127.0.0.1:43033.");
        }

        return endpoint.TrimEnd('/');
    }

    private static string BuildUri(string endpoint, string action, IReadOnlyDictionary<string, string?> query)
    {
        var builder = new StringBuilder(endpoint);
        builder.Append("/extra/");
        builder.Append(EndpointPath);
        builder.Append('/');
        builder.Append(Uri.EscapeDataString(action));

        if (query.Count == 0)
        {
            return builder.ToString();
        }

        builder.Append('?');
        var first = true;
        foreach (var pair in query)
        {
            if (string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }

            if (!first)
            {
                builder.Append('&');
            }

            first = false;
            builder.Append(Uri.EscapeDataString(pair.Key));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(pair.Value));
        }

        return builder.ToString();
    }

    private static string GetProfileName(ProxyEntry proxy)
    {
        return string.IsNullOrWhiteSpace(proxy.DisplayName)
            ? $"Proxy {proxy.RowNumber}"
            : proxy.DisplayName.Trim();
    }

    private static string ToJancyProxy(ProxyEntry proxy)
    {
        return string.IsNullOrWhiteSpace(proxy.Username)
            ? $"{proxy.Host}:{proxy.Port}"
            : $"{proxy.Host}:{proxy.Port}:{proxy.Username}:{proxy.Password}";
    }

    private static string Csv(string? value)
    {
        var text = value ?? "";
        return text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r')
            ? $"\"{text.Replace("\"", "\"\"")}\""
            : text;
    }

    private static WindowRect ComputeWindowRect(SyncMonitorOption monitor, int index, int count)
    {
        var columns = count switch
        {
            <= 1 => 1,
            <= 4 => 2,
            <= 9 => 3,
            _ => 4
        };
        var rows = (int)Math.Ceiling(count / (double)columns);
        const int gap = 8;
        var width = Math.Max(240, (monitor.AvailWidth - (columns - 1) * gap) / columns);
        var height = Math.Max(220, (monitor.AvailHeight - (rows - 1) * gap) / rows);
        var col = index % columns;
        var row = index / columns;
        return new WindowRect(
            monitor.AvailLeft + col * (width + gap),
            monitor.AvailTop + row * (height + gap),
            width,
            height);
    }

    private sealed record WindowRect(int X, int Y, int Width, int Height);

    private sealed class JancyBridgeResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public string Version { get; set; } = "";
        public List<JancyBridgeTab> Tabs { get; set; } = [];
    }

    private sealed class JancyBridgeTab
    {
        public string StableId { get; set; } = "";
    }

private const string BridgeJyl = """
<jyl user-version="1.14" user-desc="Proxy Sentry CSV bridge">
  <webinterface-endpoint path="proxy-sentry-csv/version">
    return { success: true, version: "1.14" };
  </webinterface-endpoint>

  <webinterface-endpoint path="proxy-sentry-csv/list">
    var allTabs = jancy.tabManager.getAllTabs();
    var tabs = [];
    for (var i = 0; i !== allTabs.length; i = i + 1) {
      var tab = allTabs[i];
      var data = tab.data || {};
      if (data.proxySentryCsv) {
        tabs.push({
          tabId: tab.uuid || "",
          stableId: data.proxySentryCsvStableId || "",
          profileKey: data.proxySentryCsvProfileKey || "",
          url: tab.url || "",
          title: tab.title || ""
        });
      }
    }
    return { success: true, tabs: tabs };
  </webinterface-endpoint>

  <webinterface-endpoint path="proxy-sentry-csv/open">
    try {
      function decodeQueryValue(value) {
        value = String(value || "").replace(/\+/g, " ");
        try {
          return decodeURIComponent(value);
        }
        catch (err) {
          return value;
        }
      }

      function getParam(name) {
        if (parsedURL) {
          if (parsedURL.searchParams && parsedURL.searchParams.get) {
            var directValue = parsedURL.searchParams.get(name);
            if (directValue !== undefined && directValue !== null) {
              return directValue;
            }
          }

          if (parsedURL.query && typeof parsedURL.query === "object") {
            var objectValue = parsedURL.query[name];
            if (objectValue !== undefined && objectValue !== null) {
              if (objectValue && objectValue.length && typeof objectValue !== "string") {
                objectValue = objectValue[0];
              }
              return String(objectValue);
            }
          }
        }

        var raw = "";
        if (parsedURL) {
          if (typeof parsedURL.query === "string") {
            raw = parsedURL.query;
          }
          else if (typeof parsedURL.search === "string") {
            raw = parsedURL.search;
          }
          else if (typeof parsedURL.href === "string") {
            raw = parsedURL.href;
          }
          else if (typeof parsedURL.path === "string") {
            raw = parsedURL.path;
          }
        }

        if (!raw && req && typeof req.url === "string") {
          raw = req.url;
        }

        var questionIndex = raw.indexOf("?");
        if (questionIndex >= 0) {
          raw = raw.slice(questionIndex + 1);
        }
        if (raw.charAt(0) === "?") {
          raw = raw.slice(1);
        }
        if (!raw) {
          return null;
        }

        var parts = raw.split("&");
        for (var i = 0; i !== parts.length; i = i + 1) {
          var part = parts[i];
          if (!part) {
            continue;
          }

          var equalsIndex = part.indexOf("=");
          var key = equalsIndex >= 0 ? part.slice(0, equalsIndex) : part;
          var value = equalsIndex >= 0 ? part.slice(equalsIndex + 1) : "";
          if (decodeQueryValue(key) === name) {
            return decodeQueryValue(value);
          }
        }

        return null;
      }

      function formatError(err) {
        return {
          success: false,
          message: err ? (err.message ? err.message : String(err)) : "unknown Jancy bridge error"
        };
      }

      function send(payload) {
        var text = JSON.stringify(payload || { success: false, message: "empty Jancy bridge response" });
        if (res && res.writeHead && res.end) {
          res.writeHead(200, { "Content-Type": "application/json" });
          res.end(text);
          return null;
        }
        return payload;
      }

      function sendError(err) {
        return send(formatError(err));
      }

      var stableId = getParam("stableId") || "";
      var profileKey = getParam("profileKey") || stableId.slice(0, 16);
      var displayName = getParam("name") || profileKey || "Proxy Sentry";
      var url = getParam("url") || "about:blank";
      var windowName = getParam("windowName") || ("ProxySentryCSV-" + profileKey);
      var groupName = getParam("group") || "Proxy Sentry CSV";
      var profileName = getParam("profile") || profileKey;
      var email = getParam("email") || (profileKey + "@proxy-sentry-csv.local");
      var proxyHost = getParam("proxyHost") || "";
      var proxyPort = parseInt(getParam("proxyPort") || "0", 10);
      var proxyUsername = getParam("proxyUsername") || null;
      var proxyPassword = getParam("proxyPassword") || null;
      var timezone = getParam("timezone") || null;
      var requestedX = parseInt(getParam("x") || "", 10);
      var requestedY = parseInt(getParam("y") || "", 10);
      var requestedWidth = parseInt(getParam("width") || "", 10);
      var requestedHeight = parseInt(getParam("height") || "", 10);

      function makeWindowOptions(tab) {
        var options = { name: windowName };
        if (tab) {
          options.tabs = [tab];
          options.activeTab = tab.uuid;
        }
        if (!isNaN(requestedX)) {
          options.x = requestedX;
        }
        if (!isNaN(requestedY)) {
          options.y = requestedY;
        }
        if (!isNaN(requestedWidth) && requestedWidth > 0) {
          options.width = requestedWidth;
        }
        if (!isNaN(requestedHeight) && requestedHeight > 0) {
          options.height = requestedHeight;
        }
        return options;
      }

      function getTabFromWindow(tw) {
        var tab = null;
        if (!tw) {
          return null;
        }
        if (tw.getActiveTab) {
          tab = tw.getActiveTab();
        }
        if (!tab && tw.getTabs) {
          var windowTabs = tw.getTabs();
          if (windowTabs && windowTabs.length) {
            tab = windowTabs[windowTabs.length - 1];
          }
        }
        return tab;
      }

      function waitForActiveTab(tw) {
        return new Promise(function(resolve, reject) {
          var attemptsLeft = 20;
          function tick() {
            var tab = getTabFromWindow(tw);
            if (tab) {
              resolve(tab);
              return;
            }
            attemptsLeft = attemptsLeft - 1;
            if (attemptsLeft === 0) {
              reject(new Error("Jancy created " + windowName + ", but no active tab became available."));
              return;
            }
            setTimeout(tick, 100);
          }
          tick();
        });
      }

      function createManagedProxy() {
        if (!proxyHost || !proxyPort || !jancy.proxyFactory || !jancy.proxyFactory.createSync) {
          return null;
        }
        var proxyData = {
          name: "Proxy for " + displayName,
          host: proxyHost,
          port: proxyPort,
          username: proxyUsername,
          password: proxyPassword
        };
        if (timezone) {
          proxyData.timezone = timezone;
        }
        return jancy.proxyFactory.createSync(proxyData);
      }

      function registerManagedProfile(proxy) {
        if (!jancy.profileFactory || !jancy.profileFactory.create || !jancy.profileRegistry || !jancy.profileRegistry.addProfile) {
          return false;
        }
        if (jancy.profileRegistry.getGroups && jancy.profileRegistry.getProfilesFromGroup) {
          try {
            var groups = jancy.profileRegistry.getGroups({ filterByBuyerIDs: false });
            for (var groupIndex = 0; groups && groupIndex !== groups.length; groupIndex = groupIndex + 1) {
              var group = groups[groupIndex];
              if (!group || group.groupName !== groupName) {
                continue;
              }
              var profiles = jancy.profileRegistry.getProfilesFromGroup(group, { filterByBuyerIDs: false });
              for (var profileIndex = 0; profiles && profileIndex !== profiles.length; profileIndex = profileIndex + 1) {
                if (profiles[profileIndex] && profiles[profileIndex].profileName === profileName) {
                  return false;
                }
              }
            }
          }
          catch (err) {
          }
        }
        var profile = jancy.profileFactory.create({
          providerId: "proxy-sentry-csv",
          groupName: groupName,
          profileName: profileName,
          name: displayName,
          email: email,
          url: url,
          timezone: timezone,
          persistCookies: true,
          persistSessions: true
        });
        if (proxy && jancy.profileFactory.addProxy) {
          jancy.profileFactory.addProxy(profile, proxy);
        }
        jancy.profileRegistry.addProfile(profile);
        return true;
      }

      function applyManagedTab(tab, proxy, addProxyToPartition) {
        if (!tab) {
          throw new Error("No Jancy tab is available for " + windowName + ".");
        }

        if (jancy.tabManager && jancy.tabManager.updateTab) {
          jancy.tabManager.updateTab(tab, {
            updateData: {
              proxySentryCsv: true,
              proxySentryCsvStableId: stableId,
              proxySentryCsvProfileKey: profileKey,
              proxySentryCsvWindowName: windowName
            },
            subTitle: displayName
          }, true);
        }

        if (jancy.partitions && jancy.partitions.getPartition && tab.partitionId) {
          var partition = jancy.partitions.getPartition(tab.partitionId);
          if (partition && jancy.partitions.updatePartition) {
            jancy.partitions.updatePartition(partition, {
              profileInfo: {
                groupName: groupName,
                profileName: profileName
              }
            }, true);
          }

          if (partition && proxy && addProxyToPartition && jancy.partitions.addProxy) {
            jancy.partitions.addProxy(partition, proxy, true);
          }
        }
      }

      function navigateManagedTab(tw, tab, createdWindow, proxyApplied) {
        applyManagedTab(tab, createdProxy, proxyApplied);
        jancy.tabManager.navigateTab(tab, { mode: "url", url: url });
        return {
          success: true,
          tabId: tab.uuid || "",
          windowName: windowName,
          profile: profileKey,
          createdWindow: createdWindow,
          proxyHost: proxyHost,
          proxyApplied: !!createdProxy
        };
      }

      var createdProxy = createManagedProxy();
      registerManagedProfile(createdProxy);

      var existingWindow = null;
      if (jancy.windowManager && jancy.windowManager.getWindow) {
        existingWindow = jancy.windowManager.getWindow({ name: windowName });
      }

      if (existingWindow) {
        var existingTab = getTabFromWindow(existingWindow);
        return send(navigateManagedTab(existingWindow, existingTab, false, true));
      }

      if (!jancy.windowManager || !jancy.windowManager.createTabbedWindow) {
        return send({ success: false, message: "Jancy windowManager.createTabbedWindow is not available." });
      }

      Promise.resolve(jancy.windowManager.createTabbedWindow(makeWindowOptions(null)))
        .then(function(tw) {
          return waitForActiveTab(tw).then(function(activeTab) {
            return navigateManagedTab(tw, activeTab, true, true);
          });
        })
        .then(send)
        .catch(sendError);
      return null;
    }
    catch (err) {
      return send({
        success: false,
        message: err ? (err.message ? err.message : String(err)) : "unknown Jancy bridge error"
      });
    }
  </webinterface-endpoint>

  <webinterface-endpoint path="proxy-sentry-csv/arrange">
    try {
      function decodeQueryValue(value) {
        value = String(value || "").replace(/\+/g, " ");
        try {
          return decodeURIComponent(value);
        }
        catch (err) {
          return value;
        }
      }

      function getParam(name) {
        if (parsedURL) {
          if (parsedURL.searchParams && parsedURL.searchParams.get) {
            var directValue = parsedURL.searchParams.get(name);
            if (directValue !== undefined && directValue !== null) {
              return directValue;
            }
          }

          if (parsedURL.query && typeof parsedURL.query === "object") {
            var objectValue = parsedURL.query[name];
            if (objectValue !== undefined && objectValue !== null) {
              if (objectValue && objectValue.length && typeof objectValue !== "string") {
                objectValue = objectValue[0];
              }
              return String(objectValue);
            }
          }
        }

        var raw = "";
        if (parsedURL) {
          if (typeof parsedURL.query === "string") {
            raw = parsedURL.query;
          }
          else if (typeof parsedURL.search === "string") {
            raw = parsedURL.search;
          }
          else if (typeof parsedURL.href === "string") {
            raw = parsedURL.href;
          }
          else if (typeof parsedURL.path === "string") {
            raw = parsedURL.path;
          }
        }

        if (!raw && req && typeof req.url === "string") {
          raw = req.url;
        }

        var questionIndex = raw.indexOf("?");
        if (questionIndex >= 0) {
          raw = raw.slice(questionIndex + 1);
        }
        if (raw.charAt(0) === "?") {
          raw = raw.slice(1);
        }
        if (!raw) {
          return null;
        }

        var parts = raw.split("&");
        for (var i = 0; i !== parts.length; i = i + 1) {
          var part = parts[i];
          if (!part) {
            continue;
          }

          var equalsIndex = part.indexOf("=");
          var key = equalsIndex >= 0 ? part.slice(0, equalsIndex) : part;
          var value = equalsIndex >= 0 ? part.slice(equalsIndex + 1) : "";
          if (decodeQueryValue(key) === name) {
            return decodeQueryValue(value);
          }
        }

        return null;
      }

      function splitStableIds(value) {
        var parts = String(value || "").split(",");
        var ids = [];
        for (var i = 0; i !== parts.length; i = i + 1) {
          var id = String(parts[i] || "").trim();
          if (id) {
            ids.push(id);
          }
        }
        return ids;
      }

      function windowHasManagedTab(tw, stableId) {
        if (!tw || !tw.getTabs) {
          return false;
        }
        var tabs = tw.getTabs();
        for (var i = 0; tabs && i !== tabs.length; i = i + 1) {
          var data = tabs[i].data || {};
          if (data.proxySentryCsvStableId === stableId) {
            return true;
          }
        }
        return false;
      }

      function getWindowForStableId(stableId) {
        if (!jancy.windowManager || !jancy.windowManager.getWindows) {
          return null;
        }
        var windows = jancy.windowManager.getWindows(0);
        for (var i = 0; windows && i !== windows.length; i = i + 1) {
          if (windowHasManagedTab(windows[i], stableId)) {
            return windows[i];
          }
        }
        return null;
      }

      function applyBounds(tw, x, y, width, height) {
        if (!tw) {
          return false;
        }
        var bounds = { x: x, y: y, width: width, height: height };
        if (tw.browserWindow && tw.browserWindow.setBounds) {
          tw.browserWindow.setBounds(bounds);
          return true;
        }
        if (tw.setBounds) {
          tw.setBounds(bounds);
          return true;
        }
        if (tw.browserWindow && tw.browserWindow.setPosition && tw.browserWindow.setSize) {
          tw.browserWindow.setPosition(x, y);
          tw.browserWindow.setSize(width, height);
          return true;
        }
        if (tw.setPosition && tw.setSize) {
          tw.setPosition(x, y);
          tw.setSize(width, height);
          return true;
        }
        return false;
      }

      var stableIds = splitStableIds(getParam("stableIds") || "");
      var arranged = 0;
      for (var index = 0; index !== stableIds.length; index = index + 1) {
        var x = parseInt(getParam("x" + index) || "", 10);
        var y = parseInt(getParam("y" + index) || "", 10);
        var width = parseInt(getParam("width" + index) || "", 10);
        var height = parseInt(getParam("height" + index) || "", 10);
        if (isNaN(x) || isNaN(y) || isNaN(width) || isNaN(height)) {
          continue;
        }
        if (applyBounds(getWindowForStableId(stableIds[index]), x, y, width, height)) {
          arranged = arranged + 1;
        }
      }

      return { success: true, arranged: arranged };
    }
    catch (err) {
      return {
        success: false,
        message: err ? (err.message ? err.message : String(err)) : "unknown Jancy bridge error"
      };
    }
  </webinterface-endpoint>

  <webinterface-endpoint path="proxy-sentry-csv/sync-start">
    try {
      function decodeQueryValue(value) {
        value = String(value || "").replace(/\+/g, " ");
        try {
          return decodeURIComponent(value);
        }
        catch (err) {
          return value;
        }
      }

      function getParam(name) {
        if (parsedURL) {
          if (parsedURL.searchParams && parsedURL.searchParams.get) {
            var directValue = parsedURL.searchParams.get(name);
            if (directValue !== undefined && directValue !== null) {
              return directValue;
            }
          }

          if (parsedURL.query && typeof parsedURL.query === "object") {
            var objectValue = parsedURL.query[name];
            if (objectValue !== undefined && objectValue !== null) {
              if (objectValue && objectValue.length && typeof objectValue !== "string") {
                objectValue = objectValue[0];
              }
              return String(objectValue);
            }
          }
        }

        var raw = "";
        if (parsedURL) {
          if (typeof parsedURL.query === "string") {
            raw = parsedURL.query;
          }
          else if (typeof parsedURL.search === "string") {
            raw = parsedURL.search;
          }
          else if (typeof parsedURL.href === "string") {
            raw = parsedURL.href;
          }
          else if (typeof parsedURL.path === "string") {
            raw = parsedURL.path;
          }
        }

        if (!raw && req && typeof req.url === "string") {
          raw = req.url;
        }

        var questionIndex = raw.indexOf("?");
        if (questionIndex >= 0) {
          raw = raw.slice(questionIndex + 1);
        }
        if (raw.charAt(0) === "?") {
          raw = raw.slice(1);
        }
        if (!raw) {
          return null;
        }

        var parts = raw.split("&");
        for (var i = 0; i !== parts.length; i = i + 1) {
          var part = parts[i];
          if (!part) {
            continue;
          }

          var equalsIndex = part.indexOf("=");
          var key = equalsIndex >= 0 ? part.slice(0, equalsIndex) : part;
          var value = equalsIndex >= 0 ? part.slice(equalsIndex + 1) : "";
          if (decodeQueryValue(key) === name) {
            return decodeQueryValue(value);
          }
        }

        return null;
      }

      function formatError(err) {
        return {
          success: false,
          message: err ? (err.message ? err.message : String(err)) : "unknown Jancy bridge error"
        };
      }

      function send(payload) {
        var text = JSON.stringify(payload || { success: false, message: "empty Jancy bridge response" });
        if (res && res.writeHead && res.end) {
          res.writeHead(200, { "Content-Type": "application/json" });
          res.end(text);
          return null;
        }
        return payload;
      }

      function sendError(err) {
        return send(formatError(err));
      }

      function splitStableIds(value) {
        var parts = String(value || "").split(",");
        var ids = [];
        for (var i = 0; i !== parts.length; i = i + 1) {
          var id = String(parts[i] || "").trim();
          if (id) {
            ids.push(id);
          }
        }
        return ids;
      }

      function getSyncState() {
        if (!jancy.proxySentryCsvSync) {
          jancy.proxySentryCsvSync = {
            enabled: false,
            timer: null,
            busy: false,
            masterStableId: "",
            stableIds: [],
            lastError: ""
          };
        }
        return jancy.proxySentryCsvSync;
      }

      function stopSyncTimer() {
        var state = getSyncState();
        state.enabled = false;
        if (state.timer) {
          clearInterval(state.timer);
          state.timer = null;
        }
      }

      function getManagedTab(stableId) {
        var allTabs = jancy.tabManager.getAllTabs();
        for (var i = 0; i !== allTabs.length; i = i + 1) {
          var tab = allTabs[i];
          var data = tab.data || {};
          if (data.proxySentryCsvStableId === stableId) {
            return tab;
          }
        }
        return null;
      }

      function getManagedWebContents(tab) {
        if (!tab || !jancy.tabManager || !jancy.tabManager.getWebContents) {
          return null;
        }
        try {
          return jancy.tabManager.getWebContents(tab, true);
        }
        catch (err) {
          try {
            return jancy.tabManager.getWebContents(tab);
          }
          catch (ignored) {
            return null;
          }
        }
      }

      function syncCaptureScript() {
        if (window.__proxySentryCsvSyncInstalled && window.__proxySentryCsvSyncVersion === "1.14") {
          return true;
        }
        window.__proxySentryCsvSyncInstalled = true;
        window.__proxySentryCsvSyncVersion = "1.14";
        window.__proxySentryCsvSyncQueue = [];

        function cssEscape(value) {
          if (window.CSS && CSS.escape) {
            return CSS.escape(value);
          }
          return String(value).replace(/["\\]/g, "\\$&");
        }

        function selectorFor(element) {
          if (!element || element === document || element === window) {
            return "";
          }
          if (element.id) {
            return "#" + cssEscape(element.id);
          }

          var attrNames = ["name", "aria-label", "placeholder", "data-testid", "role", "type"];
          for (var attrIndex = 0; attrIndex !== attrNames.length; attrIndex = attrIndex + 1) {
            var attr = attrNames[attrIndex];
            var value = element.getAttribute && element.getAttribute(attr);
            if (!value) {
              continue;
            }
            var selector = element.tagName.toLowerCase() + "[" + attr + "=\"" + cssEscape(value) + "\"]";
            try {
              if (document.querySelectorAll(selector).length === 1) {
                return selector;
              }
            }
            catch (err) {
            }
          }

          var parts = [];
          var node = element;
          while (node && node.nodeType === Node.ELEMENT_NODE) {
            var part = node.tagName.toLowerCase();
            var parent = node.parentElement;
            if (parent) {
              var sameTagIndex = 0;
              var sameTagCount = 0;
              for (var childIndex = 0; childIndex !== parent.children.length; childIndex = childIndex + 1) {
                var child = parent.children[childIndex];
                if (child.tagName === node.tagName) {
                  sameTagCount = sameTagCount + 1;
                  if (child === node) {
                    sameTagIndex = sameTagCount;
                  }
                }
              }
              if (sameTagCount > 1) {
                part = part + ":nth-of-type(" + sameTagIndex + ")";
              }
            }
            parts.unshift(part);
            if (parts.length === 5) {
              break;
            }
            node = parent;
          }
          return parts.join(" > ");
        }

        function baseEvent(type, target) {
          return {
            type: type,
            selector: selectorFor(target),
            tag: target && target.tagName ? target.tagName.toLowerCase() : "",
            url: location.href,
            time: Date.now()
          };
        }

        function pushEvent(payload) {
          try {
            window.__proxySentryCsvSyncQueue.push(payload);
            if (window.__proxySentryCsvSyncQueue.length > 2000) {
              window.__proxySentryCsvSyncQueue.shift();
            }
          }
          catch (err) {
          }
        }

        function addPointerPayload(payload, event) {
          payload.button = event.button || 0;
          payload.detail = event.detail || 1;
          payload.ctrlKey = !!event.ctrlKey;
          payload.shiftKey = !!event.shiftKey;
          payload.altKey = !!event.altKey;
          payload.metaKey = !!event.metaKey;
          payload.xRatio = window.innerWidth ? event.clientX / window.innerWidth : 0;
          payload.yRatio = window.innerHeight ? event.clientY / window.innerHeight : 0;
          payload.movementX = event.movementX || 0;
          payload.movementY = event.movementY || 0;
          return payload;
        }

        var lastMoveTime = 0;
        var lastMouseUpTime = 0;
        function capturePointer(type, event) {
          if (type === "mouseMove") {
            var now = Date.now();
            if (now - lastMoveTime < 16) {
              return;
            }
            lastMoveTime = now;
          }
          pushEvent(addPointerPayload(baseEvent(type, event.target), event));
        }

        document.addEventListener("mousemove", function(event) {
          capturePointer("mouseMove", event);
        }, true);

        document.addEventListener("mousedown", function(event) {
          capturePointer("mouseDown", event);
        }, true);

        document.addEventListener("mouseup", function(event) {
          lastMouseUpTime = Date.now();
          capturePointer("mouseUp", event);
        }, true);

        document.addEventListener("click", function(event) {
          if (Date.now() - lastMouseUpTime < 120) {
            return;
          }
          var payload = addPointerPayload(baseEvent("click", event.target), event);
          pushEvent(payload);
        }, true);

        document.addEventListener("input", function(event) {
          var target = event.target;
          if (!target || !("value" in target)) {
            return;
          }
          var payload = baseEvent("input", target);
          payload.value = target.value;
          payload.checked = target.checked;
          pushEvent(payload);
        }, true);

        document.addEventListener("change", function(event) {
          var target = event.target;
          if (!target || !("value" in target)) {
            return;
          }
          var payload = baseEvent("input", target);
          payload.value = target.value;
          payload.checked = target.checked;
          pushEvent(payload);
        }, true);

        document.addEventListener("keydown", function(event) {
          var payload = baseEvent("keyDown", event.target);
          payload.key = event.key;
          payload.code = event.code || "";
          payload.ctrlKey = !!event.ctrlKey;
          payload.shiftKey = !!event.shiftKey;
          payload.altKey = !!event.altKey;
          payload.metaKey = !!event.metaKey;
          payload.repeat = !!event.repeat;
          pushEvent(payload);
        }, true);

        document.addEventListener("keyup", function(event) {
          var payload = baseEvent("keyUp", event.target);
          payload.key = event.key;
          payload.code = event.code || "";
          payload.ctrlKey = !!event.ctrlKey;
          payload.shiftKey = !!event.shiftKey;
          payload.altKey = !!event.altKey;
          payload.metaKey = !!event.metaKey;
          pushEvent(payload);
        }, true);

        var scrollTimer = 0;
        document.addEventListener("scroll", function() {
          clearTimeout(scrollTimer);
          scrollTimer = setTimeout(function() {
            var doc = document.documentElement;
            pushEvent({
              type: "scroll",
              url: location.href,
              xRatio: doc.scrollWidth > window.innerWidth ? window.scrollX / (doc.scrollWidth - window.innerWidth) : 0,
              yRatio: doc.scrollHeight > window.innerHeight ? window.scrollY / (doc.scrollHeight - window.innerHeight) : 0
            });
          }, 45);
        }, true);

        document.addEventListener("wheel", function(event) {
          pushEvent({
            type: "wheel",
            url: location.href,
            deltaX: event.deltaX || 0,
            deltaY: event.deltaY || 0,
            xRatio: window.innerWidth ? event.clientX / window.innerWidth : 0,
            yRatio: window.innerHeight ? event.clientY / window.innerHeight : 0
          });
        }, true);

        window.__proxySentryCsvSyncDrain = function() {
          var events = window.__proxySentryCsvSyncQueue.slice(0);
          window.__proxySentryCsvSyncQueue = [];
          return events;
        };

        return true;
      }

      function syncReplayScript() {
        if (window.__proxySentryCsvSyncReplayInstalled && window.__proxySentryCsvSyncReplayVersion === "1.14") {
          return true;
        }
        window.__proxySentryCsvSyncReplayInstalled = true;
        window.__proxySentryCsvSyncReplayVersion = "1.14";

        function findTarget(selector) {
          if (!selector) {
            return null;
          }
          try {
            return document.querySelector(selector);
          }
          catch (err) {
            return null;
          }
        }

        function dispatchMouse(target, eventType, event) {
          if (!target) {
            return;
          }
          var x = window.innerWidth ? Number(event.xRatio || 0) * window.innerWidth : 0;
          var y = window.innerHeight ? Number(event.yRatio || 0) * window.innerHeight : 0;
          var options = {
            bubbles: true,
            cancelable: true,
            view: window,
            clientX: x,
            clientY: y,
            button: event.button || 0,
            ctrlKey: !!event.ctrlKey,
            shiftKey: !!event.shiftKey,
            altKey: !!event.altKey,
            metaKey: !!event.metaKey
          };
          target.dispatchEvent(new MouseEvent(eventType, options));
        }

        function setElementValue(target, value, checked) {
          if (!target || !("value" in target)) {
            return;
          }
          if (target.type === "checkbox" || target.type === "radio") {
            if (checked !== undefined) {
              target.checked = !!checked;
            }
          }
          var proto = target.constructor && target.constructor.prototype;
          var descriptor = proto ? Object.getOwnPropertyDescriptor(proto, "value") : null;
          if (descriptor && descriptor.set) {
            descriptor.set.call(target, value);
          }
          else {
            target.value = value;
          }
          target.dispatchEvent(new Event("input", { bubbles: true }));
          target.dispatchEvent(new Event("change", { bubbles: true }));
        }

        window.__proxySentryCsvSyncReplay = function(events) {
          if (!events || !events.length) {
            return true;
          }

          for (var i = 0; i !== events.length; i = i + 1) {
            var event = events[i] || {};
            try {
              if (event.type === "input") {
                setElementValue(findTarget(event.selector), event.value || "", event.checked);
              }
              else if (event.type === "click") {
                dispatchMouse(findTarget(event.selector), "click", event);
              }
              else if (event.type === "scroll") {
                var doc = document.documentElement;
                var x = (doc.scrollWidth - window.innerWidth) * Number(event.xRatio || 0);
                var y = (doc.scrollHeight - window.innerHeight) * Number(event.yRatio || 0);
                window.scrollTo(x, y);
              }
            }
            catch (err) {
            }
          }
          return true;
        };

        return true;
      }

      function nativeButtonName(button) {
        if (button === 1) {
          return "middle";
        }
        if (button === 2) {
          return "right";
        }
        return "left";
      }

      function nativeKeyCode(event) {
        var key = String(event.key || "");
        if (key.length === 1) {
          return key.toUpperCase();
        }
        if (key === " ") {
          return "Space";
        }
        return key || String(event.code || "");
      }

      function sendNativeInputEvents(wc, events) {
        if (!wc || !wc.sendInputEvent || !events || !events.length) {
          return;
        }

        wc.executeJavaScript("[window.innerWidth, window.innerHeight]")
          .then(function(size) {
            var width = size && size[0] ? Number(size[0]) : 1280;
            var height = size && size[1] ? Number(size[1]) : 720;
            for (var index = 0; index !== events.length; index = index + 1) {
              var event = events[index] || {};
              try {
                if (event.type === "mouseMove") {
                  var moveX = Math.max(1, Math.min(width - 1, Number(event.xRatio || 0) * width));
                  var moveY = Math.max(1, Math.min(height - 1, Number(event.yRatio || 0) * height));
                  wc.sendInputEvent({
                    type: "mouseMove",
                    x: moveX,
                    y: moveY,
                    movementX: Number(event.movementX || 0),
                    movementY: Number(event.movementY || 0)
                  });
                }
                else if (event.type === "mouseDown" || event.type === "mouseUp") {
                  var clickX = Math.max(1, Math.min(width - 1, Number(event.xRatio || 0) * width));
                  var clickY = Math.max(1, Math.min(height - 1, Number(event.yRatio || 0) * height));
                  var buttonName = nativeButtonName(event.button || 0);
                  var clickCount = event.detail || 1;
                  wc.sendInputEvent({
                    type: event.type,
                    x: clickX,
                    y: clickY,
                    button: buttonName,
                    clickCount: clickCount
                  });
                }
                else if (event.type === "wheel") {
                  var wheelX = Math.max(1, Math.min(width - 1, Number(event.xRatio || 0) * width));
                  var wheelY = Math.max(1, Math.min(height - 1, Number(event.yRatio || 0) * height));
                  wc.sendInputEvent({
                    type: "mouseWheel",
                    x: wheelX,
                    y: wheelY,
                    deltaX: Number(event.deltaX || 0),
                    deltaY: Number(event.deltaY || 0)
                  });
                }
                else if (event.type === "keyDown" || event.type === "keyUp") {
                  var keyCode = nativeKeyCode(event);
                  if (keyCode) {
                    wc.sendInputEvent({
                      type: event.type === "keyDown" ? "keyDown" : "keyUp",
                      keyCode: keyCode,
                      modifiers: {
                        control: !!event.ctrlKey,
                        shift: !!event.shiftKey,
                        alt: !!event.altKey,
                        meta: !!event.metaKey
                      }
                    });
                  }
                }
              }
              catch (err) {
              }
            }
          })
          .catch(function(err) {
          });
      }

      var masterStableId = getParam("masterStableId") || "";
      var stableIds = splitStableIds(getParam("stableIds") || "");
      if (!masterStableId && stableIds.length) {
        masterStableId = stableIds[0];
      }
      if (masterStableId && stableIds.indexOf(masterStableId) === -1) {
        stableIds.unshift(masterStableId);
      }
      if (!masterStableId || !(stableIds.length > 1)) {
        return send({ success: false, message: "Jancy sync requires at least two managed tabs." });
      }

      var masterTab = getManagedTab(masterStableId);
      var masterWc = getManagedWebContents(masterTab);
      if (!masterTab || !masterWc) {
        return send({ success: false, message: "Jancy sync master tab is not available. Open the sync group first." });
      }

      var followerCount = 0;
      var installPromises = [];
      var captureCode = "(" + syncCaptureScript.toString() + ")()";
      var replayCode = "(" + syncReplayScript.toString() + ")()";
      installPromises.push(masterWc.executeJavaScript(captureCode));
      for (var followerIndex = 0; followerIndex !== stableIds.length; followerIndex = followerIndex + 1) {
        var followerStableId = stableIds[followerIndex];
        if (followerStableId === masterStableId) {
          continue;
        }
        var followerTab = getManagedTab(followerStableId);
        var followerWc = getManagedWebContents(followerTab);
        if (followerTab && followerWc) {
          followerCount = followerCount + 1;
          installPromises.push(followerWc.executeJavaScript(replayCode));
        }
      }

      if (!followerCount) {
        return send({ success: false, message: "Jancy sync followers are not available. Open the sync group first." });
      }

      Promise.all(installPromises)
        .then(function() {
          stopSyncTimer();
          var state = getSyncState();
          state.enabled = true;
          state.masterStableId = masterStableId;
          state.stableIds = stableIds;
          state.lastError = "";
          function replayToFollower(wc, serialized) {
            try {
              sendNativeInputEvents(wc, JSON.parse(serialized));
            }
            catch (err) {
            }
            wc.executeJavaScript(replayCode)
              .then(function() {
                return wc.executeJavaScript("window.__proxySentryCsvSyncReplay && window.__proxySentryCsvSyncReplay(" + serialized + ")");
              })
              .catch(function(err) {
                state.lastError = err ? (err.message ? err.message : String(err)) : "replay failed";
              });
          }

          state.timer = setInterval(function() {
            if (!state.enabled) {
              return;
            }
            if (state.busy) {
              return;
            }
            var currentMasterTab = getManagedTab(state.masterStableId);
            var currentMasterWc = getManagedWebContents(currentMasterTab);
            if (!currentMasterWc) {
              return;
            }
            state.busy = true;
            currentMasterWc.executeJavaScript(captureCode)
              .then(function() {
                return currentMasterWc.executeJavaScript("window.__proxySentryCsvSyncDrain ? window.__proxySentryCsvSyncDrain() : []");
              })
              .then(function(events) {
                if (!events || !events.length) {
                  state.busy = false;
                  return;
                }
                var serialized = JSON.stringify(events);
                for (var replayIndex = 0; replayIndex !== state.stableIds.length; replayIndex = replayIndex + 1) {
                  var replayStableId = state.stableIds[replayIndex];
                  if (replayStableId === state.masterStableId) {
                    continue;
                  }
                  var replayTab = getManagedTab(replayStableId);
                  var replayWc = getManagedWebContents(replayTab);
                  if (!replayWc) {
                    continue;
                  }
                  replayToFollower(replayWc, serialized);
                }
                state.busy = false;
              })
              .catch(function(err) {
                state.lastError = err ? (err.message ? err.message : String(err)) : "capture failed";
                state.busy = false;
              });
          }, 20);

          return send({ success: true, masterStableId: masterStableId, followers: followerCount });
        })
        .catch(sendError);
      return null;
    }
    catch (err) {
      return {
        success: false,
        message: err ? (err.message ? err.message : String(err)) : "unknown Jancy bridge error"
      };
    }
  </webinterface-endpoint>

  <webinterface-endpoint path="proxy-sentry-csv/sync-stop">
    try {
      if (jancy.proxySentryCsvSync) {
        jancy.proxySentryCsvSync.enabled = false;
        if (jancy.proxySentryCsvSync.timer) {
          clearInterval(jancy.proxySentryCsvSync.timer);
          jancy.proxySentryCsvSync.timer = null;
        }
      }
      return { success: true };
    }
    catch (err) {
      return {
        success: false,
        message: err ? (err.message ? err.message : String(err)) : "unknown Jancy bridge error"
      };
    }
  </webinterface-endpoint>

  <webinterface-endpoint path="proxy-sentry-csv/close">
    try {
      function decodeQueryValue(value) {
        value = String(value || "").replace(/\+/g, " ");
        try {
          return decodeURIComponent(value);
        }
        catch (err) {
          return value;
        }
      }

      function getParam(name) {
        if (parsedURL) {
          if (parsedURL.searchParams && parsedURL.searchParams.get) {
            var directValue = parsedURL.searchParams.get(name);
            if (directValue !== undefined && directValue !== null) {
              return directValue;
            }
          }

          if (parsedURL.query && typeof parsedURL.query === "object") {
            var objectValue = parsedURL.query[name];
            if (objectValue !== undefined && objectValue !== null) {
              if (objectValue && objectValue.length && typeof objectValue !== "string") {
                objectValue = objectValue[0];
              }
              return String(objectValue);
            }
          }
        }

        var raw = "";
        if (parsedURL) {
          if (typeof parsedURL.query === "string") {
            raw = parsedURL.query;
          }
          else if (typeof parsedURL.search === "string") {
            raw = parsedURL.search;
          }
          else if (typeof parsedURL.href === "string") {
            raw = parsedURL.href;
          }
          else if (typeof parsedURL.path === "string") {
            raw = parsedURL.path;
          }
        }

        if (!raw && req && typeof req.url === "string") {
          raw = req.url;
        }

        var questionIndex = raw.indexOf("?");
        if (questionIndex >= 0) {
          raw = raw.slice(questionIndex + 1);
        }
        if (raw.charAt(0) === "?") {
          raw = raw.slice(1);
        }
        if (!raw) {
          return null;
        }

        var parts = raw.split("&");
        for (var i = 0; i !== parts.length; i = i + 1) {
          var part = parts[i];
          if (!part) {
            continue;
          }

          var equalsIndex = part.indexOf("=");
          var key = equalsIndex >= 0 ? part.slice(0, equalsIndex) : part;
          var value = equalsIndex >= 0 ? part.slice(equalsIndex + 1) : "";
          if (decodeQueryValue(key) === name) {
            return decodeQueryValue(value);
          }
        }

        return null;
      }

      function closeWindow(tw) {
        if (!tw) {
          return false;
        }
        if (tw.browserWindow && tw.browserWindow.close) {
          tw.browserWindow.close();
          return true;
        }
        if (tw.close) {
          tw.close();
          return true;
        }
        return false;
      }

      function windowHasManagedTab(tw, stableId, profileKey) {
        if (!tw || !tw.getTabs) {
          return false;
        }
        var tabs = tw.getTabs();
        for (var i = 0; tabs && i !== tabs.length; i = i + 1) {
          var data = tabs[i].data || {};
          if (data.proxySentryCsvStableId === stableId || data.proxySentryCsvProfileKey === profileKey) {
            return true;
          }
        }
        return false;
      }

      var stableId = getParam("stableId") || "";
      var profileKey = getParam("profileKey") || "";
      var windowName = getParam("windowName") || (profileKey ? "ProxySentryCSV-" + profileKey : "");
      var closed = 0;

      if (windowName && jancy.windowManager && jancy.windowManager.getWindow) {
        var namedWindow = jancy.windowManager.getWindow({ name: windowName });
        if (closeWindow(namedWindow)) {
          closed = closed + 1;
        }
      }

      if (!closed && jancy.windowManager && jancy.windowManager.getWindows) {
        var windows = jancy.windowManager.getWindows(0);
        for (var index = 0; windows && index !== windows.length; index = index + 1) {
          if (windowHasManagedTab(windows[index], stableId, profileKey) && closeWindow(windows[index])) {
            closed = closed + 1;
            break;
          }
        }
      }

      if (closed && jancy.proxySentryCsvSync && jancy.proxySentryCsvSync.masterStableId === stableId) {
        jancy.proxySentryCsvSync.enabled = false;
        if (jancy.proxySentryCsvSync.timer) {
          clearInterval(jancy.proxySentryCsvSync.timer);
          jancy.proxySentryCsvSync.timer = null;
        }
      }

      return { success: true, closed: closed };
    }
    catch (err) {
      return {
        success: false,
        message: err ? (err.message ? err.message : String(err)) : "unknown Jancy bridge error"
      };
    }
  </webinterface-endpoint>
</jyl>
""";
}
