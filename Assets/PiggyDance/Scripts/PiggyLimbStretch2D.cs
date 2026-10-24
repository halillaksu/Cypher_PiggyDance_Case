// Unity 2021.3 / 2D Animation 7.x. Install outside an Editor folder.
// Native Limb IK remains enabled. This component changes segment attachments
// before IKManager2D (-2); it never calls UpdateIK or changes the target.
using UnityEngine;
using UnityEngine.U2D.IK;

[ExecuteAlways]
[DisallowMultipleComponent]
[DefaultExecutionOrder(-4)]
[AddComponentMenu("Piggy Dance/Limb Stretch 2D")]
public sealed class PiggyLimbStretch2D : MonoBehaviour
{
    [Header("Existing Limb IK")]
    public LimbSolver2D solver;

    [Header("Stretch")]
    [Range(0f, 1f), Tooltip("Overall length effect: 1 = full stretch/compression. 0 = original lengths.")]
    public float weight = 1f;
    [Tooltip("Off: reach is unlimited. On: the foot may leave the target above Max Stretch Ratio.")]
    public bool limitStretch = false;
    [Min(1f)] public float maxStretchRatio = 1.5f;

    [Header("Compression - Less Knee Bend")]
    [Range(0f, 1f), Tooltip("0 = original IK knee bend. Increase to shorten both segments when the hip approaches the target. 1 approaches a straight leg unless Min Length Ratio limits shortening.")]
    public float compressionWeight = 0f;
    [Range(0.1f, 1f), Tooltip("Minimum segment length relative to the bound length. 0.85 allows up to 15 percent shortening. Does not limit upward stretch.")]
    public float minLengthRatio = 0.85f;

    [Header("Editor And Readout")]
    public bool solveInEditMode = true;
    [SerializeField, Tooltip("Readout only: 1 = original length, 1.2 = 20 percent longer, 0.9 = 10 percent shorter.")]
    private float currentStretch = 1f;
    public float CurrentStretch { get { return currentStretch; } }

    [SerializeField, HideInInspector] private bool isBound;
    [SerializeField, HideInInspector] private LimbSolver2D boundSolver;
    [SerializeField, HideInInspector] private Transform thigh, shin, foot;
    [SerializeField, HideInInspector] private Vector3 restShinPosition, restFootPosition;
    private IKChain2D chain;
    private Vector3 lastShinPosition, lastFootPosition;
    private bool wrotePositions;
    private const float Epsilon = 0.000001f;

    [ContextMenu("1 - Bind Original Leg Lengths")]
    public void BindOriginalLegLengths()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying && UnityEditor.AnimationMode.InAnimationMode())
        { Warn("Turn OFF Animation Preview and Record before Bind. The clips are not changed."); return; }
#endif
        if (solver == null) { Warn("Assign the existing Limb Solver 2D."); return; }
        IKChain2D c = solver.GetChain(0);
        if (c == null || !c.isValid || c.transformCount != 3)
        { Warn("The Limb solver needs a valid Thigh > Shin > Foot chain and target first."); return; }
        Transform f = c.effector;
        Transform s = f != null ? f.parent : null;
        Transform t = s != null ? s.parent : null;
        if (t == null || c.transforms == null || c.transforms.Length != 3
            || c.transforms[0] != t || c.transforms[1] != s || c.transforms[2] != f)
        { Warn("Expected three directly linked transforms: Thigh > Shin > Foot."); return; }
        if (c.target == null || (t.parent != null
            && (c.target == t.parent || c.target.IsChildOf(t.parent))))
        { Warn("The foot target must be outside Pelvis, so moving Pelvis does not move it."); return; }
        if (!UniformXY(t) || !UniformXY(s))
        { Warn("This version needs uniform XY world scale on Thigh and Shin. Leave bone scales at 1,1,1."); return; }
        if (isBound)
        { Warn("Already bound. Use Restore And Unbind before capturing different original lengths."); return; }
        Vector3 a = s.localPosition, b = f.localPosition;
        if (!Finite(a) || !Finite(b) || XY(t.TransformVector(a)).sqrMagnitude < Epsilon * Epsilon
            || XY(s.TransformVector(b)).sqrMagnitude < Epsilon * Epsilon)
        { Warn("Both original leg segments must have finite, non-zero XY lengths."); return; }
#if UNITY_EDITOR
        if (!Application.isPlaying) UnityEditor.Undo.RecordObject(this, "Bind limb stretch lengths");
#endif
        boundSolver = solver; thigh = t; shin = s; foot = f;
        restShinPosition = a; restFootPosition = b;
        chain = c; isBound = true; currentStretch = 1f;
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            UnityEditor.EditorUtility.SetDirty(this);
            UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(this);
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
        }
#endif
    }

    [ContextMenu("2 - Restore And Unbind")]
    public void RestoreAndUnbind()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying && UnityEditor.AnimationMode.InAnimationMode())
        { Warn("Turn OFF Animation Preview and Record before Unbind."); return; }
        if (!Application.isPlaying) UnityEditor.Undo.RecordObject(this, "Unbind limb stretch");
#endif
        RestorePositions();
        isBound = false; chain = null;
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            UnityEditor.EditorUtility.SetDirty(this);
            UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(this);
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
        }
