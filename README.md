# Proxy Sentry CSV

Windows portable CSV proxy launcher.

## Features

- Load and switch CSV proxy lists.
- Persist row state, names, favorites, copied state, target URL, and selected browser.
- Search by name or IP.
- Copy proxies in `ip:port:username:password` format.
- Export TXT and AdsPower-compatible proxy lists.
- Check proxy ping and response speed.
- Launch selected proxies through Jancy controlled mode, Chrome, Edge, or Firefox.
- Use a local no-auth relay for system browsers when the upstream HTTP proxy requires credentials.
- Close managed Jancy windows and synchronize navigation, mouse clicks, and keyboard input across selected Jancy windows.

## Portable Layout

The release zip is arranged as:

```text
Proxy Sentry CSV.exe
components/
Data/
README-ProxySentryCSV.txt
```

Run `Proxy Sentry CSV.exe` from the root of the unpacked folder.

## Build

```powershell
dotnet build .\ProxySentryCSV.sln -c Release
```

## Publish Portable

```powershell
dotnet publish .\ProxySentryCsv.WinUI\ProxySentryCsv.WinUI.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o .\release\components
dotnet publish .\ProxySentryCsv.Launcher\ProxySentryCsv.Launcher.csproj -c Release -r win-x64 --self-contained true -o .\release\launcher
```

