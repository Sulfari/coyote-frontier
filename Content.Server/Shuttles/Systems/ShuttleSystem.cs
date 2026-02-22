using Content.Server._NF.Shuttles.Components; // Frontier
using Content.Server.Administration.Logs;
using Content.Server.Body.Systems;
using Content.Server.Buckle.Systems;
using Content.Server.GameTicking; // Frontier
using Content.Server.Parallax;
using Content.Server.Procedural;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Events;
using Content.Server.Station.Systems;
using Content.Server.Stunnable;
using Content.Shared.Atmos;
using Content.Shared.Buckle.Components;
using Content.Shared.Damage;
using Content.Shared.Lathe;
using Content.Shared.Light.Components;
using Content.Shared.Movement.Events;
using Content.Shared.Pinpointer;
using Content.Shared.Salvage;
using Content.Shared.Shuttles.Events;
using Content.Shared.Shuttles.Systems;
using Content.Shared.Tag;
using Content.Shared.Throwing;
using JetBrains.Annotations;
using Robust.Server.GameObjects;
using Robust.Server.GameStates;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using System.Diagnostics.CodeAnalysis;
using static Content.Shared.Pinpointer.SharedNavMapSystem;

namespace Content.Server.Shuttles.Systems;

[UsedImplicitly]
public sealed partial class ShuttleSystem : SharedShuttleSystem
{
    [Dependency] private readonly IAdminLogManager _logger = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IGameTiming _gameTiming = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly IPrototypeManager _protoManager = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ITileDefinitionManager _tileDefManager = default!;
    [Dependency] private readonly BiomeSystem _biomes = default!;
    [Dependency] private readonly BodySystem _bobby = default!;
    [Dependency] private readonly BuckleSystem _buckle = default!;
    [Dependency] private readonly DamageableSystem _damageSys = default!;
    [Dependency] private readonly DockingSystem _dockSystem = default!;
    [Dependency] private readonly DungeonSystem _dungeon = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly EntityManager _entityManager = default!;
    [Dependency] private readonly FixtureSystem _fixtures = default!;
    [Dependency] private readonly MapLoaderSystem _loader = default!;
    [Dependency] private readonly MapSystem _mapSystem = default!;
    [Dependency] private readonly MetaDataSystem _metadata = default!;
    [Dependency] private readonly PvsOverrideSystem _pvs = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedSalvageSystem _salvage = default!;
    [Dependency] private readonly ShuttleConsoleSystem _console = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly StunSystem _stuns = default!;
    [Dependency] private readonly ThrowingSystem _throwing = default!;
    [Dependency] private readonly ThrusterSystem _thruster = default!;
    [Dependency] private readonly UserInterfaceSystem _uiSystem = default!;
    [Dependency] private readonly GameTicker _ticker = default!; //Frontier: needed to get the main map in FTL

    private EntityQuery<BuckleComponent> _buckleQuery;
    private EntityQuery<MapGridComponent> _gridQuery;
    private EntityQuery<PhysicsComponent> _physicsQuery;
    private EntityQuery<TransformComponent> _xformQuery;

    private Dictionary<ResPath, EntityUid> shipCache = new();

    private readonly Dictionary<string, (string?, Color?)> ShownProtoIds = new() {
        { "BaseLathe", (null, Color.White) },
        { "BaseLatheLube", (null, Color.White) },
        { "SmallConstructibleMachine", (null, Color.Gray) },
        { "ConstructibleMachine", (null, Color.Gray) },
        { "Thruster", ("shipyard-preview-comp-thruster", Color.Brown) },
        { "ThrusterSecurity", (null, Color.Olive) },
        { "SmallThruster", ("shipyard-preview-comp-mini-thruster", Color.Brown) },
        { "MedicalTechFab", (null, Color.Blue) },
        { "EngineeringTechFab", (null, Color.Yellow) },
        { "ServiceTechFab", (null, Color.GreenYellow) },
        { "MercenaryTechFab", (null, Color.Olive) },
        { "BaseGeneratorShuttle", (null, Color.Yellow) },
        { "PortableGeneratorDKJr", (null, Color.Yellow) },
        { "PortableGeneratorDK", (null, Color.Yellow) },
        { "PortableGeneratorSwitchableBase", (null, Color.Yellow) },
        { "PortableGeneratorBase", (null, Color.Yellow) },
        { "AmeController", (null, Color.Yellow) },
        { "ComputerShuttle", (null, Color.Olive) },
    };

