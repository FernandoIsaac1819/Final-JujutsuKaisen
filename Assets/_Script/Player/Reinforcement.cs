using UnityEngine;

/// <summary>
/// Cursed energy reinforcement: toggles enhanced physical stats on/off. Single press
/// toggles Normal on/off; a fast double-press escalates into High output (or jumps
/// straight to it from Off, since a double-click is just two presses close together).
/// High output always runs its full timer once triggered — no early cancel.
/// PlayerController reads the multiplier properties below; this script only touches
/// PlayerController back for the Guard-blocks-Reinforcement check.
/// </summary>
public class Reinforcement : MonoBehaviour
{
    public enum Tier { Off, Normal, High }

    [Header("Refs")]
    [Tooltip("The cursed energy pool this reinforcement draws from.")]
    [SerializeField] private CursedEnergy cursedEnergy;
    [Tooltip("Input source for the reinforce button.")]
    [SerializeField] private InputReader input;
    [Tooltip("Blocks activation entirely while guarding.")]
    [SerializeField] private PlayerController playerController;
    [Tooltip("Optional. Reinforcement refuses to activate at all while RCT is active — the " +
             "two are mutually exclusive, and this is a cleaner block than relying only on " +
             "RCT continuously forcing the tier back off.")]
    [SerializeField] private ReverseCursedTechnique rct;

    [Header("Activation Timing")]
    [Tooltip("Seconds after activating (or escalating) a tier before its stat bonuses actually apply.")]
    [SerializeField] private float statActivationDelay = 0.3f;

    [Header("Drain Rates (energy per second)")]
    [Tooltip("Cursed energy drained per second while in Normal output.")]
    [SerializeField] private float normalDrainRate = 15f;
    [Tooltip("Cursed energy drained per second while in High output.")]
    [SerializeField] private float highDrainRate = 60f;

    [Header("High Output")]
    [Range(0f, 1f)]
    [Tooltip("Minimum fraction of max cursed energy required to trigger High output.")]
    [SerializeField] private float highOutputThreshold = 0.2f;
    [Tooltip("How long High output lasts before automatically turning off.")]
    [SerializeField] private float highOutputDuration = 5f;
    [Tooltip("Max time between two presses to count as a double-click (triggers High).")]
    [SerializeField] private float doubleClickWindow = 0.3f;

    [Header("Stat Multipliers — Base (Off)")]
    [Tooltip("Damage reduction while Reinforcement is Off. Higher = more durable. " +
             "0 = no reduction, 1 = fully blocks all damage.")]
    [Range(0f, 1f)]
    [SerializeField] private float baseDurabilityReduction = 0f;
    [Tooltip("Outgoing damage multiplier while Reinforcement is Off. Higher = more damage dealt.")]
    [SerializeField] private float baseDamageMultiplier = 1f;

    [Header("Stat Multipliers — Normal")]
    [Tooltip("Attack animation playback speed while Reinforcement is Normal OR High — same " +
             "value for both tiers, no further increase at High. Read by PlayerController, " +
             "which applies it only to Animator states referencing AttackSpeedMultiplier.")]
    [SerializeField] private float reinforcedAttackAnimSpeed = 1.3f;
    [Tooltip("Move speed multiplier while in Normal output.")]
    [SerializeField] private float normalMoveSpeedMultiplier = 1.2f;
    [Tooltip("Jump height multiplier while in Normal output.")]
    [SerializeField] private float normalJumpMultiplier = 1.1f;
    [Tooltip("Dash speed multiplier while in Normal output.")]
    [SerializeField] private float normalDashSpeedMultiplier = 1.15f;
    [Tooltip("Air horizontal speed multiplier while in Normal output.")]
    [SerializeField] private float normalAirSpeedMultiplier = 1.1f;
    [Tooltip("Damage reduction while in Normal output. Higher = more durable.")]
    [Range(0f, 1f)]
    [SerializeField] private float normalDurabilityReduction = 0.15f;
    [Tooltip("Outgoing damage multiplier while in Normal output. Higher = more damage dealt.")]
    [SerializeField] private float normalDamageMultiplier = 1.2f;

    [Header("Stat Multipliers — High")]
    [Tooltip("Move speed multiplier while in High output.")]
    [SerializeField] private float highMoveSpeedMultiplier = 1.6f;
    [Tooltip("Jump height multiplier while in High output.")]
    [SerializeField] private float highJumpMultiplier = 1.4f;
    [Tooltip("Dash speed multiplier while in High output.")]
    [SerializeField] private float highDashSpeedMultiplier = 1.5f;
    [Tooltip("Air horizontal speed multiplier while in High output.")]
    [SerializeField] private float highAirSpeedMultiplier = 1.4f;
    [Tooltip("Damage reduction while in High output. Higher = more durable.")]
    [Range(0f, 1f)]
    [SerializeField] private float highDurabilityReduction = 0.4f;
    [Tooltip("Outgoing damage multiplier while in High output. Higher = more damage dealt.")]
    [SerializeField] private float highDamageMultiplier = 1.5f;
    

    public Tier CurrentTier { get; private set; } = Tier.Off;

    /// Lags CurrentTier — only updates once statActivationDelay has elapsed since the last
    /// activation/escalation, since stat bonuses shouldn't kick in instantly on press.
    private Tier _effectiveTier = Tier.Off;
    private bool _pendingActivation;
    private float _activationTimer;

