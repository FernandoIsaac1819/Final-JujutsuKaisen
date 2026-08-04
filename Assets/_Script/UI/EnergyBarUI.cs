using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Drives a filled Image's fillAmount off CursedEnergy's EnergyChanged event.
/// Doesn't know or care what's spending the energy (Reinforcement, later Cursed
/// Techniques) — only listens to the pool itself.
/// </summary>
public class EnergyBarUI : MonoBehaviour
{
    [Header("Refs")]
    [Tooltip("The cursed energy pool this bar displays.")]
    [SerializeField] private CursedEnergy cursedEnergy;
    [Tooltip("An Image with Image Type = Filled. Its fillAmount is driven by this script.")]
    [SerializeField] private Image fillImage;

    [Header("Smoothing")]
    [Tooltip("Seconds for the bar to visually catch up to the real value. 0 = instant, no smoothing.")]
    [SerializeField] private float smoothTime = 0.15f;

    private float targetFill;
    private float fillVelocity;

    private void OnEnable()
    {
        cursedEnergy.EnergyChanged += OnEnergyChanged;
        // Initialize immediately — EnergyChanged only fires on future changes, not on subscribe.
        targetFill = cursedEnergy.Max;
        fillImage.fillAmount = targetFill;
    }

    private void OnDisable()
    {
        cursedEnergy.EnergyChanged -= OnEnergyChanged;
    }

    private void OnEnergyChanged(float current, float max)
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
