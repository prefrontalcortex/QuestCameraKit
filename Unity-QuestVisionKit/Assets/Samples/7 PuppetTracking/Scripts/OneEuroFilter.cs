namespace QuestCameraKit.WebRTC {
    // The "1€ Filter" (Casiez, Roussel, Vogel - https://cristal.univ-lille.fr/~casiez/1euro/) -
    // the same adaptive smoothing technique MediaPipe's own landmark trackers use to stay
    // stable when a subject is nearly still while not lagging noticeably during real motion.
    // A plain low-pass filter has to pick one fixed cutoff frequency up front: too low and it
    // lags behind fast movement, too high and it barely smooths anything. This filter instead
    // derives the cutoff each sample from the signal's own estimated velocity, so it can smooth
    // hard when velocity is low (reducing static jitter) and relax when velocity is high
    // (staying responsive to real movement) - exactly the "detected but shaky" symptom this
    // was added for, distinct from and unrelated to whether detection succeeds in the first place.
    public sealed class OneEuroFilter {
        private readonly float _minCutoff;
        private readonly float _beta;
        private readonly float _derivativeCutoff;

        private readonly LowPassFilter _valueFilter = new LowPassFilter();
        private readonly LowPassFilter _derivativeFilter = new LowPassFilter();
        private float? _lastTimestamp;

        // minCutoff: baseline smoothing strength while the signal is roughly still - lower
        //   values smooth more but add more lag. Typical range 0.5-2 (Hz-ish).
        // beta: how much a rising velocity relaxes the cutoff - higher values track fast
        //   motion more faithfully at the cost of a bit more jitter during that motion.
        // derivativeCutoff: smooths the velocity estimate itself; 1 is the paper's default and
        //   rarely needs changing.
        public OneEuroFilter(float minCutoff = 1f, float beta = 0.3f, float derivativeCutoff = 1f) {
            _minCutoff = minCutoff;
            _beta = beta;
            _derivativeCutoff = derivativeCutoff;
        }

        public void Reset() {
            _valueFilter.Reset();
            _derivativeFilter.Reset();
            _lastTimestamp = null;
        }

        public float Filter(float value, float timestampSeconds) {
            var dt = _lastTimestamp.HasValue ? timestampSeconds - _lastTimestamp.Value : 1f / 30f;
            if (dt <= 0f) dt = 1f / 30f; // guard against a non-monotonic or duplicate timestamp
            _lastTimestamp = timestampSeconds;

            var derivative = _valueFilter.HasLastRawValue ? (value - _valueFilter.LastRawValue) / dt : 0f;
            var smoothedDerivative = _derivativeFilter.Filter(derivative, Alpha(_derivativeCutoff, dt));

            var cutoff = _minCutoff + _beta * System.Math.Abs(smoothedDerivative);
            return _valueFilter.Filter(value, Alpha(cutoff, dt));
        }

        private static float Alpha(float cutoff, float dt) {
            var tau = 1f / (2f * System.MathF.PI * cutoff);
            return 1f / (1f + tau / dt);
        }

        private sealed class LowPassFilter {
            private float _storedValue;
            public float LastRawValue { get; private set; }
            public bool HasLastRawValue { get; private set; }

            public float Filter(float value, float alpha) {
                var result = HasLastRawValue ? alpha * value + (1f - alpha) * _storedValue : value;
                LastRawValue = value;
                _storedValue = result;
                HasLastRawValue = true;
                return result;
            }

            public void Reset() {
                HasLastRawValue = false;
            }
        }
    }
}
