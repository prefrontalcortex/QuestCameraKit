using Meta.XR;
using UnityEngine;
using UnityEngine.UI;

namespace QuestCameraKit.WebRTC {
    // Neither the signaling server console nor the receiving browser are visible while
    // wearing the headset, so this mirrors the same status in-VR: camera, signaling
    // socket, WebRTC peer and video-transmission state.
    public class PuppetTrackingStatusHud : MonoBehaviour {
        [SerializeField] private PassthroughCameraAccess cameraAccess;
        [SerializeField] private PassthroughWebRTCStreamer streamer;
        [SerializeField] private float refreshInterval = 0.25f;

        private Text _statusText;
        private Transform _hudTransform;
        private float _nextRefresh;

        private void Awake() {
            if (!cameraAccess) {
                cameraAccess = FindAnyObjectByType<PassthroughCameraAccess>(FindObjectsInactive.Include);
            }
            if (!streamer) {
                streamer = FindAnyObjectByType<PassthroughWebRTCStreamer>(FindObjectsInactive.Include);
            }

            BuildHud();
        }

        private void BuildHud() {
            var canvasGo = new GameObject("PuppetTrackingStatusHudCanvas");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var canvasRect = canvasGo.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(420, 170);
            canvasGo.transform.localScale = Vector3.one * 0.001f;
            _hudTransform = canvasGo.transform;

            var background = new GameObject("Background");
            background.transform.SetParent(canvasGo.transform, false);
            var backgroundImage = background.AddComponent<Image>();
            backgroundImage.color = new Color(0f, 0f, 0f, 0.65f);
            var backgroundRect = background.GetComponent<RectTransform>();
            backgroundRect.anchorMin = Vector2.zero;
            backgroundRect.anchorMax = Vector2.one;
            backgroundRect.sizeDelta = Vector2.zero;

            var textGo = new GameObject("StatusText");
            textGo.transform.SetParent(canvasGo.transform, false);
            _statusText = textGo.AddComponent<Text>();
            _statusText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _statusText.fontSize = 22;
            _statusText.color = Color.white;
            _statusText.alignment = TextAnchor.UpperLeft;
            var textRect = textGo.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(16, 16);
            textRect.offsetMax = new Vector2(-16, -16);
        }

        private void Update() {
            var head = Camera.main;
            if (head) {
                _hudTransform.position = head.transform.position + head.transform.forward * 0.6f - head.transform.up * 0.15f;
                _hudTransform.rotation = head.transform.rotation;
            }

            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + refreshInterval;
            Refresh();
        }

        private void Refresh() {
            var cameraState = !cameraAccess ? "missing" : cameraAccess.IsPlaying ? "playing" : "not playing";

            string socketState, peerState, videoState;
            if (!streamer) {
                socketState = peerState = videoState = "missing";
            } else {
                socketState = streamer.Signaling switch {
                    SignalingConnectionState.Connected => $"connected ({streamer.LocalPeerId})",
                    SignalingConnectionState.Connecting => "connecting",
                    SignalingConnectionState.Disconnected => "disconnected",
                    SignalingConnectionState.Failed => "failed",
                    _ => "idle"
                };
                peerState = streamer.KnownPeerCount == 0
                    ? "waiting for peer"
                    : $"{streamer.ConnectedPeerCount}/{streamer.KnownPeerCount} connected";
                videoState = !streamer.IsVideoTrackReady
                    ? "no track"
                    : streamer.IsStreaming ? $"on {streamer.StreamResolution.x}x{streamer.StreamResolution.y}" : "idle";
            }

            _statusText.text =
                $"Passthrough camera: {cameraState}\n" +
                $"Signaling (WS): {socketState}\n" +
                $"WebRTC peers: {peerState}\n" +
                $"Video TX: {videoState}" +
                (streamer && !string.IsNullOrEmpty(streamer.LastError) ? $"\n<error> {streamer.LastError}" : "");
        }
    }
}