#endif
    }

    private void OnEnable()
    {
#if UNITY_EDITOR
        UnityEditor.Undo.undoRedoPerformed -= RefreshChain;
        UnityEditor.Undo.undoRedoPerformed += RefreshChain;
#endif
        RefreshChain();
    }

    private void RefreshChain()
    {
        chain = isBound && boundSolver != null ? boundSolver.GetChain(0) : null;
    }

    private void OnDisable()
    {
#if UNITY_EDITOR
        UnityEditor.Undo.undoRedoPerformed -= RefreshChain;
#endif
        RestorePositions();
    }

    private void OnDestroy() { RestorePositions(); }
    private void LateUpdate() { ApplyStretch(); }

    [ContextMenu("3 - Apply Stretch Once")]
    public void ApplyStretch()
    {
        if (!isBound || chain == null || solver != boundSolver || boundSolver == null
            || thigh == null || shin == null || foot == null || shin.parent != thigh || foot.parent != shin
            || chain.effector != foot || chain.target == null || !chain.isValid)
        { RestorePositions(); return; }
        if ((!Application.isPlaying && !solveInEditMode) || !boundSolver.isActiveAndEnabled
            || weight <= 0f || !UniformXY(thigh) || !UniformXY(shin))
        { RestorePositions(); return; }

        // Use original attachment vectors every time: stretching cannot accumulate.
        // Uniform XY scales make these lengths independent of the previous IK bend.
        float upper = XY(thigh.TransformVector(restShinPosition)).magnitude;
        float lower = XY(shin.TransformVector(restFootPosition)).magnitude;
        float reach = upper + lower;
        Vector2 delta = XY(chain.target.position - thigh.position);
        float distanceSquared = delta.sqrMagnitude;
        if (!Finite(reach) || !Finite(distanceSquared) || reach <= Epsilon) return;
        float ratio = distanceSquared > reach * reach ? Mathf.Sqrt(distanceSquared) / reach : 1f;
        if (distanceSquared < reach * reach && compressionWeight > 0f)
        {
            // Shorten proportionally to the hip-target span. Partial compression
            // retains knee bend; at 0 the old, stretch-only behavior is identical.
            float spanRatio = Mathf.Sqrt(distanceSquared) / reach;
            ratio = Mathf.Max(Mathf.Clamp(minLengthRatio, 0.1f, 1f),
                Mathf.Lerp(1f, spanRatio, Mathf.Clamp01(compressionWeight)));
        }
        if (limitStretch) ratio = Mathf.Min(ratio, Mathf.Max(1f, maxStretchRatio));
        ratio = Mathf.Lerp(1f, ratio, Mathf.Clamp01(weight));
        if (!Finite(ratio)) return;
        // Only XY attachment lengths change; foot scale, target and rotations
        // remain under their existing controllers. Native IK still bends the knee.
        Vector3 a = new Vector3(restShinPosition.x * ratio, restShinPosition.y * ratio, restShinPosition.z);
        Vector3 b = new Vector3(restFootPosition.x * ratio, restFootPosition.y * ratio, restFootPosition.z);
        if (!Finite(a) || !Finite(b)) return;
        SetLocalPosition(shin, a); SetLocalPosition(foot, b);
        lastShinPosition = a; lastFootPosition = b; wrotePositions = true;
        currentStretch = ratio;
        UpdateChainLengths();
    }

    private void RestorePositions()
    {
        if (!wrotePositions) { currentStretch = 1f; return; }
        // Respect manual edits or newly sampled position keys made after our write.
        if (shin != null && (shin.localPosition - lastShinPosition).sqrMagnitude < 1e-12f)
            SetLocalPosition(shin, restShinPosition);
        if (foot != null && (foot.localPosition - lastFootPosition).sqrMagnitude < 1e-12f)
            SetLocalPosition(foot, restFootPosition);
        wrotePositions = false; currentStretch = 1f;
        UpdateChainLengths();
    }

    private void UpdateChainLengths()
    {
        if (chain == null || thigh == null || shin == null || foot == null) return;
        // Refresh the existing array; no chain Initialize (which would recapture
        // default rotations), array allocation or extra IK solve on each frame.
        float[] lengths = chain.lengths;
        if (lengths == null || lengths.Length != 2) return;
        lengths[0] = Vector3.Distance(thigh.position, shin.position);
        lengths[1] = Vector3.Distance(shin.position, foot.position);
    }

    private static Vector2 XY(Vector3 v) { return new Vector2(v.x, v.y); }
    private static bool UniformXY(Transform t)
    {
        Vector3 s = t.lossyScale;
        float x = Mathf.Abs(s.x), y = Mathf.Abs(s.y);
        return Finite(x) && Finite(y) && x > Epsilon
            && Mathf.Abs(x - y) <= Mathf.Max(x, y) * 0.001f;
    }
    private static bool Finite(float v) { return !float.IsNaN(v) && !float.IsInfinity(v); }
    private static bool Finite(Vector3 v) { return Finite(v.x) && Finite(v.y) && Finite(v.z); }
    private static void SetLocalPosition(Transform t, Vector3 p)
    { if ((t.localPosition - p).sqrMagnitude > 1e-12f) t.localPosition = p; }
    private void Warn(string message) { Debug.LogWarning("Piggy Limb Stretch: " + message, this); }
}
