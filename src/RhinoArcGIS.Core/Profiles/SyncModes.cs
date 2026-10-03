namespace RhinoArcGIS.Core.Profiles
{
    /// <summary>Pull modes (spec 02 §4, 06 §9).</summary>
    public enum PullMode
    {
        Context = 0,
        EditableLinked,
        AttributeOnly,
        GeometryOnly,
        SelectionExtent,
        Query,
        Delta
    }

    /// <summary>Push modes (spec 06 §9).</summary>
    public enum PushMode
    {
        Create = 0,
        GeometryOnly,
        AttributeOnly,
        GeometryAndAttributes,
        FullSync
    }

    /// <summary>Which side owns geometry for a layer (spec 06 §8 geometry_owner).</summary>
    public enum GeometryOwner
    {
        Rhino = 0,
        ArcGis
    }

    /// <summary>How extruded polygons derive their height (spec 06 §8 extrusion_mode).</summary>
    public enum ExtrusionMode
    {
        None = 0,
        BaseHeight,
        AbsoluteTop
    }
}
