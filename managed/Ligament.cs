using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;

namespace HappyWheel;

internal static unsafe partial class Physics
{
    internal static readonly string LigamentModel = OrganModel("ligament");
    // Key by the serial-bearing child handle. Its original joint may already be broken.
    private static readonly Dictionary<uint, (Vector3 Parent, Vector3 Child)> woundEnds = new();

    [StructLayout(LayoutKind.Sequential)]
    internal struct LigamentHandles
    {
        internal uint Upper, Middle, Lower, FirstNode, SecondNode;
    }

    private static Quaternion Rotation(CBaseEntity body)
    {
        var angles = body.AbsRotation!;
        const float rad = MathF.PI / 180;
        return Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angles.Y * rad)
             * Quaternion.CreateFromAxisAngle(Vector3.UnitY, angles.X * rad)
             * Quaternion.CreateFromAxisAngle(Vector3.UnitX, angles.Z * rad);
    }

    private static Vector3 LocalPoint(CBaseEntity body, Vector3 point) =>
        Vector3.Transform(point - (Vector3)body.AbsOrigin!, Quaternion.Conjugate(Rotation(body)));

    private static Vector3 WorldPoint(CBaseEntity body, Vector3 local) =>
        (Vector3)body.AbsOrigin! + Vector3.Transform(local, Rotation(body));

    private const float SegmentSpan = 4;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Overstretched(uint parent, uint child)
    {
        try
        {
            if (Resolve(parent) is not { } a || Resolve(child) is not { } b) return 1;
            var ends = woundEnds[child];
            // Allow solver play as the two folded segments straighten on landing.
            const float tearDistance = 2 * SegmentSpan + 3;
            return Vector3.DistanceSquared(WorldPoint(a, ends.Parent), WorldPoint(b, ends.Child))
                > tearDistance * tearDistance ? 1 : 0;
        }
        catch (Exception ex) { Log?.LogError(ex, "Checking ligament on {Child}", child); return 1; }
    }

    private static uint Strand(Vector3 start, Vector3 end, Vector3 velocity)
    {
        var d = end - start;
        var angles = new Vector3(MathF.Atan2(-d.Z, MathF.Sqrt(d.X*d.X + d.Y*d.Y)), MathF.Atan2(d.Y, d.X), 0);
        return SpawnProp(LigamentModel, (start + end) * .5f, angles * (180 / MathF.PI), velocity,
            Color.FromArgb(172, 67, 71));
    }

    // 1 = built, 2 = already stretched beyond reach, 0 = failed.
    // The native caller owns every handle as soon as it is written, including on failure.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CreateLigament(uint parent, uint child, LigamentHandles* chain)
    {
        try
        {
            if (Resolve(parent) is not { } a || Resolve(child) is not { } b) return 0;
            var anchors = woundEnds[child];
            var start = WorldPoint(a, anchors.Parent);
            var end = WorldPoint(b, anchors.Child);
            var delta = end - start;
            float distance = delta.Length();
            if (distance > 2 * SegmentSpan) return 2;
            var axis = distance > .001f ? delta / distance : Vector3.UnitX;
            var down = Math.Abs(axis.Z) < .95f ? -Vector3.UnitZ : Vector3.UnitY;
            var bend = Vector3.Normalize(down - Vector3.Dot(down, axis) * axis);
            var middle = (start + end) * .5f + bend * MathF.Sqrt(SegmentSpan*SegmentSpan - distance*distance*.25f);
            Vector3 va = (Vector3)a.AbsVelocity, vb = (Vector3)b.AbsVelocity;
            chain->FirstNode = Strand(start, middle, Vector3.Lerp(va, vb, .25f));
            if (chain->FirstNode == 0) return 0;
            chain->SecondNode = Strand(middle, end, Vector3.Lerp(va, vb, .75f));
            if (chain->SecondNode == 0) return 0;
            chain->Upper = Constraint(parent, chain->FirstNode, start, true);
            if (chain->Upper == 0) return 0;
            chain->Middle = Constraint(chain->FirstNode, chain->SecondNode, middle, true);
            if (chain->Middle == 0) return 0;
            chain->Lower = Constraint(chain->SecondNode, child, end, true);
            return chain->Lower == 0 ? 0 : 1;
        }
        catch (Exception ex) { Log?.LogError(ex, "Creating ligament on {Child}", child); return 0; }
    }
}
