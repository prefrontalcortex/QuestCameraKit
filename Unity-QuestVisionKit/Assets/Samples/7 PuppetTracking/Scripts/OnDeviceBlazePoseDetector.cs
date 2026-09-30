using Meta.XR;
using System;
using System.Collections.Generic;
using Unity.InferenceEngine;
using Unity.Mathematics;
using UnityEngine;

namespace QuestCameraKit.WebRTC {
    // Experiment: runs Google's BlazePose (the same model MediaPipe's PoseLandmarker uses in
    // the browser, converted to ONNX by Unity - https://huggingface.co/unity/inference-engine-blaze-pose)
    // directly on-device against the passthrough camera texture, instead of round-tripping
    // video to a PC over WebRTC for browser-side detection. Feeds the same PuppetPoseVisualizer
    // the WebRTC path uses, so the two approaches can be compared head-to-head by swapping
    // which component is enabled.
    //
    // Ported from Unity's own sentis-samples BlazeDetectionSample
    // (Pose/Assets/Scripts/PoseDetection.cs, https://github.com/Unity-Technologies/sentis-samples),
    // adapted from a static test image to a live PassthroughCameraAccess feed, and to hand
    // results to PuppetPoseVisualizer instead of driving its own preview scene.
    //
    // Model assets are loaded from Resources/Models/ (pose_detection.onnx,
    // pose_landmarks_detector_lite.onnx, anchors.csv) rather than wired via the Inspector -
    // Unity assigns these binary assets a GUID on first import that can't be predicted ahead
    // of time, so Resources.Load avoids needing a manual per-machine drag-and-drop step.
    public class OnDeviceBlazePoseDetector : MonoBehaviour {
        [SerializeField] private PassthroughCameraAccess cameraAccess;
        [SerializeField] private PuppetPoseVisualizer visualizer;

        // Default/fallback only - the value actually used every frame is ReferenceLengthCm
        // below, which starts here but is live-adjustable on-device (right thumbstick while
        // holding the right index trigger - see Update) since this can't be tuned in the
        // Inspector once built into an APK, and the correct value depends on whatever
        // puppet/mannequin is actually in front of the camera that session.
        [SerializeField] private float referenceLengthCm = 20f;
        private const string ReferenceLengthPrefsKey = "QuestCameraKit.PuppetTracking.ReferenceLengthCm";
        private const float ReferenceLengthMinCm = 3f;
        private const float ReferenceLengthMaxCm = 60f;
        private const float ReferenceLengthAdjustCmPerSecond = 10f;
        private const float ReferenceLengthAxisDeadzone = 0.15f;

        // The real-world shoulder-to-hip reference length (cm) used to convert 2D landmark
        // angles into an estimated camera-to-puppet distance (see PuppetPoseVisualizer.ApplyPose).
        // Persisted across sessions so a colleague testing with a different puppet only has to
        // calibrate once, not on every rebuild.
        public float ReferenceLengthCm { get; private set; }
        // MediaPipe Tasks Vision's own PoseLandmarker defaults minPoseDetectionConfidence to
        // 0.5, calibrated for real human-scale subjects - Unity's reference sample this class
        // is ported from used an even stricter 0.75. Neither is calibrated for a puppet: field
        // testing with detectionCandidateCount candidates showed the raw detector score never
        // exceeding ~0.51 even on a genuine detection, meaning 0.5 only ever let a roughly
        // 0.01-wide sliver of real detections through. Lowered accordingly; acquireConfirmFrames
        // above is what keeps a looser threshold from flooding in false positives.
        [SerializeField] private float scoreThreshold = 0.35f;
        [SerializeField] private BackendType backend = BackendType.GPUCompute;

        // How many of the highest-scoring anchors to actually try (via a full landmark-model
        // pass each) before giving up on a frame, instead of trusting only the single best-
        // scoring anchor - see BlazePoseUtils.ScoreAllAnchors for why that hurt recall. Higher
        // means better odds of finding the real subject when it isn't the single top-scoring
        // anchor, at the cost of up to this many landmark-model runs on a frame where nothing
        // is found (only while not already tracking - a confirmed track reuses its own crop and
        // never touches this).
        [SerializeField] private int detectionCandidateCount = 5;

