# HealLink Quest 3 Client

Unity client for the Quest 3 side of the HealLink mixed-reality setup.

The app receives the HoloLens PV camera image and AHAT depth stream through HL2SS, shows them on a Quest-facing billboard, projects Quest hand interactions onto the measured depth image, and sends hand, stroke, and optional microphone data back to the HoloLens.

## Project Setup

- Unity: `6000.3.14f1`
- Target device: Meta Quest 3
- Target platform: Android
- Main scene: `Assets/Scenes/HealLinkQuest3.unity`

## Runtime Architecture

The Quest app is organized around three runtime layers.

### HoloLens Stream Intake

`run_once` stores the HoloLens IP address, checks whether HL2SS is reachable, and initializes the HL2SS runtime.

`rgbd_align_ahat` opens the HoloLens PV and AHAT streams. It displays the PV image on `Quad_PV`, creates the AHAT depth/debug view, aligns depth into the PV space, and exposes the latest depth frame for interaction scripts.

### Quest Interaction Layer

`BillboardFollow`, `FingerPosition`, and `DepthDistance` make the HoloLens image usable from the Quest side.

`DepthDistance` samples the latest aligned depth frame so a Quest fingertip or pinch ray can be placed at the measured HoloLens depth instead of at an arbitrary hand distance.

### Quest-to-HoloLens Sender

`QuestHandDataSender` reads Quest hand tracking, left-hand pinch strokes, and optional microphone audio. It sends this data to the HoloLens over UDP.

By default, stroke and hand positions are sent in camera-relative coordinates. The HoloLens receiver can then reconstruct the positions relative to its own reference camera.

## Network Setup

1. Start the HL2SS server on the HoloLens.
2. Put the Quest 3 and HoloLens on the same network.
3. Find the HoloLens IPv4 address.
4. Open `Assets/Scenes/HealLinkQuest3.unity`.
5. Select the `HL2SS Scripts` object.
6. Enter the HoloLens IP in `run_once > Host`.

`QuestHandDataSender` reuses the same host when `useRunOnceHost` is enabled. If a different target is needed, disable `useRunOnceHost` and set `targetHost` directly on `QuestHandDataSender`.

Default ports:

- HL2SS PV health check: `3810`
- Quest hand/stroke UDP output: `5055`
- Custom audio receiver: `5066`
- WebRTC signaling: `5077` local / `5076` remote

## Typical Use

1. Start the HoloLens app/server first.
2. Start the Quest scene.
3. Confirm that the PV billboard updates from the HoloLens stream.
4. Use the Quest hands to point or draw against the billboard.
5. The Quest sends hand poses, stroke events, and audio data to the HoloLens receiver.

## Important Scripts

- `Assets/hl2ss_Scripts/test/run_once.cs`: HoloLens host configuration and HL2SS initialization.
- `Assets/Scripts/OwnHL2SS/rgbd_align_ahat.cs`: PV/AHAT intake, alignment, display, and latest-depth publishing.
- `Assets/Scripts/QuestHandDataSender.cs`: Quest hand, stroke, and audio sender.
- `Assets/Scripts/DepthDistance.cs`: Depth sampling for fingertip and pinch projection.
- `Assets/Scripts/FingerPosition.cs`: Quest fingertip marker on the billboard.
- `Assets/Scripts/BillboardFollow.cs`: Keeps the image billboard positioned relative to the Quest camera.
