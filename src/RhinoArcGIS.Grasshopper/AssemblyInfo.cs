using System;
using System.Drawing;
using Grasshopper.Kernel;

namespace RhinoArcGIS.Grasshopper
{
    public sealed class AssemblyInfo : GH_AssemblyInfo
    {
        public override string Name => "Rhino.Inside–ArcGIS";
        public override Bitmap Icon => null;
        public override string Description => "Read, preview and explicitly publish Grasshopper designs in ArcGIS Pro.";
        public override Guid Id => new("911cd757-1bc0-423f-a226-a047ac120e53");
        public override string AuthorName => "Nicolas Azel";
        public override string AuthorContact => "https://github.com/nicoazel/Rhino.Inside-ArcGIS-Pro";
    }
}
