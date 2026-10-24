// Unity 2021.3 + 2D Animation 7.x.
// Install in Assets/PiggyDance/Scripts/ (NOT Editor).
// Add to a NEW control object outside the leg bones. One instance per Limb solver.
// Original targets/clips are preserved. Toe local rotation is temporarily compensated.
using UnityEngine;
using UnityEngine.U2D.IK;

[ExecuteAlways]
[DefaultExecutionOrder(-3)] // IKManager2D in 2D Animation 7.x runs at -2.
[DisallowMultipleComponent]
[AddComponentMenu("Piggy Dance/Toe Pivot 2D")]
public sealed class PiggyToePivot2D : MonoBehaviour
{
    [Header("Existing IK")]
    public LimbSolver2D solver;
    [Tooltip("The EXISTING keyed target, e.g. IK_Foot_L. Keep animating this object as before.")]
    public Transform animatedTarget;
    [Tooltip("The effector must be Foot_L, not Toe_L. Bind fills this from the solver if empty.")]
    public Transform foot;
    [Tooltip("Toe_L, a direct child of Foot_L.")]
    public Transform toe;

    [Header("Contact And Heel")]
    [Range(0f, 1f), Tooltip("0 = original animation. 1 = contact point follows ToePlant.")]
    public float weight = 0f;
    [Range(-75f, 85f), Tooltip("Positive lifts the heel. This is an extra angle on top of the existing target rotation.")]
    public float heelLift = 0f;
    [Tooltip("Normally leave this off. Enable if positive Heel Lift turns the wrong way for your pose.")]
    public bool invertHeelDirection;
    [Tooltip("Zero pins the Toe bone origin. An offset can place contact at the actual tip/sole in Toe local coordinates.")]
    public Vector3 toePivotOffset = Vector3.zero;

    [Header("Optional Automatic Heel Lift")]
    [Tooltip("Adds heel lift only when the hip-to-ankle distance exceeds the allowed leg reach.")]
    public bool autoFromHip = false;
    [Range(0.75f, 1f), Tooltip("Allowed reach as a fraction of the two leg segment lengths.")]
    public float maximumExtension = 0.99f;
    [Range(0f, 85f)] public float maxAutoLift = 60f;

    [Header("Editor")]
    public bool solveInEditMode = true;
    [SerializeField, HideInInspector] private Transform toePlant;
    public Transform ToePlant { get { return toePlant; } }

    [SerializeField, HideInInspector] private bool isBound;
    [SerializeField, HideInInspector] private Transform rollOutput;
    [SerializeField, HideInInspector] private LimbSolver2D boundSolver;
    [SerializeField, HideInInspector] private Transform boundTarget, boundFoot, boundToe, hip, knee;
    [System.NonSerialized] private IKChain2D chain;
    [System.NonSerialized] private bool hasToeOverride;
    [System.NonSerialized] private Quaternion toeInputRotation, toeWrittenRotation;
    private const float Epsilon = 0.000001f;

