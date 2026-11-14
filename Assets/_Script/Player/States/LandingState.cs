using UnityEngine;

/// Short recovery after a jump. Stick input cancels it straight into Locomotion.
public class LandingState : PlayerState
{
    private float timer;

    public LandingState(PlayerController p) : base(p) { }

    public override void Enter() => timer = Jump.landingRecovery;

    public override void Tick()
    {
        // Cancel: crossfade from code, since this path never sets Landing=true and Jump
        // rise's own exit (DirZ > 0.4) misses backpedals.
        if (Input.MoveInput.magnitude >= Loco.walkThreshold)
        {
            Finish(crossfade: true);
            return;
        }

        timer -= Time.deltaTime;
        if (timer <= 0f)
            Finish(crossfade: false); // Land -> Locomotion via the Animator
    }

    private void Finish(bool crossfade)
    {
        p.EnterLocomotion(crossfade);
        Cooldowns.dashLockout = Dash.lockoutAfterLanding;
    }
}
