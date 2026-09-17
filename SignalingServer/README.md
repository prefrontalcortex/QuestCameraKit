# Puppet Tracking – Signaling Server

A minimal server that relays WebRTC signaling between the Quest 3 (`7 PuppetTracking`
sample) and a browser, and a web page that displays the incoming passthrough video. The
browser page also runs pose detection on that video (MediaPipe Tasks Vision, in-browser)
and sends the detected 2D skeleton back to the Quest over the WebRTC data channel, where
it's rendered as a 3D AR overlay on the real puppet — see "Pose detection" below.

## Why this server, and not the digicare one

The wire protocol is a pipe-delimited broadcast format
(`TYPE|SenderPeerId|ReceiverPeerId|Message|ConnectionCount|IsVideoAudioSender`), originally
defined by the third-party SimpleWebRTC Unity package. That's a different wire format from
digicare's session/role-based JSON signaling server, so it isn't a drop-in replacement.
This server reuses digicare's *shape* (Node + Express + `ws`, static web viewer with a
debug log and stats overlay) but implements that broadcast protocol: it's a dumb relay —
every message from one client is forwarded to all others, and peers self-organize via
`NEWPEER`/`NEWPEERACK` messages (mirrored in `public/client.js`).

The Quest side no longer uses the SimpleWebRTC package at all — `Assets/Samples/7 PuppetTracking/Scripts/`
implements the same wire protocol directly against Unity's official `com.unity.webrtc` +
`com.endel.nativewebsocket` packages (`PassthroughWebRTCStreamer.cs` +
`SignalingSocketClient.cs`), feeding the passthrough camera texture straight into a
`VideoStreamTrack` instead of going through SimpleWebRTC's "render a camera pointed at a UI
canvas" indirection, which never produced a reliable image. The `6 WebRTC` sample (which
does still use SimpleWebRTC) is untouched.

## Run it

```sh
npm install
npm start        # or: npm run dev (auto-restart via nodemon)
```

The server listens on `0.0.0.0:3000` by default (override with `PORT`). Open
`http://<this-machine-lan-ip>:3000` in a browser on the same network as the Quest.

If Windows Firewall blocks the port:

```powershell
New-NetFirewallRule -DisplayName "Puppet Tracking Signaling" -Direction Inbound -Protocol TCP -LocalPort 3000 -Action Allow
```

## Point the Quest at this server

In the `7 PuppetTracking` sample scene, on the `PassthroughWebRTCStreamer` component (on
the "WebRTC Controller" GameObject):

- `signalingServerUrl` → `ws://<this-machine-lan-ip>:3000` (currently checked in as
  `ws://192.168.1.102:3000` for this machine's Ethernet adapter — update it if you run the
  server from a different machine or network; find your own LAN IP with
  `Get-NetIPConfiguration` in PowerShell).
- Signaling connects automatically on scene start, independent of the camera — no
  Inspector toggle needed.

The old SimpleWebRTC-based `WebRTCController` component and the `Client-STUNConnection`
GameObject are still present in the scene but disabled, kept only so the setup is easy to
compare/revert.

In the browser page, use the same server URL (prefilled from the page's own host), click
**Connect** — no manual "start transmission" step is needed on the Quest: as soon as the
passthrough camera is playing and a browser peer is known, `PassthroughWebRTCStreamer`
creates an offer for it automatically. The live passthrough image should appear in the
viewer.

The Quest also shows a small in-headset HUD (`PuppetTrackingStatusHud`, floating in front
of the camera) with the same status the browser can't tell you about from inside the
headset: passthrough camera playing/not playing, signaling socket state, how many WebRTC
peers are connected, and whether the video track is streaming (plus the last error, if
any — e.g. a `VideoStreamTrack` format-mismatch exception). Useful because the actual video
never passes through this server — WebRTC media goes peer-to-peer directly between the
Quest and the browser, so this server's console only ever shows signaling messages
(`NEWPEER`, `OFFER`, `ANSWER`, `CANDIDATE`, ...), never the video itself.

## Pose detection

Check **Enable pose detection** in the browser page once video is flowing. It loads
MediaPipe Tasks Vision's Pose Landmarker (~17MB, once) and runs it against the `<video>`
element entirely client-side — no CDN dependency, since the WASM runtime is served
straight from `node_modules` and the model file is fetched once via `npm run postinstall`
(`scripts/download-assets.js`) into `public/models/` (gitignored, not committed).

Enter the puppet's **shoulder-to-hip length in cm** (measure the real puppet) in the field
below the toggle — the Quest uses it to estimate camera-to-puppet distance from the
apparent angle between the detected shoulder and hip landmarks (`PuppetPoseVisualizer.cs`),
then ray-casts every landmark through `PassthroughCameraAccess.ViewportPointToRay` at that
one estimated distance. This places the skeleton at roughly the right position and size on
the real puppet, but flattens it: every landmark lands at the same distance from the
camera, so there's no per-limb depth (an arm reaching toward the camera won't appear to
come forward). Real volumetric placement (via MediaPipe's `worldLandmarks` offsets around a
single anchor) is a natural follow-up once this flat version is validated.

Detected landmarks are sent over the data channel the Quest already creates
(`PassthroughWebRTCStreamer.cs`, `createDataChannel`), throttled to ~15Hz, as JSON:
`{ "type": "pose", "referenceLengthCm": ..., "landmarks": [{ "x", "y", "visibility" }, ...] }`
(33 entries, MediaPipe's normalized image-space coordinates — top-left origin, y grows
downward).

## Health check

`GET /health` returns `{ "status": "ok", "clients": <count> }` for a quick sanity check.
