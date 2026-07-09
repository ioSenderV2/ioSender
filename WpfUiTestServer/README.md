# WpfUiTestServer

A flag-gated, **in-process** HTTP server that lets an external script drive a WPF application's UI and read
its state back — so a change can be exercised end to end (and self-verified) instead of always being handed to
a human to click.

It is **app-agnostic**: the engine knows how to address and drive *any* WPF UI by `x:Uid` via
`AutomationPeer`s. Everything app-specific (domain state, message-box routing) enters through small seams the
host supplies. It has **no external dependencies** and adds no reference cycle.

- **Transport:** a raw `TcpListener` bound to `127.0.0.1` (loopback) — *not* `HttpListener`, which would need a
  `netsh http add urlacl` reservation or admin. Loopback TCP needs no privilege, so it works unattended.
- **Security:** listens on loopback only, and only when the host explicitly enables it. A normal run never
  opens a socket. There is no authentication — anything that can reach loopback can drive the app, so enable it
  only in test/dev.
- **Addressing:** WPF surfaces `x:Uid` at runtime as `UIElement.Uid`. The server walks the visual tree of every
  open window and acts through each element's `AutomationPeer` — the same machinery real UI-automation uses, so
  it respects `IsEnabled` and fires the real handlers regardless of window position.

---

## Embedding it in a host app

### 1. Reference the assembly
Add a project/assembly reference to `WpfUiTestServer`. Because it has no dependencies, any project can reference
it without a cycle.

### 2. Start it once the UI is built
Call `Start` after the main window's tabs/views are realized (e.g. at the end of your startup routine), gated
behind a flag/env var so it never runs in a normal session:

```csharp
if (testServerEnabled)
    WpfUiTestServer.UiTestServer.Start(
        mainWindow,                       // the window whose visual tree is addressed
        port,                             // <= 0 uses DefaultPort (8760)
        new MyStatusProvider(mainWindow), // optional IUiTestStatusProvider (null => /status empty)
        msg => MyLog.Write(msg));         // optional log sink (null => no-op)
```

A gently-pulsing "UNDER TEST-SERVER CONTROL" banner is docked across the top of the window while it runs, so an
operator watching the machine knows automated input is live.

### 3. Supply domain state (optional) — `IUiTestStatusProvider`
State a test asserts on but that no single control exposes (connection, mode, progress, …):

```csharp
public IEnumerable<KeyValuePair<string,string>> GetStatus()
{
    yield return new KeyValuePair<string,string>("connected", IsConnected ? "true" : "false");
    yield return new KeyValuePair<string,string>("state", ControllerState.ToString());
    // ...
}
```
Names become the `/status` keys and the `/waitfor?status=` query keys (matched case-insensitively). `GetStatus`
is called on the UI thread, so it may read view-models directly.

### 4. Route message boxes through the dialog seam (optional)
So the harness can answer/read the app's prompts instead of a modal blocking an unattended run, funnel your
`MessageBox.Show` calls through a wrapper that consults `UiTestServer.Prompt` and otherwise shows the real box.
See `CNC.Core.AppDialogs` in ioSender for the reference wrapper. `Prompt` returns `null` when the server is off,
so real users are unaffected.

> **Addressing generated controls.** `x:Uid` is a XAML markup directive; controls created in code have none.
> Set `UIElement.Uid` explicitly on them (e.g. from a registry key) if you want them addressable.

---

## HTTP API

All responses are JSON with an `"ok"` boolean. `{uid}` is a path segment; other params are query string.
Duplicate `x:Uid`s (a reused template) are disambiguated with `?index=N` (0-based, in visual-tree order).

### Discovery & state
| Method & path | Purpose |
|---|---|
| `GET /ping` | Liveness → `{"ok":true,"server":"…","port":8760}` |
| `GET /uids` | Distinct **realized** uids (sorted) with `type` and occurrence `count` — the "what can I address" list |
| `GET /tree` | Every realized element carrying an `x:Uid`, with `index,type,name,enabled,visible,value` |
| `GET /state/{uid}?index=N` | One element's full state |
| `GET /screenshot` | PNG (`image/png`) of the whole window — lets the harness *see* rendered layout |
| `GET /screenshot/{uid}?index=N` | PNG of a single element's bounds |

