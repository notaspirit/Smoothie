bl_info = {
    "name": "Smoothie - Cyberpunk 2077 World Editor",
    "author": "",
    "version": (0, 1, 0),
    "blender": (5, 0, 0),
    "location": "View3D > Sidebar > Smoothie",
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
    # If clr is already importable, the runtime was already loaded in this
    # process (e.g. addon disable/enable cycle) - coreclr can only be
    # initialized once per process, so just confirm it's usable.
    try:
        from pythonnet import load
        load("coreclr", runtime_config=RUNTIME_CONFIG)
        import clr  # noqa: F401
        return True
    except ImportError:
        return False
    except Exception as e:
        print(f"[Smoothie] Failed to load CoreCLR runtime: {e}")
        return False


def get_or_create_collection(name, hide=False):
    """Get (or create) a top-level collection, and set its hide flags.

    Collection-level hide_viewport/hide_render let us hide an entire group
    of objects (e.g. the master mesh library) independently of any other
    collection, without touching individual objects.
    """
    coll = bpy.data.collections.get(name)
    if coll is None:
        coll = bpy.data.collections.new(name)
        bpy.context.scene.collection.children.link(coll)
    coll.hide_viewport = hide
    coll.hide_render = hide
    return coll

import numpy as np

def build_mesh_from_backend(mesh, backend_mesh):
    mesh.clear_geometry()

    verts = np.asarray(backend_mesh.Vertices, dtype=np.float32)
    indices = np.asarray(backend_mesh.Indices, dtype=np.uint32)

    num_verts = len(verts) // 3
    num_loops = len(indices)
    num_tris = num_loops // 3

    mesh.vertices.add(num_verts)
    mesh.vertices.foreach_set("co", verts)

    mesh.loops.add(num_loops)
    mesh.loops.foreach_set("vertex_index", indices)

    mesh.polygons.add(num_tris)
    loop_start = np.arange(0, num_loops, 3, dtype=np.int32)
    loop_total = np.full(num_tris, 3, dtype=np.int32)
    mesh.polygons.foreach_set("loop_start", loop_start)
    mesh.polygons.foreach_set("loop_total", loop_total)

    # calc_edges=True is required here: since we built loops/polygons
    # manually (no edges array), Blender needs to derive edge data itself,
    # otherwise the mesh has faces but no edges (breaks edit mode, many
    # modifiers, etc).
    mesh.update(calc_edges=True)
    # Defensive: streamed data can be malformed; validate() will silently
    # repair/report issues instead of Blender crashing on bad geometry.
    mesh.validate(verbose=False)

import random

def assign_random_color_material(mesh, name):
    mat = bpy.data.materials.get(name)
    if mat is None:
        mat = bpy.data.materials.new(name)
        mat.use_nodes = True

    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = (
        random.random(), random.random(), random.random(), 1.0
    )

    mat.diffuse_color = bsdf.inputs["Base Color"].default_value

    mesh.materials.clear()
    mesh.materials.append(mat)

MESH_LIBRARY_COLLECTION = "MasterStreamingInstances"


def create_master_mesh_object(name):
    coll = get_or_create_collection(MESH_LIBRARY_COLLECTION, hide=True)

    if coll.objects.get(name) is not None:
        return coll.objects[name]

    mesh = bpy.data.meshes.new(name + "_mesh")
    obj = bpy.data.objects.new(name, mesh)
    coll.objects.link(obj)
    return obj

def stream_in_mesh(backend_mesh):
    obj = create_master_mesh_object(backend_mesh.Path)
    build_mesh_from_backend(obj.data, backend_mesh)
    # assign_random_color_material(obj.data, backend_mesh.Path)

def stream_out_mesh(mesh_path: str):
    coll = get_or_create_collection(MESH_LIBRARY_COLLECTION, hide=True)
    obj = coll.objects.get(mesh_path)
    if obj is not None:
        bpy.data.objects.remove(obj, do_unlink=True)

STREAMED_INSTANCES_COLLECTION = "StreamedInstances"

def create_instance_object(name: str):
    coll = get_or_create_collection(STREAMED_INSTANCES_COLLECTION, hide=False)

    placeholder = bpy.data.meshes.new(name + "_mesh")
    obj = bpy.data.objects.new(name, placeholder)
    obj.hide_viewport = True
    obj.hide_render = True
    coll.objects.link(obj)

    return obj

def add_node(id: str, position, rotation_euler, scale, path: str):
    obj = create_instance_object(id)

    master_obj = bpy.data.objects.get(path)
    if master_obj is not None:
        old_mesh = obj.data
        obj.data = master_obj.data
        # The placeholder mesh created in create_instance_object is now an
        # orphan (0 users) - clean it up instead of leaking a datablock
        # every time a node streams in.
        if old_mesh.users == 0:
            bpy.data.meshes.remove(old_mesh)
    else:
        print(f"[Smoothie] Warning: master mesh '{path}' not found for node '{id}'; using placeholder.")

    obj.location = position
    obj.rotation_euler = rotation_euler
    obj.scale = scale
    obj.hide_viewport = False
    obj.hide_render = False


def remove_node(id: str):
    # NOTE: must match the hide state used by create_instance_object,
    # otherwise removing a single node would hide the whole collection.
    coll = get_or_create_collection(STREAMED_INSTANCES_COLLECTION, hide=False)
    obj = coll.objects.get(id)
    if obj is not None:
        bpy.data.objects.remove(obj, do_unlink=True)1

STREAMING_REFERENCES_COLLECTION = "StreamingReferences"

def add_empty(id: str, position):
    coll = get_or_create_collection(STREAMING_REFERENCES_COLLECTION, hide=False)
    empty = bpy.data.objects.new(id, None)
    empty.empty_display_type = 'PLAIN_AXES'
    coll.objects.link(empty)

    empty.location = position

def remove_empty(id: str):
    obj = bpy.data.objects.get(id)
    if obj is not None:
        bpy.data.objects.remove(obj, do_unlink=True)

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
# SharpDX.Vector3 is used by the streaming operator below - add the
# reference and import it here (module load time) rather than inside the
# operator's execute(), so a missing assembly fails fast at addon
# registration instead of only when the user clicks the button.
clr.AddReference("SharpDX")

from SmoothieBackend.API import BlenderAddonAPI
from SharpDX import Vector3
from mathutils import Vector, Euler
def check_and_apply_streaming_changes():
    changes = BlenderAddonAPI.GetStreamResult()
    if changes is None:
        return 1

    for removed_mesh in changes.RemovedMeshes:
        stream_out_mesh(removed_mesh)

    for added_mesh in changes.AddedMeshes:
        stream_in_mesh(added_mesh)

    for removed_node in changes.RemovedNodes:
        remove_node(removed_node.ToString())

    for new_node in changes.AddedNodes:
        if not new_node.MeshPath:
            continue

        add_node(new_node.Id.ToString(),
                 Vector((new_node.Position.Center.X, new_node.Position.Center.Y, new_node.Position.Center.Z)),
                 Euler((new_node.Rotation.Pitch, new_node.Rotation.Roll, new_node.Rotation.Yaw)),
                 Vector((new_node.Scale.X, new_node.Scale.Y, new_node.Scale.Z)),
                 new_node.MeshPath)

    return None


class SMOOTHIE_OT_queue_streaming_update(bpy.types.Operator):
    bl_idname = "smoothie.queue_streaming_update"
    bl_label = "Update Streaming With New Refs"

    def execute(self, context):
        try:
            ref_coll = bpy.data.collections.get(STREAMING_REFERENCES_COLLECTION)
            if ref_coll is None or len(ref_coll.objects) == 0:
                self.report({'ERROR'}, "No streaming reference point found")
                return {'CANCELLED'}

            ref_point_location = ref_coll.objects[0].location

            BlenderAddonAPI.StreamInBackground(Vector3(ref_point_location.x, ref_point_location.y, ref_point_location.z))
            bpy.app.timers.register(check_and_apply_streaming_changes, first_interval=1)
            self.report({'INFO'}, f"Streaming world around {ref_point_location}")
        except Exception as e:
            self.report({'ERROR'}, f"Failed to queue streaming update: {e}")
            return {'CANCELLED'}
        return {'FINISHED'}
class SMOOTHIE_PT_panel(bpy.types.Panel):
    bl_label = "Smoothie"
    bl_idname = "SMOOTHIE_PT_panel"
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = "Smoothie"

    def draw(self, context):
        layout = self.layout

        layout.operator("smoothie.queue_streaming_update", icon='PLAY')

classes = (
    SMOOTHIE_PT_panel,
    SMOOTHIE_OT_queue_streaming_update
)

def init_streaming():
    BlenderAddonAPI.Initialize()

    get_or_create_collection(MESH_LIBRARY_COLLECTION, hide=True)
    get_or_create_collection(STREAMED_INSTANCES_COLLECTION, hide=False)
    ref_coll = get_or_create_collection(STREAMING_REFERENCES_COLLECTION, hide=False)

    # Avoid creating a duplicate "StreamingRef1.001" empty every time the
    # addon is registered/reloaded - only create it if it doesn't exist yet.
    if ref_coll.objects.get("StreamingRef1") is None:
        add_empty("StreamingRef1", Vector((0, 0, 0)))

def register():
    print("Registering Smoothie World Editor")
    for cls in classes:
        bpy.utils.register_class(cls)
    bpy.app.timers.register(init_streaming, first_interval=0.1)


def unregister():
    for cls in reversed(classes):
        bpy.utils.unregister_class(cls)
    print("Unregistering Smoothie World Editor")


if __name__ == "__main__":
    register()