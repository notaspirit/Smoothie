import bpy
import time
from ..model import global_state as gs
from SmoothieBackend.API import BlenderAddonAPI
from SharpDX import Vector3

def _tag_view3d_redraw(context):
    """
    Tags all 3D View areas in the current screen for redraw.
    """
    for area in context.screen.areas:
        if area.type == 'VIEW_3D':
            area.tag_redraw()

def _raycast_instance(context, event):
    """
    Raycasts from the mouse position in the 3D view.
    
    Returns:
        tuple: (mesh_index, hit_location_world) or (None, None) if no instance was hit.
    """
    from bpy_extras import view3d_utils
    from ..services.streaming_mesh import index_from_lib_name
    
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
        from ..services.streaming_node import find_nearest_node_id, rebuild_outline_batch
        
        mesh_index, hit_location = _raycast_instance(context, event)

        if mesh_index is None:
            gs.selected_node_id = None
        else:
            gs.selected_node_id = find_nearest_node_id(mesh_index, hit_location)

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
        from ..services.streaming_node import find_nearest_node_id, rebuild_outline_batch
        
        mesh_index, hit_location = _raycast_instance(context, event)
        if mesh_index is None:
            return {'CANCELLED'}

        node_id = find_nearest_node_id(mesh_index, hit_location)
        if node_id is None:
            return {'CANCELLED'}

        gs.selected_node_id = node_id
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
        return gs.selected_node_id is not None

    def execute(self, context):
        from ..services.streaming_node import remove_node, rebuild_outline_batch
        
        node_id = gs.selected_node_id
        if node_id is None:
            self.report({'WARNING'}, "No streamed instance selected")
            return {'CANCELLED'}

        remove_node(node_id)
        gs.removed_ids[node_id] = True

        gs.selected_node_id = None
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
        return gs.selected_node_id is not None

    def execute(self, context):
        from mathutils import Matrix
        from ..services.streaming_node import _get_node_transform, remove_node, rebuild_outline_batch
        
        node_id = gs.selected_node_id
        if node_id is None:
            self.report({'WARNING'}, "No streamed instance selected")
            return {'CANCELLED'}

        index = gs.id_to_index.get(node_id)
        if index is None:
            self.report({'ERROR'}, "Instance data not found")
            return {'CANCELLED'}

        position, rotation, scale, mesh_index = _get_node_transform(index)

        lib_name = f"MeshSource_{mesh_index}"
        src_obj = bpy.data.objects.get(lib_name)
        if src_obj is None or src_obj.data is None or len(src_obj.data.vertices) == 0:
            self.report({'ERROR'}, "Source mesh not available to replicate")
            return {'CANCELLED'}

        world_mat = gs.point_cloud_obj.matrix_world @ Matrix.LocRotScale(position, rotation, scale)

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
        gs.removed_ids[node_id] = True

        gs.selected_node_id = None
        rebuild_outline_batch()
        _tag_view3d_redraw(context)

        for obj in context.view_layer.objects:
            obj.select_set(False)
        new_obj.select_set(True)
        context.view_layer.objects.active = new_obj

        self.report({'INFO'}, f"Replicated {node_id} as {new_obj.name}")
        return {'FINISHED'}

class SMOOTHIE_OT_queue_streaming_update(bpy.types.Operator):
    """Queues a background streaming update based on the current reference point."""
    bl_idname = "smoothie.queue_streaming_update"
    bl_label = "Update Streaming With New Refs"

    def execute(self, context):
        from ..services.streaming import check_and_apply_streaming_changes
        
        try:
            from ..services.streaming_node import STREAMING_REFERENCES_COLLECTION
            ref_coll = bpy.data.collections.get(STREAMING_REFERENCES_COLLECTION)
            if ref_coll is None or len(ref_coll.objects) == 0:
                self.report({'ERROR'}, "No streaming reference point found")
                return {'CANCELLED'}

            ref_point_location = ref_coll.objects[0].location

            gs.start_time = time.perf_counter()

            BlenderAddonAPI.StreamInBackground(Vector3(ref_point_location.x, ref_point_location.y, ref_point_location.z))
            bpy.app.timers.register(check_and_apply_streaming_changes, first_interval=1)
            self.report({'INFO'}, f"Streaming world around {ref_point_location}")
        except Exception as e:
            self.report({'ERROR'}, f"Failed to queue streaming update: {e}")
            return {'CANCELLED'}
        return {'FINISHED'}

class SMOOTHIE_PT_panel(bpy.types.Panel):
    """Main panel for Smoothie addon in the 3D View sidebar."""
    bl_label = "Smoothie"
    bl_idname = "SMOOTHIE_PT_panel"
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = "Smoothie"

    def draw(self, context):
        layout = self.layout

        layout.operator("smoothie.queue_streaming_update", icon='PLAY')

        if gs.selected_node_id is not None:
            box = layout.box()
            box.label(text=f"Selected: {gs.selected_node_id}", icon='RESTRICT_SELECT_OFF')
            box.operator("smoothie.remove_instance", text="Remove", icon='X')
            box.operator("smoothie.remove_and_replicate_instance", text="Remove and Replicate", icon='DUPLICATE')

gs.blender_classes = [
    SMOOTHIE_PT_panel,
    SMOOTHIE_OT_queue_streaming_update,
    SMOOTHIE_OT_select_instance,
    SMOOTHIE_OT_instance_context_menu,
    SMOOTHIE_MT_instance_context_menu,
    SMOOTHIE_OT_remove_instance,
    SMOOTHIE_OT_remove_and_replicate_instance,
]

