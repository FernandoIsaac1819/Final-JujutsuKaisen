using UnityEngine;

/// <summary>
/// Per-character health pool. Stays deliberately "dumb" — no guard reduction, no
/// invincibility frames, no KO sequencing. Whatever applies damage (a combat script) is
/// responsible for checking guard/i-frames and passing in an already-final amount; this
/// script just tracks the number and tells other systems when it changes or hits zero.
/// </summary>
public class Health : MonoBehaviour
{
    [Header("Capacity")]
    [Tooltip("Maximum health this character can have.")]
    [SerializeField] private float maxHealth = 1000f;

    private float currentHealth;
    private bool hasFiredDied;
    private bool hasInitialized;

    private void EnsureInitialized()
    {
        if (hasInitialized) return;
        hasInitialized = true;
        currentHealth = maxHealth;
    }

    public float Current { get { EnsureInitialized(); return currentHealth; } }
    public float Max => maxHealth;
    public float Percentage { get { EnsureInitialized(); return maxHealth > 0f ? Current / maxHealth : 0f; } }
    public bool IsDead { get { EnsureInitialized(); return currentHealth <= 0f; } }

    /// (current, max) — fired only when the value actually changes.
    public event System.Action<float, float> HealthChanged;

    /// Fired once, the frame health first reaches 0. Does not fire again on subsequent
    /// TakeDamage calls while already at 0.
    public event System.Action Died;

    private void Awake()
    {
        EnsureInitialized();
    }

    /// Apply damage. amount should already reflect any guard reduction — this method
    /// doesn't know or care whether the hit was guarded.
    public void TakeDamage(float amount)
    {
        if (amount <= 0f) return;
        SetCurrent(currentHealth - amount);
    }

    /// Restore health. Not used for passive regen (there is none) — for explicit heals only.
    public void Heal(float amount)
    {
        if (amount <= 0f) return;
        SetCurrent(currentHealth + amount);
    }

    /// Resets to full and re-arms the Died event for a new round/rematch.
    public void ResetHealth()
    {
        hasFiredDied = false;
        SetCurrent(maxHealth);
    }

    private void SetCurrent(float value)
    {
        EnsureInitialized();
        float clamped = Mathf.Clamp(value, 0f, maxHealth);
        if (!Mathf.Approximately(clamped, currentHealth))
        {
            currentHealth = clamped;
            HealthChanged?.Invoke(currentHealth, maxHealth);
        }

        if (currentHealth <= 0f && !hasFiredDied)
        {
            hasFiredDied = true;
            Died?.Invoke();
        }
    }
}