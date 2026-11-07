"""Build rigid CS2 agent pieces from VRF's skinned and material GLB exports."""
import json
import struct
import sys
from collections import defaultdict
from pathlib import Path

import numpy as np

PARTS = {
    "pelvis": ("pelvis", 12), "chest": ("spine_2", 22), "head": ("head_0", 5),
    "upper_arm_l": ("arm_upper_l", 2.5), "forearm_l": ("arm_lower_l", 1.8),
    "upper_arm_r": ("arm_upper_r", 2.5), "forearm_r": ("arm_lower_r", 1.8),
    "thigh_l": ("leg_upper_l", 8), "shin_l": ("leg_lower_l", 4),
    "thigh_r": ("leg_upper_r", 8), "shin_r": ("leg_lower_r", 4),
}
HEADER = '<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc36:version{972dada4-b828-45a4-bb93-7795cf0585da} -->'
SMD = 'version 1\nnodes\n0 "root" -1\nend\nskeleton\ntime 0\n0 0 0 0 0 0 0\nend\ntriangles\n'


class Glb:
    def __init__(self, path):
        data = path.read_bytes()
        size = struct.unpack_from("<I", data, 12)[0]
        self.doc = json.loads(data[20:20 + size])
        self.buffer = data[28 + size:]

    def array(self, index):
        a = self.doc["accessors"][index]
        v = self.doc["bufferViews"][a["bufferView"]]
        dtype = np.dtype({5121: "u1", 5123: "<u2", 5125: "<u4", 5126: "<f4"}[a["componentType"]])
        cols = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}[a["type"]]
        return np.ndarray((a["count"], cols), dtype, self.buffer,
                          v.get("byteOffset", 0) + a.get("byteOffset", 0),
                          (v.get("byteStride", cols * dtype.itemsize), dtype.itemsize))


def emit(out, material, tri):
    out.append(material)
    for v in tri:
        # ModelDoc rotates SMD geometry +90 degrees around Z on import.
        x, y, z, nx, ny, nz, u, t = v
        v = (y, -x, z, ny, -nx, nz, u, t)
        out.append("0 " + " ".join(f"{x:.7f}" for x in v))


def clip(poly, values):
    """Clip a triangle in barycentric coordinates to a weight-dominance plane."""
    result = []
    for a, b in zip(poly, [*poly[1:], poly[0]]):
        da, db = a @ values, b @ values
        if da >= 0:
            result.append(a)
        if (da > 0 > db) or (db > 0 > da):
            result.append(a + (b - a) * (da / (da - db)))
    return result


def bind_pose(exports, source):
    agent = source.split("/")[-1]
    mesh = Glb(exports / "bind-src" / f"{agent}.glb")
    skin = mesh.doc["skins"][0]
    names = [mesh.doc["nodes"][j]["name"].lower() for j in skin["joints"]]
    transforms = np.linalg.inv(mesh.array(skin["inverseBindMatrices"]).reshape(-1, 4, 4).transpose(0, 2, 1))
    # Restore Source world axes (forward, left, up); local piece axes match the SMD.
    def frame(bone):
        return transforms[names.index(bone)][[2, 0, 1]]
    pose = []
    for part, (bone, _) in PARTS.items():
        m = frame(bone)
        center = m[:, 3] / .0254
        angles = np.degrees([np.arctan2(-m[2, 0], np.hypot(m[0, 0], m[1, 0])),
                             np.arctan2(m[1, 0], m[0, 0]), np.arctan2(m[2, 1], m[2, 2])])
        pivot = frame({"chest": "spine_0", "head": "neck_0"}.get(part, bone))[:, 3] / .0254
        pose.append(np.r_[center, angles, pivot].round(5).tolist())
    return pose


