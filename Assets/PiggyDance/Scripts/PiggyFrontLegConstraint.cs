// Install: Assets/PiggyDance/Scripts/PiggyFrontLegConstraint.cs
// Add to the character's main GameObject, outside the leg bone chain.
// Disable ONLY this leg's existing Limb solver before binding.
// Assign Thigh, Shin, Foot, Foot Target. In the neutral pose, use the component's
// context menu: 1 - Align Target To Foot, then 2 - Bind Current Pose.
// Test a small Pelvis movement first. This is a stylized projected-leg constraint,
// not a physical 3D knee solver. Unity rendering has not been verified here.
using UnityEngine;

[ExecuteAlways]
[DisallowMultipleComponent]
[DefaultExecutionOrder(-3)] // Before SpriteSkin's LateUpdate in 2D Animation 7.x.
[AddComponentMenu("Piggy Dance/Front Leg Constraint")]
public sealed class PiggyFrontLegConstraint : MonoBehaviour
{
    [Header("Bone Chain")]
    [Tooltip("3_Thigh_L: upper leg, directly under Pelvis.")]
    public Transform thigh;
    [Tooltip("3_Shin_L: must be a direct child of Thigh.")]
    public Transform shin;
    [Tooltip("3_Foot_L: must be a direct child of Shin.")]
    public Transform foot;
    [Tooltip("Existing foot target under CTRL_Feet, outside Pelvis.")]
    public Transform footTarget;

    [Header("Control")]
    [Tooltip("Bind Current Pose enables this. Disable to stop writing bone transforms.")]
    public bool solve;
    public bool solveInEditMode = true;
    [Tooltip("Follow target rotation while preserving its rotation difference from the foot at bind time.")]
    public bool followTargetRotation = true;

    [Header("Shape")]
    [Tooltip("Hip-to-knee share. Bind calculates this from the original upper/lower leg lengths.")]
    [Range(0.05f, 0.95f)] public float upperLegShare = 0.5f;
    [Tooltip("1 preserves the initial small knee offset; 0 places the knee on the hip-to-foot line.")]
    [Range(0f, 1f)] public float preserveKneeOffset = 1f;

    [SerializeField, HideInInspector] private bool isBound;
    [SerializeField, HideInInspector] private Transform boundThigh, boundShin, boundFoot, boundTarget, bindSpace;
    [SerializeField, HideInInspector] private float thighLocalZ, shinLocalZ, restSpan;
    [SerializeField, HideInInspector] private Vector3 restKneeOffset;
    [SerializeField, HideInInspector] private Vector3 restShinLocalPosition, restFootLocalPosition;
    [SerializeField, HideInInspector] private Quaternion restThighLocalRotation, restShinLocalRotation, restFootLocalRotation;
    [SerializeField, HideInInspector] private Quaternion targetToFootRotation;

    private const float Epsilon = 0.000001f;
    private const float PositionToleranceSquared = 0.000000000001f;

    [ContextMenu("1 - Align Target To Foot")]
    public void AlignTargetToFoot()
    {
        if (!ValidateChain(true)) return;
        if (solve)
        {
            Debug.LogWarning("Pause Solving and restore the neutral pose before aligning the target.", this);
            return;
        }
#if UNITY_EDITOR
        if (!Application.isPlaying) UnityEditor.Undo.RecordObject(footTarget, "Align foot target");
#endif
        footTarget.SetPositionAndRotation(foot.position, foot.rotation);
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(footTarget);
            UnityEditor.EditorUtility.SetDirty(footTarget);
        }
#endif
    }

    [ContextMenu("2 - Bind Current Pose")]
    public void BindCurrentPose()
    {
        if (!ValidateChain(true)) return;
        if (solve)
        {
            Debug.LogWarning("Pause Solving before rebinding; bind the neutral pose, not a solved crouch.", this);
            return;
        }
        Transform space = thigh.parent;
        Vector3 hipPosition = space.InverseTransformPoint(thigh.position);
        Vector3 kneePosition = space.InverseTransformPoint(shin.position);
        Vector3 anklePosition = space.InverseTransformPoint(foot.position);
        float upperLength = Vector3.Distance(hipPosition, kneePosition);
        float lowerLength = Vector3.Distance(kneePosition, anklePosition);
        float span = Vector3.Distance(hipPosition, anklePosition);
        if (!IsFinite(hipPosition) || !IsFinite(kneePosition) || !IsFinite(anklePosition)
            || upperLength < Epsilon || lowerLength < Epsilon || span < Epsilon)
        {
            Debug.LogError("The bind pose needs finite, non-zero upper/lower leg lengths and hip-to-foot distance.", this);
            return;
        }
#if UNITY_EDITOR
        if (!Application.isPlaying) UnityEditor.Undo.RecordObject(this, "Bind front leg constraint");
#endif
        boundThigh = thigh;
        boundShin = shin;
        boundFoot = foot;
        boundTarget = footTarget;
        bindSpace = space;
        upperLegShare = upperLength / (upperLength + lowerLength);
        restSpan = span;
        restKneeOffset = kneePosition - Vector3.Lerp(hipPosition, anklePosition, upperLegShare);
        restShinLocalPosition = shin.localPosition;
        restFootLocalPosition = foot.localPosition;
        restThighLocalRotation = thigh.localRotation;
        restShinLocalRotation = shin.localRotation;
        restFootLocalRotation = foot.localRotation;
        thighLocalZ = thigh.localEulerAngles.z;
        shinLocalZ = shin.localEulerAngles.z;
        targetToFootRotation = Quaternion.Inverse(footTarget.rotation) * foot.rotation;
        isBound = true;
        solve = true;
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            UnityEditor.EditorUtility.SetDirty(this);
            UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(this);
        }
