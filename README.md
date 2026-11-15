# WeatherFeeds

A Windows 10/11 weather dashboard for the **Memphis DMA** (27 counties in Tennessee, Arkansas and Mississippi), built on the free National Weather Service API at [api.weather.gov](https://www.weather.gov/documentation/services-web-api).

## Features

- **Four-panel dashboard:** one panel each for Tennessee, Arkansas and Mississippi, plus a High Alerts panel.
  - Each county row shows the current temperature, conditions, today's high/low and an alert badge.
- **County detail screen:** click a state panel to see every county in that state.
  - Current observation: temperature, wind, gusts and humidity.
  - The next six forecast periods, with chance of rain.
  - Any active alerts for the county.
- **High Alerts:** shows Extreme and Severe alerts touching any DMA county. Optionally includes Moderate. Click an alert to read the full text.
- **Alert sounds:** Extreme, Severe and Moderate each have their own sound.
  - Each alert sounds once when it first appears.
  - Extreme alerts can repeat every 30 s until you acknowledge them.
  - When NWS updates an alert you've already acknowledged, the update stays acknowledged and doesn't sound again.
  - The sounds are made in code and are deliberately **not** EAS tones.
- **Zoomed county view:** click a county card for a full-screen view with large type. It shows:
  - current conditions, including feels-like, dew point, pressure and visibility
  - alerts and all the extra details below
  - a wide temperature chart
  - the complete 7-day forecast text

  Use ‹ › or the arrow keys for the previous or next county, and right-click or Esc to go back.
- **County cards:** open a state, or click a county row to jump straight to that county. The app moves the mouse pointer onto it, and the highlight then follows the mouse. The cards also show:
  - **Temperature trend chart** along the bottom: 24 h, 3, 7 or 30 days of observed temperature, plus the next 24 h of forecast (dashed). Hover over it for exact readings.
  - **Observed trends:** the last 24 h high and low, peak gust, and pressure with its 3-hour tendency.
  - **Next 24 h amounts:** rain, snow, ice, peak gusts, thunder chance and feels-like range, from the NWS forecast grid.
  - **SPC severe risk** for today, tomorrow and day 3, from the Storm Prediction Center outlooks. A Marginal-or-higher risk also shows as a badge on the dashboard row.
  - **Climate** for the nearest climate site (Memphis, Tupelo or Jonesboro, via ACIS): today's normal high/low, records with years, and month- and year-to-date rain versus normal.
  - **Storm reports** from the last 24 h (Iowa Environmental Mesonet).
- **Rivers:** about 40 NOAA river gauges around the DMA. Each shows its stage, flood category, flood stage, 24 h change, forecast crest, and a 7-day chart with the flood line. Flooding gauges are listed first, and the header button flags them.
- **Radar:** NWS loops or latest images for Memphis (KNQA), Columbus MS (KGWX), Little Rock (KLZK), Paducah (KPAH), the Southern Mississippi Valley region and the national view.
- **Data age and refresh:** every panel shows when it was fetched, how old the forecast or feed is, and a countdown to the next refresh, with a manual ⟳ button.
  - Default refresh is 15 minutes for the state panels, 5 for alerts and 5 for radar. All are adjustable.
- **Appearance:** Auto (follows the Windows light/dark setting), Light or Dark. There is also an option to keep the window on top.
- **System tray:** the tray icon is always present while the app runs.
  - Click it to open the window.
  - Right-click it for Open and Exit.
  - An optional setting sends the window to the tray instead of the taskbar when you minimize it.
- **Alert popups:** each new high alert shows a Windows notification as well as playing its sound. Click the notification to open the dashboard. You can turn popups off in Settings, and there's a Test popup button.
- **Navigation:** right-click, Esc or the mouse back button goes back one level: from Settings, from a county screen or from the radar screen.
- **Exit:** the ⏻ Exit button, the window's ✕ and the tray's Exit all ask for confirmation first.

## NWS compliance

NWS asks every app to send a `User-Agent` header that names the app and gives a contact email.
- **First run:** the app opens Settings and asks for your contact email.
- **What it sends:** `WeatherFeeds/1.0 (your-email)` with every request, including radar images.
- **API key:** the optional `API-Key` header NWS is testing is not needed for these feeds. There's a Settings field for it if one is ever required.

## Requirements

- Windows 10 or 11 (x64)
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0). You don't need it if you build with `-SelfContained`.
- Microsoft Edge WebView2 Runtime. It comes with Windows 11 and current Windows 10.

## Build

```powershell
.\publish.ps1                 # -> publish\WeatherFeeds.exe (needs .NET 10 Desktop Runtime)
.\publish.ps1 -SelfContained  # -> larger exe that runs without .NET installed
```

The output is a single `WeatherFeeds.exe`. The dashboard page and icon are embedded in it, and the page is served from memory at `https://weatherfeeds.example/`. Nothing is unpacked to disk.

