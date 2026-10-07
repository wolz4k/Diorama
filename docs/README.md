# Diorama

Diorama is a tool for managing the geometry used in the LEGO videogames developed by Traveller's Tales. It allows you to view and edit the geometry of the game.

The tool is still under development, but it already has some features implemented.

## Features

- View level / general geometry with full texture support.
- Scenes that use the shared LEGO texture page (`LEGOTPAGE\*.TEX`) find it in an extracted install by themselves, without setting the game folder in Settings.
- View the hierarchy of a scene, and change the name of special objects where possible.
- View and edit (but not save, yet!) the transformations of objects in the scene.
- Swap the geometry in the scene with other geometry from a .OBJ file (see "Editing a part in Blender" below).
- Change the primary material colour
- Saving writes a scene back exactly as the game stored it, apart from your edits (all 3,926 of DC Super-Villains' scenes come back byte for byte, faces with expressions included), so a mod only differs from the original where you changed something.
- Toggle on/off lightmaps

## Editing a part in Blender

1. Select a part (click it in the viewport or the hierarchy) and press **Export mesh** in the inspector. Save as `.OBJ`: you also get an `.MTL` and the part's diffuse texture as `.DDS`, so it shows textured in Blender.
2. In Blender, use File > Import > Wavefront (.obj) with the default axes. Edit or replace the shape.
3. Export with File > Export > Wavefront (.obj), keeping the default axes (Y up, -Z forward) and ticking **UV Coordinates**, **Normals** and **Colors**. Triangulating faces is optional, because quads and n-gons are split on import.
4. Back in Diorama, select the same part and press **Replace mesh**. A report lists what was imported and what was carried over from the old shape:
   - **Bone weights** for character parts are copied from the nearest original vertex, so the new shape still follows the skeleton. Keep new geometry close to the part it replaces.
   - **Vertex colours** (the brick colour on most LEGO parts) are copied from the nearest original vertex if the OBJ has none.
   - Vertex alpha and lightmap / second UV sets always come from the nearest original vertex, since an OBJ can't hold them.
   - The part's culling box is recalculated, so a bigger shape isn't cut off at the screen edge.
5. Right-click the scene in the hierarchy and choose **Save GScene**. This overwrites the file you opened, so work on a copy.

### A whole model at once

Right-click the scene in the hierarchy and choose **Export Parts as OBJ...** to write every part shown right now (the picked LOD, plus breakup parts if they're on) to one `.OBJ`, one Blender object per part, with their textures. Each object's name ends in `__m` and a number, the game mesh it came from, so keep that ending when you rename things (Blender's `.001` suffixes are fine).

After editing, **Replace Parts from OBJ...** puts each object back on its part. Parts you didn't change are recognised and left exactly as they were, objects without an `__m` number are skipped (join new geometry into the part it belongs to with Ctrl+J), and a report lists what happened.

A game mesh can hold at most 65,536 vertices (after splitting along UV seams and hard edges). Parts with blend shapes (facial expressions) can be replaced, but the expressions won't fit the new shape.

## Supported Games

- LEGO Batman 3
- LEGO Dimensions
- LEGO Marvel's Avengers
- LEGO Worlds
- LEGO Star Wars: The Force Awakens
- LEGO Ninjago
- LEGO Marvel Super Heroes 2
- LEGO DC Super-Villains