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
- `coder.agents` (0.5.0+) brings coding agents to Coder Agent's picker:
  `[{ "id": "claude-code.agent", "name": "Claude Code", "args": ["--acp"] }]`. See
  [Coding agents](#coding-agents-agent-client-protocol).
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
| `diagnostics/changed` | notification | `{}` | 0.5.0+. Some file's diagnostics changed; sent once a burst settles (~150 ms). Read them with `diagnostics/get`. |
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
| `terminal/create` | `{name, command?, program?, args?, cwd?, env?, view?, message?, replace?}` | `{}`. Opens a terminal named `name` in the default shell, with `env` added, and types `command` into it: in the terminal panel, or in one of the extension's own side-bar views (`view`), which it then shows. A view that already runs a terminal is only shown, unless `replace` is true. `message` is printed on the terminal's screen first. With `program` (0.5.0+), see [Running a program in a terminal](#running-a-program-in-a-terminal). |
| `window/showMessage` | `{text}` | `{}`. Shown in the status bar. |
| `window/showMessageRequest` | `{message, detail?, actions?: [{title, ...}]}` | The action the user picked (the object you sent, extra fields included), or `null` when they dismissed it. 0.5.0+. See [Asking the user](#asking-the-user). |
| `editor/setDecorations` | `{path, decorations: [{line, text, kind?}]}` | `{}`. 0.5.0+. Text after the end of lines (zero-based `line`), in faint italics; `kind` (`error`, `warning`, `information`, `hint`) colors it like a diagnostic, on a light tint. Replaces this extension's decorations in that file (an empty array clears them); other extensions' stay, and where two decorate a line the more severe shows. Up to 10,000 per file, 500 characters each. They're cleared when the program stops. |
| `editor/clearDecorations` | none | `{}`. 0.5.0+. Removes all of this extension's decorations. |
| `window/log` | `{text}` | notification; ignored for now. |

## Running a program in a terminal

`command` is *typed* into the user's shell, which may be PowerShell, Command Prompt or Git Bash, each
quoting differently: `"C:\Tools\my tool.exe"` runs in Command Prompt but only prints the text in
PowerShell. To start a command-line tool reliably, run it directly instead:

```json
{ "name": "My Tool", "view": "my-tool.view", "program": "C:\Tools\my tool.exe", "args": ["--port", "4321", "two words"] }
```

- `program` is a path, or a name found on `PATH` the way Windows finds programs (`.exe` may be left out).
  A path that doesn't exist is an error.
- `args` reach the program exactly as given (an array of strings); Coder does the quoting. `command` is
  ignored.
- The terminal ends when the program does. Pressing Enter in it afterwards runs the view's `command` again
  (for a view) or the same program again (in the terminal panel).
- To run shell syntax (pipes, `;`, `&&`), make the shell the program: on Windows,
  `{"program": "powershell.exe", "args": ["-NoProfile", "-Command", "first | second; third"]}`.

## Asking the user

`window/showMessageRequest` asks a question in a small dialog with up to 4 buttons, like LSP's request of
the same name. Use it for decisions only the user can make ("Install the CLI now?"), not for status:
that's `window/showMessage`.

```json
{
  "message": "My Tool needs its CLI. Install it now?",
  "detail": "This downloads and runs the official installer (about 30 MB).",
  "actions": [{ "title": "Install" }, { "title": "Not now", "id": "later" }]
}
```

- The first action is the default button (Enter). Escape dismisses the dialog: the result is `null`.
- The result is the action object you sent, so extra fields (like `id`) come back with it.
- Without `actions` the dialog shows an OK button, and the result is `null`.
- Coder adds "From <your extension's name>." under the detail, so users always know who is asking.
- The request **waits for the user**. Cancel it (`$/cancelRequest`) to withdraw the question.

## Supporting older versions of Coder

`initialize` gives `coderVersion`. A Coder older than a method answers it with an error (unknown
method), and ignores parameters it doesn't know: before 0.5.0, `program`/`args` are ignored and only
`command` is typed. Send both when you can, or check `coderVersion` first.

## Coding agents (Agent Client Protocol)

An extension can bring a coding agent to Coder Agent, the chat in the secondary side bar. The agent is a
program speaking the [Agent Client Protocol](https://agentclientprotocol.com) (ACP), the open protocol
between editors and coding agents that Zed, JetBrains and Neovim use. An agent that already speaks ACP works
in Coder unchanged.

```json
"coder": {
  "program": { "win-x64": "bin/my-agent.exe" },
  "agents": [{ "id": "my-agent", "name": "My Agent", "args": ["--acp"] }]
}
```

- Each agent runs the extension's `coder.program` with `args`, or its own `program` (a map by platform, like
  `coder.program`), in the extension's folder. It's a separate process from the extension's program.
- The agent shows in Coder Agent's model picker as "My Agent · Agent · <extension name>". Picking it sends the
  chat to it. The first message starts it, after the same consent as the extension's program.
- Coder speaks ACP version 1 on the agent's stdin and stdout, **one JSON-RPC message per line** (not
  `Content-Length` framed like the Editor API). Write logs to stderr; Coder shows its last lines if the agent
  fails to start.
- What Coder sends: `initialize` (with `clientInfo` `coder`, and no `fs` or `terminal` capabilities yet: agents
  read and write files and run commands themselves), `session/new` (`cwd` is the open folder, or the user's home),
  `session/prompt` (text, plus images when the agent's `promptCapabilities.image` is true) and `session/cancel`
  when the user presses Stop.
- What Coder shows: `agent_message_chunk` and `agent_thought_chunk` as the reply and its reasoning, `tool_call`
  and `tool_call_update` as tool cards (status, text content; a `diff` shows as the file change), and `plan` as a
  checklist. Other updates are ignored.
- `session/request_permission` shows the agent's options as buttons on the tool's card (an `allow_*` option is the
  default button), with the tool's `diff`, if any, as a preview with "Open Diff". Stop answers `cancelled`.
- New Chat starts a new session with the same process. The process ends when the extension is uninstalled or
  Coder closes.
- An error whose code is `-32000` (ACP's `auth_required`), or whose message says to log in, is shown as "needs you to
  sign in".

## Panels: native UI for extensions

Coder 0.5.0+. An extension can show its own interface — forms, tables, trees, code — without a web view: its
program describes the content as data and Coder draws it with its own controls, in the user's theme.

```json
"coder": {
  "panels": [{ "id": "my.request", "title": "Request" }],
  "views": [{ "id": "my.collections", "title": "Collections", "type": "panel", "command": "my.showCollections" }]
}
```

- `coder.panels` open as **editor tabs**, one per `key` (a request's id, say).
- A `coder.views` entry with `"type": "panel"` is a **side-bar tab** showing a panel instead of a terminal. Showing
  it the first time runs its `command`: answer with `panel/show`.

| Method | Params | Result |
|---|---|---|
| `panel/show` | `{panel, key?, title?, content, reveal?}` | `{}`. Opens the panel (a new tab is shown) or replaces its content. `reveal: true` also brings an existing one to the front. |
| `panel/close` | `{panel, key?}` | `{closed}` |

| Notification (Coder → program) | Params |
|---|---|
| `panel/event` | `{panel, key, id, type, value}`: what the user did with the node named `id`. |
| `panel/closed` | `{panel, key}`: the user closed the tab. |

**Content** is a tree of nodes, each `{ "type": …, … }`. Send the whole tree again whenever your state changes:
Coder keeps the controls of nodes with the same `id`, so focus, the caret, text being typed, a split's position and
a tree's open folders are kept. Interactive nodes need an `id`. Any node may have `"grow": true` (take the space
left in its row or column).

| Node | Fields | Events |
|---|---|---|
| `column`, `row` | `children`, `gap` (8), `padding`, `align` (`start`/`center`/`end`/`stretch`), `scroll` (column) | |
| `split` | `orientation` (`horizontal`/`vertical`), `ratio` (0.5), `first`, `second` | the user drags it (kept by `id`) |
| `text` | `text`, `style` (`title`, `heading`, `body`, `muted`, `code`, `success`, `warning`, `error`), `selectable` | |
| `badge` | `text`, `color` (`green`, `blue`, `orange`, `red`, `purple`, `gray`) | |
| `button` | `label`, `style` (`primary`, `secondary`, `ghost`), `disabled` | `click` |
| `input` | `value`, `placeholder`, `monospace` | `change` (text, after a pause in typing), `submit` (Enter) |
| `select` | `value`, `options: [{value, label?, color?}]` | `change` (value) |
| `checkbox` | `checked`, `label` | `change` (bool) |
| `tabs` | `selected`, `items: [{id, label, badge?, content}]` | `select` (tab id) |
| `table` | `columns: [{id, label, kind (text/checkbox), editable (true), width?}]`, `rows: [{id, cells: {column: value}}]`, `canAdd`, `canRemove` | `change` `{row, column, value}`, `add` `{column, value}` (typing in the empty last row; send the row back and typing continues in it), `remove` `{row}` |
| `tree` | `items: [{id, label, detail?, badge?, expanded?, children?}]`, `selected` | `select` `{item}`, `activate` `{item}` (double-click) |
| `code` | `value`, `language` (`json`, `xml`, `html`, …), `readOnly`, `minHeight` | `change` (text) unless read-only |
| `divider`, `spacer` | | |

While the user types in an `input` (or table cell, or `code`), your next content doesn't overwrite what they're
typing unless its `value` differs from the one you sent before. Limits: 20,000 nodes, nesting 32 deep.