    [ContextMenu("1 - Bind And Create Controls")]
    public void BindAndCreateControls()
    {
        if (!AllowStructuralEdit()) return;
        if (solver == null) { Warn("Assign the existing Limb solver first."); return; }
        IKChain2D requestedChain = solver.GetChain(0);
        if (requestedChain == null) { Warn("The solver has no chain."); return; }
        Transform requestedFoot = foot != null ? foot : requestedChain.effector;
        Transform requestedTarget = animatedTarget;
        if (requestedTarget == null)
            requestedTarget = requestedChain.target == rollOutput ? boundTarget : requestedChain.target;
        if (requestedFoot == null || requestedFoot != requestedChain.effector)
        { Warn("Foot must match the solver Effector. Use Foot_L / Foot_R as the Effector."); return; }
        if (toe == null || toe.parent != requestedFoot)
        { Warn("Assign Toe_L / Toe_R as a direct child of the Foot effector."); return; }
        if (requestedFoot.parent == null || requestedFoot.parent.parent == null || requestedChain.transformCount != 3)
        { Warn("Expected a three-transform Limb chain: Thigh > Shin > Foot."); return; }
        Transform pelvis = requestedFoot.parent.parent.parent;
        if (pelvis != null && (transform == pelvis || transform.IsChildOf(pelvis)))
        { Warn("Put this new control outside Pelvis and all leg bones so the toe contact does not follow the hip."); return; }
        if (requestedTarget == null || requestedTarget == rollOutput)
        { Warn("Assign the original animated target, e.g. IK_Foot_L."); return; }
        if (requestedTarget == requestedFoot || requestedTarget.IsChildOf(requestedFoot)
            || transform == requestedFoot || transform.IsChildOf(requestedFoot)
            || transform == requestedTarget || transform.IsChildOf(requestedTarget))
        { Warn("Place this NEW control outside the leg bones and outside the original target's hierarchy."); return; }
        if (!solver.constrainRotation)
        { Warn("The Limb solver needs Constrain Rotation enabled for a toe pivot. Bind has made no changes."); return; }
        if (toePlant != null && (toePlant.parent != transform
            || toePlant == requestedFoot || toePlant.IsChildOf(requestedFoot)
            || toePlant == requestedTarget || toePlant.IsChildOf(requestedTarget)
            || toePlant == toe || toePlant == transform || toePlant == rollOutput))
        { Warn("Toe Plant must be a separate contact control outside the leg and original target."); return; }

#if UNITY_EDITOR
        int undoGroup = UnityEditor.Undo.GetCurrentGroup();
        if (!Application.isPlaying)
        {
            UnityEditor.Undo.SetCurrentGroupName("Bind Piggy Toe Pivot");
            UnityEditor.Undo.RecordObject(this, "Bind Piggy Toe Pivot");
            if (boundSolver != null) UnityEditor.Undo.RecordObject(boundSolver, "Restore old IK target");
            UnityEditor.Undo.RecordObject(solver, "Set toe pivot IK output");
        }
#endif
        RestoreToeRotation();
        RestoreTargetOnly();
        foot = requestedFoot;
        animatedTarget = requestedTarget;
        if (toePlant == null) toePlant = CreateControl("ToePlant");
        if (rollOutput == null) rollOutput = CreateControl("RollOutput");
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            UnityEditor.Undo.RecordObject(toePlant, "Place toe contact");
            UnityEditor.Undo.RecordObject(rollOutput, "Place IK output");
        }
#endif
        toePlant.position = toe.TransformPoint(toePivotOffset);
        toePlant.rotation = toe.rotation;
        rollOutput.SetPositionAndRotation(animatedTarget.position, animatedTarget.rotation);
        boundSolver = solver;
        boundTarget = animatedTarget;
        boundFoot = foot;
        boundToe = toe;
        knee = foot.parent;
        hip = knee.parent;
        chain = requestedChain;
        isBound = true;
        chain.target = rollOutput;
        ApplyOnce();
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            MarkEdited(this); MarkEdited(solver); MarkEdited(toePlant); MarkEdited(rollOutput);
            UnityEditor.Undo.CollapseUndoOperations(undoGroup);
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
        }
#endif
    }

    [ContextMenu("2 - Match ToePlant To Current Toe")]
    public void MatchToePlantToCurrentToe()
    {
        if (toe == null || toePlant == null) { Warn("Bind first."); return; }
        // At a new contact frame: set Weight to 0, let IK update, then use this command.
#if UNITY_EDITOR
        if (!Application.isPlaying) UnityEditor.Undo.RecordObject(toePlant, "Match toe contact");
#endif
        toePlant.position = toe.TransformPoint(toePivotOffset);
        toePlant.rotation = toe.rotation;
#if UNITY_EDITOR
        if (!Application.isPlaying) { MarkEdited(toePlant); UnityEditor.EditorApplication.QueuePlayerLoopUpdate(); }
#endif
    }

    [ContextMenu("3 - Restore Original IK Target")]
    public void RestoreOriginalIKTarget()
    {
        if (!AllowStructuralEdit()) return;
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            UnityEditor.Undo.RecordObject(this, "Restore original IK target");
            if (boundSolver != null) UnityEditor.Undo.RecordObject(boundSolver, "Restore original IK target");
        }
#endif
        RestoreToeRotation();
        RestoreTargetOnly();
        isBound = false;
        chain = null;
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            MarkEdited(this); if (boundSolver != null) MarkEdited(boundSolver);
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
        }
