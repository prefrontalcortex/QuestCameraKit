using Meta.XR;
using System;
using System.Collections;
using System.Collections.Generic;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.UI;
using UnityWebRTC = Unity.WebRTC.WebRTC;

namespace QuestCameraKit.WebRTC {
    // Streams the Quest 3 passthrough camera directly into a Unity.WebRTC VideoStreamTrack -
    // no intermediate "camera photographs a UI RawImage canvas" step, unlike the SimpleWebRTC-
    // based "6 WebRTC" sample this replaces for the puppet-tracking prototype. Speaks the same
    // pipe-delimited signaling protocol as SignalingServer/public/client.js so that server and
    // browser viewer need no changes.
    public class PassthroughWebRTCStreamer : MonoBehaviour {
        [Header("Passthrough source")]
        [SerializeField] private PassthroughCameraAccess cameraAccess;
        [SerializeField] private Vector2Int streamResolution = Vector2Int.zero; // zero = use cameraAccess.CurrentResolution
        [SerializeField] private bool flipVertically;
        [SerializeField] private bool mirrorHorizontally;
        [SerializeField] private RawImage previewRawImage; // optional: shows exactly the pixels being sent

        [Header("Signaling")]
        [SerializeField] private string signalingServerUrl = "ws://192.168.1.102:3000";
        [SerializeField] private string localPeerId = "Quest";
        [SerializeField] private bool appendRandomPeerIdSuffix = true;
        [SerializeField] private bool autoReconnect = true;

        [Header("WebRTC")]
        [SerializeField] private string stunServerUrl = "stun:stun.l.google.com:19302";
        [SerializeField] private bool createDataChannel = true; // ready for a future pose-data return channel
        [SerializeField] private float deadPeerTimeoutSeconds = 20f;

        public SignalingConnectionState Signaling => _signaling?.State ?? SignalingConnectionState.Idle;
        public string LocalPeerId => _localPeerId;
        public int KnownPeerCount => _sessions.Count;
        public int ConnectedPeerCount { get; private set; }
        public bool IsVideoTrackReady => _videoTrack != null;
        public bool IsStreaming => IsVideoTrackReady && ConnectedPeerCount > 0;
        public Vector2Int StreamResolution => _streamTexture?.Size ?? Vector2Int.zero;
        public string LastError { get; private set; }

        public event Action OnStateChanged;

        // Fired for every message received on any peer's data channel (peerId, payload).
        // Raised directly from RTCDataChannel.OnMessage, which - like every Unity.WebRTC
        // callback - can run off the main thread, so subscribers must not touch Unity APIs
        // directly from this event; marshal to Update()/a coroutine instead.
        public event Action<string, string> OnDataChannelMessage;

        private sealed class PeerSession {
            public string PeerId;
            public RTCPeerConnection Connection;
            public RTCRtpTransceiver VideoTransceiver;
            public RTCDataChannel DataChannel;

            public bool HasRemoteDescription;
            public bool OfferInFlight;
            public bool OfferSent;
            public readonly Queue<RTCIceCandidateInit> PendingRemoteCandidates = new Queue<RTCIceCandidateInit>();
            public readonly List<string> PendingLocalCandidates = new List<string>();
            public Coroutine NegotiationCoroutine;
            public float UnhealthySince = -1f;
        }

        private readonly Dictionary<string, PeerSession> _sessions = new Dictionary<string, PeerSession>();

        private SignalingSocketClient _signaling;
        private PassthroughStreamTexture _streamTexture;
        private VideoStreamTrack _videoTrack;
        private Coroutine _pumpCoroutine;
        private string _localPeerId;

