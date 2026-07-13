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

## Build

```powershell
dotnet build .\ProxySentryCSV.sln -c Release
```

## Publish Portable

```powershell
dotnet publish .\ProxySentryCsv.WinUI\ProxySentryCsv.WinUI.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o .\release\components
dotnet publish .\ProxySentryCsv.Launcher\ProxySentryCsv.Launcher.csproj -c Release -r win-x64 --self-contained true -o .\release\launcher
```