### Icon

`src/WeatherFeeds/Assets/WeatherFeeds.ico` holds sizes from 256 down to 16 px. It was cut from the master artwork by `Assets/make_icon.py`:

```powershell
python src/WeatherFeeds/Assets/make_icon.py <master.png> src/WeatherFeeds/Assets
```

That command also writes `icon-256.png`. Copy it to `wwwroot/icon.png`, which the dashboard header uses.

## Where data is stored

Everything lives in `%LOCALAPPDATA%\WeatherFeeds\`:
- `settings.json`: the window size and position, always-on-top, minimize-to-tray, and the User-Agent.
- `history\`: one CSV per weather station (time, temperature, dew point, pressure, wind, gust, humidity). The app keeps 30 days by default, adjustable to 7, 90 or 365 in Settings. NWS itself only keeps a few days, so longer trends build up while the app runs. Settings has an **Open history folder** button.
- `WebView2\`: your dashboard preferences, cached NWS grid lookups and which alerts you've acknowledged.

## Central share and collector

Without a collector, history only builds while someone has the app open. Gaps of up to 7 days fill in on the next launch, because NWS keeps 7 days. For history with no gaps, shared by every user, run the collector on an always-on server.

1. **Set up the share.**
   - Run `.\publish.ps1`, then copy `WeatherFeeds.exe` and the deploy files to the share, for example `\\server\WeatherFeeds\`.
   - Copy `WeatherFeeds.shared.sample.json` to `WeatherFeeds.shared.json` and fill in `contactEmail`. NWS requires a contact in every request.
   - Change `historyFolder` if the default `history` folder next to the exe isn't where you want the data.
2. **Schedule the collector.** On the server, as an administrator, run:

   ```powershell
   .\install-collector.ps1 -ExePath \\server\WeatherFeeds\WeatherFeeds.exe -EveryMinutes 30
   ```

   This registers a scheduled task that runs `WeatherFeeds.exe --collect` every 30 minutes, whether or not anyone is logged on. It runs under the Windows account you enter, which needs write access to the history folder.
3. **Point users at the share.** Users start `WeatherFeeds.exe` from the share, or a shortcut to it.
   - Each copy reads the shared history and never writes to it, so the share only ever has one writer.
   - Each copy also keeps its own local history as a fallback.
   - Settings → History shows when the collector last ran, and warns if that was more than 3 hours ago.

What the collector does on each run:
- It builds its station list from the three nearest stations to each county seat (`counties.js`) and refreshes the list weekly.
- It downloads each station's new readings, following NWS paging, and backfills up to 7 days if a station's history has a gap.
- It writes `collector.log` and `collector-status.json` in the history folder.
- Only one collector can run per folder at a time.
- It keeps 400 days of history unless the config says otherwise.

| `--collect` option | Meaning |
|---|---|
| `--history <folder>` | Write here instead of the shared config's folder |
| `--contact <email>` | NWS contact, if not in the shared config |
| `--days <n>` | Retention in days |

| Exit code | Meaning |
|---|---|
| 0 | OK |
| 1 | Some stations failed |
| 2 | Nothing worked |
| 3 | Another collector is already running |
| 4 | Setup problem (no folder or contact) |

**Updating the app on the share:** Windows won't replace `WeatherFeeds.exe` while anyone has it open, and that includes the collector's short runs. Copy the new exe in when users have closed the app.

## Developing the dashboard UI

The whole UI is one page: `src/WeatherFeeds/wwwroot/index.html`. You can work on it in a normal browser:

```powershell
python -m http.server 8765 --directory src/WeatherFeeds/wwwroot
```

Then open http://localhost:8765/. Add `?demo` to the address to inject sample alerts, which is handy on quiet weather days.

A browser can't set the User-Agent header, so only the Windows app is fully NWS-compliant.

Command-line options:
- `WeatherFeeds.exe --demo` adds sample `[DEMO]` alerts, so you can try the alert panel, sounds and popups.
- `WeatherFeeds.exe --selftest report.json` loads live data, writes a short JSON report and exits. You can combine it with `--demo`.

## Counties

| State | Counties |
|---|---|
| Tennessee (9) | Shelby, Fayette, Tipton, Lauderdale, Haywood, Crockett, Dyer, Hardeman, McNairy |
| Arkansas (7) | Crittenden, Cross, Lee, Mississippi, Phillips, Poinsett, St. Francis |
| Mississippi (11) | DeSoto, Marshall, Tate, Tunica, Benton, Tippah, Alcorn, Panola, Lafayette, Coahoma, Quitman |

Each county's forecast and nearest observation station come from its county seat's coordinates. Alerts are matched by county FIPS (SAME) code. The county list is the `COUNTIES` table near the top of the script in `index.html`.
