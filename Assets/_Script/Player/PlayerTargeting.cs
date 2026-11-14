using System;
using UnityEngine;

/// Always-on lock-on: locks onto the nearest TargetGroup's Primary and switches targets
/// with a right-stick flick. Owned by PlayerController.
public class PlayerTargeting
{
    private readonly InputReader input;
    private readonly Transform transform;

    private TargetGroup activeGroup;
    private float prevLookX;
    private float switchCooldownTimer;

    public PlayerTargeting(InputReader input, Transform transform, Transform initialTarget)
    {
        this.input = input;
        this.transform = transform;
        Target = initialTarget;
    }

    public Transform Target { get; private set; }
    public event Action<Transform> Changed;

    /// Locks onto the nearest TargetGroup's Primary. Safe to call again.
    public void Engage()
    {
        if (activeGroup == null) activeGroup = FindNearestTargetGroup();
        if (activeGroup != null) SetTarget(activeGroup.Primary);
        Debug.Log($"[LockOn] Engaged, target='{Target?.name}'");
    }

    /// Flick switching fires on the edge (crossing the threshold), not while held.
    /// Threshold and cooldown come from the TargetGroup.
    public void Tick()
    {
        if (Target == null) return;
        if (activeGroup == null)
        {
            Debug.Log($"[LockOn] No TargetGroup found; flick switching disabled. Frame {Time.frameCount}");
            return;
        }
        if (switchCooldownTimer > 0f) switchCooldownTimer -= Time.deltaTime;

        float lookX = input.LookInput.x;
        bool wasBelow = Mathf.Abs(prevLookX) < activeGroup.FlickThreshold;
        bool isAbove = Mathf.Abs(lookX) >= activeGroup.FlickThreshold;

        if (Mathf.Abs(lookX) > 0.05f)
            Debug.Log($"[LockOn] lookX={lookX:F3}, threshold={activeGroup.FlickThreshold:F2}, wasBelow={wasBelow}, isAbove={isAbove}, cooldown={switchCooldownTimer:F2}");

        if (wasBelow && isAbove && switchCooldownTimer <= 0f)
        {
            int direction = lookX > 0f ? 1 : -1;
            Transform next = activeGroup.GetTargetInDirection(transform.position, transform.right, Target, direction);
            if (next != null)
            {
                SetTarget(next);
                Debug.Log($"[LockOn] Switched target to '{Target.name}' (flick {(direction > 0 ? "right" : "left")})");
            }
            else
            {
                Debug.Log($"[LockOn] Flick {(direction > 0 ? "right" : "left")} found no target; check the TargetGroup's Secondaries.");
            }
            switchCooldownTimer = activeGroup.FlickCooldown;
        }

        prevLookX = lookX;
    }

    private void SetTarget(Transform newTarget)
    {
        Target = newTarget;
        Changed?.Invoke(Target);
    }

    private TargetGroup FindNearestTargetGroup()
    {
        TargetGroup nearest = null;
        float nearestDistSqr = float.MaxValue;
        foreach (TargetGroup group in UnityEngine.Object.FindObjectsByType<TargetGroup>(FindObjectsSortMode.None))
        {
            float distSqr = (group.Primary.position - transform.position).sqrMagnitude;
            if (distSqr < nearestDistSqr)
            {
                nearestDistSqr = distSqr;
                nearest = group;
            }
        }
        return nearest;
    }
}
