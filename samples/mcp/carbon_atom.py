#!/usr/bin/env python3
"""A stylized carbon atom, built through AVA Modeler's MCP server as an AI agent would build it.

Six white protons and six grey neutrons packed in a nucleus, and six green electrons whizzing round it on three
crossed orbits: modeled in the Modeler, then staged, lit and animated in Layout, with nothing but MCP tool calls.

    python3 samples/mcp/carbon_atom.py            # headless: saves carbon.lwo, carbon.avascene and carbon.png here
    python3 samples/mcp/carbon_atom.py --live     # in the open app (Macros › Allow AI Agents on), to watch it build

Live, it works on the open object and scene, so it stops if the object already has work in it. Nothing is saved
live unless --save is given; press F2 for Layout and Play to see the electrons go round.

It needs only Python 3.8 or later: the few lines of MCP it needs (JSON-RPC over the server's stdin and stdout) are
below. --app gives the command that starts AVA Modeler; by default the Debug build in this repo, through dotnet.
"""
import argparse
import base64
import json
import math
import os
import shlex
import subprocess
import sys
import time

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
DEFAULT_APP = ["dotnet", os.path.join(REPO, "src", "AVAModeler", "bin", "Debug", "net10.0", "AVAModeler.dll")]


class Server:
    """AVA Modeler's MCP server over stdio: one request at a time, each answered before the next."""

    def __init__(self, command, pause):
        self.process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
        self.pause = pause
        self.next_id = 0
        info = self.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                           "clientInfo": {"name": "carbon-atom-sample", "version": "1"}})
        print(f"Connected to {info['serverInfo']['name']} {info['serverInfo'].get('version', '')}")
        self.send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    def send(self, message):
        self.process.stdin.write(json.dumps(message) + "\n")
        self.process.stdin.flush()

    def request(self, method, params):
        self.next_id += 1
        self.send({"jsonrpc": "2.0", "id": self.next_id, "method": method, "params": params})
        while True:
            line = self.process.stdout.readline()
            if not line:
                sys.exit("AVA Modeler's MCP server stopped. Is the app built (dotnet build AVAModeler.slnx)?")
            reply = json.loads(line)
            if reply.get("id") == self.next_id:
                if "error" in reply:
                    sys.exit(f"{method} failed: {reply['error'].get('message')}")
                return reply["result"]

    def call(self, tool, **arguments):
        """Calls a tool; stops with its message if it fails. Returns the reply's JSON (or its picture, for renders)."""
        time.sleep(self.pause)
        result = self.request("tools/call", {"name": tool, "arguments": arguments})
        text = next((c["text"] for c in result["content"] if c["type"] == "text"), "")
        if result.get("isError"):
            sys.exit(f"{tool} failed: {text}")
        image = next((c for c in result["content"] if c["type"] == "image"), None)
        if image:
            return base64.b64decode(image["data"])
        try:
            return json.loads(text)
        except ValueError:
            return text

    def close(self):
        self.process.stdin.close()
        self.process.wait(timeout=30)


def model(s):
    """The Modeler: the nucleus in layer 1 and an orbit (a ring and two electrons) in each of layers 2 to 4."""
    s.call("set_surface", name="Proton", color=[245, 245, 240], specular=0.5, glossiness=128, smoothing=True, assign=False)
    s.call("set_surface", name="Neutron", color=[120, 122, 128], specular=0.3, glossiness=64, smoothing=True, assign=False)
    s.call("set_surface", name="Electron", color=[60, 255, 90], luminosity=0.7, specular=0.6, glossiness=256, smoothing=True, assign=False)
    s.call("set_surface", name="Orbit", color=[150, 200, 255], luminosity=0.5, smoothing=True, double_sided=True, assign=False)

    # The nucleus: twelve balls on the corners of an icosahedron, so they pack evenly. Opposite corners get opposite
    # kinds, which mixes the protons and neutrons.
    phi = (1 + 5 ** 0.5) / 2
    corners = ([(0, a, b * phi) for a in (1, -1) for b in (1, -1)]
               + [(a, b * phi, 0) for a in (1, -1) for b in (1, -1)]
               + [(b * phi, 0, a) for a in (1, -1) for b in (1, -1)])
    spread = 0.513 / math.sqrt(1 + phi * phi)
    protons = {0, 3, 5, 6, 8, 11}
    s.call("set_layers", foreground=[1])
    for i, corner in enumerate(corners):
        s.call("run_tool", name="Ball", params={
            "Center": [round(c * spread, 4) for c in corner], "Radius": [0.21] * 3, "Sides": 32, "Segments": 16,
            "Surface": "Proton" if i in protons else "Neutron"})

    # Each orbit: a thin ring (a small circle lathed round Y) and an electron on each side of it.
    radius = 1.6
    for layer in (2, 3, 4):
        s.call("set_layers", foreground=[layer])
        circle = [[radius + 0.018 * math.cos(a * math.pi / 4), 0.018 * math.sin(a * math.pi / 4), 0] for a in range(8)]
        added = s.call("add_points", points=circle)["indices"]
        s.call("add_polygons", polygons=[list(range(added["first"], added["last"] + 1))], surface="Orbit")
        s.call("run_tool", name="Lathe", params={"Axis": "Y", "Sides": 96})
        for x in (radius, -radius):
            s.call("run_tool", name="Ball", params={"Center": [x, 0, 0], "Radius": [0.1] * 3, "Sides": 16, "Segments": 8,
                                                    "Surface": "Electron"})
    s.call("set_layers", foreground=[1, 2, 3, 4])


