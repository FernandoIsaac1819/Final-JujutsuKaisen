using UnityEngine;

/// <summary>
/// Drives grounded 8-directional locomotion (always on, no Free/Combat mode toggle),
/// jump, dash, and guard lock. Guard is a fully static hold — no movement, no rotation
/// beyond tracking the target, no interruption except releasing it. "Combat" as a broader
/// concept is intentionally not represented here anymore — that's reserved for whatever
/// actual fight-state logic gets built later. Requires CharacterController + Animator on
/// the same GameObject. Root motion is not used anywhere in this script.
/// </summary>
[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(Animator))]
public partial class PlayerControllerSplit : MonoBehaviour
{
    public enum ActionState { Locomotion, Jumping, Landing, DashWindup, Dashing, Guarding, UsingRCT, Attacking, Hitstun }

    [Header("Refs")]
    [SerializeField] private InputReader input;
    [SerializeField] private Transform target; // optional — if set, character faces it; if null, keeps current facing
    [SerializeField] private Reinforcement reinforcement; // optional — if null, stats just use base values
    [Tooltip("Attack animation playback speed while Reinforcement is Off. Only affects " +
             "states in the Animator that reference the AttackSpeedMultiplier parameter — " +
             "see setup instructions. Does not affect Locomotion, Guard, Dash, etc.")]
    [SerializeField] private float normalAnimSpeed = 1f;
    [Tooltip("Optional. Locks movement while active, based on its own ActiveChanged event.")]
    [SerializeField] private ReverseCursedTechnique rct;
    [Tooltip("Optional. Locks movement while attacking, based on its own ActiveChanged event.")]
    [SerializeField] private ComboController combo;

    public bool CanUseRCT => action == ActionState.Locomotion;
    public bool CanAttack => action == ActionState.Locomotion ||
        (action == ActionState.Dashing && DashProgress01 >= dashAttackCancelWindow);

    /// 0 at the instant a dash launches, 1 once it's fully decelerated — based on how far
    /// the current speed has decayed from its launch value, not on animation clip time,
    /// since the dash's actual duration varies with Reinforcement's speed multiplier.
    private float DashProgress01
    {
        get
        {
            if (dashIsOrbital)
            {
                if (dashLaunchAngularSpeed <= 0.0001f) return 1f;
                return 1f - Mathf.Clamp01(Mathf.Abs(dashOrbitAngularSpeed) / dashLaunchAngularSpeed);
            }
            if (dashLaunchSpeed <= 0.0001f) return 1f;
            return 1f - Mathf.Clamp01(velocity.magnitude / dashLaunchSpeed);
        }
    }

    [Header("Hitstun")]
    [Tooltip("How quickly knockback velocity decays back to zero during hitstun.")]
    [SerializeField] private float knockbackDecay = 15f;
    private float hitstunTimer;

    /// Stub hook for a future Sidestep system — not implemented yet, but this is the
    /// victim-side interrupt point it would check. A real Sidestep would query something
    /// like this to know whether the character is currently vulnerable/interruptible.
    public bool IsVulnerableToSidestep => action == ActionState.Locomotion;

    [Header("Movement")]
    [SerializeField] private float moveSpeed = 4.5f; // run speed
    [SerializeField] private float dirBlendTime = 0.1f; // eases DirX/DirZ instead of snapping instantly

    [Header("Walk / Run")]
    [Tooltip("Stick magnitude (0-1) below which there's no movement at all.")]
    [Range(0f, 1f)]
    [SerializeField] private float walkThreshold = 0.15f;
    [Tooltip("Stick magnitude (0-1) at or above which movement becomes running instead of walking.")]
    [Range(0f, 1f)]
    [SerializeField] private float runThreshold = 0.35f;
    [Tooltip("Fixed walk speed — NOT affected by Reinforcement, unlike run (moveSpeed).")]
    [SerializeField] private float walkSpeed = 2f;

    [Header("Sprint Buildup")]
    [Tooltip("How long you must continuously hold run-level input before the speed bonus starts building. Resets instantly the moment you drop below the run threshold.")]
    [SerializeField] private float sprintBuildupDelay = 1f;
    [Tooltip("How long it takes to ramp from 0 to the max bonus, once buildup starts.")]
    [SerializeField] private float sprintRampDuration = 2f;
    [Tooltip("Extra speed ADDED on top of moveSpeed * Reinforcement's multiplier once fully built up — not a replacement value.")]
    [SerializeField] private float sprintMaxBonus = 5f;
    [Tooltip("Animation playback speed multiplier at max sprint bonus (1 = unaffected). Only affects Animator states with Speed Multiplier bound to LocomotionSpeedMultiplier — see setup instructions.")]
    [SerializeField] private float sprintMaxAnimSpeedMultiplier = 1.2f;
    [Tooltip("Off for now — sprint bonus is blocked entirely while Reinforcement is actively boosting move speed. Flip on to allow it later.")]
    [SerializeField] private bool sprintBonusAppliesWhileReinforced = false;
    private float sprintHoldTimer;

