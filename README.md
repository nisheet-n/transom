<p align="center">
  <img src="app/Assets/Square150x150Logo.scale-200.png" width="96" alt="Transom logo" />
</p>

<h1 align="center">Transom</h1>

<p align="center">A slim bar on the edge of your Windows 11 screen for media, volume, clipboard history, conversions, notes and more.</p>

Transom docks to any edge of the screen and stays as thick as your taskbar. Each tool is one slot. Click it and its panel opens toward the middle of the screen. It's free and open source, built natively with WinUI 3, so it looks and feels like the rest of Windows.

## Features

| Tool | What it does |
| --- | --- |
| **Media** | Shows what's playing in any app (Spotify, browsers, players), with artwork, a timeline and play/pause/skip. |
| **Volume** | Master volume plus a slider and mute button for each app that's playing. Scroll over the slot to change the volume without opening anything. |
| **Selected text** | Select text in any app and Transom notices it, with no copying needed. Copy it, save it as a note or search the web. Select a link to open it, shorten it or turn it into a QR code. AI actions (rewrite, summarize, explain) are coming soon. |
| **Clipboard history** | Keeps the text, links and images you copy. Search, filter, pin, and click an item to paste it into the app you were using. Passwords from password managers are never saved. |
| **Convert** | Currencies, units and time zones from one bar: type `50 EUR`, `5 ft` or `3 PM EST`, or a name like `CAD` or `Paris` to add it to your list. Select a price, measurement or time anywhere and Convert lights up, ready to go. |
| **Quick notes** | Notes that save as you type, with search and pinning. |
| **Capture** | Takes a screenshot with Windows' snipping tool. Copy the text in it (offline OCR), draw on it (pen, highlighter, rectangle, arrow), pick a color, save it or add it to the Shelf. |
| **Shelf** | Drop files anywhere on the bar to keep them handy, then drag them back out wherever you need them. |
| **Keep awake** | Stops your PC from sleeping for 30 minutes, 1 hour, 2 hours or until you turn it off. |
| **Reminders** | Quick reminders that arrive as Windows notifications, with Snooze and Done buttons. |
| **Call controls** | Appear while an app is using your microphone: mute your mic in every app at once, jump to the call, and turn your camera on or off or leave the call in Teams, Zoom and Google Meet. |

More:

- **Looks native.** The default style matches your taskbar's glass and follows light/dark mode, accent color and transparency. Wallpaper tints, presets, a custom color and a solid mode for battery saver are in Settings.
- **Stays out of the way.** Full-screen videos and games cover the bar, and the Start key brings it back. On a side dock, the slots start below the title-bar line so they line up with your windows' content.
- **Fits your setup.** Dock it left, right, top or bottom, hide the tools you don't use, and optionally start it with Windows. Each tool's settings sit right next to its icon in Settings.
- **Live details on top and bottom docks.** Slots grow to show their value right on the bar: a conversion like `$100 → ₹9,499`, the song that's playing, Keep awake's time left and your next reminder. Your pinned clipboard items appear as chips you click to paste, and shelf files appear as icons you click to open or drag anywhere.
- **Make it yours.** Choose the icon color (automatic, accent or a preset), icon size and badge color, and show or hide the separators.
- **Light.** About 30 MB of memory and no CPU use while idle.

## Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| `Win` + `Alt` + `V` | Open clipboard history |
| `Win` + `Alt` + `N` | Open quick notes |
| `Ctrl` + `Alt` + `1`…`9` | Open the bar's items in order (turn on in Settings › General) |

## Privacy

- **No telemetry.** Transom doesn't collect or send usage data. It makes only two kinds of network request, both over HTTPS: currency rates when you open Convert, and the link you chose when you click **Shorten** (sent to TinyURL, or v.gd if TinyURL can't be reached). QR codes are made on your PC.
- **Local data.** Clipboard history, notes, reminders and shelf links stay in Transom's private app folder on your PC. Every save keeps the previous version as a backup, and a file Transom can't read is set aside rather than overwritten.
- **Selected text** is read only to light up Convert and the selection actions. It's kept in memory, cleared when you switch apps, and never saved or sent anywhere. Password fields and terminals are skipped.
- **The copy fallback.** Some apps, like VS Code, hide their text from accessibility tools. In those, Transom copies the selection for a moment, puts your clipboard back, and removes the copy from Windows' clipboard history (Win+V). It never does this in password managers, terminals or File Explorer. You can turn selection reading off in Settings › Selected text and AI.
- **Screen capture.** Settings › Privacy can hide the bar from screenshots, recordings and screen sharing.

## Install

Transom installs as an MSIX package of about 16 MB. It uses the shared Windows App Runtime, which Windows installs once and other apps share. Windows only runs MSIX packages signed with a trusted certificate, and Smart App Control also blocks unsigned apps, so a release has to be signed:

- **Microsoft Store (planned).** One-click install and automatic updates. The Store signs the package and installs the runtime for you.
- **GitHub Releases.** A signed `.msix` you double-click to install.

Until a signed release is out, run it from source.

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

The first run restores packages and registers a development copy of the app, so it takes a minute or two. In VS Code you can also press <kbd>F5</kbd>.

To quit, click the Transom logo at the end of the bar and choose **Quit Transom**. Quitting this way gives the screen space back to other windows.

**Build a release**

```powershell
dotnet publish app -c Release -p:Platform=x64
```

Release builds are trimmed and ReadyToRun. Publish profiles for `x64`, `x86` and `ARM64` are in `app/Properties/PublishProfiles`. To build an installer package, add `-p:GenerateAppxPackageOnBuild=true`. The package then needs signing before it installs on other PCs.

## Project layout

```
app/
  App.xaml(.cs)            Startup (single instance), notifications, crash log
  RailWindow.xaml(.cs)     The bar: slots, panels, docking, drag and drop
  SettingsWindow.xaml(.cs) Settings, with each tool's section next to its icon
  CaptureEditor.xaml(.cs)  Drawing and color picking on screenshots
  Controls/                Panels for Convert, Notes, Shelf, Capture, Volume and calls
  Services/                Docking, media, audio, calls, clipboard, selection, convert, notes, shelf, reminders, settings
  Native/                  Win32 and COM interop, clipboard snapshots
  Models/                  Small view models for lists
```

## Credits

Currency rates by [ExchangeRate-API](https://www.exchangerate-api.com). Short links by [TinyURL](https://tinyurl.com). QR codes by [QRCoder](https://github.com/codebude/QRCoder) (MIT). Inspired by WindowSill.
