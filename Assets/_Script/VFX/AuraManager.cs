using System.Collections.Generic;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.UI;

namespace JJK.Aura
{
    public class AuraManager : MonoBehaviour
    {
        public static AuraManager instance;

        [Header("Layer Pool")]
        [Tooltip("Unity Layers reserved for aura capture, configured once in Project Settings > Tags and Layers. " +
                 "Each active body aura consumes one from this pool while in use, and returns it when " +
                 "that character despawns/disables. Size this to your expected max CONCURRENT aura users, not " +
                 "your total character count (32-layer engine ceiling, most of it already used by Unity/your project).")]
        [SerializeField] private string[] _reservedLayerNames;

        [Header("UI")]
        [Tooltip("Parent for runtime-created aura RawImages — typically a full-screen RectTransform under your Canvas.")]
        [SerializeField] private RectTransform _rawImageContainer;

        /// <summary>True only if every reserved layer name resolved successfully at Awake.</summary>
        public bool LayerPoolValid { get; private set; }

        /// <summary>One fully-provisioned capture: a dedicated camera + RenderTexture rendering
        /// a single reserved layer, feeding a runtime-created RawImage. Both aura materials
        /// share this same capture — only the material assigned to the RawImage differs.</summary>
        private class AuraRig
        {
            public int layer;
            public Camera camera;
            public RenderTexture texture;
            public RawImage rawImage;
            public Material normalMaterial;
            public Material rctMaterial;
        }

        private readonly Queue<int> _freeLayers = new Queue<int>();
        private readonly Dictionary<AuraSource, AuraRig> _bodyRigs = new Dictionary<AuraSource, AuraRig>();

        private void Awake()
        {
            if (instance == null) instance = this;

            LayerPoolValid = _reservedLayerNames != null && _reservedLayerNames.Length > 0;
            foreach (string layerName in _reservedLayerNames)
            {
                int index = LayerMask.NameToLayer(layerName);
                if (index == -1)
                {
                    Debug.LogError($"[AuraManager] Reserved layer '{layerName}' does not exist. " +
                                    "Create it under Project Settings > Tags and Layers.");
                    LayerPoolValid = false;
                    continue;
                }
                _freeLayers.Enqueue(index);
            }

            if (_rawImageContainer == null)
                Debug.LogError("[AuraManager] No Raw Image Container assigned — auras have nowhere to render.", this);
            else
                ConfigureCanvas(_rawImageContainer);

            AuraSource.Registered += ProvisionSource;
            AuraSource.Unregistered += ReleaseSource;

            // Catch anything that already registered before this Awake ran — registration
            // order between AuraSources and AuraManager is never guaranteed in Unity.
            foreach (var source in AuraSource.Active)
                ProvisionSource(source);
        }

        private void OnEnable()
        {
            CinemachineCore.CameraUpdatedEvent.AddListener(OnCameraUpdated);
        }

        private void OnDisable()
        {
            CinemachineCore.CameraUpdatedEvent.RemoveListener(OnCameraUpdated);
        }

        private void OnDestroy()
        {
            AuraSource.Registered -= ProvisionSource;
            AuraSource.Unregistered -= ReleaseSource;
        }

        private void ConfigureCanvas(RectTransform container)
        {
            Canvas canvas = container.GetComponentInParent<Canvas>();
            if (canvas == null) return;

            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = Camera.main;
            CanvasScaler scaler = canvas.GetComponent<CanvasScaler>();
            if (scaler != null)
                scaler.referenceResolution = new Vector2(Screen.width, Screen.height);
        }

        private void ProvisionSource(AuraSource source)
        {
            if (_bodyRigs.ContainsKey(source)) return; // already provisioned

            if (source.AuraMaterial == null)
            {
                Debug.LogWarning($"[AuraManager] '{source.name}' has no Aura Material assigned. Skipping aura.", source);
                return;
            }

            AuraRig body = BuildRig(source.BodyRenderers, source.AuraMaterial, source.RctAuraMaterial, source.name + "_Body");
            if (body != null) _bodyRigs[source] = body;

            Aura_Controller controller = source.GetComponent<Aura_Controller>();
            if (controller != null)
            {
                controller.Initialize(body?.rawImage, body?.rctMaterial);
            }
            else
            {
                Debug.LogWarning($"[AuraManager] '{source.name}' has an AuraSource but no Aura_Controller — provisioned but nothing will drive the fade.", source);
            }
        }

