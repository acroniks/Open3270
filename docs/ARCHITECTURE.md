# Architecture

How this library is put together, how to build it, and the things that
surprise people. Companion to the README.

## What this is

Open3270 is a **C# library that implements the TN3270 / TN3270E terminal protocol**, letting .NET
applications talk to IBM mainframe (3270) sessions programmatically — the classic use case being
screen-scraping and automating green-screen applications without a human at a terminal emulator.

The library is a managed port/derivative of the x3270 emulator (hence the `TN3270E/X3270`
directory), packaged behind a much simpler high-level API. It ships on NuGet as `Open3270`
(assembly name `Open3270`, root namespace `Open3270`, version 1.6.0.0). MIT licensed,
copyright 2004-2020 Michael Warriner.

## Repository layout

```
src/Open3270.sln          Visual Studio solution (all projects below)
src/Open3270Library/      THE library — everything that matters lives here
src/SampleConsole/        Console demo: connect, dump screen, interactive key/text commands
src/SampleScreenscraping/ Console demo: scripted login + scrape against MUSIC/SP on Hercules
src/SampleForm/           WinForms terminal (RichTextBox-based screen render)
src/SampleWPF/            WPF terminal demo ("TerminalDemo")
src/Test3rdPartyServers/  Ad-hoc manual test harness against real public 3270 hosts (not unit tests)
src/ScreenEquivalenceTest/ Offline check that the two screen-build paths agree, plus wait-loop
                          timings. Replays a synthetic TN3270 stream, so it needs no mainframe.
                          Deliberately NOT in Open3270.sln.
documentation/            IBM 3270 Data Stream Programmer's Reference PDFs + OneNote notes
old_subversion_branches/  Frozen copies of pre-Git forks (chris_hulbert, clyde_coulter,
                          codeplex_forums) under the old "WinFX3270" name. Historical only —
                          never edit, and don't let greps here confuse you.
```

## Architecture of Open3270Library

Layered, outermost first:

1. **`Engine/TNEmulator.cs`** — the public API and the class consumers actually use.
   `Connect()` / `SendKey()` / `SetText()` / `GetText()` / `WaitForText*()` / `CurrentScreenXML` /
   `Close()`. Owns screen caching, the wait-for-screen semaphore, and `IDisposable` teardown.
   `Engine/ConnectionConfig.cs` holds all tunables (`HostName`, `HostPort`, `HostLU`, `TermType`,
   `UseSSL`, `FastScreenMode`, `DefaultTimeout`, `ThrowExceptionOnLockedScreen`, …).

2. **`TN3270E/TN3270API.cs`** — thin session facade over `Telnet`. Connect/disconnect, cursor
   moves, field attributes, `ExecuteAction()`, and the `Disconnected` / `CursorLocationChanged` /
   `RunScriptRequested` events that `TNEmulator` re-raises.

3. **`TN3270E/X3270/`** — the x3270-derived emulator core. All `internal`.
   - `Telnet.cs` — socket + Telnet option negotiation, TN3270E subnegotiation, SSL, async I/O,
     and the service locator holding the objects below.
   - `Controller.cs` — the 3270 screen buffer: fields, attributes, the data-stream write commands,
     and `DumpXMLAction` which serializes the live screen to XML.
   - `Keyboard.cs` — every keyboard action (AID keys, PF/PA, cursor motion, field editing) and the
     keyboard-lock state machine. Largest file in the repo.
   - `Actions.cs` — string-name → action-delegate table (`"enter"`, `"pf"`, `"string"`,
     `"fieldset"`, `"dumpxml"`, …) plus the `CausesSubmit` flag that tells the upper layers whether
     an action requires waiting for a host response. This registry is how `TNEmulator` reaches the
     core — commands are dispatched **by string name**, so renaming an action silently breaks
     callers.
   - `Ansi.cs`, `SF.cs`, `AID.cs`, `See.cs`, `Tables.cs` (EBCDIC/ASCII), `Print.cs`, `Idle.cs`,
     `TNTrace.cs`, `Appres.cs` — supporting pieces of the emulation.

4. **`CommFramework/`** — low-level plumbing: `ClientSocket`/`ServerSocket`, `MySemaphore`
   (custom counting semaphore used for screen-arrival signalling), `ByteHandler`, `Message`, `Audit`.

5. **`Interfaces/` + `Engine/TnXMLScreen.cs`** — the screen model exposed to callers.
   `IXMLScreen` (`GetText`, `GetRow`, `Dump`, `Fields`, `LookForTextStrings`) is implemented by
   `XMLScreen`, which is deserialized from the XML that `Controller.DumpXMLAction` produces.
   `IAudit` / `StringAudit` are the logging/tracing hook — set `TNEmulator.Audit` and
   `TNEmulator.Debug = true` to get a trace of everything.

