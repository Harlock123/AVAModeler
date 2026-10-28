#!/usr/bin/env python3
"""Draws the sample textures (crate.png, planet.png, tiles.png) with nothing but the standard library.

Run it from anywhere: the images are written beside this script. They're procedural, so they come out the same
every time.
"""
import math
import os
import struct
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))


def write_png(name, width, height, pixel):
    """Writes an RGB PNG; pixel(x, y) gives (r, g, b) from 0 to 1, top row first."""
    rows = bytearray()
    for y in range(height):
        rows.append(0)
        for x in range(width):
            r, g, b = pixel(x, y)
            rows += bytes(max(0, min(255, int(round(c * 255)))) for c in (r, g, b))

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(bytes(rows), 9)) + chunk(b"IEND", b"")
    with open(os.path.join(HERE, name), "wb") as f:
        f.write(png)


def hash01(*n):
    """A repeatable pseudo-random number in [0, 1) for some integers."""
    h = 2166136261
    for v in n:
        h = ((h ^ (v & 0xFFFFFFFF)) * 16777619) & 0xFFFFFFFF
    h ^= h >> 13
    h = (h * 0x5BD1E995) & 0xFFFFFFFF
    h ^= h >> 15
    return h / 4294967296.0


def smooth(t):
    return t * t * (3 - 2 * t)


def noise(x, y, period_x, seed):
    """Value noise that tiles across x every period_x cells."""
    x0, y0 = math.floor(x), math.floor(y)
    fx, fy = smooth(x - x0), smooth(y - y0)

    def v(i, j):
        return hash01(i % period_x, j, seed)

    a = v(x0, y0) + (v(x0 + 1, y0) - v(x0, y0)) * fx
    b = v(x0, y0 + 1) + (v(x0 + 1, y0 + 1) - v(x0, y0 + 1)) * fx
    return a + (b - a) * fy


def fractal(x, y, period_x, seed, octaves=5):
    total, amp, norm = 0.0, 1.0, 0.0
    for o in range(octaves):
        total += noise(x, y, period_x, seed + o) * amp
        norm += amp
        x, y, period_x, amp = x * 2, y * 2, period_x * 2, amp * 0.5
    return total / norm


def lerp(a, b, t):
    return tuple(p + (q - p) * t for p, q in zip(a, b))


def crate(x, y):
    """A wooden crate's face: a frame of boards round the edge, a diagonal brace, planks behind, nails."""
    size = 256
    u, v = x / size, y / size
    border = 0.12
    # Grain runs along the boards: stretched noise, its contrast raised.
    grain = max(0.0, min(1.0, (fractal(u * 3, v * 40, 3, 7, 4) - 0.5) * 2.2 + 0.5))
    light = (0.78, 0.58, 0.33)
    dark = (0.52, 0.35, 0.17)
    on_frame = u < border or u > 1 - border or v < border or v > 1 - border
    on_brace = abs((u - v)) < border * 0.75 and not on_frame
    if on_frame or on_brace:
        if on_brace:
            grain = max(0.0, min(1.0, (fractal((u + v) * 20, (u - v) * 2, 40, 11, 4) - 0.5) * 2.2 + 0.5))
        c = lerp(dark, light, 0.55 + 0.45 * grain)
        edge = min(u, v, 1 - u, 1 - v)
        # A dark line where the frame meets the planks, and round the brace.
        if on_frame and abs(edge - border) < 0.008 or on_brace and abs(abs(u - v) - border * 0.75) < 0.01:
            c = tuple(p * 0.45 for p in c)
        # Nails at the frame's corners.
        for nx, ny in ((border / 2, border / 2), (1 - border / 2, border / 2), (border / 2, 1 - border / 2), (1 - border / 2, 1 - border / 2)):
            if (u - nx) ** 2 + (v - ny) ** 2 < 0.012 ** 2:
                c = (0.25, 0.25, 0.27)
        return c
    # Planks across, each its own shade, with gaps between.
    plank = int(v * 5)
    shade = 0.75 + 0.25 * hash01(plank, 3)
    gap = abs(v * 5 - round(v * 5)) < 0.03
    c = lerp(dark, light, (0.35 + 0.5 * grain) * shade)
    return tuple(p * 0.35 for p in c) if gap else c


def planet(x, y):
    """An imaginary planet, as a latitude-longitude map: seas, land, mountains and ice caps."""
    w, h = 512, 256
    lon, lat = x / w, 0.5 - y / h
    e = fractal(lon * 8, (lat + 0.5) * 4, 8, 3, 6)
    ocean, shallow = (0.05, 0.16, 0.42), (0.12, 0.38, 0.62)
    grass, desert, rock = (0.20, 0.48, 0.18), (0.72, 0.62, 0.38), (0.45, 0.40, 0.36)
    sea_level = 0.52
    if e < sea_level:
        c = lerp(ocean, shallow, max(0.0, (e - 0.35) / (sea_level - 0.35)))
    else:
        t = (e - sea_level) / (1 - sea_level)
        dry = fractal(lon * 4 + 9, (lat + 0.5) * 2, 4, 21, 3)
        c = lerp(grass, desert, max(0.0, min(1.0, (dry - 0.45) * 3))) if t < 0.45 else lerp(grass, rock, min(1.0, (t - 0.45) * 2.5))
    ice = abs(lat) * 2 + (fractal(lon * 16, lat * 8, 16, 5, 3) - 0.5) * 0.25
    if ice > 0.82:
        c = lerp(c, (0.93, 0.95, 0.98), min(1.0, (ice - 0.82) * 10))
    return c


def tiles(x, y):
    """Floor tiles: two greys in a checker, with grout between and a little speckle."""
    size, n = 128, 4
    cell = size // n
    i, j = x // cell, y // cell
    fx, fy = x % cell, y % cell
    grout = fx < 2 or fy < 2
    if grout:
        return (0.30, 0.29, 0.27)
    base = (0.80, 0.78, 0.74) if (i + j) % 2 == 0 else (0.42, 0.44, 0.48)
    s = 0.92 + 0.08 * hash01(x, y, 9)
    return tuple(p * s for p in base)


if __name__ == "__main__":
    write_png("crate.png", 256, 256, crate)
    write_png("planet.png", 512, 256, planet)
    write_png("tiles.png", 128, 128, tiles)
