using HarmonyLib;
using HMLLibrary;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

public class DynamicOcean : Mod
{
    private static Harmony harmony;
    private static readonly List<PickupItem_Networked> trackedLoot = new List<PickupItem_Networked>();

    private static float windAngle;
    private static float targetWindAngle;
    private static float currentAngle;
    private static float targetCurrentAngle;
    private static float nextWindChange;
    private static float nextCurrentChange;
    private static float nextLootScan;

    private const float WindTurnSpeed = 0.65f;
    private const float CurrentTurnSpeed = 0.18f;
    private const float LootCurrentSpeed = 0.65f;
    private const float WindMinInterval = 240f;
    private const float WindMaxInterval = 720f;
    private const float CurrentMinInterval = 7200f;
    private const float CurrentMaxInterval = 14400f;

    private struct SpawnerState
    {
        public bool active;
        public Vector3 position;
        public Vector3 raftDirection;
    }

    public void Start()
    {
        harmony = new Harmony("el.dynamicocean");
        harmony.PatchAll(Assembly.GetExecutingAssembly());

        targetWindAngle = ChooseNewAngle(windAngle, 55f);
        targetCurrentAngle = ChooseNewAngle(currentAngle, 40f);
        ScheduleWind();
        ScheduleCurrent();

        PatchRaftWind();
        PatchLootSpawner();
        RegisterClearLootCommand();

        Debug.Log("[DynamicOcean] Loaded");
    }

    public void Update()
    {
        if (!Raft_Network.IsHost) return;

        windAngle = Mathf.MoveTowardsAngle(windAngle, targetWindAngle, WindTurnSpeed * Time.deltaTime);
        currentAngle = Mathf.MoveTowardsAngle(currentAngle, targetCurrentAngle, CurrentTurnSpeed * Time.deltaTime);

        if (Time.time >= nextWindChange)
        {
            targetWindAngle = ChooseNewAngle(windAngle, 55f);
            ScheduleWind();
        }

        if (Time.time >= nextCurrentChange)
        {
            targetCurrentAngle = ChooseNewAngle(currentAngle, 40f);
            ScheduleCurrent();
        }

        if (Time.time >= nextLootScan)
        {
            nextLootScan = Time.time + 0.5f;
            RefreshLoot();
        }
    }

    public void LateUpdate()
    {
        if (!Raft_Network.IsHost || trackedLoot.Count == 0) return;

        Vector3 movement = CurrentDirection * LootCurrentSpeed * Time.deltaTime;

        for (int i = trackedLoot.Count - 1; i >= 0; i--)
        {
            PickupItem_Networked pickup = trackedLoot[i];
            if (!IsOceanLoot(pickup))
            {
                trackedLoot.RemoveAt(i);
                continue;
            }

            pickup.transform.position += movement;
        }
    }

    public void OnModUnload()
    {
        if (harmony != null) harmony.UnpatchAll(harmony.Id);
        trackedLoot.Clear();
    }

    private static Vector3 WindDirection => DirectionFromAngle(windAngle);
    private static Vector3 CurrentDirection => DirectionFromAngle(currentAngle);

    private static void PatchRaftWind()
    {
        harmony.Patch(
            AccessTools.Method(typeof(Raft), "FixedUpdate"),
            transpiler: new HarmonyMethod(AccessTools.Method(typeof(DynamicOcean), nameof(RaftWindTranspiler)))
        );
    }

