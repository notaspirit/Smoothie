import time
import bpy
import gpu
from gpu_extras.batch import batch_for_shader
from mathutils import Vector, Euler
from ..model import global_state as gs
from . import utils
from SmoothieBackend.API import BlenderAddonAPI

SELECTION_OUTLINE_COLOR = (0.0, 0.95, 1.0, 1.0)
_shader_cache = {}

def get_shader(name: str):
    """
    Returns a cached GPU shader or creates a new one if not cached.
    """
    if name not in _shader_cache:
        _shader_cache[name] = gpu.shader.from_builtin(name)
    return _shader_cache[name]

def draw():
    """
    Main draw callback for the 3D View. Draws the point cloud.
    """
    if gs.batch is None:
        return

    shader = get_shader("UNIFORM_COLOR")
    shader.bind()
    shader.uniform_float("color", (0, 1, 0, 0.5))
    gs.batch.draw(shader)

def draw_outline():
    """
    Draws a selection outline for the currently selected streamed instance.
    """
    if gs.outline_batch is None:
        return

    gpu.state.blend_set('ALPHA')
    gpu.state.depth_test_set('NONE')  # always visible on top, like a real selection outline
    gpu.state.line_width_set(2.5)

    shader = get_shader('UNIFORM_COLOR')
    shader.bind()
    shader.uniform_float("color", SELECTION_OUTLINE_COLOR)
    gs.outline_batch.draw(shader)

    gpu.state.line_width_set(1.0)
    gpu.state.depth_test_set('LESS_EQUAL')
    gpu.state.blend_set('NONE')

def apply_points():
    """
    Rebuilds the GPU batch for the point cloud based on current positions.
    """
    shader = get_shader("UNIFORM_COLOR")
    gs.batch = batch_for_shader(
        shader,
        "POINTS",
        {"pos": gs.positions}
    )

    for area in bpy.context.screen.areas:
        if area.type == 'VIEW_3D':
            area.tag_redraw()

def check_and_apply_streaming_changes():
    """
    Timer callback that polls the backend for streaming changes and applies them to Blender.
    """
    from .streaming_mesh import stream_in_mesh, stream_out_mesh, get_or_create_lib_name, index_from_lib_name
    from .streaming_node import add_node, remove_node
    from .streaming_material import stream_in_material, stream_out_material
    
    changes = BlenderAddonAPI.GetStreamResult()
    if changes is None:
        return 1

    t = [time.perf_counter()]  # t[0] = start_time reference point (after backend call)

    def mark():
        t.append(time.perf_counter())
        return t[-1] - t[-2]

    backend_time = t[0] - gs.start_time
    gs.logger.info(f"Streaming update received in {utils.format_elapsed(backend_time)}")

    for removed_mat in changes.RemovedTextures:
        stream_out_material(removed_mat.ToString())

    gs.logger.info(f"Removed materials in {utils.format_elapsed(mark())}")

    for added_mat in changes.AddedTextures:
        stream_in_material(added_mat)

    gs.logger.info(f"Added materials in {utils.format_elapsed(mark())}")

    for removed_mesh in changes.RemovedMeshes:
        stream_out_mesh(removed_mesh)

    gs.logger.info(f"Removed meshes in {utils.format_elapsed(mark())}")

    for added_mesh in changes.AddedMeshes:
        stream_in_mesh(added_mesh)

    gs.logger.info(f"Added meshes in {utils.format_elapsed(mark())}")

    for removed_node_data in changes.RemovedNodes:
        remove_node(removed_node_data.ToString())

    gs.logger.info(f"Removed nodes in {utils.format_elapsed(mark())}")

    for new_node in changes.AddedNodes:
        node_id = new_node.Id.ToString()
        if node_id in gs.removed_ids:
            # User explicitly removed this instance via the picker - don't
            # let the backend stream it back in.
            continue

        if new_node.Instances is not None and len(new_node.Instances) > 0:
            for node_instance in new_node.Instances:
                add_node(node_instance.Id.ToString(),
                         Vector((node_instance.Position.Center.X, node_instance.Position.Center.Y, node_instance.Position.Center.Z)),
                         Euler((node_instance.Rotation.Pitch, node_instance.Rotation.Roll, node_instance.Rotation.Yaw)),
                         Vector((node_instance.Scale.X, node_instance.Scale.Y, node_instance.Scale.Z)),
                         index_from_lib_name(get_or_create_lib_name(node_instance.MeshPath)))
        else:
            add_node(node_id,
                     Vector((new_node.Position.Center.X, new_node.Position.Center.Y, new_node.Position.Center.Z)),
                     Euler((new_node.Rotation.Pitch, new_node.Rotation.Roll, new_node.Rotation.Yaw)),
                     Vector((new_node.Scale.X, new_node.Scale.Y, new_node.Scale.Z)),
                     index_from_lib_name(get_or_create_lib_name(new_node.MeshPath)))

    gs.logger.info(f"Added nodes in {utils.format_elapsed(mark())}")
    gs.logger.info(f"Processed nodes, meshes and textures in {utils.format_elapsed(t[-1] - t[0])}")

    bpy.context.view_layer.update()

    gs.logger.info(f"Updated depends graph in {utils.format_elapsed(mark())}")
    gs.logger.info(f"Streaming update applied in {utils.format_elapsed(time.perf_counter() - gs.start_time)}")
    
    return None

def init_streaming():
    """
    Initializes the backend API, mesh pool, and point cloud object.
    """
    from .streaming_mesh import init_mesh_pool
    from .streaming_node import init_point_cloud, init_instancing_tree, get_or_create_collection, add_empty, STREAMING_REFERENCES_COLLECTION
    
    BlenderAddonAPI.Initialize()
    init_mesh_pool()

    gs.point_cloud_obj = init_point_cloud()
    init_instancing_tree(gs.point_cloud_obj)

    ref_coll = get_or_create_collection(STREAMING_REFERENCES_COLLECTION, hide=False)
    if ref_coll.objects.get("StreamingRef1") is None:
        add_empty("StreamingRef1", Vector((0, 0, 0)))
