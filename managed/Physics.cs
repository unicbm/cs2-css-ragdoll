using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;


namespace HappyWheel;

internal static unsafe partial class Physics
{
    const string Dll = "HappyWheelNative";
    internal static ILogger? Log;
    internal static readonly string[] Parts = ["pelvis", "chest", "head", "upper_arm_l", "forearm_l", "upper_arm_r", "forearm_r", "thigh_l", "shin_l", "thigh_r", "shin_r"];
    internal static string[] Agents = [];
    private static readonly Dictionary<string, int> AgentPaths = new(StringComparer.OrdinalIgnoreCase);
    internal static void LoadAgents(string module)
    {
        var sources = File.ReadAllLines(Path.Combine(module, "agents.txt"));
        Agents = sources.Select(Path.GetFileName).ToArray()!;
        AgentPaths.Clear();
        for (int i = 0; i < sources.Length; i++)
            foreach (var prefix in new[] { "characters/models/", "agents/models/" })
                AgentPaths.Add(prefix + sources[i] + ".vmdl", i);
    }
    internal static int Agent(CCSPlayerPawn pawn)
    {
        var path = pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState.ModelName;
        return path == null ? -1 : AgentPaths.GetValueOrDefault(path.Replace('\\', '/'), -1);
    }
    internal static string Model(int agent, string part) => $"models/happy_wheel_ragdoll/agents/{Agents[agent]}/{part}.vmdl";
    internal static readonly string LiverModel = OrganModel("porcine_liver");
    internal static string OrganModel(string name) => $"models/happy_wheel_ragdoll/organs/{name}.vmdl";
    internal static CBaseEntity? Resolve(uint raw) => new CHandle<CBaseEntity>(raw).Value;
    [StructLayout(LayoutKind.Sequential)]
    internal struct Bridge
    {
        internal delegate* unmanaged[Cdecl]<int, int, Vector3, Vector3, Vector3, uint> Body;
        internal delegate* unmanaged[Cdecl]<uint, uint, Vector3, float, int, uint> Joint;
        internal delegate* unmanaged[Cdecl]<uint, void> Remove;
        internal delegate* unmanaged[Cdecl]<uint, uint> Liver;
        internal delegate* unmanaged[Cdecl]<uint, Vector3*, Vector3, int, void> Blood;
        internal delegate* unmanaged[Cdecl]<uint, uint, LigamentHandles*, int> Ligament;
        internal delegate* unmanaged[Cdecl]<uint, uint, int> Overstretched;
    }
    [DllImport(Dll)] private static extern int hwr_bind_v8(Bridge callbacks);
    [DllImport(Dll)] internal static extern void hwr_clear();
    [DllImport(Dll)] internal static extern void hwr_unbind();
    [DllImport(Dll)] internal static extern int hwr_config(int capacity, float seconds);
    [DllImport(Dll)] internal static extern int hwr_spawn(int agent, float yaw, Vector3 velocity, int probe, double now, int hit, float damage, Vector3 impact, int scatter, DeathPose.Segment* pose);
    [DllImport(Dll)] internal static extern int hwr_shot(uint body, Vector3 position, Vector3 direction, double now);
    [DllImport(Dll)] internal static extern int hwr_hit(int id, int part, float damage);
    [DllImport(Dll)] internal static extern int hwr_broken(uint handle);
    [DllImport(Dll)] internal static extern int hwr_count();
    [DllImport(Dll)] internal static extern void hwr_sweep(double now);
    internal static bool Bind() => hwr_bind_v8(new Bridge { Body = &Body, Joint = &Joint, Remove = &Remove, Liver = &Liver, Blood = &BloodEffects.Emit, Ligament = &CreateLigament, Overstretched = &Overstretched }) == 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Body(int agent, int part, Vector3 center, Vector3 angles, Vector3 velocity)
        => SpawnProp(Model(agent, Parts[part]), center, angles, velocity);

