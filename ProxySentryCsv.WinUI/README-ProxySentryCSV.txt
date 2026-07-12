Proxy Sentry CSV

Portable layout:
  Proxy Sentry CSV.exe
  components\
  Data\

Proxy Sentry CSV loads proxy CSV files, keeps row state, copies and exports proxies, checks ping/response speed, and opens selected proxies through a browser provider.

Browser providers:
  Jancy
    Uses the local Jancy Web Interface and the Proxy Sentry CSV JYL bridge.
    This is the provider used for controlled windows and sync.

  Chrome, Edge, Firefox
    Uses browsers installed in Windows.
    Each proxy gets a separate app-managed profile under Data\SystemBrowserProfiles\.
    The app starts a local no-auth proxy relay on 127.0.0.1 and forwards traffic to the upstream proxy with Proxy-Authorization.
    This avoids native browser username/password prompts in Chrome, Edge, and Firefox.

Jancy setup:
  Open Settings in Proxy Sentry CSV.
  Press Install Jancy bridge.
  In Jancy open Settings -> JYL Scripts and press Rescan JYL directory, or restart Jancy.

Selection and sync:
  Select rows in the table with the standard Windows list behavior.
  Use Ctrl or Shift for multi-select.
  Open sync group, Start sync, and Stop sync apply to selected Jancy controlled windows.
