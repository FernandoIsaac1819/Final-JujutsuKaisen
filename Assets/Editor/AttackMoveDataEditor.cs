using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;

/// <summary>
/// Custom Inspector for AttackMoveData. Replaces guessing normalized-time floats with a
/// visual timeline: drag the red diamond markers to set each hit event's precise trigger
/// time, drag blue diamonds for forward steps, and scrub the slider to see the actual pose
/// live in the Scene view via AnimationMode — as long as a scene Animator is assigned as
/// the Preview Target.
///
/// Must be placed in a folder literally named "Editor" anywhere under Assets — that's what
/// tells Unity this is editor-only code, excluded from builds.
/// </summary>
[CustomEditor(typeof(AttackMoveData))]
public class AttackMoveDataEditor : Editor
{
    private Animator previewTarget;
    private float scrubTime01; // 0-1 normalized
    private bool isPreviewing;

    private enum DragTarget { None, HitEvent, Step }
    private DragTarget dragTarget = DragTarget.None;
    private int dragIndex = -1;

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space(12);
        EditorGUILayout.LabelField("Hitbox Timing Preview", EditorStyles.boldLabel);

        previewTarget = (Animator)EditorGUILayout.ObjectField(
            "Preview Target (scene Animator)", previewTarget, typeof(Animator), true);

        if (previewTarget == null)
        {
            EditorGUILayout.HelpBox(
                "Assign an Animator from the scene (e.g. your Player) to scrub this move's " +
                "actual pose live in the Scene view.", MessageType.Info);
            return;
        }

        AttackMoveData move = (AttackMoveData)target;
        AnimationClip clip = FindClipForState(previewTarget, move.AnimatorStateName);

        if (clip == null)
        {
            EditorGUILayout.HelpBox(
                $"No state named \"{move.AnimatorStateName}\" found on this Animator's controller, " +
                "or its motion isn't a single AnimationClip.", MessageType.Warning);
            return;
        }

        EditorGUILayout.LabelField($"Clip: {clip.name}   Length: {clip.length:F2}s");
        EditorGUILayout.Space(4);