def build(content, exports, source):
    agent = source.split("/")[-1]
    mesh = Glb(exports / "bind-src" / f"{agent}.glb")
    shaded = Glb(exports / "agent-export/agents/models" / f"{source}.glb")
    nodes = mesh.doc["nodes"]
    parents = {c: i for i, n in enumerate(nodes) for c in n.get("children", [])}
    names = list(PARTS)
    owners = {bone: names.index(p) for p, (bone, _) in PARTS.items()}
    owners.update(spine_0=0, spine_1=1, spine_3=1, neck_0=2)
    triangles = [[] for _ in names]
    seams = defaultdict(list)
    identity = np.eye(3)
    mats = {m["name"]: m["extras"]["vmat"]["Name"] for m in shaded.doc["materials"]}
    for n in nodes:
        if "mesh" not in n:
            continue
        skin = mesh.doc["skins"][n["skin"]]
        joints = skin["joints"]
        inverse = mesh.array(skin["inverseBindMatrices"]).reshape(-1, 4, 4).transpose(0, 2, 1)
        node_names = [nodes[j]["name"].lower() for j in joints]
        transforms = [inverse[node_names.index(bone)] for bone, _ in PARTS.values()]
        joint_parts = []
        for j in joints:
            while nodes[j]["name"].lower() not in owners and j in parents:
                j = parents[j]
            joint_parts.append(owners.get(nodes[j]["name"].lower(), 0))
        prims = mesh.doc["meshes"][n["mesh"]]["primitives"]
        other = shaded.doc["meshes"][n["mesh"]]["primitives"]
        for p, q in zip(prims, other, strict=True):
            ids = mesh.array(p["indices"]).reshape(-1, 3)
            if not np.array_equal(ids, shaded.array(q["indices"]).reshape(-1, 3)):
                raise ValueError("Material export has different topology")
            attrs = p["attributes"]
            positions = mesh.array(attrs["POSITION"])
            normals = mesh.array(attrs["NORMAL"])
            uv = mesh.array(attrs["TEXCOORD_0"]).copy()
            uv[:, 1] = 1 - uv[:, 1]
            weights = np.zeros((len(uv), len(names)))
            owner = np.take(joint_parts, mesh.array(attrs["JOINTS_0"]))
            np.add.at(weights, (np.arange(len(uv))[:, None], owner), mesh.array(attrs["WEIGHTS_0"]))
            local = [np.column_stack(((positions @ t[:3, :3].T + t[:3, 3]) / .0254,
                                      normals @ t[:3, :3].T, uv)) for t in transforms]
            material = shaded.doc["materials"][q["material"]]["name"]
            for face in ids:
                w = weights[face]
                active = np.flatnonzero(w.max(axis=0) > 0)
                winners = w.argmax(axis=1)
                if np.all(winners == winners[0]):
                    k = winners[0]
                    emit(triangles[k], material, local[k][face])
                    continue
                for k in active:
                    poly = list(identity)
                    for h in active:
                        if h != k and poly:
                            poly = clip(poly, w[:, k] - w[:, h])
                    if len(poly) < 3:
                        continue
                    verts = np.array(poly) @ local[k][face]
                    for i in range(1, len(poly)-1):
                        emit(triangles[k], material, verts[[0, i, i+1]])
                    for i, a in enumerate(poly):
                        b = poly[(i+1) % len(poly)]
                        for h in active:
                            if h != k and max(abs(a @ (w[:, k]-w[:, h])), abs(b @ (w[:, k]-w[:, h]))) < 1e-7:
                                seams[k, h].append((verts[i, :3], verts[(i+1) % len(poly), :3]))
                                break
    # Close cut surfaces using the same border vertices as the textured clothing.
    for (k, _), edges in seams.items():
        border = np.array(edges).reshape(-1, 3)
        center = border.mean(axis=0)
        # Project this ring onto its best-fit plane, preserving the texture aspect.
        _, axes = np.linalg.eigh((border-center).T @ (border-center))
        plane = axes[:, 1:]
        extent = max(np.abs((border-center) @ plane).max(), 1e-6)
        for a, b in edges:
            normal = np.cross(a-b, center-b)
            length = np.linalg.norm(normal)
            if length > 1e-6:
                emit(triangles[k], "cut", [np.r_[pt, normal/length, .5 + .47 * ((pt-center) @ plane) / extent] for pt in (b, a, center)])
    mats["cut"] = "materials/happy_wheel_ragdoll/cut.vmat"
    remaps = ", ".join(f'{{ from = "{k}.vmat" to = "{v}" }}' for k, v in mats.items())
    folder = f"models/happy_wheel_ragdoll/agents/{agent}"
    dest = content / folder
    dest.mkdir(parents=True, exist_ok=True)
    for part, lines in zip(names, triangles, strict=True):
        if not lines:
            raise ValueError(f"Empty part: {part}")
        (dest / f"{part}.smd").write_text(SMD + "\n".join(lines) + "\nend\n")
        path = f"{folder}/{part}.smd"
        model = HEADER + f'''
{{ rootNode = {{ _class = "RootNode" children = [
    {{ _class = "RenderMeshList" children = [ {{ _class = "RenderMeshFile" filename = "{path}" }} ] }},
    {{ _class = "MaterialGroupList" children = [ {{ _class = "DefaultMaterialGroup" remaps = [ {remaps} ] }} ] }},
    {{ _class = "PhysicsShapeList" children = [ {{ _class = "PhysicsHullFile" filename = "{path}" name = "{part}" surface_prop = "flesh" collision_prop = "foliage_no_collision_pass_bullets" }} ] }},
    {{ _class = "PhysicsBodyMarkupList" children = [ {{ _class = "PhysicsBodyMarkup" target_body = "" mass_override = {PARTS[part][1]} }} ] }}
] }} }}
'''
        (dest / f"{part}.vmdl").write_text(model)


if __name__ == "__main__":
    if sys.argv[1] == "--bind-poses":
        poses = [bind_pose(Path(sys.argv[2]), source) for source in sys.argv[4:]]
        Path(sys.argv[3]).write_text(json.dumps(poses, separators=(",", ":")))
        sys.exit(0)
    for agent in sys.argv[3:]:
        build(Path(sys.argv[1]), Path(sys.argv[2]), agent)
