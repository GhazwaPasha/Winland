# Project rules

- **No automatic testing with screenshots/clicks.** Don't launch the app and drive it with screenshots, clicks, or other desktop/browser automation to "verify" a change on your own initiative. Verify via build output, direct process exit-code/stderr checks, log files the app already writes (e.g. `accent-debug.log`), or code review instead. Only take a screenshot or interact with the running app if the user explicitly asks for it.
