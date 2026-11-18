# Changelog

## [2.11.2] — 2026-11-18

A fix for the search.

### Fixed
- Search results could stop opening when one of your open windows had an emoji cut off in its title. They open again,
  and one odd result can no longer break the rest.

## [2.11.1] — 2026-11-18

A better memory, and cleaner pictures.

### Changed
- Say "remember…" (or save this, don't forget, retiens, n'oublie pas…) and it's kept at once: how you want the AI to
  act from now on, something to give back later, a fact about you, or a reminder at a time. "Remember this" keeps
  what was just said. Passwords and card numbers are never kept.
- Pictures the AI makes have no watermark, and a second one in a row waits a moment instead of failing.

## [2.11.0] — 2026-11-18

The AI chat got a second engine, and got a lot quicker.

### Added
- Groq as a second, free AI engine (optional): quick questions and commands go to Groq and come back in under a
  second, bigger or visual ones go to Google, and each takes over when the other is busy, used up or down, even in the
  middle of a task. Add it when the chat offers, with /groq, or in Settings → AI chat.
- Simple commands ("volume 30", "dark mode on", "open spotify and discord") are done at once, without waiting for the AI.
- "Play lofi", "mets du jazz sur spotify": Spotify starts the song in one step, or YouTube plays the first video.
- "Find me a live stream of…", "a video about…": real YouTube results, opened at once.
- The AI learns as it works: what got things done on your PC, your corrections, your habits.
- A usage card on the chat's home page: what's left today for your keys, Google and Groq, and when it all comes back.
- The notch tells you when the AI finishes, or couldn't, while you're in another app.

### Changed
- The AI never gives or opens a link it made up, and sticks to what you asked.
- Much sturdier with Google: retired or busy models are skipped, a minute's limit is never taken for the whole day,
  and a key is never forgotten unless Google says it's wrong.
- Voice is faster with Groq.

## [2.10.0] — 2026-11-18

A better-looking AI chat.

### Changed
- What the AI does shows as a live timeline: each step lights up while it works, then shows how it went and how long it took.
- Asking for your OK is a proper card, with a clear risk badge and what will run.
- When it can't answer, a card says why, and when free requests come back.
- Connecting the AI the first time is quicker: one button, a Paste button, and a check mark when it works.
- Attached pictures and files show as small previews you can remove one by one, and you can drop files straight onto the chat.
- Send lights up when there's something to send, and your messages show when you sent them.

## [2.9.0] — 2026-11-18

The AI can now use your apps, not just open them.

### Added
- The AI works inside any app: it reads the buttons and boxes on screen, clicks, types, uses shortcuts, and checks
  with a picture that it worked. "Call Sam on Discord" or "play lofi on Spotify" now really happen.
- Next-step buttons under answers, and Shorter / More detail when you hover one.
- Battery and PC answers come with live ring meters.
- A new welcome page: a greeting that comes alive, and ideas that fit your PC and your day.
- Chat history grouped by day, with an icon for what each chat was about.
- Preferences show where your chats are kept and how much room they take, with an option to delete old ones.

### Changed
- The chat uses less CPU while it answers, and none while you're in another app. Chat files are smaller.

## [2.8.0] — 2026-11-18

The search can talk now. Press Tab (or pick Ask AI) and you get a chat that can look things up, find your files and do
things on your PC for you. It runs on a free Google AI Studio key, which the notch helps you get the first time.

### Added
- An AI chat in the search, also as its own window. It answers from the web with sources, reads your files, makes
  Word, Excel and PDF files, makes pictures, and acts on your PC (volume, apps, windows, settings, PowerShell).
- It knows what the notch knows: your calls, reminders, notifications, what you copied, what's playing, your
  headset and mouse battery, and how your PC is doing.
- Anything risky (deleting, installing, admin, the registry) waits for your OK, and most actions can be undone.
- Talk to it instead of typing, and have answers read aloud.
- Tables, code with a copy button, clickable links and file paths, picture and video cards in answers.
- Saved chats you can search, rename, export or delete. It can also remember a few things about you, if you want.
- Long alerts from apps are put in a few words (messages from people are left as they are).

### Changed
- The same alert sent twice shows once, and Windows' wordy alerts are shorter. The screenshot alert shows once.
- Copying the same text or picture again no longer adds it twice to the clipboard history.
- A calmer, clearer System page.

