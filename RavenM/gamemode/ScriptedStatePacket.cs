using HarmonyLib;
using Ravenfield.Trigger;
using System.Collections;
using System.Collections.Generic;
using Steamworks;
using System.Linq;
namespace RavenM
{
    [HarmonyPatch(typeof(ScriptedGameMode), "StartGame")]
    public class ScriptedMissionPlayerCheckPatch
    {
        public static List<TriggerOnStart> triggerOnStart = new List<TriggerOnStart>();

        public static List<TriggerLoadCheckpoint> triggerCheckpoint = new List<TriggerLoadCheckpoint>();

        public static bool ready = false;


        static bool Prefix(ScriptedGameMode __instance)
        {
            if (IngameNetManager.instance.IsClient && !IngameNetManager.instance.IsHost)
                return false;
            if (ready)
            {
                //trigger every saved onstart trigger
                foreach (TriggerOnStart triggerOnStart in triggerOnStart)
                {
                    if (triggerOnStart != null)
                    {
                        triggerOnStart.Start();
                    }
                }
                foreach (TriggerLoadCheckpoint checkpoint in triggerCheckpoint)
                {
                    if (checkpoint != null)
                    {
                        checkpoint.Trigger();
                    }
                }
                triggerOnStart.Clear();
                triggerCheckpoint.Clear();
                ready = false;
                return true;
            }
            __instance.StartCoroutine(WaitForPlayers(__instance));
            return false;
        }


        public static IEnumerator WaitForPlayers(ScriptedGameMode gamemode)
        {
            ready = false;


            IngameUI.ShowOverlayText("Waiting For All Players");

            while (LobbySystem.instance.GetLobbyMembers().Any(x => SteamMatchmaking.GetLobbyMemberData(LobbySystem.instance.ActualLobbyID, x, "loaded") != "yes") ||
                LobbySystem.instance.GetLobbyMembers().Count > IngameNetManager.instance.GetPlayers().Count)
            {
                //wait until everyone is in the scene
                //just checking if all players have "loaded" set to true won't 100% work because the other lobby members could still be in the previous scene and are technically still loaded
                //we can also check if the players are inside the host's scene just to be extra sure
                yield return null;
            }
            IngameUI.ShowOverlayText("All Players Loaded!");
            ready = true;
            yield return null;


            gamemode.StartGame();
            yield break;
        }

    }


    //players might be left behind in scripted missions if they never get respawned.
    //usually, devs add respawn triggers right after the checkpoint. we can check for that
    [HarmonyPatch(typeof(TriggerSaveCheckpoint), "OnTriggered")]
    public class ScriptedMissionCheckpointRespawn
    {
        static void Prefix(TriggerSaveCheckpoint __instance)
        {
            if (IngameNetManager.instance.IsHost && IngameNetManager.instance.IsClient)
                return;

            TriggerLoadCheckpoint[] array = UnityEngine.Object.FindObjectsOfType<TriggerLoadCheckpoint>();
            for (int i = 0; i < array.Length; i++)
            {
                if (array[i].checkpoint == __instance.checkpoint)
                {
                    foreach (TriggerReceiver receiver in array[i].onCheckpointLoadedTrigger.destinations)
                    {
                        if (typeof(TriggerSpawnPlayer).IsAssignableFrom(receiver.GetType()))
                        {
                            IngameUI.ShowOverlayText("Checkpoint Respawn");
                            (receiver as TriggerSpawnPlayer).ReceiveSignal(new TriggerSignal());
                            return;
                        }
                    }
                }
            }
        }
    }


    [HarmonyPatch(typeof(TriggerOnStart), "Start")]
    public class ScriptedMissionStartPatch
    {
        static bool Prefix(TriggerOnStart __instance)
        {
            if (!IngameNetManager.instance.IsHost && IngameNetManager.instance.IsClient)
                return false;
            if (IngameNetManager.instance.IsHost)
            {
                if (GameModeBase.activeGameMode is ScriptedGameMode && __instance.type == TriggerOnStart.Type.OnStart)
                {
                    if (ScriptedMissionPlayerCheckPatch.ready)
                    {
                        return true;
                    }
                    else
                    {
                        ScriptedMissionPlayerCheckPatch.triggerOnStart.Add(__instance);
                        return false;
                    }
                }
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(TriggerLoadCheckpoint), "Trigger")]
    public class ScriptedMissionCheckpointPatch
    {
        static bool Prefix(TriggerLoadCheckpoint __instance)
        {
            if (!IngameNetManager.instance.IsHost && IngameNetManager.instance.IsClient)
                return false;
            if (IngameNetManager.instance.IsHost)
            {
                if (GameModeBase.activeGameMode is ScriptedGameMode)
                {
                    if (ScriptedMissionPlayerCheckPatch.ready)
                    {
                        return true;
                    }
                    else
                    {
                        ScriptedMissionPlayerCheckPatch.triggerCheckpoint.Add(__instance);
                        return false;
                    }
                }
            }
            return true;
        }
    }
}