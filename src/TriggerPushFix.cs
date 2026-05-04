using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Memory;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.SchemaDefinitions;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Convars;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tomlyn;

namespace TriggerPushFix;

[PluginMetadata(
    Id = "TriggerPushFix",
    Version = "1.0.1",
    Name = "TriggerPushFix",
    Author = "Source2ZE devs (port by Marchand)",
    Description = "Revert trigger_push behaviour to be like CSGO")
]
public partial class TriggerPushFix : BasePlugin {

    // Spawnflag: trigger fires once and kills itself
    private const uint SF_TRIG_PUSH_ONCE = 0x80;

    // CCollisionProperty::m_usSolidFlags bit
    private const byte FSOLID_NOT_SOLID = 0x04;

    // TriggerPush_Touch(CTriggerPush* this, CBaseEntity* pOther)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TriggerPushTouchDelegate(nint pPush, nint pOther);

    // SetGroundEntity(CBaseEntity* this, CBaseEntity* pGround, <bone index or nullptr>)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetGroundEntityDelegate(nint pEntity, nint pGround, nint unk);

    private class Config {
        public List<string> Maps { get; set; } = [];
        public bool CachePushVector { get; set; } = true;
        public bool KillOnFailure { get; set; } = true;
        public string DiscordWebhook { get; set; } = "";
    }

    private static readonly HttpClient s_http = new();

    private IConVar<bool>? _useOldPushConVar;
    private bool _useOldPush;
    private HashSet<string> _mapOverrides = [];
    private bool _currentMapInOverrides;
    private bool _cachePushVector = true;
    private bool _suppressAllPushes;
    private bool _killPushEntitiesAtSpawn;
    private bool _mapLoadComplete;
    private readonly Dictionary<nint, Vector> _pushVectorCache = [];
    private IUnmanagedFunction<TriggerPushTouchDelegate>? _triggerPushTouch;
    private IUnmanagedFunction<SetGroundEntityDelegate>? _setGroundEntity;
    private Guid _hookGuid;
    private int _passesTriggerFiltersVtableSlot;

    public TriggerPushFix(ISwiftlyCore core) : base(core) { }

    public override void ConfigureSharedInterface(IInterfaceManager interfaceManager) {
        Core.Configuration.InitializeWithTemplate("config.toml", "template.toml");
    }

    public override void Load(bool hotReload) {
        _useOldPushConVar = Core.ConVar.CreateOrFind(
            "trigger_push_fix",
            "Whether to use the old CSGO trigger_push behavior (set to 0 to disable)",
            true);

        _useOldPush = _useOldPushConVar.Value;
        Core.Event.OnConVarValueChanged += OnConVarChanged;

        // Handle hot-reload mid-map as OnMapLoad won't fire
        var cfg = LoadConfig(Core.Configuration.GetConfigPath("config.toml"));
        _mapOverrides = NormalizeMaps(cfg.Maps);
        _cachePushVector = cfg.CachePushVector;

        bool sigsOk = true;
        var failedSigs = new List<string>();

        if (!Core.GameData.TryGetOffset("PassesTriggerFilters", out nint passesTriggerFiltersRaw)) {
            Core.Logger.LogError("[TriggerPushFix] Failed to find PassesTriggerFilters offset.");
            failedSigs.Add("PassesTriggerFilters");
            sigsOk = false;
        } else {
            _passesTriggerFiltersVtableSlot = (int)passesTriggerFiltersRaw;
        }

        if (!Core.GameData.TryGetSignature("SetGroundEntity", out var setGroundEntityAddr)) {
            Core.Logger.LogError("[TriggerPushFix] Failed to find SetGroundEntity signature.");
            failedSigs.Add("SetGroundEntity");
            sigsOk = false;
        } else {
            _setGroundEntity = Core.Memory.GetUnmanagedFunctionByAddress<SetGroundEntityDelegate>(setGroundEntityAddr);
        }

        if (!Core.GameData.TryGetSignature("TriggerPush_Touch", out var touchAddr)) {
            Core.Logger.LogError("[TriggerPushFix] Failed to find TriggerPush_Touch signature.");
            failedSigs.Add("TriggerPush_Touch");
            if (cfg.KillOnFailure) {
                _killPushEntitiesAtSpawn = true;
                Core.Logger.LogWarning("[TriggerPushFix] KillOnFailure: TriggerPush_Touch unavailable - trigger_push entities will be disabled on maps that you have configured to use it.");
                Core.Event.OnEntitySpawned += OnEntitySpawned;
                Core.Event.OnMapUnload += OnMapUnload;
            }
            Core.Event.OnMapLoad += OnMapLoad;
            NotifyDiscord(cfg.DiscordWebhook, failedSigs);
            return;
        }

        _triggerPushTouch = Core.Memory.GetUnmanagedFunctionByAddress<TriggerPushTouchDelegate>(touchAddr);

        if (!sigsOk && cfg.KillOnFailure) {
            _suppressAllPushes = true;
            Core.Logger.LogWarning("[TriggerPushFix] KillOnFailure: signature failure detected - all trigger_push events will be disabled.");
        }

        _hookGuid = _triggerPushTouch.AddHook(next => (pPush, pOther) => OnTriggerPushTouch(next, pPush, pOther));

        Core.Event.OnMapLoad += OnMapLoad;

        // Only meaningful on hot-reload mid-map, MapName throws before any map is loaded
        try {
            var currentMap = (string)Core.Engine.GlobalVars.MapName;
            if (!string.IsNullOrEmpty(currentMap))
                _currentMapInOverrides = _mapOverrides.Contains(currentMap.ToLowerInvariant());
        } catch (NullReferenceException) { }

        NotifyDiscord(cfg.DiscordWebhook, failedSigs);

        if (_suppressAllPushes)
            Core.Logger.LogInformation("[TriggerPushFix] Loaded in suppression mode - all trigger_push entities are inactive.");
        else if (sigsOk)
            Core.Logger.LogInformation("[TriggerPushFix] Loaded. CSGO push behaviour active - set trigger_push_fix 0 to disable.");
    }

