# Track shape bench. Alt+P runs this. Edit the numbers, run again.
#
# Lives on disk rather than inside a .blend, and that is the point: as a text
# block it existed in LT_PARTS.blend alone, so it was invisible from every other
# scene and absent from git. Load it with Text -> Open in the Text Editor, from
# whichever scene you are working in.
#
#   1. run it as it stands -> a `<belt>.Preview` object appears and the belt hides
#   2. select the preview and drag it: S Y is the length, S Z is the height
#      (global axes are exact here - the track root turns exactly 90 degrees),
#      or type into the sidebar. What you see while dragging is a proposal: the
#      scale is stretching the tread too.
#   3. switch to TAKE below and run -> the drag becomes a real belt, shoes back
#      to their true size, ends back to true arcs, scale reset to 1
#   4. RESTORE removes the previews and unhides the belts
#
# The readout lands in the text block `track_shape_report`, so no system console
# is needed. `corner_px_along_up` is the pair to watch: equal means the ends are
# circles in the frame and not just in the mesh.

import sys, importlib, json
import bpy

D = r"D:\Projects\AgentCoding\BlenderMCP"
if D not in sys.path:
    sys.path.append(D)
import track_cycle
importlib.reload(track_cycle)

UPP = 0.006285          # the last render's units_per_pixel, for the readout only

STEP = "RESTORE"        # PREVIEW | TAKE | RESTORE

ASK = {
    "units_per_pixel": UPP,
    # None means "the shape the belt already has". In world units, which divide
    # by UPP into pixels: 1.0 world is 159.1 px.
    "length": None,
    "height": None,
    "corner": 1.0,      # 1.0 is a stadium: the ends follow the height on their own
    "in_world": True,   # round in the frame, and the two sizes in world units
    "keep": "ground",   # hold the bottom run, which is what the tank stands on
    # "belts": ("L.Caterpillar.Geometry",),   # one side only, to A/B them
}

if STEP == "TAKE":
    ASK["take_scale"] = True
elif STEP == "RESTORE":
    ASK = {"restore": True}

report = track_cycle.preview(ASK)

text = bpy.data.texts.get("track_shape_report") or \
    bpy.data.texts.new("track_shape_report")
text.clear()
text.write(json.dumps(report, indent=1, default=str))
print(json.dumps(report, indent=1, default=str))
