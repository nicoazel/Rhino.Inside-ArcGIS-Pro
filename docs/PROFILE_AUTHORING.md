# Per-link profile authoring

A synchronization profile defines how one ArcGIS feature layer and one Rhino layer exchange
geometry and attributes. Profiles are saved with their link in the Rhino document, so a `.3dm`
contains the pairing and the rules needed to resume it.

## Author a profile

1. Launch Rhino.Inside and select a complete pair in **Active layer pair**.
2. Open **Profile**. The editor reads the live ArcGIS schema and the selected Rhino layer.
3. Check only the fields the workflow needs.
4. For each included field, choose its Rhino user-text key and owner. Optional units and
   semicolon-separated validators travel with the mapping.
5. Select **Save profile**. Then run **Preview** before **Apply**.

**Restore defaults** removes the custom profile and rebuilds a safe, ArcGIS-owned mapping from the
current schema. It does not delete geometry, fields or the link.

## Field ownership

| Owner | Normal behavior |
|---|---|
| Rhino owns | A Rhino edit pushes to ArcGIS. An ArcGIS-only edit is an ownership violation and is not allowed to overwrite Rhino. |
| ArcGIS owns | An ArcGIS edit pulls to Rhino. A Rhino-only edit is an ownership violation and does not write ArcGIS. |
| Shared | Either side may edit. Editing both sides since the last baseline raises a conflict. |
| Rhino only | The value remains in Rhino and never crosses to ArcGIS. |
| ArcGIS only | The value remains in ArcGIS and never crosses to Rhino. |
| Derived | The value is computed from geometry when applicable; Rhino does not author it. |
| Locked | ArcGIS maintains the field; Rhino receives it read-only. |

Identity columns, `Shape_*`, `OBJECTID`, `FID`, `OID`, `GLOBALID` and other ArcGIS-managed fields
are forced to Locked or Derived as appropriate. The editor does not allow a saved profile to weaken
those protections.

The global **Let Rhino edit attributes** switch is a compatibility fallback only. It generates
Shared ordinary fields for a link without a custom profile. Once a custom profile is saved, the
per-field rules above are authoritative.

Supported validator names are `non_empty`, `positive_number`, `non_negative_number`, and inclusive
`range:[minimum,maximum]`. Unknown or malformed rules stop the profile at save time. Type, coded
domain and named-rule validation also run before PushService or SyncService writes a field.

## Schema and geometry target

The editor shows CRS and the geometry target from the live feature layer as read-only context. It
does not currently expose geometry ownership, pull/push mode, elevation or extrusion settings.
Point Z, Polyline Z, Polygon Z and Multipatch are supported. A saved profile is reconciled against
the schema before every run: type/domain metadata are refreshed and ArcGIS-managed fields are
protected again.

For Rhino-first publishing, planar regions remain Polygon. Meshes plus three-dimensional or
nonplanar Breps, surfaces and extrusions produce a mesh payload and create a Z-enabled Multipatch
feature class. The new link receives a Shared design-field profile so the initial user text is pushed
and remains editable after the global fallback returns to its prior value.

## Persistence and change safety

Custom JSON is stored under `gis.link.N.profile` beside the link's ArcGIS layer, Rhino layer and
direction. Saving or reopening the `.3dm` restores it. A missing profile means generated defaults;
invalid JSON or blocking validation stops the run with a visible error instead of silently using a
different rule set.

Changing field ownership without changing a value should leave Preview clean. A later allowed field
edit updates the existing tracked feature and advances the baseline; it must not create a duplicate.
Profile edits do not write either model until Apply.

## Help and keyboard access

Every Profile action has an accessible name and a tooltip that describes its effect. The Profile
information button opens persistent guidance for field inclusion, Rhino keys, ownership, validators
and save-before-run behavior, so the instructions do not depend on hover timing. From the
Synchronization tab, Tab enters the current workspace; the four workspace choices, field table,
editors, Restore defaults and Save profile are keyboard reachable.

## Validation and troubleshooting

- **Field missing:** refresh the pane. If the ArcGIS schema changed, reopen Profile so reconciliation
  can show the current fields. A mapping to a removed field is not silently written.
- **Rhino edit is held:** check that the field is included and is Rhino-owned or Shared, and that the
  pair direction permits push.
- **ArcGIS edit is held:** check that the owner permits pull and that the pair is not push-only.
- **Conflict:** both sides changed since the stored baseline. Preview the row and choose Manual,
  Prefer Rhino or Prefer ArcGIS deliberately.
- **Managed field cannot be changed:** this is intentional. ArcGIS owns identity and shape metrics.

Automated Core contracts verify profile validation, schema reconciliation, protected ownership,
document storage and bridge coverage. `tools/e2e.ps1` is the live acceptance test: it saves a
Rhino-owned rule through the pane while the global fallback is off, changes the Rhino value, updates
the existing ArcGIS row and verifies the next Preview is clean inside a real ArcGIS Pro + Rhino host.
