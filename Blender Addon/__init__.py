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

# Ids that were explicitly removed by the user via the picker context menu.
# Checked in check_and_apply_streaming_changes() so the backend doesn't
# stream them back in on the next update.
removed_ids = {}


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

import bpy
import gpu
from gpu_extras.batch import batch_for_shader
from bpy_extras import view3d_utils

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

import uuid

point_cloud_obj = None

mesh_path_lib_name_map = {}
lib_free_indices = []

# Reverse of mesh_path_lib_name_map (path side only) so the picker/replicate
# code can figure out which source mesh path a given lib_name currently
# holds, without scanning the whole dict.
lib_name_to_mesh_path = {}

POOL_SIZE = 8000

def init_mesh_pool():
    coll = get_or_create_collection("MeshLibrary", True)
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
        lib_name_to_mesh_path[lib_name] = mesh_path
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
    lib_name_to_mesh_path.pop(lib_name, None)
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

def remove_node(id: str):
    index = id_to_index.pop(id, None)
    if index is None:
        return

    mesh = point_cloud_obj.data
    # Zero scale = renders nothing, regardless of mesh_index/position -
    # avoids relying on an out-of-bounds position or a "safe" mesh index.
    mesh.attributes["inst_scale"].data[index].vector = (0.0, 0.0, 0.0)

    free_indices.append(index)


# --- Instance picker -----------------------------------------------------
#
# Blender doesn't natively let you click-select individual points inside a
# Geometry Nodes "Instance on Points" cloud - only the point-cloud object
# itself is a real bpy.types.Object. Instead we:
#   1. Raycast the evaluated depsgraph (this DOES hit instanced geometry
#      and returns the source object used for that instance, e.g.
#      "MeshSource_42").
#   2. Recover mesh_index from that object's name.
#   3. Among all live nodes sharing that mesh_index, pick whichever point
#      is spatially closest to the hit location (several points can share
#      the same source mesh, so the name alone isn't unique).
#   4. Draw a wireframe "outline" built from the source mesh's edges,
#      transformed by that point's baked position/rotation/scale, in a
#      color distinct from Blender's own selection outline.

from mathutils import Vector, Euler, Matrix

selected_node_id = None

outline_shader = gpu.shader.from_builtin('UNIFORM_COLOR')
outline_batch = None

# Deliberately not Blender's orange/white selection colors.
SELECTION_OUTLINE_COLOR = (0.0, 0.95, 1.0, 1.0)


def _get_node_transform(index):
    """Returns (position, rotation_euler, scale, mesh_index) for a point-cloud vertex index."""
    mesh = point_cloud_obj.data
    position = Vector(mesh.vertices[index].co)
    rotation = Euler(mesh.attributes["inst_rotation"].data[index].vector)
    scale = Vector(mesh.attributes["inst_scale"].data[index].vector)
    mesh_index = mesh.attributes["mesh_index"].data[index].value
    return position, rotation, scale, mesh_index


def find_nearest_node_id(mesh_index, hit_location_world, max_dist=None):
    """Among nodes instancing `mesh_index`, find the one closest to a world-space point."""
    mesh = point_cloud_obj.data
    if "mesh_index" not in mesh.attributes:
        return None

    best_id = None
    best_dist = float('inf')

    for node_id, index in id_to_index.items():
        if mesh.attributes["mesh_index"].data[index].value != mesh_index:
            continue

        world_pos = point_cloud_obj.matrix_world @ Vector(mesh.vertices[index].co)
        dist = (world_pos - hit_location_world).length
        if dist < best_dist:
            best_dist = dist
            best_id = node_id

    if best_id is not None and max_dist is not None and best_dist > max_dist:
        return None

    return best_id


def rebuild_outline_batch():
    """Regenerates the wireframe batch for the currently selected node (or clears it)."""
    global outline_batch
    outline_batch = None

    if selected_node_id is None:
        return

    index = id_to_index.get(selected_node_id)
    if index is None:
        return

    position, rotation, scale, mesh_index = _get_node_transform(index)

    lib_name = f"MeshSource_{mesh_index}"
    src_obj = bpy.data.objects.get(lib_name)
    if src_obj is None or src_obj.data is None or len(src_obj.data.vertices) == 0:
        return

    local_mat = Matrix.LocRotScale(position, rotation, scale)
    world_mat = point_cloud_obj.matrix_world @ local_mat

    src_mesh = src_obj.data
    coords = [world_mat @ v.co for v in src_mesh.vertices]
    edge_indices = [tuple(e.vertices) for e in src_mesh.edges]

    if not edge_indices:
        return

    outline_batch = batch_for_shader(
        outline_shader, 'LINES', {"pos": coords}, indices=edge_indices
    )


def draw_outline():
    if outline_batch is None:
        return

    gpu.state.blend_set('ALPHA')
    gpu.state.depth_test_set('NONE')  # always visible on top, like a real selection outline
    gpu.state.line_width_set(2.5)

    outline_shader.bind()
    outline_shader.uniform_float("color", SELECTION_OUTLINE_COLOR)
    outline_batch.draw(outline_shader)

    gpu.state.line_width_set(1.0)
    gpu.state.depth_test_set('LESS_EQUAL')
    gpu.state.blend_set('NONE')


