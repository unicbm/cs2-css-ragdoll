using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace HappyWheel;

public sealed class Settings : BasePluginConfig
{
    public bool ReplaceDeaths { get; set; } = true;
    public int MaxCorpses { get; set; } = 6;
    public float LifetimeSeconds { get; set; } = 20;
}

public sealed unsafe class Plugin : BasePlugin, IPluginConfig<Settings>
{
    [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint GetModuleHandleW(string name);
    private string NativePath => Path.GetFullPath(Path.Combine(ModuleDirectory, "../../../happy-wheel-ragdoll/bin/win64/HappyWheelNative.dll"));
    public override string ModuleName => "happy-wheel-ragdoll";
    public override string ModuleVersion => "0.8.1";
    public Settings Config { get; set; } = new();
    private bool bound, precached;
    private int epoch;
    private const uint NoDraw = (uint)EntityEffects_t.EF_NODRAW;
    private readonly List<uint> pendingCorpseWrites = new();
    private readonly Dictionary<uint, (Color color, bool enabled, bool client, bool baseClient, bool noDraw, int deathTick)> hidden = new();
    private readonly Dictionary<uint, (int tick, int group, float damage, bool bullet, Vector3 direction, Vector3 position, Vector3 velocity)> hits = new();

    public void OnConfigParsed(Settings config)
    {
        if (config.MaxCorpses is < 1 or > 6 || !float.IsFinite(config.LifetimeSeconds) || config.LifetimeSeconds is < 1 or > 120)
            throw new ArgumentException("MaxCorpses must be 1..6 and LifetimeSeconds 1..120");
        Config = config;
        if (bound) Physics.hwr_config(config.MaxCorpses, config.LifetimeSeconds);
    }

    public override void Load(bool hotReload)
    {
        NativeLibrary.SetDllImportResolver(typeof(Plugin).Assembly, (name, _, _) =>
            name == "HappyWheelNative" ? GetModuleHandleW(NativePath) : 0);
        Physics.Log = Logger;
        Physics.LoadAgents(ModuleDirectory);
        DeathPose.Initialize(ModuleDirectory, Logger);
        RegisterListener<Listeners.OnServerPrecacheResources>(manifest =>
        {
            foreach (string part in Physics.Parts)
                for (int agent = 0; agent < Physics.Agents.Length; agent++) manifest.AddResource(Physics.Model(agent, part));
            manifest.AddResource(Physics.LiverModel);
            manifest.AddResource(Physics.LigamentModel);
            foreach (string resource in BloodEffects.Resources) manifest.AddResource(resource);
            precached = true;
        });
        RegisterListener<Listeners.OnMapEnd>(() => { Clear(); precached = false; });
        RegisterEventHandler<EventRoundStart>((_, _) => { Clear(); return HookResult.Continue; });
        RegisterEventHandler<EventPlayerSpawn>((ev, _) =>
        {
            if (ev.Userid?.PlayerPawn.Value is { LifeState: 0, Health: > 0 } p)
                Restore(p.EntityHandle.Raw, respawning: true);
            return HookResult.Continue;
        });
        RegisterListener<Listeners.OnPlayerTakeDamagePre>((p, d) =>
        {
            if (Config.ReplaceDeaths && precached)
                hits[p.EntityHandle.Raw] = (Server.TickCount, (int)d.HitGroupId, d.Damage, (d.BitsDamageType & DamageTypes_t.DMG_BULLET) != 0,
                    (Vector3)d.DamageForce, (Vector3)d.DamagePosition, (Vector3)p.AbsVelocity);
            return HookResult.Continue;
        });
        RegisterEventHandler<EventBulletImpact>(BulletImpact);
        RegisterEventHandler<EventPlayerDeath>(Death, HookMode.Pre);
        RegisterListener<Listeners.OnServerPostEntityThink>(() =>
        {
            FinishCorpseWrites();
            if (bound && (Server.TickCount & 3) == 0) Physics.hwr_sweep(Server.CurrentTime);
        });
        RegisterListener<Listeners.CheckTransmit>(clients =>
        {
            FinishCorpseWrites();
            foreach (var raw in hidden.Keys)
                if (Physics.Resolve(raw) is { IsValid: true, LifeState: not 0 } dead)
                    foreach (var (info, client) in clients)
                        if (client?.PlayerPawn.Raw != raw) info.TransmitEntities.Remove(dead);
        });
        foreach (var cls in new[] { "phys_ragdollconstraint", "phys_ballsocket" })
            HookEntityOutput(cls, "OnBreak", (_, _, _, caller, _, _) =>
            {
                if (!bound || caller is not { IsValid: true }) return HookResult.Continue;
                uint handle = caller.EntityHandle.Raw;
                int generation = epoch;
                Server.NextFrame(() => { if (bound && epoch == generation) Physics.hwr_broken(handle); });
                return HookResult.Continue;
            });
        AddTimer(0.25f, () => { if (bound) BloodEffects.Sweep(Server.CurrentTime); }, TimerFlags.REPEAT);
        AddCommand("rd_probe_joint", "Spawn two linked physics parts", (p, c) => SpawnTest(p, c, true));
        AddCommand("rd_spawn_test", "Spawn a full crash dummy", (p, c) => SpawnTest(p, c, false));
        AddCommand("rd_break", "rd_break <rig> <part 1..10> <damage>", (p, c) =>
        {
            if (!CanManage(p) || !bound) return;
            if (!int.TryParse(c.GetArg(1), out int id) || !int.TryParse(c.GetArg(2), out int part) ||
               !float.TryParse(c.GetArg(3), out float damage) || !float.IsFinite(damage) || damage < 0)
            { c.ReplyToCommand("rd_break <rig> <part 1..10> <damage>"); return; }
            c.ReplyToCommand($"[HWR] transition={Physics.hwr_hit(id, part, damage)}");
        });
        AddCommand("rd_replace", "rd_replace <0|1>", (p, c) =>
        {
            if (!CanManage(p)) return;
            if (c.GetArg(1) == "0") { Config.ReplaceDeaths = false; Clear(); }
            if (c.GetArg(1) == "1") Config.ReplaceDeaths = true;
            c.ReplyToCommand($"[HWR] replacement={Config.ReplaceDeaths}");
        });
        AddCommand("rd_status", "Report physics body count", (p, c) =>
        {
            if (CanManage(p)) c.ReplyToCommand($"[HWR] precached={precached} bound={bound} bodies={(bound ? Physics.hwr_count() : 0)} hidden={hidden.Count}");
        });
    }
    private static bool CanManage(CCSPlayerController? p) => p == null || AdminManager.PlayerHasPermissions(p, "@css/root");
    private bool Ready()
    {
        if (!precached) return false;
        if (bound) return true;
        if (GetModuleHandleW(NativePath) == 0) return false;
        bound = Physics.Bind();
        if (bound) Physics.hwr_config(Config.MaxCorpses, Config.LifetimeSeconds);
        return bound;
    }
    private void SpawnTest(CCSPlayerController? player, CommandInfo command, bool probe)
    {
        if (!CanManage(player)) return;
        var pawn = (player ?? Utilities.GetPlayers().FirstOrDefault(p => p.PawnIsAlive))?.PlayerPawn.Value;
        if (pawn?.AbsOrigin is not { } pos || !Ready()) { command.ReplyToCommand("[HWR] Requires a live pawn, map precache and Metamod module"); return; }
        float yaw = pawn.EyeAngles.Y, radians = yaw * MathF.PI / 180;
        var feet = (Vector3)pos + new Vector3(MathF.Cos(radians) * 50, MathF.Sin(radians) * 50, 35);
        DeathPose.Segment* pose = stackalloc DeathPose.Segment[11];
        int agent = Physics.Agent(pawn);
        if (agent < 0) { command.ReplyToCommand("[HWR] No matching segmented model"); return; }
        if (!DeathPose.ReadAgent(pawn, agent, new Span<DeathPose.Segment>(pose, 11))) { command.ReplyToCommand("[HWR] Agent pose unavailable"); return; }
        for (int i = 0; i < 11; ++i) { pose[i].Center += feet - (Vector3)pos; pose[i].Pivot += feet - (Vector3)pos; }
        int id = Physics.hwr_spawn(agent, yaw, default, probe ? 1 : 0, Server.CurrentTime, -1, 0, default, 0, pose);
        command.ReplyToCommand($"[HWR] rig={id} bodies={Physics.hwr_count()}");
    }
    private static readonly TraceOptions CorpseTrace = new() { InteractsWith = Masks.Shot | Contents.PhysicsProp };
    private readonly HashSet<(uint shooter, uint body)> shotTicks = new();
    private HookResult BulletImpact(EventBulletImpact ev, GameEventInfo _)
    {
        if (!bound || Physics.hwr_count() == 0 || ev.Userid?.PlayerPawn.Value is not { } shooter ||
            shooter.AbsOrigin is not { } feet) return HookResult.Continue;
        Vector3 eye = new(feet.X, feet.Y, feet.Z + shooter.ViewOffset.Z);
        Vector3 end = new(ev.X, ev.Y, ev.Z);
        Vector3 path = end - eye;
        if (!float.IsFinite(path.LengthSquared()) || path.LengthSquared() < .001f) return HookResult.Continue;
        var trace = Trace.TraceEndShape(new(eye.X, eye.Y, eye.Z), new(end.X, end.Y, end.Z), shooter, CorpseTrace);
        if (!trace.DidHit() || trace.HitEntity() is not { IsValid: true, DesignerName: "prop_physics_override" } struck)
            return HookResult.Continue;
        uint handle = struck.EntityHandle.Raw;
        var key = (shooter.EntityHandle.Raw, handle);
        int tick = Server.TickCount;
        // A penetrated surface can produce multiple impact events for one bullet.
        if (shotTick != tick) { shotTicks.Clear(); shotTick = tick; }
        if (shotTicks.Contains(key)) return HookResult.Continue;
        Vector3 contact = eye + path * trace.Fraction;
        int part = Physics.hwr_shot(handle, contact, Vector3.Normalize(path), Server.CurrentTime);
        if (part < 0) return HookResult.Continue;
        shotTicks.Add(key);
        if (part == Physics.Parts.Length) return HookResult.Continue; // The struck ligament was severed.
        var body = struck.As<CPhysicsProp>();
        float speed = part is 0 or 1 ? 75 : part is 7 or 9 ? 105 : 155;
        body.Teleport(null, null, (Vector3)body.AbsVelocity + Vector3.Normalize(path) * speed);
        return HookResult.Continue;
    }
    private int shotTick = -1;

    private HookResult Death(EventPlayerDeath ev, GameEventInfo _)
    {
        if (!Config.ReplaceDeaths || ev.Userid is not { ControllingBot: false } victim ||
           victim.PlayerPawn.Value is not { IsValid: true } pawn || !Ready()) return HookResult.Continue;
        uint raw = pawn.EntityHandle.Raw;
        hits.Remove(raw, out var hit);
        int agent = Physics.Agent(pawn);
        if (agent < 0 || hidden.ContainsKey(raw)) return HookResult.Continue;
        bool current = hit.damage > 0 && Server.TickCount - hit.tick is >= 0 and <= 1;
        DeathPose.Segment* pose = stackalloc DeathPose.Segment[11];
        if (!DeathPose.ReadAgent(pawn, agent, new Span<DeathPose.Segment>(pose, 11))) return HookResult.Continue;
        int part = 1;
        if (ev.Headshot || (current && hit.group is 1 or 8)) part = 2;
        else if (current && hit.group is >= 4 and <= 7)
        {
            int upper = 3 + 2 * (hit.group - 4);
            int lower = upper + 1;
            part = Vector3.DistanceSquared(hit.position, pose[upper].Center) <= Vector3.DistanceSquared(hit.position, pose[lower].Center) ? upper : lower;
        }
        string weapon = ev.Weapon.Replace("weapon_", "");
        bool shotgun = weapon is "mag7" or "nova" or "xm1014" or "sawedoff";
        bool heavy = shotgun || weapon == "awp";
        float damage = Math.Max(current ? hit.damage : 0, ev.DmgHealth);
        Vector3 direction = current ? hit.direction : default;
        if (ev.Attacker?.PlayerPawn.Value is { } attacker && attacker.EntityHandle.Raw != raw &&
            attacker.AbsOrigin is { } source)
        {
            Vector3 ray = pose[part].Center - ((Vector3)source + Vector3.UnitZ * attacker.ViewOffset.Z);
            if (ray.LengthSquared() > .0001f) direction = ray;
        }
        Vector3 kick = default;
        if (float.IsFinite(direction.LengthSquared()) && direction.LengthSquared() > .0001f)
        {
            float speed = Math.Clamp(175 + damage * 1.4f, 175, 345);
            float lift = 58;
            if (heavy)
            {
                speed = shotgun ? 850 : 1000;
                lift = shotgun ? 300 : 265;
            }
            else if (current && hit.bullet)
            {
                speed *= 1.65f;
                lift = 145;
            }
            kick = Vector3.Normalize(direction) * speed + Vector3.UnitZ * lift;
        }
        if (heavy && kick != default) damage = Math.Max(damage, 105);
        else if (current && hit.bullet && !heavy) damage = Math.Min(damage, 60);
        var velocity = current ? hit.velocity : (Vector3)pawn.AbsVelocity;
        int id = Physics.hwr_spawn(agent, pawn.AbsRotation?.Y ?? 0, velocity, 0, Server.CurrentTime,
            part, damage, kick, shotgun && kick != default ? 1 : 0, pose);
        if (id == 0) return HookResult.Continue;
        hidden.TryAdd(raw, (pawn.Render, pawn.RagdollEnabled, pawn.RagdollClientSide, pawn.ClientSideRagdoll, (pawn.Effects & NoDraw) != 0, Server.TickCount));
        pendingCorpseWrites.Add(raw);
        pawn.Effects |= NoDraw;
        pawn.Render = Color.FromArgb(0, pawn.Render);
        pawn.RagdollEnabled = false;
        pawn.RagdollClientSide = false;
        pawn.ClientSideRagdoll = false;
        MarkChanged(pawn);
        return HookResult.Continue;
    }
    // CS2 sets CBaseEntity::m_bClientSideRagdoll after dispatching player_death.
    // Consume that write at the simulation/network boundary and keep its value for restoration.
    private void FinishCorpseWrites()
    {
        for (int i = pendingCorpseWrites.Count - 1; i >= 0; --i)
        {
            uint raw = pendingCorpseWrites[i];
            if (Physics.Resolve(raw)?.As<CCSPlayerPawn>() is { IsValid: true } pawn && hidden.TryGetValue(raw, out var original))
            {
                if (Server.TickCount != original.deathTick && pawn.LifeState == 0 && pawn.Health > 0)
                {
                    Restore(raw, respawning: true);
                    continue;
                }
                if ((pawn.Effects & NoDraw) == 0)
                {
                    pawn.Effects |= NoDraw;
                    Utilities.SetStateChanged(pawn, "CBaseEntity", "m_fEffects");
                }
                if (pawn.Render.A != 0)
                {
                    pawn.Render = Color.FromArgb(0, pawn.Render);
                    Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");
                }
                if (!pawn.ClientSideRagdoll) continue;
                original.baseClient = true;
                hidden[raw] = original;
                pawn.ClientSideRagdoll = false;
                Utilities.SetStateChanged(pawn, "CBaseEntity", "m_bClientSideRagdoll");
                continue;
            }
            hidden.Remove(raw);
            pendingCorpseWrites.RemoveAt(i);
        }
    }
    private static void MarkChanged(CCSPlayerPawn p)
    {
        Utilities.SetStateChanged(p, "CBaseEntity", "m_fEffects");
        Utilities.SetStateChanged(p, "CBaseEntity", "m_bClientSideRagdoll");
        Utilities.SetStateChanged(p, "CBaseModelEntity", "m_clrRender");
        Utilities.SetStateChanged(p, "CBaseAnimGraph", "m_bRagdollEnabled");
        Utilities.SetStateChanged(p, "CBaseAnimGraph", "m_bRagdollClientSide");
    }
    private void Restore(uint raw, bool respawning = false)
    {
        pendingCorpseWrites.Remove(raw);
        if (!hidden.Remove(raw, out var original) || Physics.Resolve(raw)?.As<CCSPlayerPawn>() is not { IsValid: true } p) return;
        if (!original.noDraw) p.Effects &= ~NoDraw;
        if (p.Render.A == 0) p.Render = original.color;
        if (!respawning && p.LifeState != 0)
        {
            p.RagdollEnabled = original.enabled;
            p.RagdollClientSide = original.client;
            p.ClientSideRagdoll = original.baseClient;
        }
        MarkChanged(p);
    }
    private void Clear()
    {
        ++epoch;
        BloodEffects.Clear();
        pendingCorpseWrites.Clear();
        if (bound) Physics.hwr_clear();
        foreach (var raw in hidden.Keys.ToArray()) Restore(raw);
        hits.Clear();
    }
    public override void Unload(bool hotReload)
    {
        Clear();
        if (bound) Physics.hwr_unbind();
        bound = false;
        Physics.Log = null;
    }
}
