using UnityEngine;

/// Knocked back by a hit: slides with decaying velocity, then returns to Locomotion.
public class HitstunState : PlayerState
{
    private float timer;

    public HitstunState(PlayerController p) : base(p) { }

    /// Set before (re-)entering; a hit during hitstun restarts it.
    public void Begin(float duration, Vector3 knockbackVelocity)
    {
        timer = duration;
        p.Velocity = knockbackVelocity;
    }

    public override void Tick()
    {
        p.Velocity = Vector3.MoveTowards(p.Velocity, Vector3.zero, Combat.knockbackDecay * Time.deltaTime);
        Motor.MoveStraight(p.Velocity * Time.deltaTime);

        timer -= Time.deltaTime;
        if (timer <= 0f) p.EnterLocomotion();
    }
}