        private IEnumerator Start() {
            if (!cameraAccess) {
                cameraAccess = ResolveCameraAccess(cameraAccess);
            }
            if (!cameraAccess) {
                Debug.LogWarning("[PassthroughWebRTCStreamer] No PassthroughCameraAccess found.");
            }

            _localPeerId = appendRandomPeerIdSuffix ? $"{localPeerId}-{GenerateRandomSuffix()}" : localPeerId;

            // WebRTC.Update() pumps the native pipeline for every track/connection that exists
            // process-wide; it must run for this component's whole lifetime, started before
            // anything else touches the WebRTC API.
            _pumpCoroutine = StartCoroutine(UnityWebRTC.Update());

            _signaling = new SignalingSocketClient(signalingServerUrl, _localPeerId, autoReconnect);
            _signaling.OnEnvelope += HandleEnvelope;
            _signaling.OnConnected += RefreshPublicState;
            _signaling.OnDisconnected += _ => RefreshPublicState();
            _signaling.Connect(); // independent of the camera - the HUD should show socket state either way

            yield return new WaitUntil(() => !cameraAccess || cameraAccess.IsPlaying);
            if (!cameraAccess) yield break;

            var size = streamResolution != Vector2Int.zero ? streamResolution : cameraAccess.CurrentResolution;
            _streamTexture = new PassthroughStreamTexture(size, flipVertically, mirrorHorizontally);
            _streamTexture.TryUpdate(cameraAccess.GetTexture()); // seed one real frame before the track exists

            if (previewRawImage) {
                previewRawImage.texture = _streamTexture.Texture;
            }

            try {
                _videoTrack = new VideoStreamTrack(_streamTexture.Texture);
            } catch (ArgumentException ex) {
                LastError = $"VideoStreamTrack format mismatch: {ex.Message} (texture format: {_streamTexture.Texture.graphicsFormat}, expected: {UnityWebRTC.GetSupportedGraphicsFormat(SystemInfo.graphicsDeviceType)})";
                Debug.LogError($"[PassthroughWebRTCStreamer] {LastError}");
                yield break;
            }

            // A browser may have already registered while we were waiting on the camera.
            foreach (var session in _sessions.Values) {
                TryStartNegotiation(session);
            }
            RefreshPublicState();
        }

        private void Update() {
            _signaling?.Pump(); // always first - never gate this behind a camera check, or signaling goes silent

            if (_videoTrack != null && cameraAccess && cameraAccess.IsPlaying) {
                _streamTexture.TryUpdate(cameraAccess.GetTexture());
            }

            PruneDeadSessions();
            RefreshPublicState();
        }

        private void OnDestroy() {
            Shutdown();
        }

        private void HandleEnvelope(SignalingEnvelope envelope) {
            switch (envelope.Type) {
                case SignalingMessageType.NEWPEER: {
                    var session = EnsureSession(envelope.SenderPeerId);
                    _signaling.Send(SignalingMessageType.NEWPEERACK, SignalingEnvelope.BroadcastPeerId, "New peer ACK", _sessions.Count, true);
                    TryStartNegotiation(session);
                    break;
                }
                case SignalingMessageType.NEWPEERACK: {
                    var session = EnsureSession(envelope.SenderPeerId);
                    TryStartNegotiation(session);
                    break;
                }
                case SignalingMessageType.OFFER:
                    Debug.LogWarning($"[PassthroughWebRTCStreamer] Ignoring unexpected OFFER from {envelope.SenderPeerId} - this component only sends offers.");
                    break;
                case SignalingMessageType.ANSWER:
                    if (envelope.IsAddressedTo(_localPeerId) && _sessions.TryGetValue(envelope.SenderPeerId, out var answerSession)) {
                        StartCoroutine(ApplyAnswerCoroutine(answerSession, envelope.Message));
                    }
                    break;
                case SignalingMessageType.CANDIDATE:
                    if (envelope.IsAddressedTo(_localPeerId) && _sessions.TryGetValue(envelope.SenderPeerId, out var candidateSession)) {
                        HandleRemoteCandidate(candidateSession, envelope.Message);
                    }
                    break;
                case SignalingMessageType.DISPOSE:
                    // The browser sends this to ALL, not addressed - do not filter by receiver id.
                    CloseSession(envelope.SenderPeerId);
                    break;
                default:
                    break;
            }
        }

