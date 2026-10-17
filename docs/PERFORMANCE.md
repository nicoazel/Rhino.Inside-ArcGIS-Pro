# Measured scale performance

Measured on 2026-10-17 in an embedded Rhino host with ArcGIS Pro 3.7.1.1904 and Rhino 8.34.26223.11001.

Frozen source: `5dd601f97165ca8a23a3ece609de5fa0091c47dd`.
Installer SHA-256: `f94396760c5081c09c3af36161a279474d60a85bd275f4827fde9850c25e5805`.

The machine used an Intel Core i9-10920X (12 cores / 24 logical processors), 128 GB RAM, and eight preparation workers. The host used the established default Rhino profile and a normal Rhino window. V-Ray for Rhino had been removed.

All data are invented street and parcel fixtures from `tools/generate-benchmark-data.py`. The run used `tools/benchmark.ps1` with sizes 1,000, 10,000 and 100,000, both datasets, and 1% edits. Times below are measured wall seconds.

| Dataset | Features | Pull | Clean Preview | Apply 1% geometry edits |
|---|---:|---:|---:|---:|
| Streets | 1,000 | 1.34 | 0.27 | 0.53 |
| Streets | 10,000 | 7.65 | 1.58 | 2.11 |
| Streets | 100,000 | 94.12 | 14.50 | 18.45 |
| Parcels | 1,000 | 1.05 | 0.26 | 0.53 |
| Parcels | 10,000 | 8.44 | 1.88 | 2.37 |
| Parcels | 100,000 | 99.67 | 15.55 | 22.67 |

All 60 recorded rows completed with no errors or skips. Pull counts matched each requested layer size. Geometry and attribute edits in both directions affected the expected 1%. Every final Preview was clean. Preparation samples matched serial results with zero mismatches or errors (all 1,000 features at the smallest size; 2,000 per larger case).

Earlier development notes recorded 4-4.5 minute pulls before parallel preparation; this run reproduces the optimized approximately 1.5-1.7 minute range. That historical comparison is not a controlled before/after run performed today.

At 100,000 streets, object creation used 44.7 s and preparation 42.8 s of the 93.0 s service total. At 100,000 parcels, creation used 67.6 s and preparation 23.6 s of the 98.1 s service total. Native Rhino object creation remains a substantial cost.

The existing 100,000-feature in-memory engine contracts also passed all five cases, including bulk/non-bulk sync and parallel-baseline equivalence. These complement the live measurements; they do not replace the licensed-host runs.

This is performance evidence, not release certification. Clean shutdown/reopen, actual keyboard navigation and local MCP approval dialogs remain pending. Raw logs and machine/project paths remain private.
