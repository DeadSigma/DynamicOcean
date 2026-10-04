using HarmonyLib;
using HMLLibrary;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using UnityEngine;
using UltimateWater;

public class DynamicOcean : Mod
{
    private static Harmony harmony;

    private static readonly List<IslandFlow> islandFlows =
        new List<IslandFlow>();

    private static readonly List<PickupItem_Networked> trackedLoot =
        new List<PickupItem_Networked>();

    private static readonly List<PickupItem_Networked> oceanLoot =
        new List<PickupItem_Networked>();

    private static readonly Dictionary<int, float> nextNetworkSync =
        new Dictionary<int, float>();

    private static readonly Dictionary<Type, DirtyHook> dirtyHooks =
        new Dictionary<Type, DirtyHook>();

    private static readonly HashSet<Type> syncWarnings =
        new HashSet<Type>();

    private static ObjectSpawnerManager cachedSpawnerManager;
    private static ObjectSpawner_RaftDirection cachedPlankSpawner;
    private static ObjectSpawner_RaftDirection cachedItemSpawner;

    private static float oceanAngle;
    private static float nextIslandScan;
    private static float nextOceanScan;
    private static float nextCleanup;
    private static float nextWaterLookup;

    private static Water cachedWater;
    private static Transform cachedWindDirectionPointer;
    private static int oceanScheduleSeed = 1337;

    private static bool oceanScheduleInitialized;
    private static int oceanScheduleSegment;
    private static int oceanScheduleSeedApplied;
    private static float oceanBaseAngle;
    private static float oceanTargetAngle;
    private static float oceanHoldEnd;
    private static float oceanTransitionEnd;
    private static float oceanLastClock;
    private static bool oceanTransitionLogged;

    private const float LootCurrentSpeed = 0.65f;

    private const float OceanDayDuration = 1200f;
    private const float OceanMinInterval = OceanDayDuration;
    private const int OceanDefaultMaxIntervalDays = 3;
    private const float OceanTransitionDuration = 60f;
    private const float OceanMinDirectionChange = 40f;
    private const float OceanMaxDirectionChange = 140f;

    private const float IslandActivationRadius = 90f;
    private const float UpstreamGap = 24f;
    private const float UpstreamScatter = 18f;
    private const float SidePadding = 10f;
    private const float SmallIslandLineScale = 0.80f;
    private const float SmallIslandWidthThreshold = 80f;
    private const float CleanupPadding = 120f;
    private const float MinIslandLootLifetime = 150f;
    private const float OrphanLootRemoveDistance = 320f;
    private const float MaxIslandExtent = 220f;
    private const float NetworkSyncInterval = 0.15f;

    private const float PlankChance = 0.32f;

    private static bool ExtraSettingsAPI_Loaded;
    private static float islandLootSpawnRate = 1f;
    private static int oceanMaxIntervalDays = OceanDefaultMaxIntervalDays;

    private const string SettingIslandLootSpawnRate =
        "Island Loot Spawn Rate";

    private const string SettingOceanDirectionChangeInterval =
        "Ocean Direction Change Interval";

    private sealed class IslandFlow
    {
        public Landmark landmark;
        public Collider[] colliders;
        public int id;
        public float nextSpawn;
        public float minAlong;
        public float maxAlong;
        public float minSide;
        public float maxSide;
        public float width;
        public bool hasPlayer;
    }

    private sealed class DirtyHook
    {
        public MethodInfo method;
        public bool methodTakesBool;
        public PropertyInfo property;
        public FieldInfo field;
        public bool supported;
    }

    public void Start()
    {
        harmony = new Harmony("el.dynamicocean");
        harmony.PatchAll(Assembly.GetExecutingAssembly());

        RefreshWaterReference(true);
        UpdateOceanDirection();

        PatchRaftDirection();
        RefreshExtraSettings();

        Debug.Log("[DynamicOcean] Loaded");
    }

    public void Update()
    {
        UpdateOceanDirection();
        ApplyOceanDirectionToWater();

        if (!Raft_Network.IsHost)
            return;

        if (Time.time >= nextIslandScan)
        {
            nextIslandScan = Time.time + 2f;
            RefreshIslandFlows();
        }

        if (Time.time >= nextOceanScan)
        {
            nextOceanScan = Time.time + 0.5f;
            RefreshOceanLoot();
        }

        UpdateIslandFlows();

        if (Time.time >= nextCleanup)
        {
            nextCleanup = Time.time + 1f;
            CleanupIslandLoot();
        }
    }

    public void LateUpdate()
    {
        if (!Raft_Network.IsHost || oceanLoot.Count == 0)
            return;

        MoveOceanLoot();
    }

    public void OnModUnload()
    {
        if (harmony != null)
            harmony.UnpatchAll(harmony.Id);

        islandFlows.Clear();
        trackedLoot.Clear();
        oceanLoot.Clear();
        nextNetworkSync.Clear();
        dirtyHooks.Clear();
        syncWarnings.Clear();

        cachedSpawnerManager = null;
        cachedPlankSpawner = null;
        cachedItemSpawner = null;

        cachedWater = null;
        cachedWindDirectionPointer = null;
        nextWaterLookup = 0f;

        oceanScheduleInitialized = false;
        oceanScheduleSegment = 0;
        oceanScheduleSeedApplied = 0;
        oceanBaseAngle = 0f;
        oceanTargetAngle = 0f;
        oceanHoldEnd = 0f;
        oceanTransitionEnd = 0f;
        oceanLastClock = 0f;
        oceanTransitionLogged = false;
    }

    internal static Vector3 OceanDirection
    {
        get
        {
            Vector3 direction =
                DirectionFromAngle(
                    oceanAngle
                );

            direction.y = 0f;

            if (direction.sqrMagnitude <= 0.0001f)
                return Vector3.forward;

            return direction.normalized;
        }
    }

