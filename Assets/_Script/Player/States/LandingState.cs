using UnityEngine;

/// Short recovery after a jump. Stick input cancels it straight into Locomotion.
public class LandingState : PlayerState
{
    private float timer;

    public LandingState(PlayerController p) : base(p) { }

    public override void Enter()
    {
        timer = Jump.landingRecovery;
        Anim.PlayLand();
    }

    public override void Tick()
    {
        if (Input.MoveInput.magnitude >= Loco.walkThreshold)
        {
            Finish(null); // cancelled by input: default Locomotion fade
            return;
        }

        timer -= Time.deltaTime;
        if (timer <= 0f)
            Finish(Anim.LocomotionFromLandFade);
    }

    private void Finish(float? fade)
    {
        p.EnterLocomotion(fade);
        Cooldowns.dashLockout = Dash.lockoutAfterLanding;
    }
}
