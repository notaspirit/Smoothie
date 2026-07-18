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
    mesh.validate()

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

def get_or_create_collection(name):
    coll = bpy.data.collections.get(name)
    if coll is None:
        coll = bpy.data.collections.new(name)
        bpy.context.scene.collection.children.link(coll)
    return coll

import uuid

point_cloud_obj = None

mesh_path_lib_name_map = {}
lib_free_indices = []

POOL_SIZE = 8000

def init_mesh_pool():
    coll = get_or_create_collection("MeshLibrary")
    for i in range(POOL_SIZE):
        name = f"MeshSource_{i}"
        if name not in bpy.data.objects:
            mesh = bpy.data.meshes.new(name + "_mesh")
            obj = bpy.data.objects.new(name, mesh)
            # obj.location = (1e6, 1e6, 1e6)
            coll.objects.link(obj)

        slot_id = uuid.uuid4()
        mesh_path_lib_name_map[slot_id] = name
        lib_free_indices.append(slot_id)

def init_point_cloud():
    name = "StreamedPoints"
    if name in bpy.data.objects:
        return bpy.data.objects[name]

    mesh = bpy.data.meshes.new(name + "_mesh")
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    return obj

def init_instancing_tree(points_obj):
    mod = points_obj.modifiers.new("Streamed Instances", 'NODES')
    tree = bpy.data.node_groups.new("StreamedInstancesTree", 'GeometryNodeTree')
    mod.node_group = tree

    tree.interface.new_socket(name="Geometry", in_out='INPUT', socket_type='NodeSocketGeometry')
    tree.interface.new_socket(name="Geometry", in_out='OUTPUT', socket_type='NodeSocketGeometry')

    nodes, links = tree.nodes, tree.links
    nodes.clear()

    group_in  = nodes.new("NodeGroupInput");  group_in.location  = (-900, 0)
    group_out = nodes.new("NodeGroupOutput"); group_out.location = (700, 0)

    # Reads all objects in MeshLibrary as a flat list of instance-able
    # geometries. Separate Children = True is what lets Pick Instance
    # address them individually by index, rather than treating the whole
    # collection as one blob.
    coll_info = nodes.new("GeometryNodeCollectionInfo")
    coll_info.inputs["Collection"].default_value = bpy.data.collections["MeshLibrary"]
    coll_info.transform_space = 'RELATIVE'
    coll_info.inputs["Separate Children"].default_value = True
    coll_info.location = (-900, -250)

    idx_attr = nodes.new("GeometryNodeInputNamedAttribute")
    idx_attr.data_type = 'INT'
    idx_attr.inputs["Name"].default_value = "mesh_index"
    idx_attr.location = (-900, -450)

    rot_attr = nodes.new("GeometryNodeInputNamedAttribute")
    rot_attr.data_type = 'FLOAT_VECTOR'
    rot_attr.inputs["Name"].default_value = "inst_rotation"
    rot_attr.location = (-900, -650)

    scale_attr = nodes.new("GeometryNodeInputNamedAttribute")
    scale_attr.data_type = 'FLOAT_VECTOR'
    scale_attr.inputs["Name"].default_value = "inst_scale"
    scale_attr.location = (-900, -850)

    instance = nodes.new("GeometryNodeInstanceOnPoints")
    instance.inputs["Pick Instance"].default_value = True
    instance.location = (0, 0)

    links.new(group_in.outputs["Geometry"], instance.inputs["Points"])
    links.new(coll_info.outputs["Instances"], instance.inputs["Instance"])
    links.new(idx_attr.outputs["Attribute"], instance.inputs["Instance Index"])
    links.new(rot_attr.outputs["Attribute"], instance.inputs["Rotation"])
    links.new(scale_attr.outputs["Attribute"], instance.inputs["Scale"])
    links.new(instance.outputs["Instances"], group_out.inputs["Geometry"])

    return tree

def get_or_create_lib_name(mesh_path: str):
    try:
        return mesh_path_lib_name_map[mesh_path]
    except KeyError:
        if not lib_free_indices:
            print("WARNING: Ran out of pool size, cannot stream in mesh with path: " + mesh_path)
            return None
        slot_id = lib_free_indices.pop()
        lib_name = mesh_path_lib_name_map[slot_id]
        del mesh_path_lib_name_map[slot_id]
        mesh_path_lib_name_map[mesh_path] = lib_name
        return lib_name

def stream_in_mesh(backend_mesh):
    lib_name = get_or_create_lib_name(backend_mesh.Path)
    if lib_name is None:
        return

    obj = bpy.data.objects[lib_name]
    build_mesh_from_backend(obj.data, backend_mesh)
    assign_random_color_material(obj.data, backend_mesh.Path)