        // Once a pose is found, later frames skip the person detector entirely and derive the
        // next crop directly from the landmark model's own two alignment keypoints (indices 33
        // and 34, beyond the 33 body joints - see https://arxiv.org/pdf/2006.10204) - this is
        // what MediaPipe's own runtime does to stay stable across frames, and the ported
        // reference sample this class is based on skipped:
        // it re-ran the full-frame detector every single frame, which is both slower and far
        // less reliable at typical distances since a puppet (unlike a framed photo of a person)
        // only fills a small fraction of the passthrough camera's wide field of view.
        //
        // Like scoreThreshold above, 0.5 was calibrated for real human subjects, not this
        // puppet: field testing showed LastTrackingConfidence topping out around 0.3-0.4 even
        // on a genuine track, so 0.5 never let tracking actually engage. Lowered to leave a
        // margin below that observed real range rather than right at its ceiling.
        [SerializeField] private float minTrackingConfidence = 0.25f;

        // A static subject (mannequin/puppet) does not truly leave the frame from one moment to
        // the next, so a single low-confidence frame is almost always detection noise, not a
        // real loss of track. Without this grace period, one noisy frame immediately dropped
        // tracking and fell back to the full-frame person detector - which the README already
        // documents as having poor recall on its own - producing a visible flicker: pose briefly
        // acquired, then gone and often not reacquired for many frames. Tuned in frames, not
        // seconds, since it must scale with however fast DetectOnce actually runs on-device.
        [SerializeField] private int lostTrackingGraceFrames = 15;

        // Mirror image of the grace period above: a single noisy frame can also cause a false
        // *acquisition* (a spurious detection that happens to clear scoreThreshold and
        // minTrackingConfidence once), which the grace period then held onto for many frames as
        // a visible wrong skeleton. Require this many consecutive good frames on the same
        // tracked crop before ever calling ApplyPose - a real, physically-present subject stays
        // consistent frame to frame; noise usually does not.
        [SerializeField] private int acquireConfirmFrames = 3;

        // BlazePose's "image space" output (see the landmark loop in TryLandmarksAt below) is
        // expected to already match PassthroughCameraAccess's viewport convention (origin
        // bottom-left, y up), the
        // opposite of MediaPipe's browser-side output - flip this if the on-device skeleton
        // comes out upside down, rather than re-deriving the convention from scratch.
        [SerializeField] private bool flipY;

        // Exposed so PuppetTrackingStatusHud can show what the pipeline is actually doing right
        // now - without this, "sometimes visible, sometimes not" is impossible to debug further:
        // failing to ever detect (LastDetectionScore never crosses scoreThreshold) and losing an
        // acquired track too eagerly (LastTrackingConfidence dropping below minTrackingConfidence)
        // look identical from the outside but need very different fixes.
        public string Mode { get; private set; } = "Idle";
        public float LastDetectionScore { get; private set; }
        public float LastTrackingConfidence { get; private set; }
        public float ScoreThreshold => scoreThreshold;
        public float MinTrackingConfidence => minTrackingConfidence;

        // Raw (pre-sigmoid) logits behind LastTrackingConfidence's four inputs, for the last
        // landmark pass attempted regardless of success - temporary diagnostics while tracking
        // confidence reads as ~0 even with sigmoid applied and a strong detection score.
        public float LastRawVisibility33 { get; private set; }
        public float LastRawPresence33 { get; private set; }
        public float LastRawVisibility34 { get; private set; }
        public float LastRawPresence34 { get; private set; }

        private const int NumAnchors = 2254;
        private const int NumKeypoints = 33;
        private const int DetectorInputSize = 224;
        private const int LandmarkerInputSize = 256;

