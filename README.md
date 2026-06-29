# HealLink Quest 3 Client

Unity client for the Quest 3 side of the HealLink mixed-reality setup. The app shows the HoloLens PV camera and AHAT depth view on a billboard in Quest space, lets the Quest user point/draw against that depth billboard, and sends hand, stroke, and optional microphone data back to the HoloLens.

## Project Setup

- Unity: `6000.3.14f1`
- Target platform: Android / Quest 3
- Main scene: `Assets/Scenes/HealLinkQuest3.unity`
- Build menu: `Build > Build Quest 3 APK`

Generated Unity folders such as `Library`, `Temp`, `Logs`, build outputs, IDE files, diagnostics, and capture dumps are ignored and should not be committed.

## Runtime Architecture

The Quest app has three main runtime areas:

1. HoloLens image intake

`run_once` stores the HoloLens IP address and initializes HL2SS if the server is reachable. `rgbd_align_ahat` then opens the HL2SS PV and AHAT streams, displays the PV image on `Quad_PV`, creates the depth/debug view on `rm_depth_ahat_z`, and exposes the latest aligned depth frame for sampling.

2. Quest interaction layer

`BillboardFollow`, `FingerPosition`, and `DepthDistance` keep the image billboard usable from the Quest point of view. `DepthDistance` samples the latest depth frame so a Quest fingertip or pinch ray can be projected onto the measured HoloLens depth instead of floating at an arbitrary hand distance.

3. Quest-to-HoloLens sender

`QuestHandDataSender` reads Quest hand tracking, left-hand pinch strokes, and optional microphone audio. It sends this data to the HoloLens over UDP, using camera-relative coordinates by default so the HoloLens receiver can reconstruct positions relative to its own reference camera.

## Network Setup

1. Start the HL2SS/HoloLens server.
2. Put Quest 3 and HoloLens on the same network.
3. Find the HoloLens IPv4 address.
4. Open `Assets/Scenes/HealLinkQuest3.unity`.
5. Select the `HL2SS Scripts` object.
6. Enter the HoloLens IP in `run_once > Host`.

The same host is reused by `QuestHandDataSender` when `useRunOnceHost` is enabled. If needed, disable `useRunOnceHost` and set `targetHost` directly on `QuestHandDataSender`.

Default ports:

- HL2SS PV health check: `3810`
- Quest hand/stroke UDP output: `5055`
- Custom audio receiver: `5066`
- WebRTC signaling: `5077` local / `5076` remote

## Test Images

The scene keeps two fallback/reference textures in `Assets/TestImages`:

- `Quad1_20260609_181907.png` for `Quad_PV`
- `Quad2_20260609_181907.png` for `rm_depth_ahat_z`

`SaveQuadImages` is enabled in the scene and overwrites these two files every 20 seconds while Play Mode is running in the Unity Editor. That means the next successful editor run with the HoloLens server online refreshes the test images in place while keeping the existing Unity material GUIDs stable. Player builds keep this disabled by default because an installed Quest APK cannot write back into the Unity project folder.

## Build

Use the Unity menu item:

```text
Build > Build Quest 3 APK
```

The editor helper builds an Android ARM64 IL2CPP APK to:

```text
Builds/Quest3/MedXRQuest3.apk
```

For manual builds, make sure the active build target is Android, architecture is ARM64, and the enabled scene is `Assets/Scenes/HealLinkQuest3.unity`.

## Important Scripts

- `Assets/hl2ss_Scripts/test/run_once.cs`: HoloLens host configuration and HL2SS initialization.
- `Assets/Scripts/OwnHL2SS/rgbd_align_ahat.cs`: PV/AHAT intake, depth processing, and depth-frame publishing.
- `Assets/Scripts/QuestHandDataSender.cs`: Quest hand, stroke, and audio sender.
- `Assets/Scripts/DepthDistance.cs`: Depth sampling for fingertip and pinch projection.
- `Assets/Scripts/FingerPosition.cs`: Quest fingertip marker on the billboard.
- `Assets/Scripts/SaveQuadImages.cs`: Refreshes the two fallback test images from the live quad textures.

## Cleanup Notes

PointCloud experiments, recovery scenes, old unused helper scripts, local diagnostics, and generated build/cache data were removed from the project. Some Unity cache files may remain locally while Unity is open because the editor locks them; they are ignored and can be deleted after closing Unity.
