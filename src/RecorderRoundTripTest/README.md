# RecorderRoundTripTest

Offline check that `SessionRecorder` writes what `ConnectionConfig.LogFile` reads back. Needs no
mainframe: it synthesises a TN3270 data stream, records it, and replays the recording through a
real `TNEmulator`.

```
dotnet run --project src/RecorderRoundTripTest
```

Deliberately not in `Open3270.sln`, matching `ScreenEquivalenceTest`. Exit code is non-zero if any
check fails.

## What it checks

1. **Column discipline.** The replay parser reads fixed columns, not tokens: `HHMMSS` in columns
   0-5 (which must parse as an integer), the direction character in column 9, a space in column
   10, and space separated hex from column 18. A prefix one character out makes every line
   misparse, or worse, parse into plausible garbage. This check reads the written file back and
   asserts each column, then decodes the hex and compares it to the bytes that went in.

2. **Round trip through the state machine.** A recorded stream is replayed through
   `ConnectionConfig.LogFile` and the resulting screen is compared against the stream's content.
   This is the check that matters: it proves a recording the collector wrote is readable by the
   engine that has to read it.

3. **The replay branch is not recorded.** Attaching a recorder to a replaying session must not
   capture client bytes - `SendRawOutput` takes its replay branch there, and recording it would
   write the recording back out again. Action tags still fire, because they are emitted above the
   wire in `Actions.Execute`.

Run this after touching the log line format, either tee point in `Telnet.cs`, or `SessionRecorder`.