        private float[,] _anchors;
        private Worker _detectorWorker;
        private Worker _landmarkerWorker;
        private Tensor<float> _detectorInput;
        private Tensor<float> _landmarkerInput;
        private Awaitable _detectLoop;
        private PuppetPoseVisualizer.Landmark[] _landmarks;

        private bool _isTracking; // has a candidate crop worth re-evaluating (confirmed or not)
        private bool _confirmed; // candidate has passed acquireConfirmFrames - safe to display
        private float2 _trackedKp1ImageSpace;
        private float2 _trackedKp2ImageSpace;
        private int _lowConfidenceStreak;
        private int _highConfidenceStreak;

        private void Awake() {
            ReferenceLengthCm = PlayerPrefs.HasKey(ReferenceLengthPrefsKey)
                ? PlayerPrefs.GetFloat(ReferenceLengthPrefsKey)
                : referenceLengthCm;
        }

        // Runs every frame regardless of the async detect loop's own cadence below, so the
        // adjustment feels responsive. Right thumbstick while holding the right index trigger -
        // not B, which is also a right-thumb control and unusable together with the thumbstick
        // (same thumb, both hands full). The trigger is the index finger, independent of the
        // thumb on the stick. Left thumbstick/X/Y are already SampleMenu's navigation controls.
        private void Update() {
            if (!OVRInput.Get(OVRInput.RawButton.RIndexTrigger)) return;
            var axis = OVRInput.Get(OVRInput.RawAxis2D.RThumbstick).y;
            if (Mathf.Abs(axis) < ReferenceLengthAxisDeadzone) return;

            ReferenceLengthCm = Mathf.Clamp(
                ReferenceLengthCm + axis * ReferenceLengthAdjustCmPerSecond * Time.deltaTime,
                ReferenceLengthMinCm, ReferenceLengthMaxCm);
            PlayerPrefs.SetFloat(ReferenceLengthPrefsKey, ReferenceLengthCm);
        }

        private async void Start() {
            if (!cameraAccess) {
                cameraAccess = FindAnyObjectByType<PassthroughCameraAccess>(FindObjectsInactive.Include);
            }
            if (!visualizer) {
                visualizer = FindAnyObjectByType<PuppetPoseVisualizer>(FindObjectsInactive.Include);
            }

            var anchorsCsv = Resources.Load<TextAsset>("Models/anchors");
            var detectorAsset = Resources.Load<ModelAsset>("Models/pose_detection");
            var landmarkerAsset = Resources.Load<ModelAsset>("Models/pose_landmarks_detector_lite");
            if (anchorsCsv == null || detectorAsset == null || landmarkerAsset == null) {
                Debug.LogError("[OnDeviceBlazePoseDetector] Missing model assets under Resources/Models/ - see the root README.md's 'On-device pose experiment' section for download URLs.");
                enabled = false;
                return;
            }

            _anchors = BlazePoseUtils.LoadAnchors(anchorsCsv.text, NumAnchors);
            _landmarks = new PuppetPoseVisualizer.Landmark[NumKeypoints];

            var detectorModel = ModelLoader.Load(detectorAsset);
            // Score every anchor on the GPU (cheap) but no longer pick a single "best" one here -
            // see BlazePoseUtils.ScoreAllAnchors for why the old single-anchor ArgMax approach
            // hurt recall. DetectCandidates below reads back the full scored anchor set instead.
            var graph = new FunctionalGraph();
            var input = graph.AddInput(detectorModel, 0);
            var outputs = Functional.Forward(detectorModel, input);
            var scoresAndBoxes = BlazePoseUtils.ScoreAllAnchors(outputs[0], outputs[1]);
            detectorModel = graph.Compile(scoresAndBoxes.Item1, scoresAndBoxes.Item2);
            _detectorWorker = new Worker(detectorModel, backend);

            var landmarkerModel = ModelLoader.Load(landmarkerAsset);
            _landmarkerWorker = new Worker(landmarkerModel, backend);

            _detectorInput = new Tensor<float>(new TensorShape(1, DetectorInputSize, DetectorInputSize, 3));
            _landmarkerInput = new Tensor<float>(new TensorShape(1, LandmarkerInputSize, LandmarkerInputSize, 3));

            while (true) {
                try {
                    _detectLoop = DetectOnce();
                    await _detectLoop;
                } catch (OperationCanceledException) {
                    break;
                }
            }

            _detectorWorker.Dispose();
            _landmarkerWorker.Dispose();
            _detectorInput.Dispose();
            _landmarkerInput.Dispose();
        }

