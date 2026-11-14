using System;
using UnityEngine;

/// Locked-on player. Owns the current state (Player/States) and decides when to switch:
/// input and gameplay events come in here, the active state's Tick does the per-frame
/// work. PlayerMotor moves the CharacterController, PlayerTargeting handles lock-on,
/// PlayerAnimation is the only Animator writer (each state plays its own animation in
/// Enter), and tuning lives in a PlayerSettings asset. Requires CharacterController +
/// Animator.
[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(Animator))]
public class PlayerController : MonoBehaviour
{
    public enum ActionState { Locomotion, Jumping, Landing, DashWindup, Dashing, Guarding, UsingRCT, Attacking, Hitstun }
    public enum GuardReactionResult { None, TimedBlock, Parry }

    [Header("Refs")]
    [SerializeField] private InputReader input;
    [SerializeField] private PlayerSettings settings;
    [Tooltip("Optional starting target. Lock-on replaces it with the nearest TargetGroup's Primary.")]
    [SerializeField] private Transform target;
    [Tooltip("Optional. If null, stats use base values.")]
    [SerializeField] private Reinforcement reinforcement;
    [Tooltip("Optional. Movement follows Locomotion rules while it's active.")]
    [SerializeField] private ReverseCursedTechnique rct;
    [Tooltip("Optional. Movement is locked while a combo is active.")]
    [SerializeField] private ComboController combo;

    [Header("Debug")]
    [SerializeField] private bool showLockOnDebugOverlay = true;
    [SerializeField] private float debugOrbitRadius;
    [SerializeField] private float debugActualDistance;

    // ================================================================ Public API

    public event Action<Transform> TargetChanged;
    public Transform Target => targeting != null ? targeting.Target : target;
    public bool CanUseRCT => action == ActionState.Locomotion;
    public bool CanAttack => action == ActionState.Locomotion ||
        (action == ActionState.Dashing && dash.Progress01 >= settings.dash.attackCancelWindow);
    public bool IsGuarding => action == ActionState.Guarding;
    public float GuardDamageReduction => settings.combat.guardDamageReduction;
    /// Hook for a future Sidestep system.
    public bool IsVulnerableToSidestep => action == ActionState.Locomotion;
    public bool IsInCombatRange() => Target != null && Vector3.Distance(transform.position, Target.position) <= settings.combat.combatRange;

    /// Called by ComboController when a move starts.
    public void BeginAttackStep(float distance, float duration) => attack.BeginStep(distance, duration);

    /// Called by an attacker's ComboController when a hit lands on this character.
    public void ApplyHitReaction(float hitstunDuration, Vector3 knockbackVelocity)
    {
        rct?.ForceDeactivate();
        ChangeState(ActionState.Hitstun); // before Begin: leaving a dash clears velocity
        hitstun.Begin(hitstunDuration, knockbackVelocity);
    }

    /// Called when a hit lands while guarding: timed block / parry check.
    public GuardReactionResult EvaluateGuardTiming(float reactionWindowDuration, float parryThreshold, out float quality)
    {
        quality = 0f;
        return IsGuarding ? guard.EvaluateTiming(reactionWindowDuration, parryThreshold, out quality) : GuardReactionResult.None;
    }

    // ================================================================ Shared with states

    internal PlayerSettings Settings => settings;
    internal InputReader Input => input;
    internal PlayerAnimation Anim { get; private set; }
    internal PlayerMotor Motor { get; private set; }
    internal PlayerCooldowns Cooldowns { get; } = new PlayerCooldowns();
    internal LocomotionState Locomotion => locomotion;
    internal Reinforcement Reinforcement => reinforcement;
    /// Horizontal velocity, handed from state to state.
    internal Vector3 Velocity { get; set; }

    internal float MoveSpeedMultiplier => reinforcement != null ? reinforcement.MoveSpeedMultiplier : 1f;
    internal float JumpMultiplier => reinforcement != null ? reinforcement.JumpMultiplier : 1f;
    internal float DashSpeedMultiplier => reinforcement != null ? reinforcement.DashSpeedMultiplier : 1f;
    internal float AirSpeedMultiplier => reinforcement != null ? reinforcement.AirSpeedMultiplier : 1f;

