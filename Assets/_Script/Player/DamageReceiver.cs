using UnityEngine;

/// <summary>
/// Single entry point for dealing damage to a character. Combines guard reduction and
/// reinforcement durability into one multiplier, then calls Health.TakeDamage with the
/// final amount. Whatever deals damage (test scripts now, real combat/hit-detection later)
/// only ever needs to call TakeDamage here — it doesn't need to know about guard state or
/// reinforcement tiers itself.
/// </summary>
public class DamageReceiver : MonoBehaviour
{
    [Header("Refs")]
    [Tooltip("The health pool damage is ultimately applied to.")]
    [SerializeField] private Health health;
    [Tooltip("Optional. If assigned, guard status is checked and reduces incoming damage.")]
    [SerializeField] private PlayerController playerController;
    [Tooltip("Optional. If assigned, durability (damage taken multiplier) scales with reinforcement tier.")]
    [SerializeField] private Reinforcement reinforcement;

    /// Applies baseDamage after guard reduction and durability, in that order.
    public void TakeDamage(float baseDamage)
    {
        if (baseDamage <= 0f || health == null) return;

        float guardMultiplier = (playerController != null && playerController.IsGuarding)
            ? 1f - playerController.GuardDamageReduction
            : 1f;

        ApplyDamage(baseDamage, guardMultiplier);
    }

    /// Overload for callers that have already determined guard's contribution upstream —
    /// e.g. a timed-parry/graded-block check — instead of the automatic IsGuarding check
    /// above. Durability still applies normally on top either way.
    public void TakeDamage(float baseDamage, float guardMultiplierOverride)
    {
        if (baseDamage <= 0f || health == null) return;
        ApplyDamage(baseDamage, guardMultiplierOverride);
    }

    private void ApplyDamage(float baseDamage, float guardMultiplier)
    {
        float multiplier = guardMultiplier;

        if (reinforcement != null)
            multiplier *= reinforcement.DamageTakenMultiplier;

        health.TakeDamage(baseDamage * multiplier);
    }
}