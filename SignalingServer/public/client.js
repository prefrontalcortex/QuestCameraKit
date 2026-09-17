// Browser-side receiver peer for the SimpleWebRTC signaling protocol used by
// the QuestCameraKit "WebRTC" / "PuppetTracking" samples (com.firedragongamestudio.simplewebrtc).
// Protocol: "TYPE|SenderPeerId|ReceiverPeerId|Message|ConnectionCount|IsVideoAudioSender"
// The server (server.js) is a dumb broadcast relay, so all peer bookkeeping happens here,
// mirroring SimpleWebRTC's WebRTCManager.cs on the Unity side.

const STUN_SERVER = 'stun:stun.l.google.com:19302';
const RECONNECT_MIN_DELAY_MS = 1000;
const RECONNECT_MAX_DELAY_MS = 10000;

const videoEl = document.getElementById('video');
const placeholderEl = document.getElementById('placeholder');
const playBtn = document.getElementById('playBtn');
const statsOverlayEl = document.getElementById('statsOverlay');
const serverUrlInput = document.getElementById('serverUrl');
const peerIdInput = document.getElementById('peerId');
const connectBtn = document.getElementById('connectBtn');
const disconnectBtn = document.getElementById('disconnectBtn');
const statusDot = document.getElementById('statusDot');
const statusText = document.getElementById('statusText');
const logEl = document.getElementById('log');

let ws = null;
let localPeerId = '';
let wantsConnection = false;
let reconnectDelay = RECONNECT_MIN_DELAY_MS;
let reconnectTimer = null;
let statsTimer = null;
const peerConnections = new Map();

serverUrlInput.value = `ws://${location.hostname}:${location.port || 3000}`;
peerIdInput.value = generatePeerId();

function generatePeerId() {
  const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz';
  let id = '';
  for (let i = 0; i < 5; i++) id += chars[Math.floor(Math.random() * chars.length)];
  return `${id}-Web`;
}

function log(message, cls) {
  const line = document.createElement('div');
  if (cls) line.className = cls;
  const time = new Date().toLocaleTimeString();
  line.textContent = `[${time}] ${message}`;
  logEl.appendChild(line);
  logEl.scrollTop = logEl.scrollHeight;
}

function setStatus(state) {
  statusText.textContent = state;
  statusDot.className = `dot ${state === 'connected' ? 'connected' : state === 'disconnected' ? 'disconnected' : 'waiting'}`;
}

function send(type, receiverId, message) {
  const line = [type, localPeerId, receiverId, message, peerConnections.size, false].join('|');
  ws.send(line);
  log(`> ${type} to ${receiverId}`, 'out');
}

function parseMessage(raw) {
  const parts = raw.split('|');
  if (parts.length < 6) return null;
  return {
    type: parts[0],
    senderId: parts[1],
    receiverId: parts[2],
    message: parts[3],
    connectionCount: parseInt(parts[4], 10),
    isVideoAudioSender: parts[5].toLowerCase() === 'true',
  };
}

function ensurePeerConnection(peerId) {
  if (peerConnections.has(peerId)) return peerConnections.get(peerId);

  const pc = new RTCPeerConnection({ iceServers: [{ urls: STUN_SERVER }] });
  peerConnections.set(peerId, pc);
  log(`Created peer connection for ${peerId}`);

  pc.onicecandidate = (event) => {
    if (!event.candidate) return;
    const candidateInit = {
      candidate: event.candidate.candidate,
      sdpMid: event.candidate.sdpMid,
      sdpMLineIndex: event.candidate.sdpMLineIndex,
    };
    send('CANDIDATE', peerId, JSON.stringify(candidateInit));
  };

  pc.oniceconnectionstatechange = () => {
    log(`ICE state for ${peerId}: ${pc.iceConnectionState}`);
    if (pc.iceConnectionState === 'connected' || pc.iceConnectionState === 'completed') {
      setStatus('connected');
      startStatsLoop(pc);
    } else if (pc.iceConnectionState === 'failed' || pc.iceConnectionState === 'disconnected') {
      setStatus('waiting for peer');
    }
  };

  pc.ontrack = (event) => {
    log(`Receiving ${event.track.kind} track from ${peerId}`);
    if (event.track.kind === 'video') {
      // event.streams[0] is only populated if the sender's SDP included an a=msid binding
      // for this track. The Quest side attaches its track via AddTrack/AddTransceiver without
      // ever creating a named MediaStream, so streams is empty here - falling back to wrapping
      // the track in our own MediaStream is required, or srcObject silently ends up undefined
      // and the video element stays black even though bytes are genuinely flowing (visible in
      // the stats overlay, which reads receiver/transport stats independent of this).
      const stream = event.streams && event.streams[0] ? event.streams[0] : new MediaStream([event.track]);
      videoEl.srcObject = stream;
      placeholderEl.hidden = true;
      videoEl.play().catch(() => { playBtn.hidden = false; });
    }
  };

  pc.ondatachannel = (event) => {
    log(`Data channel from ${peerId} opened`);
    event.channel.onmessage = (e) => log(`[data:${peerId}] ${e.data}`);
  };

  return pc;
}

async function handleOffer(senderId, offerJson) {
  const pc = ensurePeerConnection(senderId);
  const offer = JSON.parse(offerJson);
  await pc.setRemoteDescription({ type: 'offer', sdp: offer.sdp });
  const answer = await pc.createAnswer();
  await pc.setLocalDescription(answer);
  send('ANSWER', senderId, JSON.stringify({ type: 'answer', sdp: pc.localDescription.sdp }));
}

