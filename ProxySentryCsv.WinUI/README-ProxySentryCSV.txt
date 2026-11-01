Proxy Sentry CSV

Portable layout:
  Proxy Sentry CSV.exe
  components\
  Data\

Proxy Sentry CSV loads proxy CSV files, removes added lists, keeps row state, copies and exports proxies, checks ping/response speed, and opens each proxy through its own browser provider.

Browser providers:
  Chrome
    Uses the Chrome installed in Windows, but with app-managed per-proxy profiles,
    local proxy relay, native User-Agent, regional language/timezone settings,
    WebRTC proxy-only handling, and Chrome DevTools sync support.

  Jancy
    Uses the local Jancy Web Interface and the Proxy Sentry CSV JYL bridge.
    This is the provider used for controlled windows and sync.

  Edge, Firefox
    Uses browsers installed in Windows.
    Each proxy gets a separate app-managed profile under Data\SystemBrowserProfilesClean\.
    The app starts a local no-auth proxy relay on 127.0.0.1 and forwards traffic to the upstream proxy with Proxy-Authorization.
    This avoids native browser username/password prompts in Chrome, Edge, and Firefox.
    Browser configuration is built in. Python and BrowserForge installation are not required.
    User-Agent and Chromium Client Hints are supplied by the installed browser, not generated.
    Existing browser profiles and cookies are retained. Configuration migration creates backups.
    An unavailable geo lookup does not invent a US location. Proxy gateway location is not treated as exit location.
    IP geolocation is approximate; language is a country-based suggestion, not a verified user preference.
    Profile Details -> Regional settings shows the expected locale/timezone and geo source.
    Check open tabs reads current Chrome/Edge tab values without modifying pages.
    Copy local check URL: paste into a tab of the intended Chrome, Edge or Firefox profile.
    The local page checks the tab and a dedicated worker without sending network requests.
    This does not verify HTTP headers, existing site workers or current exit IP.
    Reopen managed profiles after updating to apply native UA migration and regional settings.
    Hardware, fonts and installed browser versions can still differ between PCs.

Jancy setup:
  Open Settings in Proxy Sentry CSV.
  Press Install Jancy bridge.
  In Jancy open Settings -> JYL Scripts and press Rescan JYL directory, or restart Jancy.

Selection and sync:
  Select rows in the table with the standard Windows list behavior.
  Use Ctrl or Shift for multi-select.
  Open sync group, Start sync, Stop sync, and Align selected support Chrome, Firefox and Jancy.
  Select one browser type for a group. The first row is the master; other rows are followers.
  Firefox uses WebDriver BiDi only when launched through Open sync group.
  Close Firefox windows opened individually before reopening them through Open sync group.
  Regular Firefox launches do not enable remote control. Sync mode is detectable as browser automation.
  Sync targets the page attached at Start sync; switching tabs, browser UI, file pickers and native dialogs are not synchronized.
  Stop sync leaves browser windows and profile data open. Ctrl-click delay and Apply target URL support Firefox.
  Sync includes navigation, mouse movement, clicks, wheel, scroll, text input, and keyboard input.

Event Sentry bridge:
  Check proxy rows in Select, then press Select for Event Sentry. Browser selection is preserved.
  Select Chrome or Firefox in Event Sentry's bridge browser setting.
  Proxy Sentry CSV starts a local Named Pipe bridge:
    ProxySentryCSV.EventSentry.v1
  The bridge accepts one UTF-8 JSON request per connection and returns one JSON response.
  Supported commands are status, acquire_etix_profiles, set_profile_status,
  bring_to_front, and release_etix_profiles.
  Chrome leases return profile metadata and the loopback CDP endpoint, without proxy credentials.
  Firefox leases return proxy connection details (including credentials) over the current-user-only
  named pipe to Event Sentry, which owns and controls its Playwright Firefox process.
  Firefox focus and process shutdown are handled by Event Sentry, not Proxy Sentry's launcher.
  Releasing an external Firefox lease never closes a separately opened Proxy Sentry browser.
  acquire_etix_profiles starts or reuses persistent Chrome profiles and keeps them
  open on release when keepOpen is true.

AdsPower API compatibility:
  Optional AdsPower API compatibility is available in Settings, with an editable API port (default 50325).
  Select Chrome rows, press Select for Event Sentry, then enable the compatibility checkbox and Save.
  Legacy clients can use http://127.0.0.1:50325 and group Inventory Etix (DO NOT TOUCH).
  Close AdsPower first if it is using that port. Only selected Chrome profiles are exposed.
  This is a local CDP API adapter, not the AdsPower browser engine. Firefox and Selenium are not supported.
  Event Sentry bridge acquisition is paused while this API is enabled. API shutdown leaves browser windows open.
  No API key is required: enable only on a trusted computer. Do not expose the port to other machines.
  Set the same port in the API client. The port is saved between launches.
  Changing the port keeps browser windows open; a busy port restores the previous listener when possible.

Updates:
  Check updates uses the built-in GitHub Releases channel:
    https://api.github.com/repos/rockstar203-ne/proxy-sentry-csv/releases/latest
  No Git credential, token file, or browser authorization prompt is required.
  Install update downloads the portable ZIP, starts ProxySentryCsv.Updater.exe,
  backs up the current program files, replaces only the app files, and restarts.
  Rollback previous version restores the latest local backup.
  Data\ keeps app state and is not reset by update or rollback.
