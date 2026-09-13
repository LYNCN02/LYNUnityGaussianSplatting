# LYNOOK Room Scenes

Open `projects/GaussianExample` as the Unity project. All room scenes live in
`Assets/LYNOOK/Scenes`. Use **Tools > LYNOOK > Room Scenes** to open them.

| Scene | Purpose | Camera setup | Previous name |
| --- | --- | --- | --- |
| `00_RoomPreview_Perspective.unity` | Inspect the room and adjust the original framing. | Ordinary perspective cameras with independent transforms. | `GSTestScene` |
| `01_DualScreen_SeamCalibration.unity` | Check alignment and continuity across the screen seam. | Shared-eye off-axis projection; seam markers and depth references. | `LYNOOK_DualScreen_SeamTest` |
| `02_DualScreen_DepthTest.unity` | Check spatial depth and motion between screens. | Same shared-eye off-axis setup; near/far references and moving objects. | `LYNOOK_DualScreen_3DViewTest` |
| `03_CornerRoom_View45.unity` | Preview and record the room on a convex corner display. | Shared-eye off-axis projection; 90-degree panels, 45-degree viewing direction, 600 mm viewing distance. | `LYNOOK_DualScreen_CornerBox45Test` |

## Naming convention

Use **`NN_Subject_Purpose.unity`**, with a two-digit workflow order and descriptive
English words. Keep all room scenes in this directory. Avoid generic names such
as `TestScene`, unexplained abbreviations, and names that only describe a version.

- `00`: room preview and framing.
- `01`: seam calibration.
- `02`: spatial-depth validation.
- `03`: corner-room presentation.

Scene paths are defined once in `LYNOOKSceneCatalog.cs`. Builders and the calibration
exporter use that catalog. Update it when adding or renaming a scene.

## Opening and recording

Use **Tools > LYNOOK > Record** and select one of the four numbered modes.
The command opens the existing scene, starts recording automatically, then stops
and saves when the configured frame interval is complete (default: 300 frames at
30 fps, or 10 seconds). **Tools > LYNOOK > Record Current Scene** records the open
room scene using the same workflow. No manual Play step is needed.

Pressing **Play** only previews the scene; it never starts a LYNOOK recording.
Unity Recorder runs in Play Mode internally, which the Record command manages.
Recording commands are disabled while Play Mode or another recording is active.

Each take is saved under:

```text
Recordings/LYNOOK/<scene-name>/<yyyyMMdd_HHmmss_fff>/
```

- Scene 00 captures the original perspective camera pair without changing their
  positions, rotations, or projection method. Outputs include `main_perspective`
  and `right_perspective` at 1280 x 800 and 720 x 1280.
- Scenes 01 and 02 capture their existing off-axis camera pair.
- Scene 03 captures the two screen feeds and four observer views.

Existing output basenames and MP4/MOV options are retained inside each new take
folder. Earlier recordings remain available at their original paths.

### World configuration saved with recordings

All four **Record** commands and **Record Current Scene** now save
`world_config.json` beside the panel videos in the take folder. The JSON uses
the LYNOOK runtime `WorldConfig` format: float arrays for transforms, display
0/1, `Screen_Main` / `Screen_Right` bindings, and an enabled `offAxisFrustum`
that reproduces the actual capture projection (including ordinary perspective).
Only the two panel feeds are bound; observer/stitched previews are not world videos.

Cameras are snapshotted after recorder preparation, before recording starts.
The JSON is published after both panel videos have been finalized, including
when Play Mode is stopped early. It references actual filenames in the same
folder: MOV when available, otherwise MP4. No config is published for a missing
or empty panel file. The take folder name is the `worldId`; keep them identical
if renaming/copying the folder into `StreamingAssets/Worlds/`.

This exports configuration only. Supply your downloaded GLB separately at
`mesh/collision.glb`; no mesh is generated, copied, or inferred from the video.
The complete default configuration is editable in
`Assets/LYNOOK/WorldExport/world_config.defaults.json`. It uses World E as the
initial preset: player and avatar spawn, one additional character point, four
activity points (including `seat_main`), navigation parameters, preview path,
scan transition, and reflection sphere. Recording replaces the identity, camera
poses/projections, and video bindings with the current take's values. These are
reused preset positions, not inferred scene measurements; adjust them for each
room. The preview and navigation paths are references only: recording does not
generate those files. Blank path overrides inherit the preset paths.

All current runtime configuration sections are emitted, including disabled
`continuityTestGeometry`. The obsolete `cameraRigSpawn` alias is omitted in favor
of `cameraRig`. `worldTransform` uses the runtime's plain transform shape, without
an unsupported `applyTransform` field.

By default, camera positions use **recording scene coordinates** and
`worldTransform` is identity. This does not establish alignment with a downloaded
GLB. Optionally add one **LYNOOKWorldExportSettings** component to an active scene
object to set asset paths, renderer names, display name, and `worldCoordinateRoot`.
That root represents the supplied GLB's Unity coordinate frame in the recording
scene. Camera positions/rotations and frustum distances are converted into that
frame so the runtime mesh can remain at identity. Only positive uniform scale is
supported; do not assign a mirrored Gaussian transform as the coordinate root.
The `recording.meshAlignmentVerified` metadata remains false until separately
verified. A single configuration describes fixed capture cameras; animated camera
paths require an additional playback format and are not represented here.

Use **Tools > LYNOOK > Room Scenes** to open scenes without recording. Commands
labeled **Rebuild** regenerate scene content or recorder assets; recording uses
existing content and does not rebuild a scene.

Scenes 01 and 02 use the same projection method. Their test content differs.
Scene 03 uses a different display arrangement, with provisional hardware dimensions
and viewing parameters. Its observer cameras do not change the two capture projections.

Scene GUIDs, scene contents, recording filenames, and script entry-point names are
preserved by this organization change. The independent URP/HDRP example projects
retain their own scenes.

For recording outputs, calibration details, and validation limits, see the
[dual-screen recording guide](../DualScreenRecorder/README.md).
