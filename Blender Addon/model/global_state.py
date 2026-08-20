from ..services.logger import Logger
from typing import List, Dict, Any, Optional

logger: Logger = None

# --- Addon Registration ---
blender_classes = []
addon_keymaps = []

# --- Streaming State ---
positions: List[float] = []
id_to_index: Dict[str, int] = {}
free_indices: List[int] = []

# Ids that were explicitly removed by the user via the picker context menu.
# Checked in check_and_apply_streaming_changes() so the backend doesn't
# stream them back in on the next update.
removed_ids: Dict[str, bool] = {}

point_cloud_obj = None
next_index: int = 0

# --- Mesh Library State ---
mesh_path_lib_name_map: Dict[Any, str] = {}
lib_free_indices: List[Any] = []

# --- Material Library State ---
material_library: Dict[str, Any] = {}

# Reverse of mesh_path_lib_name_map (path side only) so the picker/replicate
# code can figure out which source mesh path a given lib_name currently
# holds, without scanning the whole dict.
lib_name_to_mesh_path: Dict[str, str] = {}

# --- Selection State ---
selected_node_id: Optional[str] = None

# --- Draw Handlers & Batches ---
draw_handle_3d = None
outline_handle_3d = None

batch = None
outline_batch = None

# --- Timing ---
start_time: float = 0