#endif
        Debug.Log("Front leg bound. Move Pelvis slightly while the foot target stays fixed. " +
            "Do not run a Limb solver on these same bones.", this);
    }

    [ContextMenu("3 - Apply Once")]
    public void ApplyOnce()
    {
        if (!isBound || !ReferencesMatch())
        {
            Debug.LogWarning("Assign the four references and Bind Current Pose first.", this);
            return;
        }
        SolveLeg();
    }

    [ContextMenu("4 - Pause Solving")]
    public void PauseSolving() { solve = false; }

    [ContextMenu("5 - Restore Bound Bone Pose And Pause")]
    public void RestoreBoundBonePose()
    {
        if (!isBound || !ReferencesMatch()) return;
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            UnityEditor.Undo.RecordObject(this, "Pause front leg constraint");
            UnityEditor.Undo.RecordObject(thigh, "Restore upper leg");
            UnityEditor.Undo.RecordObject(shin, "Restore lower leg");
            UnityEditor.Undo.RecordObject(foot, "Restore foot");
        }
#endif
        solve = false;
        thigh.localRotation = restThighLocalRotation;
        shin.localPosition = restShinLocalPosition;
        shin.localRotation = restShinLocalRotation;
        foot.localPosition = restFootLocalPosition;
        foot.localRotation = restFootLocalRotation;
        // Deliberately leave Pelvis and the target where the user placed them.
    }

    private void LateUpdate()
    {
        if (!solve || !isBound || (!Application.isPlaying && !solveInEditMode)) return;
        if (!ReferencesMatch()) return;
        SolveLeg();
    }

    private void SolveLeg()
    {
        Vector3 ankleWorld = footTarget.position;
        Vector3 hip = bindSpace.InverseTransformPoint(thigh.position);
        Vector3 ankle = bindSpace.InverseTransformPoint(ankleWorld);
        if (!IsFinite(hip) || !IsFinite(ankle)) return;
        float span = Vector3.Distance(hip, ankle);
        if (span < Epsilon || restSpan < Epsilon) return;

        // Preserve the source pose at bind time. Scaling its small knee offset by
        // span/restSpan keeps that offset proportional to the projected leg span.
        float share = Mathf.Clamp(upperLegShare, 0.05f, 0.95f);
        float offsetScale = span / restSpan * Mathf.Clamp01(preserveKneeOffset);
        Vector3 knee = Vector3.Lerp(hip, ankle, share) + restKneeOffset * offsetScale;
        Vector3 kneeWorld = bindSpace.TransformPoint(knee);
        if (!IsFinite(kneeWorld)) return;

        // Changing child attachment positions produces the stylized shortening.
        // Do not scale parents: the foot/toe scale is left untouched.
        LockLocalZ(thigh, thighLocalZ);
        LockLocalZ(shin, shinLocalZ);
        SetPositionIfChanged(shin, kneeWorld);
        SetPositionIfChanged(foot, ankleWorld);
        if (followTargetRotation)
        {
            Quaternion wanted = footTarget.rotation * targetToFootRotation;
            if (Quaternion.Angle(foot.rotation, wanted) > 0.0001f) foot.rotation = wanted;
        }
    }

    private bool ReferencesMatch()
    {
        return thigh != null && shin != null && foot != null && footTarget != null && bindSpace != null
            && thigh == boundThigh && shin == boundShin && foot == boundFoot && footTarget == boundTarget
            && thigh.parent == bindSpace && shin.parent == thigh && foot.parent == shin;
    }

    private bool ValidateChain(bool report)
    {
        string message = null;
        if (thigh == null || shin == null || foot == null || footTarget == null)
            message = "Assign Thigh, Shin, Foot and Foot Target.";
        else if (thigh.parent == null || shin.parent != thigh || foot.parent != shin)
            message = "Expected chain: Pelvis > Thigh > Shin > Foot, with direct parent-child links.";
        else if (footTarget == thigh.parent || footTarget.IsChildOf(thigh.parent))
            message = "Foot Target must be outside Pelvis and the leg chain; place it under CTRL_Feet.";
        if (message != null && report) Debug.LogError(message, this);
        return message == null;
    }

    private static void LockLocalZ(Transform bone, float z)
    {
        Vector3 angles = bone.localEulerAngles;
        if (Mathf.Abs(Mathf.DeltaAngle(angles.z, z)) < 0.0001f) return;
        angles.z = z; // Preserve current local X/Y angles.
        bone.localEulerAngles = angles;
    }

    private static void SetPositionIfChanged(Transform bone, Vector3 position)
    {
        if ((bone.position - position).sqrMagnitude > PositionToleranceSquared) bone.position = position;
    }

    private static bool IsFinite(Vector3 v)
    {
        return !float.IsNaN(v.x) && !float.IsInfinity(v.x)
            && !float.IsNaN(v.y) && !float.IsInfinity(v.y)
            && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
    }
}
