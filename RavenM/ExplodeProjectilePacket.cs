using System.IO;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace RavenM
{
    [HarmonyPatch(typeof(ExplodingProjectile), "Explode")]
    public class ProjectileExplodePatch
    {
        // TODO: Should we send over the exact position and up vector as well?
        static void Prefix(ExplodingProjectile __instance, Vector3 position, Vector3 up)
        {
            if (!IngameNetManager.instance.IsClient)
                return;

            var guidComponent = __instance.GetComponent<GuidComponent>();

            if (guidComponent == null)
                return;

            var id = guidComponent.guid;

            if (!IngameNetManager.instance.OwnedProjectiles.Contains(id))
                return;

            int sourceId = -1;
            if (__instance.killCredit != null && __instance.killCredit.TryGetComponent(out GuidComponent aguid))
                sourceId = aguid.guid;


            using MemoryStream memoryStream = new MemoryStream();
            var explodePacket = new ExplodeProjectilePacket
            {
                Id = id,
                SourceId = sourceId, //we can also store the actor responsible to 100% ensure credit
                Position = position, //some weapons spawn things at the impact location
                //if the thing spawned wasn't a vehicle, stuff might desync. 
            };

            using (var writer = new ProtocolWriter(memoryStream))
            {
                writer.Write(explodePacket);
            }

            byte[] data = memoryStream.ToArray();
            IngameNetManager.instance.SendPacketToServer(data, PacketType.Explode, Constants.k_nSteamNetworkingSend_Reliable);
        }
    }
    [HarmonyPatch(typeof(ActorManager), "Explode", new System.Type[] { typeof(ExplosionInfo), typeof(bool) })]
    public class FixExplosionDesyncPatch
    {
        static bool Prefix(ActorManager __instance, ref ExplosionInfo info, ref bool reduceFriendlyDamage, ref bool __result)
        {
            reduceFriendlyDamage = false;
            if (info.configuration.damageRange <= 0.3f && info.configuration.balanceRange <= 0.3f)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }
    [HarmonyPatch(typeof(ActorManager), "Explode", new System.Type[] { typeof(ExplosionInfo), typeof(bool) })]
    public class ExplosionCheckDistancePatch
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            bool replacedFirst = false;
            MethodInfo maxCall = typeof(Mathf).GetMethod(
                nameof(Mathf.Max), 
                BindingFlags.Static | BindingFlags.Public, 
                null, 
                CallingConventions.Any, 
                new System.Type[] { typeof(float[]) }, 
                null);
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Call && (MethodInfo)instruction.operand == maxCall && !replacedFirst)
                {
                    replacedFirst = true;
                    yield return new CodeInstruction(OpCodes.Call, typeof(ExplosionCheckDistancePatch).GetMethod(nameof(MaxPatch), BindingFlags.Static | BindingFlags.NonPublic));
                }
                else
                {
                    yield return instruction;
                }
            }
        }

        static float MaxPatch(float[] val)
        {
            return Mathf.Max(val[0], val[1]);
        }
    }

    public class ExplodeProjectilePacket
    {
        public int Id;

        public int SourceId;

        public Vector3 Position;
    }
}
