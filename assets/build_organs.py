"""Procedural stylized porcine props. All dimensions are in Source units."""
from math import cos, sin, pi
from pathlib import Path
import sys

import numpy as np
from build_models import HEADER, SMD, emit


def shell(lines, surface, rings, sides):
    def vertex(t, a):
        p = np.array(surface(t, a))
        dt = np.subtract(surface(t + .0001, a), surface(t - .0001, a))
        da = np.subtract(surface(t, a + .0001), surface(t, a - .0001))
        n = np.cross(da, dt)
        return np.r_[p, n / max(np.linalg.norm(n), 1e-10), a / (2*pi), t]
    rows = [[vertex(t, 2*pi*j/sides) for j in range(sides)]
            for t in np.linspace(.0001, .9999, rings+1)]
    for low, high in zip(rows, rows[1:]):
        for j in range(sides):
            k = (j+1) % sides
            emit(lines, "organ", [low[j], low[k], high[k]])
            emit(lines, "organ", [low[j], high[k], high[j]])
    for row, reverse in ((rows[0], True), (rows[-1], False)):
        for j in range(1, sides-1):
            tri = [row[0], row[j], row[j+1]]
            emit(lines, "organ", tri[::-1] if reverse else tri)


def liver(lines):
    for x, length, width, tilt in [(-2.2, 3.1, 1.45, -.35), (0, 3.9, 2, .12), (2.2, 2.9, 1.45, .42)]:
        def lobe(t, a):
            lat = (t-.5)*pi
            bulge = cos(lat)*(1-.24*sin(lat))
            return (x + width*bulge*cos(a) + tilt*sin(lat),
                    length*sin(lat), -1.45*bulge*sin(a) + .55*sin(lat)**2)
        shell(lines, lobe, 14, 24)


def ligament(lines):
    # Both ends overlap their anchors slightly so a flexed joint stays connected.
    def strand(t, a):
        radius = (.42 + .16*cos(2*pi*t)) * (1 + .12*cos(3*a + 2*pi*t))
        return (4.8*(t-.5), radius*cos(a), radius*sin(a))
    shell(lines, strand, 8, 10)


def build(content):
    folder = "models/happy_wheel_ragdoll/organs"
    dest = content / folder
    dest.mkdir(parents=True, exist_ok=True)
    for name, shape, mass in [("porcine_liver", liver, 1.4), ("ligament", ligament, .6)]:
        lines = []
        shape(lines)
        (dest / f"{name}.smd").write_text(SMD + "\n".join(lines) + "\nend\n")
        path = f"{folder}/{name}.smd"
        (dest / f"{name}.vmdl").write_text(HEADER + f"""
{{ rootNode = {{ _class = "RootNode" children = [
    {{ _class = "RenderMeshList" children = [ {{ _class = "RenderMeshFile" filename = "{path}" }} ] }},
    {{ _class = "MaterialGroupList" children = [ {{ _class = "DefaultMaterialGroup" remaps = [ {{ from = "organ.vmat" to = "materials/dev/reflectivity_50.vmat" }} ] }} ] }},
    {{ _class = "PhysicsShapeList" children = [ {{ _class = "PhysicsHullFile" filename = "{path}" name = "{name}" surface_prop = "flesh" collision_prop = "foliage_no_collision_pass_bullets" }} ] }},
    {{ _class = "PhysicsBodyMarkupList" children = [ {{ _class = "PhysicsBodyMarkup" target_body = "" mass_override = {mass} }} ] }}
] }} }}
""")


if __name__ == "__main__":
    build(Path(sys.argv[1]))
