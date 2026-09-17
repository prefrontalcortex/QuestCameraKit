using Meta.XR;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
#if WEBRTC_ENABLED
using SimpleWebRTC;
#endif

namespace QuestCameraKit.WebRTC {
    public class WebRTCController : MonoBehaviour {
        [SerializeField] private PassthroughCameraAccess cameraAccess;
        [SerializeField] private RawImage canvasRawImage;
        [SerializeField] private GameObject connectionGameObject;
        [SerializeField] private bool adaptFovToCustomValue;
        [SerializeField] private float customFovValue;
        [SerializeField] private Camera[] streamingCameras;

#if WEBRTC_ENABLED
        private Texture _cameraTexture;
        private WebRTCConnection _webRTCConnection;

        private IEnumerator Start() {
            cameraAccess = ResolveCameraAccess(cameraAccess);
            if (!cameraAccess || !connectionGameObject || !canvasRawImage) {
                Debug.LogError("[WebRTCController] Assign camera, connection and preview references.");
                enabled = false;
                yield break;
            }
            _webRTCConnection = connectionGameObject.GetComponent<WebRTCConnection>();
            yield return new WaitUntil(() => !cameraAccess || cameraAccess.IsPlaying);
            if (!cameraAccess) {
                Debug.LogWarning("[WebRTCController] Passthrough camera unavailable.");
                yield break;
            }

            _webRTCConnection = connectionGameObject.GetComponent<WebRTCConnection>();
            _cameraTexture = cameraAccess.GetTexture();
            canvasRawImage.texture = _cameraTexture;
        }

        private void Update() {
            if (!_webRTCConnection || !cameraAccess || !cameraAccess.IsPlaying) return;
            canvasRawImage.texture = cameraAccess.GetTexture();

            if (OVRInput.GetDown(OVRInput.Button.Start)) {
                TryStartVideoTransmission();
            }

            if (adaptFovToCustomValue && streamingCameras != null) {
                foreach (var camera in streamingCameras) {
                    if (camera) camera.fieldOfView = Mathf.Clamp(customFovValue, 1f, 179f);
                }
            }

#if UNITY_EDITOR
            if (Keyboard.current?.spaceKey.wasReleasedThisFrame == true) {
                TryStartVideoTransmission();
            }
#endif
        }

        private void TryStartVideoTransmission() {
            // WebRTCConnection.StartVideoTransmission() is not idempotent: its internal
            // StopCoroutine(StartVideoTransmissionAsync()) call can never actually cancel a
            // previous run (StopCoroutine only matches the exact enumerator instance it was
            // started with, and every call creates a new one), so triggering it again while
            // transmission is already active adds a second video track/transceiver to the
            // same peer connection instead of restarting the first one.
            if (_webRTCConnection.IsVideoTransmissionActive) return;
            _webRTCConnection.StartVideoTransmission();
        }
#endif

        private static PassthroughCameraAccess ResolveCameraAccess(PassthroughCameraAccess configuredAccess) {
            if (configuredAccess) {
                return configuredAccess;
            }

            return FindAnyObjectByType<PassthroughCameraAccess>(FindObjectsInactive.Include);
        }
    }
}