    private static Vector3 CurrentDirection
    {
        get { return OceanDirection; }
    }

    private static void PatchRaftDirection()
    {
        harmony.Patch(
            AccessTools.Method(typeof(Raft), "FixedUpdate"),
            transpiler: new HarmonyMethod(
                AccessTools.Method(
                    typeof(DynamicOcean),
                    nameof(RaftDirectionTranspiler)
                )
            )
        );
    }

    private static IEnumerable<CodeInstruction> RaftDirectionTranspiler(
        IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo forward =
            AccessTools.PropertyGetter(
                typeof(Vector3),
                nameof(Vector3.forward)
            );

        MethodInfo replacement =
            AccessTools.PropertyGetter(
                typeof(DynamicOcean),
                nameof(OceanDirection)
            );

        foreach (CodeInstruction instruction in instructions)
        {
            if (!instruction.Calls(forward))
            {
                yield return instruction;
                continue;
            }

            CodeInstruction changed =
                new CodeInstruction(
                    OpCodes.Call,
                    replacement
                );

            changed.labels.AddRange(instruction.labels);
            changed.blocks.AddRange(instruction.blocks);

            yield return changed;
        }
    }

    private static void RefreshIslandFlows()
    {
        Landmark[] landmarks =
            UnityEngine.Object.FindObjectsOfType<Landmark>();

        HashSet<int> alive =
            new HashSet<int>();

        for (int i = 0; i < landmarks.Length; i++)
        {
            Landmark landmark =
                landmarks[i];

            if (landmark == null ||
                !landmark.isSpawned ||
                !landmark.gameObject.activeInHierarchy)
            {
                continue;
            }

            int id =
                landmark.GetInstanceID();

            alive.Add(id);

            IslandFlow flow =
                FindFlow(id);

            if (flow == null)
            {
                flow =
                    new IslandFlow
                    {
                        landmark = landmark,
                        id = id
                    };

                islandFlows.Add(flow);
            }

            flow.landmark =
                landmark;

            flow.colliders =
                landmark.GetComponentsInChildren<Collider>(
                    false
                );

            bool wasActive =
                flow.hasPlayer;

            flow.hasPlayer =
                false;

            if (wasActive)
                flow.nextSpawn =
                    Mathf.Min(
                        flow.nextSpawn,
                        Time.time + 1f
                    );
        }

        for (int i = islandFlows.Count - 1; i >= 0; i--)
        {
            IslandFlow flow =
                islandFlows[i];

            if (flow == null ||
                flow.landmark == null ||
                !alive.Contains(flow.id))
            {
                islandFlows.RemoveAt(i);
            }
        }

        Network_Player[] players =
            UnityEngine.Object.FindObjectsOfType<Network_Player>();

        float maxDistanceSqr =
            IslandActivationRadius *
            IslandActivationRadius;

        for (int i = 0; i < players.Length; i++)
        {
            Network_Player player =
                players[i];

            if (player == null ||
                !player.gameObject.activeInHierarchy)
            {
                continue;
            }

            IslandFlow closest =
                null;

            float closestDistance =
                maxDistanceSqr;

            for (int j = 0; j < islandFlows.Count; j++)
            {
                IslandFlow flow =
                    islandFlows[j];

                float distance =
                    GetDistanceToFlowSqr(
                        flow,
                        player.transform.position
                    );

                if (distance > closestDistance)
                    continue;

                closestDistance =
                    distance;

                closest =
                    flow;
            }

            if (closest != null)
            {
                if (!closest.hasPlayer)
                {
                    closest.nextSpawn =
                        Mathf.Min(
                            closest.nextSpawn,
                            Time.time + 0.15f
                        );
                }

                closest.hasPlayer =
                    true;
            }
        }
    }

    private static IslandFlow FindFlow(int id)
    {
        for (int i = 0; i < islandFlows.Count; i++)
        {
            IslandFlow flow =
                islandFlows[i];

            if (flow != null && flow.id == id)
                return flow;
        }

        return null;
    }

    private static float GetDistanceToFlowSqr(
        IslandFlow flow,
        Vector3 position)
    {
        if (flow == null ||
            flow.landmark == null)
        {
            return float.MaxValue;
        }

        Vector3 origin =
            flow.landmark.transform.position;

        float coarseRadius =
            IslandActivationRadius +
            MaxIslandExtent;

        if (DistanceXZSqr(
                position,
                origin) >
            coarseRadius *
            coarseRadius)
        {
            return float.MaxValue;
        }

        bool hadCollider =
            false;

        float best =
            float.MaxValue;

        if (flow.colliders != null)
        {
            for (int i = 0; i < flow.colliders.Length; i++)
            {
                Collider collider =
                    flow.colliders[i];

                if (!IsUsefulIslandCollider(
                    flow.landmark,
                    collider))
                {
                    continue;
                }

                Bounds bounds =
                    collider.bounds;

                float sizeXZ =
                    Mathf.Max(
                        bounds.size.x,
                        bounds.size.z
                    );

                if (sizeXZ >
                    MaxIslandExtent * 2f)
                {
                    continue;
                }

                if (DistanceXZSqr(
                        bounds.center,
                        origin) >
                    MaxIslandExtent *
                    MaxIslandExtent)
                {
                    continue;
                }

                hadCollider =
                    true;

                Vector3 nearest =
                    collider.ClosestPoint(
                        position
                    );

                best =
                    Mathf.Min(
                        best,
                        DistanceXZSqr(
                            position,
                            nearest
                        )
                    );
            }
        }

        if (hadCollider)
            return best;

        return DistanceXZSqr(
            position,
            origin
        );
    }

