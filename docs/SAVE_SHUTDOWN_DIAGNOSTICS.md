# Saving and shutdown: open investigation

The stable installer is withheld while this issue is investigated. Save the ArcGIS project and
Rhino document separately before ending a working session. A successful save does not establish
that host shutdown succeeded.

## Confirmed evidence

- Environment: Windows 11 x64, ArcGIS Pro 3.7.1.1904, Rhino 8.34.26223.11001.
- Windows Application Error 1000 repeatedly named `VRayForRhino.rhp_unloaded` 6.10.0.0,
  exception `0xc0000005`, fault offset `0x25397b`.
- A controlled run completed three Rhino saves, then Pro exited with access-violation exit code
  `-1073741819` during normal shutdown. The save requests themselves succeeded.
- V-Ray was loaded from its Rhino 7 installation into the embedded Rhino 8 process. This is the
  observed configuration, not proof that every V-Ray version has the same problem.
- A separate embedded settings scheme did not prevent V-Ray from autoloading.
- Two focused safe-mode hosts excluded V-Ray and exited with code 0. Both saved a new document
  and three modified versions; the second reopened the saved file and retained all four objects.
  These are save/lifecycle probes, not full E2E matrices. Safe mode suppresses other plug-ins and
  can limit display/features; normal-mode validation remains necessary.
- Normal-mode probes with V-Ray loaded also stalled on document creation/reopening. Those owned
  disposable hosts required termination and count as failures, not clean exits.

The save/safe-mode probes used source `c98dadf` and candidate installer SHA-256
`cbed8fc74b440101a2bb7c52b98ba9360f5a3d2b0d31f4d48c3ff1e0f346cb68`.
All four add-in DLL hashes in the active Esri assembly cache matched that package. Normal-mode
exact-package acceptance, the full sync matrices, companion coexistence and UI checks remain open.

Raw Windows dumps, user settings, project paths and machine-specific logs remain private.

## Changes under test

A startup compatibility check refuses to construct embedded Rhino when the exact V-Ray
6.10.0.0 build is registered to autoload and is not load-protected. It explains the conflict
before the native plug-in enters Pro's process. The check reads registration/protection only;
it does not change standalone Rhino settings. Updating V-Ray or explicitly disabling that plug-in
in Rhino's Plug-in Manager is a user action; compatibility still requires normal-mode retesting.
The explicit diagnostic safe-mode option bypasses this check because Rhino suppresses plug-ins.

The live guard probe used source `a95fb81` and installer SHA-256
`fe9b4de831eba4bb48b298e2bfaf50246d182c19c43bf98e066fad39fcf1b589`.
Launch returned the expected compatibility error, neither V-Ray nor RhinoCore entered the process,
and Pro closed normally with exit code 0. This verifies the refusal, not normal embedded operation.

Both first saves and subsequent saves use Rhino's `WriteFile` API with `UpdateDocumentPath`,
`SuppressDialogBoxes` and `SuppressAllInput`, without a viewport thumbnail. Rhino manages file
locking and backup/temp files. The code verifies that the output exists and that the active document
adopted the saved path. It no longer deletes the destination or drives an interactive Save As command.

`Close-Pro` now distinguishes a normal exit from a crash by retaining the process handle before
closing, reading its exit code and querying matching Windows crash events. E2E lifecycle assertions
require `Clean`, not merely `Exited`.

## Diagnostic isolation

For a disposable test project only, a PowerShell-launched host can opt into Rhino's documented
safe mode. This changes the child process environment and does not disable V-Ray globally:

```powershell
$prior = $env:RHINOINSIDE_ARCGIS_SAFE_MODE
try {
    $env:RHINOINSIDE_ARCGIS_SAFE_MODE = '1'
    Start-Process "$env:ProgramFiles\ArcGIS\Pro\bin\ArcGISPro.exe" -ArgumentList '"C:\Tests\Disposable.aprx"'
} finally {
    $env:RHINOINSIDE_ARCGIS_SAFE_MODE = $prior
}
```

Launch Rhino from the Rhino.Inside ribbon in that process. This option is diagnostic, is off by
default, and may limit Grasshopper, rendering and third-party plug-ins. It does not qualify a release.
Do not change global plug-in protection or delete settings as part of agent installation.

For a report, include application/plugin versions, whether the crash follows Save or Close, and
the Windows crash event's fault module and exception code. Share only synthetic projects and
sanitized event fields. Native dump analysis and normal-mode retesting remain open.

API references: [Rhino command-line switches](https://developer.rhino3d.com/guides/cpp/running-rhino-from-command-line/)
and [RhinoDoc.WriteFile](https://developer.rhino3d.com/api/rhinocommon/rhino.rhinodoc/writefile).
