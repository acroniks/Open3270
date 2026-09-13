# CorpusClusterSpike

Phase 01, work item 6. Replays a directory of recordings, reduces every screen to its field
geometry signature, clusters on that signature, and reports whether the two hypotheses the whole
identification design rests on actually hold.

```
dotnet run --project src/CorpusClusterSpike -- <corpus-dir> [--out <report-dir>]
```

`<corpus-dir>` is a directory of `.log` recordings, each optionally with its `.json` sidecar
beside it. Nothing is written back into it.

## What it answers

**H1 - stability.** The same screen with different data should produce the same signature.
Falsified if one logical screen yields several signatures. The likeliest cause is a
variable-height detail area: a repeating region with a row per transaction adds and removes
fields, so the geometry changes with the data.

**H2 - discrimination.** Screens a human calls different should not collide. Falsified if
visibly different screens share a signature - screens that differ only in protected label text
over an identical field skeleton would do it.

Read the counts. With `S` screens captured and `C` distinct signatures: `C` near `S` means
geometry is over-splitting and H1 failed; `C` far below the number of screens a person walking
the system would name means it is colliding and H2 failed. The healthy result sits close to that
number.

## Anchor candidates

Once screens cluster, anchors fall out of a set operation: for each cluster, the protected text
present in **every** member and in **no** member of any other cluster. That is, by definition,
text that identifies the screen and survives a change of data. The report ranks candidates by how
far they sit from anything data-shaped, so phase 02 starts from a shortlist per screen rather
than a blank page.

Candidates on the nav field are excluded - revision 3 established that no node may anchor there.

## How it replays

Each recording is copied to a temporary **H-only derivative**, with the client lines stripped.
Two reasons. A recording carrying client data stalls on replay unless the automation driving it
sends matching bytes, and the clustering only cares about what the host drew. The derivative also
contains no keyed input at all, which makes it far less sensitive than the original.

Screens are captured through `TNEmulator.ScreenArrived`, not by polling `CurrentScreenXML` - a
replay produces screens faster than a poller can read them, and a poller silently misses some.

Unformatted screens have no fields, so their signature is empty and they would otherwise all
collide into one meaningless cluster. They are counted and segregated before clustering.

Not in `Open3270.sln`, matching the other harnesses here.
