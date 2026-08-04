using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace JJK.Aura
{
    public class Aura_Controller : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("The Reinforcement component that owns the CE tier state. Auto-found on the same GameObject if left empty.")]
        [SerializeField] private Reinforcement _reinforcement;

        [Header("Fade Speeds")]
        [Tooltip("How fast the aura fades in (visible).")]
        [SerializeField] private float _fadeInSpeed = 2f;
        [Tooltip("How fast the aura fades out (hidden).")]
        [SerializeField] private float _fadeOutSpeed = 2f;

        // Must match the Reference name in the Shader Graph blackboard.
        private const string FadeProperty = "_Fade";

        // Flattened from a former multi-target FadeTarget class — only the body aura is
        // ever used here, so a dedicated class per instance was pure indirection.
        private RawImage _rawImage;
        private Material _material;      // whichever material is currently active (normal or RCT)
        private Material _normalMaterial;
        private Material _rctMaterial;
        private int _fadeId;
        private float _fadeMin;
        private float _fadeMax;
        private float _current;
        private Coroutine _fadeRoutine;
        private bool _initialized;
        private bool _rctActive;
        /// True while the RCT material is currently showing, OR still fading out toward
        /// the normal material — this is what other systems should check for "is RCT still
        /// visually present," since _rctActive alone turns off before the fade finishes.
        public bool IsRctVisuallyActive => _rctActive || _pendingSwapMaterial != null;
        private bool _reinforcementOn;
        private Material _pendingSwapMaterial; // set when a swap needs to wait for a fade-out to finish first

        private void Awake()
        {
            if (_reinforcement == null) _reinforcement = GetComponent<Reinforcement>();
        }

        private void OnEnable()
        {
            if (_reinforcement != null) _reinforcement.TierChanged += HandleTierChanged;
        }

        private void OnDisable()
        {
            if (_reinforcement != null) _reinforcement.TierChanged -= HandleTierChanged;
        }

        public void Initialize(RawImage bodyImage, Material rctMaterial)
        {
            _initialized = bodyImage != null;
            if (!_initialized) return;

            _rawImage = bodyImage;
            _material = bodyImage.material; // AuraManager already instanced and assigned this (the normal material)
            _normalMaterial = _material;
            _rctMaterial = rctMaterial;
            _fadeId = Shader.PropertyToID(FadeProperty);

            if (!_material.HasProperty(_fadeId))
                Debug.LogWarning($"[Aura_Controller] Material '{_material.name}' has no property '{FadeProperty}'. " +
                                  "Check the Reference field in the Shader Graph blackboard.");

            GetShaderRange(_material, FadeProperty, out _fadeMin, out _fadeMax);

            _reinforcementOn = _reinforcement != null && _reinforcement.CurrentTier != Reinforcement.Tier.Off;
            _current = (_reinforcementOn || _rctActive) ? _fadeMax : _fadeMin;
            _material.SetFloat(_fadeId, _current);
        }

        private void HandleTierChanged(Reinforcement.Tier tier)
        {
            _reinforcementOn = tier != Reinforcement.Tier.Off;
            UpdateVisibility();
        }

        /// <summary>Fades the aura in (visible). This shader's _Fade is higher = more visible.</summary>
        public void ShowAura() => SetFadeTarget(_fadeMax);

        /// <summary>Fades the aura out (hidden).</summary>
        public void HideAura() => SetFadeTarget(_fadeMin);

        /// Visibility is the OR of both sources — neither Reinforcement nor RCT needs to
        /// know about the other, they just both feed this same recomputation.
        private void UpdateVisibility()
        {
            if (_reinforcementOn || _rctActive) ShowAura();
            else HideAura();
        }

        /// Swaps which material is assigned to the RawImage — normal cursed energy aura or
        /// the white RCT aura. If the aura is/stays visible across this change, the swap
        /// happens immediately (reads as a clean color change). If this change is going to
        /// hide the aura entirely, the swap is deferred until the fade-out actually finishes
        /// (see Apply()) — otherwise the normal material would flash visible for the length
        /// of the fade before disappearing, which looks like it turned on rather than off.
        public void SetRCTActive(bool active)
        {
            if (!_initialized || active == _rctActive) return;
            if (active && _rctMaterial == null) return; // no RCT material assigned — nothing to swap to

            _rctActive = active;
            bool staysVisible = active || _reinforcementOn;

            if (staysVisible)
            {
                _pendingSwapMaterial = null; // cancel any swap that was waiting on a fade-out
                SwapMaterialImmediate(active ? _rctMaterial : _normalMaterial);
            }
            else
            {
                _pendingSwapMaterial = _normalMaterial; // applied once Apply() sees we've reached _fadeMin
            }

            UpdateVisibility();
        }

        private void SwapMaterialImmediate(Material target)
        {
            _material = target;
            if (_material.HasProperty(_fadeId))
                _material.SetFloat(_fadeId, _current);
            _rawImage.material = _material;
        }

        private void SetFadeTarget(float target)
        {
            if (!_initialized) return;

            target = Mathf.Clamp(target, _fadeMin, _fadeMax);
            if (Mathf.Approximately(target, _current)) return;

            float speed = target < _current ? _fadeOutSpeed : _fadeInSpeed;

            if (_fadeRoutine != null) StopCoroutine(_fadeRoutine);
            _fadeRoutine = StartCoroutine(SmoothMoveFloat(target, speed));
        }

        private IEnumerator SmoothMoveFloat(float to, float speed)
        {
            float from = _current;
            float duration = speed > 0f ? Mathf.Abs(to - from) / speed : 0f;
            if (duration <= 0f) { Apply(to); yield break; }

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                Apply(Mathf.SmoothStep(from, to, Mathf.Clamp01(elapsed / duration)));
                yield return null;
            }
            Apply(to);
        }

        private void Apply(float value)
        {
            _current = value;
            _material.SetFloat(_fadeId, value);

            if (_pendingSwapMaterial != null && Mathf.Approximately(value, _fadeMin))
            {
                SwapMaterialImmediate(_pendingSwapMaterial);
                _pendingSwapMaterial = null;
            }
        }

        /// <summary>Reads a property's authored Range(min,max) off the shader so clamping matches the blackboard.</summary>
        private void GetShaderRange(Material material, string propertyName, out float min, out float max)
        {
            Shader shader = material.shader;
            int index = shader.FindPropertyIndex(propertyName);

            if (index >= 0 && shader.GetPropertyType(index) == ShaderPropertyType.Range)
            {
                Vector2 range = shader.GetPropertyRangeLimits(index);
                min = range.x;
                max = range.y;
            }
            else
            {
                Debug.LogWarning($"[Aura_Controller] No Range found for '{propertyName}' - defaulting to 0-1. " +
                                 "Check the property exists and is set to Slider mode in the blackboard.");
                min = 0f;
                max = 1f;
            }
        }
    }
}