using System.Linq;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;

namespace QuestCameraKit.Editor
{
    // Temporary, one-off script: disables QUAK's own Android OpenXR API layer
    // (XR_APILAYER_XRDEVROB_quak_injection) without opening the OpenXR project
    // settings UI, which currently crashes with a KeyNotFoundException for
    // BuildTargetGroup.Android (Unity OpenXR package bug). Delete this file
    // after running it once.
    public static class TempDisableQuakLayer
    {
        private const string LayerName = "XR_APILAYER_XRDEVROB_quak_injection";

        [MenuItem("QUAK/Temp - Disable Android Injection Layer")]
        public static void Run()
        {
            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            var feature = settings?.GetFeature<ApiLayersFeature>();
            if (feature == null)
            {
                Debug.LogError("[TempDisableQuakLayer] ApiLayersFeature not found for Android.");
                return;
            }

            var installed = feature.apiLayers.collection.Any(layer =>
                layer.name == LayerName && layer.libraryArchitecture == Architecture.Arm64);
            if (!installed)
            {
                Debug.Log("[TempDisableQuakLayer] QUAK Android layer is not installed; nothing to disable.");
                return;
            }

            var wasEnabled = feature.apiLayers.IsEnabled(LayerName, Architecture.Arm64);
            feature.apiLayers.SetEnabled(LayerName, Architecture.Arm64, false);
            EditorUtility.SetDirty(feature);
            AssetDatabase.SaveAssets();

            var nowEnabled = feature.apiLayers.IsEnabled(LayerName, Architecture.Arm64);
            Debug.Log($"[TempDisableQuakLayer] wasEnabled={wasEnabled} nowEnabled={nowEnabled}");
        }
    }
}
