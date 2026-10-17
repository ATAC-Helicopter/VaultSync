# Read-only disk worker development contract

Owner: VS-1904 / [#492](https://github.com/ATAC-Helicopter/VaultSync/issues/492).
Preparation: #736, 1.9.0. Status: **development transport**, not an approved
privileged protocol or a supported recovery workflow. Review with
[ADR 001](adr/001-imaging-engine-1.9.md), the image and identity contracts.

`VaultSync.DiskWorker` is a separate executable in the solution. Its sole operation
calls the fixed `LinuxBlockInventoryService` sysfs/proc observer. It accepts no
arguments, filenames, image metadata, executable names, shell commands, device
selectors or credentials. It opens no raw device and performs no capture/restore.
On other operating systems the body reports UnsupportedPlatform.

The executable is not connected to desktop/CLI launchers, updater assets or release
packaging. A local developer can build and test it; it must not be shipped as an
elevated helper. It has the launching user's privileges, with no elevation and no
OS sandbox. Separate process execution alone does not establish containment.

## One-shot protocol version 1

The parent creates private inherited binary standard-input/output pipes and must
close its input writer after exactly one request. There is no listening socket,
named public endpoint, interactive command parser or subsequent operation.
Integers are unsigned little-endian; UUIDs are 16 bytes in RFC/network order.

| Request offset | Length | Value |
| --- | --- | --- |
| 0 | 8 | ASCII `VSINVREQ` |
| 8 | 4 | Version 1 |
| 12 | 4 | Operation 1: inventory |
| 16 | 16 | Nonzero operation UUID |

EOF after byte 32 is mandatory. Truncation, any trailing byte (including another
request), empty UUID, unknown version or operation refuses before observation.
At most 33 input bytes are consumed. The host applies a 30-second deadline to
the request, observation and output, starting after runtime initialization.
Cancellation is cooperative inside the service; the executable also bounds its
wait when a standard pipe read does not honor cancellation. Runtime startup and
OS scheduling are outside that application deadline.

| Response offset | Length | Value |
| --- | --- | --- |
| 0 | 8 | ASCII `VSINVRSP` |
| 8 | 4 | Version 1 |
| 12 | 4 | Operation 1 |
| 16 | 16 | Exact request operation UUID |
| 32 | 4 | UTF-8 JSON body byte count, at most 1,048,576 |
| 36 | declared count | `LinuxBlockInventory` JSON, numeric enum values |

The full bounded body is prepared before publishing the header. The provider
independently limits observations to 256 devices and bounded metadata; JSON depth
is limited to eight. Strings are technical metadata, never display-ready UI text.
Raw hardware/boot/namespace identifiers belong only on the private channel and
must be redacted from public reports. Standard error contains no protocol data
or exception/path diagnostics. No state is persisted by this worker.

| Exit | Meaning |
| --- | --- |
| 0 | Complete transport response; inspect inventory State/Reason separately |
| 64 | Arguments or request refused |
| 65 | Response encoding/bounds failure |
| 70 | Unexpected internal failure, without private diagnostic text |
| 74 | Input/output failure |
| 77 | Access denied outside the observer's typed result |
| 124 | Deadline/cancellation |

Exit zero does not mean Observed, offline, verified or authorized. Incomplete,
ChangedDuringRead and UnsupportedPlatform remain typed inventory results. A
consumer must bound reads and require a complete matching version/operation/UUID,
valid declared body, EOF and process exit zero; partial output or any failure
cannot become a usable result. There is no product consumer yet.

## Remaining security and integration work

Operation correlation is not authentication, freshness proof or replay prevention.
Before privilege or writes, review trusted worker selection/build provenance,
authenticated channel/peer ownership, cancellation on disconnect, hostile-image
inspection separation, global use/exclusivity, fresh handle identity, repository
ancestry and exact overwrite authorization. Ordinary files and these observations
cannot stand in for that boundary. No elevated generic file-open endpoint exists.
The worker never converts Unknown use to Offline or grants restore permission.

There is no destructive state, checkpoint/resume or interrupted-write record in
this increment. Those need the reviewed VS-1917/1919 contracts and actual durable
capture/restore behavior. Qualification still needs independent recovery media
and the complete five-stage measured loop. Current validation and its limits:
[worker evidence](release-evidence/1.9-disk-worker-2026-10-17.md).
