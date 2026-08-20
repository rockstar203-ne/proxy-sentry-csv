Proxy Sentry CSV

Portable layout:
  Proxy Sentry CSV.exe
  components\
  Data\

Proxy Sentry CSV loads proxy CSV files, removes added lists, keeps row state, copies and exports proxies, checks ping/response speed, and opens each proxy through its own browser provider.

Browser providers:
  Chrome
    Uses the Chrome installed in Windows, but with app-managed per-proxy profiles,
    local proxy relay, BrowserForge-based proxy country matching, forced
    Accept-Language, timezone/language/geolocation overrides, WebRTC proxy-only
    handling, and Chrome DevTools sync support.

  Jancy
    Uses the local Jancy Web Interface and the Proxy Sentry CSV JYL bridge.
    This is the provider used for controlled windows and sync.

  Edge, Firefox
    Uses browsers installed in Windows.
    Each proxy gets a separate app-managed profile under Data\SystemBrowserProfiles\.
    The app starts a local no-auth proxy relay on 127.0.0.1 and forwards traffic to the upstream proxy with Proxy-Authorization.
    This avoids native browser username/password prompts in Chrome, Edge, and Firefox.
    If Python browserforge is available, Proxy Sentry CSV detects the proxy exit country through the proxy and creates a matching BrowserForge profile.
    Example: a US proxy gets en-US language, US timezone, and a browser-specific UA/fingerprint.
    Chrome and Edge also get extension-based timezone offset, Intl locale, navigator, screen, and WebRTC leak controls.
    Firefox uses profile-level proxy, DNS, WebRTC, locale, and UA preferences; stock Firefox limits arbitrary timezone JavaScript spoofing compared with Chrome.
    If BrowserForge is unavailable, the browser still opens with a local fallback profile based on the detected proxy country.

Jancy setup:
  Open Settings in Proxy Sentry CSV.
  Press Install Jancy bridge.
  In Jancy open Settings -> JYL Scripts and press Rescan JYL directory, or restart Jancy.

Selection and sync:
  Select rows in the table with the standard Windows list behavior.
  Use Ctrl or Shift for multi-select.
  Open sync group, Start sync, Stop sync, and Align selected apply to selected Jancy controlled windows or Chrome DevTools windows.
  Sync includes navigation, mouse movement, clicks, wheel, scroll, text input, and keyboard input.

Updates:
  Check updates uses the built-in GitHub Releases channel:
    https://api.github.com/repos/rockstar203-ne/proxy-sentry-csv/releases/latest
  No Git credential or browser authorization prompt is shown by the app.
  For a private release channel, put a GitHub token with release read access into
  update.key next to Proxy Sentry CSV.exe or into Data\update.key.
  Install update downloads the portable ZIP, starts ProxySentryCsv.Updater.exe,
  backs up the current program files, replaces only the app files, and restarts.
  Rollback previous version restores the latest local backup.
  Data\ keeps app state and is not reset by update or rollback.
