using UnityEngine;

/// Brief aim window after pressing Dash: frozen until a direction is given. No direction
/// before it expires = no dash.
public class DashWindupState : PlayerState
{
    private float aimTimer;

    public DashWindupState(PlayerController p) : base(p) { }

    public override void Enter()
    {
        aimTimer = Dash.aimWindow;
        p.Velocity = Vector3.zero;
    }

    public override void Tick()
    {
        aimTimer -= Time.deltaTime;

        if (Input.MoveInput.sqrMagnitude > 0.0001f)
            p.ChangeState(PlayerController.ActionState.Dashing);
        else if (aimTimer <= 0f)
            p.EnterLocomotion();
    }
}