6. **`Exceptions/`** — `TNHostException` (connection lost / not connected),
   `TNIdentificationException`, `TNRouteException`.

7. **`Server/` and `LogParser/`** — a partial TN3270 *server*-side emulation and parsers for
   captured client/host logs. Compiled into the assembly but **not referenced by any sample, test,
   or by the emulator path**. Treat as dormant code.

## Data flow, in one line

`TNEmulator` → `TN3270API` → `Telnet` (sockets + negotiation) → `Controller` (screen buffer) →
`DumpXMLAction` emits XML → `XMLScreen` → `IXMLScreen` back to the caller.

## Building

All projects are SDK-style and target **.NET 8**:

| Project | TFM | Notes |
| --- | --- | --- |
| `Open3270Library` | `net8.0` | No NuGet dependencies |
| `SampleConsole`, `SampleScreenscraping`, `Test3rdPartyServers` | `net8.0` | Cross-platform |
| `SampleForm` (WinForms), `SampleWPF` (WPF) | `net8.0-windows` | **Windows only** |

```
dotnet build src/Open3270.sln                                    # Windows: builds everything
dotnet build src/Open3270.sln -p:EnableWindowsTargeting=true     # macOS/Linux: builds everything
dotnet build src/Open3270Library                                 # library alone, anywhere
```

- On macOS/Linux a plain solution build fails with `NETSDK1100` on the two `net8.0-windows` GUI
  samples. `-p:EnableWindowsTargeting=true` pulls the Windows targeting packs and compiles them
  fine — they just can't be *run* off Windows. Use it rather than skipping those projects.
- The whole solution currently builds with 0 errors and 2 pre-existing warnings: `CS0618` in
  `TNEmulator.cs:543` (the library calling its own deprecated `GetLastError`) and `CS0162`
  unreachable code in `SampleForm/Emulator.cs:51`. Neither is migration damage.
- No unit test suite exists. `Test3rdPartyServers` is a manual harness that connects to live
  external hosts; the samples expect a local Hercules/MUSIC/SP mainframe on `localhost:3270`.
- `src/ScreenEquivalenceTest` is the one thing here that tests without a host:
  `dotnet run --project src/ScreenEquivalenceTest`. It synthesises a TN3270 data stream, writes
  it in the format `ConnectionConfig.LogFile` replays, and drives a real `TNEmulator` against it.
  Run it after touching `Controller`'s dump/build code or `XMLScreen`'s rendering. It is not in
  the solution, so a plain solution build ignores it.

## Conventions

- `.editorconfig` in `src/Open3270Library`: **tabs** for C#, CRLF line endings, final newline.
  `.gitattributes` enforces the line endings — don't fight it.
- Every library file starts with the MIT license header block wrapped in `#region License`.
  Keep it when adding files.
- Assembly identity, version and copyright live in the `.csproj` files, not in `AssemblyInfo.cs`.
  The only attributes left in `AssemblyInfo.cs` are ones with no MSBuild equivalent
  (`CLSCompliant` in the library, `ThemeInfo` in SampleWPF) — adding an attribute the SDK also
  generates will fail the build with `CS0579`.
- Members are grouped with `#region` blocks (`Fields`, `Properties`, `Public Methods`, …), matching
  the existing style.
- Old public methods are kept and marked `[Obsolete]` under a `#region Deprecated Methods`
  (`SendKeyFromText`, `SendText`, `PutText`, `GetStringData`, `GetLastError`). This is a published
  NuGet library — preserve backward compatibility rather than deleting.
- The core is deliberately `internal`; only `Open3270.*` engine/interface types are public. Don't
  widen visibility of `X3270` internals without a reason.
- The code is old and carries dated idioms (`ArrayList`/`Hashtable`, `lock(this)`, inline
  attribution comments like `// CFCJR Feb/29/2008`). Match the surrounding style in a file rather
  than modernizing opportunistically.

## Known issues

### TLS certificate validation is disabled (accepted, deferred)

`Telnet.cs` `cryptocallback` unconditionally `return true`, so when `UseSSL` is on, **every**
certificate is accepted — expired, self-signed, wrong hostname, or an attacker's. The connection
is encrypted but not authenticated, leaving it open to an active MITM.

This is known and was deliberately left alone as of the .NET 8 migration. **Do not "fix" it as a
drive-by** — turning validation on is a behavioral break for anyone connecting to a mainframe with
a self-signed cert. Raise it before changing it.