outline_handle = bpy.types.SpaceView3D.draw_handler_add(
    draw_outline,
    (),
    'WINDOW',
    'POST_VIEW'
)


def _tag_view3d_redraw(context):
    for area in context.screen.areas:
        if area.type == 'VIEW_3D':
            area.tag_redraw()


def _raycast_instance(context, event):
    """Raycasts from the mouse position; returns (mesh_index, hit_location_world) or (None, None)."""
    region = context.region
    rv3d = context.region_data
    coord = (event.mouse_region_x, event.mouse_region_y)

    ray_origin = view3d_utils.region_2d_to_origin_3d(region, rv3d, coord)
    ray_dir = view3d_utils.region_2d_to_vector_3d(region, rv3d, coord)

    depsgraph = context.evaluated_depsgraph_get()
    result, location, normal, poly_index, obj, matrix = context.scene.ray_cast(
        depsgraph, ray_origin, ray_dir
    )

    if not result or obj is None or not obj.name.startswith("MeshSource_"):
        return None, None

    return index_from_lib_name(obj.name), location


class SMOOTHIE_OT_select_instance(bpy.types.Operator):
    """Alt+Click picker for streamed instances. Bound so it never fires alongside
    Blender's default click-select of the point cloud object."""
    bl_idname = "smoothie.select_instance"
    bl_label = "Select Streamed Instance"
    bl_options = {'REGISTER'}

    def invoke(self, context, event):
        global selected_node_id

        mesh_index, hit_location = _raycast_instance(context, event)

        if mesh_index is None:
            selected_node_id = None
        else:
            selected_node_id = find_nearest_node_id(mesh_index, hit_location)

        rebuild_outline_batch()
        _tag_view3d_redraw(context)

        # FINISHED (rather than PASS_THROUGH) so the click is consumed here
        # and never reaches Blender's own select operator underneath.
        return {'FINISHED'}


class SMOOTHIE_MT_instance_context_menu(bpy.types.Menu):
    bl_idname = "SMOOTHIE_MT_instance_context_menu"
    bl_label = "Streamed Instance"

    def draw(self, context):
        layout = self.layout
        layout.operator("smoothie.remove_instance", text="Remove", icon='X')
        layout.operator(
            "smoothie.remove_and_replicate_instance",
            text="Remove and Replicate",
            icon='DUPLICATE',
        )


class SMOOTHIE_OT_instance_context_menu(bpy.types.Operator):
    """Alt+Right-Click: selects whatever's under the cursor, then opens the context menu."""
    bl_idname = "smoothie.instance_context_menu"
    bl_label = "Streamed Instance Context Menu"
    bl_options = {'REGISTER'}

    def invoke(self, context, event):
        global selected_node_id

        mesh_index, hit_location = _raycast_instance(context, event)
        if mesh_index is None:
            return {'CANCELLED'}

        node_id = find_nearest_node_id(mesh_index, hit_location)
        if node_id is None:
            return {'CANCELLED'}

        selected_node_id = node_id
        rebuild_outline_batch()
        _tag_view3d_redraw(context)

        bpy.ops.wm.call_menu(name=SMOOTHIE_MT_instance_context_menu.bl_idname)
        return {'FINISHED'}


class SMOOTHIE_OT_remove_instance(bpy.types.Operator):
    """Removes the currently selected streamed instance and blacklists its id
    so the backend won't stream it back in."""
    bl_idname = "smoothie.remove_instance"
    bl_label = "Remove Streamed Instance"
    bl_options = {'REGISTER', 'UNDO'}

    @classmethod
    def poll(cls, context):
        return selected_node_id is not None

    def execute(self, context):
        global selected_node_id

        node_id = selected_node_id
        if node_id is None:
            self.report({'WARNING'}, "No streamed instance selected")
            return {'CANCELLED'}

        remove_node(node_id)
        removed_ids[node_id] = True

        selected_node_id = None
        rebuild_outline_batch()
        _tag_view3d_redraw(context)

        self.report({'INFO'}, f"Removed streamed instance {node_id}")
        return {'FINISHED'}


