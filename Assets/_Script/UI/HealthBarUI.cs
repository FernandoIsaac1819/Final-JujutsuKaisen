using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Drives a filled Image's fillAmount off Health's HealthChanged event. Doesn't know or
/// care what's dealing the damage — only listens to the pool itself. Same pattern as
/// EnergyBarUI.
/// </summary>
public class HealthBarUI : MonoBehaviour
{
    [Header("Refs")]
    [Tooltip("The health pool this bar displays.")]
    [SerializeField] private Health health;
    [Tooltip("An Image with Image Type = Filled. Its fillAmount is driven by this script.")]
    [SerializeField] private Image fillImage;

    [Header("Smoothing")]
    [Tooltip("Seconds for the bar to visually catch up to the real value. 0 = instant, no smoothing.")]
    [SerializeField] private float smoothTime = 0.15f;

    private float targetFill;
    private float fillVelocity;

    private void OnEnable()
    {
        health.HealthChanged += OnHealthChanged;
        // Initialize immediately — HealthChanged only fires on future changes, not on subscribe.
        targetFill = health.Percentage;
        fillImage.fillAmount = targetFill;
    }

    private void OnDisable()
    {
        health.HealthChanged -= OnHealthChanged;
    }

    private void OnHealthChanged(float current, float max)
    {
        targetFill = max > 0f ? current / max : 0f;
    }

    private void Update()
    {
        if (smoothTime <= 0f)
        {
            fillImage.fillAmount = targetFill;
            return;
        }

        fillImage.fillAmount = Mathf.SmoothDamp(fillImage.fillAmount, targetFill, ref fillVelocity, smoothTime);
    }
}
