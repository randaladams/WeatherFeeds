# WeatherFeeds — handoff

**Use this to pick up the work on another machine or in a new session.** The [README](README.md) covers features, setup and usage. This file covers where things stand, how the code is put together, what was learned the hard way, and what's next.

- **Last updated:** 2026-11-17
- **Version:** 1.7.0
- **Branch:** `main`, 1.7.0 pushed to GitHub
- **Working tree:** clean

---

## 1. Where things stand

A Windows 10/11 dashboard for the **Memphis DMA**: 27 counties across TN, AR and MS. It shows NWS weather, alerts, trends, rivers and radar.

**Done and pushed:**

| Version | What |
|---|---|
| 1.0 | Four-panel dashboard (TN / AR / MS / High Alerts), county drill-down, refresh timers and data-age stats, light/dark/auto theme, alert sounds with acknowledge, NWS radar loops, NWS-compliant User-Agent |
| 1.1 | Tray icon, minimize to tray, Windows popups for new alerts, Exit with confirm, right-click/Esc/mouse-back to go back, app icon |
| 1.2 | Click a county row to open its state with that county highlighted; the highlight follows the mouse |
| 1.3 | Temperature trend chart on each card (24 h / 3 / 7 / 30 days plus 24 h forecast), local history saved as CSV, next-24 h amounts (rain/snow/ice/gusts/thunder/feels-like), SPC day 1–3 risk, ACIS climate normals and records, IEM storm reports, Rivers screen (about 40 NOAA gauges) |
| 1.4 | Zoomed single-county view (large type, full forecast text, wide chart, ‹ › to step through counties); pointer moved onto the opened county |
| 1.5 / 1.5.1 | Version shown in the title bar, tray, every screen header and Settings → About; pointer lands at the card's center; **fix:** history backfill now follows NWS paging to get all 7 days |
| 1.6 | **`--collect` headless mode** and **central-share history** via `WeatherFeeds.shared.json`; clients read the share and never write to it; `install-collector.ps1` creates a scheduled task (NETWORK SERVICE by default) |
| 1.6.1 | Settings → History says when the shared folder **can't be reached** (offline, wrong path, no Read access) instead of "collector hasn't run yet"; self-tested both cases |
| 1.7 | **Start with Windows** (Settings): a per-user `HKCU\…\Run` entry launching `WeatherFeeds.exe --startup`, which starts minimized (straight to the tray if that's on). The checkbox reads the registry, so it stays right if the entry is removed in Task Manager. **Fix:** after an upgrade the first NWS requests now carry the new version in the User-Agent |

**Verified:**
- **Every build** has passed `--selftest`: all 27 counties loading, NWS User-Agent seen on the wire, history, SPC, climate and rivers.
- **The collector** was tested end-to-end on a fake share in a temp folder. The first run saved 8,416 readings for 14 stations. Incremental runs take about 2 s, a second collector exits with code 3, and a client reads the share without writing to it.

**Not verified yet:**
- `install-collector.ps1` has passed a syntax check but has **never been run**, because it changes Windows scheduled tasks.
- The collector has never run as **NETWORK SERVICE** or **SYSTEM**, only as the interactive user.
- **Start with Windows (1.7)**: the self-test confirms the checkbox shows and reads the registry, but ticking it, signing in with it on, and the tray start have **not been tried**, since that writes the user's Run key. Needs a manual check.
- Moving the pointer to the **center** of the card (1.5) hasn't been confirmed by the user. They did confirm the earlier version, which landed near the top of the card.

---

## 2. Open items and next steps

### Waiting on the user
1. **Deploy to the station server: in progress (2026-11-17).** The user **can't run PowerShell scripts** on the server, so `deploy/SERVER-SETUP.txt` gives the same steps as plain `cmd` commands (`mkdir`, `icacls`, `schtasks`, `net share`); it's copied next to the exe by `publish.ps1`. The server folder is `D:\APPS\WeatherFeeds`. Lessons so far:
   - `schtasks /Create` said "Access is denied" until the Command Prompt was run as administrator.
   - The task then failed with Last Result **-2147024891** (0x80070005): NETWORK SERVICE also needs **RX on the program folder**, not just Modify on `history`. Now step 3 of the setup file.
   - Confirm the task's Last Result is 0 and `collector.log` ends with `Done: 14/14 stations`.
   - **Use the standard (framework-dependent, about 2 MB) build.** The self-contained build is about 167 MB and took **4–5 minutes to start from the share** on a user's PC. Every PC has the .NET 10 Desktop Runtime; **the server didn't**, and the user chose to install it there (2026-11-17) rather than run a renamed self-contained `WeatherFeeds-collector.exe` for the task. That alternative was tested and works, since the config is found next to whichever exe runs. Started from `\\…\WeatherFeeds.exe` the 2 MB build is tested only via `\\localhost` here, not yet on a user's PC.
2. ~~Yalobusha County, MS~~: **decided 2026-11-15: leave it out.** The 27 counties stay as they are.

### Offered, not started
3. **Interactive county map** with NWS radar overlaid: Census county boundaries plus NWS radar map layers. This is the biggest remaining idea.
4. ~~Self-contained build~~: `publish.ps1 -SelfContained` already works (about 167 MB), but it's too slow to start from a share, and every PC has .NET 10, so it isn't used. If it's ever needed, a launcher that copies the exe locally would fix the startup time.
9. **Startup-crash message and log.** If the app crashes before its window shows, it closes silently today. Offered, not started.
5. ~~"Start with Windows" option~~: done in 1.7.0.
6. ~~Clearer Settings message when the share is unreachable~~: done in 1.6.1.
7. **Small-size icon variant** (16/32 px) showing only the sun and cloud. The temperature/wind text in the artwork turns to mush at small sizes.
8. **Archive forecasts** alongside observations, to compare forecast against actual later.

### Known quirks
- One self-test run exited without writing a report; the next run passed. Not reproduced since. The likely cause is the user closing the test window.
- The browser preview can't send the NWS User-Agent, which browsers don't allow pages to set. Only the exe is fully compliant.

---

## 3. Repo layout

```
src/WeatherFeeds/
  WeatherFeeds.csproj      <Version> is the single source of the version number
  App.xaml(.cs)            Startup: --collect runs Collector headless; otherwise opens MainWindow
  MainWindow.xaml(.cs)     WebView2 host, tray, popups, exit confirm, page<->host messages, --selftest, --demo
  HostSettings.cs          %LOCALAPPDATA%\WeatherFeeds\settings.json (window, topmost, tray, User-Agent, history days)
  HistoryStore.cs          One CSV per station; merge, trim, read-only load for the share
  SharedConfig.cs          WeatherFeeds.shared.json next to the exe (historyFolder, contactEmail, collectorRetentionDays)
  StartupRegistration.cs   Start with Windows: HKCU Run entry "<exe>" --startup
  Collector.cs             --collect: station list, paged fetch, lock file, log, status JSON, exit codes
  Assets/                  WeatherFeeds.ico plus make_icon.py (cuts the icon from the master artwork)
  wwwroot/
    index.html             The whole dashboard UI (HTML/CSS/JS, about 1,300 lines)
    counties.js            The 27 counties as JSON after "const COUNTIES =" (shared by the page and the collector)
    icon.png               Header logo
deploy/                    WeatherFeeds.shared.sample.json and install-collector.ps1 (copied into publish\ by publish.ps1)
publish.ps1                Builds the single-file exe into .\publish (gitignored)
nuget.config               Adds nuget.org; the dev PC had no package source configured
```

---

## 4. How it works

- **The host:** a WPF window containing a **WebView2** control. The UI is a single web page (`wwwroot/index.html`) embedded in the exe.
- **Serving the page:** the host answers requests for `https://weatherfeeds.example/*` from embedded resources (`WebResourceRequested`).
  - The original `SetVirtualHostNameToFolderMapping` approach suddenly started failing on the dev PC with `ConnectionAborted`. The old code failed the same way, so it was an environment problem, not the code.
  - **Don't switch back to folder mapping.**
- **The User-Agent:** set via `CoreWebView2.Settings.UserAgent`, so it applies to every request, including radar images. The self-test confirms the header on the wire using DevTools `Network.requestWillBeSent`.
- **Browser flags:** `--autoplay-policy=no-user-gesture-required`, so alert sounds play without a click.

**Messages from the page to the host** (`chrome.webview.postMessage({type, value})`):

| Message | Purpose |
|---|---|
| `ua` | Save and apply the User-Agent |
| `topmost` | Keep the window on top |
| `tray` | Minimize to tray on/off |
| `startup` | Start with Windows on/off; the host replies `{type:'startupState', value, error}` with what the registry really holds |
| `notify` | Show a Windows popup for alerts |
| `exit` | Exit, after confirmation |
| `cursor` | Move the real mouse pointer (`SetCursorPos`, only while the window is active) |
| `histAppend` | Add readings to the local CSV history |
| `histLoad` | Request history; the host replies `{type:'histData', data, shared}` |
| `histRetention` | Change how many days of history are kept |
| `openHistory` | Open the history folder in Explorer |

**Values the host injects into the page** before load: `window.WF_HOST = {userAgent, version, build, startup}`.
- The page restores the contact email from it if page storage was reset.
- The default app name in the User-Agent follows the version.

**Where data lives:** everything is under `%LOCALAPPDATA%\WeatherFeeds\`.
- `settings.json`: the host's settings
- `history\*.csv`: local station history
- `WebView2\`: the page's preferences, cached grid lookups and acknowledged alerts, all in localStorage. These are tied to the page's origin: changing the host name resets them.

**Central share:**
- **Location:** if `WeatherFeeds.shared.json` sits next to the exe, `historyFolder` points at the shared history.
- **Who writes:** clients merge it with their local history **read-only**; only `--collect` writes it.
- **Settings shows** the folder and the collector status.

### Data sources

All are free and need no API key. All except the Drought Monitor allow direct browser requests (CORS).

| Data | Endpoint | Notes |
|---|---|---|
| Points / grid / stations | `api.weather.gov/points/{lat},{lon}` | Cached per county in localStorage `wf.meta.v1` |
| Forecast periods | `…/gridpoints/{office}/{x},{y}/forecast` | |
| Raw grid (amounts, gusts, temperature series) | `…/gridpoints/{office}/{x},{y}` | About 220 KB each; re-fetched at most every 45 min |
| Latest observation | `/stations/{id}/observations/latest` | Tries the 3 nearest stations until one reports a temperature |
| Observation history | `/stations/{id}/observations?start=…` | **500 per page, newest first: follow `pagination.next`.** NWS keeps 7 days |
| Alerts | `/alerts/active?area=TN,AR,MS` | Matched to counties by `geocode.SAME` (6-digit FIPS) |
| SPC outlooks | `spc.noaa.gov/products/outlook/day{1,2,3}otlk_cat.lyr.geojson` | Point-in-polygon at the county seat; risk rank from `LABEL` |
| Climate | `data.rcc-acis.org/StnData?params=…` (GET) | Sites `MEMthr`, `TUPthr`, `JBRthr`. Records need `sdate:"1870-MM-DD"` with `interval:[1,0,0]` (a `por` start date gives Jan 1 values) |
| Storm reports | `mesonet.agron.iastate.edu/geojson/lsr.php?states=TN,AR,MS&hours=24` | **Use `states=`. `wfos=` is ignored, and the unfiltered feed caps at 100 nationwide** |
| Rivers | `api.water.noaa.gov/nwps/v1/gauges?bbox…`, `/gauges/{lid}`, `/gauges/{lid}/stageflow` | About 40 gauges have flood stages; flood categories cached a week |
| Radar | `radar.weather.gov/ridge/standard/{SITE}_loop.gif` / `_0.gif` | KNQA, KGWX, KLZK, KPAH, SOUTHMISSVLY, CONUS |

---

## 5. Build, run, test

```powershell
.\publish.ps1                      # -> publish\WeatherFeeds.exe (framework-dependent single file) plus deploy files
```

| Command | What it does |
|---|---|
| `publish\WeatherFeeds.exe --demo` | Adds sample `[DEMO]` alerts and a storm report to try alerts, sounds and popups |
| `publish\WeatherFeeds.exe --selftest out.json` | Runs about 45 s, writes a JSON report (counties, errors, wire User-Agent, history, SPC, climate, rivers, version), then exits |
| `publish\WeatherFeeds.exe --collect [--history <dir>] [--contact <email>] [--days <n>]` | Runs the collector once |
| `python -m http.server 8765 --directory src/WeatherFeeds/wwwroot` | Browser preview at http://localhost:8765/ (`?demo` works too) |

The browser preview keeps 3 days of history in localStorage, and the version badge shows "preview".

**Testing a share locally:**
1. `dotnet publish … -o <tempdir>`.
2. Write `<tempdir>\WeatherFeeds.shared.json` containing `{"historyFolder":"history","contactEmail":"…"}`.
3. Run `<tempdir>\WeatherFeeds.exe --collect`, then `--selftest`. The report's `sharedText` shows the collector status.

### Gotchas
- **`publish.ps1` fails with "access denied" while the app is running,** and that includes when it's hidden in the tray. Check `Get-Process WeatherFeeds` first; never kill the user's copy.
- **PowerShell 5.1 here-strings don't pipe to `git commit -F -`.** Write the message to a file and use `-F <file>`.
- **`Set-Content -Encoding utf8` in PS 5.1 writes a BOM.** It's harmless for `.csproj`. For `index.html`, use `[IO.File]::WriteAllText(..., UTF8Encoding($false))`.
- **The Edit tool requires a fresh Read** after any scripted rewrite of a file.
- **The Release `bin` build and `publish` are separate.** You can self-test `bin\Release\net10.0-windows\WeatherFeeds.exe` while the user has the published exe open.
- **The browser pane pauses `requestAnimationFrame` when hidden,** so use `setTimeout` for post-render work.
- **Chromium sends hover events when content moves under a still cursor.** The state screen locks the highlight until the mouse really moves (`hoverLock`).

---

## 6. Working agreements with the user

- **Preview before committing** new UI.
- **Commit locally, then ask before every push.** Pushing to `randaladams/WeatherFeeds` has always been explicitly approved.
- **Versioning:** bump the minor version for a feature round and the patch version for a fix, in `WeatherFeeds.csproj` only.
- **Commit messages** end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Git identity is repo-local: `randaladams` / `randaladams@users.noreply.github.com`.
- **Never commit personal email addresses.** The user's contact lives only in their local `settings.json`. Self-test reports are redacted before display.
- **Don't change system settings** (scheduled tasks, ACLs) on the user's machines yourself. Provide the script and the commands to run.
- **Sounds are not EAS tones.** That's deliberate: this is a TV environment.
