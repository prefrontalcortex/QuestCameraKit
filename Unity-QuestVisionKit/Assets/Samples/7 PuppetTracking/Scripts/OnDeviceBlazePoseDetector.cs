using Meta.XR;
using System;
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
        [SerializeField] private float referenceLengthCm = 20f;
        // MediaPipe Tasks Vision's own PoseLandmarker defaults minPoseDetectionConfidence to
        // 0.5 (our browser-side pose-detection.js never overrides it) - Unity's reference
        // sample this class is ported from used a much stricter 0.75, which was silently
        // making the on-device path harder to trigger than the browser path it's compared
        // against for no reason related to the puppet itself.
        [SerializeField] private float scoreThreshold = 0.5f;
        [SerializeField] private BackendType backend = BackendType.GPUCompute;

        // Once a pose is found, later frames skip the person detector entirely and derive the
        // next crop directly from the landmark model's own two alignment keypoints (indices 33
        // and 34, beyond the 33 body joints - see https://arxiv.org/pdf/2006.10204) - this is
        // what MediaPipe's own runtime does to stay stable across frames, and the ported
        // reference sample this class is based on skipped:
        // it re-ran the full-frame detector every single frame, which is both slower and far
        // less reliable at typical distances since a puppet (unlike a framed photo of a person)
        // only fills a small fraction of the passthrough camera's wide field of view.
        [SerializeField] private float minTrackingConfidence = 0.5f;

        // BlazePose's "image space" output (see the landmark loop in DetectOnce below) is
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

        private bool _isTracking;
        private float2 _trackedKp1ImageSpace;
        private float2 _trackedKp2ImageSpace;

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
            // Post-process the model to filter scores + argmax select the best pose candidate
            // on the GPU, so only three small tensors need reading back instead of the full
            // (1, 2254, 12) box tensor every frame.
            var graph = new FunctionalGraph();
            var input = graph.AddInput(detectorModel, 0);
            var outputs = Functional.Forward(detectorModel, input);
            var idxScoresBoxes = BlazePoseUtils.ArgMaxFiltering(outputs[0], outputs[1]);
            detectorModel = graph.Compile(idxScoresBoxes.Item1, idxScoresBoxes.Item2, idxScoresBoxes.Item3);
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

            float2 kp1ImageSpace, kp2ImageSpace;
            if (_isTracking) {
                // Skip the person detector entirely - reuse where the landmark model itself
                // last said its two alignment points were.
                Mode = "Tracking";
                kp1ImageSpace = _trackedKp1ImageSpace;
                kp2ImageSpace = _trackedKp2ImageSpace;
            } else {
                Mode = "Detecting";
                var detection = await TryDetectPerson(width, height);
                if (!detection.success) {
                    visualizer?.ClearPose();
                    return;
                }
                kp1ImageSpace = detection.kp1;
                kp2ImageSpace = detection.kp2;
            }

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
                var visibility = landmarks[5 * i + 3];
                var presence = landmarks[5 * i + 4];

                var y = positionImageSpace.y / height;
                _landmarks[i] = new PuppetPoseVisualizer.Landmark {
                    X = positionImageSpace.x / width,
                    Y = flipY ? 1f - y : y,
                    Visibility = Mathf.Min(visibility, presence),
                };
            }

            // Landmarks 33 and 34 (beyond the 33 body joints in the 39-point output) are the
            // model's own re-predicted alignment keypoints - their confidence is the signal for
            // whether the tracked crop is still centered on something real, independent of how
            // much of the body itself happens to be visible right now.
            var trackingConfidence = Mathf.Min(landmarks[5 * 33 + 3], landmarks[5 * 33 + 4], landmarks[5 * 34 + 3], landmarks[5 * 34 + 4]);
            LastTrackingConfidence = trackingConfidence;
            _isTracking = trackingConfidence >= minTrackingConfidence;
            if (_isTracking) {
                _trackedKp1ImageSpace = BlazePoseUtils.mul(m2, new float2(landmarks[5 * 33 + 0], landmarks[5 * 33 + 1]));
                _trackedKp2ImageSpace = BlazePoseUtils.mul(m2, new float2(landmarks[5 * 34 + 0], landmarks[5 * 34 + 1]));
            }

            visualizer?.ApplyPose(_landmarks, referenceLengthCm);
        }

        // Runs the full-frame person detector to (re-)acquire the two alignment keypoints that
        // seed the landmarker's crop, for when there's no tracked pose to derive them from yet.
        // (async methods can't have out/ref parameters, hence the tuple return.)
        private async Awaitable<(bool success, float2 kp1, float2 kp2)> TryDetectPerson(int width, int height) {
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

            var idxAwaitable = (_detectorWorker.PeekOutput(0) as Tensor<int>).ReadbackAndCloneAsync();
            var scoreAwaitable = (_detectorWorker.PeekOutput(1) as Tensor<float>).ReadbackAndCloneAsync();
            var boxAwaitable = (_detectorWorker.PeekOutput(2) as Tensor<float>).ReadbackAndCloneAsync();

            using var outputIdx = await idxAwaitable;
            using var outputScore = await scoreAwaitable;
            using var outputBox = await boxAwaitable;

            LastDetectionScore = outputScore[0];
            if (outputScore[0] < scoreThreshold) {
                return (false, default, default);
            }

            var idx = outputIdx[0];
            var anchorPosition = DetectorInputSize * new float2(_anchors[idx, 0], _anchors[idx, 1]);

            var kp1ImageSpace = BlazePoseUtils.mul(m, anchorPosition + new float2(outputBox[0, 0, 4], outputBox[0, 0, 5]));
            var kp2ImageSpace = BlazePoseUtils.mul(m, anchorPosition + new float2(outputBox[0, 0, 6], outputBox[0, 0, 7]));
            return (true, kp1ImageSpace, kp2ImageSpace);
        }

        private void OnDestroy() {
            _detectLoop?.Cancel();
        }
    }
}