If it is picked up later, the shape agreed on was: default to
`sslPolicyErrors == SslPolicyErrors.None`, add an explicit `ConnectionConfig` opt-out for
self-signed/dev hosts, and log the actual `SslPolicyErrors` to the audit output (today a handshake
failure surfaces as a generic disconnect and is very hard to diagnose).

Related: `AuthenticateAsClient` is passed `this.address`, so connecting by IP rather than DNS name
will fail hostname validation once validation is enabled.

### TLS protocol versions need no work

`AuthenticateAsClient(this.address)` resolves to `SslProtocols.None` on .NET 8, i.e. OS defaults —
TLS 1.2 on Windows 10/Server 2016+, TLS 1.3 on Windows 11/Server 2022+. **Do not pin
`SslProtocols.Tls12`**; it would cap the connection below TLS 1.3 and override enterprise Schannel
policy. `ServicePointManager.SecurityProtocol` is irrelevant here — it never affected `SslStream`.

### `Actions.encodeXML` double-escapes `<` (live in the legacy path only)

`encodeXML` replaces `<` with `&lt;` and *then* replaces `&` with `&amp;`, so a `<` on the screen
leaves as `&amp;lt;`, parses back as the four characters `&lt;`, and shifts the rest of the row.
`XMLScreen.Render` has a `text.Replace("&lt;", "<")` hack that patches it up for unformatted
rows but not for field text.

The direct build does no escaping and is simply correct, so this now only affects
`UseLegacyXmlScreenRendering = true` and the `dumpxml` action's own output. It was left alone
rather than fixed, because the legacy path's whole job is to behave exactly as it always has.
`ScreenEquivalenceTest` asserts the difference so it stays visible.

## Gotchas

- **Timing is the hard part.** `Refresh(waitForValidScreen, timeoutMS)` blocks on `MySemaphore`
  until the host signals a new screen. `Config.FastScreenMode` ignores keyboard-inhibit to go fast
  at the cost of possible data loss on a still-locked screen. Most "flaky" behavior lives here.
- Screen arrival is **edge triggered, not polled**. The socket receive thread runs
  `Controller.ProcessWrite` → `Events.RunScript` → `TN3270API.RunScript` →
  `TNEmulator.currentConnection_RunScriptEvent`, which nulls the screen cache and calls
  `semaphore.Release(1)`; `Refresh` is parked in `Monitor.Wait` on the other side.
  `WaitForText`/`WaitForTextOnScreen` wait on that same signal through the private
  `WaitForScreenUpdate(deadline)` helper. Do not reintroduce a `Thread.Sleep` poll in a wait loop —
  every wakeup that finds nothing new costs a whole screen fetch.
- **Two ways to build a screen.** `TNEmulator.GetScreenAsXML()` normally calls
  `TN3270API.BuildCurrentScreen()` → `Controller.BuildXMLScreen()`, which reads the screen buffer
  straight into an `XMLScreen`. Setting `Config.UseLegacyXmlScreenRendering = true` falls back to
  the old `DumpXML` + `XMLScreen.LoadFromString` round trip. Both are verified equivalent by
  `ScreenEquivalenceTest`; the flag exists purely as an escape hatch.
- The field walk is shared. `Controller.EnumerateFieldSegments()` (built on `AppendFieldSegments`,
  which is the old `DumpFieldAsXML` with the XML emission taken out) feeds **both**
  `DumpXMLAction` and `BuildXMLScreen`. Change field discovery in one place only — that is the
  point of the split. `GetFieldType` is shared the same way.
- Blank rows and blank fields come back as `string.Empty`, not as a run of spaces, from both
  `GetUnformatedStrings()` and `XMLScreenField.Text`. That is an artifact of `XmlSerializer`
  dropping whitespace-only element content, and `ReadDisplayText` reproduces it on purpose so the
  direct build does not change the shape of that data. The rendered screen is padded either way.
- `TNEmulator.CurrentScreenXML` lazily re-fetches the screen; nearly every mutating method nulls the
  cache first via `DisposeOfCurrentScreenXML()`. Preserve that pattern when adding methods.
- `TNEmulator.SetField(FieldInfo, string)` throws `NotImplementedException`, and the `FieldInfo`
  there is `System.Reflection.FieldInfo` (leaked via `using System.Reflection`), not a 3270 type.
- Screen coordinates are zero-based `(x = column, y = row)`.
- The Code Access Security demands that used to sit in `Audit.WriteLine` (`FileIOPermission`) and
  `ClientSocket.Connect` (`SocketPermission`) were deleted during the .NET 8 migration. CAS is not
  a security boundary on .NET 5+ and those types are non-functional shims — don't restore them.