        private PeerSession EnsureSession(string peerId) {
            if (_sessions.TryGetValue(peerId, out var existing)) return existing;

            var configuration = new RTCConfiguration {
                iceServers = string.IsNullOrEmpty(stunServerUrl)
                    ? null
                    : new[] { new RTCIceServer { urls = new[] { stunServerUrl } } }
            };

            var session = new PeerSession {
                PeerId = peerId,
                Connection = new RTCPeerConnection(ref configuration)
            };

            session.Connection.OnIceCandidate = candidate => {
                var payload = new IceCandidatePayload {
                    candidate = candidate.Candidate,
                    sdpMid = candidate.SdpMid,
                    sdpMLineIndex = candidate.SdpMLineIndex ?? 0
                };
                var json = JsonUtility.ToJson(payload);
                if (session.OfferSent) {
                    _signaling.Send(SignalingMessageType.CANDIDATE, peerId, json, _sessions.Count, true);
                } else {
                    session.PendingLocalCandidates.Add(json);
                }
            };

            session.Connection.OnIceConnectionChange = state => {
                if (state == RTCIceConnectionState.Connected || state == RTCIceConnectionState.Completed) {
                    _signaling.Send(SignalingMessageType.COMPLETE, peerId, $"Peer connection with {peerId} completed.", _sessions.Count, true);
                }
            };

            session.Connection.OnConnectionStateChange = _ => RefreshPublicState();

            // Manual negotiation only - do not auto-renegotiate on OnNegotiationNeeded, that
            // caused duplicate offers/tracks in the SimpleWebRTC-based sample we're replacing.

            if (createDataChannel) {
                session.DataChannel = session.Connection.CreateDataChannel("puppet");
                session.DataChannel.OnMessage = bytes => {
                    OnDataChannelMessage?.Invoke(peerId, System.Text.Encoding.UTF8.GetString(bytes));
                };
            }

            _sessions[peerId] = session;
            AttachVideoTrack(session);
            RefreshPublicState();
            return session;
        }

        private void AttachVideoTrack(PeerSession session) {
            if (_videoTrack == null || session.VideoTransceiver != null) return;
            session.VideoTransceiver = session.Connection.AddTransceiver(_videoTrack, new RTCRtpTransceiverInit {
                direction = RTCRtpTransceiverDirection.SendOnly
            });
        }

        private void TryStartNegotiation(PeerSession session) {
            if (_videoTrack == null || session.OfferInFlight || session.NegotiationCoroutine != null) return;
            AttachVideoTrack(session);
            session.OfferInFlight = true;
            session.NegotiationCoroutine = StartCoroutine(NegotiateCoroutine(session));
        }

        private IEnumerator NegotiateCoroutine(PeerSession session) {
            var offerOp = session.Connection.CreateOffer();
            yield return offerOp;
            if (offerOp.IsError) {
                LastError = $"CreateOffer failed for {session.PeerId}: {offerOp.Error.message}";
                Debug.LogError($"[PassthroughWebRTCStreamer] {LastError}");
                session.OfferInFlight = false;
                session.NegotiationCoroutine = null;
                yield break;
            }

            var desc = offerOp.Desc;
            var localOp = session.Connection.SetLocalDescription(ref desc);
            yield return localOp;
            if (localOp.IsError) {
                LastError = $"SetLocalDescription failed for {session.PeerId}: {localOp.Error.message}";
                Debug.LogError($"[PassthroughWebRTCStreamer] {LastError}");
                session.OfferInFlight = false;
                session.NegotiationCoroutine = null;
                yield break;
            }

            var payload = new SdpPayload { type = "offer", sdp = session.Connection.LocalDescription.sdp };
            _signaling.Send(SignalingMessageType.OFFER, session.PeerId, JsonUtility.ToJson(payload), _sessions.Count, true);
            session.OfferSent = true;

            foreach (var json in session.PendingLocalCandidates) {
                _signaling.Send(SignalingMessageType.CANDIDATE, session.PeerId, json, _sessions.Count, true);
            }
            session.PendingLocalCandidates.Clear();

            session.OfferInFlight = false;
            session.NegotiationCoroutine = null;
        }

        private IEnumerator ApplyAnswerCoroutine(PeerSession session, string json) {
            var payload = JsonUtility.FromJson<SdpPayload>(json);
            var desc = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = payload.sdp };
            var op = session.Connection.SetRemoteDescription(ref desc);
            yield return op;
            if (op.IsError) {
                LastError = $"SetRemoteDescription failed for {session.PeerId}: {op.Error.message}";
                Debug.LogError($"[PassthroughWebRTCStreamer] {LastError}");
                yield break;
            }

            session.HasRemoteDescription = true;
            while (session.PendingRemoteCandidates.Count > 0) {
                session.Connection.AddIceCandidate(new RTCIceCandidate(session.PendingRemoteCandidates.Dequeue()));
            }
        }

