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
        // The tracked subject here is a mannequin/puppet, not a live moving person - it doesn't
        // move on its own, so beta (which relaxes smoothing when the filter senses real motion)
        // has nothing genuine to relax for: any apparent "velocity" in a static prop's landmarks
        // is just detection noise, not motion worth staying responsive to. Head movement is
        // already handled separately and correctly by ray-casting through the current camera
        // pose each frame (see ApplyPose) - a static prop should land at a stable world position
        // regardless of where the headset is looking from, as long as the 2D detection itself is
        // stable. So beta is tuned near zero and minCutoff low: prioritize a rock-steady pose
        // over reacting quickly, since there's no real motion here to react to.
        [SerializeField] private bool enableSmoothing = true;
        [SerializeField] private float smoothingMinCutoff = 0.25f;
        [SerializeField] private float smoothingBeta = 0.02f;
        [SerializeField] private float smoothingDerivativeCutoff = 1f;

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

            BuildVisuals();
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
            var material = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { color = skeletonColor };

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
        public void ApplyPose(Landmark[] landmarks, float referenceLengthCm) {
            if (landmarks == null || landmarks.Length != LandmarkCount) return;
            if (!cameraAccess || !cameraAccess.IsPlaying) return;

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

            var camPose = cameraAccess.GetCameraPose();

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
        }

        // Hides the whole skeleton - e.g. when an on-device detector loses tracking. Also
        // resets the smoothing filters, so a fresh detection after a gap doesn't get pulled
        // toward wherever the pose was before it was lost.
        public void ClearPose() {
            foreach (var lr in _connectorRenderers) lr.enabled = false;
            foreach (var joint in _jointSpheres) joint.gameObject.SetActive(false);

            foreach (var filter in _xFilters) filter.Reset();
            foreach (var filter in _yFilters) filter.Reset();
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