    private static uint SpawnProp(string model, Vector3 center, Vector3 angles, Vector3 velocity, System.Drawing.Color? tint = null)
    {
        CPhysicsProp? e = null;
        try
        {
            e = Utilities.CreateEntityByName<CPhysicsProp>("prop_physics_override") ?? throw new InvalidOperationException("prop allocation");
            using var kv = new CEntityKeyValues();
            kv.SetString("targetname", $"hwr_{e.EntityHandle.Raw}");
            kv.SetString("model", model);
            kv.SetVector("origin", center.X, center.Y, center.Z);
            kv.SetAngle("angles", angles.X, angles.Y, angles.Z);
            kv.SetInt("spawnflags", 8192);
            kv.SetString("interactAs", "physics_prop");
            if (model == LigamentModel) kv.SetString("interactExclude", "physics_prop");
            kv.SetBool("forcenavignore", true);
            kv.SetBool("nonavmeshblocker", true);
            kv.SetBool("auto_convert_back_from_debris", false);
            e.DispatchSpawn(kv);
            if (tint is { } color) e.Render = color;
            if (!e.IsValid || e.MoveType != MoveType_t.MOVETYPE_VPHYSICS) throw new InvalidOperationException($"No VPhysics body for {model}");
            if (velocity != Vector3.Zero) e.Teleport(null, null, velocity);
            return e.EntityHandle.Raw;
        }
        catch (Exception ex) { Log?.LogError(ex, "Failed to spawn body {Model}", model); if (e is { IsValid: true }) e.Remove(); return 0; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Liver(uint child)
    {
        try
        {
            var body = Resolve(child);
            if (body?.AbsOrigin is not { } origin) return 0;
            Vector3 point = (Vector3)origin;
            float azimuth = Random.Shared.NextSingle() * MathF.Tau;
            Vector3 outward = new(MathF.Cos(azimuth), MathF.Sin(azimuth), 0);
            return SpawnProp(LiverModel, point + outward * 5,
                new Vector3(0, azimuth * 180 / MathF.PI, 0),
                (Vector3)body.AbsVelocity + outward * Random.Shared.Next(105, 165) + Vector3.UnitZ * Random.Shared.Next(85, 145),
                System.Drawing.Color.FromArgb(145, 42, 53));
        }
        catch (Exception ex)
        {
            Log?.LogError(ex, "Failed to spawn liver");
            return 0;
        }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Joint(uint parent, uint child, Vector3 anchor, float yaw, int hinge)
    {
        try
        {
            if (Resolve(parent) is not { } a || Resolve(child) is not { } b) return 0;
            var ends = (LocalPoint(a, anchor), LocalPoint(b, anchor));
            uint joint = Constraint(parent, child, anchor, false, yaw, hinge == 1);
            if (joint != 0) woundEnds[child] = ends;
            return joint;
        }
        catch (Exception ex) { Log?.LogError(ex, "Recording wound anchors on {Child}", child); return 0; }
    }

    private static uint Constraint(uint parent, uint child, Vector3 anchor, bool ligament, float yaw = 0, bool hinge = false)
    {
        CPhysConstraint? e = null;
        try
        {
            e = Utilities.CreateEntityByName<CPhysConstraint>(ligament ? "phys_ballsocket" : "phys_ragdollconstraint") ?? throw new InvalidOperationException("joint allocation");
            using var kv = new CEntityKeyValues();
            kv.SetString("attach1", $"hwr_{parent}");
            kv.SetString("attach2", $"hwr_{child}");
            kv.SetInt("spawnflags", 1);
            if (!ligament)
            {
                kv.SetAngle("angles", 0, yaw, 0);
                kv.SetFloat("xmin", hinge ? -8 : -40); kv.SetFloat("xmax", hinge ? 8 : 40);
                kv.SetFloat("ymin", -80); kv.SetFloat("ymax", hinge ? 12 : 65);
                kv.SetFloat("zmin", hinge ? -8 : -35); kv.SetFloat("zmax", hinge ? 8 : 35);
            }
            // The light strand between heavy props takes a sharp load when the ragdoll lands.
            kv.SetFloat("forcelimit", ligament ? 1200 : 2400);
            kv.SetVector("origin", anchor.X, anchor.Y, anchor.Z);
            e.DispatchSpawn(kv);
            if (e.Joint == null) e.AcceptInput("TurnOn");
            if (e.Joint == null) throw new InvalidOperationException("Engine did not create constraint");
            return e.EntityHandle.Raw;
        }
        catch (Exception ex) { Log?.LogError(ex, "Failed to connect {Parent} and {Child}", parent, child); if (e is { IsValid: true }) e.Remove(); return 0; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Remove(uint raw)
    {
        try { woundEnds.Remove(raw); if (Resolve(raw) is { IsValid: true } entity) entity.Remove(); }
        catch (Exception ex) { Log?.LogError(ex, "Removing entity {Handle}", raw); }
    }
}
