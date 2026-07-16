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
BlenderAddonAPI.Initialize()

def on_streaming_tick():
    for area in bpy.context.screen.areas:
        if area.type == 'VIEW_3D':
            region_3d = area.spaces.active.region_3d

            location = region_3d.view_matrix.inverted().translation
            BlenderAddonAPI.OnStreamingTick(location.x, location.y, location.z)
            return 1 / 3
    return 1 / 3

bpy.app.timers.register(on_streaming_tick, persistent=True)

def register():
    print("Registering Smoothie World Editor")


def unregister():
    print("Unregistering Smoothie World Editor")


if __name__ == "__main__":
    register()