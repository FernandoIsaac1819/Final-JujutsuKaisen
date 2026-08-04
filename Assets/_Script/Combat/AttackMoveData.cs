using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One attack move — a node in the combo tree. Every field is independently authored;
/// nothing here is derived from position-in-string or from a formula. Damage, hitstun,
/// knockback, and timing are all set by hand per move, same philosophy as everything else
/// in this project (Reinforcement's stat tiers, RCT's rates, etc.).
/// </summary>
[CreateAssetMenu(fileName = "AttackMove", menuName = "JJK/Attack Move")]
public class AttackMoveData : ScriptableObject
{
    public enum KnockbackType { None, Backward, LaunchUp }

    /// One forward step within a move — a single animation can have several of these
    /// (e.g. a two-swing combo hit), each triggering independently at its own point in the
    /// clip. Triggering a step always restarts/cuts off whatever motion a previous step in
    /// the same move was still carrying out.
    [System.Serializable]
    public class ForwardStepEntry
    {
        [Tooltip("Normalized time (0-1) within the clip when this step triggers.")]
        [Range(0f, 1f)]
        public float triggerTime = 0.1f;
        [Tooltip("How far this step moves toward the target, in meters.")]
        public float distance = 1f;
        [Tooltip("How long this step takes, in seconds.")]
        public float duration = 0.15f;
    }

    [Header("Animation")]
    [Tooltip("Exact Animator state name this move plays. Played directly via CrossFade, " +
             "not through a per-move trigger parameter — keeps this scalable as the combo " +
             "tree grows without needing a new Animator parameter for every move.")]
    [SerializeField] private string animatorStateName;
    [Tooltip("Crossfade blend duration into this move's animation state.")]
    [SerializeField] private float blendInDuration = 0.05f;

    [Header("Hitbox Timing")]
    [Tooltip("Normalized time (0-1) within this clip where the hitbox becomes active.")]
    [Range(0f, 1f)]
    [SerializeField] private float hitboxActiveStart = 0.2f;
    [Tooltip("Normalized time (0-1) within this clip where the hitbox stops being active.")]
    [Range(0f, 1f)]
    [SerializeField] private float hitboxActiveEnd = 0.35f;
    [Tooltip("Forward offset from the attacker's position for the hitbox center, in meters.")]
    [SerializeField] private float hitboxForwardOffset = 1.2f;
    [Tooltip("Vertical offset from the attacker's position for the hitbox center, in meters.")]
    [SerializeField] private float hitboxHeightOffset = 1f;
    [Tooltip("Hitbox radius, in meters.")]
    [SerializeField] private float hitboxRadius = 0.8f;

    [Header("Movement")]
    [Tooltip("Each entry is a separate forward step, firing at its own point in the clip — " +
             "add more than one for an animation with multiple swings/movements. Each step " +
             "is only applied if the character is outside combat range at the moment IT " +
             "fires (checked per-step, not just once for the whole move), and each step " +
             "re-aims fresh toward the target's current position when it triggers.")]
    [SerializeField] private List<ForwardStepEntry> forwardSteps = new List<ForwardStepEntry>();

    [Header("Defender Reaction Window")]
    [Tooltip("Seconds before this hit lands during which a FRESH Guard press counts as a " +
             "timed read at all. Narrow this for Light attacks (hard to time), widen it for " +
             "Heavy (easier to time) — this is the actual difficulty lever, independent of " +
             "how fast the animations themselves look.")]
    [SerializeField] private float reactionWindowDuration = 0.15f;
    [Tooltip("Seconds before this hit lands within which a fresh Guard press counts as a " +
             "full parry instead of a graded block. Must be <= reactionWindowDuration.")]
    [SerializeField] private float parryThreshold = 0.05f;

    [Header("Effect On Hit")]
    [Tooltip("Base damage, before Reinforcement's DamageMultiplier is applied.")]
    [SerializeField] private float damage = 10f;
    [Tooltip("Seconds of hitstun this move causes on a successful hit. 0 = no hitstun at " +
             "all — some moves are deliberately left without it, to create real gaps in a " +
             "string that a slow follow-up can be sidestepped/guarded/parried inside of.")]
    [SerializeField] private float hitstunDuration = 0.3f;
    [SerializeField] private KnockbackType knockback = KnockbackType.None;
    [Tooltip("Knockback strength, meters/second at the moment of the hit. Unused if " +
             "knockback is None.")]
    [SerializeField] private float knockbackForce = 4f;

    [Header("Combo Chaining")]
    [Tooltip("The punish window is implicit — whatever's left of this clip after " +
             "Hitbox Active End, not a separately authored value. Pressing Light/Heavy " +
             "during that time continues the combo; letting the clip finish naturally " +
             "returns to normal movement (Dash/move freely — Jump stays gated by the " +
             "usual combat-range rule).")]
    [SerializeField] private AttackMoveData nextOnLight;
    [Tooltip("Next move if Heavy is pressed during the punish window. Null = string ends here on Heavy.")]
    [SerializeField] private AttackMoveData nextOnHeavy;
    [Tooltip("Only relevant if this move is a Heavy that's meant to gate an Advanced string. " +
             "If this move's hit actually connects, this becomes available as the next move " +
             "on the FOLLOWING Heavy press, instead of nextOnHeavy. Leave empty if this move " +
             "doesn't gate an Advanced branch.")]
    [SerializeField] private AttackMoveData advancedUnlock;
    [Tooltip("Marks this move as part of an Advanced string. Advanced moves don't offer the " +
             "cancel-into-Dash/Jump/Move choice point Basic moves do — the string is " +
             "committed once you're in it, running to its defined end.")]
    [SerializeField] private bool isAdvanced;

    public string AnimatorStateName => animatorStateName;
    public float BlendInDuration => blendInDuration;
    public IReadOnlyList<ForwardStepEntry> ForwardSteps => forwardSteps;
    public float ReactionWindowDuration => reactionWindowDuration;
    public float ParryThreshold => parryThreshold;
    public float HitboxActiveStart => hitboxActiveStart;
    public float HitboxActiveEnd => hitboxActiveEnd;
    public float HitboxForwardOffset => hitboxForwardOffset;
    public float HitboxHeightOffset => hitboxHeightOffset;
    public float HitboxRadius => hitboxRadius;
    public float Damage => damage;
    public float HitstunDuration => hitstunDuration;
    public KnockbackType Knockback => knockback;
    public float KnockbackForce => knockbackForce;
    public AttackMoveData NextOnLight => nextOnLight;
    public AttackMoveData NextOnHeavy => nextOnHeavy;
    public AttackMoveData AdvancedUnlock => advancedUnlock;
    public bool IsAdvanced => isAdvanced;
    public bool GatesAdvanced => advancedUnlock != null;
}