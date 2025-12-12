using HarmonyLib;
using Reptile;

namespace BombRushRadio;

[HarmonyPatch(typeof(MusicPlayerBuffer), nameof(MusicPlayerBuffer.BufferMusicTrack))]
public class MusicPlayerBuffer_BufferMusicTrack_Patches
{
    static bool Prefix(MusicPlayerBuffer __instance, MusicTrack musicTrackToLoad) // tell the game to not unload our files please lol
    {
        if (musicTrackToLoad == null || musicTrackToLoad.AudioClip == null)
        {
            return false;
        }

        MusicPlayerData musicPlayerData = __instance.FindMusicPlayerDataByMusicTrack(musicTrackToLoad);

        if (musicPlayerData == null)
        {
            musicPlayerData = __instance.CreateNewMusicPlayerDataObject(musicTrackToLoad);
        }

        __instance.BufferMusicPlayerData(musicPlayerData);
        return false;
    }
}

[HarmonyPatch(typeof(MusicPlayerBuffer), nameof(MusicPlayerBuffer.UnloadMusicPlayerData))]
public class MusicPlayerBuffer_Patches
{
    static bool Prefix(MusicPlayerData musicPlayerData) // tell the game to not unload our files please lol
    {
        string trackKey = $"{musicPlayerData.Artist}|{musicPlayerData.Title}";

        if (BombRushRadio.AudioLookup.ContainsKey(trackKey))
        {
            return false;
        }

        return true;
    }
}