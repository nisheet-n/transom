<p align="center">
  <img src="app/Assets/Square150x150Logo.scale-200.png" width="96" alt="Transom logo" />
</p>

<h1 align="center">Transom</h1>

<p align="center">A slim bar on the edge of your Windows 11 screen for media, clipboard history, conversions, notes and more.</p>

Transom docks to any edge of the screen and stays as thick as your taskbar. Each tool is a single slot. Click a slot and its panel opens toward the middle of the screen. It's free and open source, and it's built natively with WinUI 3, so it looks and feels like the rest of Windows.

## Features

| Tool | What it does |
| --- | --- |
| **Media** | Shows what's playing in any app (Spotify, browsers, players), with artwork, a timeline and play/pause/skip. |
| **Selected text** | Select text in any app and Transom notices it. You can copy it, save it to a note or search the web. AI actions (rewrite, summarize, explain) are coming soon. |
| **Clipboard history** | Keeps text, links and images you copy. You can search, filter, pin, and click an item to paste it back into the app you were using. Passwords from password managers are never saved. |
| **Convert** | Currencies, units and time zones. Select or copy something like `$100`, `5 ft` or `30 °C` and the Convert slot lights up, ready to convert. |
| **Quick notes** | Notes that save as you type, with search and pinning. |
| **Shelf** | Drop files anywhere on the bar to keep them handy, then drag them back out wherever you need them. |
| **Capture** | Takes a screenshot with Windows' snipping tool. You can then copy the text in it (offline OCR), save it or add it to the Shelf. |
| **Keep awake** | Stops your PC from sleeping for 30 minutes, 1 hour, 2 hours or until you turn it off. |
| **Reminders** | Quick reminders that arrive as Windows notifications, with Snooze and Done buttons. |

More:

- **Looks native.** The default style matches your taskbar's glass and follows light/dark mode, accent color and transparency. There are also wallpaper tints, presets, a custom color and a solid mode that saves power on battery saver.
- **Stays out of the way.** Full-screen videos and games cover the bar. Pressing the Start key brings it back.
- **Fits your setup.** Dock it left, right, top or bottom, hide the tools you don't use, and optionally start it with Windows.
- **Light.** About 30 MB of memory, and no CPU use while idle.

## Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| `Win` + `Alt` + `V` | Open clipboard history |
| `Win` + `Alt` + `N` | Open quick notes |
| `Ctrl` + `Alt` + `1`…`9` | Open the bar's items in order (turn on in Settings › General) |

## Privacy

- No telemetry. Transom doesn't collect or send usage data.
- The only network request is for currency rates, made when you open Convert.
- Clipboard history, notes, reminders and shelf links are stored only on your PC.
- Selected-text detection skips password fields, and you can turn it off in Settings › Privacy. VS Code hides editor text from accessibility tools, so in VS Code Transom copies the selection for a moment and then puts your clipboard back as it was.
- Settings › Privacy can also hide the bar from screenshots and screen sharing.

## Run it from source

**You need**

- Windows 11 (build 22000 or newer) with [Developer Mode](https://learn.microsoft.com/windows/apps/get-started/enable-your-device-for-development) turned on
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Any editor. VS Code with the C# Dev Kit works well, and so does Visual Studio.

**Run**

```powershell
git clone https://github.com/nisheet-n/transom.git
cd transom
dotnet run --project app
```

The first run restores packages and registers a development copy of the app, so it takes a minute. In VS Code you can also press <kbd>F5</kbd>.

To quit, click the Transom logo at the end of the bar and choose **Quit Transom**. Quitting this way gives the screen space back to other windows.

**Build a release**

```powershell
dotnet publish app -c Release -p:Platform=x64
```

Release builds are trimmed and ReadyToRun. Publish profiles for `x64`, `x86` and `ARM64` are in `app/Properties/PublishProfiles`.

## Project layout

```
app/
  App.xaml(.cs)            Startup, notifications, crash log
  RailWindow.xaml(.cs)     The bar: slots, panels, docking, drag and drop
  SettingsWindow.xaml(.cs) Settings: General, Appearance, Dock, Tools, Privacy, About
  Controls/                Panels for Convert, Notes, Shelf and Capture
  Services/                Docking, media, clipboard, selection, convert, notes, shelf, reminders, settings
  Native/                  Win32 interop
  Models/                  Small view models for lists
```

## Credits

Currency rates by [ExchangeRate-API](https://www.exchangerate-api.com). Inspired by WindowSill.