    [Header("Movement-Direction Facing (Locomotion/RCT)")]
    [Tooltip("Degrees from straight forward/backward within which movement uses the " +
             "Forward/Backward animation (rotated to face the actual movement direction) " +
             "instead of the dedicated strafe-left/right animation. Must leave room for a " +
             "strafe zone — e.g. 60 leaves 60°-wide strafe zones on each side.")]
    [Range(10f, 89f)]
    [SerializeField] private float forwardBackThreshold = 60f;
    [Tooltip("Extra angular buffer before switching category again once already in one — " +
             "prevents rapid flip-flopping between animations when input hovers near the boundary.")]
    [SerializeField] private float categoryHysteresis = 5f;
    [Tooltip("Degrees/sec the character rotates to face the movement direction while moving. " +
             "Separate from FaceTarget, which resumes once movement stops.")]
    [SerializeField] private float movementFacingTurnSpeed = 720f;
    [Tooltip("How quickly the rotation TARGET direction itself is smoothed — small values " +
             "just filter out stick noise at low magnitude without adding noticeable lag to " +
             "intentional turns. Separate from Movement Facing Turn Speed, which caps how " +
             "fast the character can actually rotate.")]
    [SerializeField] private float rotationDirSmoothTime = 0.08f;
    [Tooltip("How long the DirX/DirZ blend tree input takes to glide between category points " +
             "(Forward/Backward/StrafeLeft/StrafeRight/Idle) when the category changes. This " +
             "is the actual smoothness dial — shorter feels snappier, longer feels floatier.")]
    [SerializeField] private float locomotionBlendSmoothTime = 0.12f;

    [Tooltip("How quickly currentSpeed eases between walk and run (or from idle into either) — separate from moveAccelTime, which still governs Guard's speed easing.")]
    [SerializeField] private float walkRunTransitionTime = 0.15f;
    [Tooltip("Seconds to accelerate/decelerate to the target move speed. This is what actually smooths " +
             "the handoff back into Locomotion after Jump/Dash/Landing — without it, movement snaps to " +
             "full speed instantly the moment there's input, no matter how smooth the state before it was.")]
    [SerializeField] private float moveAccelTime = 0.15f;
    private Vector3 velocity; // shared by Locomotion, Dash, and Jump's horizontal component — one value, no handoff needed
    private float currentSpeed;
    private float speedVelocityRef;

    [Header("Jump")]
    [SerializeField] private float jumpHeight = 2f;
    [SerializeField] private float timeToApex = 0.35f;
    [SerializeField] private float timeToFall = 0.28f;
    [SerializeField] private float airHorizontalSpeed = 4f;
    [Tooltip("Seconds after landing before Jump can be used again.")]
    [SerializeField] private float jumpCooldown = 0.5f;
    private float jumpCooldownTimer;
    [Tooltip("Seconds after a dash ends before Jump can be used, letting the Dash→Locomotion " +
             "animator transition finish before cutting to Jump. Only applies to jumping from " +
             "settled Locomotion — jumping to directly interrupt an in-progress dash is unaffected.")]
    [SerializeField] private float jumpLockoutAfterDash = 0.15f;
    private float jumpLockoutTimer;
    [Tooltip("Grace window after walking off a ledge where Jump still works, as if still grounded.")]
    [SerializeField] private float coyoteTime = 0.1f;
    private float airborneTimer;
    [Tooltip("Window before landing where a Jump press is remembered and fires automatically the " +
             "instant you land, instead of requiring frame-perfect timing.")]
    [SerializeField] private float jumpBufferWindow = 0.15f;
    private float jumpBufferTimer;
    [Tooltip("Units/sec² horizontal velocity can be steered by input while airborne. 0 = fully locked " +
             "momentum (current behavior) — raise this later for characters that need real air control.")]
    [SerializeField] private float airControl = 0f;

