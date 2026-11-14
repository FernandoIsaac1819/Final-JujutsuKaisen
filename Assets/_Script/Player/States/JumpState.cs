using UnityEngine;

/// A committed directional jump. Horizontal velocity lives in the orbit frame, so a side
/// jump arcs around the target. Optional air control steers past the dead zone;
/// releasing the stick keeps momentum. Animation: the directional rise, then the fall
/// pose once vertical velocity turns downward.
public class JumpState : PlayerState
{
    private float gravityUp, gravityDown;
    private float verticalVelocity;
    private Vector2 airLocalVelocity; // x = right, y = toward target
    private bool falling;

    public JumpState(PlayerController p) : base(p) { }

    public override void Enter() => Launch();

    public override void Tick()
    {
        Motor.FaceTarget();

        if (Jump.airControl > 0f)
        {
            Vector2 mv = Input.MoveInput;
            float magnitude = mv.magnitude;
            if (magnitude >= Loco.walkThreshold)
            {
                Vector2 targetLocal = (mv / magnitude) * Mathf.Clamp01(magnitude) * Jump.airSpeed * p.AirSpeedMultiplier;
                airLocalVelocity = Vector2.MoveTowards(airLocalVelocity, targetLocal, Jump.airControl * Time.deltaTime);
            }
        }
        p.Velocity = Motor.LocalToWorld(airLocalVelocity);

        verticalVelocity += (verticalVelocity > 0f ? gravityUp : gravityDown) * Time.deltaTime;
        Motor.MoveAir(p.Velocity * Time.deltaTime, verticalVelocity * Time.deltaTime);

        if (!falling && verticalVelocity < 0f && !Motor.IsGrounded)
        {
            falling = true;
            Anim.PlayFall();
        }

        if (Motor.IsGrounded && verticalVelocity <= 0f)
        {
            verticalVelocity = 0f;
            Cooldowns.jumpCooldown = Jump.cooldown;

            if (Cooldowns.jumpBuffer > 0f)
            {
                Cooldowns.jumpBuffer = 0f;
                Launch(); // buffered press: jump again, skip Landing
                return;
            }

            p.ChangeState(PlayerController.ActionState.Landing);
        }
    }

    private void Launch()
    {
        // Recomputed each jump to pick up runtime tweaks and Reinforcement.
        float height = Jump.height * p.JumpMultiplier;
        gravityUp = -(2f * height) / (Jump.timeToApex * Jump.timeToApex);
        gravityDown = -(2f * height) / (Jump.timeToFall * Jump.timeToFall);
        verticalVelocity = -gravityUp * Jump.timeToApex;

        // 4-way snapped to match the directional jump clips; inside the dead zone it's a
        // neutral jump.
        int snappedX = 0, snappedZ = 0;
        Vector2 mv = Input.MoveInput;
        if (mv.magnitude >= Loco.walkThreshold)
        {
            float snappedAngle = Mathf.Round(Mathf.Atan2(mv.x, mv.y) * Mathf.Rad2Deg / 90f) * 90f * Mathf.Deg2Rad;
            snappedX = Mathf.RoundToInt(Mathf.Sin(snappedAngle));
            snappedZ = Mathf.RoundToInt(Mathf.Cos(snappedAngle));
        }
        airLocalVelocity = new Vector2(snappedX, snappedZ) * Jump.airSpeed * p.AirSpeedMultiplier;
        p.Velocity = Motor.LocalToWorld(airLocalVelocity);

        falling = false;
        Anim.PlayJump(snappedX, snappedZ);
    }
}
