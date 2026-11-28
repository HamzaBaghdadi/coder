# Coder extension programs

An extension can include a **program**: an executable that Coder starts and talks to while it runs.
That is how an extension does more than data (themes, snippets): Claude Code's integration is one.

Coder starts a program only after the user allowed it on that machine. The question comes when the
program would first start, and again after every update (consent is for one package, by its SHA-256).
In Coder's registry, entries with a program must be verified.

## The manifest

```json
{
  "publisher": "coder",
  "name": "claude-code",
  "version": "1.0.0",
  "displayName": "Claude Code",
  "coder": {
    "program": { "win-x64": "bin/claude-code.exe", "linux-x64": "bin/claude-code", "osx-arm64": "bin/claude-code" }
  },
  "contributes": {
    "commands": [{ "command": "claude-code.open", "title": "Open in Terminal", "category": "Claude Code" }],
    "keybindings": [{ "command": "claude-code.open", "key": "ctrl+escape" }]
  }
}
```

- `coder.program` maps a .NET runtime id (`win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`,
  `osx-x64`, `osx-arm64`) to the program's path inside the package. VS Code ignores this section.
- `coder.views` adds tabs to the secondary side bar, beside Coder Agent:
  `[{ "id": "claude-code.view", "title": "Claude Code", "location": "secondarySidebar", "command": "claude-code.open" }]`.
  A view shows a terminal the program opens in it (`terminal/create` with `view`). Showing the tab
  before that runs the view's `command`; so does pressing Enter after its shell exits.
- `contributes.commands` and `contributes.keybindings` are VS Code's. The commands show in the command
  palette from the moment the extension is installed. Running one starts the program if needed and
  sends it `commands/execute`.
- Ship one package per platform (the registry's `platforms` map, below), each with its own program.

## Running

- Started once the window is up (and after an install), in the extension's folder, with stdin/stdout
  redirected and no window. Environment: `CODER_PID` (Coder's process id), `CODER_EXTENSION_ID`,
  `CODER_EXTENSION_FOLDER`.
- Messages are **JSON-RPC 2.0** with `Content-Length` headers, exactly as LSP frames them. Write logs to
  stderr, never stdout. Coder keeps the last lines of stderr for error messages.
- A program that exits unexpectedly is restarted, at most 3 times a minute.
- Before the extension is updated or uninstalled, and when Coder exits, Coder sends `shutdown` (a
  request; answer it) then `exit` (a notification). The program should end within two seconds;
  otherwise it is ended.

## Coder → program

| Message | Kind | Params | Notes |
|---|---|---|---|
| `initialize` | request | `{coderVersion, pid, platform, workspaceFolders: [path], extension: {id, version, folder}}` | Answer within 15 s (any result). |
| `initialized` | notification | `{}` | |
| `commands/execute` | request | `{command}` | One of the manifest's commands was run. |
| `editor/activeChanged` | notification | `{path}` | `path` is null for an unsaved file or no editor. |
| `editor/selectionChanged` | notification | `{path, text, range, isEmpty}` | Sent once the selection settles (~100 ms). |
| `workspace/foldersChanged` | notification | `{folders: [path]}` | |
| `shutdown` / `exit` | request / notification | none | |

## Program → Coder

All paths are absolute. A `range` is `{start: {line, character}, end: {line, character}}`, zero-based
lines and UTF-16 columns (as in LSP). Errors come back as JSON-RPC errors with a message.

| Method | Params | Result |
|---|---|---|
| `workspace/folders` | none | `{folders: [path]}` |
| `editor/open` | `{path, range?, focus?: true}` | `{path, languageId, lineCount}`. Selects `range` when given; `focus: false` opens it behind the current tab. |
| `editor/list` | none | `{editors: [{path, title, languageId, isActive, isDirty}]}` |
| `editor/selection` | none | `{path, text, range, isEmpty}` of the active editor, or `null` |
| `editor/text` | `{path}` | `{text, isOpen}` (the open editor's text, unsaved changes included), or `null` |
| `editor/save` | `{path}` | `{saved}` |
| `editor/isDirty` | `{path}` | `{isOpen, isDirty}` |
| `diff/review` | `{path, proposed, original?, title?, apply?: true}` | `{accepted, text}`. **Waits for the user.** Shows the change in a diff tab with Accept and Reject. Accepted: Coder writes it like a hand edit (an open file changes in its editor as one undo step and is saved), unless `apply` is `false`, in which case the program writes it. Closing the tab, or a new review of the same file, is a reject. Cancel the request (`$/cancelRequest`) to withdraw it. |
| `diff/close` | `{title}` | `{closed}`: closes this program's review diffs with that title (rejecting them). |
| `diff/closeAll` | none | `{closed}`: closes all of this extension's review diffs. |
| `diagnostics/get` | `{path?}` | `{files: [{path, diagnostics: [{range, severity, message, source, code}]}]}`. `severity` is `error`, `warning`, `information` or `hint`. |
| `terminal/create` | `{name, command?, cwd?, env?, view?, message?, replace?}` | `{}`. Opens a terminal named `name` in the default shell, with `env` added, and types `command` into it: in the terminal panel, or in one of the extension's own side-bar views (`view`), which it then shows. A view that already runs a terminal is only shown, unless `replace` is true. `message` is printed on the terminal's screen first. |
| `window/showMessage` | `{text}` | `{}`. Shown in the status bar. |
| `window/log` | `{text}` | notification; ignored for now. |
