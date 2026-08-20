from distutils.dep_util import newer

import bpy
import numpy as np
import random

from ..model import global_state as gs

_material_template = None

def _load_blender_texture(name: str, backend_texture):
    """
    Loads a texture from backend byte data into a Blender Image datablock.
    
    Args:
        name: Name for the new Blender image.
        backend_texture: Object containing Width, Height, and PixelData (System.Byte[]).
    """
    width = backend_texture.Width
    height = backend_texture.Height

    # backend_texture.PixelData is a System.Byte[] (CLR array).
    # np.frombuffer works on it since it supports the buffer protocol.
    pixels = np.frombuffer(bytes(backend_texture.PixelData), dtype=np.uint8)

    expected_size = width * height * 4
    assert pixels.size == expected_size, f"{pixels.size} != {expected_size}"

    pixels = pixels.astype(np.float32) / 255.0
    pixels = pixels.reshape((height, width, 4))
    pixels = np.flipud(pixels).ravel()

    image = bpy.data.images.new(name, width=width, height=height, alpha=True)
    image.colorspace_settings.name = 'sRGB'
    image.pixels.foreach_set(pixels)
    image.source = 'GENERATED'

    return image

def get_base_material():
    global _material_template
    if _material_template is None:
        mat = bpy.data.materials.new("_smoothie_template")
        mat.use_nodes = True
        bsdf = mat.node_tree.nodes["Principled BSDF"]
        tex_node = mat.node_tree.nodes.new("ShaderNodeTexImage")
        tex_node.name = "AlbedoTex"
        mat.node_tree.links.new(tex_node.outputs["Color"], bsdf.inputs["Base Color"])
        _material_template = mat
    return _material_template.copy()

def stream_in_material(texture):
    id = texture.Id.ToString()
    mat = get_base_material()
    image = _load_blender_texture(id, texture)
    mat.node_tree.nodes["AlbedoTex"].image = image

    tex_node = mat.node_tree.nodes["AlbedoTex"]
    tex_node.image = image
    if image.depth == 32:
        bsdf = mat.node_tree.nodes["Principled BSDF"]
        mat.node_tree.links.new(tex_node.outputs["Alpha"], bsdf.inputs["Alpha"])
        mat.blend_method = 'HASHED'

    gs.material_library[id] = mat

def stream_out_material(id: str):
    if id in gs.material_library:
        del gs.material_library[id]