async function handleCandidate(senderId, candidateJson) {
  const pc = peerConnections.get(senderId);
  if (!pc) return;
  const candidateInit = JSON.parse(candidateJson);
  if (!candidateInit.candidate) return;
  try {
    await pc.addIceCandidate(candidateInit);
  } catch (err) {
    log(`Failed to add ICE candidate from ${senderId}: ${err.message}`, 'err');
  }
}

function handleDispose(senderId) {
  const pc = peerConnections.get(senderId);
  if (!pc) return;
  pc.close();
  peerConnections.delete(senderId);
  videoEl.srcObject = null;
  placeholderEl.hidden = false;
  setStatus('waiting for peer');
  log(`Peer ${senderId} disposed`);
}

function handleServerMessage(event) {
  const msg = parseMessage(event.data);
  if (!msg) {
    log(`Unparsable message: ${event.data}`, 'err');
    return;
  }
  log(`< ${msg.type} from ${msg.senderId}`);

  switch (msg.type) {
    case 'NEWPEER':
      ensurePeerConnection(msg.senderId);
      send('NEWPEERACK', 'ALL', 'New peer ACK');
      break;
    case 'NEWPEERACK':
      ensurePeerConnection(msg.senderId);
      break;
    case 'OFFER':
      if (msg.receiverId === localPeerId) handleOffer(msg.senderId, msg.message);
      break;
    case 'CANDIDATE':
      if (msg.receiverId === localPeerId) handleCandidate(msg.senderId, msg.message);
      break;
    case 'DISPOSE':
      handleDispose(msg.senderId);
      break;
    case 'COMPLETE':
      if (msg.receiverId === localPeerId) setStatus('connected');
      break;
    default:
      log(`Unhandled message type ${msg.type}`);
  }
}

function startStatsLoop(pc) {
  if (statsTimer) return;
  let lastBytes = 0;
  let lastTime = performance.now();

  statsTimer = setInterval(async () => {
    const report = await pc.getStats();
    let width = 0, height = 0, fps = 0, bytes = 0;

    report.forEach((stat) => {
      // frameWidth/frameHeight live on the inbound-rtp report itself; the separate
      // "track" stat type that used to carry them is deprecated and gone in current browsers.
      if (stat.type === 'inbound-rtp' && stat.kind === 'video') {
        bytes = stat.bytesReceived ?? 0;
        fps = stat.framesPerSecond ?? 0;
        width = stat.frameWidth ?? width;
        height = stat.frameHeight ?? height;
      }
    });

    const now = performance.now();
    const deltaSeconds = (now - lastTime) / 1000;
    const kbps = deltaSeconds > 0 ? Math.round(((bytes - lastBytes) * 8) / 1000 / deltaSeconds) : 0;
    lastBytes = bytes;
    lastTime = now;

    statsOverlayEl.hidden = false;
    statsOverlayEl.textContent = `${width || '?'}x${height || '?'} · ${fps || '?'} fps · ${kbps} kbps`;
  }, 1000);
}

function stopStatsLoop() {
  clearInterval(statsTimer);
  statsTimer = null;
  statsOverlayEl.hidden = true;
}

function connect() {
  localPeerId = peerIdInput.value.trim() || generatePeerId();
  const url = serverUrlInput.value.trim();
  if (!url) {
    log('Server URL is required', 'err');
    return;
  }

  wantsConnection = true;
  setStatus('connecting');
  ws = new WebSocket(url);

  ws.onopen = () => {
    log('WebSocket connected');
    reconnectDelay = RECONNECT_MIN_DELAY_MS;
    setStatus('waiting for peer');
    send('NEWPEER', 'ALL', `New peer ${localPeerId}`);
  };

  ws.onmessage = handleServerMessage;

  ws.onerror = () => log('WebSocket error', 'err');

  ws.onclose = () => {
    log('WebSocket closed');
    setStatus('disconnected');
    if (wantsConnection) scheduleReconnect();
  };

  connectBtn.disabled = true;
  disconnectBtn.disabled = false;
  serverUrlInput.disabled = true;
  peerIdInput.disabled = true;
}

function disconnect() {
  wantsConnection = false;
  clearTimeout(reconnectTimer);
  stopStatsLoop();

  for (const pc of peerConnections.values()) pc.close();
  peerConnections.clear();

  videoEl.srcObject = null;
  placeholderEl.hidden = false;
  playBtn.hidden = true;

  ws?.close();
  ws = null;

  setStatus('disconnected');
  connectBtn.disabled = false;
  disconnectBtn.disabled = true;
  serverUrlInput.disabled = false;
  peerIdInput.disabled = false;
}

function scheduleReconnect() {
  clearTimeout(reconnectTimer);
  log(`Reconnecting in ${Math.round(reconnectDelay / 1000)}s...`);
  reconnectTimer = setTimeout(() => {
    reconnectDelay = Math.min(reconnectDelay * 2, RECONNECT_MAX_DELAY_MS);
    if (wantsConnection) connect();
  }, reconnectDelay);
}

connectBtn.addEventListener('click', connect);
disconnectBtn.addEventListener('click', disconnect);
playBtn.addEventListener('click', () => {
  videoEl.play();
  playBtn.hidden = true;
});
