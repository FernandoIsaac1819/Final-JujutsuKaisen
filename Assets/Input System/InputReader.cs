using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Central input hub. A single shared asset that every gameplay system
/// (movement, camera, VFX, SFX...) references.
///
/// - Buttons (Jump, Dash) are broadcast as C# events you subscribe to.
/// - Sticks (Move, Look) are exposed as polled properties you read each frame.
///
/// Requires a generated input class named "PlayerControls" with an action
/// map named "Player" containing actions: Move, Look, Jump, Dash.
/// </summary>
[CreateAssetMenu(fileName = "InputReader", menuName = "JJK/Input Reader")]
public class InputReader : ScriptableObject, Controls.IPlayerActions
{
    // ----- Button events (discrete) -----
    public event Action GuardEvent;         // fired the frame the button is pressed
    public event Action GuardCanceledEvent; // fired the frame guard is released (exit guard)
    public event Action RCTEvent;           // fired the frame the RCT button is pressed
    public event Action RCTCanceledEvent;   // fired the frame the RCT button is released
    public event Action JumpEvent;          // fired the frame the button is pressed
    public event Action JumpCanceledEvent;  // fired when released (useful later for variable jump height)
    public event Action DashEvent;          // fired the frame the button is pressed
    public event Action LockOnEvent;        // fired the frame the lock-on button is pressed
    public event Action ReinforceEvent;    // fired the frame the reinforce button is pressed
    public event Action LightAttackEvent;   // fired the frame the light attack button is pressed
    public event Action LightAttackCanceledEvent; // fired the frame the light attack button is released
    public event Action HeavyAttackEvent;   // fired the frame the heavy attack button is pressed
    // ----- Stick values (continuous, polled by consumers) -----
    public Vector2 MoveInput { get; private set; }
    public Vector2 LookInput { get; private set; }

    private Controls _controls;

    private void OnEnable()
    {
        if (_controls == null)
        {
            _controls = new Controls();
            _controls.Player.SetCallbacks(this);
        }
        EnableGameplayInput();
    }

    private void OnDisable()
    {
        DisableAllInput();
    }

    // Call these later to swap input contexts (e.g. disable gameplay input while a menu is open).
    public void EnableGameplayInput() => _controls.Player.Enable();
    public void DisableAllInput() => _controls?.Player.Disable();

    // ----- IPlayerActions callbacks -----
    // SetCallbacks routes every phase (started/performed/canceled) of each action here.

    public void OnMove(InputAction.CallbackContext context)
    {
        // ReadValue returns zero automatically on the "canceled" phase (stick released).
        MoveInput = context.ReadValue<Vector2>();
    }

    public void OnLook(InputAction.CallbackContext context)
    {
        LookInput = context.ReadValue<Vector2>();
    }

    public void OnJump(InputAction.CallbackContext context)
    {
        if (context.performed) JumpEvent?.Invoke();
        else if (context.canceled) JumpCanceledEvent?.Invoke();
    }

    public void OnDash(InputAction.CallbackContext context)
    {
        if (context.performed) DashEvent?.Invoke();
    }

    public void OnLockOn(InputAction.CallbackContext context)
    {
        if (context.performed) LockOnEvent?.Invoke();
    }

    public void OnReinforce(InputAction.CallbackContext context)
    {
        if (context.performed) ReinforceEvent?.Invoke();
    }

    public void OnLightAttack(InputAction.CallbackContext context)
    {
        if (context.performed) LightAttackEvent?.Invoke();
        else if (context.canceled) LightAttackCanceledEvent?.Invoke();
    }

    public void OnHeavyAttack(InputAction.CallbackContext context)
    {
        if (context.performed) HeavyAttackEvent?.Invoke();
    }

    public void OnGuard(InputAction.CallbackContext context)
    {
        if (context.performed) GuardEvent?.Invoke();
        else if (context.canceled) GuardCanceledEvent?.Invoke();
    }

    public void OnRCT(InputAction.CallbackContext context)
    {
        if (context.performed) RCTEvent?.Invoke();
        else if (context.canceled) RCTCanceledEvent?.Invoke();
    }
}