    /// Fired only when the tier actually changes.
    public event System.Action<Tier> TierChanged;

    private void SetTier(Tier newTier)
    {
        if (CurrentTier == newTier) return;
        CurrentTier = newTier; // changes immediately — this alone drives drain rate/cooldowns

        if (newTier == Tier.Off)
        {
            // Deactivation is instant on every front — stats, aura, everything.
            _pendingActivation = false;
            _effectiveTier = Tier.Off;
            TierChanged?.Invoke(Tier.Off);
        }
        else
        {
            // Activation is delayed — TierChanged (which the aura listens to) doesn't fire
            // yet. Stats and the aura's fade-in both start together once the timer elapses.
            _pendingActivation = true;
            _activationTimer = statActivationDelay;
        }
    }

    /// Forces the tier to Off immediately, bypassing normal toggle logic. Used by other
    /// systems (e.g. RCT) that need to guarantee Reinforcement isn't active.
    public void ForceOff() => SetTier(Tier.Off);

    public float MoveSpeedMultiplier => _effectiveTier switch
    {
        Tier.Normal => normalMoveSpeedMultiplier,
        Tier.High => highMoveSpeedMultiplier,
        _ => 1f
    };

    public float JumpMultiplier => _effectiveTier switch
    {
        Tier.Normal => normalJumpMultiplier,
        Tier.High => highJumpMultiplier,
        _ => 1f
    };

    public float DashSpeedMultiplier => _effectiveTier switch
    {
        Tier.Normal => normalDashSpeedMultiplier,
        Tier.High => highDashSpeedMultiplier,
        _ => 1f
    };

    public float AirSpeedMultiplier => _effectiveTier switch
    {
        Tier.Normal => normalAirSpeedMultiplier,
        Tier.High => highAirSpeedMultiplier,
        _ => 1f
    };
    public float ReinforcedAnimSpeed => reinforcedAttackAnimSpeed;

    /// Damage reduction (0-1, higher = more durable) — this is the intuitive value shown
    /// in the inspector.
    public float DurabilityReduction => _effectiveTier switch
    {
        Tier.Normal => normalDurabilityReduction,
        Tier.High => highDurabilityReduction,
        _ => baseDurabilityReduction
    };

    /// Outgoing damage multiplier — higher deals more damage. Same gating as every other
    /// stat here: reads _effectiveTier, so it's delayed by statActivationDelay like the rest.
    public float DamageMultiplier => _effectiveTier switch
    {
        Tier.Normal => normalDamageMultiplier,
        Tier.High => highDamageMultiplier,
        _ => baseDamageMultiplier
    };

    /// True once Normal or High has actually taken effect (same statActivationDelay gating
    /// as every multiplier above) — for anything that just needs an on/off check rather than
    /// a specific multiplier, like attack animation speed.
    public bool IsEffectivelyReinforced => _effectiveTier != Tier.Off;

    /// Derived from DurabilityReduction for actual damage math — DamageReceiver multiplies
    /// incoming damage by this, not by DurabilityReduction directly.
    public float DamageTakenMultiplier => 1f - DurabilityReduction;

    private float lastPressTime = -999f;
    private float highOutputTimer;

    private void OnEnable() => input.ReinforceEvent += OnReinforcePressed;
    private void OnDisable() => input.ReinforceEvent -= OnReinforcePressed;

    private void Update()
    {
        if (_pendingActivation)
        {
            _activationTimer -= Time.deltaTime;
            if (_activationTimer <= 0f)
            {
                _pendingActivation = false;
                _effectiveTier = CurrentTier;
                TierChanged?.Invoke(CurrentTier); // aura starts fading in now, in sync with stats applying
            }
        }

        if (CurrentTier == Tier.High)
        {
            highOutputTimer -= Time.deltaTime;
            if (highOutputTimer <= 0f || cursedEnergy.Current <= 0f)
                SetTier(Tier.Off);
        }
        else if (CurrentTier == Tier.Normal && cursedEnergy.Current <= 0f)
        {
            SetTier(Tier.Off); // ran fully out
        }

        float drainRate = CurrentTier switch
        {
            Tier.Normal => normalDrainRate,
            Tier.High => highDrainRate,
            _ => 0f
        };

        if (drainRate > 0f)
            cursedEnergy.Drain(drainRate);
    }

    private void OnReinforcePressed()
    {
        if (rct != null && rct.IsActiveOrDeactivating) return; // mutually exclusive — blocked through RCT's full deactivation tail, not just while held

        float now = Time.time;
        bool isDoubleClick = (now - lastPressTime) <= doubleClickWindow;
        lastPressTime = now;

        if (isDoubleClick)
            TryEnterHigh();
        else
            ToggleNormal();
    }

    private void ToggleNormal()
    {
        if (CurrentTier == Tier.High) return; // High ignores presses until its timer finishes
        SetTier(CurrentTier == Tier.Off ? Tier.Normal : Tier.Off);
    }

    private void TryEnterHigh()
    {
        if (CurrentTier == Tier.High) return;
        if (!cursedEnergy.HasPercentage(highOutputThreshold)) return;

        SetTier(Tier.High);
        highOutputTimer = highOutputDuration;
    }
}