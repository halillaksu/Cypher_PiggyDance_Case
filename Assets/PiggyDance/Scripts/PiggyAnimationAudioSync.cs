#if UNITY_EDITOR
// Low-power revision. Install at Assets/PiggyDance/Editor/PiggyAnimationAudioSync.cs
// Menu: Tools > Piggy Dance > Animation Audio Sync
// Does not write animation keys, meshes, weights, or scene data.
// Internal AudioUtil is resolved once to delegates. Verify in Unity 2021.3.4f1.
using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public sealed class PiggyAnimationAudioSync : EditorWindow
{
    private const double PollInterval = 0.05; // At most 20 state reads/second while enabled.
    private const double ReadoutInterval = 0.5;
    private delegate void PlayPreviewDelegate(AudioClip clip, int sample, bool loop);

    [SerializeField] private AudioClip music;
    [SerializeField] private AnimationWindow animationWindow;
    [SerializeField] private float musicOffsetSeconds;
    [SerializeField] private float scrubDuration = 0.10f;
    [SerializeField] private int scrubStartsPerSecond = 5;
    [SerializeField] private bool liveTimeReadout;

    private PlayPreviewDelegate playPreview;
    private Action stopPreview;
    private bool syncEnabled, updateSubscribed, ownsPreview, continuousPreview, wasPlaying;
    private bool pendingScrub, loadRequested;
    private float pendingMusicTime, displayAnimationTime;
    private float previousTime = float.NaN;
    private AudioClip cachedMusic;
    private AnimationClip previousAnimation;
    private float musicLength;
    private int musicSamples, musicFrequency;
    private double nextPollAt, nextScrubAt, stopSnippetAt, nextReadoutAt, previousPollAt;
    private string error;

    [MenuItem("Tools/Piggy Dance/Animation Audio Sync")]
    public static void Open()
    {
        var window = GetWindow<PiggyAnimationAudioSync>("Animation Audio Sync");
        window.minSize = new Vector2(380, 370);
        window.Show();
    }

    private void OnEnable()
    {
        syncEnabled = false;
        updateSubscribed = false;
        ownsPreview = false;
        ResetTracking();
        ResolveAudioMethods();
        // There is deliberately no EditorApplication.update subscription here.
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
        AssemblyReloadEvents.beforeAssemblyReload += DisableSync;
        EditorApplication.quitting += DisableSync;
    }

    private void OnDisable()
    {
        DisableSync();
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        AssemblyReloadEvents.beforeAssemblyReload -= DisableSync;
        EditorApplication.quitting -= DisableSync;
    }

    private void ResolveAudioMethods()
    {
        error = null;
        playPreview = null;
        stopPreview = null;
        try
        {
            Type type = typeof(AudioImporter).Assembly.GetType("UnityEditor.AudioUtil");
            if (type == null) throw new MissingMemberException("UnityEditor.AudioUtil is unavailable.");
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo play = type.GetMethod("PlayPreviewClip", flags, null,
                new[] { typeof(AudioClip), typeof(int), typeof(bool) }, null);
            MethodInfo stop = type.GetMethod("StopAllPreviewClips", flags, null, Type.EmptyTypes, null);
            if (play == null || stop == null)
                throw new MissingMethodException("Required Editor audio preview methods are unavailable.");
            playPreview = (PlayPreviewDelegate)Delegate.CreateDelegate(typeof(PlayPreviewDelegate), play);
            stopPreview = (Action)Delegate.CreateDelegate(typeof(Action), stop);
        }
        catch (Exception exception)
        {
            error = "Editor audio setup failed: " + exception.Message;
        }
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Animation Audio Sync - Low Power", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Use outside Play mode. Select your character and dance clip in the " +
            "Animation window. No animation keys or rig data are changed.", MessageType.Info);

        EditorGUI.BeginChangeCheck();
        music = (AudioClip)EditorGUILayout.ObjectField("Music", music, typeof(AudioClip), false);
        musicOffsetSeconds = EditorGUILayout.FloatField("Music Offset (seconds)", musicOffsetSeconds);
        scrubDuration = EditorGUILayout.Slider("Scrub Snippet (seconds)", scrubDuration, 0.05f, 0.20f);
        scrubStartsPerSecond = EditorGUILayout.IntSlider("Scrub Starts / Second", scrubStartsPerSecond, 3, 10);
        liveTimeReadout = EditorGUILayout.Toggle("Live Time Readout", liveTimeReadout);
        if (EditorGUI.EndChangeCheck())
        {
            if (float.IsNaN(musicOffsetSeconds) || float.IsInfinity(musicOffsetSeconds)) musicOffsetSeconds = 0;
            ResetTracking();
        }
        EditorGUILayout.LabelField("Music time = animation time + offset", EditorStyles.miniLabel);

        using (new EditorGUI.DisabledScope(syncEnabled))
        {
            if (GUILayout.Button("Connect to Animation Window"))
            {
                animationWindow = GetWindow<AnimationWindow>();
                ResetTracking();
            }
        }
        using (new EditorGUI.DisabledScope(true))
            EditorGUILayout.ObjectField("Connected Window", animationWindow, typeof(AnimationWindow), true);

        bool available = animationWindow != null && music != null && error == null
            && !EditorApplication.isPlayingOrWillChangePlaymode;
        using (new EditorGUI.DisabledScope(!available))
        {
            bool requested = EditorGUILayout.ToggleLeft("Enable Audio Sync", syncEnabled);
            if (requested != syncEnabled)
            {
                if (requested) EnableSync(); else DisableSync();
            }
        }
        bool canControl = available && syncEnabled && animationWindow.animationClip != null;
        using (new EditorGUI.DisabledScope(!canControl))
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Preview Play"))
            {
                ResetTracking();
                animationWindow.playing = true;
            }
            if (GUILayout.Button("Pause"))
            {
                animationWindow.playing = false;
                ResetTracking();
            }
            if (GUILayout.Button("Restart"))
            {
                animationWindow.playing = false;
                animationWindow.time = 0;
                ResetTracking();
                animationWindow.playing = true;
            }
            EditorGUILayout.EndHorizontal();
        }
        if (liveTimeReadout)
        {
            EditorGUILayout.LabelField("Animation Time", displayAnimationTime.ToString("F3") + " s");
            EditorGUILayout.LabelField("Music Time", (displayAnimationTime + musicOffsetSeconds).ToString("F3") + " s");
        }
        if (GUILayout.Button("Stop Audio / Disable Sync")) DisableSync();
        if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
        else EditorGUILayout.HelpBox(syncEnabled
            ? "Enabled: 20 checks/second. Scrub audio is rate-limited. Live readout is optional."
            : "Disabled: no update callback and no periodic repaint requests.", MessageType.None);
    }

    private void EnableSync()
    {
        if (music == null || animationWindow == null || error != null
            || EditorApplication.isPlayingOrWillChangePlaymode) return;
        ResetTracking();
        syncEnabled = true;
        if (!updateSubscribed)
        {
            EditorApplication.update += Tick;
            updateSubscribed = true;
        }
        Repaint();
    }

    private void DisableSync()
    {
        if (updateSubscribed)
        {
            EditorApplication.update -= Tick;
            updateSubscribed = false;
        }
        syncEnabled = false;
        ResetTracking();
        // No repeated repaint: only this state change requests one.
        Repaint();
    }

    private void OnPlayModeChanged(PlayModeStateChange state)
    {
        DisableSync(); // Re-enable manually after returning to Edit mode.
    }

    private void ResetTracking()
    {
        StopAudio();
        previousTime = float.NaN;
        wasPlaying = false;
        pendingScrub = false;
        cachedMusic = null;
        previousAnimation = null;
        loadRequested = false;
        nextPollAt = nextScrubAt = nextReadoutAt = 0;
    }

    private void Tick()
    {
        double now = EditorApplication.timeSinceStartup;
        if (now < nextPollAt) return;
        nextPollAt = now + PollInterval;
        if (!syncEnabled || music == null || animationWindow == null || error != null
            || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
        {
            DisableSync();
            return;
        }

        AnimationClip selectedClip = animationWindow.animationClip;
        if (selectedClip != previousAnimation || music != cachedMusic)
        {
            StopAudio();
            previousTime = float.NaN;
            wasPlaying = pendingScrub = false;
            loadRequested = false;
            previousAnimation = selectedClip;
            cachedMusic = music;
            musicLength = music.length;
            musicSamples = music.samples;
            musicFrequency = music.frequency;
            Repaint(); // Once when the selected clip/music changes.
        }
        if (selectedClip == null)
        {
            StopAudio();
            previousTime = float.NaN;
            wasPlaying = pendingScrub = false;
            return;
        }

        float time = animationWindow.time;
        if (float.IsNaN(time) || float.IsInfinity(time)) return;
        bool playing = animationWindow.playing;
        bool first = float.IsNaN(previousTime);
        float step = first ? 0 : time - previousTime;
        bool moved = !first && Mathf.Abs(step) > 0.00001f;
        float audioTime = time + musicOffsetSeconds;
        bool inRange = audioTime >= 0 && audioTime < musicLength;
        displayAnimationTime = time;

        if (playing != wasPlaying) Repaint();
        if (playing)
        {
            pendingScrub = false;
            double elapsed = first ? 0 : now - previousPollAt;
            bool jumped = !first && (step < -0.001f || Math.Abs(step - elapsed) > 0.20);
            // Slow editor frames alone do not cause an audio restart: compare the
            // animation advance with actual elapsed time, rather than a fixed step.
            if (inRange && (!wasPlaying || !continuousPreview || jumped)) StartAudio(audioTime, true, now);
            else if (!inRange) StopAudio();
        }
        else
        {
            if (wasPlaying) StopAudio();
            if (moved)
            {
                pendingMusicTime = audioTime;
                pendingScrub = true; // Coalesce rapid changes to the latest requested time.
            }
            if (pendingScrub && now >= nextScrubAt)
            {
                if (StartAudio(pendingMusicTime, false, now))
                {
                    pendingScrub = false;
                    nextScrubAt = now + 1.0 / Mathf.Clamp(scrubStartsPerSecond, 3, 10);
                }
            }
            if (ownsPreview && !continuousPreview && now >= stopSnippetAt) StopAudio();
        }
        if (liveTimeReadout && moved && now >= nextReadoutAt)
        {
            nextReadoutAt = now + ReadoutInterval;
            Repaint(); // At most 2 time-readout refreshes/second, only while moving.
        }
        previousTime = time;
        previousPollAt = now;
        wasPlaying = playing;
    }

    // True: request handled, including silence outside the music range.
    // False: audio data is loading; retain the latest pending request for retry.
    private bool StartAudio(float seconds, bool continuous, double now)
    {
        StopAudio();
        if (music == null || seconds < 0 || seconds >= musicLength
            || float.IsNaN(seconds) || float.IsInfinity(seconds)
            || musicSamples <= 0 || musicFrequency <= 0) return true;
        AudioDataLoadState loadState = music.loadState;
        if (loadState == AudioDataLoadState.Unloaded)
        {
            if (!loadRequested)
            {
                loadRequested = true;
                music.LoadAudioData();
            }
            loadState = music.loadState;
        }
        if (loadState == AudioDataLoadState.Loading || loadState == AudioDataLoadState.Unloaded) return false;
        if (loadState == AudioDataLoadState.Failed)
        {
            Fail("Audio data could not be loaded. Check the music asset's import settings.");
            return true;
        }
        int sample = (int)Math.Min(musicSamples - 1L,
            Math.Max(0L, (long)Math.Floor((double)seconds * musicFrequency)));
        try
        {
            ownsPreview = true;
            playPreview(music, sample, false); // Cached delegate: no Invoke/object[] allocation.
            continuousPreview = continuous;
            stopSnippetAt = now + scrubDuration;
        }
        catch (Exception exception)
        {
            Fail("Editor audio preview failed: " + exception.Message);
        }
        return true;
    }

    private void StopAudio()
    {
        if (ownsPreview && stopPreview != null)
        {
            try { stopPreview(); }
            catch (Exception) { /* Editor may be reloading or shutting down. */ }
        }
        ownsPreview = continuousPreview = false;
    }

    private void Fail(string message)
    {
        error = message;
        DisableSync();
        Debug.LogWarning("Piggy Animation Audio Sync: " + message);
    }
}
#endif
