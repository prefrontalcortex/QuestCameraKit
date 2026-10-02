using Meta.XR;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace QuestCameraKit.WebRTC {
    // Places a flat 2D-pose "billboard" skeleton in world space over the real puppet, using
    // PassthroughCameraAccess's own camera model (ViewportPointToRay) to cast a ray through
    // each received landmark, and a single estimated camera-to-puppet distance (from the
    // shoulder-to-hip reference length measured in the browser) to place every point along
    // its own ray. This is a first approximation: every landmark lands at the same distance
    // from the camera, so there is no per-limb depth (an arm reaching toward the camera won't
    // appear to come forward) - real volumetric placement would use the pose's worldLandmarks
    // offsets around a single anchor instead, as a follow-up step.
    public class PuppetPoseVisualizer : MonoBehaviour {
        [SerializeField] private PassthroughCameraAccess cameraAccess;
        [SerializeField] private PassthroughWebRTCStreamer streamer;
        [SerializeField] private float jointRadius = 0.01f;
        [SerializeField] private float lineWidth = 0.006f;
        [SerializeField] private float minVisibility = 0.3f;
        [SerializeField] private Color skeletonColor = new Color(0.36f, 0.55f, 1f);

        // Neither producer (browser MediaPipe nor the on-device detector) smooths landmarks
        // over time, so raw per-frame noise goes straight into the placement math and shows up
        // as visible jitter - a 1€ filter per landmark coordinate fixes that without adding the
        // fixed lag a plain low-pass filter would (see OneEuroFilter.cs for why).
        //
        // Originally tuned assuming the puppet never moves on its own (beta near zero, so the
        // filter never relaxes) - field testing showed it actually gets picked up and moved
        // around during testing, and that heavy a smoothing setting visibly lags behind real
        // motion instead of just rejecting noise. Raised beta so the filter relaxes and catches
        // up during real movement, and minCutoff for better baseline responsiveness - some
        // jitter returns when the puppet is held perfectly still, which is the correct trade-off
        // once the subject actually moves.
        [SerializeField] private bool enableSmoothing = true;
        [SerializeField] private float smoothingMinCutoff = 1f;
        [SerializeField] private float smoothingBeta = 0.3f;
        [SerializeField] private float smoothingDerivativeCutoff = 1f;

        // The 1€ filter above still lets a perfectly static subject (a mannequin that is never
        // actually moving) visibly jitter, since it smooths noise rather than rejecting it
        // outright - raising its beta to catch up with real movement (see above) made this
        // worse, not better. Realization from first real-device feedback: a static subject's
        // pose only needs to be *determined* once, not every frame - so instead of a small
        // per-frame distance deadband, place the skeleton in world space once and then hold
        // every joint there outright, skipping re-placement entirely until this many seconds
        // have passed. Default/fallback only; PoseHoldSeconds below is the live value,
        // adjustable on-device (left trigger + left thumbstick) for the same reason
        // ReferenceLengthCm is in OnDeviceBlazePoseDetector: how static the subject really is
        // (and how much that's worth trading off against) varies by session. Trade-off: real
        // movement during the hold window (someone picking the puppet up) isn't reflected
        // until the window ends - acceptable, even desirable, for a mostly-static training
        // mannequin; set to 0 to disable and re-place every frame instead.
        [SerializeField] private bool enablePoseHold = true;
        [SerializeField] private float poseHoldSeconds = 10f;
        private const string PoseHoldPrefsKey = "QuestCameraKit.PuppetTracking.PoseHoldSeconds";
        private const float PoseHoldMinSeconds = 0f;
        private const float PoseHoldMaxSeconds = 30f;
        private const float PoseHoldAdjustSecondsPerSecond = 5f;
        private const float PoseHoldAxisDeadzone = 0.15f;

        public float PoseHoldSeconds { get; private set; }
        public float PoseHoldRemainingSeconds => Mathf.Max(0f, _nextPoseSampleTime - Time.unscaledTime);

        private float _nextPoseSampleTime;

        // Standard MediaPipe BlazePose 33-landmark connection graph, matching
        // PoseLandmarker.POSE_CONNECTIONS on the browser side exactly (extracted from
        // @mediapipe/tasks-vision) so both ends draw the same skeleton topology.
        private static readonly (int From, int To)[] Connections = {
            (0, 1), (1, 2), (2, 3), (3, 7), (0, 4), (4, 5), (5, 6), (6, 8), (9, 10),
            (11, 12), (11, 13), (13, 15), (15, 17), (15, 19), (15, 21), (17, 19),
            (12, 14), (14, 16), (16, 18), (16, 20), (16, 22), (18, 20),
            (11, 23), (12, 24), (23, 24), (23, 25), (24, 26), (25, 27), (26, 28),
            (27, 29), (28, 30), (29, 31), (30, 32), (27, 31), (28, 32),
        };

        private const int LeftShoulder = 11;
        private const int RightShoulder = 12;
        private const int LeftHip = 23;
        private const int RightHip = 24;
        public const int LandmarkCount = 33;

        // Already in PassthroughCameraAccess's own viewport convention (normalized [0,1],
        // origin bottom-left, y grows upward) - producers in a different convention (e.g.
        // MediaPipe's browser output, which is top-left/y-down) must convert before calling
        // ApplyPose, so this visualizer never has to know or care where a pose came from.
        public struct Landmark {
            public float X;
            public float Y;
            public float Visibility;
        }

        [Serializable]
        private class Landmark2D {
            public float x;
            public float y;
            public float visibility;
        }

        [Serializable]
        private class PoseMessage {
            public string type;
            public float referenceLengthCm;
            public Landmark2D[] landmarks;
        }

        private LineRenderer[] _connectorRenderers;
        private Transform[] _jointSpheres;
        private readonly Queue<string> _pendingMessages = new Queue<string>();
        private readonly object _queueLock = new object();

        private OneEuroFilter[] _xFilters;
        private OneEuroFilter[] _yFilters;
        private Landmark[] _smoothedLandmarks;

        private void Awake() {
            if (!cameraAccess) {
                cameraAccess = FindAnyObjectByType<PassthroughCameraAccess>(FindObjectsInactive.Include);
            }
            if (!streamer) {
                streamer = FindAnyObjectByType<PassthroughWebRTCStreamer>(FindObjectsInactive.Include);
            }
            if (streamer) {
                streamer.OnDataChannelMessage += HandleDataChannelMessage;
            }

            _xFilters = new OneEuroFilter[LandmarkCount];
            _yFilters = new OneEuroFilter[LandmarkCount];
            for (var i = 0; i < LandmarkCount; i++) {
                _xFilters[i] = new OneEuroFilter(smoothingMinCutoff, smoothingBeta, smoothingDerivativeCutoff);
                _yFilters[i] = new OneEuroFilter(smoothingMinCutoff, smoothingBeta, smoothingDerivativeCutoff);
            }
            _smoothedLandmarks = new Landmark[LandmarkCount];

            PoseHoldSeconds = PlayerPrefs.HasKey(PoseHoldPrefsKey)
                ? PlayerPrefs.GetFloat(PoseHoldPrefsKey)
                : poseHoldSeconds;

            BuildVisuals();
        }

        // Mirrors OnDeviceBlazePoseDetector's own reference-length adjustment, but on the left
        // hand/trigger so both can be held independently of each other. Runs every frame
        // regardless of whether a pose is currently being applied, so it stays responsive.
        private void AdjustPoseHold() {
            if (!OVRInput.Get(OVRInput.RawButton.LIndexTrigger)) return;
            var axis = OVRInput.Get(OVRInput.RawAxis2D.LThumbstick).y;
            if (Mathf.Abs(axis) < PoseHoldAxisDeadzone) return;

            PoseHoldSeconds = Mathf.Clamp(
                PoseHoldSeconds + axis * PoseHoldAdjustSecondsPerSecond * Time.deltaTime,
                PoseHoldMinSeconds, PoseHoldMaxSeconds);
            PlayerPrefs.SetFloat(PoseHoldPrefsKey, PoseHoldSeconds);
        }

        private void OnDestroy() {
            if (streamer) {
                streamer.OnDataChannelMessage -= HandleDataChannelMessage;
            }
        }

        private void HandleDataChannelMessage(string peerId, string payload) {
            // RTCDataChannel.OnMessage's thread affinity isn't guaranteed, so hop through a
            // queue drained on Update() rather than touching transforms/materials here.
            lock (_queueLock) {
                _pendingMessages.Enqueue(payload);
            }
        }

        private void BuildVisuals() {
            // Shader.Find is unsafe at runtime: IL2CPP/Android builds strip any shader that no
            // Material asset statically references, and this one only ever got looked up here -
            // it worked in the Editor (which never strips) and silently returned null on-device,
            // throwing out of Awake() before _connectorRenderers/_jointSpheres were assigned and
            // crashing the next ClearPose() call with a NullReferenceException. Also kept in
            // GraphicsSettings' Always Included Shaders now, but fall back rather than crash if
            // that ever regresses.
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
            if (!shader) {
                Debug.LogError("[PuppetPoseVisualizer] No skeleton shader available (stripped from build?); skeleton will not be drawn.");
                return;
            }
            var material = new Material(shader) { color = skeletonColor };

            _connectorRenderers = new LineRenderer[Connections.Length];
            for (var i = 0; i < Connections.Length; i++) {
                var go = new GameObject($"Bone_{Connections[i].From}_{Connections[i].To}");
                go.transform.SetParent(transform, false);
                var lr = go.AddComponent<LineRenderer>();
                lr.positionCount = 2;
                lr.widthMultiplier = lineWidth;
                lr.material = material;
                lr.useWorldSpace = true;
                lr.enabled = false;
                _connectorRenderers[i] = lr;
            }

            _jointSpheres = new Transform[LandmarkCount];
            for (var i = 0; i < LandmarkCount; i++) {
                var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = $"Joint_{i}";
                sphere.transform.SetParent(transform, false);
                sphere.transform.localScale = Vector3.one * (jointRadius * 2f);
                sphere.GetComponent<Renderer>().material = material;
                Destroy(sphere.GetComponent<Collider>());
                sphere.SetActive(false);
                _jointSpheres[i] = sphere.transform;
            }
        }

        private void Update() {
            AdjustPoseHold();

            string payload = null;
            lock (_queueLock) {
                // Only the latest message matters for a live overlay - drop anything stale
                // that piled up while a previous frame's parsing/placement was running.
                while (_pendingMessages.Count > 0) {
                    payload = _pendingMessages.Dequeue();
                }
            }
            if (payload == null) return;
            if (!cameraAccess || !cameraAccess.IsPlaying) return;

            PoseMessage message;
            try {
                message = JsonUtility.FromJson<PoseMessage>(payload);
            } catch (Exception) {
                return;
            }
            if (message?.landmarks == null || message.landmarks.Length != LandmarkCount || message.type != "pose") return;

            // MediaPipe's browser output is normalized [0,1] with the origin at the top-left
            // (y grows downward) - convert to PassthroughCameraAccess's viewport convention
            // (origin bottom-left, y grows upward) before handing off to the shared path.
            var landmarks = new Landmark[LandmarkCount];
            for (var i = 0; i < LandmarkCount; i++) {
                var lm = message.landmarks[i];
                landmarks[i] = new Landmark { X = lm.x, Y = 1f - lm.y, Visibility = lm.visibility };
            }
            ApplyPose(landmarks, message.referenceLengthCm);
        }

        // Places the skeleton from a set of landmarks already in PassthroughCameraAccess's
        // viewport convention (see the Landmark struct). Called both from the WebRTC data
        // channel path above (after converting from MediaPipe's convention) and directly by
        // an on-device pose detector, if one is present.
        //
        // cachedCameraPose should be captured by the caller at the same moment the landmarks'
        // source texture was grabbed, not derived fresh in here - GetCameraPose() reflects
        // whatever the latest camera frame's timestamp is *right now*, which by the time this
        // runs (after however long detection/the data channel round-trip took) is no longer
        // the frame these landmarks came from. Falls back to a fresh pose only for the WebRTC
        // path above, which has no local frame-timestamp to cache against in the first place.
        public void ApplyPose(Landmark[] landmarks, float referenceLengthCm, Pose? cachedCameraPose = null) {
            if (landmarks == null || landmarks.Length != LandmarkCount) return;
            if (!cameraAccess || !cameraAccess.IsPlaying) return;
            if (_connectorRenderers == null || _jointSpheres == null) return; // BuildVisuals failed (see there)

            // Still "holding" the last placement - leave the currently displayed skeleton
            // exactly where it is rather than re-deriving it from this frame's (noisy) input.
            if (enablePoseHold && Time.unscaledTime < _nextPoseSampleTime) return;

            if (enableSmoothing) {
                var timestamp = Time.unscaledTime;
                for (var i = 0; i < LandmarkCount; i++) {
                    var lm = landmarks[i];
                    _smoothedLandmarks[i] = new Landmark {
                        X = _xFilters[i].Filter(lm.X, timestamp),
                        Y = _yFilters[i].Filter(lm.Y, timestamp),
                        Visibility = lm.Visibility
                    };
                }
                landmarks = _smoothedLandmarks;
            }

            var camPose = cachedCameraPose ?? cameraAccess.GetCameraPose();

            var shoulderMid = Average(landmarks[LeftShoulder], landmarks[RightShoulder]);
            var hipMid = Average(landmarks[LeftHip], landmarks[RightHip]);

            var shoulderRay = cameraAccess.ViewportPointToRay(new Vector2(shoulderMid.X, shoulderMid.Y), camPose);
            var hipRay = cameraAccess.ViewportPointToRay(new Vector2(hipMid.X, hipMid.Y), camPose);

            // Two rays diverging by angle theta, with points placed at equal distance D along
            // each, are separated by a chord of length 2*D*sin(theta/2) - solving for D given
            // the real reference length gives our single camera-to-puppet distance estimate.
            var angleRad = Vector3.Angle(shoulderRay.direction, hipRay.direction) * Mathf.Deg2Rad;
            if (angleRad < 0.0005f) return; // too small to get a stable estimate from - skip this frame

            var referenceLengthMeters = referenceLengthCm / 100f;
            var distance = referenceLengthMeters / (2f * Mathf.Sin(angleRad / 2f));
            distance = Mathf.Clamp(distance, 0.05f, 5f); // guard against a bad estimate flinging the skeleton away

            var worldPositions = new Vector3[LandmarkCount];
            var visible = new bool[LandmarkCount];
            for (var i = 0; i < LandmarkCount; i++) {
                var lm = landmarks[i];
                visible[i] = lm.Visibility >= minVisibility;
                var ray = cameraAccess.ViewportPointToRay(new Vector2(lm.X, lm.Y), camPose);
                worldPositions[i] = ray.GetPoint(distance);
            }

            for (var i = 0; i < Connections.Length; i++) {
                var (from, to) = Connections[i];
                var lr = _connectorRenderers[i];
                var show = visible[from] && visible[to];
                lr.enabled = show;
                if (show) {
                    lr.SetPosition(0, worldPositions[from]);
                    lr.SetPosition(1, worldPositions[to]);
                }
            }

            for (var i = 0; i < LandmarkCount; i++) {
                _jointSpheres[i].gameObject.SetActive(visible[i]);
                if (visible[i]) {
                    _jointSpheres[i].position = worldPositions[i];
                }
            }

            if (enablePoseHold) {
                _nextPoseSampleTime = Time.unscaledTime + PoseHoldSeconds;
            }
        }

        // Hides the whole skeleton - e.g. when an on-device detector loses tracking. Also
        // resets the smoothing filters, so a fresh detection after a gap doesn't get pulled
        // toward wherever the pose was before it was lost.
        public void ClearPose() {
            if (_connectorRenderers == null || _jointSpheres == null) return; // BuildVisuals failed (see there)

            foreach (var lr in _connectorRenderers) lr.enabled = false;
            foreach (var joint in _jointSpheres) joint.gameObject.SetActive(false);

            foreach (var filter in _xFilters) filter.Reset();
            foreach (var filter in _yFilters) filter.Reset();

            // A fresh acquisition after a real gap must be placed immediately, not wait out
            // whatever hold window was still running before tracking was lost.
            _nextPoseSampleTime = 0f;
        }

        private static Landmark Average(Landmark a, Landmark b) {
            return new Landmark {
                X = (a.X + b.X) * 0.5f,
                Y = (a.Y + b.Y) * 0.5f,
                Visibility = Mathf.Min(a.Visibility, b.Visibility)
            };
        }
    }
}
