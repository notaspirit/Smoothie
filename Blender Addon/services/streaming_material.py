import bpy
import numpy as np
import random

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

def assign_random_color_material(mesh: bpy.types.Mesh, name: str):
    """
    Assigns a material with a random base color to the given mesh.
    """
    mat = bpy.data.materials.get(name)
    if mat is None:
        mat = bpy.data.materials.new(name)
        mat.use_nodes = True

    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    if bsdf:
        color = (random.random(), random.random(), random.random(), 1.0)
        bsdf.inputs["Base Color"].default_value = color
        mat.diffuse_color = color

    mesh.materials.clear()
    mesh.materials.append(mat)
