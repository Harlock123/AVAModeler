#!/usr/bin/env python3
"""Draws three blueprints of a small rocket (rocket-side.png, rocket-front.png, rocket-top.png) with nothing but the
standard library, for modelling over as backdrops (samples/scripts/rocket-blueprints.lua sets them up).

Run it from anywhere: the images are written beside this script, the same every time. The drawings are true
orthographic views of one rocket, in metres, at 300 pixels a metre:

- the rocket stands on the ground (y = 0) along +Y, 2.1 m tall, its body 0.15 m in radius from y = 0.3 to 1.5, an
  elliptical nose up to 2.1, a tapered skirt and a nozzle below, and three fins at 0, 120 and 240 degrees round Y
  (measured from +X towards +Z);
- side (looking along -X: Z across, Y up) and front (looking along +Z: X across, Y up) are 1.2 m wide and run from
  y = -0.1 to 2.2, so their centres are at (0, 1.05); top (looking down: X across, Z up) is 1.2 m square round the
  origin.
"""
import math
import os
import sys

sys.dont_write_bytecode = True  # no __pycache__ beside the samples
from make_textures import write_png  # noqa: E402  (the PNG writer the texture samples use)

HERE = os.path.dirname(os.path.abspath(__file__))
SCALE = 300  # pixels a metre

PAPER = (0.93, 0.95, 0.98)
GRID = (0.80, 0.86, 0.94)
GRID_MAJOR = (0.66, 0.75, 0.88)
INK = (0.08, 0.16, 0.45)
CENTRE = (0.75, 0.25, 0.25)

# The rocket's profile, radius against height, from the nozzle's mouth to the nose's tip.
NOSE_BASE, NOSE_TIP, RADIUS = 1.5, 2.1, 0.15
PROFILE = [(0.09, 0.0), (0.05, 0.13), (0.11, 0.16), (RADIUS, 0.3), (RADIUS, NOSE_BASE)]
for i in range(1, 17):
    t = i / 16
    PROFILE.append((RADIUS * math.sqrt(max(0.0, 1 - t * t)), NOSE_BASE + t * (NOSE_TIP - NOSE_BASE)))
STAGE = 1.0  # a joint line round the body
# A fin in its own plane: (out from the axis, height), root along the body, swept tip.
FIN = [(RADIUS, 0.3), (0.42, 0.14), (0.42, 0.36), (RADIUS, 0.75)]
FIN_ANGLES = [0, 120, 240]
FIN_THICKNESS = 0.02


