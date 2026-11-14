/// One action the player can be in (Locomotion, Jump, Dash...). PlayerController calls
/// Exit on the old state and Enter on the new one, so each state sets itself up and
/// cleans up after itself; Tick runs every frame while it's active. A state that has its
/// own animation plays it in Enter, through Anim.
public abstract class PlayerState
{
    protected readonly PlayerController p;

    protected PlayerState(PlayerController p) { this.p = p; }

    public virtual void Enter() { }
    public virtual void Tick() { }
    public virtual void Exit() { }

    protected PlayerMotor Motor => p.Motor;
    protected PlayerAnimation Anim => p.Anim;
    protected InputReader Input => p.Input;
    protected PlayerCooldowns Cooldowns => p.Cooldowns;

    protected LocomotionSettings Loco => p.Settings.locomotion;
    protected FacingSettings Facing => p.Settings.facing;
    protected JumpSettings Jump => p.Settings.jump;
    protected DashSettings Dash => p.Settings.dash;
    protected CombatSettings Combat => p.Settings.combat;
}
