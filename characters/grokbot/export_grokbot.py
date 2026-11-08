"""Export GrokBot from the author's Blender project as a real 3D model (USDZ) for GLORIA to render live with SceneKit.

  /Applications/Blender.app/Contents/MacOS/Blender -b <grokbot_transform_v6.blend> --python export_grokbot.py

Takes the finished pose of the transform film (frame 650), drops the film's animation, scene and effects, and keeps
the robot's parts with their pivots, so the app can move them: Bot_root (body), EarPivot±1, FinPivot±1, EyePivot with
the nine LED faces (E_Eyes_*), the LED grid, the hover ring and the 3D arrow cursor. Modifiers are applied.
Output: grokbot.usdz next to this script (Y up, facing +Z).
"""
import bpy, os

HERE = os.path.dirname(os.path.abspath(__file__))
sc = bpy.context.scene
sc.frame_set(650)
for o in bpy.data.objects:
    if o.animation_data: o.animation_data.action = None
for m in bpy.data.materials:
    if m.node_tree and m.node_tree.animation_data: m.node_tree.animation_data.action = None

# The arrow cursor follows the old camera through a constraint; free it and face it forward.
cur = bpy.data.objects["Cursor"]
for c in list(cur.constraints): cur.constraints.remove(c)
cur.rotation_mode = "XYZ"; cur.rotation_euler = (0, 0, 0); cur.scale = (1, 1, 1); cur.location = (0, 0, 0)
for ch in cur.children:
    ch.rotation_mode = "XYZ"; ch.rotation_euler = (1.5708, 0, 0)

drop = {"Clean studio background", "LaserGrid", "ScanRing", "CamTarget", "Cam", "Key", "Fill", "Rim"}
keep = [o for o in bpy.data.objects if o.name not in drop and o.type in ("MESH", "EMPTY")]
for o in bpy.data.objects:
    o.select_set(False)
for o in keep:
    # Every part goes out visible; the app shows one LED face at a time and the cursor only when it's needed.
    o.hide_render = False; o.hide_viewport = False; o.hide_set(False)
    o.select_set(True)
bpy.context.view_layer.objects.active = keep[0]

out = os.path.join(HERE, "grokbot.usdz")
bpy.ops.wm.usd_export(filepath=out, selected_objects_only=True, visible_objects_only=False, export_animation=False,
                      export_materials=True, generate_preview_surface=True, export_textures=True,
                      export_subdivision="TESSELLATE", evaluation_mode="RENDER", export_normals=True, export_uvmaps=True,
                      convert_orientation=True, export_global_forward_selection="Z", export_global_up_selection="Y",
                      xform_op_mode="TRS", export_lights=False, export_cameras=False, triangulate_meshes=False)
print("exported", out, os.path.getsize(out) // 1024, "KB,", len(keep), "objects")