    public override void Initialize()
    {
        base.Initialize();

        _buckleQuery = GetEntityQuery<BuckleComponent>();
        _gridQuery = GetEntityQuery<MapGridComponent>();
        _physicsQuery = GetEntityQuery<PhysicsComponent>();
        _xformQuery = GetEntityQuery<TransformComponent>();

        InitializeFTL();
        InitializeGridFills();
        InitializeIFF();
        InitializeImpact();

        SubscribeLocalEvent<ShuttleComponent, ComponentStartup>(OnShuttleStartup);
        SubscribeLocalEvent<ShuttleComponent, ComponentShutdown>(OnShuttleShutdown);
        SubscribeLocalEvent<ShuttleComponent, TileFrictionEvent>(OnTileFriction);
        SubscribeLocalEvent<ShuttleComponent, FTLStartedEvent>(OnFTLStarted);
        SubscribeLocalEvent<ShuttleComponent, FTLCompletedEvent>(OnFTLCompleted);
        SubscribeNetworkEvent<ShuttleDataRequestEvent>(OnShuttleDataRequest);

        SubscribeLocalEvent<GridInitializeEvent>(OnGridInit);
        NfInitialize(); // Frontier
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        UpdateHyperspace();
        ShouldEmergencyBrake();
    }

    private void OnGridInit(GridInitializeEvent ev)
    {
        if (HasComp<MapComponent>(ev.EntityUid))
            return;

        EnsureComp<ShuttleComponent>(ev.EntityUid);
        EnsureComp<ImplicitRoofComponent>(ev.EntityUid);
    }

    private void OnShuttleStartup(EntityUid uid, ShuttleComponent component, ComponentStartup args)
    {
        if (!EntityManager.HasComponent<MapGridComponent>(uid))
        {
            return;
        }

        if (!EntityManager.TryGetComponent(uid, out PhysicsComponent? physicsComponent))
        {
            return;
        }

        if (component.Enabled)
        {
            Enable(uid, component: physicsComponent, shuttle: component);
        }

        component.DampingModifier = component.BodyModifier;
    }

    public void Toggle(EntityUid uid, ShuttleComponent component)
    {
        if (!EntityManager.TryGetComponent(uid, out PhysicsComponent? physicsComponent))
            return;

        if (HasComp<PreventGridAnchorChangesComponent>(uid)) // Frontier
            return; // Frontier

        component.Enabled = !component.Enabled;

        if (component.Enabled)
        {
            Enable(uid, component: physicsComponent, shuttle: component);
        }
        else
        {
            Disable(uid, component: physicsComponent);
        }
    }

    public void Enable(EntityUid uid, FixturesComponent? manager = null, PhysicsComponent? component = null, ShuttleComponent? shuttle = null)
    {
        if (!Resolve(uid, ref manager, ref component, ref shuttle, false))
            return;

        if (HasComp<PreventGridAnchorChangesComponent>(uid)) // Frontier
            return; // Frontier

        _physics.SetBodyType(uid, BodyType.Dynamic, manager: manager, body: component);
        _physics.SetBodyStatus(uid, component, BodyStatus.InAir);
        _physics.SetFixedRotation(uid, false, manager: manager, body: component);
    }

    public void Disable(EntityUid uid, FixturesComponent? manager = null, PhysicsComponent? component = null)
    {
        if (!Resolve(uid, ref manager, ref component, false))
            return;

        if (HasComp<PreventGridAnchorChangesComponent>(uid)) // Frontier
            return; // Frontier

        _physics.SetBodyType(uid, BodyType.Static, manager: manager, body: component);
        _physics.SetBodyStatus(uid, component, BodyStatus.OnGround);
        _physics.SetFixedRotation(uid, true, manager: manager, body: component);
    }

    private void OnShuttleShutdown(EntityUid uid, ShuttleComponent component, ComponentShutdown args)
    {
        // None of the below is necessary for any cleanup if we're just deleting.
        if (EntityManager.GetComponent<MetaDataComponent>(uid).EntityLifeStage >= EntityLifeStage.Terminating)
            return;

        Disable(uid);
    }

