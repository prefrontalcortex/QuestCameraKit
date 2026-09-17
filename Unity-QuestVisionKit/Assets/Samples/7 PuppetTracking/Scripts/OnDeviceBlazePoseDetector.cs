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
        [SerializeField] private float scoreThreshold = 0.75f;
        [SerializeField] private BackendType backend = BackendType.GPUCompute;

        // BlazePose's "image space" output (see ApplyLandmarks below) is expected to already
        // match PassthroughCameraAccess's viewport convention (origin bottom-left, y up), the
        // opposite of MediaPipe's browser-side output - flip this if the on-device skeleton
        // comes out upside down, rather than re-deriving the convention from scratch.
        [SerializeField] private bool flipY;

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
            var size = Mathf.Max(width, height);

            // The affine transformation matrix to go from detector-tensor coordinates to
            // image coordinates.
            var scale = size / (float)DetectorInputSize;
            var m = BlazePoseUtils.mul(
                BlazePoseUtils.TranslationMatrix(0.5f * (new float2(width, height) + new float2(-size, size))),
                BlazePoseUtils.ScaleMatrix(new float2(scale, -scale)));
            BlazePoseUtils.SampleImageAffine(texture, _detectorInput, m);

            _detectorWorker.Schedule(_detectorInput);

            var idxAwaitable = (_detectorWorker.PeekOutput(0) as Tensor<int>).ReadbackAndCloneAsync();
            var scoreAwaitable = (_detectorWorker.PeekOutput(1) as Tensor<float>).ReadbackAndCloneAsync();
            var boxAwaitable = (_detectorWorker.PeekOutput(2) as Tensor<float>).ReadbackAndCloneAsync();

            using var outputIdx = await idxAwaitable;
            using var outputScore = await scoreAwaitable;
            using var outputBox = await boxAwaitable;

            if (outputScore[0] < scoreThreshold) {
                visualizer?.ClearPose();
                return;
            }

            var idx = outputIdx[0];
            var anchorPosition = DetectorInputSize * new float2(_anchors[idx, 0], _anchors[idx, 1]);

            var kp1ImageSpace = BlazePoseUtils.mul(m, anchorPosition + new float2(outputBox[0, 0, 4], outputBox[0, 0, 5]));
            var kp2ImageSpace = BlazePoseUtils.mul(m, anchorPosition + new float2(outputBox[0, 0, 6], outputBox[0, 0, 7]));
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

            visualizer?.ApplyPose(_landmarks, referenceLengthCm);
        }

        private void OnDestroy() {
            _detectLoop?.Cancel();
        }
    }
}
