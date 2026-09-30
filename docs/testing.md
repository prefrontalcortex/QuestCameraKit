# Building and testing QuestCameraKit

Use **Unity 6000.3.12f1** with Android Build Support, its SDK/NDK and OpenJDK. Open `Unity-QuestVisionKit`, allow Package Manager to finish, and run `git lfs pull` if the clone contains unresolved assets. The packages are pinned; the Quak development package is included locally. No separate sample package installation is required.

## Fast repository and Unity checks

From the repository root:

```sh
python3 scripts/check_repo.py
git diff --check
```

Run the executable regression checks with the Unity executable for your platform:

```sh
Unity -batchmode -nographics -quit \
  -projectPath /absolute/path/QuestCameraKit/Unity-QuestVisionKit \
  -buildTarget Android \
  -executeMethod QuestCameraKit.Editor.MaintenanceChecks.Run \
  -logFile /absolute/path/checks.log
```

These cover stereo PCM/WAV headers and samples, the bundled model's CPU output contract on a synthetic black input, command JSON escaping (including null and control characters), and native QR overlay transitions through absent/present/lost bounds. `MaintenanceChecks.CheckWavStereo` is the focused regression that failed before the stereo fix.

Run `QuestCameraKit.Editor.MaintenanceChecks.AuditScenes` the same way to inspect all seven sample scenes for missing MonoBehaviours. Its `scene-audit.txt` is written at the repository root. It opens scenes without saving them.

## Build one real sample for Quest

The regular **Build and Run** scene list includes all six headset samples, starting with ColorPicker and its sample menu. Y opens/closes the menu, left thumbstick selects, and X loads a scene. The desktop receiving peer is excluded. The CLI helper defaults to `AllSamples` for a combined APK; pass a specific scene name to retain the individual-sample ARM64 IL2CPP development APK and enables Quak's direct OpenXR bridge:

```sh
Unity -batchmode -nographics -quit \
  -projectPath /absolute/path/QuestCameraKit/Unity-QuestVisionKit \
  -buildTarget Android \
  -executeMethod QuestCameraKit.Editor.SampleBuild.Build \
  -sampleScene ColorPicker \
  -apk /absolute/path/Builds/ColorPicker.apk \
  -logFile /absolute/path/build.log
```

Use `AllSamples` for the combined menu app, or `ObjectDetection`, `CameraMappingForShaders`, `ImageLLM`, `QRCodeDetection`, or `WebRTC-Quest` for the other headset samples. `WebRTC-SingleClient` is the receiving-peer scene. Development package IDs are `com.xrdevrob.questcamerakit.<lowercase-scene-name-without-hyphens>`; the helper restores the project's original application ID and product name afterward. Development diagnostics observe the real scene; they do not navigate, synthesize detections, or perform sample interactions. They are excluded from release players.

For a **non-development release build**, disable `XR_APILAYER_XRDEVROB_quak_injection` in Android's OpenXR API Layers settings first. Quak deliberately rejects release builds with its injection layer enabled. The command above is explicitly a device-test build, not a store-release pipeline.

## Quak device checks

Use `quak-tests/AllSamples.json` for the combined app's startup/menu/camera check. The native QR plan requires an actual QR trackable from MRUK; a merely supported API cannot pass it. Present a known QR code and visually compare its decoded payload and spatial alignment. Neither startup plan proves controller-driven navigation: open the menu, visit every sample, return to the first, and repeat on the headset to check camera/scene cleanup.


XRQA is now named **Quak**. Use the matching [v0.4.0-alpha.1 release](https://github.com/xrdevrob/quak/releases/tag/v0.4.0-alpha.1), verify its `SHA256SUMS`, and install the Python wheel in an isolated environment. The Unity package in this repository is that same release. Run `quak setup --project /absolute/path/Unity-QuestVisionKit` to create local CLI configuration when needed.

For Android signature verification, put the Unity Android **OpenJDK/bin** directory on `PATH` or set `JAVA_HOME` to that OpenJDK installation. Also make `adb` and `ffmpeg` available to Quak.

```sh
quak validate Unity-QuestVisionKit/quak-tests/ColorPicker.json
quak prove /absolute/path/Builds/ColorPicker.apk \
  Unity-QuestVisionKit/quak-tests/ColorPicker.json \
  --project /absolute/path/QuestCameraKit/Unity-QuestVisionKit \
  --device-serial YOUR_QUEST_3S_SERIAL \
  --build-source-project /absolute/path/QuestCameraKit/Unity-QuestVisionKit \
  --expected-source-commit YOUR_FULL_COMMIT_SHA \
  --output /absolute/path/evidence/ColorPicker
```

Keep the Quest awake, unlocked, and in the app with camera/spatial-data permissions granted. Select the serial explicitly when more than one headset is connected. Quak handles its temporary bridge and port forward. Review `receipt.json`, `run-report.html`, and the recorded visual evidence; a connected bridge alone is not a sample pass.

The camera tests require fresh frames from every camera in the scene. ObjectDetection additionally requires completed inference. These are startup and processing checks, **not assertions that the detector correctly recognized a particular real object or QR code**. Verify actual content with known objects and QR codes in the headset's view. Confirm color sampling, marker alignment during head motion, stereo shader appearance, and controller/hand interactions on hardware.

ImageLLM additionally needs a private API key, microphone permission and network access. Use only a development key for local testing; do not serialize production credentials into an APK. A production integration should send authenticated requests through your own backend. WebRTC needs a reachable signaling server and another receiving peer. Camera-startup tests do not establish end-to-end cloud or peer-to-peer success.

PuppetTracking-Quest has two separate Quak plans, mirroring the split between "did it start correctly" and "does the feature actually work" used elsewhere in this file:

```sh
quak validate Unity-QuestVisionKit/quak-tests/PuppetTracking-Quest.json
quak prove /absolute/path/Builds/PuppetTracking-Quest.apk \
  Unity-QuestVisionKit/quak-tests/PuppetTracking-Quest.json \
  --project /absolute/path/QuestCameraKit/Unity-QuestVisionKit \
  --device-serial YOUR_QUEST_3S_SERIAL \
  --output /absolute/path/evidence/PuppetTracking-Quest
```

`PuppetTracking-Quest.json` needs no puppet in view and no reachable signaling server: it only
proves the scene starts cleanly and that `OnDeviceBlazePoseDetector` actually enters its detect
loop (`onDeviceMode` changes away from `Idle`). A build where this silently stays `Idle` forever
is the exact "works over Editor+Link, not in a real build" class of regression a manual headset
session catches only by chance; the usual causes are the gitignored `Resources/Models/*.onnx`
model assets missing from the build, IL2CPP stripping, or the backend failing to initialize on
real Android/Vulkan. It does not prove WebRTC peer connectivity to a browser viewer.

`quak-tests/PuppetTracking-Detection.json` additionally requires a puppet or person held in view
of the passthrough camera (see the root README.md's "On-device pose experiment" section for
distance/centering guidance) and proves `onDeviceMode` reaches `Tracking` with
`onDeviceLastDetectionScore`/`onDeviceLastTrackingConfidence` above `OnDeviceBlazePoseDetector`'s
configured thresholds. It does not prove the resulting skeleton looks visually correct or
well-aligned; verify puppet/skeleton plausibility on the headset separately. Keep the JSON's
expected threshold values in sync if `scoreThreshold`/`minTrackingConfidence` change in
`OnDeviceBlazePoseDetector.cs`.
