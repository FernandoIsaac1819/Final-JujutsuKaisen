using UnityEngine;

/// <summary>
/// Reverse Cursed Technique: hold the button to activate, release to deactivate.
/// Drains cursed energy, heals health, forces Reinforcement off (mutually exclusive —
/// both draw from the same energy pool, and Reinforcement itself refuses to activate at
/// all while RCT is active), and swaps the aura to its white RCT material while active.
/// Movement stays at normal speed while active — Jump and Dash are the things locked out
/// (PlayerController's gates simply don't list UsingRCT as an allowed state for either).
/// Fully self-contained, same shape as Reinforcement — PlayerController doesn't own any
/// RCT logic, it just reacts to ActiveChanged to enter/exit the UsingRCT state.
/// </summary>
public class ReverseCursedTechnique : MonoBehaviour
{
    [Header("Refs")]
    [Tooltip("The cursed energy pool RCT drains from.")]
    [SerializeField] private CursedEnergy cursedEnergy;
    [Tooltip("The health pool RCT heals.")]
    [SerializeField] private Health health;
    [Tooltip("Input source for the RCT button.")]
    [SerializeField] private InputReader input;
    [Tooltip("Optional. Forced off the instant RCT activates, and kept off for as long as " +
             "RCT is held — the two are mutually exclusive since both drain the same pool.")]
    [SerializeField] private Reinforcement reinforcement;
    [Tooltip("Optional. Swaps the aura to the white RCT material while active.")]
    [SerializeField] private JJK.Aura.Aura_Controller auraController;
    [Tooltip("The character's movement controller. Checked before activating (RCT can " +
             "only start from Locomotion), and notified via ActiveChanged to lock/unlock movement.")]
    [SerializeField] private PlayerController playerController;

    [Header("Rates")]
    [Tooltip("Cursed energy drained per second while active.")]
    [SerializeField] private float drainRate = 80f;
    [Tooltip("Health restored per second while active.")]
    [SerializeField] private float healRate = 40f;

    [Header("Activation Timing")]
    [Tooltip("Seconds after pressing before healing/draining actually starts.")]
    [SerializeField] private float activationDelay = 0.3f;
    [Tooltip("Seconds after releasing before healing/draining actually stops.")]
    [SerializeField] private float deactivationDelay = 0.3f;

    public bool IsActive { get; private set; }
    /// True while IsActive, during the post-release deactivation tail, AND while the aura
    /// itself is still visually fading out — all three are independent timers, and this is
    /// what other systems (Reinforcement) should check for mutual exclusivity, since RCT
    /// isn't fully done until the slowest of them finishes.
    public bool IsActiveOrDeactivating =>
        IsActive || _pendingDeactivation || (auraController != null && auraController.IsRctVisuallyActive);

    /// Immediately and fully deactivates RCT with no delay at all — used when Jump/Dash/
    /// Guard interrupt it, or when the character gets hit. Unlike a normal release (which
    /// lingers through deactivationDelay), this stops healing/draining right now.
    public void ForceDeactivate()
    {
        if (!IsActive && !_pendingActivation && !_pendingDeactivation) return; // already fully off

        IsActive = false;
        _pendingActivation = false;
        _pendingDeactivation = false;
        _channelReady = false;
        auraController?.SetRCTActive(false);
        ActiveChanged?.Invoke(false);
    }
    private bool _channelReady; // true only once activationDelay has actually elapsed
    private bool _pendingActivation;
    private bool _pendingDeactivation; // true after release, until deactivationDelay elapses
    private float _delayTimer;

    /// Fired whenever IsActive changes — PlayerController subscribes to this to enter/exit
    /// its movement-locked state, instead of owning any of this logic itself.
    public event System.Action<bool> ActiveChanged;

    private void OnEnable()
    {
        input.RCTEvent += OnPressed;
        input.RCTCanceledEvent += OnReleased;
    }

    private void OnDisable()
    {
        input.RCTEvent -= OnPressed;
        input.RCTCanceledEvent -= OnReleased;
    }

    private void OnPressed()
    {
        if (IsActive) return;
        if (playerController != null && !playerController.CanUseRCT) return;
        if (cursedEnergy == null || cursedEnergy.Current <= 0f) return;

        IsActive = true;
        _channelReady = false;
        _pendingDeactivation = false;
        _pendingActivation = true;
        _delayTimer = activationDelay;
        ActiveChanged?.Invoke(true); // movement locks immediately — aura and healing wait on the delay
        reinforcement?.ForceOff();
    }

    private void OnReleased()
    {
        if (!IsActive) return;

        IsActive = false;
        _pendingActivation = false;
        _pendingDeactivation = true; // aura fade-out and healing both wait for deactivationDelay
        _delayTimer = deactivationDelay;
        ActiveChanged?.Invoke(false); // movement unlocks immediately
    }

    private void Update()
    {
        if (!IsActive && !_pendingDeactivation) return;

        if (_pendingActivation)
        {
            _delayTimer -= Time.deltaTime;
            if (_delayTimer <= 0f)
            {
                _pendingActivation = false;
                _channelReady = true;
                auraController?.SetRCTActive(true); // aura starts fading in now, in sync with healing starting
            }
        }

        if (_pendingDeactivation)
        {
            _delayTimer -= Time.deltaTime;
            if (_delayTimer <= 0f)
            {
                _pendingDeactivation = false;
                _channelReady = false;
                auraController?.SetRCTActive(false); // aura starts fading out now, in sync with healing stopping
            }
        }

        // Enforced every frame while actually held, not during the release tail.
        if (IsActive) reinforcement?.ForceOff();

        if (!_channelReady) return; // wait for the activation delay before any cost/effect applies

        if (cursedEnergy != null)
        {
            cursedEnergy.Drain(drainRate);
            if (cursedEnergy.Current <= 0f)
            {
                // Ran out — hard stop immediately, don't wait for a deactivation delay.
                IsActive = false;
                _pendingActivation = false;
                _pendingDeactivation = false;
                _channelReady = false;
                auraController?.SetRCTActive(false);
                ActiveChanged?.Invoke(false);
                return;
            }
        }

        if (health != null)
            health.Heal(healRate * Time.deltaTime);
    }
}