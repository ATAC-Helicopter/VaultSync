# Disk recovery UI — VS-1907 / #495

The Recovery page now includes an image-selection and read-only verification
panel. This is the first connected UI increment, not a complete restore wizard.
Local Release checks passed with zero warnings/errors, 2,183 Core tests,
293 Python tests and 1,959 keys in 19 locales. See the
[source-bound receipt](release-evidence/1.9-recovery-image-ui-2026-10-26.json).
The earlier QA launches failed before rendering. BUG-19040 subsequently traced
the native crash to a missing sandbox Launch Services permission; corrected
packages now open and render this page, with actual screenshots retained
privately. See [the native QA verification](MACOS_QA_VALIDATION.md). This does
not provide a screenshot or qualification of the complete restoration flow.

1. Choose the directory containing `image.metadata` and `payload.raw`.
2. On Linux x64, select **Verify image**. The desktop opens both files read-only
   and sends bounded byte ranges to the fixed authenticated isolated reader.
3. Wait for content/layout verification or cancel it. A successful receipt shows
   verified byte count and logical sector size. It does not create a staged image,
   authenticate a target, prove bootability or authorize disk writes.

macOS and Windows can display and retain the selection, but explicitly require
the Linux x64 recovery environment for verification. They do not fall back to
parsing image structures inside the desktop process. Missing/untrusted reader,
invalid image, cancellation and failures have translated outcomes; raw exception
text and parser diagnostics are not displayed. All 19 maintained locales contain
the new strings. Language changes preserve image selection and cached status;
the selected language controls numeric formatting.

The panel does not enumerate or open destination disks, request elevation or
start the internal write engine. Navigating away cancels an active read-only
inspection; reader shutdown remains bounded by the existing worker client.
The source files remain caller-owned, and neither they nor disks are changed.

Remaining acceptance: integrate trusted offline writable worker authorization,
fresh destination review and actual human confirmation, staging, journal and
write/read-back progress, recovery media and measured platform/profile loops.
The visible four-step outline describes the intended workflow; the panel states
that only verification is connected in this development build. Keep #495 open
and #736 draft. OS build checks do not qualify boot or restoration support.

The [operation transport](RECOVERY_OPERATION_PROTOCOL_1.9.md) now provides
peer-bound framing and source-descriptor handoff for that integration. It is
not yet attached to this panel. A review acknowledgment is not human or device
authority and cannot, by itself, start restoration.

Real application screenshots must come from an isolated synthetic-data profile
and describe the observed build and outcome. A blank capture, rendering-only
test, replay or earlier native backend result cannot stand in for a successful
interactive restore. Editorial screenshots and manuscripts stay outside GitHub.

The internal worker coordinator now joins channel events to actual staging,
review and owned execution. It requires both wire acknowledgment and a separate
trusted in-process supervisor action; it is still not attached to this panel.
Its synthetic tests do not qualify human confirmation, installed privileged
launch, retained-storage lifecycle or bootable recovery media. See the
[orchestration contract](RECOVERY_OPERATION_PROTOCOL_1.9.md#owned-worker-orchestration--2026-10-28).
