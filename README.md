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
- **Radar:** NWS loops or latest images for Memphis (KNQA), Columbus MS (KGWX), Little Rock (KLZK), Paducah (KPAH), the Southern Mississippi Valley region and the national view.
- **Data age and refresh:** every panel shows when it was fetched, how old the forecast or feed is, and a countdown to the next refresh, with a manual ⟳ button.
  - Default refresh is 15 minutes for the state panels, 5 for alerts and 5 for radar. All are adjustable.
- **Appearance:** Auto (follows the Windows light/dark setting), Light or Dark. There is also an option to keep the window on top.

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

The output is a single `WeatherFeeds.exe`. The dashboard page is embedded in it.

## Where data is stored

Everything lives in `%LOCALAPPDATA%\WeatherFeeds\`:
- `settings.json`: the window size and position, always-on-top, and the User-Agent.
- `WebView2\`: your dashboard preferences, cached NWS grid lookups and which alerts you've acknowledged.

## Developing the dashboard UI

The whole UI is one page: `src/WeatherFeeds/wwwroot/index.html`. You can work on it in a normal browser:

```powershell
python -m http.server 8765 --directory src/WeatherFeeds/wwwroot
```

Then open http://localhost:8765/. Add `?demo` to the address to inject sample alerts, which is handy on quiet weather days.

A browser can't set the User-Agent header, so only the Windows app is fully NWS-compliant.

Run `WeatherFeeds.exe --selftest report.json` to load live data, write a short JSON report and exit.

## Counties

| State | Counties |
|---|---|
| Tennessee (9) | Shelby, Fayette, Tipton, Lauderdale, Haywood, Crockett, Dyer, Hardeman, McNairy |
| Arkansas (7) | Crittenden, Cross, Lee, Mississippi, Phillips, Poinsett, St. Francis |
| Mississippi (11) | DeSoto, Marshall, Tate, Tunica, Benton, Tippah, Alcorn, Panola, Lafayette, Coahoma, Quitman |

Each county's forecast and nearest observation station come from its county seat's coordinates. Alerts are matched by county FIPS (SAME) code. The county list is the `COUNTIES` table near the top of the script in `index.html`.
