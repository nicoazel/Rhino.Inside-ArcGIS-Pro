using System;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Dialogs;

namespace RhinoInside.ArcGISPro
{
    /// <summary>
    /// Ribbon button that starts Rhino in-process. Referenced by Config.daml as
    /// className="LaunchRhinoInside".
    /// </summary>
    public class LaunchRhinoInside : Button
    {
        protected override void OnClick()
        {
            try
            {
                if (RhinoHost.IsStarted)
                {
                    MessageBox.Show(
                        $"Rhino is already running in-process.\n\n" +
                        $"Version: {RhinoHost.RhinoVersion}\n" +
                        $"RhinoCommon: {RhinoHost.LoadedRhinoCommon}",
                        "Rhino.Inside");
                    return;
                }

                RhinoHost.Start();

                MessageBox.Show(
                    $"Rhino started in-process.\n\n" +
                    $"Version: {RhinoHost.RhinoVersion}\n" +
                    $"Resolved from: {RhinoHost.RhinoSystemDirectory}\n" +
                    $"RhinoCommon: {RhinoHost.LoadedRhinoCommon}",
                    "Rhino.Inside");
            }
            catch (Exception ex)
            {
                // Surface the whole chain; Rhino.Inside failures are usually a nested TypeInitializer
                // or FileNotFound whose detail is lost if only the outer message is shown.
                var detail = ex.ToString();
                System.Diagnostics.Debug.WriteLine($"Rhino.Inside launch failed: {detail}");
                MessageBox.Show(
                    $"Rhino.Inside failed to start.\n\n{detail}",
                    "Rhino.Inside");
            }
        }
    }
}
