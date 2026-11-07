using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace HappyWheel;

internal static unsafe class BloodEffects
{
    private const string Root = "particles/blood_impact/";
    private const string Heavy = Root + "blood_impact_heavy.vpcf";
    private const string Spray = Root + "blood_impact_directional_highvel_spray.vpcf";
    private static readonly string[] Stains = ["materials/decals/bloodstain_001.vmat", "materials/decals/bloodstain_003.vmat", "materials/decals/bloodstain_003b.vmat"];
    internal static readonly string[] Resources = [Heavy, Spray, .. Stains];
    private static readonly TraceOptions Surface = new() { InteractsWith = Contents.Solid };
    private readonly record struct Entry(uint Handle, float Expires);
    private static readonly Queue<Entry> particles = new(), decals = new();

    private static void Expire(Queue<Entry> queue, float now)
    {
        while (queue.TryPeek(out var item) && item.Expires <= now)
            Physics.Resolve(queue.Dequeue().Handle)?.Remove();
    }
    private static void Track(CBaseEntity entity, Queue<Entry> queue, int limit, float lifetime)
    {
        if (queue.Count >= limit) Physics.Resolve(queue.Dequeue().Handle)?.Remove();
        queue.Enqueue(new(entity.EntityHandle.Raw, Server.CurrentTime + lifetime));
    }
    internal static void Sweep(float now) { Expire(particles, now); Expire(decals, now); }
    internal static void Clear() => Sweep(float.PositiveInfinity);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void Emit(uint raw, Vector3* contact, Vector3 direction, int tier)
    {
        try
        {
            var body = Physics.Resolve(raw);
            if (body?.AbsOrigin is not { } origin) return;
            var point = contact == null ? (Vector3)origin : *contact;
            if (direction.LengthSquared() < .001f)
            {
                float yaw = Random.Shared.NextSingle() * MathF.Tau;
                direction = new(MathF.Cos(yaw), MathF.Sin(yaw), .25f);
            }
            direction = Vector3.Normalize(direction);
            Particle(Heavy, point, direction);
            if (tier >= 2) Particle(Spray, point, direction);
            if (tier >= 3) Particle(Heavy, point + direction * 9, direction);
            // Cast through the exit side of the wound, plus separate falling droplets.
            Stain(body, point, direction * (120 + 48 * tier), tier);
            for (int i = 0; i < tier; i++)
            {
                float dx = Random.Shared.Next(-26, 27), dy = Random.Shared.Next(-26, 27);
                Stain(body, point, new Vector3(dx, dy, -185) + direction * (12 + i * 14), tier);
            }
        }
        catch (Exception ex) { Physics.Log?.LogError(ex, "Blood emission failed"); }
    }
    private static QAngle Angles(Vector3 forward, float roll) =>
        new(-MathF.Asin(Math.Clamp(forward.Z, -1, 1)) * 180 / MathF.PI,
            MathF.Atan2(forward.Y, forward.X) * 180 / MathF.PI, roll);

    private static void Particle(string effect, Vector3 point, Vector3 forward)
    {
        var entity = Utilities.CreateEntityByName<CParticleSystem>("info_particle_system")
            ?? throw new InvalidOperationException("Blood particle allocation failed");
        try
        {
            entity.EffectName = effect;
            entity.Teleport(new CounterStrikeSharp.API.Modules.Utils.Vector(point.X, point.Y, point.Z), Angles(forward, 0));
            entity.DispatchSpawn();
            entity.AcceptInput("Start");
            Track(entity, particles, 64, 2.5f);
        }
        catch { if (entity.IsValid) entity.Remove(); throw; }
    }
    private static void Stain(CBaseEntity ignore, Vector3 from, Vector3 travel, int tier)
    {
        Vector3 end = from + travel;
        var trace = Trace.TraceEndShape(new(from.X, from.Y, from.Z), new(end.X, end.Y, end.Z), ignore, Surface);
        if (!trace.DidHit() || trace.IsAllSolid) return;
        Vector3 normal = (Vector3)trace.Normal;
        if (normal.LengthSquared() < .5f) return;
        Vector3 point = from + travel * trace.Fraction + normal * .5f;
        var angles = Angles(-normal, Random.Shared.Next(360));
        var decal = Utilities.CreateEntityByName<CEnvDecal>("env_decal")
            ?? throw new InvalidOperationException("Blood decal allocation failed");
        try
        {
            using var kv = new CEntityKeyValues();
            kv.SetString("targetname", "hwr_bloodstain");
            kv.SetString("material", Stains[Random.Shared.Next(Stains.Length)]);
            kv.SetVector("origin", point.X, point.Y, point.Z);
            kv.SetAngle("angles", angles.X, angles.Y, angles.Z);
            float width = Random.Shared.Next(13, 24) + tier * 8;
            kv.SetFloat("width", width);
            kv.SetFloat("height", width * (.75f + .4f * Random.Shared.NextSingle()));
            kv.SetFloat("depth", 7);
            kv.SetBool("projectonworld", true);
            kv.SetBool("projectoncharacters", false);
            kv.SetBool("projectonwater", false);
            decal.DispatchSpawn(kv);
            Track(decal, decals, 96, 30);
        }
        catch { if (decal.IsValid) decal.Remove(); throw; }
    }
}
