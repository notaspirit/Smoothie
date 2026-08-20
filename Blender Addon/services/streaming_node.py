import bpy
import gpu
from gpu_extras.batch import batch_for_shader
from mathutils import Vector, Euler, Matrix
from ..model import global_state as gs

STREAMING_REFERENCES_COLLECTION = "StreamingReferences"

def add_empty(id: str, position: Vector):
    """
    Adds an empty object at the specified position and links it to the 
    streaming references collection.
    """
    coll = get_or_create_collection(STREAMING_REFERENCES_COLLECTION, hide=False)
    empty = bpy.data.objects.new(id, None)
    empty.empty_display_type = 'PLAIN_AXES'
    coll.objects.link(empty)
    empty.location = position

def remove_empty(id: str):
    """
    Removes the specified empty object from Blender.
    """
    obj = bpy.data.objects.get(id)
    if obj is not None:
        bpy.data.objects.remove(obj, do_unlink=True)

def get_or_create_collection(name: str, hide: bool = False):
    """
    Retrieves a top-level collection or creates it if not found.
    Sets viewport and render visibility.
    """
    coll = bpy.data.collections.get(name)
    if coll is None:
        coll = bpy.data.collections.new(name)
        bpy.context.scene.collection.children.link(coll)
    coll.hide_viewport = hide
    coll.hide_render = hide
    return coll

def init_point_cloud():
    """
    Initializes the point cloud object used for geometry nodes instancing.
    """
    name = "StreamedPoints"
    if name in bpy.data.objects:
        return bpy.data.objects[name]

    mesh = bpy.data.meshes.new(name + "_mesh")
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    return obj

def init_instancing_tree(points_obj: bpy.types.Object):
    """
    Sets up the Geometry Nodes modifier and tree for the point cloud object.
    """
    mod = points_obj.modifiers.new("Streamed Instances", 'NODES')
    tree = bpy.data.node_groups.new("StreamedInstancesTree", 'GeometryNodeTree')
    mod.node_group = tree

    tree.interface.new_socket(name="Geometry", in_out='INPUT', socket_type='NodeSocketGeometry')
    tree.interface.new_socket(name="Geometry", in_out='OUTPUT', socket_type='NodeSocketGeometry')

    nodes, links = tree.nodes, tree.links
    nodes.clear()

    group_in  = nodes.new("NodeGroupInput");  group_in.location  = (-900, 0)
    group_out = nodes.new("NodeGroupOutput"); group_out.location = (700, 0)

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

def _ensure_vertex_count(mesh: bpy.types.Mesh, count: int):
    """
    Grows the mesh's vertex array if needed; never shrinks.
    """
    if len(mesh.vertices) < count:
        mesh.vertices.add(count - len(mesh.vertices))

def _ensure_attributes(mesh: bpy.types.Mesh):
    """
    Ensures the point cloud mesh has the necessary named attributes for instancing.
    """
    if "inst_rotation" not in mesh.attributes:
        mesh.attributes.new("inst_rotation", 'FLOAT_VECTOR', 'POINT')
    if "inst_scale" not in mesh.attributes:
        mesh.attributes.new("inst_scale", 'FLOAT_VECTOR', 'POINT')
    if "mesh_index" not in mesh.attributes:
        mesh.attributes.new("mesh_index", 'INT', 'POINT')

def add_node(id: str, position: Vector, rotation_euler: Euler, scale: Vector, mesh_index: int):
    """
    Adds a new node (vertex) to the point cloud with the given transform and mesh index.
    """
    mesh = gs.point_cloud_obj.data
    _ensure_attributes(mesh)

    if gs.free_indices:
        index = gs.free_indices.pop()
    else:
        index = gs.next_index
        gs.next_index += 1
        _ensure_vertex_count(mesh, gs.next_index)

    gs.id_to_index[id] = index

    mesh.vertices[index].co = position
    mesh.attributes["inst_rotation"].data[index].vector = rotation_euler
    mesh.attributes["inst_scale"].data[index].vector = scale
    mesh.attributes["mesh_index"].data[index].value = mesh_index

def remove_node(id: str):
    """
    Removes a node by zeroing its scale and adding its index to the free list.
    """
    index = gs.id_to_index.pop(id, None)
    if index is None:
        return

    mesh = gs.point_cloud_obj.data
    mesh.attributes["inst_scale"].data[index].vector = (0.0, 0.0, 0.0)
    gs.free_indices.append(index)

def _get_node_transform(index: int):
    """
    Returns (position, rotation_euler, scale, mesh_index) for a point-cloud vertex index.
    """
    mesh = gs.point_cloud_obj.data
    position = Vector(mesh.vertices[index].co)
    rotation = Euler(mesh.attributes["inst_rotation"].data[index].vector)
    scale = Vector(mesh.attributes["inst_scale"].data[index].vector)
    mesh_index = mesh.attributes["mesh_index"].data[index].value
    return position, rotation, scale, mesh_index

def find_nearest_node_id(mesh_index: int, hit_location_world: Vector, max_dist: float = None):
    """
    Among nodes instancing `mesh_index`, find the one closest to a world-space point.
    """
    mesh = gs.point_cloud_obj.data
    if "mesh_index" not in mesh.attributes:
        return None

    best_id = None
    best_dist = float('inf')

    for node_id, index in gs.id_to_index.items():
        if mesh.attributes["mesh_index"].data[index].value != mesh_index:
            continue

        world_pos = gs.point_cloud_obj.matrix_world @ Vector(mesh.vertices[index].co)
        dist = (world_pos - hit_location_world).length
        if dist < best_dist:
            best_dist = dist
            best_id = node_id

    if best_id is not None and max_dist is not None and best_dist > max_dist:
        return None

    return best_id

def rebuild_outline_batch():
    """
    Regenerates the wireframe batch for the currently selected node.
    """
    gs.outline_batch = None

    if gs.selected_node_id is None:
        return

    index = gs.id_to_index.get(gs.selected_node_id)
    if index is None:
        return

    position, rotation, scale, mesh_index = _get_node_transform(index)

    lib_name = f"MeshSource_{mesh_index}"
    src_obj = bpy.data.objects.get(lib_name)
    if src_obj is None or src_obj.data is None or len(src_obj.data.vertices) == 0:
        return

    local_mat = Matrix.LocRotScale(position, rotation, scale)
    world_mat = gs.point_cloud_obj.matrix_world @ local_mat

    src_mesh = src_obj.data
    coords = [world_mat @ v.co for v in src_mesh.vertices]
    edge_indices = [tuple(e.vertices) for e in src_mesh.edges]

    if not edge_indices:
        return

    from services.streaming import get_shader
    shader = get_shader('UNIFORM_COLOR')
    gs.outline_batch = batch_for_shader(
        shader, 'LINES', {"pos": coords}, indices=edge_indices
    )
