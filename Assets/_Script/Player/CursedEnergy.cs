using UnityEngine;

/// <summary>
/// Per-character cursed energy pool. Reinforcement, and later Cursed Techniques, both
/// draw from this via Drain(). UI listens to EnergyChanged instead of polling — nothing
/// else needs a direct reference to whatever is currently spending energy.
/// </summary>
public class CursedEnergy : MonoBehaviour
{
    [Header("Capacity")]
    [Tooltip("Maximum cursed energy this character can hold.")]
    [SerializeField] private float maxEnergy = 1000f;

    [Header("Passive Regen")]
    [Tooltip("Cursed energy regenerated per second once regen is active.")]
    [SerializeField] private float regenRate = 20f;
    [Tooltip("Seconds since the last drain before regen is allowed to start (resets every time something drains).")]
    [SerializeField] private float regenDelay = 2f;

    private float currentEnergy;
    private float regenDelayTimer;
    private bool drainedThisFrame;
    private bool hasInitialized;

    private void EnsureInitialized()
    {
        if (hasInitialized) return;
        hasInitialized = true;
        currentEnergy = maxEnergy;
    }

    public float Current { get { EnsureInitialized(); return currentEnergy; } }
    public float Max => maxEnergy;
    public float Percentage { get { EnsureInitialized(); return maxEnergy > 0f ? Current / maxEnergy : 0f; } }

    /// (current, max) — fired only when the value actually changes.
    public event System.Action<float, float> EnergyChanged;

    private void Awake()
    {
        EnsureInitialized();
    }

    private void Update()
    {
        if (drainedThisFrame)
        {
            regenDelayTimer = regenDelay;
        }
        else if (regenDelayTimer > 0f)
        {
            regenDelayTimer -= Time.deltaTime;
        }
        else if (currentEnergy < maxEnergy)
        {
            SetCurrent(currentEnergy + regenRate * Time.deltaTime);
        }

        drainedThisFrame = false;
    }

    /// Call every frame a system is actively draining this pool. Also resets the regen delay.
    public void Drain(float amountPerSecond)
    {
        drainedThisFrame = true;
        SetCurrent(currentEnergy - amountPerSecond * Time.deltaTime);
    }

    /// True if current energy is at least this fraction (0..1) of max.
    public bool HasPercentage(float percentage) => Percentage >= percentage;

    private void SetCurrent(float value)
    {
        EnsureInitialized();
        float clamped = Mathf.Clamp(value, 0f, maxEnergy);
        if (!Mathf.Approximately(clamped, currentEnergy))
        {
            currentEnergy = clamped;
            EnergyChanged?.Invoke(currentEnergy, maxEnergy);
        }
    }
}