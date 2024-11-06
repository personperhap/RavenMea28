﻿using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Steamworks;
using HarmonyLib;
using System;
using System.Diagnostics;
using System.Linq;
using RavenM.RSPatch.Wrapper;
using System.Text.RegularExpressions;
using Ravenfield.Mutator.Configuration;
using SimpleJSON;
using System.Globalization;
using Ravenfield.Trigger;

namespace RavenM
{
    [HarmonyPatch(typeof(ModManager), nameof(ModManager.OnGameManagerStart))]
    public class CleanupListPatch
    {
        static void Prefix(ModManager __instance)
        {
            if (__instance.noContentMods)
                __instance.noWorkshopMods = true;
        }
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.StartLevel))]
    public class OnStartPatch
    {
        static bool Prefix()
        {
            if (LobbySystem.instance.InLobby && !LobbySystem.instance.IsLobbyOwner && !LobbySystem.instance.ReadyToPlay)
                return false;
            OptionsPatch.SetConfigValues(false);

            // Only start if all members are ready.
            if (LobbySystem.instance.LobbyDataReady && LobbySystem.instance.IsLobbyOwner)
            {
                foreach (var memberId in LobbySystem.instance.GetLobbyMembers())
                {
                    if (SteamMatchmaking.GetLobbyMemberData(LobbySystem.instance.ActualLobbyID, memberId, "loaded") != "yes")
                    {
                        if (!LobbySystem.instance.HasCommittedToStart) {
                            LobbySystem.instance.IntentionToStart = true;
                            return false;
                        }
                    }
                }
                LobbySystem.instance.HasCommittedToStart = false;
            }

            if (LobbySystem.instance.IsLobbyOwner)
            {
                IngameNetManager.instance.OpenRelay();

                LobbySystem.instance.SetLobbyDataDedup("started", "yes");
            }

            LobbySystem.instance.ReadyToPlay = false;
            return true;
        }
    }

    [HarmonyPatch(typeof(LoadoutUi), nameof(LoadoutUi.OnDeployClick))]
    public class FirstDeployPatch
    {
        static bool Prefix()
        {
            if (LobbySystem.instance.InLobby)
            {
                // Ignore players who joined mid-game.
                if ((bool)typeof(LoadoutUi).GetField("hasAcceptedLoadoutOnce", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(LoadoutUi.instance))
                    return true;

                // Wait for everyone to load in first.
                foreach (var memberId in LobbySystem.instance.GetLobbyMembers())
                {
                    if (SteamMatchmaking.GetLobbyMemberData(LobbySystem.instance.ActualLobbyID, memberId, "ready") != "yes")
                    {
                        // Ignore players that just joined and are loading mods.
                        if (SteamMatchmaking.GetLobbyMemberData(LobbySystem.instance.ActualLobbyID, memberId, "loaded") != "yes")
                            continue;

                        return false;
                    }
                }
            }
            if (IngameNetManager.instance.IsHost || LobbySystem.instance.IsLobbyOwner)
            {
                Plugin.logger.LogInfo("SendNetworkGameObjectsHashesPacket()");
                WLobby.SendNetworkGameObjectsHashesPacket();
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(GameManager), "StartGame")]
    public class FinalizeStartPatch
    {
        // Maps sometimes have their own vehicles. We need to tag them.
        static void Prefix()
        {
            if (!LobbySystem.instance.InLobby)
                return;

            // The game will destroy any vehicles that have already spawned. Ignore them.
            var ignore = UnityEngine.Object.FindObjectsOfType<Vehicle>();
            Plugin.logger.LogInfo($"Ignore list: {ignore.Length}");

            var map = GameManager.instance.lastMapEntry;

            foreach (var vehicle in Resources.FindObjectsOfTypeAll<Vehicle>())
            {
                if (!vehicle.TryGetComponent(out PrefabTag _) && !Array.Exists(ignore, x => x == vehicle))
                {
                    Plugin.logger.LogInfo($"Detected map vehicle with name: {vehicle.name}, and from map: {map.metaData.displayName}.");

                    var tag = vehicle.gameObject.AddComponent<PrefabTag>();
                    tag.NameHash = vehicle.name.GetHashCode();
                    tag.Mod = (ulong)map.metaData.displayName.GetHashCode();
                    IngameNetManager.PrefabCache[new Tuple<int, ulong>(tag.NameHash, tag.Mod)] = vehicle.gameObject;
                }
            }

            foreach (var projectile in Resources.FindObjectsOfTypeAll<Projectile>())
            {
                if (!projectile.TryGetComponent(out PrefabTag _))
                {
                    Plugin.logger.LogInfo($"Detected map projectile with name: {projectile.name}, and from map: {map.metaData.displayName}.");

                    var tag = projectile.gameObject.AddComponent<PrefabTag>();
                    tag.NameHash = projectile.name.GetHashCode();
                    tag.Mod = (ulong)map.metaData.displayName.GetHashCode();
                    IngameNetManager.PrefabCache[new Tuple<int, ulong>(tag.NameHash, tag.Mod)] = projectile.gameObject;
                }
            }

            foreach (var destructible in Resources.FindObjectsOfTypeAll<Destructible>())
            {
                var prefab = DestructiblePacket.Root(destructible);

                if (!prefab.TryGetComponent(out PrefabTag _))
                {
                    Plugin.logger.LogInfo($"Detected map destructible with name: {prefab.name}, and from map: {map.metaData.displayName}.");

                    IngameNetManager.TagPrefab(prefab, (ulong)map.metaData.displayName.GetHashCode());
                }
            }

            foreach (var destructible in UnityEngine.Object.FindObjectsOfType<Destructible>())
            {
                // One shot created destructibles -- not cool!
                var root = DestructiblePacket.Root(destructible);

                if (IngameNetManager.instance.ClientDestructibles.ContainsValue(root))
                    continue;

                // FIXME: Shitty hack. The assumption is map destructibles are consistent
                // and thus will always spawn in the same positions regardless of the
                // client run. I have no idea how correct this assumption actually is.
                int id = root.transform.position.GetHashCode() ^ root.name.GetHashCode();

                if (root.TryGetComponent(out GuidComponent guid))
                    id = guid.guid;
                else
                    root.AddComponent<GuidComponent>().guid = id;

                IngameNetManager.instance.ClientDestructibles[id] = root;

                Plugin.logger.LogInfo($"Registered new destructible root with name: {root.name} and id: {id}");
            }

            IngameNetManager.instance.MapWeapons.Clear();
            foreach (var triggerEquipWeapon in Resources.FindObjectsOfTypeAll<TriggerEquipWeapon>())
            {
                if (triggerEquipWeapon.weaponType == TriggerEquipWeapon.WeaponType.FromWeaponEntry
                    && triggerEquipWeapon.weaponEntry != null
                    && !WeaponManager.instance.allWeapons.Contains(triggerEquipWeapon.weaponEntry))
                {
                    var entry = triggerEquipWeapon.weaponEntry;
                    Plugin.logger.LogInfo($"Detected map weapon with name: {entry.name}, and from map: {map.metaData.displayName}.");
                    IngameNetManager.instance.MapWeapons.Add(entry);
                }
            }
            EffectUi.Clear();
        }

        static void Postfix()
        {
            if (!LobbySystem.instance.LobbyDataReady)
                return;

            if (LobbySystem.instance.IsLobbyOwner)
                IngameNetManager.instance.StartAsServer();
            else
                IngameNetManager.instance.StartAsClient(LobbySystem.instance.OwnerID);

            SteamMatchmaking.SetLobbyMemberData(LobbySystem.instance.ActualLobbyID, "ready", "yes");
        }
    }

    [HarmonyPatch(typeof(MainMenu), nameof(MainMenu.GoBack))]
    public class GoBackPatch
    {
        static bool Prefix()
        {
            if (LobbySystem.instance.InLobby && (int)typeof(MainMenu).GetField("page", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(MainMenu.instance) == MainMenu.PAGE_INSTANT_ACTION)
                return false;

            return true;
        }
    }

    [HarmonyPatch(typeof(InstantActionMaps), "SetupSkinList")]
    public class SkinListPatch
    {
        static void Prefix() => ModManager.instance.actorSkins.Sort((x, y) => x.name.CompareTo(y.name));
    }

    [HarmonyPatch(typeof(ModManager), nameof(ModManager.FinalizeLoadedModContent))]
    public class AfterModsLoadedPatch
    {
        static void Postfix()
        {
            if (InstantActionMaps.instance != null)
            {
                // We need to update the skin dropdown with the new mods.
                typeof(InstantActionMaps).GetMethod("SetupSkinList", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(InstantActionMaps.instance, null);
            }

            ModManager.instance.ContentChanged();

            // Sort vehicles
            var moddedVehicles = ModManager.AllVehiclePrefabs().ToList();
            moddedVehicles.Sort((x, y) => x.name.CompareTo(y.name));
            LobbySystem.instance.sortedModdedVehicles = moddedVehicles;

            // Sort mutators
            ModManager.instance.loadedMutators.Sort((x, y) => x.name.CompareTo(y.name));

            if (!LobbySystem.instance.InLobby || !LobbySystem.instance.LobbyDataReady || LobbySystem.instance.IsLobbyOwner || LobbySystem.instance.ModsToDownload.Count > 0)
                return;

            SteamMatchmaking.SetLobbyMemberData(LobbySystem.instance.ActualLobbyID, "loaded", "yes");
        }
    }

    [HarmonyPatch(typeof(SteamMatchmaking), nameof(SteamMatchmaking.LeaveLobby))]
    public class OnLobbyLeavePatch
    {
        static void Postfix()
        {
            LobbySystem.instance.InLobby = false;
            LobbySystem.instance.ReadyToPlay = true;
            LobbySystem.instance.LobbyDataReady = false;
            if (LobbySystem.instance.LoadedServerMods)
                LobbySystem.instance.RequestModReload = true;
            LobbySystem.instance.IsLobbyOwner = false;

            ChatManager.instance.ResetChat();
        }
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.ReturnToMenu))]
    public class LeaveOnEndGame
    {
        static void Prefix()
        {
            if (!LobbySystem.instance.InLobby || LobbySystem.instance.IsLobbyOwner)
                return;

            // Exit the lobby if we actually want to leave.
            if (new StackFrame(2).GetMethod().Name == "Menu")
                SteamMatchmaking.LeaveLobby(LobbySystem.instance.ActualLobbyID);
        }

        static void Postfix()
        {
            if (!LobbySystem.instance.InLobby)
                return;

            if (LobbySystem.instance.IsLobbyOwner)
            {
                LobbySystem.instance.SetLobbyDataDedup("started", "false");
            }

            SteamMatchmaking.SetLobbyMemberData(LobbySystem.instance.ActualLobbyID, "ready", "no");
        }
    }

    [HarmonyPatch(typeof(ModManager), nameof(ModManager.GetActiveMods))]
    public class ActiveModsPatch
    {
        static bool Prefix(ModManager __instance, ref List<ModInformation> __result)
        {
            if (LobbySystem.instance.LoadedServerMods && LobbySystem.instance.ServerMods.Count > 0)
            {
                __result = new List<ModInformation>();
                foreach (var mod in __instance.mods)
                {
                    if (LobbySystem.instance.ServerMods.Contains(mod.workshopItemId))
                    {
                        __result.Add(mod);
                    }
                }
                return false;
            }

            return true;
        }
    }

    public class LobbySystem : MonoBehaviour
    {
        public static LobbySystem instance;

        public string LobbyName = "My Lobby";

        public bool PrivateLobby = false;

        public bool ShowOnList = true;

        public bool MidgameJoin = false;

        public string JoinLobbyID = string.Empty;

        public bool InLobby = false;

        public bool LobbyDataReady = false;

        public string LobbyMemberCap = "250";

        public CSteamID ActualLobbyID = CSteamID.Nil;

        public bool IsLobbyOwner = false;

        // FIXME: A stack is maybe overkill. There are only 3 menu states.
        public Stack<string> GUIStack = new Stack<string>();

        public CSteamID OwnerID = CSteamID.Nil;

        public bool ReadyToPlay = false;

        public List<PublishedFileId_t> ServerMods = new List<PublishedFileId_t>();

        public List<PublishedFileId_t> ModsToDownload = new List<PublishedFileId_t>();

        public bool LoadedServerMods = false;

        public bool RequestModReload = false;

        public Texture2D LobbyBackground = new Texture2D(1, 1);

        public Texture2D ProgressTexture = new Texture2D(1, 1);

        public List<CSteamID> OpenLobbies = new List<CSteamID>();

        public CSteamID LobbyView = CSteamID.Nil;

        public List<CSteamID> CurrentKickedMembers = new List<CSteamID>();

        public CSteamID KickPrompt = CSteamID.Nil;

        public string NotificationText = string.Empty;

        public bool nameTagsEnabled = true;

        public bool nameTagsForTeamOnly = false;

        public List<GameObject> sortedModdedVehicles = new List<GameObject>();

        public Dictionary<string, Tuple<String, float>> LobbySetCache = new Dictionary<string, Tuple<String, float>>();

        public Dictionary<string, Tuple<String, float>> LobbySetMemberCache = new Dictionary<string, Tuple<String, float>>();

        public static readonly float SET_DEADLINE = 5f; // Seconds

        public bool IntentionToStart = false;

        public bool HasCommittedToStart = false;

        private void Awake()
        {
            instance = this;

            LobbyBackground.SetPixel(0, 0, Color.black);
            LobbyBackground.Apply();

            ProgressTexture.SetPixel(0, 0, Color.green);
            ProgressTexture.Apply();
        }

        private void Start()
        {
            Callback<LobbyEnter_t>.Create(OnLobbyEnter);
            Callback<DownloadItemResult_t>.Create(OnItemDownload);
            Callback<LobbyMatchList_t>.Create(OnLobbyList);
            Callback<LobbyDataUpdate_t>.Create(OnLobbyData);
        }

        public List<CSteamID> GetLobbyMembers()
        {
            int len = SteamMatchmaking.GetNumLobbyMembers(ActualLobbyID);
            var ret = new List<CSteamID>(len);

            for (int i = 0; i < len; i++)
            {
                var member = SteamMatchmaking.GetLobbyMemberByIndex(ActualLobbyID, i);
                if (!CurrentKickedMembers.Contains(member))
                    ret.Add(member);
            }

            return ret;
        }

        public bool HostLoaded()
        {
            return SteamMatchmaking.GetLobbyMemberData(ActualLobbyID, OwnerID, "loaded") == "true";
        }

        public void SetLobbyDataDedup(string key, string value)
        {
            if (!InLobby || !LobbyDataReady || !IsLobbyOwner)
                return;

            // De-dup any lobby values since apparently Steam doesn't do that for you.
            if (LobbySetCache.TryGetValue(key, out Tuple<String, float> oldValue) && oldValue.Item1 == value && Time.time < oldValue.Item2)
                return;

            SteamMatchmaking.SetLobbyData(ActualLobbyID, key, value);
            LobbySetCache[key] = new Tuple<String, float>(value, Time.time + SET_DEADLINE);
        }

        public void SetLobbyMemberDataDedup(string key, string value)
        {
            if (!InLobby || !LobbyDataReady)
                return;

            // De-dup any lobby values since apparently Steam doesn't do that for you.
            if (LobbySetMemberCache.TryGetValue(key, out Tuple<String, float> oldValue) && oldValue.Item1 == value && Time.time < oldValue.Item2)
                return;

            SteamMatchmaking.SetLobbyMemberData(ActualLobbyID, key, value);
            LobbySetMemberCache[key] = new Tuple<String, float>(value, Time.time + SET_DEADLINE);
        }

        private void OnLobbyData(LobbyDataUpdate_t pCallback)
        {
            var lobby = new CSteamID(pCallback.m_ulSteamIDLobby);

            if (pCallback.m_bSuccess == 0 || SteamMatchmaking.GetLobbyDataCount(lobby) == 0 || SteamMatchmaking.GetLobbyData(lobby, "hidden") == "true")
                OpenLobbies.Remove(lobby);
        }

        private void OnLobbyEnter(LobbyEnter_t pCallback)
        {
            Plugin.logger.LogInfo("Joined lobby!");
            CurrentKickedMembers.Clear();
            LobbySetCache.Clear();
            RequestModReload = false;
            LoadedServerMods = false;

            if (pCallback.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                NotificationText = "Unknown error joining lobby. (Does it still exist?)";
                InLobby = false;
                return;
            }

            LobbyDataReady = true;
            ActualLobbyID = new CSteamID(pCallback.m_ulSteamIDLobby);

            ChatManager.instance.PushLobbyChatMessage($"Welcome to the lobby! Press {ChatManager.instance.GlobalChatKeybind} to chat.");

            if (IsLobbyOwner)
            {
                OwnerID = SteamUser.GetSteamID();
                SetLobbyDataDedup("owner", OwnerID.ToString());
                SetLobbyDataDedup("build_id", Plugin.BuildGUID);
                SetLobbyDataDedup("lobbyname", LobbyName);
                if (!ShowOnList)
                    SetLobbyDataDedup("hidden", "true");
                if (MidgameJoin)
                    SetLobbyDataDedup("hotjoin", "true");
                if (nameTagsEnabled)
                    SetLobbyDataDedup("nameTags", "true");
                if (nameTagsForTeamOnly)
                    SetLobbyDataDedup("nameTagsForTeamOnly", "true");

                bool needsToReload = false;
                List<PublishedFileId_t> mods = new List<PublishedFileId_t>();

                foreach (var mod in ModManager.instance.GetActiveMods())
                {
                    if (mod.workshopItemId.ToString() == "0")
                    {
                        mod.enabled = false;
                        needsToReload = true;
                    }
                    else
                        mods.Add(mod.workshopItemId);
                }

                if (needsToReload)
                    ModManager.instance.ReloadModContent();
                SetLobbyDataDedup("owner", OwnerID.ToString());
                SetLobbyDataDedup("mods", string.Join(",", mods.ToArray()));
                SteamMatchmaking.SetLobbyMemberData(ActualLobbyID, "loaded", "yes");
                SetLobbyDataDedup("started", "false");

                ulong totalModSize = 0;

                foreach (PublishedFileId_t mod in mods)
                {
                    SteamUGC.GetItemInstallInfo(mod, out ulong size, out string folder, (1024 * 32), out uint timestamp);
                    Plugin.logger.LogInfo($"Checking mod {mod.m_PublishedFileId}, size: {size}");
                    totalModSize += size;
                }

                if (totalModSize > 0)
                {
                    SetLobbyDataDedup("modtotalsize", $"{ Math.Round(totalModSize / Math.Pow(1024, 2), 2)}MB");
                }
                else
                {
                    SetLobbyDataDedup("modtotalsize", " Vanilla");
                }
                
            }
            else
            {
                OwnerID = new CSteamID(ulong.Parse(SteamMatchmaking.GetLobbyData(ActualLobbyID, "owner")));
                Plugin.logger.LogInfo($"Host ID: {OwnerID}");

                MainMenu.instance.OpenPageIndex(MainMenu.PAGE_INSTANT_ACTION);
                ReadyToPlay = false;

                if (Plugin.BuildGUID != SteamMatchmaking.GetLobbyData(ActualLobbyID, "build_id"))
                {
                    Plugin.logger.LogInfo("Build ID mismatch! Leaving lobby.");
                    NotificationText = "You cannot join this lobby because you and the host are using different versions of RavenM.";
                    SteamMatchmaking.LeaveLobby(ActualLobbyID);
                    return;
                }

                ServerMods.Clear();
                ModsToDownload.Clear();
                string[] mods = SteamMatchmaking.GetLobbyData(ActualLobbyID, "mods").Split(',');
                foreach (string mod_str in mods)
                {
                    if (mod_str == string.Empty)
                        continue;
                    PublishedFileId_t mod_id = new PublishedFileId_t(ulong.Parse(mod_str));
                    if (mod_id.ToString() == "0")
                        continue;

                    ServerMods.Add(mod_id);

                    bool alreadyHasMod = false;
                    foreach (var mod in ModManager.instance.mods)
                    {
                        if (mod.workshopItemId == mod_id)
                        {
                            alreadyHasMod = true;
                            break;
                        }
                    }

                    if (!alreadyHasMod)
                    {
                        ModsToDownload.Add(mod_id);
                    }
                }
                SteamMatchmaking.SetLobbyMemberData(ActualLobbyID, "modsDownloaded", (ServerMods.Count - ModsToDownload.Count).ToString());
                TriggerModRefresh();
                bool nameTagsConverted = bool.TryParse(SteamMatchmaking.GetLobbyData(ActualLobbyID, "nameTags"), out bool nameTagsOn);
                if (nameTagsConverted)
                {
                    nameTagsEnabled = nameTagsOn;
                }
                else
                {
                    nameTagsEnabled = false;
                }
                bool nameTagsTeamOnlyConverted = bool.TryParse(SteamMatchmaking.GetLobbyData(ActualLobbyID, "nameTagsForTeamOnly"), out bool nameTagsForTeamOnlyOn);
                if (nameTagsTeamOnlyConverted)
                {
                    nameTagsForTeamOnly = nameTagsForTeamOnlyOn;
                }
                else
                {
                    nameTagsForTeamOnly = false;
                }
                if (SteamMatchmaking.GetLobbyData(ActualLobbyID, "started") == "yes" && SteamMatchmaking.GetLobbyData(ActualLobbyID, "hotjoin") != "true")
                {
                    Plugin.logger.LogInfo("The game has already started :( Leaving lobby.");
                    NotificationText = "This lobby has already started a match and has disabled mid-game joining or is playing a gamemode that does not support it.";
                    SteamMatchmaking.LeaveLobby(ActualLobbyID);
                    return;
                }
            }
        }

        private void OnItemDownload(DownloadItemResult_t pCallback)
        {
            Plugin.logger.LogInfo($"Downloaded mod! {pCallback.m_nPublishedFileId}");
            if (ModsToDownload.Contains(pCallback.m_nPublishedFileId))
            {
                var mod = (ModInformation)typeof(ModManager).GetMethod("AddWorkshopItemAsMod", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(ModManager.instance, new object[] { pCallback.m_nPublishedFileId });
                mod.hideInModList = true;
                mod.enabled = false;

                ModsToDownload.Remove(pCallback.m_nPublishedFileId);

                if (InLobby && LobbyDataReady)
                    SteamMatchmaking.SetLobbyMemberData(ActualLobbyID, "modsDownloaded", (ServerMods.Count - ModsToDownload.Count).ToString());

                TriggerModRefresh();
            }
        }

        public void TriggerModRefresh()
        {
            if (ModsToDownload.Count == 0)
            {
                Plugin.logger.LogInfo($"All server mods downloaded.");

                if (InLobby && LobbyDataReady && !IsLobbyOwner)
                {
                    List<bool> oldState = new List<bool>();

                    foreach (var mod in ModManager.instance.mods)
                    {
                        oldState.Add(mod.enabled);

                        mod.enabled = ServerMods.Contains(mod.workshopItemId);
                    }

                    // Clones the list of enabled mods.
                    ModManager.instance.ReloadModContent();
                    LoadedServerMods = true;

                    for (int i = 0; i < ModManager.instance.mods.Count; i++)
                        ModManager.instance.mods[i].enabled = oldState[i];
                }
            }
            else
            {
                var mod_id = ModsToDownload[0];
                bool isDownloading = SteamUGC.DownloadItem(mod_id, true);
                Plugin.logger.LogInfo($"Downloading mod with id: {mod_id} -- {isDownloading}");
            }
        }

        private void StartAsClient()
        {
            ReadyToPlay = true;
            //No initial bots! Many errors otherwise!
            InstantActionMaps.instance.botNumberField.text = "0";
            InstantActionMaps.instance.StartGame();
        }

        private void OnLobbyList(LobbyMatchList_t pCallback)
        {
            Plugin.logger.LogInfo("Got lobby list.");

            OpenLobbies.Clear();
            for (int i = 0; i < pCallback.m_nLobbiesMatching; i++)
            {
                var lobby = SteamMatchmaking.GetLobbyByIndex(i);
                Plugin.logger.LogInfo($"Requesting lobby data for {lobby} -- {SteamMatchmaking.RequestLobbyData(lobby)}");
                OpenLobbies.Add(lobby);
            }
        }

        private void Update()
        {
            if (GameManager.instance == null || GameManager.IsIngame() || GameManager.IsInLoadingScreen())
                return;

            if (Input.GetKeyDown(KeyCode.M) && !InLobby)
            {
                if (GUIStack.Count == 0)
                    GUIStack.Push("Main");
                else
                    GUIStack.Clear();
            }

            if (MainMenu.instance != null
                && (int)typeof(MainMenu).GetField("page", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(MainMenu.instance) < MainMenu.PAGE_INSTANT_ACTION
                && InLobby)
                MainMenu.instance.OpenPageIndex(MainMenu.PAGE_INSTANT_ACTION);

            if (LoadedServerMods && RequestModReload)
            {
                LoadedServerMods = false;
                RequestModReload = false;
                ModManager.instance.ReloadModContent();
            }

            if (!LobbyDataReady)
                return;

            // TODO: Ok. This is really bad. We should either:
            // A) Update the menu items only when they are changed, or,
            // B) Sidestep the menu entirely, and send the game information
            //     when the host starts.
            // The latter option is the cleanest and most efficient way, but
            // the former at least has visual input for the non-host clients,
            // which is also important.
            // InstantActionMaps.instance.gameModeDropdown.value = 0;
            int customMapOptionIndex = (int)typeof(InstantActionMaps).GetField("customMapOptionIndex", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(InstantActionMaps.instance);
            var entries = (List<InstantActionMaps.MapEntry>)typeof(InstantActionMaps).GetField("entries", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(InstantActionMaps.instance);
            // Don't allow spectator.
            if (InstantActionMaps.instance.teamDropdown.value == 2)
            {
                InstantActionMaps.instance.teamDropdown.value = 0;
            }
            SetLobbyMemberDataDedup("team", InstantActionMaps.instance.teamDropdown.value == 0 ? "<color=blue>E</color>" : "<color=red>R</color>");

            if (IsLobbyOwner)
            {
                SetLobbyDataDedup("gameMode", InstantActionMaps.instance.gameModeDropdown.value.ToString());
                SetLobbyDataDedup("nightMode", InstantActionMaps.instance.nightToggle.isOn.ToString());
                SetLobbyDataDedup("playerHasAllWeapons", InstantActionMaps.instance.playerHasAllWeaponsToggle.isOn.ToString());
                SetLobbyDataDedup("reverseMode", InstantActionMaps.instance.reverseToggle.isOn.ToString());
                SetLobbyDataDedup("botNumberField", InstantActionMaps.instance.botNumberField.text);
                SetLobbyDataDedup("balance", InstantActionMaps.instance.balanceSlider.value.ToString(CultureInfo.InvariantCulture));
                SetLobbyDataDedup("respawnTime", InstantActionMaps.instance.respawnTimeField.text);
                SetLobbyDataDedup("gameLength", InstantActionMaps.instance.gameLengthDropdown.value.ToString());
                SetLobbyDataDedup("loadedLevelEntry", InstantActionMaps.instance.mapDropdown.value.ToString());
                // For SpecOps.
                if (InstantActionMaps.instance.gameModeDropdown.value == 1)
                {
                    SetLobbyDataDedup("team", InstantActionMaps.instance.teamDropdown.value.ToString());
                }

                if (InstantActionMaps.instance.mapDropdown.value == customMapOptionIndex)
                {
                    SetLobbyDataDedup("customMap", entries[customMapOptionIndex].metaData.displayName);
                }

                for (int i = 0; i < 2; i++)
                {
                    var teamInfo = GameManager.instance.gameInfo.team[i];

                    var weapons = new List<int>();
                    foreach (var weapon in teamInfo.availableWeapons)
                    {
                        weapons.Add(weapon.nameHash);
                    }
                    string weaponString = string.Join(",", weapons.ToArray());
                    SetLobbyDataDedup(i + "weapons", weaponString);

                    foreach (var vehiclePrefab in teamInfo.vehiclePrefab)
                    {
                        var type = vehiclePrefab.Key;
                        var prefab = vehiclePrefab.Value;

                        bool isDefault = true; // Default vehicle.
                        int idx = Array.IndexOf(ActorManager.instance.defaultVehiclePrefabs, prefab);

                        if (idx == -1)
                        {
                            isDefault = false;
                            idx = sortedModdedVehicles.IndexOf(prefab);
                        }

                        SetLobbyDataDedup(i + "vehicle_" + type, prefab == null ? "NULL" : isDefault + "," + idx);
                    }

                    foreach (var turretPrefab in teamInfo.turretPrefab)
                    {
                        var type = turretPrefab.Key;
                        var prefab = turretPrefab.Value;

                        bool isDefault = true; // Default turret.
                        int idx = Array.IndexOf(ActorManager.instance.defaultTurretPrefabs, prefab);

                        if (idx == -1)
                        {
                            isDefault = false;
                            var moddedTurrets = ModManager.AllTurretPrefabs().ToList();
                            moddedTurrets.Sort((x, y) => x.name.CompareTo(y.name));
                            idx = moddedTurrets.IndexOf(prefab);
                        }

                        SetLobbyDataDedup(i + "turret_" + type, prefab == null ? "NULL" : isDefault + "," + idx);
                    }

                    SetLobbyDataDedup(i + "skin", InstantActionMaps.instance.skinDropdowns[i].value.ToString());
                }

                var enabledMutators = new List<int>();
                ModManager.instance.loadedMutators.Sort((x, y) => x.name.CompareTo(y.name));

                for (int i = 0; i < ModManager.instance.loadedMutators.Count; i++)
                {
                    var mutator = ModManager.instance.loadedMutators.ElementAt(i);

                    if (!GameManager.instance.gameInfo.activeMutators.Contains(mutator))
                        continue;

                    int id = i;

                    enabledMutators.Add(id);

                    var serializedMutators = new JSONArray();
                    foreach (var item in mutator.configuration.GetAllFields())
                    {
                        JSONNode node = new JSONString(item.SerializeValue());
                        serializedMutators.Add(node);
                    }

                    SetLobbyDataDedup(id + "config", serializedMutators.ToString());
                }
                SetLobbyDataDedup("mutators", string.Join(",", enabledMutators.ToArray()));
            }
            else if (SteamMatchmaking.GetLobbyMemberData(ActualLobbyID, SteamUser.GetSteamID(), "loaded") == "yes")
            {
                InstantActionMaps.instance.gameModeDropdown.value = int.Parse(SteamMatchmaking.GetLobbyData(ActualLobbyID, "gameMode"));
                InstantActionMaps.instance.nightToggle.isOn = bool.Parse(SteamMatchmaking.GetLobbyData(ActualLobbyID, "nightMode"));
                InstantActionMaps.instance.playerHasAllWeaponsToggle.isOn = bool.Parse(SteamMatchmaking.GetLobbyData(ActualLobbyID, "playerHasAllWeapons"));
                InstantActionMaps.instance.reverseToggle.isOn = bool.Parse(SteamMatchmaking.GetLobbyData(ActualLobbyID, "reverseMode"));
                InstantActionMaps.instance.configFlagsToggle.isOn = false;
                InstantActionMaps.instance.botNumberField.text = SteamMatchmaking.GetLobbyData(ActualLobbyID, "botNumberField");
                InstantActionMaps.instance.balanceSlider.value = float.Parse(SteamMatchmaking.GetLobbyData(ActualLobbyID, "balance"), CultureInfo.InvariantCulture);
                InstantActionMaps.instance.respawnTimeField.text = SteamMatchmaking.GetLobbyData(ActualLobbyID, "respawnTime");
                InstantActionMaps.instance.gameLengthDropdown.value = int.Parse(SteamMatchmaking.GetLobbyData(ActualLobbyID, "gameLength"));
                // For SpecOps.
                if (InstantActionMaps.instance.gameModeDropdown.value == 1)
                {
                    InstantActionMaps.instance.teamDropdown.value = int.Parse(SteamMatchmaking.GetLobbyData(ActualLobbyID, "team"));
                }

                bool doubleCheck = false; //fix for entering into the wrong map with midgame joining

                if (instance.LoadedServerMods)
                {
                    int givenEntry = int.Parse(SteamMatchmaking.GetLobbyData(ActualLobbyID, "loadedLevelEntry"));

                    if (givenEntry == customMapOptionIndex)
                    {
                        string mapName = SteamMatchmaking.GetLobbyData(ActualLobbyID, "customMap");
                        if(InstantActionMaps.instance.mapDropdown == null)
                        {
                            return;
                        }
                        if (InstantActionMaps.instance.mapDropdown.value != customMapOptionIndex || entries[customMapOptionIndex].metaData.displayName != mapName)
                        {
                            foreach (Transform item in InstantActionMaps.instance.customMapsBrowser.contentPanel)
                            {
                                var entry = item.gameObject.GetComponent<CustomMapEntry>();
                                if (entry.entry.metaData.displayName == mapName)
                                {
                                    entry.Select();
                                }
                            }
                            doubleCheck = true; //just to be safe
                        }
                    }
                    else
                    {
                        InstantActionMaps.instance.mapDropdown.value = givenEntry;
                    }
                }


                for (int i = 0; i < 2; i++)
                {
                    var teamInfo = GameManager.instance.gameInfo.team[i];

                    teamInfo.availableWeapons.Clear();
                    string[] weapons = SteamMatchmaking.GetLobbyData(ActualLobbyID, i + "weapons").Split(',');
                    foreach (string weapon_str in weapons)
                    {
                        if (weapon_str == string.Empty)
                            continue;
                        int hash = int.Parse(weapon_str);
                        var weapon = NetActorController.GetWeaponEntryByHash(hash);
                        teamInfo.availableWeapons.Add(weapon);
                    }

                    bool changedVehicles = false;
                    foreach (var vehicleType in (VehicleSpawner.VehicleSpawnType[])Enum.GetValues(typeof(VehicleSpawner.VehicleSpawnType)))
                    {
                        var type = vehicleType;
                        var prefab = teamInfo.vehiclePrefab[type];

                        var targetPrefab = SteamMatchmaking.GetLobbyData(ActualLobbyID, i + "vehicle_" + type);

                        GameObject newPrefab = null;
                        if (targetPrefab != "NULL")
                        {
                            string[] args = targetPrefab.Split(',');
                            bool isDefault = bool.Parse(args[0]);
                            int idx = int.Parse(args[1]);

                            if (isDefault)
                            {
                                newPrefab = ActorManager.instance.defaultVehiclePrefabs[idx];
                            }
                            else
                            {
                                newPrefab = sortedModdedVehicles[idx];
                            }
                        }

                        if (prefab != newPrefab)
                            changedVehicles = true;

                        teamInfo.vehiclePrefab[type] = newPrefab;
                    }

                    bool changedTurrets = false;
                    foreach (var turretType in (TurretSpawner.TurretSpawnType[])Enum.GetValues(typeof(TurretSpawner.TurretSpawnType)))
                    {
                        var type = turretType;
                        var prefab = teamInfo.turretPrefab[type];

                        var targetPrefab = SteamMatchmaking.GetLobbyData(ActualLobbyID, i + "turret_" + type);

                        GameObject newPrefab = null;
                        if (targetPrefab != "NULL")
                        {
                            string[] args = targetPrefab.Split(',');
                            bool isDefault = bool.Parse(args[0]);
                            int idx = int.Parse(args[1]);

                            if (isDefault)
                            {
                                newPrefab = ActorManager.instance.defaultTurretPrefabs[idx];
                            }
                            else
                            {
                                var moddedTurrets = ModManager.AllTurretPrefabs().ToList();
                                newPrefab = moddedTurrets[idx];
                            }
                        }

                        if (prefab != newPrefab)
                            changedTurrets = true;

                        teamInfo.turretPrefab[type] = newPrefab;
                    }

                    if (changedVehicles || changedTurrets)
                        GamePreview.UpdatePreview();

                    InstantActionMaps.instance.skinDropdowns[i].value = int.Parse(SteamMatchmaking.GetLobbyData(ActualLobbyID, i + "skin"));
                }

                string[] enabledMutators = SteamMatchmaking.GetLobbyData(LobbySystem.instance.ActualLobbyID, "mutators").Split(',');
                GameManager.instance.gameInfo.activeMutators.Clear();
                foreach (var mutatorStr in enabledMutators)
                {
                    if (mutatorStr == string.Empty)
                        continue;

                    int id = int.Parse(mutatorStr);

                    for (int mutatorIndex = 0; mutatorIndex < ModManager.instance.loadedMutators.Count; mutatorIndex++)
                    {
                        var mutator = ModManager.instance.loadedMutators.ElementAt(mutatorIndex);

                        if (id == mutatorIndex)
                        {
                            GameManager.instance.gameInfo.activeMutators.Add(mutator);

                            string configStr = SteamMatchmaking.GetLobbyData(LobbySystem.instance.ActualLobbyID, mutatorIndex + "config");

                            JSONArray jsonConfig = JSON.Parse(configStr).AsArray;
                            List<string> configList = new List<string>();

                            foreach (var configItem in jsonConfig)
                            {
                                configList.Add((string)configItem.Value);
                            }

                            string[] config = configList.ToArray();

                            for (int i = 0; i < mutator.configuration.GetAllFields().Count(); i++)
                            {
                                var item = mutator.configuration.GetAllFields().ElementAt(i);

                                if (item == null)
                                    continue;

                                if (item.SerializeValue() != "")
                                {
                                    item?.DeserializeValue(config[i]);
                                }
                            }
                        }
                    }
                }

                if (doubleCheck)
                    return;

                if (SteamMatchmaking.GetLobbyData(ActualLobbyID, "started") == "yes")
                {
                    StartAsClient();
                }
            }
        }

        private void OnGUI()
        {
            if (GameManager.instance == null || (GameManager.IsIngame() && LoadoutUi.instance != null && LoadoutUi.HasBeenClosed()))
                return;

            var menu_page = (int)typeof(MainMenu).GetField("page", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(MainMenu.instance);

            if (menu_page != MainMenu.PAGE_INSTANT_ACTION)
                return;

            var lobbyStyle = new GUIStyle(GUI.skin.box);
            lobbyStyle.normal.background = LobbyBackground;

            if (GameManager.IsInMainMenu() && NotificationText != string.Empty)
            {
                GUILayout.BeginArea(new Rect((Screen.width - 250f) / 2f, (Screen.height - 200f) / 2f, 250f, 200f), string.Empty);
                GUILayout.BeginVertical(lobbyStyle);

                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label("<color=red>RavenM Message:</color>");
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                GUILayout.Space(7f);

                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label(NotificationText);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                GUILayout.Space(15f);

                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("OK"))
                    NotificationText = string.Empty;
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                GUILayout.EndVertical();
                GUILayout.EndArea();
            }

            if (GameManager.IsInMainMenu() && IntentionToStart)
            {
                GUILayout.BeginArea(new Rect((Screen.width - 250f) / 2f, (Screen.height - 200f) / 2f, 250f, 200f), string.Empty);
                GUILayout.BeginVertical(lobbyStyle);

                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label("<color=red>RavenM WARNING:</color>");
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                GUILayout.Space(7f);

                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label("Starting the match before all members have loaded is experimental and may cause inconsistencies. Are you sure?");
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                GUILayout.Space(15f);

                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("<color=green>CONTINUE</color>"))
                {
                    HasCommittedToStart = true;
                    IntentionToStart = false;
                    InstantActionMaps.instance.StartGame();
                }
                if (GUILayout.Button("ABORT"))
                {
                    IntentionToStart = false;
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                GUILayout.EndVertical();
                GUILayout.EndArea();
            }

            if (!InLobby && GUIStack.Count != 0 && GameManager.IsInMainMenu())
            {
                GUILayout.BeginArea(new Rect(10f, 10f, 150f, 10000f), string.Empty);
                GUILayout.BeginVertical(lobbyStyle);

                if (GUIStack.Peek() == "Main")
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    GUILayout.Label($"RavenM");
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();

                    GUILayout.Space(15f);

                    if (GUILayout.Button("HOST"))
                        GUIStack.Push("Host");

                    GUILayout.Space(5f);

                    if (GUILayout.Button("JOIN"))
                        GUIStack.Push("Join");
                }
                else if (GUIStack.Peek() == "Host")
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    GUILayout.Label($"HOST");
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();

                    GUILayout.Space(5f);
                    GUILayout.Label($"LOBBY NAME");
                    GUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();

                    LobbyName = GUILayout.TextField(LobbyName, 20);
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();

                    GUILayout.Space(5f);

                    GUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    GUILayout.Label($"MEMBER LIMIT: ");
                    LobbyMemberCap = GUILayout.TextField(LobbyMemberCap);
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();

                    // Ensure we are working with a valid (positive) integer.
                    for (int i = LobbyMemberCap.Length - 1; i >= 0; i--)
                        if (LobbyMemberCap[i] < '0' || LobbyMemberCap[i] > '9')
                            LobbyMemberCap = LobbyMemberCap.Remove(i, 1);

                    // Trim to max 3 characters.
                    if (LobbyMemberCap.Length > 3)
                        LobbyMemberCap = LobbyMemberCap.Remove(3);

                    PrivateLobby = GUILayout.Toggle(PrivateLobby, "FRIENDS ONLY");

                    ShowOnList = GUILayout.Toggle(ShowOnList, "SHOW ON LOBBY\nLIST");

                    MidgameJoin = GUILayout.Toggle(MidgameJoin, "JOINABLE\nMIDGAME");


                    GUILayout.Space(7f);
                    GUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    GUILayout.Label($"NAMETAGS");
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();

                    nameTagsEnabled = GUILayout.Toggle(nameTagsEnabled, "ENABLED");

                    nameTagsForTeamOnly = GUILayout.Toggle(nameTagsForTeamOnly, "FOR TEAM ONLY");


                    GUILayout.Space(10f);

                    if (GUILayout.Button("START"))
                    {
                        // No friends?
                        if (LobbyMemberCap.Length == 0 || int.Parse(LobbyMemberCap) < 2)
                            LobbyMemberCap = "2";

                        // Maximum possible allowed by steam.
                        if (int.Parse(LobbyMemberCap) > 250)
                            LobbyMemberCap = "250";

                        SteamMatchmaking.CreateLobby(PrivateLobby ? ELobbyType.k_ELobbyTypeFriendsOnly : ELobbyType.k_ELobbyTypePublic, int.Parse(LobbyMemberCap));
                        InLobby = true;
                        IsLobbyOwner = true;
                        LobbyDataReady = false;
                    }

                    GUILayout.Space(3f);

                    if (GUILayout.Button("<color=#888888>BACK</color>"))
                        GUIStack.Pop();
                }
                else if (GUIStack.Peek() == "Join")
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    GUILayout.Label($"JOIN");
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();

                    GUILayout.Space(10f);

                    if (GUILayout.Button("BROWSE"))
                    {
                        OpenLobbies.Clear();
                        SteamMatchmaking.RequestLobbyList();
                        GUIStack.Push("Browse");
                    }

                    GUILayout.Space(5f);

                    if (GUILayout.Button("DIRECT CONNECT"))
                        GUIStack.Push("Direct");

                    GUILayout.Space(3f);

                    if (GUILayout.Button("<color=#888888>BACK</color>"))
                        GUIStack.Pop();
                }
                else if (GUIStack.Peek() == "Direct")
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    GUILayout.Label($"DIRECT CONNECT");
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();

                    GUILayout.Space(10f);

                    GUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    GUILayout.Label($"LOBBY ID");
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();

                    JoinLobbyID = GUILayout.TextField(JoinLobbyID);

                    GUILayout.Space(15f);

                    if (GUILayout.Button("START"))
                    {
                        if (uint.TryParse(JoinLobbyID, out uint idLong))
                        {
                            CSteamID lobbyId = new CSteamID(new AccountID_t(idLong), (uint)EChatSteamIDInstanceFlags.k_EChatInstanceFlagLobby | (uint)EChatSteamIDInstanceFlags.k_EChatInstanceFlagMMSLobby, EUniverse.k_EUniversePublic, EAccountType.k_EAccountTypeChat);
                            SteamMatchmaking.JoinLobby(lobbyId);
                            InLobby = true;
                            IsLobbyOwner = false;
                            LobbyDataReady = false;
                        }
                    }

                    GUILayout.Space(3f);

                    if (GUILayout.Button("<color=#888888>BACK</color>"))
                        GUIStack.Pop();
                }
                else if (GUIStack.Peek() == "Browse")
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    GUILayout.Label($"BROWSE");
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();

                    GUILayout.Space(10f);

                    if (GUILayout.Button("REFRESH"))
                    {
                        OpenLobbies.Clear();
                        SteamMatchmaking.RequestLobbyList();
                    }

                    GUILayout.Space(10f);

                    GUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    GUILayout.Label($"LOBBIES - ({OpenLobbies.Count})");
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();

                    foreach (var lobby in OpenLobbies)
                    {
                        var owner = SteamMatchmaking.GetLobbyData(lobby, "owner");

                        var lobbyname = SteamMatchmaking.GetLobbyData(lobby, "lobbyname");

                        var modList = SteamMatchmaking.GetLobbyData(lobby, "mods");

                        var modtotalsize = SteamMatchmaking.GetLobbyData(lobby, "modtotalsize");

                        bool hasData = false;

                        string name = "<color=#777777>Loading...</color>";
                        if (owner != string.Empty)
                        {
                            var ownerId = new CSteamID(ulong.Parse(owner));
                            hasData = !SteamFriends.RequestUserInformation(ownerId, true);
                            if (hasData)
                            {
                                name = SteamFriends.GetFriendPersonaName(ownerId);
                                if (name.Length > 20)
                                {
                                    name = name.Substring(0, 20) + "...";
                                }
                            }
                            if (!String.IsNullOrEmpty(lobbyname))
                            {
                                if (lobbyname.Length > 15)
                                {
                                    lobbyname = lobbyname.Substring(0, 15) + "...";
                                }
                                name += $" \n <b>" + lobbyname + "</b>"; // looks nice bold, but it might stretch too large for the ui box. limit the length a little more than the owner name
                            }
                            name += $" \n({SteamMatchmaking.GetNumLobbyMembers(lobby)}/{SteamMatchmaking.GetLobbyMemberLimit(lobby)})";
                            if (!String.IsNullOrEmpty(modtotalsize))
                            {
                                if (!String.IsNullOrEmpty(modList))
                                {
                                    var modCount = modList != string.Empty ? modList.Split(',').Length : 0;
                                    name += $" \n{modCount} mods | {modtotalsize}";
                                }
                                else
                                {
                                    name += $"{modtotalsize}";
                                }
                            }
                            
                        }

                        //making the button large might make the lobby browser list take too much space (shouldn't be too much of a problem). maybe make it a scrollable box?
                        if (GUILayout.Button($"{name}", GUILayout.Height(90)) && hasData) 
                        {
                            LobbyView = lobby;
                            GUIStack.Push("Lobby View");
                        }
                    }

                    GUILayout.Space(3f);

                    if (GUILayout.Button("<color=#888888>BACK</color>"))
                        GUIStack.Pop();
                }
                else if (GUIStack.Peek() == "Lobby View")
                {
                    var owner = new CSteamID(ulong.Parse(SteamMatchmaking.GetLobbyData(LobbyView, "owner")));
                    var name = SteamFriends.GetFriendPersonaName(owner);

                    GUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    GUILayout.Label($"{name}'s");
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    GUILayout.Label($"LOBBY");
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();

                    GUILayout.Space(10f);

                    if (GUILayout.Button("REFRESH"))
                    {
                        SteamMatchmaking.RequestLobbyData(LobbyView);
                    }

                    GUILayout.Space(10f);

                    GUILayout.Label($"MEMBERS: {SteamMatchmaking.GetNumLobbyMembers(LobbyView)}/{SteamMatchmaking.GetLobbyMemberLimit(LobbyView)}");

                    var modList = SteamMatchmaking.GetLobbyData(LobbyView, "mods");
                    var modCount = modList != string.Empty ? modList.Split(',').Length : 0;

                    var modSize = SteamMatchmaking.GetLobbyData(LobbyView, "modtotalsize");

                    GUILayout.Label($"MODS: {modCount} mods | {modSize}");

                    GUILayout.Label($"BOTS: {SteamMatchmaking.GetLobbyData(LobbyView, "botNumberField")}");

                    var map = SteamMatchmaking.GetLobbyData(LobbyView, "customMap");
                    map = map != string.Empty ? map : "Default";
                    GUILayout.Label($"MAP: {map}");

                    var status = SteamMatchmaking.GetLobbyData(LobbyView, "started") == "yes" ? "<color=green>In-game</color>" : "Configuring";
                    GUILayout.Label($"STATUS: {status}");

                    GUILayout.Space(10f);

                    if (Plugin.BuildGUID != SteamMatchmaking.GetLobbyData(LobbyView, "build_id"))
                    {
                        GUILayout.Label("<color=red>This lobby is running on a different version of RavenM!</color>");
                    }
                    else if (GUILayout.Button("JOIN"))
                    {
                        SteamMatchmaking.JoinLobby(LobbyView);
                        InLobby = true;
                        IsLobbyOwner = false;
                        LobbyDataReady = false;
                    }

                    GUILayout.Space(3f);

                    if (GUILayout.Button("<color=#888888>BACK</color>"))
                        GUIStack.Pop();
                }

                GUILayout.EndVertical();
                GUILayout.EndArea();
            }

            if (InLobby && LobbyDataReady)
            {
                if (!IngameNetManager.instance.IsClient)
                {
                    if (ChatManager.instance.SelectedChatPosition == 1) // Position to the right
                    {
                        ChatManager.instance.CreateChatArea(true, 300f, 400f, 570f, Screen.width - 310f);
                    }
                    else
                    {
                        ChatManager.instance.CreateChatArea(true, 300f, 400f, 570f);
                    }
                }

                GUILayout.BeginArea(new Rect(10f, 10f, 150f, 10000f), string.Empty);
                GUILayout.BeginVertical(lobbyStyle);

                var members = GetLobbyMembers();
                int len = members.Count;

                if (GameManager.IsInMainMenu() && GUILayout.Button("<color=red>LEAVE</color>"))
                {
                    SteamMatchmaking.LeaveLobby(ActualLobbyID);
                    LobbyDataReady = false;
                    InLobby = false;
                    ChatManager.instance.ResetChat();
                }

                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label($"LOBBY - {len}/{SteamMatchmaking.GetLobbyMemberLimit(ActualLobbyID)}");
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                GUILayout.Space(5f);

                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label(ActualLobbyID.GetAccountID().ToString());
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                if (GameManager.IsInMainMenu() && GUILayout.Button("COPY ID"))
                {
                    GUIUtility.systemCopyBuffer = ActualLobbyID.GetAccountID().ToString();
                }

                GUILayout.Space(5f);

                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label($"{SteamMatchmaking.GetLobbyData(ActualLobbyID, "lobbyname")}");
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();


                if (!IsLobbyOwner) //stuff doesn't work as the host, so we can hide it :)
                {
                    var modList = SteamMatchmaking.GetLobbyData(LobbyView, "mods");
                    var modCount = modList != string.Empty ? modList.Split(',').Length : 0;

                    var modSize = SteamMatchmaking.GetLobbyData(LobbyView, "modtotalsize");

                    GUILayout.Label($"MODS: {modCount} | {modSize}");
                }

                GUILayout.Space(10f);

                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label("MEMBERS:");
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                for (int i = 0; i < len; i++)
                {
                    var memberId = members[i];

                    string team = SteamMatchmaking.GetLobbyMemberData(ActualLobbyID, memberId, "team");
                    string name = SteamFriends.GetFriendPersonaName(memberId);

                    string modsDownloaded = SteamMatchmaking.GetLobbyMemberData(ActualLobbyID, memberId, "modsDownloaded");
                    // Can't use ServerMods.Count for the lobby owner.
                    string totalMods = SteamMatchmaking.GetLobbyData(ActualLobbyID, "mods").Split(',').Length.ToString();

                    var readyColor = (GameManager.IsInMainMenu() ? SteamMatchmaking.GetLobbyMemberData(ActualLobbyID, memberId, "loaded") == "yes"
                                                                    : SteamMatchmaking.GetLobbyMemberData(ActualLobbyID, memberId, "ready") == "yes")
                                                                    ? "green" : "red";

                    if (memberId != KickPrompt)
                    {
                        GUILayout.BeginHorizontal();
                        if (SteamMatchmaking.GetLobbyMemberData(ActualLobbyID, memberId, "loaded") == "yes")
                            GUILayout.Box(team);
                        else
                            GUILayout.Box($"({modsDownloaded}/{totalMods})");
                        GUILayout.FlexibleSpace();
                        GUILayout.Box($"<color={readyColor}>{name}</color>");
                        GUILayout.FlexibleSpace();
                        GUILayout.Box(team);
                        GUILayout.EndHorizontal();

                        if (Event.current.type == EventType.Repaint
                            && GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition)
                            && Input.GetMouseButtonUp(1)
                            && IsLobbyOwner
                            && memberId != SteamUser.GetSteamID())
                        {
                            KickPrompt = memberId;
                        }
                    }
                    else
                    {
                        if (GUILayout.Button($"<color=red>KICK {name}</color>"))
                        {
                            ChatManager.instance.SendLobbyChat($"/kick {memberId}");
                            CurrentKickedMembers.Add(memberId);
                            foreach (var connection in IngameNetManager.instance.ServerConnections)
                            {
                                if (SteamNetworkingSockets.GetConnectionInfo(connection, out SteamNetConnectionInfo_t pInfo) && pInfo.m_identityRemote.GetSteamID() == memberId)
                                {
                                    SteamNetworkingSockets.CloseConnection(connection, 0, null, false);
                                }
                            }
                        }

                        if (Event.current.type == EventType.Repaint
                            && !GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition)
                            && Input.GetMouseButtonDown(0) || Input.GetMouseButton(1))
                            KickPrompt = CSteamID.Nil;
                    }
                }

                GUILayout.EndVertical();
                GUILayout.EndArea();

            }

            if (ModsToDownload.Count > 0)
            {
                GUILayout.BeginArea(new Rect(160f, 10f, 150f, 10000f), string.Empty);
                GUILayout.BeginVertical(lobbyStyle);

                int hasDownloaded = ServerMods.Count - ModsToDownload.Count;

                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label("DOWNLOADING");
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label("MODS:");
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                GUILayout.Space(5f);

                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label($"{hasDownloaded}/{ServerMods.Count}");
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                if (SteamUGC.GetItemDownloadInfo(new PublishedFileId_t(ModsToDownload[0].m_PublishedFileId), out ulong punBytesDownloaded, out ulong punBytesTotal))
                {
                    GUILayout.Space(5f);

                    GUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    GUILayout.Label($"{Math.Round(punBytesDownloaded / Math.Pow(1024, 2), 2)}MB/{Math.Round(punBytesTotal / Math.Pow(1024, 2), 2)}MB");
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();

                    GUILayout.Space(5f);

                    GUIStyle progressStyle = new GUIStyle();
                    progressStyle.normal.background = ProgressTexture;

                    GUILayout.BeginHorizontal();
                    GUILayout.BeginVertical(progressStyle);
                    GUILayout.Box(ProgressTexture);
                    GUILayout.EndVertical();
                    GUILayout.Space((float)(punBytesTotal - punBytesDownloaded) / punBytesTotal * 150f);
                    GUILayout.EndHorizontal();
                }

                GUILayout.Space(15f);

                if (GUILayout.Button("<color=red>CANCEL</color>"))
                {
                    if (InLobby)
                    {
                        SteamMatchmaking.LeaveLobby(ActualLobbyID);
                        LobbyDataReady = false;
                        InLobby = false;
                    }
                    ModsToDownload.Clear();
                }

                GUILayout.EndVertical();
                GUILayout.EndArea();
            }
        }
    }
}