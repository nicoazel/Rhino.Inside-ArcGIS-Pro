using System;

namespace RhinoArcGIS.Core.Grasshopper
{
    public static class RuntimeCompatibility
    {
        // RH-99401: legacy IronPython imports fail in a .NET 10 host before this Rhino hotfix.
        public static readonly Version MinimumRhinoForNet10 = new Version(8, 36, 26282, 15001);
        public static string GrasshopperIssue(Version rhino, int runtimeMajor) => runtimeMajor >= 10 &&
            (rhino == null || rhino < MinimumRhinoForNet10)
            ? "Grasshopper in this .NET 10 ArcGIS host requires Rhino 8.36.26282.15001 or newer. " +
              "Older Rhino builds can block startup with an IronPython 'scriptcontext' dialog (McNeel RH-99401). " +
              "Update Rhino, close ArcGIS Pro, then relaunch."
            : null;
    }
}