    /// Switches state: Exit on the old one, Enter on the new one. Locomotion and RCT share
    /// one state object, so switching between them skips Exit/Enter unless reenter is set.
    internal void ChangeState(ActionState next, bool reenter = false)
    {
        PlayerState from = states[(int)action];
        PlayerState to = states[(int)next];
        action = next;
        if (from == to && !reenter) return;
        from.Exit();
        to.Enter();
    }

    /// fade: crossfade into the Locomotion animation; null = the default.
    internal void EnterLocomotion(float? fade = null)
    {
        locomotion.EnterFade = fade;
        ChangeState(ActionState.Locomotion, reenter: true);
    }

    // ================================================================ State

    private PlayerTargeting targeting;
    private ActionState action = ActionState.Locomotion;
    private PlayerState[] states;
    private LocomotionState locomotion;
    private DashState dash;
    private GuardState guard;
    private AttackState attack;
    private HitstunState hitstun;

    private void Awake()
    {
        if (settings == null)
        {
            Debug.LogError($"[PlayerController] '{name}' has no PlayerSettings asset assigned.", this);
            enabled = false;
            return;
        }

        targeting = new PlayerTargeting(input, transform, target);
        Motor = new PlayerMotor(GetComponent<CharacterController>(), settings, () => targeting.Target);
        Anim = new PlayerAnimation(GetComponent<Animator>(), settings.animation);
        targeting.Changed += OnTargetChanged;

        locomotion = new LocomotionState(this);
        dash = new DashState(this);
        guard = new GuardState(this);
        attack = new AttackState(this);
        hitstun = new HitstunState(this);

        states = new PlayerState[Enum.GetValues(typeof(ActionState)).Length];
        states[(int)ActionState.Locomotion] = locomotion;
        states[(int)ActionState.UsingRCT] = locomotion; // RCT moves under Locomotion rules
        states[(int)ActionState.Jumping] = new JumpState(this);
        states[(int)ActionState.Landing] = new LandingState(this);
        states[(int)ActionState.DashWindup] = new DashWindupState(this);
        states[(int)ActionState.Dashing] = dash;
        states[(int)ActionState.Guarding] = guard;
        states[(int)ActionState.Attacking] = attack;
        states[(int)ActionState.Hitstun] = hitstun;

        Anim.SetAttackSpeed(settings.combat.attackAnimSpeed);
        Anim.SetRunAnimSpeed(1f);
    }