    [Header("Combat Range")]
    [Tooltip("Distance from the target within which Dash becomes an orbiting side-step " +
             "instead of a straight-line dash, and Jump is disabled entirely.")]
    [SerializeField] private float combatRange = 5f;
    [Tooltip("Degrees a lateral dash sweeps around the target.")]
    [SerializeField] private float sideDodgeOrbitAngle = 45f;
    [Tooltip("OFF (Hard Cap): sweeps with the same natural feel as a straight dash " +
             "(speed/decel derived from dashSpeed/dashDeceleration/radius), but stops early " +
             "if the angle above is reached first — duration can differ slightly from a " +
             "straight dash. ON (Solve From Duration): speed/decel are computed backward so " +
             "the sweep reaches exactly that angle AND finishes in the same duration as a " +
             "straight dash, guaranteed — but the sweep's speed is fixed by that math " +
             "instead of coming from dashSpeed's natural feel.")]
    [SerializeField] private bool dashAngleSolveFromDuration = false;
    [Tooltip("The ideal distance from the target for performing attacks — also a hard floor " +
             "for ALL movement (walking, dashing, jumping, attack forward steps), enforced " +
             "every frame, so nothing can ever cross through it. Attack forward steps work " +
             "toward this distance instead of a fixed distance, naturally getting reduced " +
             "or skipped if already this close.")]
    [SerializeField] private float attackRange = 1f;

    public bool IsInCombatRange() => target != null && Vector3.Distance(transform.position, target.position) <= combatRange;

    [Header("Dash")]
    [SerializeField] private float dashSpeed = 10f;
    [Tooltip("Units/sec² the dash decelerates by — same idea as Jump's gravity. Higher = shorter, " +
             "snappier dash. Lower = longer, more gradual. The dash ends naturally when velocity hits 0.")]
    [SerializeField] private float dashDeceleration = 25f;
    [Tooltip("Window after pressing Dash to flick a direction. Movement is frozen during this window. " +
             "If it expires with no direction given, the dash fires forward.")]
    [SerializeField] private float dashAimWindow = 0.15f;
    [Tooltip("Seconds after a dash fully stops before Dash can be used again.")]
    [SerializeField] private float dashCooldown = 0.5f;
    [Tooltip("Fraction of the dash's deceleration (0-1) that must complete before an attack " +
             "can interrupt it. 0 = interruptible immediately at launch. 1 = only " +
             "interruptible once fully decelerated (functionally never, since it'd already " +
             "be ending on its own by then).")]
    [Range(0f, 1f)]
    [SerializeField] private float dashAttackCancelWindow = 0.7f;
    [Tooltip("Seconds after landing recovery finishes before Dash can be used, letting the " +
             "Land→Locomotion animator transition finish before cutting to Dash. Only applies to " +
             "dashing from settled Locomotion — dashing to directly interrupt Landing is unaffected.")]
    [SerializeField] private float dashLockoutAfterLanding = 0.15f;
    private float dashLockoutTimer;
    [Tooltip("Window where a Dash press during cooldown is remembered and fires automatically once " +
             "cooldown clears, instead of requiring the player to re-time a second press.")]
    [SerializeField] private float dashBufferWindow = 0.15f;
    private float dashBufferTimer;
    private float dashAimTimer;
    private float dashCooldownTimer;
    private float dashLaunchMultiplier = 1f;
    private bool dashIsOrbital;
    private float dashLaunchSpeed; // captured at launch — straight dash's initial speed magnitude, for progress tracking
    private float dashLaunchAngularSpeed; // captured at launch — orbital dash's initial angular speed magnitude
    private float dashOrbitAngularSpeed;
    private float dashOrbitAngleRemaining;
    private float dashOrbitAngularDecel; // only meaningful in Solve From Duration mode — fixed at launch

    [Header("Landing")]
    [SerializeField] private float landingRecoveryDuration = 0.15f;
    private float landingTimer;

    [Header("Grounding")]
    [SerializeField] private float groundedStickForce = -2f;

    [Header("Debug (temporary — remove once orbit issue is solved)")]
    [SerializeField] private float debugOrbitRadius;
    [SerializeField] private float debugActualDistance;

    private CharacterController controller;
    private Animator animator;

    private ActionState action = ActionState.Locomotion;

    private float MoveSpeedMultiplier => reinforcement != null ? reinforcement.MoveSpeedMultiplier : 1f;
    private float JumpMultiplier => reinforcement != null ? reinforcement.JumpMultiplier : 1f;
    private float DashSpeedMultiplier => reinforcement != null ? reinforcement.DashSpeedMultiplier : 1f;
    private float AirSpeedMultiplier => reinforcement != null ? reinforcement.AirSpeedMultiplier : 1f;