    private static ObjectSpawnerManager ResolveSpawnerManager()
    {
        ObjectSpawnerManager manager =
            ComponentManager<ObjectSpawnerManager>.Value;

        if (manager == null)
        {
            manager =
                UnityEngine.Object.FindObjectOfType<
                    ObjectSpawnerManager>();
        }

        if (manager == null)
        {
            ObjectSpawnerManager[] managers =
                Resources.FindObjectsOfTypeAll<
                    ObjectSpawnerManager>();

            for (int i = 0; i < managers.Length; i++)
            {
                ObjectSpawnerManager candidate =
                    managers[i];

                if (candidate == null ||
                    candidate.gameObject == null)
                {
                    continue;
                }

                if (!candidate.gameObject.scene.IsValid())
                    continue;

                manager =
                    candidate;

                break;
            }
        }

        if (manager != null)
        {
            cachedSpawnerManager =
                manager;

            if (manager.plankSpawner != null)
                cachedPlankSpawner =
                    manager.plankSpawner;

            if (manager.itemSpawner != null)
                cachedItemSpawner =
                    manager.itemSpawner;
        }
        else if (cachedSpawnerManager != null)
        {
            manager =
                cachedSpawnerManager;
        }

        return manager;
    }

    private static void UpdateIslandFlows()
    {
        ObjectSpawnerManager manager =
            ResolveSpawnerManager();

        if (manager == null)
            return;

        ObjectSpawner_RaftDirection plankSpawner =
            cachedPlankSpawner;

        ObjectSpawner_RaftDirection itemSpawner =
            cachedItemSpawner;

        if (plankSpawner == null &&
            itemSpawner == null)
        {
            return;
        }

        Vector3 direction =
            CurrentDirection;

        Vector3 side =
            Vector3.Cross(
                direction,
                Vector3.up
            ).normalized;

        for (int i = 0; i < islandFlows.Count; i++)
        {
            IslandFlow flow =
                islandFlows[i];

            if (flow == null ||
                !flow.hasPlayer ||
                flow.landmark == null)
            {
                continue;
            }

            if (!CalculateFlowBounds(
                flow,
                direction,
                side))
            {
                continue;
            }

            if (Time.time < flow.nextSpawn)
                continue;

            if (islandLootSpawnRate <= 0.001f)
            {
                flow.nextSpawn =
                    Time.time + 0.5f;

                continue;
            }

            int maxLoot =
                Mathf.CeilToInt(
                    GetMaxLootForWidth(
                        flow.width
                    ) *
                    Mathf.Max(
                        1f,
                        islandLootSpawnRate
                    )
                );

            if (CountIslandLoot(flow.id) >= maxLoot)
            {
                flow.nextSpawn =
                    Time.time + 0.75f;

                continue;
            }

            SpawnRandomFlowItem(
                flow,
                plankSpawner,
                itemSpawner,
                direction,
                side
            );

            float interval =
                GetSpawnInterval(
                    flow.width
                );

            flow.nextSpawn =
                Time.time +
                UnityEngine.Random.Range(
                    interval * 0.72f,
                    interval * 1.28f
                );
        }
    }

    private static bool CalculateFlowBounds(
        IslandFlow flow,
        Vector3 direction,
        Vector3 side)
    {
        bool found = false;

        float minAlong =
            float.MaxValue;

        float maxAlong =
            float.MinValue;

        float minSide =
            float.MaxValue;

        float maxSide =
            float.MinValue;

        Vector3 origin =
            flow.landmark.transform.position;

        if (flow.colliders != null)
        {
            for (int i = 0; i < flow.colliders.Length; i++)
            {
                Collider collider =
                    flow.colliders[i];

                if (!IsUsefulIslandCollider(
                    flow.landmark,
                    collider))
                {
                    continue;
                }

                Bounds bounds =
                    collider.bounds;

                Vector3 extents =
                    bounds.extents;

                Vector3 center =
                    bounds.center;

                float sizeXZ =
                    Mathf.Max(
                        bounds.size.x,
                        bounds.size.z
                    );

                if (sizeXZ > MaxIslandExtent * 2f)
                    continue;

                for (int x = -1; x <= 1; x += 2)
                {
                    for (int z = -1; z <= 1; z += 2)
                    {
                        Vector3 point =
                            new Vector3(
                                center.x + extents.x * x,
                                origin.y,
                                center.z + extents.z * z
                            );

                        Vector3 delta =
                            point - origin;

                        float distanceXZ =
                            new Vector2(
                                delta.x,
                                delta.z
                            ).magnitude;

                        if (distanceXZ > MaxIslandExtent)
                            continue;

                        Vector3 relative =
                            point - origin;

                        float along =
                            Vector3.Dot(
                                relative,
                                direction
                            );

                        float lateral =
                            Vector3.Dot(
                                relative,
                                side
                            );

                        minAlong =
                            Mathf.Min(
                                minAlong,
                                along
                            );

                        maxAlong =
                            Mathf.Max(
                                maxAlong,
                                along
                            );

                        minSide =
                            Mathf.Min(
                                minSide,
                                lateral
                            );

                        maxSide =
                            Mathf.Max(
                                maxSide,
                                lateral
                            );

                        found = true;
                    }
                }
            }
        }

        if (!found)
        {
            minAlong =
                -15f;

            maxAlong =
                15f;

            minSide =
                -15f;

            maxSide =
                15f;
        }

        flow.minAlong =
            minAlong;

        flow.maxAlong =
            maxAlong;

        flow.minSide =
            minSide - SidePadding;

        flow.maxSide =
            maxSide + SidePadding;

        float lineWidth =
            flow.maxSide - flow.minSide;

        if (lineWidth < SmallIslandWidthThreshold)
        {
            float centerSide =
                (flow.minSide + flow.maxSide) * 0.5f;

            float halfWidth =
                lineWidth *
                SmallIslandLineScale *
                0.5f;

            flow.minSide =
                centerSide - halfWidth;

            flow.maxSide =
                centerSide + halfWidth;
        }

        flow.width =
            Mathf.Max(
                20f,
                flow.maxSide - flow.minSide
            );

        return true;
    }

