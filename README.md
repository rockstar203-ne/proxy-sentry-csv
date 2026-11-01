# Proxy Sentry CSV

Windows portable CSV proxy launcher.

## Features

- Load, switch, and remove CSV proxy lists.
- Persist row state, names, favorites, copied state, target URL, and per-proxy browser choice.
- Search by name or IP.
- Copy proxies in `ip:port:username:password` format.
- Export TXT and AdsPower-compatible proxy lists.
- Check proxy ping and response speed.
- Launch each proxy through its own selected provider: Jancy controlled mode, Chrome, Edge, or Firefox.
- Use a local no-auth relay for system browsers when the upstream HTTP proxy requires credentials.
- Generate proxy-aware BrowserForge profiles for Chrome, Edge, and Firefox: the app detects the proxy exit country through the proxy and applies matching locale, language, timezone, user agent, screen, navigator, DNS/WebRTC, and Intl values where the selected browser allows it.
- Close managed Jancy windows and synchronize navigation, mouse movement, clicks, wheel, scroll, text input, and keyboard input across selected Jancy windows.
- Align selected Jancy windows on the selected monitor.
- Update from `https://github.com/rockstar203-ne/proxy-sentry-csv.git` through git and roll back to the previous update commit without clearing app state.

## Portable Layout

The release zip is arranged as:

```text
Proxy Sentry CSV.exe
components/
Data/
README-ProxySentryCSV.txt
```

Run `Proxy Sentry CSV.exe` from the root of the unpacked folder.

## BrowserForge

Proxy Sentry CSV uses `daijro/browserforge` when Python and the `browserforge` package are available on the machine. On browser launch, it first checks geo through the selected proxy, caches it for 24 hours, and generates a per-proxy/per-browser fingerprint using that country. If BrowserForge is not available, the app keeps launching with a local fallback fingerprint based on the detected proxy country.

## AdsPower Local API Compatibility

This optional adapter supports the existing `E:\etix` AdsPower client without changing its code.
It is a profile-control API adapter, not an AdsPower browser engine or a full AdsPower API implementation.

1. Close AdsPower if it is already using port 50325.
2. Choose Chrome for the required profiles, check their Select boxes, and press **Select for Event Sentry**.
3. In **Settings**, enable **AdsPower-compatible API**, set **API port** (default `50325`), and save.
4. Keep Proxy Sentry running. Use `http://127.0.0.1:50325` and group **Inventory Etix (DO NOT TOUCH)** in the client.

Supported GET endpoints: `/status`, `/api/v1/group/list`, `/api/v1/user/list`,
`/api/v1/browser/start`, `/api/v1/browser/stop`, `/api/v1/browser/active`, and `/api/v1/browser/local-active`.
Lists support pagination. Start returns a browser-level WebSocket in `data.ws.puppeteer` for Playwright CDP.
The legacy client's `/browser/active` request without a profile ID returns an active-profile list.
Use `user_id` as the stable identity; CSV row numbers can repeat between lists.

Only selected Chrome profiles are exposed. Firefox is not compatible with the client's CDP connection.
Custom launch arguments, headless launches, profile mutation, and Selenium WebDriver binaries are not supported.
Proxy credentials are not returned. Profile cookies, proxy settings, and the normal browser launcher are preserved.
The API can stop only browser processes it started; manually opened profiles are reported as busy.
Disabling the API leaves its browser windows open. Re-enabling within the same app session preserves ownership.
After restarting Proxy Sentry, close and reopen remaining windows before requesting new API launches.
Event Sentry bridge acquisition is paused while this adapter is enabled; release existing bridge leases first.

Access is limited to loopback desktop clients, with browser-origin requests rejected. There is no API key,
so enable it only when needed on a trusted computer. An occupied port produces an error without stopping
the application that owns it. The local client can control the selected profiles while this mode is enabled.

The port is saved between application launches. Set the same port in your API client when changing it.
Changing the listener port leaves browser windows and API ownership intact. If the new port is unavailable,
the adapter attempts to restore the previous listener and displays the actual running status.

## Build Commands

```powershell
dotnet build .\ProxySentryCSV.sln -c Release
```

## Publish Portable

```powershell
dotnet publish .\ProxySentryCsv.WinUI\ProxySentryCsv.WinUI.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o .\release\components
dotnet publish .\ProxySentryCsv.Launcher\ProxySentryCsv.Launcher.csproj -c Release -r win-x64 --self-contained true -o .\release\launcher
```
