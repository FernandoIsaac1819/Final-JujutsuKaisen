/// Timers that outlive a single state: cooldowns, lockouts and input buffers that one
/// state sets and another checks. All count down to 0.
public class PlayerCooldowns
{
    public float jumpCooldown;
    public float jumpLockout;   // after a dash, before Jump works from Locomotion
    public float jumpBuffer;    // Jump pressed mid-air, fires on landing
    public float dashCooldown;
    public float dashLockout;   // after landing, before Dash works from Locomotion
    public float dashBuffer;    // Dash pressed during cooldown, fires when it clears
    public float airborneTime;  // counts up while not grounded (coyote time)

    public void Tick(float dt, bool grounded)
    {
        if (jumpCooldown > 0f) jumpCooldown -= dt;
        if (jumpLockout > 0f) jumpLockout -= dt;
        if (jumpBuffer > 0f) jumpBuffer -= dt;
        if (dashCooldown > 0f) dashCooldown -= dt;
        if (dashLockout > 0f) dashLockout -= dt;
        if (dashBuffer > 0f) dashBuffer -= dt;
        airborneTime = grounded ? 0f : airborneTime + dt;
    }
}
