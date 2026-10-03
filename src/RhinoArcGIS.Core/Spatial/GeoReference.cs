namespace RhinoArcGIS.Core.Spatial
{
    /// <summary>
    /// The model ↔ GIS coordinate map for a project, backed by a single affine transform. Built
    /// either from Rhino's Earth Anchor Point (preferred) or from a simple <see cref="ProjectAnchor"/>
    /// (fallback). See docs/DESIGN_georeferencing.md.
    /// </summary>
    public sealed class GeoReference : ICoordinateMap
    {
        private readonly AffineTransform _modelToGis;
        private readonly AffineTransform _gisToModel;
        private readonly double _modelToGisScale;

        public GeoReference(AffineTransform modelToGis)
        {
            _modelToGis = modelToGis ?? throw new System.ArgumentNullException(nameof(modelToGis));
            _gisToModel = modelToGis.Inverse();
            _modelToGisScale = modelToGis.ScaleFactor();
        }

        public Xyz ModelToGis(Xyz p) => _modelToGis.Apply(p);
        public Xyz GisToModel(Xyz p) => _gisToModel.Apply(p);
        public double ModelLengthToGis(double length) => length * _modelToGisScale;
        public double GisLengthToModel(double length) => _modelToGisScale == 0 ? length : length / _modelToGisScale;

        /// <summary>
        /// Compose the full model→GIS transform from Rhino's built-in model→ENU-metres transform and
        /// the anchor's coordinates in the target CRS.
        ///
        /// modelToGis = Translate(projectedOrigin) ∘ Scale(metres→CRS units) ∘ modelToEarthMetres
        /// </summary>
        public static GeoReference FromModelToEarth(AffineTransform modelToEarthMetres, Xyz projectedOrigin, UnitSystem crsUnits) =>
            FromModelToEarth(modelToEarthMetres, projectedOrigin, crsUnits.MetersPerUnit());

        /// <summary>As above, with the CRS unit given as metres per unit (NaN or 0: metres).</summary>
        public static GeoReference FromModelToEarth(AffineTransform modelToEarthMetres, Xyz projectedOrigin, double metresPerCrsUnit)
        {
            double metresToCrs = metresPerCrsUnit > 0 ? 1.0 / metresPerCrsUnit : 1.0;
            AffineTransform m =
                AffineTransform.Translation(projectedOrigin)
                    .Compose(AffineTransform.Scale(metresToCrs))
                    .Compose(modelToEarthMetres);
            return new GeoReference(m);
        }

        /// <summary>
        /// Build the model→GIS transform purely from anchor parameters (no RhinoCommon). Used as a
        /// fallback and for tests: scale model→metres, rotate model north to ENU north, move the
        /// model base point to the projected origin, then scale metres→CRS units.
        /// </summary>
        public static GeoReference FromAnchorParameters(EarthAnchor anchor, Xyz projectedOrigin, UnitSystem crsUnits) =>
            FromAnchorParameters(anchor, projectedOrigin, crsUnits.MetersPerUnit());

        /// <summary>As above, with the CRS unit given as metres per unit (NaN or 0: metres).</summary>
        public static GeoReference FromAnchorParameters(EarthAnchor anchor, Xyz projectedOrigin, double metresPerCrsUnit)
        {
            double modelToMetres = ModelToMetres(anchor);

            // model → ENU metres: translate base point to origin, rotate north to +Y, scale to metres.
            AffineTransform modelToEnu =
                AffineTransform.Scale(modelToMetres)
                    .Compose(AffineTransform.RotationZ(-anchor.NorthAngleDegrees))
                    .Compose(AffineTransform.Translation(new Xyz(-anchor.ModelBasePoint.X, -anchor.ModelBasePoint.Y, -anchor.ModelBasePoint.Z)));

            return FromModelToEarth(modelToEnu, projectedOrigin, metresPerCrsUnit);
        }

        /// <summary>
        /// The exact local frame: model -> ENU metres as <see cref="FromAnchorParameters"/> does, then
        /// ENU metres -> CRS through the CRS's own east and north basis vectors measured at the
        /// anchor (CRS units per metre of ground, see <see cref="GeoReferenceFactory"/>).
        /// </summary>
        /// <remarks>
        /// The planar path assumes the CRS is undistorted metres whose grid north is true north.
        /// Real projections are not: Web Mercator stretches ground by 1/cos(latitude) (about 1.32 at
        /// New York), a State Plane zone is in US feet with a scale factor, and every conformal
        /// projection's grid north differs from true north by the convergence angle (about a
        /// degree in UTM, several metres of error across a site). Measuring the basis from the
        /// projection itself captures all three; over a site of a few kilometres the remaining
        /// second-order distortion is millimetres.
        /// </remarks>
        public static GeoReference FromLocalFrame(EarthAnchor anchor, Xyz projectedOrigin,
            Xyz eastPerMetre, Xyz northPerMetre, double verticalPerMetre)
        {
            double modelToMetres = ModelToMetres(anchor);
            AffineTransform modelToEnu =
                AffineTransform.Scale(modelToMetres)
                    .Compose(AffineTransform.RotationZ(-anchor.NorthAngleDegrees))
                    .Compose(AffineTransform.Translation(new Xyz(-anchor.ModelBasePoint.X, -anchor.ModelBasePoint.Y, -anchor.ModelBasePoint.Z)));
            var enuToCrs = new AffineTransform(
                new[]
                {
                    eastPerMetre.X, northPerMetre.X, 0,
                    eastPerMetre.Y, northPerMetre.Y, 0,
                    0, 0, verticalPerMetre
                },
                new[] { projectedOrigin.X, projectedOrigin.Y, projectedOrigin.Z });
            return new GeoReference(enuToCrs.Compose(modelToEnu));
        }

        /// <summary>Metres per model unit for an anchor; 1 when unknown (callers check <see cref="EarthAnchor.IsValid"/>).</summary>
        static double ModelToMetres(EarthAnchor anchor)
        {
            double m = anchor.ModelToMetres();
            return m > 0 ? m : 1.0;
        }

        /// <summary>Back-compat path matching the original <see cref="CoordinateTransform"/> math.</summary>
        public static GeoReference FromProjectAnchor(ProjectAnchor anchor)
        {
            AffineTransform m =
                AffineTransform.Translation(anchor.GisPoint)
                    .Compose(AffineTransform.RotationZ(anchor.RotationDegrees))
                    .Compose(AffineTransform.Scale(anchor.Scale))
                    .Compose(AffineTransform.Translation(new Xyz(-anchor.RhinoPoint.X, -anchor.RhinoPoint.Y, -anchor.RhinoPoint.Z)));
            return new GeoReference(m);
        }
    }
}