        private async Awaitable DetectOnce() {
            if (!cameraAccess || !cameraAccess.IsPlaying) {
                await Awaitable.NextFrameAsync();
                return;
            }

            var texture = cameraAccess.GetTexture();
            if (texture == null) {
                await Awaitable.NextFrameAsync();
                return;
            }

            var width = texture.width;
            var height = texture.height;
            var wasTracking = _isTracking; // capture before any mutation below

            bool success;
            float confidence;
            float2 refinedKp1 = default, refinedKp2 = default;

            if (wasTracking) {
                // Skip the person detector entirely - reuse where the landmark model itself
                // last said its two alignment points were.
                Mode = _confirmed ? "Tracking" : "Acquiring";
                (success, confidence, refinedKp1, refinedKp2) =
                    await TryLandmarksAt(_trackedKp1ImageSpace, _trackedKp2ImageSpace, texture, width, height);
            } else {
                Mode = "Detecting";
                success = false;
                confidence = 0f;
                // Try each of the top-scoring anchors in turn, not just the single best one -
                // see BlazePoseUtils.ScoreAllAnchors for why. Stop at the first one whose crop
                // the landmark model itself also finds convincing.
                foreach (var candidate in await DetectCandidates(width, height)) {
                    var result = await TryLandmarksAt(candidate.kp1, candidate.kp2, texture, width, height);
                    // Keep the best confidence seen even on failure - otherwise a near-miss
                    // candidate (e.g. 0.3 against a 0.5 threshold) is invisible in the HUD,
                    // reported as a flat 0 instead of the real, merely-insufficient value.
                    confidence = Mathf.Max(confidence, result.confidence);
                    if (!result.success) continue;
                    success = true;
                    refinedKp1 = result.kp1;
                    refinedKp2 = result.kp2;
                    break;
                }
            }

            LastTrackingConfidence = confidence;
            if (success) {
                _lowConfidenceStreak = 0;
                _highConfidenceStreak++;
                _isTracking = true;
                _trackedKp1ImageSpace = refinedKp1;
                _trackedKp2ImageSpace = refinedKp2;
                if (_highConfidenceStreak >= acquireConfirmFrames) {
                    _confirmed = true;
                }
            } else {
                // Keep reusing the last confident crop (_trackedKp*ImageSpace stays untouched)
                // rather than updating it from this noisy frame, and rather than immediately
                // giving up - see lostTrackingGraceFrames above. Only applies when decaying an
                // existing track; a Detecting-mode frame that found nothing at all was never
                // tracking to begin with and must not be coaxed into "tracking" by this alone.
                _highConfidenceStreak = 0;
                _lowConfidenceStreak++;
                _isTracking = wasTracking && _lowConfidenceStreak < lostTrackingGraceFrames;
            }
            if (!_isTracking) {
                _confirmed = false;
            }

            // Only ever show a track once it has survived acquireConfirmFrames in a row - a
            // one-off spurious detection never reaches this and is never drawn at all.
            if (_confirmed) {
                visualizer?.ApplyPose(_landmarks, ReferenceLengthCm);
            } else {
                visualizer?.ClearPose();
            }
        }