    private void OnTileFriction(Entity<ShuttleComponent> ent, ref TileFrictionEvent args)
    {
        args.Modifier *= ent.Comp.DampingModifier;
    }

    private void OnFTLStarted(Entity<ShuttleComponent> ent, ref FTLStartedEvent args)
    {
        ent.Comp.DampingModifier = 0f;
    }

    private void OnFTLCompleted(Entity<ShuttleComponent> ent, ref FTLCompletedEvent args)
    {
        ent.Comp.DampingModifier = ent.Comp.BodyModifier;
    }

    private void OnShuttleDataRequest(ShuttleDataRequestEvent args)
    {
        try
        {
            // Check cache for this ship
            if (shipCache.ContainsKey(args.ShuttleResPath))
            {
                RaiseNetworkEvent(new ShuttleDataResponse()
                {
                    Map = _entityManager.GetNetEntity(shipCache[args.ShuttleResPath]),
                    ShuttleResPath = args.ShuttleResPath
                });

                return;
            }

            var mapUid = _mapSystem.CreateMap(out var mapId);

            _entityManager.EnsureComponent<PhysicsComponent>(mapUid);
            _entityManager.EnsureComponent<FixturesComponent>(mapUid);
            if (_loader.TryLoadGrid(mapId, args.ShuttleResPath, out var grid))
            {
                if (!grid.HasValue)
                {
                    RaiseNetworkEvent(new ShuttleDataResponse()
                    {
                        Map = null,
                        ShuttleResPath = args.ShuttleResPath
                    });
                }

                var map = _entityManager.GetComponent<TransformComponent>(grid.Value.Owner).ParentUid;
                var entities = _mapSystem.GetAnchoredEntities(grid.Value.Owner, grid.Value, grid.Value.Comp.LocalAABB);
                var navmap = _entityManager.EnsureComponent<NavMapComponent>(grid.Value.Owner);
                var mapgrid = _entityManager.GetComponent<MapGridComponent>(grid.Value.Owner);

                RefreshGrid(mapUid, navmap!, mapgrid, entities);

                // Update cache. Ship layouts should never change mid-round, so no need to invalidate
                shipCache.Add(args.ShuttleResPath, grid.Value.Owner);

                RaiseNetworkEvent(new ShuttleDataResponse()
                {
                    Map = _entityManager.GetNetEntity(grid.Value.Owner),
                    ShuttleResPath = args.ShuttleResPath
                });
            }
        }
        catch (Exception e)
        {
            RaiseNetworkEvent(new ShuttleDataResponse()
            {
                Map = null,
                ShuttleResPath = args.ShuttleResPath
            });
        }
    }

    public void DeleteGridData(EntityUid mapOwner)
    {
        _entityManager.DeleteEntity(mapOwner);
    }

    private void RefreshGrid(EntityUid uid, NavMapComponent component, MapGridComponent mapGrid, IEnumerable<EntityUid> entities)
    {
        // Clear stale data
        component.Chunks.Clear();
        component.Beacons.Clear();

        // Refresh beacons
        var query = _entityManager.EntityQueryEnumerator<LatheComponent, TransformComponent>();
        foreach (var entity in entities)
        {
            _entityManager.TryGetComponent<MetaDataComponent>(entity, out var meta);

            string? name = null;
            Color? color = null;
            bool show = false;

            if (meta != null && meta.EntityPrototype != null && meta.EntityPrototype.Parents != null)
            {
                // Try to find parents first
                foreach (var protoId in meta.EntityPrototype.Parents)
                {
                    if (ShownProtoIds.TryGetValue(protoId, out var parentTuple))
                    {
                        name = parentTuple.Item1;
                        color = parentTuple.Item2;
                        show = true;
                        break;
                    }
                }

                // Overwrite with primary ID if more specific category is found
                if (ShownProtoIds.TryGetValue(meta.EntityPrototype.ID, out var tuple))
                {
                    name = tuple.Item1;
                    color = tuple.Item2;
                    show = true;
                }
            }

            if (show)
            {
                var qTransComp = _entityManager.GetComponent<TransformComponent>(entity);

                UpdateNavMapBeaconData(entity, qTransComp, name, color);
            }
        }

        // Loop over all tiles
        var tileRefs = _mapSystem.GetAllTiles(uid, mapGrid);

        foreach (var tileRef in tileRefs)
        {
            var tile = tileRef.GridIndices;
            var chunkOrigin = SharedMapSystem.GetChunkIndices(tile, ChunkSize);

            var chunk = EnsureChunk(component, chunkOrigin);
            RefreshTileEntityContents(uid, component, mapGrid, chunkOrigin, tile, setFloor: true);
        }
    }

