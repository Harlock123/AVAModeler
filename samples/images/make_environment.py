#!/usr/bin/env python3
"""Draws the sample environment, studio.hdr: a photographer's studio as a latitude-longitude Radiance picture.

A dim grey room with a darker floor, a big warm soft box to the front left, a cool strip light to the right, a
panel overhead and a faint warm bounce behind. The lights are many times brighter than white (that's what the
high dynamic range is for), so they light a scene through Layout's environment lighting and show as bright
highlights in reflections. Nothing is downloaded: it's all worked out here, with only the standard library, and
comes out the same every time.

Run it from anywhere: the picture is written beside this script. Use it in Layout's Scene Settings (Environment),
or from an agent with set_scene environment "studio.hdr" (copied into the working folder).
"""
import math
import os

HERE = os.path.dirname(os.path.abspath(__file__))
WIDTH, HEIGHT = 512, 256


def smooth(a, b, x):
    t = max(0.0, min(1.0, (x - a) / (b - a)))
    return t * t * (3 - 2 * t)


def normalized(v):
    n = math.sqrt(sum(c * c for c in v))
    return tuple(c / n for c in v)


def softbox(d, centre, half_width, half_height, up=(0.0, 1.0, 0.0)):
    """A rectangular light facing the middle of the room: how much of it a direction sees, with soft edges."""
    c = normalized(centre)
    right = normalized((up[1] * c[2] - up[2] * c[1], up[2] * c[0] - up[0] * c[2], up[0] * c[1] - up[1] * c[0]))
    top = (c[1] * right[2] - c[2] * right[1], c[2] * right[0] - c[0] * right[2], c[0] * right[1] - c[1] * right[0])
    along = sum(a * b for a, b in zip(d, c))
    if along <= 0:
        return 0.0
    x = sum(a * b for a, b in zip(d, right)) / along
    y = sum(a * b for a, b in zip(d, top)) / along
    return smooth(half_width * 1.15, half_width, abs(x)) * smooth(half_height * 1.15, half_height, abs(y))


def colour(d):
    x, y, z = d
    # The room: walls lighter toward the ceiling, a dark floor, a soft line where they meet.
    wall = 0.16 + 0.12 * max(0.0, y)
    floor = 0.07 + 0.03 * (1 - abs(y))
    t = smooth(-0.03, 0.03, y)
    r = floor + (wall - floor) * t
    g = r
    b = r * 1.06
    # The lights.
    key = softbox(d, (-0.9, 0.55, 0.7), 0.45, 0.6)
    strip = softbox(d, (1.0, 0.25, 0.35), 0.12, 0.8)
    top = softbox(d, (0.0, 1.0, 0.2), 0.7, 0.45, up=(0.0, 0.0, 1.0))
    bounce = softbox(d, (0.3, 0.2, -1.0), 0.8, 0.4)
    r += 7.0 * key + 2.6 * strip + 3.4 * top + 0.5 * bounce
    g += 6.6 * key + 2.9 * strip + 3.4 * top + 0.42 * bounce
    b += 6.0 * key + 3.4 * strip + 3.5 * top + 0.32 * bounce
    return r, g, b


def rgbe(r, g, b):
    v = max(r, g, b)
    if v < 1e-32:
        return (0, 0, 0, 0)
    m, e = math.frexp(v)
    scale = m * 256.0 / v
    return (int(r * scale), int(g * scale), int(b * scale), e + 128)


def encode_scanline(pixels):
    """A scanline in the newer run-length encoding: 2, 2, the width, then each channel's runs and literals."""
    out = bytearray([2, 2, WIDTH >> 8, WIDTH & 0xFF])
    for c in range(4):
        values = [p[c] for p in pixels]
        i = 0
        while i < WIDTH:
            run = 1
            while i + run < WIDTH and run < 127 and values[i + run] == values[i]:
                run += 1
            if run >= 4:
                out += bytes([128 + run, values[i]])
                i += run
                continue
            start = i
            i += 1
            while i < WIDTH and i - start < 128:
                if i + 3 < WIDTH and values[i] == values[i + 1] == values[i + 2] == values[i + 3]:
                    break
                i += 1
            out += bytes([i - start]) + bytes(values[start:i])
    return out


def main():
    data = bytearray(b"#?RADIANCE\n# AVA Modeler's sample studio environment (make_environment.py)\nFORMAT=32-bit_rle_rgbe\n\n")
    data += f"-Y {HEIGHT} +X {WIDTH}\n".encode("ascii")
    for row in range(HEIGHT):
        theta = (row + 0.5) / HEIGHT * math.pi
        pixels = []
        for col in range(WIDTH):
            # The middle column looks along +Z, a quarter to the right along +X, the top row straight up.
            phi = ((col + 0.5) / WIDTH - 0.5) * 2 * math.pi
            d = (math.sin(theta) * math.sin(phi), math.cos(theta), math.sin(theta) * math.cos(phi))
            pixels.append(rgbe(*colour(d)))
        data += encode_scanline(pixels)
    with open(os.path.join(HERE, "studio.hdr"), "wb") as f:
        f.write(bytes(data))


if __name__ == "__main__":
    main()
