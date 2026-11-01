# Reliability and workspace upgrade

Implementation scope accepted by the user: transport and regional diagnostics,
stable profile identity, recoverable settings, session management, task progress,
per-profile routing, browser extension sets, responsive desktop UI, synchronization
isolation, and a separately validated Chromium experiment.

## Verification gates

- Test HTTP authentication, HTTPS proxy TLS validation, SOCKS5 authentication,
  CONNECT, direct routing, route changes, cancellation and connection cleanup.
- Preserve existing profile paths and settings during identity migration.
- Verify profile state recovery and new-tab regional settings.
- Verify the actual portable package, updater presence, and desktop launch.
- Keep experimental browser builds separate from existing browser profiles.

## Work tracking

- [x] Shared transport and actionable diagnostics (local regression tests)
- [x] Regional data provenance and browser startup checks (local validation; live proxy acceptance pending)
- [x] Stable profile identity and recoverable settings
- [ ] History, configuration snapshots and support report
- [x] Per-profile Proxy / Direct switch (transport tested; live-browser acceptance pending)
- [x] Work sessions, filters and restore (storage tested; live restore acceptance pending)
- [ ] Extension sets and installed/active state
- [x] Responsive UI with persistent Open action (1426px window and inspector reviewed)
- [x] Per-window synchronization queues (queue regression tests; browser group acceptance pending)
- [x] Portable build and regression tests
- [ ] Chromium experiment prerequisites and reproducible build path

This file tracks implementation, not a statement that the above work has passed.

## Current delivery

- 20 local regression tests pass, including HTTP/HTTPS/SOCKS5 proxy transport,
  authentication failures, CONNECT read-ahead, half-close, route changes, queue
  isolation, atomic recovery, identity migration and concurrent history writes.
- Release compilation passes with no warnings/errors. The portable launcher
  starts the UI with isolated fixture data. The updater is included.
- `Build-Portable.ps1` produces the single established release folder and a clean
  ZIP from fresh staging. Existing user Data is preserved and excluded from ZIP.
- Check All settles cancelled queued items, preserves open/sync status, limits
  concurrent checks to four, and writes sanitized local connection history.

## Still pending

- Regional provider change is deferred pending explicit approval to send the
  proxy exit IP to ipwho.is. No provider change was applied.
- Configuration snapshots/rollback UI and per-profile extension sets.
- Explicit sync-master picker and navigation replay acceptance tests.
- Live Chrome/Edge/Firefox routing acceptance and deployment on a second PC.
- Narrow-window/DPI layout verification beyond the tested 1426px window.
- Chromium experiment prerequisites/build. No custom browser kernel is built.

No claim is made that these changes bypass site restrictions or prevent blocks.
The current local package is a development build; nothing was published to GitHub.

## UI regression correction

- Restored compact command groups with separators, and kept Settings / Check
  updates / About together at the right of the toolbar.
- Moved selection count, bulk browser assignment and profile filter to a table
  toolbar. Search keeps its fixed width and aligns with the Target URL input.
- Only Open is pinned; Cache and Copy remain in the scrollable Actions column.
  The Actions column has a minimum width that fits its controls.
- Profile details have an explicit close control, a selected toggle, and a
  clearer name/details hierarchy. Cancel check is shown only while checking.
- Wrapped URL-list controls share their row height so bottom alignment works.
- Main-window layout was visually checked at 1426px. Subsequent UI automation
  could not resume because its helper process exited unexpectedly; inspector
  and full horizontal-scroll verification remain pending for this revision.

## Native configuration and regional diagnostics (2026-09-09)

- Replaced ambient Python/BrowserForge generation and silent fallback with a
  versioned built-in native configuration. No package downloads or Python are
  needed at runtime. Existing legacy configuration is migrated, with atomic
  backup; damaged configuration is reported instead of regenerated silently.
- User-Agent and Client Hints stay native. Removed generated UA rewriting,
  Firefox UA preference/injection, and the unused legacy UA-CH patch. Firefox
  migration backs up prefs.js and removes only the former global UA override.
- Geo lookup distinguishes proxy exit, proxy host and unknown. No invented US
  city/coordinates/timezone on errors; gateway results do not overwrite region.
  The existing ip-api provider is unchanged. Its HTTP response is not an
  authenticated attestation; geo location and inferred language remain approximate.
- Profile Details includes expected regional settings and source/time of lookup.
  Chrome/Edge can inspect all currently open page targets on demand, without
  changing pages or navigation. Startup checks no longer claim an override
  succeeded merely because it was attempted.
- A copied local diagnostic URL supports Chrome/Edge/Firefox tab and dedicated
  worker checks, with no outgoing requests. Firefox inspection is manual: paste
  the URL into the intended profile. HTTP headers and existing site workers are
  outside this check. New tabs are checked, not automatically reconfigured.
- 31 local regression tests pass. Local diagnostic pages were tested in isolated
  Chromium and Firefox for both matching and mismatched regions and refresh;
  no network requests/page errors. Native WinUI inspector layout is not yet
  visually rechecked for this addition. No site-blocking improvement is claimed.