        private AuraRig BuildRig(Renderer[] renderers, Material normalSourceMaterial, Material rctSourceMaterial, string rigName)
        {
            if (renderers == null || renderers.Length == 0)
            {
                Debug.LogWarning($"[AuraManager] '{rigName}' has no renderers assigned — skipping.");
                return null;
            }
            if (normalSourceMaterial == null)
            {
                Debug.LogWarning($"[AuraManager] '{rigName}' has no aura material on its CharacterSO — skipping.");
                return null;
            }
            if (_freeLayers.Count == 0)
            {
                Debug.LogWarning($"[AuraManager] No free aura layers left — increase the reserved pool size " +
                                  $"to support more concurrent auras. Skipping '{rigName}'.");
                return null;
            }
            if (Camera.main == null)
            {
                Debug.LogError("[AuraManager] No Camera.main in the scene.");
                return null;
            }

            int layer = _freeLayers.Dequeue();
            foreach (var r in renderers)
                SetLayerRecursively(r.gameObject, layer);

            GameObject camGO = new GameObject(rigName + "_Camera");
            camGO.transform.SetParent(transform, false);
            Camera cam = camGO.AddComponent<Camera>();

            // Solid transparent clear so the shader's alpha-based edge detection sees "nothing
            // here" as truly transparent, not opaque black.
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.cullingMask = 1 << layer;

            cam.orthographic = Camera.main.orthographic;
            if (cam.orthographic) cam.orthographicSize = Camera.main.orthographicSize;
            cam.nearClipPlane = Camera.main.nearClipPlane;
            cam.farClipPlane = Camera.main.farClipPlane;
            cam.fieldOfView = Camera.main.fieldOfView;
            cam.depth = Camera.main.depth - 1 - layer;   // always renders before Camera.main

            RenderTexture rt = new RenderTexture(Screen.width, Screen.height, 24, RenderTextureFormat.ARGB32);
            rt.Create();

            // Clear to fully transparent before anything samples it. An uninitialized RenderTexture
            // can contain raw GPU garbage — commonly seen as a magenta/pink flash — until its first
            // real render, and Camera.Render() doesn't happen until later this frame at the earliest.
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, new Color(0f, 0f, 0f, 0f));
            RenderTexture.active = previousActive;

            cam.targetTexture = rt;

            GameObject imgGO = new GameObject(rigName + "_RawImage", typeof(RectTransform));
            imgGO.transform.SetParent(_rawImageContainer, false);
            RectTransform rect = (RectTransform)imgGO.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            RawImage rawImage = imgGO.AddComponent<RawImage>();
            Material normalMat = new Material(normalSourceMaterial);   // instanced so each character animates independently
            normalMat.SetTexture("_Main_texture", rt);

            Material rctMat = null;
            if (rctSourceMaterial != null)
            {
                rctMat = new Material(rctSourceMaterial);
                rctMat.SetTexture("_Main_texture", rt);
            }

            rawImage.material = normalMat;   // starts on the normal aura; Aura_Controller swaps this while RCT is active
            rawImage.texture = rt;   // also set directly — canvas rendering re-syncs this onto the material's main texture slot, so setting only the material property risks it getting overwritten

            return new AuraRig { layer = layer, camera = cam, texture = rt, rawImage = rawImage, normalMaterial = normalMat, rctMaterial = rctMat };
        }

        private void ReleaseSource(AuraSource source)
        {
            if (!_bodyRigs.TryGetValue(source, out AuraRig rig)) return;
            _bodyRigs.Remove(source);

            _freeLayers.Enqueue(rig.layer);   // return the layer to the pool for reuse
            if (rig.camera != null) Destroy(rig.camera.gameObject);
            if (rig.texture != null) { rig.texture.Release(); Destroy(rig.texture); }
            if (rig.rawImage != null) Destroy(rig.rawImage.gameObject);
            if (rig.normalMaterial != null) Destroy(rig.normalMaterial);
            if (rig.rctMaterial != null) Destroy(rig.rctMaterial);
        }

        private static void SetLayerRecursively(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform)
                SetLayerRecursively(child.gameObject, layer);
        }

        // Fires after CinemachineBrain has fully resolved the camera for this frame — avoids the
        // one-frame lag you'd get from a plain LateUpdate racing against the Brain's own LateUpdate
        // (Unity doesn't guarantee ordering between the two), which is what caused the aura to
        // visibly drift off the character specifically while the camera/character were moving.
        private void OnCameraUpdated(CinemachineBrain brain)
        {
            if (Camera.main == null) return;
            Transform mainT = Camera.main.transform;

            foreach (var kvp in _bodyRigs)
            {
                Camera cam = kvp.Value.camera;
                if (cam == null) continue;

                cam.transform.SetPositionAndRotation(mainT.position, mainT.rotation);
                if (cam.fieldOfView != Camera.main.fieldOfView) cam.fieldOfView = Camera.main.fieldOfView;
                if (cam.orthographic && cam.orthographicSize != Camera.main.orthographicSize)
                    cam.orthographicSize = Camera.main.orthographicSize;
            }
        }
    }
}