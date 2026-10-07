# Diorama

Diorama opens the 3D scenes of Traveller's Tales' LEGO games (`.GSC` levels and props, `.GHG` characters) so you can look at them, see what each part is for, and change them: swap a part's mesh for one made in Blender, replace textures, move parts and attachment points, then save a file the game can load.

**Which games:** it's built and checked against **LEGO DC Super-Villains** on PC. All 3,926 of its scenes and 3,911 texture files open, and save back byte for byte when nothing was changed. Other LEGO games may open too, but haven't been checked here.

**Is an edit safe in game?** Saving only changes the bytes you edited, and the first save keeps the original as `.bak`. Mesh replacements, texture swaps and moved points haven't all been tried in the game yet, so test each change in game, and keep the `.bak` until you have.

### Getting started

1. Extract the game if it's packed in `.DAT` archives, or point *Settings* at the game folder and use *File > Open From Source Location*.
2. *File > Open* (or drag onto the window) a scene, for example `CHARS\BIGFIG\DARKSEID\DARKSEID_DX11.GHG`. Its textures load from the `.NXG_TEXTURES` beside it.
3. Click a part, or pick it in the list, to see and edit it in the inspector. Hold the right mouse button to look around, with W A S D to move.
4. Work on a copy in your mod folder: right-click the scene and *Save GScene As…* first.

## Features

- View level / general geometry with full texture support.
- Scenes that use the shared LEGO texture page (`LEGOTPAGE\*.TEX`) find it in an extracted install by themselves, without setting the game folder in Settings.
- Open several scenes at once (pick or drag several files, or `Diorama.exe A.GSC B.GSC`), for example all the pieces of a hub area, and the camera frames them together.
- Hub level pieces (Arkham, Apokolips, Gotham...) show their textures, which the game keeps in a shared texture scene such as `ARKHAM_TEXTURES_DX11.GSC`; Edit Textures says which file a texture really lives in, and exports its image from there. Textures that can't be found anywhere show white instead of black.
- View the hierarchy of a scene, and change the name of special objects where possible. Each mesh is listed by its material and size (`MAT_Eyeshadow · 464 vertices`); hover it to see which mesh of the file it is, its triangles, how many bones it bends with and how many facial expressions it has.
- View and edit the transformations of objects in the scene, and save them.
- Swap the geometry in the scene with other geometry from a .OBJ file (see "Editing a part in Blender" below).
- Change the primary material colour
- **Edit Textures** (scene right-click menu): textures are labelled by their own name (`darkseid_diff`, `darkseid_nrm`), *Export DDS…* saves one to paint over (*Export all…* saves every one into a folder), and clicking the preview replaces it with your .DDS; the size, compression and mipmaps a replacement should match are shown.
- Hover a material setting (blend mode, alpha test, specular, glow, reflection, tints…) to read what it does.
- Characters keep up to four copies of their model (LOD 0 the most detailed, LOD 3 about a quarter of the vertices); the **LOD** menu picks which one you see, and a selected part says which LOD it's in, since replacing a mesh changes only that copy.
- Breakup parts (the pieces a bigfig falls apart into, shown with *Breakup*) sit on the body instead of a hip height below it.
- Faces and other models whose parts share one vertex buffer draw correctly (each part starts partway into the buffer).
- Saving writes a scene back exactly as the game stored it, apart from your edits (all 3,926 of DC Super-Villains' scenes come back byte for byte, faces with expressions included), so a mod only differs from the original where you changed something.
- **Shading** (toolbar, on by default) darkens surfaces turned away from you so a model's shape is visible; the game's flat colours alone hide it. The **View** menu holds the other display switches (material lighting, lightmaps, shadow impostors, frustum culling), each ticked when on.
- Toggle on/off lightmaps
- **Points** (toolbar) shows a character's points of interest, the named spots the game attaches things to. Picking one such as `Hat_Locator` or `RightHand_Locator` says what it's for; move it to change where that goes.
- With nothing picked, the inspector explains how to pick parts and move the camera.

## Editing a part in Blender

1. Select a part (click it in the viewport or the hierarchy) and press **Export mesh** in the inspector. Save as `.OBJ`: you also get an `.MTL` and the part's diffuse texture as `.DDS`, so it shows textured in Blender.
2. In Blender, use File > Import > Wavefront (.obj) with the default axes. Edit or replace the shape.
3. Export with File > Export > Wavefront (.obj), keeping the default axes (Y up, -Z forward) and ticking **UV Coordinates**, **Normals** and **Colors**. Triangulating faces is optional, because quads and n-gons are split on import.
4. Back in Diorama, select the same part and press **Replace mesh**. A report lists what was imported and what was carried over from the old shape:
   - **Bone weights** for character parts are copied from the nearest original vertex, so the new shape still follows the skeleton. Keep new geometry close to the part it replaces.
   - **Vertex colours** (the brick colour on most LEGO parts) are copied from the nearest original vertex if the OBJ has none.
   - Vertex alpha and lightmap / second UV sets always come from the nearest original vertex, since an OBJ can't hold them.
   - The part's culling box is recalculated, so a bigger shape isn't cut off at the screen edge.
   - On a character, **Replace in all LODs** does the same for the matching part (same material, same place) in each of its other LODs, so the change shows at every distance. The report lists which part each LOD got, or that a LOD has none.
5. Not right? **Undo replace** in the inspector (or *Undo Mesh Replacement* in the scene's right-click menu) puts the previous mesh back, one replacement at a time, including whole-model imports.
6. Right-click the scene in the hierarchy and choose **Save GScene** (overwrites the file you opened; the first save keeps the original as `.bak`) or **Save GScene As…** (a new file, for example in your mod folder; its textures, with any you replaced, are saved beside it as a matching `.NXG_TEXTURES`, and its `.RES` and shader files are copied under the new name). **Save Textures** writes replaced textures into the scene's own `.NXG_TEXTURES`, also keeping a `.bak` the first time; unchanged textures come back byte for byte.

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