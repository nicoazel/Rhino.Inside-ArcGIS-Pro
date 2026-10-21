# Grasshopper map design example

`MapDesign.ghx` is a runnable Grasshopper definition for Rhino.Inside for ArcGIS Pro
2.0.0-alpha.1. It uses only Rhino's bundled components and the bundled ArcGIS library.

1. Install the release's `.esriAddinX`, then restart ArcGIS Pro. It includes the ArcGIS
   Grasshopper library and this example; no separate GHA installer or Yak package is needed.
   Rhino 8.36.26282.15001 or newer must already be installed and licensed. ArcGIS Pro 3.7
   and Windows 11 x64 are required. Do not install another copy of the GHA into Grasshopper Libraries.
2. Open a disposable project with an active map. Launch Rhino from the Rhino.Inside ribbon.
   Use a metre-based Rhino document and confirm its earth anchor before exchanging geometry.
3. Click **Grasshopper Core** as the first Grasshopper launch in the session. In Grasshopper,
   choose **File > Open** and open `MapDesign.ghx` downloaded from the release.
   The installed copy is also beside the add-in DLL under `Examples/Grasshopper/MapDesign.ghx`
   (ArcGIS manages that extraction directory).
4. The upper branch lists layers and reads selected features from the first listed layer.
   Select features in a feature layer and use **Refresh from ArcGIS** on the read component.
   With no eligible layers or selection, that branch may be empty; the lower branch is independent.
5. The lower branch previews a 20 x 15 x 30 metre massing box in the map. Move the
   **Lift (model units)** slider to change its height above the anchor. Preview is temporary;
   it creates neither GIS features nor baked Rhino objects.
6. To publish deliberately, inspect the destination, right-click **Create Layer** and choose
   **Create layer once**. Then right-click **Append Features** and choose **Publish keyed features**.
   The example creates a `GH_Design_Example` multipatch layer with `height_m = 30`.
   Recomputing the definition does not write features. Further explicit publish actions
   update the same keyed output set. Use a new Append component for a different key set.
7. Disable or delete **Map Preview** to remove the temporary overlay. Save your work explicitly.

The alpha is experimental. Basic 2D overlay appearance and embedded read/write execution
were observed; direct slider interaction and 3D visual acceptance remain pending.
Unattended shutdown can stop at a native ArcGIS project-save prompt. The full acceptance
matrix and install/upgrade/uninstall cycle are not certified. See the release's validation
record for which checks apply to its exact package. This definition is an example,
not an automatically executing feature-edit script.