        // Runs the landmark model against a candidate crop (either the reused tracked crop, or
        // one of this frame's freshly detected candidates) and reports whether it looks like a
        // real, confidently-aligned subject. Always fills _landmarks with this attempt's result;
        // the caller only actually displays it once success is true and acquireConfirmFrames is
        // satisfied.
        private async Awaitable<(bool success, float confidence, float2 kp1, float2 kp2)> TryLandmarksAt(
            float2 kp1ImageSpace, float2 kp2ImageSpace, Texture texture, int width, int height) {
            var deltaImageSpace = kp2ImageSpace - kp1ImageSpace;
            const float dscale = 1.25f;
            var radius = dscale * math.length(deltaImageSpace);
            var theta = math.atan2(deltaImageSpace.y, deltaImageSpace.x);
            var origin2 = new float2(0.5f * LandmarkerInputSize, 0.5f * LandmarkerInputSize);
            var scale2 = radius / (0.5f * LandmarkerInputSize);
            var m2 = BlazePoseUtils.mul(BlazePoseUtils.mul(BlazePoseUtils.mul(
                    BlazePoseUtils.TranslationMatrix(kp1ImageSpace),
                    BlazePoseUtils.ScaleMatrix(new float2(scale2, -scale2))),
                BlazePoseUtils.RotationMatrix(0.5f * Mathf.PI - theta)),
                BlazePoseUtils.TranslationMatrix(-origin2));
            BlazePoseUtils.SampleImageAffine(texture, _landmarkerInput, m2);

            _landmarkerWorker.Schedule(_landmarkerInput);
            var landmarksAwaitable = (_landmarkerWorker.PeekOutput("Identity") as Tensor<float>).ReadbackAndCloneAsync();
            using var landmarks = await landmarksAwaitable; // (1, 195) = 39 landmarks x 5 (x, y, z, visibility, presence)

            for (var i = 0; i < NumKeypoints; i++) {
                var positionImageSpace = BlazePoseUtils.mul(m2, new float2(landmarks[5 * i + 0], landmarks[5 * i + 1]));
                // The model outputs visibility/presence as raw logits, not 0-1 probabilities -
                // MediaPipe's own runtime applies a sigmoid before exposing them, which this port
                // never did. Comparing a raw logit (routinely negative) directly against a 0-1
                // threshold like minTrackingConfidence/minVisibility made tracking confidence
                // read as ~0 even on a good detection - the actual bug behind "det high, track
                // flat 0" and the detector's apparent recall problem in general.
                var visibility = Sigmoid(landmarks[5 * i + 3]);
                var presence = Sigmoid(landmarks[5 * i + 4]);

                var y = positionImageSpace.y / height;
                _landmarks[i] = new PuppetPoseVisualizer.Landmark {
                    X = positionImageSpace.x / width,
                    Y = flipY ? 1f - y : y,
                    Visibility = Mathf.Min(visibility, presence),
                };
            }

            // Landmarks 33 and 34 (beyond the 33 body joints in the 39-point output) are the
            // model's own re-predicted alignment keypoints - synthetic points the model uses to
            // seed next frame's crop, not real anatomical joints. Their "visibility" (body-part
            // occlusion) is meaningless for a point that isn't a body part - field testing found
            // it pinned at a strongly negative logit (~-17, i.e. ~0 after sigmoid) regardless of
            // how good the detection otherwise was, while "presence" (was anything usable found
            // here at all) stayed strongly positive (~+17, ~1). Mixing the two via Min() was
            // dragging tracking confidence to ~0 unconditionally - presence alone is the
            // meaningful signal for these two points specifically.
            LastRawVisibility33 = landmarks[5 * 33 + 3];
            LastRawPresence33 = landmarks[5 * 33 + 4];
            LastRawVisibility34 = landmarks[5 * 34 + 3];
            LastRawPresence34 = landmarks[5 * 34 + 4];
            var trackingConfidence = Mathf.Min(Sigmoid(LastRawPresence33), Sigmoid(LastRawPresence34));
            var refinedKp1 = BlazePoseUtils.mul(m2, new float2(landmarks[5 * 33 + 0], landmarks[5 * 33 + 1]));
            var refinedKp2 = BlazePoseUtils.mul(m2, new float2(landmarks[5 * 34 + 0], landmarks[5 * 34 + 1]));
            return (trackingConfidence >= minTrackingConfidence, trackingConfidence, refinedKp1, refinedKp2);
        }

