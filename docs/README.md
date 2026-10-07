# Diorama

Diorama is a tool for managing the geometry used in the LEGO videogames developed by Traveller's Tales. It allows you to view and edit the geometry of the game.

The tool is still under development, but it already has some features implemented.

## Features

- View level / general geometry with full texture support.
- Scenes that use the shared LEGO texture page (`LEGOTPAGE\*.TEX`) find it in an extracted install by themselves, without setting the game folder in Settings.
- Hub level pieces (Arkham, Apokolips, Gotham...) show their textures, which the game keeps in a shared texture scene such as `ARKHAM_TEXTURES_DX11.GSC`; Edit Textures says which file a texture really lives in, and exports its image from there. Textures that can't be found anywhere show white instead of black.
- View the hierarchy of a scene, and change the name of special objects where possible. Each mesh is listed by its material and size (`MAT_Eyeshadow · 464 vertices`); hover it to see which mesh of the file it is, its triangles, how many bones it bends with and how many facial expressions it has.
- View and edit (but not save, yet!) the transformations of objects in the scene.
- Swap the geometry in the scene with other geometry from a .OBJ file (see "Editing a part in Blender" below).
- Change the primary material colour
- **Edit Textures** (scene right-click menu): textures are labelled by their own name (`darkseid_diff`, `darkseid_nrm`), *Export DDS…* saves one to paint over, and clicking the preview replaces it with your .DDS; the size, compression and mipmaps a replacement should match are shown.
- Hover a material setting (blend mode, alpha test, specular, glow, reflection, tints…) to read what it does.
- Breakup parts (the pieces a bigfig falls apart into, shown with *Breakup*) sit on the body instead of a hip height below it.
- Faces and other models whose parts share one vertex buffer draw correctly (each part starts partway into the buffer).
- Saving writes a scene back exactly as the game stored it, apart from your edits (all 3,926 of DC Super-Villains' scenes come back byte for byte, faces with expressions included), so a mod only differs from the original where you changed something.
- **Shading** (toolbar, on by default) darkens surfaces turned away from you so a model's shape is visible; the game's flat colours alone hide it. The **View** menu holds the other display switches (material lighting, lightmaps, shadow impostors, frustum culling), each ticked when on.
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
5. Not right? **Undo replace** in the inspector (or *Undo Mesh Replacement* in the scene's right-click menu) puts the previous mesh back, one replacement at a time, including whole-model imports.
6. Right-click the scene in the hierarchy and choose **Save GScene** (overwrites the file you opened; the first save keeps the original as `.bak`) or **Save GScene As…** (a new file, for example in your mod folder; its `.NXG_TEXTURES`, `.RES` and shader files are copied beside it under the new name). This overwrites the file you opened, so work on a copy.

### A whole model at once

Right-click the scene in the hierarchy and choose **Export Parts as OBJ...** to write every part shown right now (the picked LOD, plus breakup parts if they're on) to one `.OBJ`, one Blender object per part, with their textures. Each object's name ends in `__m` and a number, the game mesh it came from, so keep that ending when you rename things (Blender's `.001` suffixes are fine).

After editing, **Replace Parts from OBJ...** puts each object back on its part. Parts you didn't change are recognised and left exactly as they were, objects without an `__m` number are skipped (join new geometry into the part it belongs to with Ctrl+J), and a report lists what happened.

A game mesh can hold at most 65,536 vertices (after splitting along UV seams and hard edges). Faces keep their expressions when replaced: each blend shape (smile, frown, blink…) is rebuilt for the new vertices, each moving like the nearest vertex of the old face, so keep new geometry close to the face it replaces.

## Supported Games

- LEGO Batman 3
- LEGO Dimensions
- LEGO Marvel's Avengers
- LEGO Worlds
- LEGO Star Wars: The Force Awakens
- LEGO Ninjago
- LEGO Marvel Super Heroes 2
- LEGO DC Super-Villains