    private void OnEnable()
    {
        if (settings == null) return;

        input.JumpEvent += OnJumpPressed;
        input.DashEvent += OnDashPressed;
        input.GuardEvent += OnGuardPressed;
        input.GuardCanceledEvent += OnGuardReleased;
        if (rct != null) rct.ActiveChanged += OnRCTActiveChanged;
        if (combo != null) combo.ActiveChanged += OnComboActiveChanged;
        if (reinforcement != null) reinforcement.TierChanged += OnReinforcementTierChanged;
        Anim.SetAttackSpeed(settings.combat.attackAnimSpeed);

        targeting.Engage(); // always locked on
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

    private void Update()
    {
        Cooldowns.Tick(Time.deltaTime, Motor.IsGrounded);
        targeting.Tick();

        // A dash pressed during cooldown fires the moment cooldown clears.
        if (Cooldowns.dashBuffer > 0f && Cooldowns.dashCooldown <= 0f && action == ActionState.Locomotion && Cooldowns.dashLockout <= 0f)
        {
            Cooldowns.dashBuffer = 0f;
            ChangeState(ActionState.DashWindup);
        }

        states[(int)action].Tick();
        Motor.EndFrame();

        if (Target != null)
        {
            debugOrbitRadius = Motor.OrbitRadius;
            debugActualDistance = Vector3.Distance(transform.position, Target.position);
        }
    }

    // ================================================================ Transitions

    private void OnJumpPressed()
    {
        bool fromLocomotion = action == ActionState.Locomotion && Cooldowns.jumpLockout <= 0f;
        bool fromDash = action == ActionState.Dashing || action == ActionState.DashWindup;
        bool fromRCT = action == ActionState.UsingRCT;
        bool grounded = Cooldowns.airborneTime <= settings.jump.coyoteTime;

        if ((fromLocomotion || fromDash || fromRCT) && grounded && Cooldowns.jumpCooldown <= 0f && !IsInCombatRange())
        {
            if (fromRCT) rct?.ForceDeactivate();
            ChangeState(ActionState.Jumping);
            return;
        }

        if (action == ActionState.Jumping)
            Cooldowns.jumpBuffer = settings.jump.bufferWindow; // fires on landing
    }

    private void OnDashPressed()
    {
        bool fromLocomotion = action == ActionState.Locomotion && Cooldowns.dashLockout <= 0f;
        bool fromLanding = action == ActionState.Landing;
        bool fromRCT = action == ActionState.UsingRCT;
        bool fromAttack = action == ActionState.Attacking && combo != null && combo.InPunishWindow;
        bool stateOk = fromLocomotion || fromLanding || fromRCT || fromAttack;

        if (stateOk && Cooldowns.dashCooldown <= 0f)
        {
            if (fromRCT) rct?.ForceDeactivate();
            if (fromAttack) combo?.ForceEndCombo();
            ChangeState(ActionState.DashWindup);
            return;
        }

        if (stateOk)
            Cooldowns.dashBuffer = settings.dash.bufferWindow; // only cooldown blocked it
    }

    private void OnGuardPressed()
    {
        bool fromRCT = action == ActionState.UsingRCT;
        if (action != ActionState.Locomotion && !fromRCT) return;
        if (fromRCT) rct?.ForceDeactivate();
        ChangeState(ActionState.Guarding);
    }

    private void OnGuardReleased()
    {
        if (action == ActionState.Guarding) EnterLocomotion(Anim.LocomotionFromGuardFade);
    }

    private void OnRCTActiveChanged(bool active)
    {
        if (active)
        {
            if (action == ActionState.Locomotion) ChangeState(ActionState.UsingRCT);
        }
        else if (action == ActionState.UsingRCT)
        {
            EnterLocomotion();
        }
    }

    private void OnComboActiveChanged(bool active)
    {
        if (active)
        {
            bool fromDash = action == ActionState.Dashing;
            if (action != ActionState.Locomotion && !fromDash) return; // CanAttack gates this
            if (fromDash) dash.StartCooldowns();
            ChangeState(ActionState.Attacking);
        }
        else if (action == ActionState.Attacking)
        {
            EnterLocomotion();
        }
    }

    /// New target: take its distance as the intended orbit radius, then tell listeners
    /// (camera rigs keep their own reference to the enemy).
    private void OnTargetChanged(Transform newTarget)
    {
        Motor.SyncOrbitRadius();
        TargetChanged?.Invoke(newTarget);
    }

    /// Attack anim speed follows Reinforcement's tier. TierChanged fires when the stats
    /// apply, so this stays in sync with movement speed.
    private void OnReinforcementTierChanged(Reinforcement.Tier tier)
    {
        Anim.SetAttackSpeed(tier == Reinforcement.Tier.Off ? settings.combat.attackAnimSpeed : reinforcement.ReinforcedAnimSpeed);
    }

    // ================================================================ Debug drawing

    private void OnGUI()
    {
        if (!showLockOnDebugOverlay) return;
        var style = new GUIStyle { fontSize = 20, normal = { textColor = Target != null ? Color.yellow : Color.white } };
        GUI.Label(new Rect(10, 10, 400, 60), $"Target: {(Target != null ? Target.name : "none")}", style);
    }

    private void OnDrawGizmos()
    {
        if (Target == null || settings == null) return;

        Gizmos.color = Color.cyan;
        DrawGizmoCircle(Target.position, settings.combat.combatRange, 48);
        Gizmos.color = Color.red;
        DrawGizmoCircle(Target.position, settings.combat.attackRange, 32);
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(Target.position + Vector3.up * 1.8f, 0.3f);
    }

    private static void DrawGizmoCircle(Vector3 center, float radius, int segments)
    {
        if (radius <= 0f) return;
        Vector3 prev = center + new Vector3(radius, 0f, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float angle = i / (float)segments * 360f * Mathf.Deg2Rad;
            Vector3 next = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
            Gizmos.DrawLine(prev, next);
            prev = next;
        }
    }
}