    private void NotifyDiscord(string webhook, List<string> failed) {
        if (string.IsNullOrEmpty(webhook) || failed.Count == 0) return;

        var hostnameConVar = Core.ConVar.CreateOrFind("hostname", "", "");
        _ = Task.Run(async () => {
            for (int i = 0; i < 5 && string.IsNullOrEmpty(hostnameConVar.Value); i++)
                await Task.Delay(2000);
            try {
                var hostname = hostnameConVar.Value;
                var lines = string.Join("\n", failed.Select(s => $"• `{s}`"));
                var payload = JsonSerializer.Serialize(new {
                    embeds = new[] { new {
                        title = "⚠️ TriggerPushFix - Signature Failure ⚠️",
                        description =
                            $"**Server:** {(string.IsNullOrEmpty(hostname) ? "*(unknown)*" : hostname)}\n\n" +
                            $"The following signatures failed to resolve against the current game binary:\n" +
                            $"{lines}\n\n" +
                            $"Update your gamedata to restore functionality.",
                        color = 0xE74C3C
                    }}
                });
                using var content = new StringContent(payload, System.Text.Encoding.UTF8);
                content.Headers.ContentType!.MediaType = "application/json";
                await s_http.PostAsync(webhook, content);
            } catch { }
        });
    }

    private void OnConVarChanged(IOnConVarValueChanged @event) {
        if (@event.ConVarName == "trigger_push_fix")
            _useOldPush = _useOldPushConVar!.Value;
    }

    public override void Unload() {
        Core.Event.OnConVarValueChanged -= OnConVarChanged;
        Core.Event.OnMapLoad -= OnMapLoad;
        Core.Event.OnMapUnload -= OnMapUnload;
        Core.Event.OnEntitySpawned -= OnEntitySpawned;
        if (_triggerPushTouch != null && _hookGuid != Guid.Empty) {
            _triggerPushTouch.RemoveHook(_hookGuid);
            _hookGuid = Guid.Empty;
        }
    }

    private void OnMapLoad(IOnMapLoadEvent @event) {
        var cfg = LoadConfig(Core.Configuration.GetConfigPath("config.toml"));
        _mapOverrides = NormalizeMaps(cfg.Maps);
        _cachePushVector = cfg.CachePushVector;
        _pushVectorCache.Clear();
        _currentMapInOverrides = _mapOverrides.Contains(@event.MapName.ToLowerInvariant());

        // All entities are up by now so AcceptInput("Kill") is safe - only on maps where the fix is enabled.
        if (_killPushEntitiesAtSpawn && _useOldPush != _currentMapInOverrides) {
            foreach (var entity in Core.EntitySystem.GetAllEntities()
                .Where(e => e.DesignerName == "trigger_push")
                .ToList()) {
                try { entity.AcceptInput<object>("Kill", null); }
                catch (Exception ex) { Core.Logger.LogWarning("[TriggerPushFix] Failed to kill trigger_push entity: {Message}", ex.Message); }
            }
        }

        _mapLoadComplete = true;
    }

    private void OnMapUnload(IOnMapUnloadEvent @event) {
        _mapLoadComplete = false;
    }

    // Catches trigger_push entities spawned post-load (e.g. point_template) - _mapLoadComplete
    // gates this so OnMapLoad handles the initial sweep synchronously instead.
    private void OnEntitySpawned(IOnEntitySpawnedEvent @event) {
        if (!_killPushEntitiesAtSpawn || !_mapLoadComplete) return;
        var entity = @event.Entity;
        if (entity.DesignerName == "trigger_push" && _useOldPush != _currentMapInOverrides)
            entity.AddEntityIOEvent<object>("Kill", null);
    }

    private static readonly TomlModelOptions s_tomlOptions = new() { ConvertPropertyName = name => name };

    private Config LoadConfig(string path) {
        if (!File.Exists(path)) return new Config();
        try {
            return Toml.ToModel<Config>(File.ReadAllText(path), null, s_tomlOptions);
        } catch (Exception ex) {
            Core.Logger.LogWarning("[TriggerPushFix] Failed to parse config.toml — using defaults: {Message}", ex.Message);
            return new Config();
        }
    }