def stage(s):
    """Layout: the nucleus turning slowly, each orbit spinning in its own tilted plane, lights, camera and backdrop."""
    s.call("set_scene", first_frame=1, last_frame=120, fps=30, gradient=True, gradient_top=[12, 16, 40],
           gradient_bottom=[0, 0, 0], ambient_intensity=0.15, frame=1)

    s.call("add_object", layer=1, name="Nucleus")
    for frame, heading in ((1, 0), (120, 90)):
        s.call("set_key", item="Nucleus", channel="heading", frame=frame, value=heading, linear=True)
    s.call("set_end_behavior", item="Nucleus", channel="heading", post="repeat")

    # An orbit hangs from a Spin null that turns about its own Y axis, under a Tilt null that tips the plane: three
    # planes 120 degrees apart, each 62 degrees from level. Linear keys and a repeating end keep each spin steady.
    for k, (layer, period, phase) in enumerate(((2, 30, 0), (3, 24, 60), (4, 36, 120)), start=1):
        s.call("add_null", name=f"Tilt {k}", rotation=[(k - 1) * 120, 62, 0])
        s.call("add_null", name=f"Spin {k}", parent=f"Tilt {k}")
        s.call("add_object", layer=layer, name=f"Orbit {k}", parent=f"Spin {k}")
        for frame, heading in ((1, phase), (1 + period, phase + 360)):
            s.call("set_key", item=f"Spin {k}", channel="heading", frame=frame, value=heading, linear=True)
        s.call("set_end_behavior", item=f"Spin {k}", channel="heading", post="repeat")

    s.call("set_item", item="Light", intensity=0.9, color=[255, 245, 235])
    s.call("add_light", type="distant", name="Rim", rotation=[200, 25, 0], color=[120, 150, 255], intensity=0.5)
    s.call("set_transform", item="Camera", frame=1, position=[0, 1.6, -6.8], key=False)
    s.call("look_at", item="Camera", point=[0, 0, 0], frame=1, key=False)


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--live", action="store_true", help="build in the open app (Macros › Allow AI Agents on)")
    parser.add_argument("--save", action="store_true", help="live: also save the object, scene and a render")
    parser.add_argument("--out", default=".", help="the folder to save into (default: the current folder)")
    parser.add_argument("--pause", type=float, help="seconds between steps (default 0.6 live, 0 headless)")
    parser.add_argument("--app", help="the command that starts AVA Modeler (default: this repo's Debug build)")
    args = parser.parse_args()

    out = os.path.abspath(args.out)
    os.makedirs(out, exist_ok=True)
    command = shlex.split(args.app) if args.app else DEFAULT_APP
    command += ["--mcp", "--root", out] + (["--attach"] if args.live else [])
    pause = args.pause if args.pause is not None else (0.6 if args.live else 0)
    s = Server(command, pause)

    if args.live:
        doc = s.call("get_document")
        if doc["modified"] or any(layer["points"] for layer in doc["layers"]):
            sys.exit("The open object has work in it. Start a new one (File › New) and run this again.")
        s.call("new_scene")

    save = not args.live or args.save
    model(s)
    if save:
        s.call("save", path="carbon.lwo", overwrite=True)  # first, so the scene's objects come from this file
    stage(s)

    if save:
        s.call("save_scene", path="carbon.avascene", overwrite=True, embed_objects=False)  # it refers to carbon.lwo
        with open(os.path.join(out, "carbon.png"), "wb") as f:
            f.write(s.call("render_scene", frame=10, width=640, height=480, samples=4))
        print(f"Saved carbon.lwo, carbon.avascene and carbon.png in {out}")
    else:
        print("Built. Press F2 for Layout and Play to watch the electrons.")
    s.close()


if __name__ == "__main__":
    main()
