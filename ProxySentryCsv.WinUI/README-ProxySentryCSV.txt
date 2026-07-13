Proxy Sentry CSV

Portable layout:
  Proxy Sentry CSV.exe
  components\
  Data\

Proxy Sentry CSV loads proxy CSV files, removes added lists, keeps row state, copies and exports proxies, checks ping/response speed, and opens each proxy through its own browser provider.

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
  Open sync group, Start sync, Stop sync, and Align selected apply to selected Jancy controlled windows.
  Sync includes navigation, mouse movement, clicks, wheel, scroll, text input, and keyboard input.

Updates:
  Settings uses the built-in repository URL:
    https://github.com/rockstar203-ne/proxy-sentry-csv.git
  Run git update writes a step-by-step log and saves the previous commit for rollback.
  Rollback previous update resets the git repository to that saved commit.
  Data\ keeps app state and is not reset by update or rollback.