    private static bool IsUsefulIslandCollider(
        Landmark landmark,
        Collider collider)
    {
        if (landmark == null ||
            collider == null ||
            !collider.enabled ||
            collider.isTrigger)
        {
            return false;
        }

        if (collider.GetComponentInParent<Network_Player>() != null)
            return false;

        if (collider.GetComponentInParent<Raft>() != null)
            return false;

        string name =
            collider.name ?? string.Empty;

        if (name.IndexOf(
                "trigger",
                StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf(
                "kill",
                StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return false;
        }

        return true;
    }

    private static float GetSpawnInterval(
        float width)
    {
        float t =
            Mathf.InverseLerp(
                25f,
                180f,
                width
            );

        float baseInterval =
            Mathf.Lerp(
                3.5f,
                2.6f,
                t
            );

        if (islandLootSpawnRate <= 0.001f)
            return 9999f;

        return Mathf.Max(
            0.05f,
            baseInterval /
            islandLootSpawnRate
        );
    }

    private static int GetMaxLootForWidth(
        float width)
    {
        return Mathf.Clamp(
            Mathf.RoundToInt(
                90f + width * 0.40f
            ),
            100,
            170
        );
    }

    private static void SpawnRandomFlowItem(
        IslandFlow flow,
        ObjectSpawner_RaftDirection plankSpawner,
        ObjectSpawner_RaftDirection itemSpawner,
        Vector3 direction,
        Vector3 side)
    {
        float upstream =
            flow.minAlong -
            UpstreamGap -
            UnityEngine.Random.Range(
                0f,
                UpstreamScatter
            );

        float lateral =
            UnityEngine.Random.Range(
                flow.minSide,
                flow.maxSide
            );

        Vector3 position =
            flow.landmark.transform.position +
            direction * upstream +
            side * lateral;

        position.y =
            GetWaterY(
                plankSpawner,
                itemSpawner,
                flow.landmark
            );

        SpawnFlowItem(
            flow,
            plankSpawner,
            itemSpawner,
            position
        );
    }

    private static void SpawnFlowItem(
        IslandFlow flow,
        ObjectSpawner_RaftDirection plankSpawner,
        ObjectSpawner_RaftDirection itemSpawner,
        Vector3 position)
    {
        ObjectSpawner_RaftDirection spawner =
            ChooseLootSpawner(
                plankSpawner,
                itemSpawner
            );

        if (spawner == null)
            return;

        PickupItem_Networked pickup =
            TrySpawnNetworked(
                spawner,
                position
            );

        if (pickup == null)
        {
            ObjectSpawner_RaftDirection fallback =
                spawner == itemSpawner
                    ? plankSpawner
                    : itemSpawner;

            if (fallback != null)
            {
                pickup =
                    TrySpawnNetworked(
                        fallback,
                        position
                    );
            }
        }

        if (pickup == null)
            return;

        DynamicOceanLootTag tag =
            pickup.GetComponent<
                DynamicOceanLootTag>();

        if (tag == null)
        {
            tag =
                pickup.gameObject.AddComponent<
                    DynamicOceanLootTag>();
        }

        tag.Active = true;
        tag.IslandId = flow.id;
        tag.SpawnTime = Time.time;

        if (!trackedLoot.Contains(pickup))
            trackedLoot.Add(pickup);

        if (!oceanLoot.Contains(pickup))
            oceanLoot.Add(pickup);
    }

    private static PickupItem_Networked TrySpawnNetworked(
        ObjectSpawner_RaftDirection spawner,
        Vector3 position)
    {
        if (spawner == null)
            return null;

        for (int attempt = 0; attempt < 4; attempt++)
        {
            SpawnableFloatingObject spawnable =
                spawner.GetObjectToSpawn(true);

            if (spawnable == null)
                continue;

            PickupItem_Networked pickup =
                spawner.SpawnItemNetwork(
                    position,
                    spawnable
                );

            if (pickup != null)
                return pickup;
        }

        return null;
    }

    private static ObjectSpawner_RaftDirection
        ChooseLootSpawner(
            ObjectSpawner_RaftDirection plankSpawner,
            ObjectSpawner_RaftDirection itemSpawner)
    {
        if (plankSpawner == null)
            return itemSpawner;

        if (itemSpawner == null)
            return plankSpawner;

        return UnityEngine.Random.value <
               PlankChance
            ? plankSpawner
            : itemSpawner;
    }

    private static float GetWaterY(
        ObjectSpawner_RaftDirection plankSpawner,
        ObjectSpawner_RaftDirection itemSpawner,
        Landmark landmark)
    {
        if (itemSpawner != null)
            return itemSpawner.transform.position.y;

        if (plankSpawner != null)
            return plankSpawner.transform.position.y;

        return landmark != null
            ? landmark.transform.position.y
            : 0f;
    }

    private static void RefreshOceanLoot()
    {
        oceanLoot.Clear();

        PickupItem_Networked[] pickups =
            UnityEngine.Object.FindObjectsOfType<
                PickupItem_Networked>();

        for (int i = 0; i < pickups.Length; i++)
        {
            PickupItem_Networked pickup =
                pickups[i];

            if (IsOceanLoot(pickup))
                oceanLoot.Add(pickup);
        }
    }

    private static bool IsOceanLoot(
        PickupItem_Networked pickup)
    {
        if (pickup == null ||
            !pickup.gameObject.activeInHierarchy ||
            !(pickup.spawner is ObjectSpawner_RaftDirection))
        {
            return false;
        }

        PickupItem item =
            pickup.PickupItem;

        if (item != null &&
            item.isDropped)
        {
            return false;
        }

        Transform parent =
            pickup.transform.parent;

        if (parent == null)
            return true;

        string name =
            parent.name ?? string.Empty;

        return name.IndexOf(
                   "collector",
                   StringComparison.OrdinalIgnoreCase) < 0 &&
               name.IndexOf(
                   "net",
                   StringComparison.OrdinalIgnoreCase) < 0;
    }

    private static void MoveOceanLoot()
    {
        Vector3 movement =
            CurrentDirection *
            LootCurrentSpeed *
            Time.deltaTime;

        bool multiplayer =
            IsMultiplayer();

        for (int i = oceanLoot.Count - 1; i >= 0; i--)
        {
            PickupItem_Networked pickup =
                oceanLoot[i];

            if (!IsOceanLoot(pickup))
            {
                oceanLoot.RemoveAt(i);
                continue;
            }

            if (multiplayer &&
                !CanSyncNetworkMovement(pickup))
            {
                continue;
            }

            Vector3 position =
                pickup.transform.position;

            position.x += movement.x;
            position.z += movement.z;

            pickup.transform.position =
                position;

            if (multiplayer)
                SyncNetworkMovement(pickup);
        }
    }

    private static bool CanSyncNetworkMovement(
        PickupItem_Networked pickup)
    {
        if (pickup == null ||
            pickup.networkBehaviour == null)
        {
            return false;
        }

        Type type =
            pickup.networkBehaviour.GetType();

        DirtyHook hook =
            GetDirtyHook(type);

        if (hook.supported)
            return true;

        if (syncWarnings.Add(type))
        {
            Debug.LogWarning(
                "[DynamicOcean] Сетевое движение отключено для " +
                type.Name +
                " - не найден способ отправить обновление"
            );
        }

        return false;
    }

    private static void SyncNetworkMovement(
        PickupItem_Networked pickup)
    {
        int id =
            pickup.GetInstanceID();

        float next;

        if (nextNetworkSync.TryGetValue(
                id,
                out next) &&
            Time.time < next)
        {
            return;
        }

        nextNetworkSync[id] =
            Time.time +
            NetworkSyncInterval;

        DirtyHook hook =
            GetDirtyHook(
                pickup.networkBehaviour.GetType()
            );

        try
        {
            if (hook.method != null)
            {
                if (hook.methodTakesBool)
                {
                    hook.method.Invoke(
                        pickup.networkBehaviour,
                        new object[] { true }
                    );
                }
                else
                {
                    hook.method.Invoke(
                        pickup.networkBehaviour,
                        null
                    );
                }

                return;
            }

            if (hook.property != null)
            {
                hook.property.SetValue(
                    pickup.networkBehaviour,
                    true,
                    null
                );

                return;
            }

            if (hook.field != null)
            {
                hook.field.SetValue(
                    pickup.networkBehaviour,
                    true
                );
            }
        }
        catch (Exception e)
        {
            hook.supported = false;

            Debug.LogWarning(
                "[DynamicOcean] Не удалось синхронизировать движение лута: " +
                e.Message
            );
        }
    }

    private static DirtyHook GetDirtyHook(
        Type type)
    {
        DirtyHook hook;

        if (dirtyHooks.TryGetValue(
                type,
                out hook))
        {
            return hook;
        }

        hook =
            new DirtyHook();

        MethodInfo serialize =
            AccessTools.Method(
                type,
                "Serialize_Update"
            );

        if (serialize == null ||
            serialize.DeclaringType ==
            typeof(MonoBehaviour_Network))
        {
            dirtyHooks[type] = hook;
            return hook;
        }

        hook.method =
            AccessTools.Method(
                type,
                "SetDirty",
                Type.EmptyTypes
            ) ??
            AccessTools.Method(
                type,
                "MarkDirty",
                Type.EmptyTypes
            );

        if (hook.method == null)
        {
            hook.method =
                AccessTools.Method(
                    type,
                    "SetDirty",
                    new[] { typeof(bool) }
                ) ??
                AccessTools.Method(
                    type,
                    "MarkDirty",
                    new[] { typeof(bool) }
                );

            hook.methodTakesBool =
                hook.method != null;
        }

        if (hook.method == null)
        {
            PropertyInfo property =
                AccessTools.Property(
                    type,
                    "IsDirty"
                );

            if (property != null &&
                property.CanWrite &&
                property.PropertyType ==
                typeof(bool))
            {
                hook.property =
                    property;
            }
        }

        if (hook.method == null &&
            hook.property == null)
        {
            FieldInfo field =
                AccessTools.Field(
                    type,
                    "isDirty"
                ) ??
                AccessTools.Field(
                    type,
                    "_isDirty"
                ) ??
                AccessTools.Field(
                    type,
                    "dirty"
                );

            if (field != null &&
                field.FieldType ==
                typeof(bool))
            {
                hook.field =
                    field;
            }
        }

        hook.supported =
            hook.method != null ||
            hook.property != null ||
            hook.field != null;

        dirtyHooks[type] = hook;

        return hook;
    }

    private static void CleanupIslandLoot()
    {
        Vector3 direction =
            CurrentDirection;

        for (int i = trackedLoot.Count - 1; i >= 0; i--)
        {
            PickupItem_Networked pickup =
                trackedLoot[i];

            DynamicOceanLootTag tag =
                GetActiveTag(pickup);

            if (tag == null)
            {
                trackedLoot.RemoveAt(i);
                continue;
            }

            IslandFlow flow =
                FindFlow(tag.IslandId);

            if (flow == null ||
                flow.landmark == null)
            {
                RemoveIslandLoot(
                    pickup,
                    tag
                );

                trackedLoot.RemoveAt(i);
                continue;
            }

            Vector3 relative =
                pickup.transform.position -
                flow.landmark.transform.position;

            float along =
                Vector3.Dot(
                    relative,
                    direction
                );

            float downstreamLimit =
                flow.maxAlong +
                CleanupPadding;

            if (Time.time - tag.SpawnTime <
                MinIslandLootLifetime)
            {
                continue;
            }

            if (along <= downstreamLimit)
                continue;

            RemoveIslandLoot(
                pickup,
                tag
            );

            trackedLoot.RemoveAt(i);
        }

        CleanupOrphanOceanLoot();
    }

    private static void CleanupOrphanOceanLoot()
    {
        Network_Player[] players =
            UnityEngine.Object.FindObjectsOfType<
                Network_Player>();

        if (players == null ||
            players.Length == 0)
        {
            return;
        }

        float maxDistanceSqr =
            OrphanLootRemoveDistance *
            OrphanLootRemoveDistance;

        for (int i = oceanLoot.Count - 1; i >= 0; i--)
        {
            PickupItem_Networked pickup =
                oceanLoot[i];

            if (!IsOceanLoot(pickup))
            {
                oceanLoot.RemoveAt(i);
                continue;
            }

            DynamicOceanLootTag tag =
                pickup.GetComponent<
                    DynamicOceanLootTag>();

            if (tag != null &&
                tag.Active)
            {
                continue;
            }

            bool nearPlayer =
                false;

            for (int j = 0; j < players.Length; j++)
            {
                Network_Player player =
                    players[j];

                if (player == null ||
                    !player.gameObject.activeInHierarchy)
                {
                    continue;
                }

                if (DistanceXZSqr(
                        pickup.transform.position,
                        player.transform.position) <=
                    maxDistanceSqr)
                {
                    nearPlayer =
                        true;

                    break;
                }
            }

            if (nearPlayer)
                continue;

            nextNetworkSync.Remove(
                pickup.GetInstanceID()
            );

            PickupObjectManager.RemovePickupItemNetwork(
                pickup
            );

            oceanLoot.RemoveAt(i);
        }
    }

    private static void RemoveIslandLoot(
        PickupItem_Networked pickup,
        DynamicOceanLootTag tag)
    {
        if (tag != null)
            tag.Active = false;

        if (pickup != null)
        {
            nextNetworkSync.Remove(
                pickup.GetInstanceID()
            );

            PickupObjectManager.RemovePickupItemNetwork(
                pickup
            );
        }
    }

    private static DynamicOceanLootTag GetActiveTag(
        PickupItem_Networked pickup)
    {
        if (pickup == null)
            return null;

        DynamicOceanLootTag tag =
            pickup.GetComponent<DynamicOceanLootTag>();

        if (tag == null ||
            !tag.Active ||
            !pickup.gameObject.activeInHierarchy)
        {
            if (tag != null)
                tag.Active = false;

            return null;
        }

        PickupItem item =
            pickup.PickupItem;

        if (item != null &&
            item.isDropped)
        {
            tag.Active = false;
            return null;
        }

        return tag;
    }

    private static int CountIslandLoot(
        int islandId)
    {
        int count = 0;

        for (int i = trackedLoot.Count - 1; i >= 0; i--)
        {
            DynamicOceanLootTag tag =
                GetActiveTag(
                    trackedLoot[i]
                );

            if (tag == null)
            {
                trackedLoot.RemoveAt(i);
                continue;
            }

            if (tag.IslandId == islandId)
                count++;
        }

        return count;
    }

    private static bool IsMultiplayer()
    {
        Raft_Network network =
            ComponentManager<Raft_Network>.Value;

        return network != null &&
               network.PlayerCount > 1;
    }

    private static float DistanceXZSqr(
        Vector3 a,
        Vector3 b)
    {
        float x =
            a.x - b.x;

        float z =
            a.z - b.z;

        return x * x + z * z;
    }

    private static Vector3 DirectionFromAngle(
        float angle)
    {
        return Quaternion.Euler(
                   0f,
                   angle,
                   0f
               ) *
               Vector3.forward;
    }

    private static void RefreshWaterReference(
        bool force)
    {
        if (!force &&
            cachedWater != null &&
            Time.unscaledTime <
            nextWaterLookup)
        {
            if (cachedWater.WindWaves != null)
            {
                cachedWindDirectionPointer =
                    cachedWater.WindWaves
                        .WindDirectionPointer;
            }

            return;
        }

        nextWaterLookup =
            Time.unscaledTime + 2f;

        if (cachedWater == null)
        {
            cachedWater =
                UnityEngine.Object
                    .FindObjectOfType<Water>();
        }

        if (cachedWater == null)
        {
            cachedWindDirectionPointer = null;
            return;
        }

        oceanScheduleSeed =
            cachedWater.Seed;

        if (cachedWater.WindWaves != null)
        {
            cachedWindDirectionPointer =
                cachedWater.WindWaves
                    .WindDirectionPointer;
        }
        else
        {
            cachedWindDirectionPointer = null;
        }
    }

    private static float GetOceanClock()
    {
        return Mathf.Max(
            0f,
            GameManager.TimePlayed
        );
    }

    private static void UpdateOceanDirection()
    {
        RefreshWaterReference(false);

        float clock =
            GetOceanClock();

        if (!oceanScheduleInitialized ||
            oceanScheduleSeedApplied != oceanScheduleSeed ||
            clock + 0.01f < oceanLastClock)
        {
            InitializeOceanSchedule(
                clock,
                oceanScheduleSeed
            );
        }

        oceanLastClock =
            clock;

        while (clock >= oceanTransitionEnd)
        {
            AdvanceOceanSchedule();
        }

        if (clock < oceanHoldEnd)
        {
            oceanAngle =
                oceanBaseAngle;

            oceanTransitionLogged =
                false;

            return;
        }

        if (!oceanTransitionLogged)
        {
            oceanTransitionLogged =
                true;

            Debug.Log(
                "[DynamicOcean] Ocean direction change started - segment " +
                oceanScheduleSegment +
                ", " +
                oceanBaseAngle.ToString("0.0") +
                " -> " +
                oceanTargetAngle.ToString("0.0")
            );
        }

        float duration =
            Mathf.Max(
                0.01f,
                oceanTransitionEnd -
                oceanHoldEnd
            );

        float t =
            Mathf.Clamp01(
                (clock - oceanHoldEnd) /
                duration
            );

        t =
            t *
            t *
            (3f - 2f * t);

        oceanAngle =
            Mathf.LerpAngle(
                oceanBaseAngle,
                oceanTargetAngle,
                t
            );
    }

    private static void InitializeOceanSchedule(
        float clock,
        int seed)
    {
        oceanScheduleInitialized =
            true;

        oceanScheduleSeedApplied =
            seed;

        oceanScheduleSegment =
            0;

        oceanBaseAngle =
            Hash01(
                seed,
                0,
                1u
            ) *
            360f;

        float cursor =
            0f;

        // Расписание восстанавливается один раз из сохранённого времени мира
        while (true)
        {
            float holdDuration =
                GetOceanHoldDuration(
                    seed,
                    oceanScheduleSegment
                );

            float holdEnd =
                cursor +
                holdDuration;

            float transitionEnd =
                holdEnd +
                OceanTransitionDuration;

            float targetAngle =
                GetNextOceanAngle(
                    oceanBaseAngle,
                    seed,
                    oceanScheduleSegment
                );

            if (clock < transitionEnd)
            {
                oceanTargetAngle =
                    targetAngle;

                oceanHoldEnd =
                    holdEnd;

                oceanTransitionEnd =
                    transitionEnd;

                oceanLastClock =
                    clock;

                oceanTransitionLogged =
                    clock >= holdEnd;

                if (clock < holdEnd)
                {
                    oceanAngle =
                        oceanBaseAngle;
                }
                else
                {
                    float t =
                        Mathf.Clamp01(
                            (clock - holdEnd) /
                            Mathf.Max(
                                0.01f,
                                OceanTransitionDuration
                            )
                        );

                    t =
                        t *
                        t *
                        (3f - 2f * t);

                    oceanAngle =
                        Mathf.LerpAngle(
                            oceanBaseAngle,
                            oceanTargetAngle,
                            t
                        );
                }

                return;
            }

            oceanBaseAngle =
                targetAngle;

            cursor =
                transitionEnd;

            oceanScheduleSegment++;
        }
    }

    private static void AdvanceOceanSchedule()
    {
        oceanBaseAngle =
            oceanTargetAngle;

        float segmentStart =
            oceanTransitionEnd;

        oceanScheduleSegment++;

        float holdDuration =
            GetOceanHoldDuration(
                oceanScheduleSeedApplied,
                oceanScheduleSegment
            );

        oceanHoldEnd =
            segmentStart +
            holdDuration;

        oceanTargetAngle =
            GetNextOceanAngle(
                oceanBaseAngle,
                oceanScheduleSeedApplied,
                oceanScheduleSegment
            );

        oceanTransitionEnd =
            oceanHoldEnd +
            OceanTransitionDuration;

        oceanTransitionLogged =
            false;
    }

    private static float GetOceanHoldDuration(
        int seed,
        int segment)
    {
        float maxInterval =
            OceanDayDuration *
            Mathf.Clamp(
                oceanMaxIntervalDays,
                1,
                OceanDefaultMaxIntervalDays
            );

        return Mathf.Lerp(
            OceanMinInterval,
            maxInterval,
            Hash01(
                seed,
                segment,
                2u
            )
        );
    }

    private static void ApplyOceanDirectionToWater()
    {
        RefreshWaterReference(false);

        if (cachedWater == null ||
            cachedWater.WindWaves == null)
        {
            return;
        }

        if (cachedWindDirectionPointer == null)
        {
            cachedWindDirectionPointer =
                cachedWater.WindWaves
                    .WindDirectionPointer;
        }

        if (cachedWindDirectionPointer == null)
            return;

        Vector3 direction =
            OceanDirection;

        if (direction.sqrMagnitude <= 0.0001f)
            return;

        cachedWindDirectionPointer.rotation =
            Quaternion.LookRotation(
                direction,
                Vector3.up
            );
    }

    private static float GetNextOceanAngle(
        float current,
        int seed,
        int segment)
    {
        float amount =
            Mathf.Lerp(
                OceanMinDirectionChange,
                OceanMaxDirectionChange,
                Hash01(
                    seed,
                    segment,
                    3u
                )
            );

        if (Hash01(
                seed,
                segment,
                4u) < 0.5f)
        {
            amount =
                -amount;
        }

        return Mathf.Repeat(
            current + amount,
            360f
        );
    }

    private static float Hash01(
        int seed,
        int index,
        uint salt)
    {
        unchecked
        {
            uint value =
                (uint)seed;

            value ^=
                (uint)index *
                0x9E3779B9u;

            value ^=
                salt *
                0x85EBCA6Bu;

            value ^= value >> 16;
            value *= 0x7FEB352Du;
            value ^= value >> 15;
            value *= 0x846CA68Bu;
            value ^= value >> 16;

            return
                (value & 0x00FFFFFFu) /
                16777215f;
        }
    }

    public void ExtraSettingsAPI_Load()
    {
        ExtraSettingsAPI_Loaded = true;
        RefreshExtraSettings();
    }

    public void ExtraSettingsAPI_Unload()
    {
        ExtraSettingsAPI_Loaded = false;
        islandLootSpawnRate = 1f;
        oceanMaxIntervalDays = OceanDefaultMaxIntervalDays;
        oceanScheduleInitialized = false;
    }

    public void ExtraSettingsAPI_SettingsClose()
    {
        RefreshExtraSettings();
        ExtraSettingsAPI_SaveSettings();
    }

    public string ExtraSettingsAPI_HandleSliderText(
        string name,
        float value)
    {
        if (name == SettingIslandLootSpawnRate)
            return value.ToString("0.0") + "x";

        if (name == SettingOceanDirectionChangeInterval)
        {
            int days =
                Mathf.Clamp(
                    Mathf.RoundToInt(value),
                    1,
                    OceanDefaultMaxIntervalDays
                );

            return days == 1
                ? "1 day"
                : "1-" + days + " days";
        }

        return value.ToString("0.0");
    }

    private static void RefreshExtraSettings()
    {
        if (!ExtraSettingsAPI_Loaded)
            return;

        int previousOceanMaxIntervalDays =
            oceanMaxIntervalDays;

        try
        {
            islandLootSpawnRate =
                Mathf.Clamp(
                    ExtraSettingsAPI_GetSliderValue(
                        SettingIslandLootSpawnRate
                    ),
                    0f,
                    10f
                );
        }
        catch
        {
            islandLootSpawnRate = 1f;
        }

        try
        {
            oceanMaxIntervalDays =
                Mathf.Clamp(
                    Mathf.RoundToInt(
                        ExtraSettingsAPI_GetSliderValue(
                            SettingOceanDirectionChangeInterval
                        )
                    ),
                    1,
                    OceanDefaultMaxIntervalDays
                );
        }
        catch
        {
            oceanMaxIntervalDays =
                OceanDefaultMaxIntervalDays;
        }

        if (previousOceanMaxIntervalDays !=
            oceanMaxIntervalDays)
        {
            oceanScheduleInitialized = false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static float ExtraSettingsAPI_GetSliderValue(
        string SettingName)
    {
        if (SettingName == SettingIslandLootSpawnRate)
            return 1f;

        if (SettingName == SettingOceanDirectionChangeInterval)
            return OceanDefaultMaxIntervalDays;

        return 0f;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ExtraSettingsAPI_SaveSettings()
    {
    }

}

[HarmonyPatch(
    typeof(Streamer),
    "Update"
)]
public static class DynamicOcean_StreamerPatch
{
    private static void Prefix(
        ref Vector3 ___worldRotationTargetDir)
    {
        Vector3 direction =
            DynamicOcean.OceanDirection;

        if (direction.sqrMagnitude <= 0.0001f)
            return;

        // Сохраняется ванильная ориентация модели флажка
        ___worldRotationTargetDir =
            -direction;
    }
}

[HarmonyPatch(
    typeof(ObjectSpawner_RaftDirection),
    "Update"
)]
public static class DynamicOcean_RaftSpawnerPatch
{
    private sealed class State
    {
        public List<PickupItem_Networked> hidden;
    }

    private static bool Prefix(
        ObjectSpawner_RaftDirection __instance,
        out State __state)
    {
        __state = null;

        if (BlockCreator.FoundationCount <= 0)
            return false;

        if (__instance == null ||
            __instance.spawnedObjects == null ||
            __instance.spawnedObjects.Count == 0)
        {
            return true;
        }

        State state =
            new State
            {
                hidden =
                    new List<PickupItem_Networked>()
            };

        // Островной лут исключается из ванильного удаления по дистанции от плота
        for (int i = __instance.spawnedObjects.Count - 1;
             i >= 0;
             i--)
        {
            PickupItem_Networked pickup =
                __instance.spawnedObjects[i];

            if (pickup == null)
                continue;

            DynamicOceanLootTag tag =
                pickup.GetComponent<
                    DynamicOceanLootTag>();

            if (tag == null ||
                !tag.Active)
            {
                continue;
            }

            state.hidden.Add(
                pickup
            );

            __instance.spawnedObjects.RemoveAt(
                i
            );
        }

        if (state.hidden.Count > 0)
            __state = state;

        return true;
    }

    private static void Postfix(
        ObjectSpawner_RaftDirection __instance,
        State __state)
    {
        Restore(
            __instance,
            __state
        );
    }

    private static Exception Finalizer(
        ObjectSpawner_RaftDirection __instance,
        State __state,
        Exception __exception)
    {
        Restore(
            __instance,
            __state
        );

        return __exception;
    }

    private static void Restore(
        ObjectSpawner_RaftDirection spawner,
        State state)
    {
        if (spawner == null ||
            state == null ||
            state.hidden == null)
        {
            return;
        }

        for (int i = 0; i < state.hidden.Count; i++)
        {
            PickupItem_Networked pickup =
                state.hidden[i];

            if (pickup == null ||
                spawner.spawnedObjects.Contains(
                    pickup
                ))
            {
                continue;
            }

            spawner.spawnedObjects.Add(
                pickup
            );
        }
    }
}

[HarmonyPatch(
    typeof(WorldShiftManager),
    "HandleWorldShift"
)]
public static class DynamicOcean_NoRaftWorldShiftPatch
{
    private static bool Prefix()
    {
        return BlockCreator.FoundationCount > 0;
    }
}

public class DynamicOceanLootTag : MonoBehaviour
{
    public bool Active;
    public int IslandId;
    public float SpawnTime;
}