    // jump physics (derived)
    private float gravityUp, gravityDown, jumpVelocity;
    private float verticalVelocity;

    private static readonly int DirXHash = Animator.StringToHash("DirX");
    private static readonly int DirZHash = Animator.StringToHash("DirZ");
    private static readonly int AttackSpeedMultiplierHash = Animator.StringToHash("AttackSpeedMultiplier");
    private static readonly int LocomotionSpeedMultiplierHash = Animator.StringToHash("LocomotionSpeedMultiplier");
    private static readonly int LocomotionSpeedHash = Animator.StringToHash("LocomotionSpeed01");
    private static readonly int JumpHash = Animator.StringToHash("Jump");
    private static readonly int DashHash = Animator.StringToHash("Dash");
    [Tooltip("Exact Animator state name for the dash animation — CrossFaded into directly " +
             "on launch, guaranteeing it interrupts immediately regardless of Exit Time " +
             "settings on any bool-driven transition into it.")]
    [SerializeField] private string dashStateName = "Directional_Dash";
    [SerializeField] private float dashCrossfadeDuration = 0.05f;
    [Tooltip("Exact Animator state name for Locomotion — EnterLocomotion crossfades to this " +
             "explicitly rather than relying on graph-level transitions, so returning to " +
             "Locomotion is reliable regardless of which state was interrupted to get here.")]
    [SerializeField] private string locomotionStateName = "Locomotion_Combat";
    [SerializeField] private float locomotionCrossfadeDuration = 0.1f;
    private static readonly int GuardHash = Animator.StringToHash("Guard");
    private static readonly int GroundedHash = Animator.StringToHash("Grounded");
    private static readonly int LandingHash = Animator.StringToHash("Landing");
    private static readonly int VerticalVelocityHash = Animator.StringToHash("VerticalVelocity");

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        animator = GetComponent<Animator>();
        RecalculateJumpPhysics();
        animator.SetFloat(AttackSpeedMultiplierHash, normalAnimSpeed); // earliest possible point — never left at the Animator's own 0 default
        animator.SetFloat(LocomotionSpeedMultiplierHash, 1f);
    }

    private void OnValidate()
    {
        RecalculateJumpPhysics();
    }

    private void OnEnable()
    {
        input.JumpEvent += OnJumpPressed;
        input.DashEvent += OnDashPressed;
        input.GuardEvent += OnGuardPressed;
        input.GuardCanceledEvent += OnGuardReleased;
        if (rct != null) rct.ActiveChanged += OnRCTActiveChanged;
        if (combo != null) combo.ActiveChanged += OnComboActiveChanged;
        if (reinforcement != null) reinforcement.TierChanged += OnReinforcementTierChanged;
        animator.SetFloat(AttackSpeedMultiplierHash, normalAnimSpeed); // initial value, in case Reinforcement never fires an early event
    }

    private void OnDisable()
    {
        input.JumpEvent -= OnJumpPressed;
        input.DashEvent -= OnDashPressed;
        input.GuardEvent -= OnGuardPressed;
        input.GuardCanceledEvent -= OnGuardReleased;
        if (rct != null) rct.ActiveChanged -= OnRCTActiveChanged;
        if (combo != null) combo.ActiveChanged -= OnComboActiveChanged;
        if (reinforcement != null) reinforcement.TierChanged -= OnReinforcementTierChanged;
    }

    /// Fires in sync with when stats actually apply (TierChanged is already delayed by
    /// statActivationDelay to match MoveSpeedMultiplier etc.), not the raw button press —
    /// so attack animation speed changes exactly when movement speed does. Sets an Animator
    /// PARAMETER, not animator.speed — animator.speed is global and would affect every
    /// clip (Locomotion, Guard, Dash, everything), not just attacks. This parameter only
    /// affects whichever states explicitly reference it via their own "Speed Multiplier"
    /// setting in the Animator graph — see setup instructions. Normal and High use the same
    /// value; there's no further increase for High specifically.
    private void OnReinforcementTierChanged(Reinforcement.Tier tier)
    {
        float value = tier == Reinforcement.Tier.Off ? normalAnimSpeed : reinforcement.ReinforcedAnimSpeed;
        animator.SetFloat(AttackSpeedMultiplierHash, value);
    }

    private void Update()
    {
        if (jumpCooldownTimer > 0f) jumpCooldownTimer -= Time.deltaTime;
        if (dashCooldownTimer > 0f) dashCooldownTimer -= Time.deltaTime;
        if (jumpLockoutTimer > 0f) jumpLockoutTimer -= Time.deltaTime;
        if (dashLockoutTimer > 0f) dashLockoutTimer -= Time.deltaTime;
        if (jumpBufferTimer > 0f) jumpBufferTimer -= Time.deltaTime;
        if (dashBufferTimer > 0f) dashBufferTimer -= Time.deltaTime;

        airborneTimer = controller.isGrounded ? 0f : airborneTimer + Time.deltaTime;

        // A buffered dash fires the instant cooldown clears, same idea as jump buffering on landing.
        if (dashBufferTimer > 0f && dashCooldownTimer <= 0f && action == ActionState.Locomotion && dashLockoutTimer <= 0f)
        {
            dashBufferTimer = 0f;
            EnterDashWindup();
        }

        switch (action)
        {
            case ActionState.Locomotion: TickLocomotion(); break;
            case ActionState.Jumping: TickJump(); break;
            case ActionState.Landing: TickLanding(); break;
            case ActionState.Guarding: TickGuard(); break;
            case ActionState.UsingRCT: TickRCT(); break;
            case ActionState.Attacking: TickAttacking(); break;
            case ActionState.Hitstun: TickHitstun(); break;
            case ActionState.DashWindup: TickDashWindup(); break;
            case ActionState.Dashing: TickDash(); break;
        }

        ClampMinDistanceToTarget();

        if (target != null)
        {
            debugOrbitRadius = orbitRadius;
            debugActualDistance = Vector3.Distance(transform.position, target.position);
        }

        animator.SetBool(GroundedHash, controller.isGrounded);
        animator.SetBool(LandingHash, action == ActionState.Landing);
    }

    /// Pushes the character back out to attackRange if anything this frame moved it
    /// closer than that — applied once, universally, regardless of which state did the moving.
    private void ClampMinDistanceToTarget()
    {
        if (target == null) return;

        Vector3 toPlayer = transform.position - target.position;
        toPlayer.y = 0f;
        float distance = toPlayer.magnitude;

        if (distance < attackRange && distance > 0.0001f)
        {
            Vector3 correctedPos = target.position + toPlayer.normalized * attackRange;
            Vector3 correction = correctedPos - transform.position;
            correction.y = 0f;
            controller.Move(correction + Vector3.up * groundedStickForce * Time.deltaTime);

            if (orbitInitialized) orbitRadius = attackRange; // keep authoritative state in sync
        }
    }

    /// Reduces a proposed horizontal displacement's approach-toward-target component, if
    /// any, so it can't push closer than attackRange — used by anything that moves
    /// straight toward the target (attack forward step, straight dash) so the overshoot
    /// never happens in the first place, instead of ClampMinDistanceToTarget correcting it
    /// one frame later. That one-frame-later correction is what was causing a visible
    /// "slide back" once the original motion stopped and the correction became the only
    /// remaining movement. Only the approach component is touched — lateral/perpendicular
    /// motion in the same displacement passes through untouched.
    private Vector3 ClampApproachToMinDistance(Vector3 desiredDisplacement)
    {
        if (target == null) return desiredDisplacement;

        Vector3 toPlayer = transform.position - target.position;
        toPlayer.y = 0f;
        float currentDistance = toPlayer.magnitude;
        if (currentDistance <= 0.0001f) return desiredDisplacement;

        Vector3 towardTarget = -toPlayer.normalized;
        float approachAmount = Vector3.Dot(desiredDisplacement, towardTarget);
        if (approachAmount <= 0f) return desiredDisplacement; // not approaching at all — nothing to clamp

        float maxApproach = Mathf.Max(0f, currentDistance - attackRange);
        if (approachAmount > maxApproach)
        {
            float excess = approachAmount - maxApproach;
            desiredDisplacement -= towardTarget * excess;
        }

        return desiredDisplacement;
    }

    // ---------------- Gizmos ----------------

    private void OnDrawGizmos()
    {
        if (target == null) return;

        Gizmos.color = Color.cyan;
        DrawGizmoCircle(target.position, combatRange, 48);

        Gizmos.color = Color.red;
        DrawGizmoCircle(target.position, attackRange, 32);
    }

    private void DrawGizmoCircle(Vector3 center, float radius, int segments)
    {
        if (radius <= 0f) return;

        Vector3 prevPoint = center + new Vector3(radius, 0f, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float angle = (i / (float)segments) * 360f * Mathf.Deg2Rad;
            Vector3 nextPoint = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
            Gizmos.DrawLine(prevPoint, nextPoint);
            prevPoint = nextPoint;
        }
    }
}
