bl_info = {
    "name": "Smoothie - Cyberpunk 2077 World Editor",
    "author": "sprt_",
    "version": (0, 1, 0),
    "blender": (5, 0, 0),
    "location": "View3D > Sidebar > C# Bridge",
    "description": "World Editing Plugin for Cyberpunk 2077. Directly integrates into WolvenKit.",
    "category": "Development",
}

import bpy
import subprocess
import sys
import os

# --- Paths -------------------------------------------------------------
# Everything pythonnet needs gets installed *inside the addon*, under a
# "vendor" subfolder, instead of Blender's (often unwritable) site-packages
# or the OS user-site folder (which Blender's embedded Python doesn't
# search by default). This keeps the addon self-contained and portable.

ADDON_DIR = os.path.dirname(os.path.abspath(__file__))
VENDOR_DIR = os.path.join(ADDON_DIR, "vendor")
LIB_DIR = os.path.join(ADDON_DIR, "lib")  # where the C# .dll(s) live
RUNTIME_CONFIG = os.path.join(LIB_DIR, "runtimeconfig.json")

def ensure_vendor_on_path():
    """Make sure the vendored packages folder is importable."""
    if os.path.isdir(VENDOR_DIR) and VENDOR_DIR not in sys.path:
        sys.path.insert(0, VENDOR_DIR)

def get_blender_python():
    """Path to Blender's own bundled Python interpreter."""
    return sys.executable

def ensure_pythonnet():
    """Install pythonnet into the addon's vendor folder if not already present."""
    ensure_vendor_on_path()
    try:
        import clr  # noqa: F401
        return True, "pythonnet already installed"
    except ImportError:
        pass

    python_exe = get_blender_python()
    os.makedirs(VENDOR_DIR, exist_ok=True)
    try:
        subprocess.check_call([
            python_exe, "-m", "pip", "install",
            "--target", VENDOR_DIR,
            "pythonnet",
        ])
        ensure_vendor_on_path()
        return True, "pythonnet installed successfully into addon/vendor"
    except subprocess.CalledProcessError as e:
        return False, f"pip install failed: {e}"

def load_clr():
    """Loads the clr with the runtime config."""
    try:
        from pythonnet import load
        load("coreclr", runtime_config=RUNTIME_CONFIG)
        return True
    except ImportError:
        pass

    return False

positions = []
id_to_index = {}
free_indices = []

def add_empty(id: str, position):
    if free_indices:
        index = free_indices.pop()
        positions[index] = position
    else:
        index = len(positions)
        positions.append(position)

    id_to_index[id] = index

def remove_empty(id: str):
    index = id_to_index.pop(id, None)
    if index is None:
        return

    positions[index] = Vector((-10000, -10000, -10000))
    free_indices.append(index)

import bpy
import gpu
from gpu_extras.batch import batch_for_shader

shader = gpu.shader.from_builtin("UNIFORM_COLOR")

batch = None

def draw():
    if batch is None:
        return

    shader.bind()
    shader.uniform_float("color", (0, 1, 0, 0.5))
    batch.draw(shader)


handle = bpy.types.SpaceView3D.draw_handler_add(
    draw,
    (),
    'WINDOW',
    'POST_VIEW'
)

def apply_points():
    global batch

    batch = batch_for_shader(
        shader,
        "POINTS",
        {"pos": positions}
    )

    for area in bpy.context.screen.areas:
        if area.type == 'VIEW_3D':
            area.tag_redraw()

ensure_vendor_on_path()
if not load_clr():
    python_net_success, msg = ensure_pythonnet()

    if not python_net_success:
        raise RuntimeError(msg)

    if not load_clr():
        raise RuntimeError("Failed to load clr")

import clr

if LIB_DIR not in sys.path:
    sys.path.append(LIB_DIR)

clr.AddReference("SmoothieBackend")

from SmoothieBackend.API import BlenderAddonAPI
from mathutils import Vector
BlenderAddonAPI.Initialize()

BlenderAddonAPI.StartStreaming()

def on_streaming_tick():
    """
    for area in bpy.context.screen.areas:
    if area.type == 'VIEW_3D':
        region_3d = area.spaces.active.region_3d

        location = region_3d.view_matrix.inverted().translation
        BlenderAddonAPI.OnStreamingTick(location.x, location.y, location.z)
        break
    :return:
    """

    cube = bpy.data.objects["Cube"]
    BlenderAddonAPI.OnStreamingTick(cube.location.x, cube.location.y, cube.location.z)

    return 1 / 3

bpy.app.timers.register(on_streaming_tick, persistent=True)

def node_id_to_string(node_id):
    return "{} {}".format(node_id.ParentSector, str(node_id.Index))

def on_apply_streamed_changes_tick():
    for new_node in BlenderAddonAPI.GetLoadNodesQueue(6000):
        add_empty(node_id_to_string(new_node.Id), Vector((new_node.Position.Center.X, new_node.Position.Center.Y, new_node.Position.Center.Z)))

    for removed_node in BlenderAddonAPI.GetUnloadNodesQueue(6000):
        remove_empty(node_id_to_string(removed_node))

    apply_points()

    return 1 / 60

bpy.app.timers.register(on_apply_streamed_changes_tick, persistent=True)

def register():
    print("Registering Smoothie World Editor")


def unregister():
    print("Unregistering Smoothie World Editor")


if __name__ == "__main__":
    register()