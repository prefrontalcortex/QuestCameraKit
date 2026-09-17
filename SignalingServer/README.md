# Puppet Tracking – Signaling Server

Step 1 of the puppet/skeleton-tracking prototype: a minimal server that relays WebRTC
signaling between the Quest 3 (`7 PuppetTracking` sample) and a browser, and a web page
that displays the incoming passthrough video. No pose/skeleton AI yet — this only proves
the video signal makes it from the headset to a viewer on the LAN.

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

## Health check

`GET /health` returns `{ "status": "ok", "clients": <count> }` for a quick sanity check.
