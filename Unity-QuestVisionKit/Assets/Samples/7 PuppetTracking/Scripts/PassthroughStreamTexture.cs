using System;
using UnityEngine;
using UnityWebRTC = Unity.WebRTC.WebRTC;

namespace QuestCameraKit.WebRTC {
    // Bridges PassthroughCameraAccess's texture (R8G8B8A8, i.e. RGBA-first) into a
    // RenderTexture in whatever format Unity.WebRTC's VideoStreamTrack actually requires on
    // this platform (B8G8R8A8 on Quest/Vulkan) - VideoStreamTrack validates the input
    // texture's graphicsFormat at construction time and throws if it doesn't match, and the
    // passthrough texture never matches directly.
    public sealed class PassthroughStreamTexture : IDisposable {
        private readonly Vector2 _blitScale;
        private readonly Vector2 _blitOffset;

        public RenderTexture Texture { get; private set; }
        public Vector2Int Size { get; }
        public bool IsReady => Texture != null;

        public PassthroughStreamTexture(Vector2Int size, bool flipVertically, bool mirrorHorizontally) {
            Size = size;

            var scale = Vector2.one;
            var offset = Vector2.zero;
            if (flipVertically) {
                scale.y = -1f;
                offset.y = 1f;
            }
            if (mirrorHorizontally) {
                scale.x = -1f;
                offset.x = 1f;
            }
            _blitScale = scale;
            _blitOffset = offset;

            // Must come from WebRTC.GetSupportedGraphicsFormat, never a hardcoded
            // RenderTextureFormat enum - a mismatched-but-legacy format can pass the native
            // validator while still being visually wrong (washed out / over-bright) instead
            // of throwing, which is much harder to diagnose than an exception.
            var format = UnityWebRTC.GetSupportedGraphicsFormat(SystemInfo.graphicsDeviceType);
            Texture = new RenderTexture(size.x, size.y, 0, format) {
                name = "PuppetTracking_WebRTC_Stream",
                useMipMap = false,
                autoGenerateMips = false,
                antiAliasing = 1,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                vrUsage = VRTextureUsage.None // a stereo usage here would allocate a texture array, not a 2D texture
            };
            Texture.Create();
        }

        // Returns false if there is nothing to copy yet. The passthrough texture is stable
        // across frames (only its contents update in place), so no null-checks against a
        // previous reference are needed here.
        public bool TryUpdate(Texture source) {
            if (source == null || Texture == null) return false;
            Graphics.Blit(source, Texture, _blitScale, _blitOffset);
            RenderTexture.active = null; // Blit leaves the destination bound as the active RT
            return true;
        }

        public void Dispose() {
            if (Texture == null) return;
            Texture.Release();
            UnityEngine.Object.Destroy(Texture);
            Texture = null;
        }
    }
}