class SMOOTHIE_OT_remove_and_replicate_instance(bpy.types.Operator):
    """Removes the currently selected streamed instance and replaces it with an
    independent Blender object holding its own copy of the mesh (not linked
    to the shared MeshLibrary source), preserving the same world transform."""
    bl_idname = "smoothie.remove_and_replicate_instance"
    bl_label = "Remove and Replicate Streamed Instance"
    bl_options = {'REGISTER', 'UNDO'}

    @classmethod
    def poll(cls, context):
        return selected_node_id is not None

    def execute(self, context):
        global selected_node_id

        node_id = selected_node_id
        if node_id is None:
            self.report({'WARNING'}, "No streamed instance selected")
            return {'CANCELLED'}

        index = id_to_index.get(node_id)
        if index is None:
            self.report({'ERROR'}, "Instance data not found")
            return {'CANCELLED'}

        position, rotation, scale, mesh_index = _get_node_transform(index)

        lib_name = f"MeshSource_{mesh_index}"
        src_obj = bpy.data.objects.get(lib_name)
        if src_obj is None or src_obj.data is None or len(src_obj.data.vertices) == 0:
            self.report({'ERROR'}, "Source mesh not available to replicate")
            return {'CANCELLED'}

        world_mat = point_cloud_obj.matrix_world @ Matrix.LocRotScale(position, rotation, scale)

        # .copy() makes a fully independent mesh datablock - editing the
        # replica will never affect the pooled MeshSource object or any
        # other instance still using it.
        new_mesh = src_obj.data.copy()
        new_mesh.name = f"{node_id}_replica_mesh"

        for mat in src_obj.data.materials:
            new_mesh.materials.append(mat)

        new_obj = bpy.data.objects.new(f"{node_id}_replica", new_mesh)
        new_obj.matrix_world = world_mat
        context.scene.collection.objects.link(new_obj)

        remove_node(node_id)
        removed_ids[node_id] = True

        selected_node_id = None
        rebuild_outline_batch()
        _tag_view3d_redraw(context)

        for obj in context.view_layer.objects:
            obj.select_set(False)
        new_obj.select_set(True)
        context.view_layer.objects.active = new_obj

        self.report({'INFO'}, f"Replicated {node_id} as {new_obj.name}")
        return {'FINISHED'}


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

from SmoothieBackend.API import BlenderAddonAPI
from SharpDX import Vector3

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

        node_id = new_node.Id.ToString()
        if node_id in removed_ids:
            # User explicitly removed this instance via the picker - don't
            # let the backend stream it back in.
            continue

        add_node(node_id,
                 Vector((new_node.Position.Center.X, new_node.Position.Center.Y, new_node.Position.Center.Z)),
                 Euler((new_node.Rotation.Pitch, new_node.Rotation.Roll, new_node.Rotation.Yaw)),
                 Vector((new_node.Scale.X, new_node.Scale.Y, new_node.Scale.Z)),
                 index_from_lib_name(get_or_create_lib_name(new_node.MeshPath)))
    return None

def init_streaming():
    global point_cloud_obj
    BlenderAddonAPI.Initialize()
    init_mesh_pool()

    point_cloud_obj = init_point_cloud()
    init_instancing_tree(point_cloud_obj)

    ref_coll = get_or_create_collection(STREAMING_REFERENCES_COLLECTION, hide=False)
    if ref_coll.objects.get("StreamingRef1") is None:
        add_empty("StreamingRef1", Vector((0, 0, 0)))

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

        if selected_node_id is not None:
            box = layout.box()
            box.label(text=f"Selected: {selected_node_id}", icon='RESTRICT_SELECT_OFF')
            box.operator("smoothie.remove_instance", text="Remove", icon='X')
            box.operator("smoothie.remove_and_replicate_instance", text="Remove and Replicate", icon='DUPLICATE')

classes = (
    SMOOTHIE_PT_panel,
    SMOOTHIE_OT_queue_streaming_update,
    SMOOTHIE_OT_select_instance,
    SMOOTHIE_OT_instance_context_menu,
    SMOOTHIE_MT_instance_context_menu,
    SMOOTHIE_OT_remove_instance,
    SMOOTHIE_OT_remove_and_replicate_instance,
)

addon_keymaps = []

def register_keymaps():
    wm = bpy.context.window_manager
    kc = wm.keyconfigs.addon
    if kc is None:
        # Happens when running with --factory-startup / no addon keyconfig
        # available (e.g. background mode). Nothing to bind in that case.
        return

    km = kc.keymaps.new(name='Object Mode', space_type='EMPTY')

    kmi_select = km.keymap_items.new(
        SMOOTHIE_OT_select_instance.bl_idname, 'LEFTMOUSE', 'PRESS', alt=True
    )
    # Move to the front of the keymap so it's evaluated - and consumes the
    # click - before any other Alt+LMB binding gets a chance to run.
    # km.keymap_items.move(len(km.keymap_items) - 1, 0)

    kmi_menu = km.keymap_items.new(
        SMOOTHIE_OT_instance_context_menu.bl_idname, 'RIGHTMOUSE', 'PRESS', alt=True
    )
    # km.keymap_items.move(len(km.keymap_items) - 1, 0)

    addon_keymaps.append((km, kmi_select))
    addon_keymaps.append((km, kmi_menu))


def unregister_keymaps():
    for km, kmi in addon_keymaps:
        km.keymap_items.remove(kmi)
    addon_keymaps.clear()


def register():
    print("Registering Smoothie World Editor")
    for cls in classes:
        bpy.utils.register_class(cls)
    register_keymaps()
    bpy.app.timers.register(init_streaming, first_interval=0.1)

def unregister():
    print("Unregistering Smoothie World Editor")
    unregister_keymaps()
    for cls in reversed(classes):
        bpy.utils.unregister_class(cls)
    if handle is not None:
        bpy.types.SpaceView3D.draw_handler_remove(handle, 'WINDOW')
    if outline_handle is not None:
        bpy.types.SpaceView3D.draw_handler_remove(outline_handle, 'WINDOW')


if __name__ == "__main__":
    register()