### Actions
| Method & path | Purpose |
|---|---|
| `POST /invoke/{uid}?index=N` | Invoke (button), else Toggle, else Select (tab/list item) via the automation peer |
| `POST /set/{uid}?value=V&index=N` | Set value: `ValuePattern` text, a checkbox/toggle bool, or a range number |
| `POST /key/{keyName}?uid=T` | Raise a key (real routed events) on target `T` (default = the window). **Plain keys only** — synthesized events can't set `Keyboard.Modifiers`, so Ctrl/Shift/Alt combos won't fire handlers. Use e.g. `F1`, `Escape`, `Up` |
| `POST /menu/{uid}` | Open the element's context menu (fires its `Opened`, so dynamic submenus populate); returns the items (`uid,header,enabled,hasItems`) |
| `POST /menu/{uid}?item=X` | …and invoke the menu item with uid `X` |

### Synchronization
| Method & path | Purpose |
|---|---|
| `GET /idle` | Block until the Dispatcher drains to Background priority (layout/render done). Does **not** know about async I/O |
| `GET /status` | Host/domain state map from the `IUiTestStatusProvider` |
| `GET /waitfor?…` | Block until a condition holds or `timeout` (ms, default 5000) elapses; polls every `poll` ms (default 100). Returns `matched`+`elapsedMs`, or `timeout`+last observed value |

`/waitfor` conditions (pick one):
- `?uid=X&exists=true` — element appears / `false` = goes away
- `?uid=X&enabled=true` · `?uid=X&visible=true`
- `?uid=X&value=Foo`
- `?status=state&equals=Idle` — an `IUiTestStatusProvider` field

### Dialog broker
The app's message boxes (routed through the host's wrapper → `UiTestServer.Prompt`) become answerable by the
harness. These routes run **without** the Dispatcher, so they work even while the UI thread is blocked in a
prompt (answer it on a second connection).

| Method & path | Purpose |
|---|---|
| `POST /dialog/arm?answer=Yes` | Pre-answer the next prompt once (no modal appears) |
| `POST /dialog/arm?standing=Yes` | Answer *every* prompt with this until cleared |
| `POST /dialog/arm?capture=true` | Intercept prompts with no preset — they become *pending*, answered via `/dialog` |
| `POST /dialog/arm?clear=true` | Clear armed queue + standing + capture |
| `GET /dialogs` | `capture`/`armed`/`standing`, the `pending` list (id/title/message/buttons), and a `recent` ring buffer of shown prompts + how each resolved (readback of message-box **output**) |
| `POST /dialog?answer=Yes[&id=X]` | Answer the oldest pending prompt (or the one with `id`) |

When the server is running but nothing is armed and capture is off, `Prompt` returns `null` → the host shows its
real dialog (so interactive use is unaffected). A captured prompt that is never answered resolves to the host's
supplied default after its timeout.

---

## Known limitations
- **Realized elements only.** Content on a not-yet-selected tab is created lazily and won't appear until that
  tab has been shown once (`POST /invoke/{tabUid}` to realize it). The full *static* catalog of every declared
  `x:Uid` lives in the app's XAML/localization source, not at runtime.
- **`/idle` is dispatcher-only.** It does not track async I/O (a connection, a running job). Use `/waitfor` on a
  status field or an element condition for those.
- **No auth, loopback only.** By design; keep it test/dev-gated.

---

## Example: assert an outcome
```bash
curl -s localhost:8760/waitfor?status=connected&equals=true&timeout=8000
curl -s -X POST localhost:8760/dialog/arm?standing=No       # decline any confirm
curl -s -X POST localhost:8760/invoke/btn_resetDefault      # would prompt; intercepted → No, no modal
curl -s localhost:8760/dialogs                               # read back what the box said (recent[0])
```
