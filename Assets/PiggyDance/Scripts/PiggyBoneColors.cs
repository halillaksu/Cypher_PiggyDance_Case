#if UNITY_EDITOR
// Install: Assets/PiggyDance/Editor/PiggyBoneColors.cs
// Menu: Tools > Piggy Dance > Bone Colors
// Scan a scene character root once. Enable Colored Bones, then hide Unity's
// original BoneGizmos in the Scene view Gizmos menu to avoid duplicate drawings.
// Colors affect only these custom handles. No rig, animation or package data is edited.
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.U2D.Animation;

public sealed class PiggyBoneColors : EditorWindow
{
    private enum Group { Left, Right, Body, Face, Helper, Other }
    private sealed class BoneEntry
    {
        public Transform bone;
        public string label;
        public string stem;
        public Group group;
        public SpriteRenderer[] users;
        public int child = -1;
        public readonly Vector3[] line = new Vector3[2];
    }

    [SerializeField] private GameObject characterRoot;
    [SerializeField] private bool onlyActiveSprites = true;
    [SerializeField] private bool showFace = true;
    [SerializeField] private bool showHelpers = true;
    [SerializeField] private bool showSelectedName = true;
    [SerializeField] private bool clickToSelectBones = false;
    [SerializeField] private float markerSize = 0.035f;
    [SerializeField] private float lineWidth = 2f;
    [SerializeField] private Color leftColor = new Color(0.18f, 0.65f, 1f);
    [SerializeField] private Color rightColor = new Color(1f, 0.30f, 0.22f);
    [SerializeField] private Color bodyColor = new Color(1f, 0.85f, 0.15f);
    [SerializeField] private Color faceColor = new Color(0.85f, 0.42f, 1f);
    [SerializeField] private Color helperColor = new Color(0.15f, 1f, 0.78f);
    [SerializeField] private Color otherColor = new Color(0.8f, 0.8f, 0.85f);

    private BoneEntry[] bones = new BoneEntry[0];
    private Vector3[] positions = new Vector3[0];
    private bool[] visible = new bool[0];
    private bool drawing;
    private string status = "Select the main character root, then Scan Bones.";
    private GUIStyle labelStyle;
    private static readonly Handles.CapFunction DotCap = Handles.DotHandleCap;

    [MenuItem("Tools/Piggy Dance/Bone Colors")]
    public static void Open()
    {
        var window = GetWindow<PiggyBoneColors>("Piggy Bone Colors");
        window.minSize = new Vector2(340, 440);
        window.Show();
    }

    private void OnEnable()
    {
        drawing = false; // Re-scan after a compilation/reload.
        bones = new BoneEntry[0];
        positions = new Vector3[0];
        visible = new bool[0];
    }