#endif
    }

    private void OnEnable()
    {
#if UNITY_EDITOR
        UnityEditor.Undo.undoRedoPerformed -= RefreshLink;
        UnityEditor.Undo.undoRedoPerformed += RefreshLink;
#endif
        RefreshLink();
    }

    private void RefreshLink()
    {
        chain = null;
        if (!isBound || boundSolver == null || boundTarget == null || rollOutput == null
            || rollOutput.parent != transform || toePlant == null || toePlant.parent != transform) return;
        IKChain2D c = boundSolver.GetChain(0);
        // Do not take ownership if someone has assigned a different target.
        if (c == null || (c.target != boundTarget && c.target != rollOutput)) return;
        rollOutput.SetPositionAndRotation(boundTarget.position, boundTarget.rotation);
        chain = c;
        c.target = rollOutput;
    }

    private void OnDisable()
    {
#if UNITY_EDITOR
        UnityEditor.Undo.undoRedoPerformed -= RefreshLink;
#endif
        RestoreToeRotation(); RestoreTargetOnly(); chain = null;
    }
    private void OnDestroy() { RestoreToeRotation(); RestoreTargetOnly(); }

    // Restore before the next Animator evaluation. In editor preview ApplyOnce also
    // detects newly sampled keys, avoiding accumulated counter-rotation.
    private void Update() { RestoreToeRotation(); }

    private Quaternion ReadToeInputRotation()
    {
        if (hasToeOverride && Quaternion.Angle(boundToe.localRotation, toeWrittenRotation) < 0.0001f)
            return toeInputRotation;
        hasToeOverride = false;
        return boundToe.localRotation;
    }

    private void RestoreToeRotation()
    {
        if (!hasToeOverride) return;
        if (boundToe != null && Quaternion.Angle(boundToe.localRotation, toeWrittenRotation) < 0.0001f)
            boundToe.localRotation = toeInputRotation;
        hasToeOverride = false;
    }

    private void RestoreTargetOnly()
    {
        if (boundSolver == null || boundTarget == null || rollOutput == null || rollOutput.parent != transform) return;
        IKChain2D c = boundSolver.GetChain(0);
        if (c != null && c.target == rollOutput) c.target = boundTarget;
    }

    private void LateUpdate() { ApplyOnce(); }

    [ContextMenu("4 - Apply Once")]
    public void ApplyOnce()
    {
        if (!isBound || chain == null || chain.target != rollOutput || rollOutput == null || rollOutput.parent != transform
            || boundTarget == null || boundFoot == null || boundToe == null || toePlant == null
            || solver != boundSolver || animatedTarget != boundTarget || foot != boundFoot || toe != boundToe)
            return;
        Vector3 sourcePosition = boundTarget.position;
        Quaternion sourceRotation = boundTarget.rotation;
        float w = Mathf.Clamp01(weight);
        if (w <= 0f || (!Application.isPlaying && !solveInEditMode) || !boundSolver.constrainRotation)
        {
            RestoreToeRotation();
            SetOutput(sourcePosition, sourceRotation);
            return;
        }

        Quaternion inputToeRotation = ReadToeInputRotation();
        Quaternion sourceToeRotation = sourceRotation * inputToeRotation;
        Quaternion plantedToeRotation = Quaternion.Slerp(sourceToeRotation, toePlant.rotation, w);
        // Separate ankle-to-toe-root from toe-root-to-contact. The latter must
        // stay aligned with the PLANTED toe, rather than rotate with the heel.
        Vector3 rootOffset = sourceRotation * (Quaternion.Inverse(boundFoot.rotation)
            * boundFoot.TransformVector(boundToe.localPosition));
        Vector3 toeSpaceOffset = Quaternion.Inverse(boundToe.rotation) * boundToe.TransformVector(toePivotOffset);
        Vector3 baseOffset = rootOffset + sourceToeRotation * toeSpaceOffset;
        Vector3 baseToe = sourcePosition + baseOffset;
        Vector3 pivot = Vector3.LerpUnclamped(baseToe, toePlant.position, w);

        Vector3 plantedContactOffset = plantedToeRotation * toeSpaceOffset;
        Vector3 toeRootPivot = pivot - plantedContactOffset;
        Vector3 ankleFromToe = -rootOffset;
        float direction = ankleFromToe.x < 0f ? -1f : 1f;
        if (invertHeelDirection) direction = -direction;
        float angle = direction * Mathf.Clamp(heelLift, -75f, 85f) * w;
        Quaternion delta = Quaternion.AngleAxis(angle, Vector3.forward);
        if (autoFromHip && hip != null && knee != null && maxAutoLift > 0f)
        {
            // Bone distances, not a scene scan. No additional IK iterations are performed.
            Vector2 h = hip.position;
            float length = Vector2.Distance(h, (Vector2)knee.position)
                + Vector2.Distance((Vector2)knee.position, (Vector2)boundFoot.position);
            Vector2 d = delta * ankleFromToe;
            float extra = MinimumHeelLift(h - (Vector2)toeRootPivot, d,
                length * Mathf.Clamp(maximumExtension, 0.75f, 1f), direction,
                Mathf.Clamp(maxAutoLift, 0f, 85f));
            angle += direction * extra * w;
            delta = Quaternion.AngleAxis(angle, Vector3.forward);
        }
        Quaternion outputRotation = delta * sourceRotation;
        Vector3 outputPosition = toeRootPivot - delta * rootOffset;
        if (!IsFinite(outputPosition) || !IsFinite(outputRotation) || !IsFinite(plantedToeRotation)) return;
        SetOutput(outputPosition, outputRotation);
        // Limb IK constrains Foot to outputRotation. Pre-compensate its child
        // before IK (-2) and SpriteSkin (-1), with no second IK/skin update.
        toeInputRotation = inputToeRotation;
        toeWrittenRotation = Quaternion.Inverse(outputRotation) * plantedToeRotation;
        boundToe.localRotation = toeWrittenRotation;
        hasToeOverride = true;
    }

    private void SetOutput(Vector3 position, Quaternion rotation)
    {
        // Only the proxy target is assigned here; Toe compensation is separate.
        if (!IsFinite(position) || !IsFinite(rotation)) return;
        if ((rollOutput.position - position).sqrMagnitude > 0.000000000001f
            || Quaternion.Angle(rollOutput.rotation, rotation) > 0.0001f)
            rollOutput.SetPositionAndRotation(position, rotation);
    }

    // h = hip relative to toe; d = ankle relative to toe after the manual roll.
    // Find the smallest permitted positive heel lift that satisfies the reach limit.
    // If the leg still cannot reach, use the best achievable angle within the limit.
    private static float MinimumHeelLift(Vector2 h, Vector2 d, float reach, float direction, float maxDegrees)
    {
        if (reach <= Epsilon || (h - d).sqrMagnitude <= reach * reach) return 0f;
        float a = Vector2.Dot(h, d);
        float b = (h.y * d.x - h.x * d.y) * direction;
        float radius = Mathf.Sqrt(a * a + b * b);
        if (radius < Epsilon) return 0f;
        float threshold = (h.sqrMagnitude + d.sqrMagnitude - reach * reach) * 0.5f;
        float phase = Mathf.Atan2(b, a);
        float maxAngle = maxDegrees * Mathf.Deg2Rad;
        const float turn = 2f * Mathf.PI;
        float bestEntry = float.PositiveInfinity;
        if (threshold <= radius)
        {
            float halfWidth = Mathf.Acos(Mathf.Clamp(threshold / radius, -1f, 1f));
            for (int i = -1; i <= 1; i++)
            {
                float center = phase + turn * i;
                float entry = Mathf.Max(0f, center - halfWidth);
                if (entry <= maxAngle && entry <= center + halfWidth)
                    bestEntry = Mathf.Min(bestEntry, entry);
            }
            if (!float.IsPositiveInfinity(bestEntry)) return bestEntry * Mathf.Rad2Deg;
        }
        float best = 0f;
        float bestDot = a;
        float atEnd = a * Mathf.Cos(maxAngle) + b * Mathf.Sin(maxAngle);
        if (atEnd > bestDot) { bestDot = atEnd; best = maxAngle; }
        for (int i = -1; i <= 1; i++)
        {
            float peak = phase + turn * i;
            if (peak >= 0f && peak <= maxAngle && radius > bestDot)
            { bestDot = radius; best = peak; }
        }
        return best * Mathf.Rad2Deg;
    }

    private Transform CreateControl(string controlName)
    {
        GameObject go = new GameObject(controlName);
#if UNITY_EDITOR
        if (!Application.isPlaying) UnityEditor.Undo.RegisterCreatedObjectUndo(go, "Create toe pivot control");
#endif
        go.transform.SetParent(transform, false);
        return go.transform;
    }

    private bool AllowStructuralEdit()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying && UnityEditor.AnimationMode.InAnimationMode())
        { Warn("Turn OFF Animation Preview/Record before Bind or Restore. Existing clips will not be edited by setup."); return false; }
#endif
        return true;
    }

    private void Warn(string message) { Debug.LogWarning("Piggy Toe Pivot: " + message, this); }
    private static bool IsFinite(Vector3 p)
    {
        return !(float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z)
            || float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z));
    }
    private static bool IsFinite(Quaternion q)
    {
        return !(float.IsNaN(q.x) || float.IsNaN(q.y) || float.IsNaN(q.z) || float.IsNaN(q.w)
            || float.IsInfinity(q.x) || float.IsInfinity(q.y) || float.IsInfinity(q.z) || float.IsInfinity(q.w));
    }
#if UNITY_EDITOR
    private static void MarkEdited(UnityEngine.Object obj)
    {
        UnityEditor.EditorUtility.SetDirty(obj);
        UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(obj);
    }
#endif
    private void OnDrawGizmosSelected()
    {
        if (!isBound || toePlant == null) return;
        Gizmos.color = new Color(1f, 0.8f, 0.1f);
        Gizmos.DrawWireSphere(toePlant.position, 0.045f);
        if (rollOutput != null) Gizmos.DrawLine(toePlant.position, rollOutput.position);
    }
}
