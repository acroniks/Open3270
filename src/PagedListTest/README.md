# PagedListTest

Offline checks for paging through a list with PF8 and scraping each page. Needs no mainframe.

```
dotnet run --project src/PagedListTest
```

Deliberately not in `Open3270.sln`, matching the other harnesses here. Exit code is non-zero if any
check fails.

## What it checks

Page 1 of a list has three entries and page 2 has one. Two separate causes made a scrape read page
2 with page 1's last two entries still under its first:

1. **Non-display rows render blank.** A host can blank unused rows by re-sending only their field
   attribute as non-display, leaving the old characters in the buffer - CICS does this. A terminal
   draws them blank, so the rendered screen (`GetText`, `GetRow`, `LookForTextStrings`) must too,
   on both the direct and legacy build paths. `Fields` still carries the raw text with
   `FieldType` `Hidden`, and `ConnectionConfig.RevealNonDisplayFields = true` restores the old
   rendering. Replayed through `ConnectionConfig.LogFile`.

2. **`WaitForHostSettle` waits for the last write.** A host may answer one AID with several writes,
   and `SendKey` returns at the first. This needs real gaps between writes, which a log replay
   cannot produce, so it runs against a small host on a loopback socket.

Run this after touching `XMLScreen` rendering, `Controller.BuildXMLScreen`, or the wait methods.
