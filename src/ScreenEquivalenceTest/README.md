# ScreenEquivalenceTest

A standalone harness that checks the direct screen build (`Controller.BuildXMLScreen`) against the
original XML round trip (`DumpXML` + `XMLScreen.LoadFromString`), and times the `WaitForText` /
`WaitForTextOnScreen` loops.

It needs **no mainframe**. It synthesises a TN3270 data stream, writes it in the log format that
`ConnectionConfig.LogFile` replays, and drives a real `TNEmulator` against it.

```
dotnet run --project src/ScreenEquivalenceTest
```

Exit code 0 means every screen matched. It is deliberately **not** in `Open3270.sln` - add it if you
want it in CI, delete the directory if you don't.

## What it currently reports

- **plain screen: IDENTICAL** - CX/CY, the rendered screen, the unformatted rows and every field
  (position, length, base attribute, protection, field type, colours, text) match between the two
  paths, including a field that wraps across a row boundary and a field carrying extended colour
  attributes.
- **screen with `<` `&` `>`: one deliberate difference** - the XML path renders `A<B&C>D` as
  `A&lt;B&C>D`, because `Actions.encodeXML` escapes `<` to `&lt;` and then escapes that string's
  `&` again. The extra characters shift the rest of the row. The direct path does not escape
  anything and renders the row correctly. This is a fix, not a regression.

Note: the log replay thread spins on "Disconnected from log file" once it reaches end of file. That
is a pre-existing bug in `Telnet.LogFileProcessorThreadHandler` (the `text == null` branch never sets
`logFileProcessorThread_Quit`); it only produces console noise here.