## [2.7.1] — 2026-11-16

New Neon backgrounds.

### Changed
- New Neon backgrounds, one for every page, drifting slowly while the panel is open.

## [2.7.0] — 2026-11-16

A better-looking search.

### Added
- A preview beside the results: pictures, videos and documents show their thumbnail, text files their first lines,
  folders how many items they hold, apps whether they're running and where they're installed.
- Files in the list show their real thumbnail instead of a plain icon.
- The best match sits on top as a bigger card.

### Changed
- The notch fits the results: a short list makes it shorter.
- The keys you can press are shown at the bottom and change with what's picked.
- Sharper icons at any display scaling.

## [2.6.0] — 2026-11-16

A new System page, smarter notifications, and a notch that moves faster.

### Added
- A new System page. One line tells you how your PC is doing and why ("Working hard", which app is busiest), with
  the parts that matter in bold. Four tiles show CPU, GPU, memory and network as big numbers with the minute's peak.
  Click a tile to see the three apps using the most of it, and close one from there (click ✕ twice).
- The mic tile on Home shows when your mic is live, and a ring lights up while you talk.
- Sign-in codes: a notification with a code shows just the code. One click copies it, and it never goes into
  your clipboard history.

### Changed
- Notifications get to the point: who sent it and what they said, nothing repeated. Several from the same app add
  up instead of replacing each other, and each stays as long as it takes to read.
- Pop-ups wear the app's own colour (Discord blue, WhatsApp green and so on), and their text slides in.
- Opening and closing feel quicker. The content pours out of the notch when it opens and is drawn back in when it
  closes. Neon switches off like an old TV.
- Neon looks sharper: brighter colours, crisp edges that follow every cut corner, and a soft glow that's easy on
  the eyes. Battery saver turns the glow off.
- The side pill waits for the notch to finish closing before it pops out.

## [2.5.0] — 2026-11-14

Calls get their own page.

### Added
- A Calls page, right after Home. During a call: a big mute button, your app's buttons (camera, share, raise hand,
  captions and more), the call's own volume, and quick replies you can paste into the chat.
- Mute only the call: the other apps can still hear your mic. Hold the button while muted to talk for a moment.
- A red leave button: hold it to hang up, whatever app you're in.
- A small live wave of your voice. It ignores fans and keyboard, so you can tell at a glance if your mic works or
  is left open. The notch also tells you if your mic is silent or picking up steady noise.
- Between calls: a mic check. Live plays your voice back as you talk; Replay plays it after you pause. It tells you
  if you're too quiet or too loud, and fixes it in one click.
- Esc closes any sheet on the page.

## [2.4.0] — 2026-11-14

The search is faster, smarter and smoother.

### Added
- Filters in plain words: `pdf invoice`, `pictures today`, `videos last month`, `excel last week`, `big videos`,
  `.psd`. Results show under the filter's name.
- Words in any order (`notch dynamic`), accents don't matter (`resume` finds `résumé`), and a few capitals find
  a name (`ic` finds `InvoiceCalc`).

### Changed
- Typing is instant: results are worked out in the background and files show up on every key, not after a pause.
- System and game files (`.dll`, `.sys`, caches, game data) never show up.
- Your own folders and recent files come before old, deeply buried ones.
- The highlight and the list move on springs, the search folds away when it closes, and Enter flashes the line.
- On a new PC, files on every drive can be found within seconds of installing.
- The first time you open the search after starting the PC, it's ready straight away.

## [2.3.0] — 2026-11-13

This one is all about the search.

### Added
- Files on every drive, not just your user folders. The notch keeps its own list of file names in the background,
  so this works even on PCs where Windows indexing is off.
- Words inside your documents, through Windows' index.
- Open windows, your clipboard history and the shelf show up in results.
- It learns what you pick and puts it first next time. A grey hint finishes the name you're typing, and Tab takes it.
- Press ↑ in the empty box to get an earlier search back.
- Right-click a result (or press →) for more: open with, show in folder, copy the path, run as administrator and more.
- With your desktop icons hidden, the search shows your desktop first. Type `desktop:` to see only what's on it.
- A Search page in Preferences: change or turn off the shortcut, pick your web search and AI assistant, remove apps
  from Frequent, and clear what it has learned.

