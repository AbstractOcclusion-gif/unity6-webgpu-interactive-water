import math
import os
import sys

import bpy
from mathutils import Vector


AXIS_RADIUS_FACTOR = 0.008
AXIS_LENGTH_FACTOR = 0.65
ARROW_LENGTH_FACTOR = 0.12
CAMERA_DISTANCE_FACTOR = 2.8
RENDER_SIZE = 700


def script_arguments():
    separator = sys.argv.index("--")
    return sys.argv[separator + 1], sys.argv[separator + 2]


def clear_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)


def import_model(model_path):
    extension = os.path.splitext(model_path)[1].lower()
    if extension == ".blend":
        return
    clear_scene()
    if extension == ".fbx":
        bpy.ops.import_scene.fbx(filepath=model_path)
        return
    if extension == ".obj":
        bpy.ops.wm.obj_import(filepath=model_path)
        return
    raise RuntimeError(f"Unsupported model extension: {extension}")


def mesh_objects():
    return [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]


def world_bounds(objects):
    corners = [obj.matrix_world @ Vector(corner) for obj in objects for corner in obj.bound_box]
    minimum = Vector((min(point.x for point in corners),
                      min(point.y for point in corners),
                      min(point.z for point in corners)))
    maximum = Vector((max(point.x for point in corners),
                      max(point.y for point in corners),
                      max(point.z for point in corners)))
    return minimum, maximum


def log_object_bounds(objects):
    for obj in objects:
        minimum, maximum = world_bounds([obj])
        size = maximum - minimum
        print(f"CODEX_OBJECT name={obj.name!r} minimum={tuple(minimum)} "
              f"maximum={tuple(maximum)} size={tuple(size)}")


def add_axis(name, direction, color, center, length, radius):
    midpoint = center + direction * length * 0.5
    bpy.ops.mesh.primitive_cylinder_add(vertices=24, radius=radius, depth=length, location=midpoint)
    shaft = bpy.context.object
    shaft.name = f"Axis {name}"
    shaft.rotation_mode = "QUATERNION"
    shaft.rotation_quaternion = Vector((0.0, 0.0, 1.0)).rotation_difference(direction)
    shaft.color = (*color, 1.0)

    arrow_length = length * ARROW_LENGTH_FACTOR
    tip_location = center + direction * (length + arrow_length * 0.5)
    bpy.ops.mesh.primitive_cone_add(vertices=24, radius1=radius * 2.8, radius2=0.0,
                                    depth=arrow_length, location=tip_location)
    tip = bpy.context.object
    tip.name = f"Axis {name} Tip"
    tip.rotation_mode = "QUATERNION"
    tip.rotation_quaternion = Vector((0.0, 0.0, 1.0)).rotation_difference(direction)
    tip.color = (*color, 1.0)


def add_axes(center, largest_span):
    length = largest_span * AXIS_LENGTH_FACTOR
    radius = largest_span * AXIS_RADIUS_FACTOR
    add_axis("+X", Vector((1.0, 0.0, 0.0)), (1.0, 0.05, 0.05), center, length, radius)
    add_axis("+Y", Vector((0.0, 1.0, 0.0)), (0.05, 1.0, 0.05), center, length, radius)
    add_axis("+Z", Vector((0.0, 0.0, 1.0)), (0.05, 0.2, 1.0), center, length, radius)


def add_camera(center, largest_span):
    direction = Vector((1.35, -1.6, 1.1)).normalized()
    location = center + direction * largest_span * CAMERA_DISTANCE_FACTOR
    bpy.ops.object.camera_add(location=location)
    camera = bpy.context.object
    camera.data.type = "ORTHO"
    camera.data.ortho_scale = largest_span * 1.7
    camera.rotation_euler = (center - location).to_track_quat("-Z", "Y").to_euler()
    bpy.context.scene.camera = camera


def render(output_path):
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.light = "STUDIO"
    scene.display.shading.color_type = "OBJECT"
    scene.display.shading.show_shadows = True
    scene.display.shading.show_cavity = True
    scene.display.shading.cavity_type = "WORLD"
    scene.display.shading.background_type = "VIEWPORT"
    scene.display.shading.background_color = (0.035, 0.04, 0.055)
    scene.render.resolution_x = RENDER_SIZE
    scene.render.resolution_y = RENDER_SIZE
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.filepath = output_path
    scene.render.film_transparent = False
    bpy.ops.render.render(write_still=True)


def main():
    model_path, output_path = script_arguments()
    import_model(model_path)
    models = mesh_objects()
    if not models:
        raise RuntimeError(f"No mesh objects found in {model_path}")

    for model in models:
        model.color = (0.62, 0.66, 0.72, 1.0)

    log_object_bounds(models)

    minimum, maximum = world_bounds(models)
    center = (minimum + maximum) * 0.5
    size = maximum - minimum
    largest_span = max(size)
    print(f"CODEX_BOUNDS minimum={tuple(minimum)} maximum={tuple(maximum)} size={tuple(size)}")

    add_axes(center, largest_span)
    add_camera(center, largest_span)
    render(output_path)


main()
