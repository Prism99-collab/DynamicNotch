# Changelog

All notable changes to Dynamic Notch are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/),
and versions follow [Semantic Versioning](https://semver.org/).

## [1.7.2] — 2026-11-01

### Fixed
- Turning on "FPS in games" works at once: one Windows prompt, and the frame rate shows in the next game. No sign-out
  and no restart of the notch needed any more (before, the switch stayed off until you signed out and back in).
- The FPS switch shows what you chose, in the notch's settings and in Preferences.
- Turning FPS off takes effect immediately, even with a game in front.

### Added
- Preferences → Behaviour → "Size in games": Small, Normal, Large or Extra large for the whole FPS pill (the frame
  rate and the CPU/GPU extras together). Applies at once, even mid-game.

## [1.7.1] — 2026-11-01

### Fixed
- Scrolling in the notch works on every PC: the home page, the sound-device lists and every other list scroll
  even where Windows' "Scroll inactive windows when I hover over them" is off (the notch catches the wheel over
  itself; everywhere else the wheel is untouched; never in games).
- Every open player stays on the notch, playing or paused, until it's closed: Spotify, each YouTube tab, a film…
  The wheel or a swipe on the Home card goes through the call and every player in turn, with one dot per card.

## [1.7.0] — 2026-11-01

### Added
- FPS pill extras (Preferences → Behaviour, all off by default): CPU and GPU usage in %, CPU and GPU temperature
  in °C, beside the frame rate in small, quiet type; they change once a second at fixed widths, and CPU and GPU each
  get the colour you choose. A temperature the PC doesn't report simply isn't shown.
- Cyberpsychosis / strain warnings: set each target's line yourself (5 % / 5 °C steps) in Preferences → Alerts.
- Drag from any web page onto the shelf: an image, a download link, a picture embedded in the page or a link
  straight to a file is fetched as the real file (a plain page link stays a link).
- Clipboard history keeps your last 30 texts; recent files show 15; both scroll.

### Changed
- Every list in the notch scrolls smoothly (devices, app mixer, clipboard, files, shelf, the live list).
- PCs without graphics hardware for drawing (basic GPUs, virtual machines, Remote Desktop) get the light motion
  automatically, so the notch stays fast there.

### Fixed
- The sound-device list no longer shows the music or call card through it when they switch while it's open; it
  has a solid backdrop.
- A taken Win+Alt+N shortcut is noted in the log instead of failing silently.

## [1.6.0] — 2026-11-01

### Added
- Holocall: in Cyberpunk an incoming call rings as a holocall: the caller's picture in a HUD frame, INCOMING CALL
  decoding in, a live signal line; answered, LINK ESTABLISHED.
- Ringing is caught more reliably: besides call notifications (any app, browsers included) and a call app
  flashing, a call app ringing out loud in the background (Discord, Teams, WhatsApp, Zoom…) is heard too.
- Strain warnings (Cyberpunk: "Cyberpsychosis risk"): a warning when CPU, GPU, memory, GPU or CPU temperature stays
  over its line for 20 s, never in games; choose what's watched in Preferences → Alerts. A click opens Task Manager.
- Cyberpunk: the notch switches off like a CRT when it closes (a bright line, then a dot); changed digits decode in
  (clock, timer, call length, battery); downloads and the timer show the game's segmented quickhack bar; locking is
  BLACK ICE ENGAGED, unlocking BREACH SUCCESSFUL; the battery is a segmented health bar; RAM cells under the
  memory gauge.
- Cyberpunk ships with default page backgrounds (your own pictures in the Backgrounds folder still win).

### Changed
- Side pills are slimmer and adapt to what they show (a small bead for an icon or a cover, a touch taller with
  text), always clearly smaller than the notch; Cyberpunk cuts are in proportion.
- Hover hints: every hint in the notch is the notch's own (in front, in the theme's style), shorter, and fades by
  itself after a few seconds. Windows' white tooltips no longer appear behind the notch.

## [1.5.0] — 2026-11-01