### Changed
- Results always come in the same order: apps first, then open windows, the desktop, settings and files.
- Typing feels much smoother.
- The search matches whichever theme you use.
- File results show their type, and the one you're on shows where it lives.

## [2.1.0] — 2026-11-13

### Added
- Search: <kbd>Alt</kbd>+<kbd>Space</kbd>, the magnifier in the header, or start typing while the notch is open.
  Finds apps, files, folders and settings, allows small typos, and puts what you use most first.
- Plain-language commands with a preview before you run them: reminders, timers, focus, volume, brightness, Wi-Fi,
  Bluetooth, dark mode, music, opening and closing apps, shutdown and restart. Chain several with "and".
- Quick answers: maths, unit conversions, time in other cities, dates and countdowns, battery, storage, passwords.
- Reminders that survive a restart. Click one to snooze it.
- Send a question to an AI assistant or a search site from the search.
- Clipboard history is kept between restarts, with pins and pictures.
- Companion: new moments and reactions, and a size setting.

## [2.0.0] — 2026-11-11

### Changed
- New theme names and new built-in artwork.
- New notification sound.

### Added
- Privacy policy, trademark terms and third-party notices.

## [1.8.0] — 2026-11-09

### Added
- An optional 3D companion that sits on a swing under the notch, reacts to what you do and stays out of the way.

## [1.7.7] — 2026-11-08

### Fixed
- Settings are saved safely and restored from a backup if the file gets damaged.
- Game mode no longer flickers on and off.

## [1.7.6] — 2026-11-08

### Added
- A log of overload warnings with the readings and apps behind each one.
- Animated backgrounds for every theme.

### Fixed
- More accurate CPU, GPU, temperature and network readings across different PCs.

## [1.7.5] — 2026-11-01

### Added
- A button to show or hide the desktop icons.

### Changed
- Tidier header on Home. Song pop-ups show up less over the app you're using.

## [1.7.4] — 2026-11-01

### Added
- Optional browser extension: each tab that plays media shows up as its own player.
- Click the cover to bring the player to the front.

### Fixed
- More reliable play, pause and progress across players.

## [1.7.3] — 2026-11-01

### Added
- Spinning record covers in the Neon theme.

### Fixed
- Switching between a call and music on Home.

## [1.7.2] — 2026-11-01

### Fixed
- The FPS counter turns on and off right away.

### Added
- A size option for the FPS counter.

## [1.7.1] — 2026-11-01

### Fixed
- Scrolling works on every PC. Every open player stays on the notch until it's closed.

## [1.7.0] — 2026-11-01

### Added
- CPU and GPU usage and temperatures beside the FPS counter (optional).
- Adjustable overload warning levels.
- Drag images and file links from web pages onto the shelf.

### Changed
- Smooth scrolling everywhere. Lighter animations on PCs without graphics acceleration.

## [1.6.0] — 2026-11-01

### Added
- Incoming-call screen in the Neon theme, and better ring detection.
- Overload warnings for CPU, GPU, memory and temperatures.

### Changed
- Slimmer side pills. Hints drawn by the notch instead of Windows tooltips.

## [1.5.0] — 2026-11-01

### Added
- Three more themes: Prism, Crystal and Abyss.
- Your own pictures as page backgrounds in the Neon theme.
- A new app icon.

## [1.4.0] — 2026-10-31

### Added
- Neon theme, a colour editor and a Preferences window.
- English, French and Arabic.
- Mouse and headset battery levels, incoming-call alerts.

## [1.3.1] — 2026-10-31

### Added
- Lock the notch so it only shows things and ignores clicks.
- The open notch takes on the colour of the app you're in.

## [1.3.0] — 2026-10-31

### Added
- Game mode with a live FPS counter.
- Per-app volume mixer.
- Touch gestures.

## [1.2.0] — 2026-10-30

### Added
- File shelf: drop files, pictures or text on the notch and drag them out later.
- Live call activity with mute.
- Download progress, more alerts, and a picker for what the notch shows.

## [1.1.0] — 2026-10-29

### Added
- Notification sound, per-app volume, external monitor brightness, microphone picker, focus timer.

### Changed
- Animations run at the screen's refresh rate.

## [1.0.0] — 2026-10-29

First release.