class Canvas:
    """A drawing in metres: u across from u0, v up to v1, inked with anti-aliased lines over paper and a grid."""

    def __init__(self, u0, u1, v0, v1):
        self.u0, self.v1 = u0, v1
        self.w = int(round((u1 - u0) * SCALE))
        self.h = int(round((v1 - v0) * SCALE))
        self.px = [[PAPER] * self.w for _ in range(self.h)]
        for k in range(int(math.floor(u0 * 10)), int(math.ceil(u1 * 10)) + 1):
            self.line((k / 10, v0), (k / 10, v1), 1.0, GRID_MAJOR if k % 5 == 0 else GRID)
        for k in range(int(math.floor(v0 * 10)), int(math.ceil(v1 * 10)) + 1):
            self.line((u0, k / 10), (u1, k / 10), 1.0, GRID_MAJOR if k % 5 == 0 else GRID)
        self.line((u0, v0), (u1, v0), 3, INK)
        self.line((u1, v0), (u1, v1), 3, INK)
        self.line((u1, v1), (u0, v1), 3, INK)
        self.line((u0, v1), (u0, v0), 3, INK)

    def to_px(self, p):
        return ((p[0] - self.u0) * SCALE, (self.v1 - p[1]) * SCALE)

    def line(self, a, b, width, colour, dash=None):
        """A line from a to b (metres), width in pixels; dash is a pattern of on and off lengths in pixels."""
        (x0, y0), (x1, y1) = self.to_px(a), self.to_px(b)
        dx, dy = x1 - x0, y1 - y0
        length = math.hypot(dx, dy)
        half = width / 2
        lo_x, hi_x = int(math.floor(min(x0, x1) - half - 1)), int(math.ceil(max(x0, x1) + half + 1))
        lo_y, hi_y = int(math.floor(min(y0, y1) - half - 1)), int(math.ceil(max(y0, y1) + half + 1))
        period = sum(dash) if dash else 0
        for y in range(max(0, lo_y), min(self.h, hi_y + 1)):
            for x in range(max(0, lo_x), min(self.w, hi_x + 1)):
                cx, cy = x + 0.5, y + 0.5
                t = 0.0 if length == 0 else max(0.0, min(1.0, ((cx - x0) * dx + (cy - y0) * dy) / (length * length)))
                d = math.hypot(cx - (x0 + dx * t), cy - (y0 + dy * t))
                cover = max(0.0, min(1.0, half + 0.5 - d))
                if cover <= 0:
                    continue
                if dash:
                    along, on = (t * length) % period, False
                    for k, seg in enumerate(dash):
                        if along < seg:
                            on = k % 2 == 0
                            break
                        along -= seg
                    if not on:
                        continue
                old = self.px[y][x]
                self.px[y][x] = tuple(o + (c - o) * cover for o, c in zip(old, colour))

    def poly(self, points, width=4.0, colour=INK, closed=False):
        pts = list(points) + ([points[0]] if closed else [])
        for a, b in zip(pts, pts[1:]):
            self.line(a, b, width, colour)

    def circle(self, centre, r, width=4.0, colour=INK, steps=96):
        self.poly([(centre[0] + r * math.cos(2 * math.pi * k / steps), centre[1] + r * math.sin(2 * math.pi * k / steps))
                   for k in range(steps)], width, colour, closed=True)

    def centre_line(self, a, b):
        self.line(a, b, 2.5, CENTRE, dash=(16, 5, 4, 5))

    def save(self, name):
        write_png(name, self.w, self.h, lambda x, y: self.px[y][x])


def elevation(across):
    """A side-on view, where a point at angle a (degrees round Y from +X towards +Z) at radius r is across(a) * r across."""
    c = Canvas(-0.6, 0.6, -0.1, 2.2)
    c.centre_line((0, -0.05), (0, 2.15))
    c.centre_line((-0.55, 0), (0.55, 0))
    # The body: its silhouette both sides, and the lines across where its outline turns.
    c.poly([(r, y) for r, y in PROFILE])
    c.poly([(-r, y) for r, y in PROFILE])
    for r, y in PROFILE[:5]:
        c.line((-r, y), (r, y), 3, INK)
    c.line((-RADIUS, STAGE), (RADIUS, STAGE), 3, INK)
    # The fins, each foreshortened by where it points; one end on is a thin strip.
    for angle in FIN_ANGLES:
        k = across(angle)
        if abs(k) < 0.05:
            half = FIN_THICKNESS / 2
            top = max(y for _, y in FIN)
            c.poly([(-half, 0.14), (half, 0.14), (half, top), (-half, top)], closed=True)
            continue
        c.poly([(k * r, y) for r, y in FIN], closed=True)
    return c


def plan():
    """The rocket from above: X across, Z up the picture."""
    c = Canvas(-0.6, 0.6, -0.6, 0.6)
    c.centre_line((-0.55, 0), (0.55, 0))
    c.centre_line((0, -0.55), (0, 0.55))
    c.circle((0, 0), RADIUS)
    c.circle((0, 0), 0.11, 3)
    c.circle((0, 0), 0.05, 3)
    tip = max(r for r, _ in FIN)
    for angle in FIN_ANGLES:
        a = math.radians(angle)
        d, n = (math.cos(a), math.sin(a)), (-math.sin(a), math.cos(a))
        h = FIN_THICKNESS / 2
        c.poly([(d[0] * r + n[0] * s * h, d[1] * r + n[1] * s * h) for r, s in ((RADIUS, -1), (tip, -1), (tip, 1), (RADIUS, 1))])
    return c


if __name__ == "__main__":
    # Side: looking along -X, Z across; a fin at angle a lies sin(a) across. Front: looking along +Z, X across: cos(a).
    elevation(lambda a: math.sin(math.radians(a))).save("rocket-side.png")
    elevation(lambda a: math.cos(math.radians(a))).save("rocket-front.png")
    plan().save("rocket-top.png")
