import bpy
import numpy as np
import uuid
from ..model import global_state as gs

def get_or_create_mesh(name: str):
    """
    Retrieves an existing mesh object by name or creates a new one if it doesn't exist.
    """
    mesh_name = name + "_mesh"
    obj = bpy.data.objects.get(mesh_name)
    if obj is None:
        mesh = bpy.data.meshes.new(mesh_name)
        obj = bpy.data.objects.new(name, mesh)
    return obj

def remove_mesh(name: str):
    """
    Retrieves a mesh object by name intended for removal.
    """
    mesh_name = name + "_mesh"
    return bpy.data.objects.get(mesh_name)

def build_mesh_from_backend(mesh: bpy.types.Mesh, backend_mesh):
    """
    Updates a Blender mesh datablock with vertex, index, UV, and material data 
    from a backend mesh representation.
    """
    from .streaming_material import _load_blender_texture
    
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

    # --- UVs ---
    uvs = np.asarray(backend_mesh.UVs, dtype=np.float32).reshape(-1, 2)
    loop_uvs = uvs[indices]  # per-vertex UVs -> per-loop, via the same index buffer

    uv_layer = mesh.uv_layers.new(name="UVMap")
    uv_layer.data.foreach_set("uv", loop_uvs.ravel())

    # --- Materials / submeshes ---
    offsets = list(backend_mesh.SubMeshIndexOffsets) + [num_loops]
    num_submeshes = len(offsets) - 1

    # pick first appearance, ignore appearance name
    textures_per_submesh = None
    if backend_mesh.Textures:
        first_appearance = next(iter(backend_mesh.Textures.values()))
        textures_per_submesh = first_appearance  # MaterialID[]

    material_index = np.zeros(num_tris, dtype=np.int32)

    for sub_idx in range(num_submeshes):
        start, end = offsets[sub_idx], offsets[sub_idx + 1]
        poly_start, poly_end = start // 3, end // 3
        material_index[poly_start:poly_end] = sub_idx

        if textures_per_submesh is not None and sub_idx < len(textures_per_submesh):
            backend_texture_id = textures_per_submesh[sub_idx].ToString()
            if backend_texture_id in gs.material_library:
                mesh.materials.append(gs.material_library[backend_texture_id])
            else:
                print("WARNING: Material not found in library: " + backend_texture_id)

    mesh.polygons.foreach_set("material_index", material_index)
    mesh.update()

def init_mesh_pool(pool_size: int = 8000):
    """
    Initializes a pool of empty mesh objects to be used as instance sources.
    """
    from .streaming_node import get_or_create_collection
    
    coll = get_or_create_collection("MeshLibrary", True)
    for i in range(pool_size):
        name = f"MeshSource_{i}"
        if name not in bpy.data.objects:
            mesh = bpy.data.meshes.new(name + "_mesh")
            obj = bpy.data.objects.new(name, mesh)
            coll.objects.link(obj)

        slot_id = uuid.uuid4()
        gs.mesh_path_lib_name_map[slot_id] = name
        gs.lib_free_indices.append(slot_id)

def get_or_create_lib_name(mesh_path: str):
    """
    Gets a library object name for a given mesh path, allocating from the pool if necessary.
    """
    try:
        return gs.mesh_path_lib_name_map[mesh_path]
    except KeyError:
        if not gs.lib_free_indices:
            if gs.logger:
                gs.logger.info("WARNING: Ran out of pool size, cannot stream in mesh with path: " + mesh_path)
            return None
        slot_id = gs.lib_free_indices.pop()
        lib_name = gs.mesh_path_lib_name_map[slot_id]
        del gs.mesh_path_lib_name_map[slot_id]
        gs.mesh_path_lib_name_map[mesh_path] = lib_name
        gs.lib_name_to_mesh_path[lib_name] = mesh_path
        return lib_name

def stream_in_mesh(backend_mesh):
    """
    Streams in a mesh from the backend, updating one of the pooled mesh library objects.
    """
    lib_name = get_or_create_lib_name(backend_mesh.Path)
    if lib_name is None:
        return

    obj = bpy.data.objects[lib_name]
    build_mesh_from_backend(obj.data, backend_mesh)

def stream_out_mesh(mesh_path: str):
    """
    Streams out a mesh, clearing the geometry of its library object and returning it to the pool.
    """
    try:
        lib_name = gs.mesh_path_lib_name_map[mesh_path]
    except KeyError:
        return

    obj = bpy.data.objects[lib_name]
    obj.data.clear_geometry()

    del gs.mesh_path_lib_name_map[mesh_path]
    gs.lib_name_to_mesh_path.pop(lib_name, None)
    slot_id = uuid.uuid4()
    gs.mesh_path_lib_name_map[slot_id] = lib_name
    gs.lib_free_indices.append(slot_id)

def index_from_lib_name(lib_name: str) -> int:
    """
    Extracts the pool index from a library object name.
    """
    return int(lib_name.rsplit("_", 1)[1])
