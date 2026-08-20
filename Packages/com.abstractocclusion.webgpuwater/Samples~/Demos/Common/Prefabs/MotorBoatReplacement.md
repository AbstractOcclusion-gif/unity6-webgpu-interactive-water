# MotorBoat visual replacement

The original third-party MotorBoat model was removed from the distributable sample
on 2026-08-19 because its bundled license allowed personal/student use only.

The replacement deliberately retains the established `MotorBoat.prefab` asset GUID,
root object, child object file IDs, and runtime hierarchy. Its visual children now use
Unity built-in meshes and materials, reshaped into a simple low-poly placeholder.
This keeps scene instances, nested prefab overrides, buoyancy, interaction, and boat
controller references intact without distributing the restricted model or textures.