def stream_out_mesh(mesh_path: str):
    try:
        lib_name = mesh_path_lib_name_map[mesh_path]
    except KeyError:
        return

    obj = bpy.data.objects[lib_name]
    obj.data.clear_geometry()

    del mesh_path_lib_name_map[mesh_path]
    slot_id = uuid.uuid4()
    mesh_path_lib_name_map[slot_id] = lib_name
    lib_free_indices.append(slot_id)

def index_from_lib_name(lib_name):
    return int(lib_name.rsplit("_", 1)[1])

import numpy as np

next_index = 0

def _ensure_vertex_count(mesh, count):
    """Grows the mesh's vertex array if we need a new slot; never shrinks."""
    if len(mesh.vertices) < count:
        mesh.vertices.add(count - len(mesh.vertices))


def _ensure_attributes(mesh):
    if "inst_rotation" not in mesh.attributes:
        mesh.attributes.new("inst_rotation", 'FLOAT_VECTOR', 'POINT')
    if "inst_scale" not in mesh.attributes:
        mesh.attributes.new("inst_scale", 'FLOAT_VECTOR', 'POINT')
    if "mesh_index" not in mesh.attributes:
        mesh.attributes.new("mesh_index", 'INT', 'POINT')


def add_node(id: str, position, rotation_euler, scale, mesh_index: int):
    global next_index
    mesh = point_cloud_obj.data
    _ensure_attributes(mesh)

    if free_indices:
        index = free_indices.pop()          # reuse a previously-removed slot
    else:
        index = next_index                  # brand new slot
        next_index += 1
        _ensure_vertex_count(mesh, next_index)

    id_to_index[id] = index

    mesh.vertices[index].co = position
    mesh.attributes["inst_rotation"].data[index].vector = rotation_euler
    mesh.attributes["inst_scale"].data[index].vector = scale
    mesh.attributes["mesh_index"].data[index].value = mesh_index

    mesh.update()


def remove_node(id: str):
    index = id_to_index.pop(id, None)
    if index is None:
        return

    mesh = point_cloud_obj.data
    # Zero scale = renders nothing, regardless of mesh_index/position -
    # avoids relying on an out-of-bounds position or a "safe" mesh index.
    mesh.attributes["inst_scale"].data[index].vector = (0.0, 0.0, 0.0)

    free_indices.append(index)
    mesh.update()

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

def on_apply_streamed_changes_tick():
    for new_node in BlenderAddonAPI.GetLoadNodesQueue(1000):
        if not new_node.MeshPath:
            continue

        # add_empty(node_id_to_string(new_node.Id), Vector((new_node.Position.Center.X, new_node.Position.Center.Y, new_node.Position.Center.Z)))
        add_node(node_id_to_string(new_node.Id),
                 Vector((new_node.Position.Center.X, new_node.Position.Center.Y, new_node.Position.Center.Z)),
                 Euler((new_node.Rotation.Pitch, new_node.Rotation.Roll, new_node.Rotation.Yaw)),
                 Vector((new_node.Scale.X, new_node.Scale.Y, new_node.Scale.Z)),
                 index_from_lib_name(get_or_create_lib_name(new_node.MeshPath)))

    for removed_node in BlenderAddonAPI.GetUnloadNodesQueue(1000):
        remove_node(node_id_to_string(removed_node))

    # apply_points()

    return 1 / 60

def on_mesh_io_tick():
    for new_mesh in BlenderAddonAPI.GetLoadMeshesQueue(1):
        stream_in_mesh(new_mesh)

    for removed_mesh in BlenderAddonAPI.GetUnloadMeshesQueue(1):
        stream_out_mesh(removed_mesh.Path)

    return 1 / 10

def init_streaming():
    global point_cloud_obj
    BlenderAddonAPI.Initialize()
    init_mesh_pool()

    point_cloud_obj = init_point_cloud()
    init_instancing_tree(point_cloud_obj)

    BlenderAddonAPI.StartStreaming()

    bpy.app.timers.register(on_streaming_tick, persistent=True)
    bpy.app.timers.register(on_apply_streamed_changes_tick, persistent=True)
    bpy.app.timers.register(on_mesh_io_tick, persistent=True)

def register():
    print("Registering Smoothie World Editor")
    bpy.app.timers.register(init_streaming, first_interval=0.1)


def unregister():
    print("Unregistering Smoothie World Editor")


if __name__ == "__main__":
    register()