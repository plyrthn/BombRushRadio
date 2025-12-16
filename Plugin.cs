using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Reptile;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace BombRushRadio;

[BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
public class BombRushRadio : BaseUnityPlugin
{
    public static ConfigEntry<KeyCode> ReloadKey;
    public static ConfigEntry<KeyCode> SkipKey;
    public static ConfigEntry<KeyCode> SkipKeyController;
    public static ConfigEntry<bool> RemoveBaseGameSongs;
    public static ConfigEntry<bool> StreamAudio;
    public static ConfigEntry<int> MaxConcurrentLoads;

    public static MusicPlayer MInstance;
    public static List<MusicTrack> Audios = new();
    public static Dictionary<string, MusicTrack> AudioLookup = new();
    public int ShouldBeDone;
    public int Done;
    private int ActiveLoads;

    private static readonly List<string> Loaded = new();

    public static bool InMainMenu = false;
    public static bool Loading;
    public static bool Skipping = false;

    private readonly AudioType[] _trackerTypes = new[] { AudioType.IT, AudioType.MOD, AudioType.S3M, AudioType.XM };
    private readonly string _songFolder = Path.Combine(Application.streamingAssetsPath, "Mods", "BombRushRadio", "Songs");

    [System.Diagnostics.Conditional("DEBUG")]
    private void DebugLog(string message)
    {
        Logger.LogInfo(message);
    }

    public int TotalFileCount;

    public void SanitizeSongs()
    {
        if (Core.Instance == null || Core.Instance.audioManager == null)
        {
            return;
        }

        if (Core.Instance.audioManager.musicPlayer != null)
        {
            var currentTracks = MInstance.musicTrackQueue.currentMusicTracks;
            var loadedSet = new HashSet<string>(Loaded);
            var toRemove = new List<MusicTrack>();

            int idx = 0;

            foreach (MusicTrack tr in Audios)
            {
                if (currentTracks.Contains(tr))
                {
                    currentTracks.Remove(tr);
                }
                else
                {
                    Logger.LogInfo("[BRR] Adding " + tr.Title);
                }

                string trackKey = Helpers.FormatMetadata(new[] { tr.Artist, tr.Title }, "dash");
                
                if (!loadedSet.Contains(trackKey))
                {
                    Logger.LogInfo("[BRR] Removing " + tr.Title);
                    toRemove.Add(tr);
                }

                currentTracks.Insert(1 + idx, tr);
                idx++;
            }

            foreach (MusicTrack tr in toRemove)
            {
                Audios.Remove(tr);
                AudioLookup.Remove($"{tr.Artist}|{tr.Title}");
                if (tr.AudioClip != null)
                {
                    tr.AudioClip.UnloadAudioData();
                }
            }
        }
    }

    public IEnumerator LoadAudioFile(string filePath, AudioType type)
    {
        while (ActiveLoads >= MaxConcurrentLoads.Value)
        {
            yield return null;
        }

        ActiveLoads++;
        DebugLog($"[BRR] Starting load (active: {ActiveLoads}/{MaxConcurrentLoads.Value}): {Path.GetFileName(filePath)}");

        string[] metadata = Helpers.GetMetadata(filePath, false);
        string songName = Helpers.FormatMetadata(metadata, "dash");
        string songKey = $"{metadata[0]}|{metadata[1]}";

        // Escape special characters so we don't get an HTML error when we send the request
        filePath = UnityWebRequest.EscapeURL(filePath);

        using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip("file:///" + filePath, type))
        {
            var downloadHandler = (DownloadHandlerAudioClip) www.downloadHandler;
            downloadHandler.streamAudio = StreamAudio.Value && !_trackerTypes.Contains(type);

            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.ConnectionError)
            {
                Logger.LogError(www.error);
            }
            else
            {
                Done++;

                MusicTrack musicTrack = ScriptableObject.CreateInstance<MusicTrack>();
                musicTrack.Artist = metadata[0];
                musicTrack.Title = metadata[1];
                musicTrack.isRepeatable = false;

                AudioClip myClip = downloadHandler.audioClip;
                myClip.name = songName;

                musicTrack.AudioClip = myClip;

                Audios.Add(musicTrack);
                AudioLookup[songKey] = musicTrack;

                Logger.LogInfo($"[BRR] Loaded {Helpers.FormatMetadata(metadata, "by")} ({Done}/{ShouldBeDone})");
                Loaded.Add(songName);
            }
        }

        ActiveLoads--;
        DebugLog($"[BRR] Finished load (active: {ActiveLoads}/{MaxConcurrentLoads.Value})");
    }

    public IEnumerator LoadFile(string f)
    {
        string extension = Path.GetExtension(f).ToLowerInvariant().Substring(1);
        string[] metadata = Helpers.GetMetadata(f, false);
        string songKey = $"{metadata[0]}|{metadata[1]}";

        if (AudioLookup.ContainsKey(songKey))
        {
            string songName = Helpers.FormatMetadata(metadata, "dash");
            Loaded.Add(songName);
            
            // prefer MP3 over other formats
            if (extension == "ogg" || extension == "flac" || extension == "wav")
            {
                Logger.LogInfo("[BRR] " + songName + " is already loaded, deleting duplicate " + extension.ToUpper() + " file.");
                File.Delete(f);
            }
            else
            {
                Logger.LogInfo("[BRR] " + songName + " is already loaded, skipping.");
            }
        }
        else
        {
            AudioType type = extension switch
            {
                "aif" => AudioType.AIFF,
                "aiff" => AudioType.AIFF,
                "it" => AudioType.IT,
                "mod" => AudioType.MOD,
                "mp2" => AudioType.MPEG,
                "mp3" => AudioType.MPEG,
                "ogg" => AudioType.OGGVORBIS,
                "s3m" => AudioType.S3M,
                "wav" => AudioType.WAV,
                "xm" => AudioType.XM,
                "flac" => AudioType.UNKNOWN,
                _ => AudioType.UNKNOWN
            };

            ShouldBeDone++;
            StartCoroutine(LoadAudioFile(f, type));
        }

        yield return null;
    }

    public IEnumerator SearchDirectories(string path = "")
    {
        string p = path.Length == 0 ? _songFolder : path;

        foreach (string f in Directory.GetDirectories(p))
        {
            Logger.LogInfo("[BRR] Searching directory " + f);
            StartCoroutine(SearchDirectories(f));
        }

        foreach (string f in Directory.GetFiles(p))
        {
            StartCoroutine(LoadFile(f));
        }

        yield return null;
    }

    public IEnumerator SkipTrack()
    {
        DebugLog($"[BRR] Skip requested - index: {MInstance.CurrentTrackIndex}/{MInstance.musicTrackQueue.AmountOfTracks}, track: {MInstance.GetMusicTrack(MInstance.CurrentTrackIndex)?.Title}");
        
        Skipping = true;
        MInstance.ForcePaused();
        MInstance.PlayNext();
        
        yield return new WaitForSeconds(0.3f);
        Skipping = false;
        
        DebugLog($"[BRR] Skip complete - now at index: {MInstance.CurrentTrackIndex}, track: {MInstance.GetMusicTrack(MInstance.CurrentTrackIndex)?.Title}");
    }

    public IEnumerator ReloadSongs()
    {
        DebugLog("[BRR] ===== RELOAD STARTED =====");
        Loaded.Clear();
        AudioLookup.Clear();
        Loading = true;
        ActiveLoads = 0;

        if (Audios.Count > 0)
        {
            if (Core.Instance.audioManager.musicPlayer.IsPlaying && MInstance != null)
            {
                Core.Instance.audioManager.musicPlayer.ForcePaused();
            }
        }

        Logger.LogInfo("[BRR] Loading songs...");
        ShouldBeDone = 0;
        Done = 0;

        yield return StartCoroutine(SearchDirectories());

        DebugLog("[BRR] Waiting for all loads to complete...");
        while (ActiveLoads > 0)
        {
            yield return null;
        }

        Logger.LogInfo($"[BRR] TOTAL SONGS LOADED: {Audios.Count}");
        Logger.LogInfo("[BRR] Bomb Rush Radio has been loaded!");
        DebugLog("[BRR] ===== RELOAD COMPLETE =====");
        Loading = false;

        Audios.Sort((t1, t2) => 
        {
            int artistCompare = string.Compare(t1.Artist, t2.Artist, StringComparison.OrdinalIgnoreCase);
            return artistCompare != 0 ? artistCompare : string.Compare(t1.Title, t2.Title, StringComparison.OrdinalIgnoreCase);
        });

        SanitizeSongs();
    }

    private void Awake()
    {
        // setup mod directory
        if (!Directory.Exists(_songFolder))
        {
            Directory.CreateDirectory(_songFolder);
        }

        // bind to config
        ReloadKey = Config.Bind("Settings", "Reload Key", KeyCode.F1, "Keybind used for reloading songs.");
        SkipKey = Config.Bind("Settings", "Skip Key", KeyCode.F2, "Keybind used for skipping to next song.");
        SkipKeyController = Config.Bind("Settings", "Skip Key (Controller)", KeyCode.JoystickButton9, "Controller button for skipping to next song. R3/Right Stick Click is usually JoystickButton9.");
        RemoveBaseGameSongs = Config.Bind("Settings", "Remove Base Game Songs", false, "Remove all base game songs from the music player.");
        StreamAudio = Config.Bind("Settings", "Stream Audio", true, "Whether to stream audio from disk or load at runtime (Streaming is faster but more CPU intensive)");
        MaxConcurrentLoads = Config.Bind("Settings", "Max Concurrent Loads", 5, "Maximum number of songs to load simultaneously (lower = less stuttering, higher = faster loading)");

        // load em
        StartCoroutine(ReloadSongs());

        var harmony = new Harmony("kade.bombrushradio");
        harmony.PatchAll();
        Logger.LogInfo("[BRR] Patched...");
    }

    private void Update()
    {
        if (Input.GetKeyDown(ReloadKey.Value) && !InMainMenu)
        {
            StartCoroutine(ReloadSongs());
        }

        // skip to next song - idea by goatgirl
        // only check controller after phone is loaded (means we're actually in game)
        bool skipPressed = Input.GetKeyUp(SkipKey.Value);
        if (WorldHandler.instance?.GetCurrentPlayer()?.phone != null)
        {
            skipPressed = skipPressed || Input.GetKeyUp(SkipKeyController.Value);
        }
        
        if (skipPressed && !InMainMenu)
        {
            DebugLog($"[BRR] Skip button pressed! Skipping: {Skipping}, IsPlaying: {MInstance?.IsPlaying}");
            if (MInstance != null && MInstance.IsPlaying && !Skipping)
            {
                StartCoroutine(SkipTrack());
            }
        }
    }
}