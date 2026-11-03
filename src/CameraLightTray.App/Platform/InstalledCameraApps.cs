using System.Diagnostics;
using System.Xml.Linq;
using CameraLightTray.Core;
using Windows.Management.Deployment;

namespace CameraLightTray.App.Platform;

internal static class InstalledCameraApps
{
    internal static CameraApp[] Read(string sid)
    {
        var apps = new List<CameraApp>();
        foreach (var package in new PackageManager().FindPackagesForUser(sid))
        {
            try
            {
                if (package.IsFramework || package.IsResourcePackage) continue;
                var manifest = XDocument.Load(Path.Combine(package.InstalledLocation.Path, "AppxManifest.xml"));
                if (!manifest.Descendants().Any(e => e.Name.LocalName == "DeviceCapability" && (string?)e.Attribute("Name") == "webcam"))
                    continue;
                foreach (var application in manifest.Descendants().Where(e => e.Name.LocalName == "Application"))
                {
                    if ((string?)application.Attribute("Executable") is not { Length: > 0 } executable) continue;
                    apps.Add(new("package:" + package.Id.FamilyName + "|" + Path.GetFileName(executable),
                        package.DisplayName, CameraStatus.Unknown));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                System.Runtime.InteropServices.COMException or System.Xml.XmlException)
            {
                Trace.WriteLine("Camera capability manifest unavailable: " + ex.GetType().Name);
            }
        }
        return apps.DistinctBy(a => a.Key, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