        private void HandleRemoteCandidate(PeerSession session, string json) {
            var payload = JsonUtility.FromJson<IceCandidatePayload>(json);
            if (string.IsNullOrEmpty(payload.candidate)) return; // end-of-gathering sentinel

            var init = new RTCIceCandidateInit {
                candidate = payload.candidate,
                sdpMid = payload.sdpMid,
                sdpMLineIndex = payload.sdpMLineIndex
            };

            // This buffering is the whole point of this rewrite: adding a candidate before the
            // remote description is set silently fails (ICE never reaches Connected) even though
            // the signaling log looks perfect - exactly what SimpleWebRTC's WebRTCManager does
            // NOT guard against.
            if (!session.HasRemoteDescription) {
                session.PendingRemoteCandidates.Enqueue(init);
                return;
            }
            session.Connection.AddIceCandidate(new RTCIceCandidate(init));
        }

        private void CloseSession(string peerId) {
            if (!_sessions.TryGetValue(peerId, out var session)) return;

            if (session.NegotiationCoroutine != null) {
                StopCoroutine(session.NegotiationCoroutine);
            }
            session.DataChannel?.Close();
            session.DataChannel?.Dispose();
            session.Connection?.Close();
            session.Connection?.Dispose();
            session.PendingRemoteCandidates.Clear();
            session.PendingLocalCandidates.Clear();

            _sessions.Remove(peerId);
            RefreshPublicState();
        }

        private void PruneDeadSessions() {
            // Browsers never send DISPOSE on tab close/reload, so this is the only cleanup path
            // for a viewer that simply vanished. Only prune on Failed/Closed, not Disconnected -
            // a flaky Wi-Fi link can and does recover from Disconnected.
            List<string> toClose = null;
            foreach (var session in _sessions.Values) {
                var state = session.Connection.ConnectionState;
                var bad = state == RTCPeerConnectionState.Failed || state == RTCPeerConnectionState.Closed;
                if (!bad) {
                    session.UnhealthySince = -1f;
                    continue;
                }
                if (session.UnhealthySince < 0f) {
                    session.UnhealthySince = Time.unscaledTime;
                } else if (Time.unscaledTime - session.UnhealthySince > deadPeerTimeoutSeconds) {
                    (toClose ??= new List<string>()).Add(session.PeerId);
                }
            }
            if (toClose == null) return;
            foreach (var peerId in toClose) CloseSession(peerId);
        }

        private void RefreshPublicState() {
            var connected = 0;
            foreach (var session in _sessions.Values) {
                if (session.Connection.ConnectionState == RTCPeerConnectionState.Connected) connected++;
            }
            ConnectedPeerCount = connected;
            OnStateChanged?.Invoke();
        }

        private void Shutdown() {
            if (_signaling is { State: SignalingConnectionState.Connected }) {
                _signaling.Send(SignalingMessageType.DISPOSE, SignalingEnvelope.BroadcastPeerId, $"Remove peer {_localPeerId}", _sessions.Count, true);
            }

            foreach (var peerId in new List<string>(_sessions.Keys)) {
                CloseSession(peerId);
            }

            // Order matters: peer connections -> track -> pump -> render texture. Releasing the
            // RenderTexture while WebRTC.Update() can still touch it is a native use-after-free.
            _videoTrack?.Stop();
            _videoTrack?.Dispose();
            _videoTrack = null;

            if (_pumpCoroutine != null) {
                StopCoroutine(_pumpCoroutine);
                _pumpCoroutine = null;
            }

            _streamTexture?.Dispose();
            _streamTexture = null;

            _signaling?.Dispose();
            _signaling = null;
        }

        private static PassthroughCameraAccess ResolveCameraAccess(PassthroughCameraAccess configured) {
            if (configured) return configured;
            return FindAnyObjectByType<PassthroughCameraAccess>(FindObjectsInactive.Include);
        }

        private static string GenerateRandomSuffix() {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
            var buffer = new char[5];
            for (var i = 0; i < buffer.Length; i++) {
                buffer[i] = chars[UnityEngine.Random.Range(0, chars.Length)];
            }
            return new string(buffer);
        }
    }
}