        Rect timelineRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(36));
        DrawTimeline(timelineRect);

        EditorGUILayout.Space(6);
        scrubTime01 = EditorGUILayout.Slider("Scrub", scrubTime01, 0f, 1f);

        EditorGUILayout.Space(4);
        if (GUILayout.Button(isPreviewing ? "Stop Preview" : "Start Preview"))
        {
            SetPreviewing(!isPreviewing);
        }

        if (isPreviewing)
        {
            AnimationMode.SampleAnimationClip(previewTarget.gameObject, clip, scrubTime01 * clip.length);
            SceneView.RepaintAll();
            Repaint(); // keep this Inspector redrawing too, since scrub can change via drag
        }
    }

    private void DrawTimeline(Rect rect)
    {
        SerializedProperty hitsProp = serializedObject.FindProperty("hitEvents");
        SerializedProperty stepsProp = serializedObject.FindProperty("forwardSteps");

        EditorGUI.DrawRect(rect, new Color(0.15f, 0.15f, 0.15f));

        // Each hit event's small tolerance window, shaded around its trigger time.
        for (int i = 0; i < hitsProp.arraySize; i++)
        {
            SerializedProperty hitProp = hitsProp.GetArrayElementAtIndex(i);
            float triggerTime = hitProp.FindPropertyRelative("triggerTime").floatValue;
            float activeWindow = hitProp.FindPropertyRelative("activeWindow").floatValue;
            float startX = rect.x + Mathf.Max(0f, triggerTime - activeWindow / 2f) * rect.width;
            float endX = rect.x + Mathf.Min(1f, triggerTime + activeWindow / 2f) * rect.width;
            EditorGUI.DrawRect(new Rect(startX, rect.y, Mathf.Max(1f, endX - startX), rect.height), new Color(1f, 0.3f, 0.3f, 0.35f));
        }

        // Scrub position marker.
        float scrubX = rect.x + scrubTime01 * rect.width;
        EditorGUI.DrawRect(new Rect(scrubX - 1f, rect.y, 2f, rect.height), Color.white);

        // One red diamond per hit event, at its precise trigger time.
        const float diamondHalf = 5f;
        float midY = rect.y + rect.height / 2f;
        for (int i = 0; i < hitsProp.arraySize; i++)
        {
            float t = hitsProp.GetArrayElementAtIndex(i).FindPropertyRelative("triggerTime").floatValue;
            float x = rect.x + t * rect.width;
            DrawDiamond(new Vector2(x, midY), diamondHalf, Color.red);
        }

        // One blue diamond per forward step entry, at its trigger time.
        for (int i = 0; i < stepsProp.arraySize; i++)
        {
            float t = stepsProp.GetArrayElementAtIndex(i).FindPropertyRelative("triggerTime").floatValue;
            float x = rect.x + t * rect.width;
            DrawDiamond(new Vector2(x, midY), diamondHalf, new Color(0.3f, 0.6f, 1f));
        }

        HandleTimelineInput(rect, hitsProp, stepsProp);
    }

    private void DrawDiamond(Vector2 center, float half, Color color)
    {
        Handles.color = color;
        Vector3[] points =
        {
            center + new Vector2(0, -half),
            center + new Vector2(half, 0),
            center + new Vector2(0, half),
            center + new Vector2(-half, 0),
        };
        Handles.DrawAAConvexPolygon(points);
    }

    private void HandleTimelineInput(Rect rect, SerializedProperty hitsProp, SerializedProperty stepsProp)
    {
        Event e = Event.current;

        if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition))
        {
            float t = Mathf.Clamp01((e.mousePosition.x - rect.x) / rect.width);

            // Find the closest of every draggable marker — every hit event, plus every step.
            dragTarget = DragTarget.None;
            dragIndex = -1;
            float bestDist = float.MaxValue;

            for (int i = 0; i < hitsProp.arraySize; i++)
            {
                float hitT = hitsProp.GetArrayElementAtIndex(i).FindPropertyRelative("triggerTime").floatValue;
                float dist = Mathf.Abs(t - hitT);
                if (dist < bestDist) { bestDist = dist; dragTarget = DragTarget.HitEvent; dragIndex = i; }
            }

            for (int i = 0; i < stepsProp.arraySize; i++)
            {
                float stepT = stepsProp.GetArrayElementAtIndex(i).FindPropertyRelative("triggerTime").floatValue;
                float dist = Mathf.Abs(t - stepT);
                if (dist < bestDist) { bestDist = dist; dragTarget = DragTarget.Step; dragIndex = i; }
            }

            e.Use();
        }
        else if (e.type == EventType.MouseDrag && dragTarget != DragTarget.None)
        {
            float t = Mathf.Clamp01((e.mousePosition.x - rect.x) / rect.width);

            switch (dragTarget)
            {
                case DragTarget.HitEvent:
                    hitsProp.GetArrayElementAtIndex(dragIndex).FindPropertyRelative("triggerTime").floatValue = t;
                    break;
                case DragTarget.Step:
                    stepsProp.GetArrayElementAtIndex(dragIndex).FindPropertyRelative("triggerTime").floatValue = t;
                    break;
            }

            serializedObject.ApplyModifiedProperties();
            e.Use();
            Repaint();
        }
        else if (e.type == EventType.MouseUp)
        {
            dragTarget = DragTarget.None;
            dragIndex = -1;
        }
    }

    private void SetPreviewing(bool value)
    {
        if (value)
        {
            AnimationMode.StartAnimationMode();
            isPreviewing = true;
        }
        else
        {
            AnimationMode.StopAnimationMode();
            isPreviewing = false;
        }
    }

    /// Searches the Animator's controller state machine (all layers) for a state matching
    /// the given name, and returns its motion if it's a plain AnimationClip. Matches by
    /// STATE name, not clip name — a state can be named differently than its clip.
    private AnimationClip FindClipForState(Animator animator, string stateName)
    {
        if (string.IsNullOrEmpty(stateName)) return null;
        if (!(animator.runtimeAnimatorController is AnimatorController controller)) return null;

        foreach (AnimatorControllerLayer layer in controller.layers)
        {
            foreach (ChildAnimatorState child in layer.stateMachine.states)
            {
                if (child.state.name == stateName && child.state.motion is AnimationClip clip)
                    return clip;
            }
        }

        return null;
    }

    private void OnSceneGUI()
    {
        if (previewTarget == null) return;

        AttackMoveData move = (AttackMoveData)target;
        Transform t = previewTarget.transform;

        for (int i = 0; i < move.HitEvents.Count; i++)
        {
            AttackMoveData.HitEvent hit = move.HitEvents[i];
            float halfWindow = hit.activeWindow / 2f;
            bool active = scrubTime01 >= hit.triggerTime - halfWindow && scrubTime01 <= hit.triggerTime + halfWindow;

            Vector3 center = t.position + t.forward * hit.hitboxForwardOffset + Vector3.up * hit.hitboxHeightOffset;
            Handles.color = active ? Color.red : Color.white;
            Handles.DrawWireDisc(center, Vector3.up, hit.hitboxRadius);
            Handles.DrawWireDisc(center, Vector3.forward, hit.hitboxRadius);
            Handles.DrawWireDisc(center, Vector3.right, hit.hitboxRadius);
        }
    }

    private void OnDisable()
    {
        if (isPreviewing)
        {
            AnimationMode.StopAnimationMode();
            isPreviewing = false;
        }
    }
}