    private void UpdateNavMapBeaconData(EntityUid uid, TransformComponent? xform = null, string? customName = null, Color? color = default)
    {
        if (!_entityManager.TransformQuery.Resolve(uid, ref xform))
            return;

        if (xform.GridUid == null)
            return;

        if (!_entityManager.GetEntityQuery<NavMapComponent>().TryComp(xform.GridUid, out var navMap))
            return;

        var meta = _entityManager.MetaQuery.GetComponent(uid);
        var changed = navMap.Beacons.Remove(meta.NetEntity);

        if (TryCreateNavMapBeaconData(uid, xform, meta, out var beaconData, customName, color))
        {
            navMap.Beacons.Add(meta.NetEntity, beaconData.Value);
            changed = true;
        }
    }

    protected bool TryCreateNavMapBeaconData(EntityUid uid, TransformComponent xform, MetaDataComponent meta, [NotNullWhen(true)] out NavMapBeacon? beaconData, string? customName = null, Color? color = null)
    {
        beaconData = null;

        if (xform.GridUid == null || !xform.Anchored)
            return false;

        var name = meta.EntityName;
        if (string.IsNullOrEmpty(name))
            name = meta.EntityName;

        beaconData = new NavMapBeacon(meta.NetEntity, color ?? Color.White, Loc.GetString(customName ?? name), xform.LocalPosition);

        return true;
    }

    private NavMapChunk EnsureChunk(NavMapComponent component, Vector2i origin)
    {
        if (!component.Chunks.TryGetValue(origin, out var chunk))
        {
            chunk = new(origin);
            component.Chunks[origin] = chunk;
        }

        return chunk;
    }

    private (int NewVal, NavMapChunk Chunk) RefreshTileEntityContents(EntityUid uid,
        NavMapComponent component,
        MapGridComponent mapGrid,
        Vector2i chunkOrigin,
        Vector2i tile,
        bool setFloor)
    {
        var relative = SharedMapSystem.GetChunkRelative(tile, ChunkSize);
        var chunk = EnsureChunk(component, chunkOrigin);
        ref var tileData = ref chunk.TileData[GetTileIndex(relative)];

        // Clear all data except for floor bits
        if (setFloor)
            tileData = FloorMask;
        else
            tileData &= FloorMask;

        var enumerator = _mapSystem.GetAnchoredEntitiesEnumerator(uid, mapGrid, tile);
        while (enumerator.MoveNext(out var ent))
        {
            var category = GetEntityType(ent.Value);
            if (category == NavMapChunkType.Invalid)
                continue;

            tileData |= (int)AtmosDirection.All << (int)category;
        }
        // Remove walls that intersect with doors (unless they can both physically fit on the same tile)
        // TODO NAVMAP why can this even happen?
        // Is this for blast-doors or something?

        // Shift airlock bits over to the wall bits
        var shiftedAirlockBits = (tileData & AirlockMask) >> ((int)NavMapChunkType.Airlock - (int)NavMapChunkType.Wall);

        // And then mask door bits
        tileData &= ~shiftedAirlockBits;

        return (tileData, chunk);
    }

    private readonly ProtoId<TagPrototype>[] WallTags = { "Wall", "Window" };

    public NavMapChunkType GetEntityType(EntityUid uid)
    {
        var _doorQuery = _entityManager.GetEntityQuery<NavMapDoorComponent>();
        if (_doorQuery.HasComp(uid))
            return NavMapChunkType.Airlock;
        var eq = _entityManager.GetEntityQuery<TagComponent>();
        eq.TryComp(uid, out var comp);
        foreach (var tag in WallTags)
        {
            if (comp?.Tags?.Contains(tag) ?? false)
                return NavMapChunkType.Wall;
        }

        return NavMapChunkType.Invalid;
    }
}
