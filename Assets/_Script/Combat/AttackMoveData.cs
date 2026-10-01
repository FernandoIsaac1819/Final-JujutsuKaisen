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

    /// One discrete, precisely-timed hit within a move — a single animation can have
    /// several (e.g. a two-slash attack), each with its own hitbox, damage, knockback, and
    /// reaction window. Unlike the old Start/End range, this checks a small tolerance
    /// window AROUND a specific trigger point, not a wide plausible span — the hit is
    /// meant to land at a precise, intentional moment, not whenever the target first
    /// happens to be in range sometime during a longer window.
    [System.Serializable]
    public class HitEvent
    {
        [Header("Timing")]
        [Tooltip("Precise normalized time (0-1) this hit checks for a connection.")]
        [Range(0f, 1f)]
        public float triggerTime = 0.2f;
        [Tooltip("Small tolerance window (0-1) centered on triggerTime — exists only to " +
                 "cover rare frame-rate timing misses, not to widen when this hit is " +
                 "'allowed' to land. Keep this small.")]
        [Range(0f, 0.1f)]
        public float activeWindow = 0.02f;

        [Header("Hitbox")]
        [Tooltip("Forward offset from the attacker's position for this hit's hitbox center, in meters.")]
        public float hitboxForwardOffset = 1.2f;
        [Tooltip("Vertical offset from the attacker's position for this hit's hitbox center, in meters.")]
        public float hitboxHeightOffset = 1f;
        [Tooltip("Hitbox radius, in meters.")]
        public float hitboxRadius = 0.8f;

        [Header("Effect")]
        [Tooltip("Base damage for THIS hit specifically, before Reinforcement's DamageMultiplier is applied.")]
        public float damage = 10f;
        [Tooltip("Seconds of hitstun THIS hit causes. 0 = no hitstun at all for this specific hit.")]
        public float hitstunDuration = 0.3f;
        public KnockbackType knockback = KnockbackType.None;
        [Tooltip("Knockback strength, meters/second, for this hit. Unused if knockback is None.")]
        public float knockbackForce = 4f;

        [Header("Defender Reaction")]
        [Tooltip("Seconds before THIS hit lands during which a fresh Guard press counts as a timed read at all.")]
        public float reactionWindowDuration = 0.15f;
        [Tooltip("Seconds before THIS hit lands within which a fresh Guard press counts as a full parry instead of a graded block. Must be <= reactionWindowDuration.")]
        public float parryThreshold = 0.05f;
    }

    [Header("Animation")]
    [Tooltip("Exact Animator state name this move plays. Played directly via CrossFade, " +
             "not through a per-move trigger parameter — keeps this scalable as the combo " +
             "tree grows without needing a new Animator parameter for every move.")]
    [SerializeField] private string animatorStateName;
    [Tooltip("Crossfade blend duration into this move's animation state.")]
    [SerializeField] private float blendInDuration = 0.05f;

    [Header("Hit Events")]
    [Tooltip("Each entry is a separate, precisely-timed hit — add more than one for an " +
             "animation with multiple distinct impact moments (e.g. a two-slash attack). " +
             "Each fires at most once per move, checked against its own small tolerance " +
             "window around its trigger time, not a wide plausible range.")]
    [SerializeField] private List<HitEvent> hitEvents = new List<HitEvent>();

    [Header("Movement")]
    [Tooltip("Each entry is a separate forward step, firing at its own point in the clip — " +
             "add more than one for an animation with multiple swings/movements. Each step " +
             "is only applied if the character is outside combat range at the moment IT " +
             "fires (checked per-step, not just once for the whole move), and each step " +
             "re-aims fresh toward the target's current position when it triggers.")]
    [SerializeField] private List<ForwardStepEntry> forwardSteps = new List<ForwardStepEntry>();

    [Header("Combo Chaining")]
    [Tooltip("The punish window opens once the LAST hit event's tolerance window has " +
             "passed — not a separately authored value. Pressing Light/Heavy during that " +
             "time continues the combo; letting the clip finish naturally returns to " +
             "normal movement (Dash/move freely — Jump stays gated by the usual " +
             "combat-range rule).")]
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
    public IReadOnlyList<HitEvent> HitEvents => hitEvents;
    public IReadOnlyList<ForwardStepEntry> ForwardSteps => forwardSteps;
    public AttackMoveData NextOnLight => nextOnLight;
    public AttackMoveData NextOnHeavy => nextOnHeavy;
    public AttackMoveData AdvancedUnlock => advancedUnlock;
    public bool IsAdvanced => isAdvanced;
    public bool GatesAdvanced => advancedUnlock != null;
}