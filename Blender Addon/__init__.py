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


def ensure_vendor_on_path():
    """Make sure the vendored packages folder is importable."""
    if os.path.isdir(VENDOR_DIR) and VENDOR_DIR not in sys.path:
        sys.path.insert(0, VENDOR_DIR)


# Run this at module import time (i.e. as soon as Blender loads the addon),
# so `import clr` works anywhere else in the addon without extra wiring.
ensure_vendor_on_path()

ADDON_DIR = os.path.dirname(os.path.abspath(__file__))
RUNTIME_CONFIG = os.path.join(
    ADDON_DIR,
    "lib",
    "runtimeconfig.json"
)

from pythonnet import load
load("coreclr", runtime_config=RUNTIME_CONFIG)

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


def call_csharp_library(dll_path, namespace, class_name, method_name, *args):
    """
    Load a compiled C# assembly (.dll) and call a static method on it.
    Assumes the method signature matches the args you pass in.
    """
    ensure_vendor_on_path()
    import clr  # available only after ensure_pythonnet() has run

    dll_dir = os.path.dirname(dll_path)
    dll_name = os.path.splitext(os.path.basename(dll_path))[0]

    if dll_dir not in sys.path:
        sys.path.append(dll_dir)

    clr.AddReference(dll_name)  # loads MyLibrary.dll by its assembly name

    # Import the actual C# namespace as if it were a Python module
    module = __import__(namespace, fromlist=[class_name])
    csharp_class = getattr(module, class_name)
    method = getattr(csharp_class, method_name)

    initMethod = getattr(csharp_class, "Initialize")
    initMethod()

    return method(*args)


class CSBRIDGE_OT_install_pythonnet(bpy.types.Operator):
    bl_idname = "csbridge.install_pythonnet"
    bl_label = "Install pythonnet"

    def execute(self, context):
        ok, msg = ensure_pythonnet()
        (self.report({'INFO'}, msg) if ok else self.report({'ERROR'}, msg))
        return {'FINISHED'} if ok else {'CANCELLED'}


class CSBRIDGE_OT_run_csharp(bpy.types.Operator):
    bl_idname = "csbridge.run_csharp"
    bl_label = "Run C# Method"

    def execute(self, context):
        props = context.scene.csbridge_props
        try:
            result = call_csharp_library(
                dll_path=bpy.path.abspath(props.dll_path),
                namespace=props.namespace,
                class_name=props.class_name,
                method_name=props.method_name,
            )
            self.report({'INFO'}, f"Result: {result}")
        except Exception as e:
            self.report({'ERROR'}, f"Failed to call C# method: {e}")
            return {'CANCELLED'}
        return {'FINISHED'}


class CSBridgeProps(bpy.types.PropertyGroup):
    dll_path: bpy.props.StringProperty(
        name="DLL Path",
        subtype='FILE_PATH',
        default=os.path.join(LIB_DIR, "SmoothieBackend.dll"),
    )
    namespace: bpy.props.StringProperty(name="Namespace", default="SmoothieBackend.API")
    class_name: bpy.props.StringProperty(name="Class", default="BlenderAddonAPI")
    method_name: bpy.props.StringProperty(name="Method", default="GetInt")


class CSBRIDGE_PT_panel(bpy.types.Panel):
    bl_label = "Smoothie - Cyberpunk 2077 World Editor"
    bl_idname = "Smoothie_CP77_WE_PT_panel"
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = "Cyberpunk 2077 World Editor"

    def draw(self, context):
        layout = self.layout
        props = context.scene.csbridge_props

        layout.operator("csbridge.install_pythonnet", icon='IMPORT')
        layout.separator()
        layout.prop(props, "dll_path")
        layout.prop(props, "namespace")
        layout.prop(props, "class_name")
        layout.prop(props, "method_name")
        layout.operator("csbridge.run_csharp", icon='PLAY')


classes = (
    CSBridgeProps,
    CSBRIDGE_OT_install_pythonnet,
    CSBRIDGE_OT_run_csharp,
    CSBRIDGE_PT_panel,
)


def register():
    for cls in classes:
        bpy.utils.register_class(cls)
    bpy.types.Scene.csbridge_props = bpy.props.PointerProperty(type=CSBridgeProps)


def unregister():
    for cls in reversed(classes):
        bpy.utils.unregister_class(cls)
    del bpy.types.Scene.csbridge_props


if __name__ == "__main__":
    register()