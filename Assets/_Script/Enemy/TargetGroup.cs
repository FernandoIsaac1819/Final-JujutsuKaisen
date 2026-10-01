using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Attached to a PRIMARY enemy — the actual character being fought (Megumi, Kenjaku,
/// Junpei, or any enemy with no secondaries at all). Tracks that enemy's own summoned
/// SECONDARY targets (shikigami) as a dynamic, runtime-modifiable list, since summons can
/// appear and vanish mid-fight once an actual summoning system exists. For now, secondaries
/// can also be pre-populated in the Inspector for testing.
///
/// Owns the "who is to the left/right of the current target within this group" logic —
/// domain knowledge about this specific enemy's own structure, not something
/// PlayerController needs to know about. Scales to any number of secondaries (0, 1, 2, or
/// more) without special-casing — it's always just "find whoever's most directly to this
/// side, among everyone in the group."
///
/// Lock-on tuning also lives here rather than on PlayerController, since different enemies
/// (e.g. one with several fast-moving shikigami vs. a plain 1v1 opponent) may reasonably
/// want different flick feel.
/// </summary>
public class TargetGroup : MonoBehaviour
{
    public Transform Primary => transform;

    [Tooltip("Pre-populated for testing. At runtime, a summoning system would call " +
             "RegisterSecondary/UnregisterSecondary as shikigami appear/vanish instead.")]
    [SerializeField] private List<Transform> secondaries = new List<Transform>();
    public IReadOnlyList<Transform> Secondaries => secondaries;

    [Header("Lock-On Target Switching")]
    [Tooltip("Right stick horizontal magnitude that counts as a flick.")]
    [Range(0.1f, 1f)]
    [SerializeField] private float flickThreshold = 0.7f;
    public float FlickThreshold => flickThreshold;

    [Tooltip("Minimum time between target switches, so one flick can't cascade into multiple.")]
    [SerializeField] private float flickCooldown = 0.3f;
    public float FlickCooldown => flickCooldown;

    public void RegisterSecondary(Transform t)
    {
        if (t != null && !secondaries.Contains(t)) secondaries.Add(t);
    }

    public void UnregisterSecondary(Transform t)
    {
        secondaries.Remove(t);
    }

    /// All targets in this group — primary plus every currently-active secondary.
    public IEnumerable<Transform> AllTargets()
    {
        yield return Primary;
        for (int i = 0; i < secondaries.Count; i++)
        {
            if (secondaries[i] != null) yield return secondaries[i];
        }
    }

    /// Finds whichever OTHER target in this group is most directly to the given side of
    /// currentTarget, by ANGLE from the player's position — not by distance. Works
    /// identically regardless of how many secondaries exist (0, 1, 2, or more): it's always
    /// just "who's most directly over there, among everyone in the group." Returns null if
    /// nothing qualifies (e.g. only one target exists total, or everyone else is on the
    /// opposite side).
    public Transform GetTargetInDirection(Vector3 fromPosition, Vector3 rightAxis, Transform currentTarget, int direction)
    {
        Transform best = null;
        float bestDot = 0f; // must be positive — actually on that side at all

        foreach (Transform candidate in AllTargets())
        {
            if (candidate == currentTarget) continue;

            Vector3 toCandidate = candidate.position - fromPosition;
            toCandidate.y = 0f;
            if (toCandidate.sqrMagnitude < 0.0001f) continue;
            toCandidate.Normalize();

            float dot = Vector3.Dot(toCandidate, rightAxis) * direction;
            if (dot > bestDot)
            {
                bestDot = dot;
                best = candidate;
            }
        }

        return best;
    }
}