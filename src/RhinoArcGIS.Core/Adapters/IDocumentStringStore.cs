namespace RhinoArcGIS.Core.Adapters
{
    /// <summary>
    /// Document-level key/value text that travels with the Rhino file. Optional: an adapter that
    /// implements it lets the sync remember which features a Rhino layer already holds, which is
    /// what tells "deleted in Rhino" apart from "new in ArcGIS".
    /// </summary>
    public interface IDocumentStringStore
    {
        /// <summary>The value for a key, or null when it is not set.</summary>
        string GetDocumentString(string key);

        /// <summary>Sets a key; a null value removes it.</summary>
        void SetDocumentString(string key, string value);
    }
}
