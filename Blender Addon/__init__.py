bl_info = {
    "name": "Smoothie - Cyberpunk 2077 World Editor",
    "author": "",
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


def get_or_create_mesh(name):
    name = name + "_mesh"
    obj = bpy.data.objects.get(name)
    if obj is None:
        mesh = bpy.data.meshes.new(name)
        obj = bpy.data.objects.new(name, mesh)
    return obj

def remove_mesh(name):
    name = name + "_mesh"
    return bpy.data.objects.get(name)

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

    mesh.update()

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


# --- Master mesh pool ---------------------------------------------------
# "MeshSource_i" objects are never rendered directly - they just hold the
# actual mesh geometry (streamed in/out by stream_in_mesh/stream_out_mesh)
# that real instance objects point their .data at. Kept in their own
# collection so they can be hidden as a group. The pool grows on demand
# (no precreated/fixed-size set of objects) and freed slots are reused.

MESH_LIBRARY_COLLECTION = "MeshLibrary"

mesh_path_lib_name_map = {}  # mesh_path -> lib_name (object name)
lib_free_indices = []        # lib_names available for reuse
_next_lib_index = 0


def init_mesh_pool():
    # Just ensure the (hidden) collection exists; objects are created
    # lazily as meshes actually stream in.
    get_or_create_collection(MESH_LIBRARY_COLLECTION, hide=True)


def _create_master_mesh_object():
    global _next_lib_index
    name = f"MeshSource_{_next_lib_index}"
    _next_lib_index += 1

    mesh = bpy.data.meshes.new(name + "_mesh")
    obj = bpy.data.objects.new(name, mesh)

    coll = get_or_create_collection(MESH_LIBRARY_COLLECTION, hide=True)
    coll.objects.link(obj)

    return name


def get_or_create_lib_name(mesh_path: str):
    try:
        return mesh_path_lib_name_map[mesh_path]
    except KeyError:
        if lib_free_indices:
            lib_name = lib_free_indices.pop()
        else:
            lib_name = _create_master_mesh_object()
        mesh_path_lib_name_map[mesh_path] = lib_name
        return lib_name


def stream_in_mesh(backend_mesh):
    lib_name = get_or_create_lib_name(backend_mesh.Path)
    if lib_name is None:
        return

    obj = bpy.data.objects[lib_name]
    build_mesh_from_backend(obj.data, backend_mesh)
    # assign_random_color_material(obj.data, backend_mesh.Path)


def stream_out_mesh(mesh_path: str):
    try:
        lib_name = mesh_path_lib_name_map[mesh_path]
    except KeyError:
        return

    obj = bpy.data.objects[lib_name]
    obj.data.clear_geometry()

    del mesh_path_lib_name_map[mesh_path]
    lib_free_indices.append(lib_name)


# --- Streamed node instances ---------------------------------------------
# Each streamed-in node becomes a real Blender object living in its own
# visible collection. Its .data is pointed at whichever MeshLibrary object
# currently backs its MeshPath, so it behaves like a linked duplicate: no
# geometry copy, and it updates automatically whenever the master mesh's
# geometry is rebuilt by stream_in_mesh/stream_out_mesh.

STREAMED_INSTANCES_COLLECTION = "StreamedInstances"

instance_objs = []   # pool of real instance objects, grown on demand
id_to_index = {}     # node id (string) -> index into instance_objs
free_indices = []    # indices into instance_objs that are free for reuse


def _create_instance_object():
    coll = get_or_create_collection(STREAMED_INSTANCES_COLLECTION, hide=False)

    index = len(instance_objs)
    name = f"NodeInstance_{index}"

    # Placeholder mesh so the object is type 'MESH'; its .data will be
    # replaced with a shared master mesh the first time it's used, and
    # only ever reused/reassigned after that (this placeholder is only
    # created once per pool growth).
    placeholder = bpy.data.meshes.new(name + "_mesh")
    obj = bpy.data.objects.new(name, placeholder)
    obj.hide_viewport = True
    obj.hide_render = True
    coll.objects.link(obj)

    instance_objs.append(obj)
    return index, obj


def _get_free_instance_object():
    if free_indices:
        index = free_indices.pop()
        return index, instance_objs[index]
    return _create_instance_object()


def add_node(id: str, position, rotation_euler, scale, lib_name: str):
    index, obj = _get_free_instance_object()
    id_to_index[id] = index

    master_obj = bpy.data.objects.get(lib_name)
    if master_obj is not None:
        obj.data = master_obj.data

    obj.location = position
    obj.rotation_euler = rotation_euler
    obj.scale = scale
    obj.hide_viewport = False
    obj.hide_render = False


def remove_node(id: str):
    index = id_to_index.pop(id, None)
    if index is None:
        return

    obj = instance_objs[index]
    obj.hide_viewport = True
    obj.hide_render = True
    free_indices.append(index)


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
from mathutils import Vector, Euler

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

def node_id_to_string(node_id):
    return "{} {}".format(node_id.ParentSector, str(node_id.Index))

import time

def on_apply_streamed_changes_tick():
    for new_node in BlenderAddonAPI.GetLoadNodesQueue(1000000):
        if not new_node.MeshPath:
            continue

        lib_name = get_or_create_lib_name(new_node.MeshPath)
        if lib_name is None:
            continue

        add_node(node_id_to_string(new_node.Id),
                 Vector((new_node.Position.Center.X, new_node.Position.Center.Y, new_node.Position.Center.Z)),
                 Euler((new_node.Rotation.Pitch, new_node.Rotation.Roll, new_node.Rotation.Yaw)),
                 Vector((new_node.Scale.X, new_node.Scale.Y, new_node.Scale.Z)),
                 lib_name)

    for removed_node in BlenderAddonAPI.GetUnloadNodesQueue(1000000):
        remove_node(node_id_to_string(removed_node))

    for new_mesh in BlenderAddonAPI.GetLoadMeshesQueue(1000000):
        stream_in_mesh(new_mesh)

    for removed_mesh in BlenderAddonAPI.GetUnloadMeshesQueue(1000000):
        stream_out_mesh(removed_mesh.Path)

    return 10

def init_streaming():
    BlenderAddonAPI.Initialize()
    init_mesh_pool()

    BlenderAddonAPI.StartStreaming()

    bpy.app.timers.register(on_streaming_tick, persistent=True)
    bpy.app.timers.register(on_apply_streamed_changes_tick, persistent=True)

def register():
    print("Registering Smoothie World Editor")
    bpy.app.timers.register(init_streaming, first_interval=0.1)


def unregister():
    print("Unregistering Smoothie World Editor")


if __name__ == "__main__":
    register()