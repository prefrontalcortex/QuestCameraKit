// Runs MediaPipe Tasks Vision's PoseLandmarker against the already-playing
// <video> element and draws the detected skeleton onto an overlay <canvas>.
// Loaded lazily (dynamic import from client.js) so the ~35MB WASM runtime is
// only fetched once pose detection is actually switched on.
//
// Assets are served locally (see server.js static routes) instead of from a
// CDN, so this works on a LAN without internet access.

let visionModulePromise = null;
function loadVisionModule() {
  if (!visionModulePromise) {
    visionModulePromise = import('./vendor/mediapipe/vision_bundle.mjs');
  }
  return visionModulePromise;
}

export class PoseOverlay {
  constructor(videoEl, canvasEl) {
    this.video = videoEl;
    this.canvas = canvasEl;
    this.ctx = canvasEl.getContext('2d');
    this.landmarker = null;
    this.drawingUtils = null;
    this.poseConnections = null;
    this.running = false;
    this.rafHandle = null;
    this.loop = this.loop.bind(this);
  }

  async start(onLog, onResult) {
    if (this.running) return;
    this.running = true;
    this.onResult = onResult;

    const { PoseLandmarker, FilesetResolver, DrawingUtils } = await loadVisionModule();
    onLog?.('Loading pose model...');

    const vision = await FilesetResolver.forVisionTasks('/vendor/mediapipe/wasm');
    const landmarker = await PoseLandmarker.createFromOptions(vision, {
      baseOptions: {
        modelAssetPath: '/models/pose_landmarker_lite.task',
        delegate: 'CPU', // CPU is slower than GPU but avoids WebGL-context setup entirely - safer default for a first pass
      },
      runningMode: 'VIDEO',
      numPoses: 1,
    });

    if (!this.running) {
      // stop() was called while the model was still loading.
      landmarker.close();
      return;
    }

    this.landmarker = landmarker;
    this.poseConnections = PoseLandmarker.POSE_CONNECTIONS;
    this.drawingUtils = new DrawingUtils(this.ctx);
    onLog?.('Pose model ready.');
    this.loop();
  }

  stop() {
    this.running = false;
    if (this.rafHandle) cancelAnimationFrame(this.rafHandle);
    this.rafHandle = null;
    this.ctx.clearRect(0, 0, this.canvas.width, this.canvas.height);
    this.landmarker?.close();
    this.landmarker = null;
  }

  loop() {
    if (!this.running) return;
    this.rafHandle = requestAnimationFrame(this.loop);
    this.detectAndDraw();
  }

  detectAndDraw() {
    if (!this.landmarker || this.video.readyState < 2) return;

    const { videoWidth, videoHeight } = this.video;
    if (videoWidth === 0 || videoHeight === 0) return;
    if (this.canvas.width !== videoWidth || this.canvas.height !== videoHeight) {
      this.canvas.width = videoWidth;
      this.canvas.height = videoHeight;
    }

    const result = this.landmarker.detectForVideo(this.video, performance.now());
    this.ctx.clearRect(0, 0, this.canvas.width, this.canvas.height);
    for (const landmarks of result.landmarks) {
      this.drawingUtils.drawConnectors(landmarks, this.poseConnections, { color: '#5b8cff', lineWidth: 3 });
      this.drawingUtils.drawLandmarks(landmarks, { color: '#ffffff', fillColor: '#ffffff', radius: 3 });
    }

    // Only the first detected pose is forwarded - numPoses is 1 anyway (see createFromOptions above).
    if (result.landmarks[0]) this.onResult?.(result.landmarks[0]);
  }
}