    private static void PatchLootSpawner()
    {
        MethodInfo update = AccessTools.Method(typeof(ObjectSpawner_RaftDirection), "Update");
        harmony.Patch(
            update,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(DynamicOcean), nameof(SpawnerPrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(DynamicOcean), nameof(SpawnerPostfix)))
        );
    }

    private static IEnumerable<CodeInstruction> RaftWindTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo forward = AccessTools.PropertyGetter(typeof(Vector3), nameof(Vector3.forward));
        MethodInfo replacement = AccessTools.PropertyGetter(typeof(DynamicOcean), nameof(WindDirection));

        foreach (CodeInstruction instruction in instructions)
        {
            if (!instruction.Calls(forward))
            {
                yield return instruction;
                continue;
            }

            CodeInstruction changed = new CodeInstruction(OpCodes.Call, replacement);
            changed.labels.AddRange(instruction.labels);
            changed.blocks.AddRange(instruction.blocks);
            yield return changed;
        }
    }

    private static void SpawnerPrefix(ObjectSpawner_RaftDirection __instance, ref SpawnerState __state)
    {
        if (!Raft_Network.IsHost || __instance == null) return;

        Network_Player player = GetReferencePlayer();
        if (player == null) return;

        __state.active = true;
        __state.position = __instance.transform.position;
        __state.raftDirection = Raft.direction;

        // Спавнер временно переносится к игроку
        Vector3 position = player.transform.position;
        position.y = __state.position.y;
        __instance.transform.position = position;

        // Лут создаётся выше по течению
        Raft.direction = -CurrentDirection;
    }

    private static void SpawnerPostfix(ObjectSpawner_RaftDirection __instance, SpawnerState __state)
    {
        if (!__state.active || __instance == null) return;

        __instance.transform.position = __state.position;
        Raft.direction = __state.raftDirection;
    }

    private static void RefreshLoot()
    {
        trackedLoot.Clear();
        PickupItem_Networked[] pickups = UnityEngine.Object.FindObjectsOfType<PickupItem_Networked>();

        for (int i = 0; i < pickups.Length; i++)
        {
            if (IsOceanLoot(pickups[i])) trackedLoot.Add(pickups[i]);
        }
    }

    private static bool IsOceanLoot(PickupItem_Networked pickup)
    {
        if (pickup == null || !pickup.gameObject.activeInHierarchy) return false;
        if (!(pickup.spawner is ObjectSpawner_RaftDirection)) return false;

        PickupItem item = pickup.PickupItem;
        if (item != null && item.isDropped) return false;

        Transform parent = pickup.transform.parent;
        if (parent == null) return true;

        string name = parent.name ?? string.Empty;
        return name.IndexOf("collector", StringComparison.OrdinalIgnoreCase) < 0 &&
               name.IndexOf("net", StringComparison.OrdinalIgnoreCase) < 0;
    }

    private static Network_Player GetReferencePlayer()
    {
        Raft_Network network = ComponentManager<Raft_Network>.Value;
        Network_Player local = network != null ? network.GetLocalPlayer() : null;
        return local != null ? local : UnityEngine.Object.FindObjectOfType<Network_Player>();
    }

    private static Vector3 DirectionFromAngle(float angle)
    {
        return Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
    }

    private static float ChooseNewAngle(float current, float minDifference)
    {
        for (int i = 0; i < 12; i++)
        {
            float angle = UnityEngine.Random.Range(0f, 360f);
            if (Mathf.Abs(Mathf.DeltaAngle(current, angle)) >= minDifference) return angle;
        }

        return current + 90f;
    }

    private static void ScheduleWind()
    {
        nextWindChange = Time.time + UnityEngine.Random.Range(WindMinInterval, WindMaxInterval);
    }

    private static void ScheduleCurrent()
    {
        nextCurrentChange = Time.time + UnityEngine.Random.Range(CurrentMinInterval, CurrentMaxInterval);
    }

    private static void RegisterClearLootCommand()
    {
        try
        {
            Type console = AccessTools.TypeByName("RConsole");
            if (console == null) return;

            MethodInfo register = AccessTools.Method(
                console,
                "registerCommand",
                new[] { typeof(Type), typeof(string), typeof(string), typeof(Action) }
            );

            if (register == null) return;

            register.Invoke(null, new object[]
            {
                typeof(DynamicOcean),
                "Удаляет свободно плавающий океанский лут",
                "clearloot",
                (Action)ClearLoot
            });
        }
        catch (Exception e)
        {
            Debug.LogWarning("[DynamicOcean] clearloot: " + e.Message);
        }
    }

    private static void ClearLoot()
    {
        if (!Raft_Network.IsHost) return;

        PickupItem_Networked[] pickups = UnityEngine.Object.FindObjectsOfType<PickupItem_Networked>();
        int removed = 0;

        for (int i = 0; i < pickups.Length; i++)
        {
            PickupItem_Networked pickup = pickups[i];
            if (!IsOceanLoot(pickup)) continue;

            PickupObjectManager.RemovePickupItemNetwork(pickup);
            removed++;
        }

        trackedLoot.Clear();
        Debug.Log("[DynamicOcean] Removed ocean loot: " + removed);
    }
}
