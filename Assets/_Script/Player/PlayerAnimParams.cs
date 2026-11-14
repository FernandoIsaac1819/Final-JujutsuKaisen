using UnityEngine;

/// PlayerAnimator.controller parameter hashes, in one place.
public static class PlayerAnimParams
{
    public static readonly int DirX = Animator.StringToHash("DirX");
    public static readonly int DirZ = Animator.StringToHash("DirZ");
    public static readonly int MoveAmount = Animator.StringToHash("MoveAmount");
    public static readonly int LocomotionSpeed = Animator.StringToHash("LocomotionSpeed01");
    public static readonly int LocomotionSpeedMultiplier = Animator.StringToHash("LocomotionSpeedMultiplier");
    public static readonly int AttackSpeedMultiplier = Animator.StringToHash("AttackSpeedMultiplier");
    public static readonly int Jump = Animator.StringToHash("Jump");
    public static readonly int Dash = Animator.StringToHash("Dash");
    public static readonly int Guard = Animator.StringToHash("Guard");
    public static readonly int Grounded = Animator.StringToHash("Grounded");
    public static readonly int Landing = Animator.StringToHash("Landing");
    public static readonly int VerticalVelocity = Animator.StringToHash("VerticalVelocity");
}