        // Runs the full-frame person detector once and returns up to detectionCandidateCount
        // candidate crops, highest-scoring first, for when there's no tracked pose to derive a
        // crop from yet. Each is only a raw-detector-score guess; TryLandmarksAt above is what
        // actually validates one.
        private async Awaitable<List<(float2 kp1, float2 kp2, float score)>> DetectCandidates(int width, int height) {
            // Crop to the SHORTER side (centered) rather than padding out to the longer one -
            // the passthrough frame is wide (e.g. 1280x960), and padding to a 1280x1280 square
            // wastes a third of the 224x224 detector input on empty letterbox bars, making an
            // already-small puppet even smaller by the time the model sees it. Cropping loses
            // some peripheral field of view instead, which is the better trade here since
            // whoever is holding the puppet keeps it roughly centered anyway.
            var size = Mathf.Min(width, height);

            // The affine transformation matrix to go from detector-tensor coordinates to
            // image coordinates.
            var scale = size / (float)DetectorInputSize;
            var m = BlazePoseUtils.mul(
                BlazePoseUtils.TranslationMatrix(0.5f * (new float2(width, height) + new float2(-size, size))),
                BlazePoseUtils.ScaleMatrix(new float2(scale, -scale)));
            BlazePoseUtils.SampleImageAffine(cameraAccess.GetTexture(), _detectorInput, m);

            _detectorWorker.Schedule(_detectorInput);

            var scoresAwaitable = (_detectorWorker.PeekOutput(0) as Tensor<float>).ReadbackAndCloneAsync(); // (1, NumAnchors, 1)
            var boxesAwaitable = (_detectorWorker.PeekOutput(1) as Tensor<float>).ReadbackAndCloneAsync(); // (1, NumAnchors, 12)

            using var outputScores = await scoresAwaitable;
            using var outputBoxes = await boxesAwaitable;

            var bestIndices = TopScoringAnchorIndices(outputScores, detectionCandidateCount);
            LastDetectionScore = bestIndices.Count > 0 ? outputScores[0, bestIndices[0], 0] : 0f;

            var candidates = new List<(float2, float2, float)>(bestIndices.Count);
            foreach (var idx in bestIndices) {
                var score = outputScores[0, idx, 0];
                if (score < scoreThreshold) continue;

                var anchorPosition = DetectorInputSize * new float2(_anchors[idx, 0], _anchors[idx, 1]);
                var kp1ImageSpace = BlazePoseUtils.mul(m, anchorPosition + new float2(outputBoxes[0, idx, 4], outputBoxes[0, idx, 5]));
                var kp2ImageSpace = BlazePoseUtils.mul(m, anchorPosition + new float2(outputBoxes[0, idx, 6], outputBoxes[0, idx, 7]));
                candidates.Add((kp1ImageSpace, kp2ImageSpace, score));
            }
            return candidates;
        }

        private static float Sigmoid(float logit) => 1f / (1f + Mathf.Exp(-logit));

        // Small partial top-k selection over NumAnchors (2254) scores - cheap on the CPU, no
        // need for a full sort or a GPU-side op for such a small k.
        private static List<int> TopScoringAnchorIndices(Tensor<float> scores, int k) {
            var keptScores = new List<float>(k);
            var keptIndices = new List<int>(k);
            for (var i = 0; i < NumAnchors; i++) {
                var score = scores[0, i, 0];
                var insertAt = 0;
                while (insertAt < keptScores.Count && keptScores[insertAt] >= score) insertAt++;
                if (insertAt >= k) continue;

                keptScores.Insert(insertAt, score);
                keptIndices.Insert(insertAt, i);
                if (keptScores.Count > k) {
                    keptScores.RemoveAt(k);
                    keptIndices.RemoveAt(k);
                }
            }
            return keptIndices;
        }

        private void OnDestroy() {
            _detectLoop?.Cancel();
        }
    }
}