    private static HashSet<string> NormalizeMaps(List<string> maps) =>
        [..maps.Select(m => m.Trim().ToLowerInvariant()).Where(m => m.Length > 0)];

    private void OnTriggerPushTouch(Func<TriggerPushTouchDelegate> next, nint pPush, nint pOther) {
        if (_suppressAllPushes) {
            // Only block trigger_push on maps where the fix is on - don't kill other entity types
            // or maps that opted out, let the hook chain keep doing its thing.
            var pushEntity = Helper.AsSchema<CBaseEntity>(pPush);
            if (pushEntity.DesignerName == "trigger_push" && _useOldPush != _currentMapInOverrides)
                return;
            next()(pPush, pOther);
            return;
        }

        var push = Helper.AsSchema<CTriggerPush>(pPush);

        // Fall through if old push is off, it's a one-shot trigger, or it's StartTouch-only.
        var effectiveUseOldPush = _useOldPush != _currentMapInOverrides;
        if (!effectiveUseOldPush
            || (push.Spawnflags & SF_TRIG_PUSH_ONCE) != 0
            || push.TriggerOnStartTouch) {
            next()(pPush, pOther);
            return;
        }

        var other = Helper.AsSchema<CBaseEntity>(pOther);
        var movetype = other.ActualMoveType;

        // VPhysics entities are handled correctly by the game already
        if (movetype == MoveType_t.MOVETYPE_VPHYSICS) {
            next()(pPush, pOther);
            return;
        }

        // These movetypes should not receive a push at all
        if (movetype is MoveType_t.MOVETYPE_NONE or MoveType_t.MOVETYPE_PUSH or MoveType_t.MOVETYPE_NOCLIP)
            return;

        var collision = other.Collision;
        if (collision == null) return;

        if (collision.SolidType == SolidType_t.SOLID_NONE || (collision.SolidFlags & FSOLID_NOT_SOLID) != 0)
            return;

        if (!CallPassesTriggerFilters(pPush, pOther))
            return;

        // Skip entities that are attached to a parent
        if (other.CBodyComponent?.SceneNode?.Parent != null)
            return;

        var pushSceneNode = push.CBodyComponent?.SceneNode;
        if (pushSceneNode == null) return;

        // Rotate the entity-space push direction into world space (lazily cached per trigger, or recomputed per-tick)
        Vector vecPush;
        if (_cachePushVector) {
            if (!_pushVectorCache.TryGetValue(pPush, out vecPush))
                _pushVectorCache[pPush] = vecPush = VectorRotate(push.PushDirEntitySpace, pushSceneNode.AbsRotation) * push.Speed;
        } else {
            vecPush = VectorRotate(push.PushDirEntitySpace, pushSceneNode.AbsRotation) * push.Speed;
        }

        var flags = other.Flags;

        // Accumulate with any existing base velocity set this tick
        if ((flags & (uint)Flags_t.FL_BASEVELOCITY) != 0)
            vecPush += other.BaseVelocity;

        // If the push has an upward component, lift the entity off the ground so
        // that gravity does not immediately cancel it out
        if (vecPush.Z > 0f && (flags & (uint)Flags_t.FL_ONGROUND) != 0 && _setGroundEntity != null) {
            _setGroundEntity.Call(pOther, nint.Zero, nint.Zero);

            var origin = other.AbsOrigin ?? Vector.Zero;
            origin.Z += 1.0f;
            other.Teleport(origin, null, null);
        }

        other.BaseVelocity = vecPush;
        other.BaseVelocityUpdated();

        other.Flags = flags | (uint)Flags_t.FL_BASEVELOCITY;
        other.FlagsUpdated();
    }

    // Calls CTriggerPush::PassesTriggerFilters(pOther) via vtable
    private unsafe bool CallPassesTriggerFilters(nint pPush, nint pOther) {
        var vtable  = *(nint*)pPush;
        var funcPtr = *(nint*)(vtable + _passesTriggerFiltersVtableSlot * IntPtr.Size);
        return ((delegate* unmanaged<nint, nint, bool>)funcPtr)(pPush, pOther);
    }

    // Equivalent to Source's VectorRotate(vec, EntityToWorldTransform(angles, pos), out)
    private static Vector VectorRotate(Vector vec, QAngle angles) {
        const float deg2rad = MathF.PI / 180f;
        var sy = MathF.Sin(angles.Yaw   * deg2rad);
        var cy = MathF.Cos(angles.Yaw   * deg2rad);
        var sp = MathF.Sin(angles.Pitch * deg2rad);
        var cp = MathF.Cos(angles.Pitch * deg2rad);
        var sr = MathF.Sin(angles.Roll  * deg2rad);
        var cr = MathF.Cos(angles.Roll  * deg2rad);

        return new Vector(
            vec.X * (cp * cy) + vec.Y * (sp * sr * cy - cr * sy) + vec.Z * (sp * cr * cy + sr * sy),
            vec.X * (cp * sy) + vec.Y * (sp * sr * sy + cr * cy) + vec.Z * (sp * cr * sy - sr * cy),
            vec.X * -sp       + vec.Y * (sr * cp)                + vec.Z * (cr * cp)
        );
    }
}