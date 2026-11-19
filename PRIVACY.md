# Privacy policy

*Dynamic Notch · updated 19 November 2026 (version 2.13)*

Dynamic Notch has no account, no ads, no analytics and no telemetry. Nothing about you or your PC is sent to the
developer or to anyone else.

## What it reads

All of this stays on your PC.

| What | Why |
|---|---|
| The media that's playing | To show and control it |
| Notifications, if you allow it in Windows | To show them in the notch |
| The clipboard | For the clipboard history and the "copied" alert |
| Whether the microphone or camera is in use | For the privacy indicator |
| When you last typed or moved the mouse (never which keys) | So the notch and the companion can react |
| The app in front and whether it's full screen | To step aside and hide in games and videos |
| CPU, GPU, memory, temperatures and the apps using them | For the System page, game stats and warnings |
| Battery, Wi-Fi, Bluetooth, brightness and audio devices | For the controls and alerts |
| Start menu apps, recent files, and the names of files and folders on your drives | For the search |
| The windows you have open | So the search can take you to them |
| Your files, browser history and other PC details | Only when you ask the AI chat something that needs them |
| How long each app is in front each day (never what you do in it) | For "what did I use most" and your day's summary |
| The title of the window in front (a document's name, a page's title) | So the Work page knows what you're working on. Videos, chats and mail aren't kept, and you can pause it |
| Text you've selected, and the page or window you're on | Only when you ask the AI about it ("explain this", "summarize this page") |
| Documents and pictures you give the AI | Read on your PC; text in pictures and scans is read by Windows itself |

## What it keeps

Only on your PC, in `%APPDATA%\DynamicNotch`:

- settings;
- the shelf;
- the clipboard history (text and pictures; anything copied from a password manager is skipped);
- reminders;
- how often you open things from the search and what you searched lately, so they show up first;
- a list of file and folder names on your drives, with their dates (never what's inside them), so the search can find
  files anywhere. Windows, program and app-data folders are left out. You can turn this off in Preferences → Search;
- your AI chats, the few facts you ask it to remember, and your AI key (encrypted with your Windows account);
- what the AI learned from tasks that worked, and how much of each AI service you've used today;
- your calendar's private link, if you add one (encrypted with your Windows account);
- answers you marked with 👎;
- today's notifications, and how long each app was in front over the last two weeks;
- the last two weeks of notifications for the Work page's inbox (you can clear them, or mute an app);
- the documents, PDFs and pages you worked on and your projects (names, titles and time spent: never what's in them);
- the AI services' model lists, so the notch never waits for them;
- the overload log;
- technical logs (`app.log`, `error.log`).

You can delete any of it, and uninstalling removes it.

## What goes over the network

- **Connection check:** pings public DNS servers (1.1.1.1, 8.8.8.8, 9.9.9.9) to show your ping.
- **Album art:** may download a playing tab's cover from that website.
- **Dropped links:** a picture or file link dropped on the shelf is downloaded.
- **Search:** web searches, maps, translations and questions for AI assistants open in your browser, and only when
  you press Enter. What you typed goes to that site and is covered by its own privacy policy.
- **AI chat (only if you use it):** your messages, and what it looks up for them (a file it read, a web page, the
  notch's own records), go to the AI services whose keys you entered (Google AI Studio, Groq, or OpenRouter),
  under their privacy policies. Web searches it runs go to Google, Brave, DuckDuckGo or Wikipedia. Video searches
  go to YouTube. Pictures it makes are drawn by Pollinations from the description. Each key is only ever sent to its
  own service. When you ask about the page you're on, its text goes with your question.
- **Calendar (only if you add it):** your calendar is downloaded from its private link (Google, Outlook, iCloud…).
  The link itself is never sent anywhere else.
- **Shorter alerts (with an AI key, can be turned off):** the text of a long alert from an app (never a message from a
  person) is sent to the same AI service to be put in a few words.
- **Browser extension (optional):** talks to the app only on your own PC (`127.0.0.1`, port 47853), and only about
  tabs that play media, or the tab in front when you ask the AI about it.

The app doesn't check for updates and never contacts the developer.

## Changes

Any changes to this policy will be posted here with a new date.

## Contact

https://github.com/Prism99-collab/DynamicNotch/issues