### Added
- Three new themes beside Original and Cyberpunk, with their own glass controls, colours and motion:
  - Liquid glass: tinted glass with a slow teal and violet aurora inside, a rim that catches light, a caustic
    along the bottom edge and a glint that follows the pointer near the top of the screen; a small liquid wobble
    when it changes shape; its glow adapts to the app you're on.
  - Crystal: clear, frosted glass with a prism rim that splits light into a thin rainbow.
  - Abyss: the deep sea, a bioluminescent glow from below and glowing particles that drift up now and then.
  The theme button in the notch moves to the next theme; Settings and Preferences (five preview cards) pick one
  directly. Everything on the Colors page applies on top of every theme.
- Cyberpunk: your own faded pictures behind each page of the control center, read from
  `%APPDATA%\DynamicNotch\Backgrounds` (`page1.*` … `page6.*`); they cover the whole panel and cross-fade between
  pages. Nothing is bundled with the app.
- Cyberpunk pop-ups arrive like the game's HUD: a scan wipes them in, the icon flickers on like a hologram, the
  title and the line under it decode behind a block cursor.
- Cyberpunk reacts to what happens on the PC: a tiny glitch when an app comes to the front, a song changes or
  pauses, a new activity starts, a device's battery changes, a side pill appears, or a button is pressed (at most
  one every 0.7 s; never in games or on battery mode). Glitches tear the content sideways for a few frames.
- A new app icon: the cyber notch, drawn in HD at every Windows size.

### Changed
- Music bars: the cover's two strongest colours flow across them, each bar lit at its tip; they move with physics
  (up on the beat, falling with gravity), each following a different part of the sound.
- Preferences: a slim floating scrollbar and smooth wheel scrolling.

## [1.4.0] — 2026-10-31

### Added
- Cyberpunk theme (the yellow button beside the lock, Settings or Preferences): cut 45° shape with a yellow frame,
  cyan traces, hazard stripes and a page tag; the 2077 palette and Bahnschrift everywhere; cut-corner tiles,
  switches, chips, rows, sliders and segmented gauges; glitch bursts, a scan line on opening, decoding titles and a
  data pulse along the frame. Side pills get their own cut frames. In games the FPS tag is just the number.
  Switching it off restores the original look exactly; the tray icon follows the theme.
- Colors page: a colour wheel and brightness mixer for six things (accent, glow, edge, clock, panel, pills),
  previewed live on the notch, with Auto, eight presets and Shuffle.
- Preferences window (right-click the tray icon): General, Appearance, Behaviour, Alerts, Pages, Shortcuts and
  About, with search, instant saving and Mica on Windows 11. Pages of the control center can be hidden.
- Languages: English, French and Arabic (right to left) for the notch's pages, Settings, Colors, the tray menu and
  Preferences.
- Mouse and headset battery rings beside the date, with low-battery alerts.
- Incoming-call alert before you answer (Discord, Teams, WhatsApp…).

### Changed
- Smart and Settings are one page with three tabs (Display, Behaviour, Smart); the tray menu is short (open,
  Preferences, lock, exit).
- FPS pill: the frame count keeps running for a minute after Alt-Tab, so going back to the game is instant; the
  number shows the exact rate (no easing); Store / Xbox games are recognised; games on another monitor and
  anti-cheat-protected games are detected; launchers and clients never get the pill.
- Side pills follow the notch instantly and absorb quickly when it opens.

