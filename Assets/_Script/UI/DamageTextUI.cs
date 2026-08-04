using UnityEngine;
using TMPro;

/// <summary>
/// Test-only UI: shows the damage amount from the most recent hit, computed as the delta
/// between successive Health.HealthChanged values. Not a floating combat-text system —
/// just a fixed UI label for verifying actual numbers during testing. Ignores heals
/// (delta &lt;= 0) since this is specifically for watching damage values.
/// </summary>
public class DamageTextUI : MonoBehaviour
{
    [Header("Refs")]
    [Tooltip("The health pool being watched.")]
    [SerializeField] private Health health;
    [Tooltip("TextMeshPro label the damage number is written to.")]
    [SerializeField] private TMP_Text damageText;

    [Header("Display")]
    [Tooltip("Seconds the number stays visible after a hit before hiding again.")]
    [SerializeField] private float displayDuration = 1.5f;
    [Tooltip("Format string for the number. {0} is the damage amount.")]
    [SerializeField] private string format = "-{0:0}";

    private float previousHealth;
    private float hideTimer;

    private void OnEnable()
    {
        health.HealthChanged += OnHealthChanged;
        previousHealth = health.Current;
        damageText.gameObject.SetActive(false);
    }

    private void OnDisable()
    {
        health.HealthChanged -= OnHealthChanged;
    }

    private void OnHealthChanged(float current, float max)
    {
        float delta = previousHealth - current;
        previousHealth = current;

        if (delta <= 0f) return; // a heal, not damage — ignored

        damageText.text = string.Format(format, delta);
        damageText.gameObject.SetActive(true);
        hideTimer = displayDuration;
    }

    private void Update()
    {
        if (hideTimer <= 0f) return;

        hideTimer -= Time.deltaTime;
        if (hideTimer <= 0f)
            damageText.gameObject.SetActive(false);
    }
}