    private void OnDisable() { SetDrawing(false); }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Scene Bone Colors", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Select the parent containing the Sprite Skin components. " +
            "Scan once and enable drawing. Select bones in the Hierarchy. " +
            "Optional dot selection can be enabled temporarily below.", MessageType.Info);
        EditorGUI.BeginChangeCheck();
        characterRoot = (GameObject)EditorGUILayout.ObjectField("Character Root", characterRoot, typeof(GameObject), true);
        if (EditorGUI.EndChangeCheck()) ClearCache();
        if (GUILayout.Button("Use Selected Object"))
        {
            characterRoot = Selection.activeGameObject;
            ClearCache();
        }
        if (GUILayout.Button("Scan Bones")) ScanBones();
        EditorGUILayout.LabelField(status, EditorStyles.wordWrappedMiniLabel);
        using (new EditorGUI.DisabledScope(bones.Length == 0))
        {
            bool requested = EditorGUILayout.ToggleLeft("Enable Colored Bones", drawing);
            if (requested != drawing) SetDrawing(requested);
        }

        EditorGUI.BeginChangeCheck();
        onlyActiveSprites = EditorGUILayout.Toggle("Only Active Sprites", onlyActiveSprites);
        showFace = EditorGUILayout.Toggle("Show Face Bones", showFace);
        showHelpers = EditorGUILayout.Toggle("Show Helper Bones", showHelpers);
        showSelectedName = EditorGUILayout.Toggle("Selected Bone Name", showSelectedName);
        clickToSelectBones = EditorGUILayout.Toggle("Click To Select Bones", clickToSelectBones);
        markerSize = EditorGUILayout.Slider("Marker Size", markerSize, 0.015f, 0.08f);
        lineWidth = EditorGUILayout.Slider("Line Width", lineWidth, 1f, 5f);
        leftColor = EditorGUILayout.ColorField("Left", leftColor);
        rightColor = EditorGUILayout.ColorField("Right", rightColor);
        bodyColor = EditorGUILayout.ColorField("Body", bodyColor);
        faceColor = EditorGUILayout.ColorField("Face", faceColor);
        helperColor = EditorGUILayout.ColorField("Helper", helperColor);
        otherColor = EditorGUILayout.ColorField("Other", otherColor);
        if (EditorGUI.EndChangeCheck() && drawing) SceneView.RepaintAll();
        EditorGUILayout.HelpBox("Keep this window open. The tool has no Update loop and does not " +
            "continuously request repaints. Re-scan after changing the rig. " +
            "Keep Click To Select Bones off while moving bones. Hide Sprite Skin in the " +
            "Scene Gizmos menu; keep the main Gizmos toggle on.", MessageType.None);
    }

    private void ClearCache()
    {
        SetDrawing(false);
        bones = new BoneEntry[0];
        positions = new Vector3[0];
        visible = new bool[0];
        status = "Press Scan Bones for this root.";
    }

    private void SetDrawing(bool enable)
    {
        if (enable == drawing) return;
        if (enable) SceneView.duringSceneGui += DrawScene;
        else SceneView.duringSceneGui -= DrawScene;
        drawing = enable;
        SceneView.RepaintAll(); // Only when the user toggles drawing or closes the tool.
    }

    private void ScanBones()
    {
        ClearCache();
        if (characterRoot == null || EditorUtility.IsPersistent(characterRoot))
        {
            status = "Choose the character instance from the scene Hierarchy.";
            return;
        }
        // All scene/component searches and collection allocations happen only on Scan.
        SpriteSkin[] skins = characterRoot.GetComponentsInChildren<SpriteSkin>(true);
        var entries = new List<BoneEntry>();
        var indices = new Dictionary<Transform, int>();
        var owners = new List<List<SpriteRenderer>>();
        foreach (SpriteSkin skin in skins)
        {
            Transform[] skinBones = skin.boneTransforms;
            SpriteRenderer renderer = skin.GetComponent<SpriteRenderer>();
            if (skinBones == null || renderer == null) continue;
            foreach (Transform bone in skinBones)
            {
                if (bone == null) continue;
                int index;
                if (!indices.TryGetValue(bone, out index))
                {
                    index = entries.Count;
                    indices.Add(bone, index);
                    string name = bone.name;
                    entries.Add(new BoneEntry { bone = bone, label = name,
                        stem = Normalize(name), group = Classify(name) });
                    owners.Add(new List<SpriteRenderer>());
                }
                if (!owners[index].Contains(renderer)) owners[index].Add(renderer);
            }
        }
        for (int i = 0; i < entries.Count; i++)
        {
            BoneEntry entry = entries[i];
            entry.users = owners[i].ToArray();
            int candidate = -1;
            int count = 0;
            for (int j = 0; j < entry.bone.childCount; j++)
            {
                int child;
                if (!indices.TryGetValue(entry.bone.GetChild(j), out child)) continue;
                if (entries[child].group == Group.Helper) continue;
                count++;
                candidate = child;
                if (PreferredChild(entry.stem, entries[child].stem))
                {
                    entry.child = child;
                    break;
                }
            }
            // Avoid spokes from Head/Pelvis to every facial or accessory bone.
            if (entry.child < 0 && count == 1 && entry.group != Group.Helper) entry.child = candidate;
        }
        bones = entries.ToArray();
        positions = new Vector3[bones.Length];
        visible = new bool[bones.Length];
        status = bones.Length + " unique bones cached from " + skins.Length + " Sprite Skins.";
        if (bones.Length == 0) status += " Select a parent above the mesh/Sprite Skin objects.";
        Repaint();
    }

    private void DrawScene(SceneView view)
    {
        if (!drawing || characterRoot == null || !view.drawGizmos) return;
        EventType type = Event.current.type;
        if (type != EventType.Repaint && type != EventType.Layout && type != EventType.MouseDown
            && type != EventType.MouseUp && type != EventType.MouseMove) return;

        for (int i = 0; i < bones.Length; i++)
        {
            BoneEntry entry = bones[i];
            bool show = entry.bone != null && entry.bone.gameObject.activeInHierarchy
                && (showFace || entry.group != Group.Face) && (showHelpers || entry.group != Group.Helper);
            if (show && onlyActiveSprites)
            {
                show = false;
                // Cached owners allow mesh/character activation switches without re-scanning.
                for (int j = 0; j < entry.users.Length; j++)
                {
                    SpriteRenderer renderer = entry.users[j];
                    if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy && renderer.sprite != null)
                    {
                        show = true;
                        break;
                    }
                }
            }
            visible[i] = show;
            if (show) positions[i] = entry.bone.position;
        }
        Color oldColor = Handles.color;
        Transform selected = Selection.activeTransform;
        for (int i = 0; i < bones.Length; i++)
        {
            if (!visible[i]) continue;
            BoneEntry entry = bones[i];
            Vector3 position = positions[i];
            Color color = GetColor(entry.group);
            Handles.color = color;
            float size = HandleUtility.GetHandleSize(position) * markerSize;
            if (entry.bone == selected) size *= 1.5f;
            if (type == EventType.Repaint && entry.child >= 0 && visible[entry.child])
            {
                entry.line[0] = position;
                entry.line[1] = positions[entry.child];
                Handles.DrawAAPolyLine(lineWidth, entry.line); // Reuse the cached two-point array.
            }
            // Passive dots register no picking controls, so they cannot capture Move clicks.
            // Even in optional selection mode, the selected bone stays passive.
            if (clickToSelectBones && entry.bone != selected)
            {
                if (Handles.Button(position, Quaternion.identity, size, size * 1.7f, DotCap))
                    Selection.activeGameObject = entry.bone.gameObject;
            }
            else if (type == EventType.Repaint)
                DotCap(0, position, Quaternion.identity, size, EventType.Repaint);
            if (type == EventType.Repaint && showSelectedName && entry.bone == selected)
            {
                if (labelStyle == null) labelStyle = new GUIStyle(EditorStyles.miniBoldLabel);
                labelStyle.normal.textColor = color;
                Handles.Label(position + Vector3.up * size * 2f, entry.label, labelStyle);
            }
        }
        Handles.color = oldColor;
    }

    private Color GetColor(Group group)
    {
        switch (group)
        {
            case Group.Left: return leftColor;
            case Group.Right: return rightColor;
            case Group.Body: return bodyColor;
            case Group.Face: return faceColor;
            case Group.Helper: return helperColor;
            default: return otherColor;
        }
    }

    private static string Normalize(string name)
    {
        string lower = name.ToLowerInvariant();
        int i = 0;
        while (i < lower.Length && (char.IsDigit(lower[i]) || lower[i] == '_')) i++;
        return lower.Substring(i);
    }

    private static Group Classify(string name)
    {
        string n = Normalize(name);
        if (n.Contains("helper")) return Group.Helper;
        if (n.Contains("head") || n.Contains("eye") || n.Contains("brow") || n.Contains("lip")
            || n.Contains("mouth") || n.Contains("nose") || n.StartsWith("ear", StringComparison.Ordinal) || n.Contains("hair")
            || n.Contains("glasses") || n.Contains("ribbon")) return Group.Face;
        if (n.EndsWith("_l", StringComparison.Ordinal) || n.Contains("_l_")) return Group.Left;
        if (n.EndsWith("_r", StringComparison.Ordinal) || n.Contains("_r_")) return Group.Right;
        if (n.Contains("root") || n.Contains("pelvis") || n.Contains("spine") || n.Contains("chest")
            || n.Contains("neck")) return Group.Body;
        return Group.Other;
    }

    private static bool PreferredChild(string parent, string child)
    {
        if (parent.StartsWith("root")) return child.StartsWith("pelvis");
        if (parent.StartsWith("pelvis")) return child.StartsWith("spine");
        if (parent.StartsWith("spine")) return child.StartsWith("spine") || child.StartsWith("chest");
        if (parent.StartsWith("chest")) return child.StartsWith("neck");
        if (parent.StartsWith("neck")) return child.StartsWith("head");
        if (parent.Contains("upperarm")) return child.Contains("forearm");
        if (parent.Contains("forearm")) return child.Contains("hand");
        if (parent.Contains("thigh")) return child.Contains("shin");
        if (parent.Contains("shin")) return child.Contains("foot");
        if (parent.Contains("foot")) return child.Contains("toe");
        return false;
    }
}
#endif
