bl_info = {
    "name": "Smoothie - Cyberpunk 2077 World Editor",
    "author": "sprt_",
    "version": (0, 1, 0),
    "blender": (5, 0, 0),
    "location": "View3D > Sidebar > C# Bridge",
    "description": "World Editing Plugin for Cyberpunk 2077. Directly integrates into WolvenKit.",
    "category": "Development",
}

import subprocess
import sys
import os
import bpy

from .services.logger import Logger
from .model import global_state as gs

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
        subprocess.check_call([
            python_exe, "-m", "pip", "install",
            "--target", VENDOR_DIR,
            "pillow",
        ])
        ensure_vendor_on_path()
        return True, "pythonnet and pillow installed successfully into addon/vendor"
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

# --- Initialize Package ---
gs.logger = Logger("logs")
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
clr.AddReference("SharpDX")

# --- Blender Registration ---

def register_keymaps():
    """Registers custom keymaps for the Smoothie addon."""
    from .ui.pt_panel import SMOOTHIE_OT_select_instance, SMOOTHIE_OT_instance_context_menu
    
    wm = bpy.context.window_manager
    kc = wm.keyconfigs.addon
    if kc is None:
        return

    km = kc.keymaps.new(name='Object Mode', space_type='EMPTY')

    kmi_select = km.keymap_items.new(
        SMOOTHIE_OT_select_instance.bl_idname, 'LEFTMOUSE', 'PRESS', alt=True
    )

    kmi_menu = km.keymap_items.new(
        SMOOTHIE_OT_instance_context_menu.bl_idname, 'RIGHTMOUSE', 'PRESS', alt=True
    )

    gs.addon_keymaps.append((km, kmi_select))
    gs.addon_keymaps.append((km, kmi_menu))

def unregister_keymaps():
    """Unregisters custom keymaps for the Smoothie addon."""
    for km, kmi in gs.addon_keymaps:
        km.keymap_items.remove(kmi)
    gs.addon_keymaps.clear()

def register():
    """Registers the Smoothie addon classes and handlers."""
    from .services.streaming import draw, draw_outline, init_streaming
    from .ui.pt_panel import SMOOTHIE_OT_select_instance # ensure ui is loaded to populate gs.blender_classes
    
    gs.logger.info("Registering Smoothie World Editor")
    
    for cls in gs.blender_classes:
        bpy.utils.register_class(cls)
    
    register_keymaps()
    
    gs.draw_handle_3d = bpy.types.SpaceView3D.draw_handler_add(
        draw, (), 'WINDOW', 'POST_VIEW'
    )
    gs.outline_handle_3d = bpy.types.SpaceView3D.draw_handler_add(
        draw_outline, (), 'WINDOW', 'POST_VIEW'
    )
    
    bpy.app.timers.register(init_streaming, first_interval=0.1)

def unregister():
    """Unregisters the Smoothie addon classes and handlers."""
    gs.logger.info("Unregistering Smoothie World Editor")
    
    unregister_keymaps()
    
    for cls in reversed(gs.blender_classes):
        bpy.utils.unregister_class(cls)
    
    if gs.draw_handle_3d is not None:
        bpy.types.SpaceView3D.draw_handler_remove(gs.draw_handle_3d, 'WINDOW')
        gs.draw_handle_3d = None
        
    if gs.outline_handle_3d is not None:
        bpy.types.SpaceView3D.draw_handler_remove(gs.outline_handle_3d, 'WINDOW')
        gs.outline_handle_3d = None

if __name__ == "__main__":
    register()