### Fixed
- Device rings no longer disappear when the activity switcher shows.
- Lower render cost while a game is full screen (the notch's window is hidden there).

## [1.3.1] — 2026-10-31

### Added
- Lock: a gold button in the control center (also in Settings and the tray menu). Locked, the notch keeps showing
  music, calls, alerts and the FPS pill but reacts to nothing: no opening at the top edge, no file drops, no
  clicks, no touch gestures. A click on its tray icon (or Win+Alt+N) unlocks it. It stays locked across restarts.
- "Drop files on the notch" switch in Settings.
- The open notch takes the colour of the app you're on: a deep glow of it fades in behind the panel, the rim lights
  up in it and the sliders wear it. A call, download, timer, Focus or a colourful album cover still lead with their
  own colour.
- Quick toggles have their own colours (Wi-Fi blue, Bluetooth indigo, Focus purple), dimmer when on but not connected.
- Sliders come alive under the pointer: a soft knob springs in, the track thickens while dragging; the brightness
  icons glow with the level.

### Fixed
- Dragging a desktop icon near the top of the screen turned the notch into a drop zone: it now wakes only when the
  dragged thing is on the notch itself.

## [1.3.0] — 2026-10-31

### Added
- Game mode: while a game is in front, the notch becomes the smallest pill it has, the live frame rate and nothing
  else (no pop-ups, glow or side island). The rate is worked out from every real frame's timestamp, refreshed
  20 times a second, for any game (DirectX 9 to 12, Vulkan, OpenGL), counted through the graphics kernel. Its window
  shrinks around the pill while you play so it costs almost nothing. "FPS in games" in Settings; on a PC where the
  account can't read frame events yet, switching it on asks Windows once.
- Mini mixer: an Apps tab in the sound picker with every app's own volume and a mute on its icon.
- Touch screens: pull the notch down with a finger and it stretches after it, then springs open when you let go
  (or flick); let go early and it springs back. Swipe sideways on it to switch which live activity leads, tap to
  open, and it gives a little squeeze under your finger. None of this runs on a PC without a touch screen or pen.

### Removed
- The scrolling network and power graphs on the System page (the numbers stay).

### Fixed
- A game could be set up twice, and leaving one game for another quickly could stop the new frame count.
- Turning on "FPS in games" no longer freezes the notch while Windows' permission prompt is open.
- Dragging from the shelf no longer looks up the app under the cursor on every mouse move; the browser's download
  list is read less often.

## [1.2.0] — 2026-10-30

### Added
- File shelf: drag a file, picture or text up to the notch and it morphs into a drop zone (marching outline,
  bobbing arrow, what's already kept fanned out); let go and it swallows it. Drag things back out from the new
  Shelf tab on the Clipboard & Files page. Kept across restarts. Dragging out adapts to the app: Excel, Word,
  PowerPoint and other editors get the picture or text itself, folders and chats get the file; a Copy button
  puts it on the clipboard for Ctrl+V anywhere. Pictures from a browser or an Office app, and Outlook
  attachments, are kept as real files.
- Live call activity: while Discord, Teams, Zoom, WhatsApp, Slack, a browser (Meet)… records from a microphone, the
  notch becomes a call pill (app icon, live dot, voice bars that move with you, timer; red when muted). The control
  center shows a call card with a big mute button and "back to the call".
- "Opening …" alert with the app's icon when an app starts (switch in Settings and the tray menu).
- Keyboard language alerts (Win+Space / Alt+Shift), screen connected / disconnected, and "Fully charged" on the charger.
- Choose what the notch shows when several things are live: a switcher on Home lists the call, the timer, Focus,
  downloads and every tab or app playing sound (each browser tab separately); a "+N" list when they don't fit.
  During a call with music, scroll or drag the Home card to flip between them. Tap the side island on a touch screen to swap.
- System page, live: a download card with the real progress from the browser ("24 MB of 65 MB · 5 MB/s · 9 s left"),
  then Open / Show in folder; the last minute of network traffic scrolling behind the speeds; the battery card's
  charging or discharging watts as a live graph with a pulsing bolt.
- Touch screens and pens: tap the notch to open it, swipe up to close, lists scroll with a finger.
- A hello on the first start, so a new install is never mistaken for "nothing happened".
- Hover hints drawn inside the notch (dark glass, in front of it) instead of white Windows tooltips hidden behind it.

### Changed
- The music visualizer follows the real sound; the slim glow over windows pulses with it.
- Jelly morph: height and width spring on their own timing, so the notch stretches organically.
- The notch leans toward the cursor while you touch the top edge or hover a clickable alert.
- Good news (done, connected, charging, full) gets a light sweep; alerts cast a soft glow of their colour below the notch.
- Crisper edges: a one-pixel rim that fades up the sides, a faint glass bevel when idle, and a glow under live
  music or calls that moves with the sound.
- Opening the notch: rows rise in one after another; album covers flip to the next song.
- App and notification icons are loaded in high resolution.
- Installed as a normal app folder with its own .NET runtime instead of one self-extracting file: nothing is
  unpacked to a temp folder at start (some antivirus tools and locked-down PCs blocked that).

### Fixed
- On some PCs the notch didn't appear after installing: one feature failing at start (no Bluetooth radio, no
  battery, no audio device...) took the whole notch down silently. Each feature now starts on its own, and if
  the notch can't start at all it says why. A "presentation mode" flag left on by some PCs no longer hides it.
- The Home card could go blank after a call ended.
- The monitor brightness slider vanished after a reboot (handle 0 was taken for "no monitor"); both brightness
  sliders now retry after startup, wake and display changes instead of disappearing.

## [1.1.0] — 2026-10-29

### Added
- Notification ringtone, played at your own volume (one ring per burst of messages).
- Volume slider for just the playing app (Spotify, a browser…), under the media buttons.
- Brightness slider for an external monitor (DDC/CI), next to the laptop's.
- Input devices in the sound picker: switch microphones as easily as speakers.
- Bluetooth Low Energy devices (game controllers, earbuds…) on the Bluetooth tile and in connect alerts, with their battery
  level ("50% · Xbox Wireless Controller") and a heads-up when it drops to 15 %.
- Pomodoro focus timer on the Tools page: progress ring, focus / break, rounds, scroll to adjust, next step offered when one ends.
- Keep awake tool, colour-coded tool tiles, right-click shortcuts (notch clipboard, Downloads).
- Alerts for error / warning dialogs from any app (click: copies the message and brings the dialog up) and for failed downloads.
- A tiny shiver for things that didn't work (failure notifications, a toggle Windows refused, a disconnected Bluetooth device)
  and a bump at the end of the volume / brightness range.

### Changed
- Over a maximized window the notch shrinks to a slim glowing bar and fades away when the mouse heads for a tab under it.
- Animations run at the screen's real refresh rate instead of 60 fps.

### Fixed
- Lag when opening the sound picker (device names are now read once, in the background).
- Stutters during animations: background garbage collection, and icons, thumbnails, drive info, sounds and audio lookups
  no longer run on the UI thread.
- Alerts that waited behind a full-screen app no longer all play minutes later; a screenshot no longer shows a "Copied" alert first.
- Garbled icons and text in the Smart page's alerts.

## [1.0.0] — 2026-10-29

First public release.

### Notch
- Dynamic-Island-style notch with spring-physics motion, crisp at any display scaling.
- Minimal by default, click-through, slims over maximized windows, hides in full screen.
- Live activities: music (cover-tinted visualizer), timer, downloads, Focus; a second activity splits into its own island.
- Expressive motion: pulse for new things, shake for problems.

### Control center
- Swipeable pages: Home, System, Tools, Clipboard & Files, Smart, Settings.
- Quick toggles (Wi-Fi, Bluetooth, Airplane, Focus, Mic, Power mode), volume with output picker, brightness, media controls.
- System monitor with battery time left, ping & signal, battery health and power draw.

### Alerts
- Clickable pop-ups for notifications, attention requests, downloads, screenshots, USB drives, Wi-Fi, Bluetooth,
  battery, Caps Lock and copied text, each tinted with its app's colour.
- Smart helpers: battery-drain warnings, shaky-connection alerts, charge reminder, Downloads tidy-up,
  break reminder, opt-in meeting mode.
- Focus mode that holds notifications and summarises what you missed.

### Windows integration
- Per-user installer for x64 and ARM64 (no admin rights), Start menu and desktop shortcuts, clean uninstall.
- Starts at sign-in and comes back after lock, sleep or a crash; Exit keeps it off until reopened.
- Dark tray menu, automatic battery mode, about 1 % of one CPU core when idle.

[1.3.1]: https://github.com/Prism99-collab/DynamicNotch/releases/tag/v1.3.1
[1.3.0]: https://github.com/Prism99-collab/DynamicNotch/releases/tag/v1.3.0
[1.2.0]: https://github.com/Prism99-collab/DynamicNotch/releases/tag/v1.2.0
[1.1.0]: https://github.com/Prism99-collab/DynamicNotch/releases/tag/v1.1.0
[1.0.0]: https://github.com/Prism99-collab/DynamicNotch/releases/tag/v1.0.0
