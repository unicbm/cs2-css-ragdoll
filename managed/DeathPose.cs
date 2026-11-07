using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;

namespace HappyWheel;

// The server returns capsules in world space. Validate the stock 19-hitbox layout
// before using its indices to fit our 11 rigid pieces to the final pose.
internal static unsafe class DeathPose
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Binding
    {
        internal uint Size, Version;
        internal nint Function;
        internal int Scene, Skeleton, A, B, Radius, Group, Shape, Index;
    }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Capsule(Vector3 A, Vector3 B, float Radius, int Group, int Index);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Bone(Vector3 Origin, Quaternion Rotation);
    internal struct Segment { internal Vector3 Center, Angles, Pivot; }
    [DllImport("HappyWheelNative")]
    private static extern int Hwr_ReadBonePoseV1(ref Binding config, nint pawn, Capsule* output, int capacity, Bone* frames);
    private static Binding binding;
    private static float[][][] bindPoses = [];
    internal static bool ReadAgent(CCSPlayerPawn pawn, int agent, Span<Segment> rig)
    {
        if (Read(pawn, rig)) return true;
        if (binding.Function == 0 || pawn.AbsOrigin is not { } origin) return false;
        float yaw = pawn.AbsRotation?.Y ?? 0;
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yaw * MathF.PI / 180);
        Vector3 feet = (Vector3)origin;
        for (int i = 0; i < rig.Length; i++)
        {
            var f = bindPoses[agent][i];
            rig[i] = new Segment {
                Center = feet + Vector3.Transform(new Vector3(f[0], f[1], f[2]), rotation),
                Angles = new(f[3], f[4] + yaw, f[5]),
                Pivot = feet + Vector3.Transform(new Vector3(f[6], f[7], f[8]), rotation)
            };
        }
        return true;
    }
    internal static void Initialize(string directory, ILogger log)
    {
        bindPoses = JsonSerializer.Deserialize<float[][][]>(File.ReadAllText(Path.Combine(directory, "bind-poses.json"))) ?? [];
        if (bindPoses.Length != Physics.Agents.Length || bindPoses.Any(p => p.Length != 11 || p.Any(f => f.Length != 9 || f.Any(x => !float.IsFinite(x)))))
            throw new InvalidDataException("Invalid agent bind poses");
        binding = default;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "pose.json")));
            var cfg = doc.RootElement;
            var server = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Single(m => string.Equals(m.ModuleName, "server.dll", StringComparison.OrdinalIgnoreCase) && m.FileName.Replace('/', '\\').EndsWith(@"\csgo\bin\win64\server.dll", StringComparison.OrdinalIgnoreCase));
            using var file = File.OpenRead(server.FileName);
            if (Convert.ToHexString(SHA256.HashData(file)) != cfg.GetProperty("serverSha256").GetString()) throw new InvalidDataException("server fingerprint mismatch");
            var entry = cfg.GetProperty("hitboxList");
            int rva = Convert.ToInt32(entry.GetProperty("rva").GetString(), 16);
            var bytes = Convert.FromHexString(entry.GetProperty("expected").GetString()!);
            if (rva <= 0 || bytes.Length < 5 || rva > server.ModuleMemorySize - bytes.Length || !new ReadOnlySpan<byte>((void*)(server.BaseAddress + rva), bytes.Length).SequenceEqual(bytes))
                throw new InvalidDataException("hitbox function mismatch");
            int Offset(string key) => Convert.ToInt32(cfg.GetProperty("offsets").GetProperty(key).GetString(), 16);
            binding = new Binding
            {
                Size = (uint)sizeof(Binding),
                Version = 1,
                Function = server.BaseAddress + rva,
                Scene = Offset("hitboxPawnSceneNode"),
                Skeleton = Offset("hitboxSkeletonVtable"),
                A = Offset("hitboxA"),
                B = Offset("hitboxB"),
                Radius = Offset("hitboxRadius"),
                Group = Offset("hitboxGroup"),
                Shape = Offset("hitboxShape"),
                Index = Offset("hitboxIndex")
            };
        }
        catch (Exception ex) { log.LogWarning("Death pose unavailable, preserving original corpses: {Reason}", ex.Message); }
    }
    private static bool Read(CCSPlayerPawn pawn, Span<Segment> rig)
    {
        if (binding.Function == 0) return false;
        Capsule* raw = stackalloc Capsule[32];
        Bone* frames = stackalloc Bone[32];
        if (Hwr_ReadBonePoseV1(ref binding, pawn.Handle, raw, 32, frames) != 19) return false;
        Span<Bone> ordered = stackalloc Bone[19];
        Span<Capsule> bones = stackalloc Capsule[19];
        ReadOnlySpan<int> groups = [1, 8, 3, 3, 2, 2, 2, 6, 7, 6, 7, 6, 7, 4, 5, 4, 4, 5, 5];
        int seen = 0;
        for (int i = 0; i < 19; i++)
        {
            var c = raw[i];
            if ((uint)c.Index >= 19 || c.Group != groups[c.Index] || (seen & (1 << c.Index)) != 0) return false;
            ordered[c.Index] = frames[i];
            bones[c.Index] = c; seen |= 1 << c.Index;
        }
        static Vector3 Mid(Capsule c) => (c.A + c.B) * 0.5f;
        ReadOnlySpan<int> indices = [2, 5, 0, 15, 16, 17, 18, 7, 9, 8, 10];
        for (int i = 0; i < indices.Length; i++)
        {
            var f = ordered[indices[i]];
            var q = Quaternion.Normalize(f.Rotation);
            rig[i] = new Segment { Center = f.Origin, Angles = Angles(q),
                Pivot = i switch { 1 => Mid(bones[3]), 2 => ordered[1].Origin, _ => f.Origin } };
        }
        return true;
    }
    private static Vector3 Angles(Quaternion q)
    {
        var x = Vector3.Transform(Vector3.UnitX, q);
        var y = Vector3.Transform(Vector3.UnitY, q);
        var z = Vector3.Transform(Vector3.UnitZ, q);
        float xy = MathF.Sqrt(x.X * x.X + x.Y * x.Y);
        float yaw = xy > .001f ? MathF.Atan2(x.Y, x.X) : MathF.Atan2(-y.X, y.Y);
        float roll = xy > .001f ? MathF.Atan2(y.Z, z.Z) : 0;
        return new Vector3(MathF.Atan2(-x.Z, xy), yaw, roll) * (180 / MathF.PI);
    }
}
