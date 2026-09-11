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
