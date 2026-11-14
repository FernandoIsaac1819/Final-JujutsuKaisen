using System;
using UnityEngine;

/// Tuning values for PlayerController, kept in an asset so they can be edited in Play mode
/// without being lost, and shared or swapped per character. Defaults match Yuta's feel.
[CreateAssetMenu(menuName = "JJK/Player Settings", fileName = "PlayerSettings")]
public class PlayerSettings : ScriptableObject
{
    public LocomotionSettings locomotion = new LocomotionSettings();
    public FacingSettings facing = new FacingSettings();
    public JumpSettings jump = new JumpSettings();
    public DashSettings dash = new DashSettings();
    public CombatSettings combat = new CombatSettings();
    public AnimationSettings animation = new AnimationSettings();
}

/// Animator state names and the crossfade into each. Code decides which state plays;
/// the Animator graph only blends within a state.
[Serializable]
public class AnimationSettings
{
    [Header("Locomotion")]
    public string locomotionState = "Locomotion_Combat";
    [Tooltip("Crossfade into Locomotion from most states.")]
    public float locomotionFade = 0.3f;
    [Tooltip("Crossfade into Locomotion when landing recovery ends.")]
    public float locomotionFromLandFade = 0.25f;
    [Tooltip("Crossfade into Locomotion when releasing Guard.")]
    public float locomotionFromGuardFade = 0.4f;

    [Header("Jump")]
    public string jumpState = "Jump rise";
    public float jumpFade = 0.1f;
    [Tooltip("Played once the jump starts falling.")]
    public string fallState = "Fall neutral";
    public float fallFade = 0.2f;
    public string landState = "Land";
    public float landFade = 0.1f;

    [Header("Other")]
    public string dashState = "Directional_Dash";
    public float dashFade = 0.05f;
    public string guardState = "Guard neutral";
    public float guardFade = 0.2f;
}

[Serializable]
public class LocomotionSettings
{
    [Header("Speed")]
    [Tooltip("Run speed. Scaled by Reinforcement.")]
    public float runSpeed = 8f;
    [Tooltip("Walk speed. Not scaled by Reinforcement or sprint.")]
    public float walkSpeed = 2f;
    [Tooltip("Stick magnitude below which there is no movement.")]
    [Range(0f, 1f)] public float walkThreshold = 0.15f;
    [Tooltip("Stick magnitude at or above which movement is a run.")]
    [Range(0f, 1f)] public float runThreshold = 0.8f;
    [Tooltip("Seconds to ease toward the stick's target velocity: walk<->run, starting, and reversing.")]
    public float velocitySmoothTime = 0.15f;
    [Tooltip("Seconds the stick may sit in the dead zone before it counts as Idle (covers a fast flick through center).")]
    public float idleInputGraceTime = 0.1f;
    [Tooltip("Downward push while grounded, keeps the CharacterController on slopes and steps.")]
    public float groundStickForce = -2f;

    [Header("Sprint buildup")]
    [Tooltip("Seconds of continuous running before the sprint bonus starts building.")]
    public float sprintBuildupDelay = 1.5f;
    [Tooltip("Seconds to ramp from no bonus to the full bonus.")]
    public float sprintRampDuration = 3f;
    [Tooltip("Speed added on top of run speed at full sprint.")]
    public float sprintMaxBonus = 2f;
    [Tooltip("Run animation playback speed at full sprint.")]
    public float sprintMaxAnimSpeed = 1.15f;
    [Tooltip("Allow the sprint bonus while Reinforcement is boosting speed.")]
    public bool sprintWhileReinforced = true;

    [Header("Animation blend")]
    [Tooltip("Seconds for MoveAmount / DirZ to glide to their new value.")]
    public float blendSmoothTime = 0.06f;
    [Tooltip("Damping on the walk/run blend parameter.")]
    public float walkRunBlendDamp = 0.1f;
}

[Serializable]
public class FacingSettings
{
    [Tooltip("Stick angle (degrees from toward-target) up to which movement is a forward run; beyond it, a backpedal.")]
    public float forwardBackThreshold = 100f;
    [Tooltip("Extra degrees before switching back, so input near the boundary doesn't flip-flop.")]
    public float categoryHysteresis = 5f;
    [Tooltip("Max turn speed in degrees per second while moving.")]
    public float turnSpeed = 720f;
    [Tooltip("Smoothing on the facing angle; filters stick noise without noticeable lag.")]
    public float smoothTime = 0.1f;
}

[Serializable]
public class JumpSettings
{
    public float height = 1.5f;
    public float timeToApex = 0.4f;
    public float timeToFall = 0.3f;
    [Tooltip("Horizontal speed of a directional jump. Scaled by Reinforcement.")]
    public float airSpeed = 10f;
    [Tooltip("How fast the stick can steer mid-air (units/sec²). 0 = committed jump.")]
    public float airControl = 0f;
    [Tooltip("Seconds after landing before Jump can be used again.")]
    public float cooldown = 0.5f;
    [Tooltip("Seconds after a dash ends before Jump can be used from Locomotion.")]
    public float lockoutAfterDash = 0.2f;
    [Tooltip("Seconds after leaving a ledge during which Jump still works.")]
    public float coyoteTime = 0.1f;
    [Tooltip("A Jump pressed this long before landing fires on landing.")]
    public float bufferWindow = 0.15f;
    [Tooltip("Landing recovery when no stick input is held.")]
    public float landingRecovery = 0.1f;
}

[Serializable]
public class DashSettings
{
    [Header("Straight dash (outside combat range)")]
    public float speed = 13f;
    [Tooltip("Units/sec² the dash slows by. Higher = shorter, snappier.")]
    public float deceleration = 25f;

    [Header("In combat range")]
    [Tooltip("Degrees a sideways dash sweeps around the target.")]
    public float sideDodgeAngle = 90f;
    [Tooltip("ON: the side dash hits exactly Side Dodge Angle in the straight dash's duration. OFF: it uses the straight dash's speed/deceleration and stops early at the angle.")]
    public bool solveAngleFromDuration = true;
    [Tooltip("Distance of a forward/back dash in combat range.")]
    public float inRangeDistance = 4f;
    [Tooltip("Constant speed of a forward/back dash in combat range.")]
    public float inRangeSpeed = 10f;

    [Header("Timing")]
    [Tooltip("Window after pressing Dash to pick a direction. No direction = no dash.")]
    public float aimWindow = 0.2f;
    [Tooltip("Seconds after a dash ends before the next one.")]
    public float cooldown = 0.3f;
    [Tooltip("Fraction of the dash (0-1) that must pass before an attack can cancel it.")]
    [Range(0f, 1f)] public float attackCancelWindow = 0.3f;
    [Tooltip("Seconds after landing before Dash can be used from Locomotion.")]
    public float lockoutAfterLanding = 0.25f;
    [Tooltip("A Dash pressed during cooldown fires when it clears, if within this window.")]
    public float bufferWindow = 0.15f;
}

[Serializable]
public class CombatSettings
{
    [Tooltip("Within this distance of the target, side dashes orbit and Jump is disabled.")]
    public float combatRange = 7f;
    [Tooltip("Closest any movement may get to the target.")]
    public float attackRange = 1.2f;
    [Tooltip("Damage reduction while guarding (0 = none, 1 = full block).")]
    [Range(0f, 1f)] public float guardDamageReduction = 0.5f;
    [Tooltip("How quickly knockback velocity decays during hitstun.")]
    public float knockbackDecay = 15f;
    [Tooltip("Attack animation speed while Reinforcement is off (AttackSpeedMultiplier parameter).")]
    public float attackAnimSpeed = 1f;
}
