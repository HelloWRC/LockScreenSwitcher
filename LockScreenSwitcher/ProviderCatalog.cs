using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Management.Deployment;
using Windows.Storage;
using Windows.Storage.Streams;

namespace LockScreenSwitcher;

internal sealed record LockScreenProvider(string Aumid, string Name, IRandomAccessStream? Logo = null,
    bool UsePlaceholder = false);

internal static class ProviderCatalog
{
    private static readonly XNamespace Foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
    private static readonly XNamespace Uap3 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/3";

    internal sealed record AppRegistration(string Aumid, string? LogoPath);

    internal static LockScreenProvider Default { get; } =
        new("Microsoft.LockApp_cw5n1h2txyewy!WindowsDefaultLockScreen", "Windows 默认锁屏");

    internal static async Task<LockScreenProvider[]> DiscoverAsync(uint dpi, bool lightTheme)
    {
        // The list displays a 32-DIP image. Target-size assets are specified in physical pixels.
        int targetPixels = (int)Math.Round(32 * dpi / 96d);
        int scalePercent = (int)Math.Round(100 * dpi / 96d);
        var providers = new Dictionary<string, LockScreenProvider>(StringComparer.OrdinalIgnoreCase);
        var manager = new PackageManager();
        foreach (var package in manager.FindPackagesForUserWithPackageTypes(
            string.Empty, PackageTypes.Main | PackageTypes.Optional))
        {
            foreach (var registration in ReadPackageProviders(package))
            {
                string aumid = registration.Aumid;
                if (providers.ContainsKey(aumid))
                {
                    continue;
                }

                bool isDefault = string.Equals(aumid, Default.Aumid, StringComparison.OrdinalIgnoreCase);
                string name = isDefault ? Default.Name : package.Id.Name;
                string? logoPath = registration.LogoPath;
                var logo = logoPath is null ? null :
                    await OpenDeclaredLogoAsync(package, logoPath, targetPixels, scalePercent, lightTheme);
                try
                {
                    var info = AppInfo.GetFromAppUserModelId(aumid)?.DisplayInfo;
                    if (!isDefault && !string.IsNullOrWhiteSpace(info?.DisplayName))
                    {
                        name = info.DisplayName;
                    }

                    if (logo is null && logoPath is not null &&
                        info?.GetLogo(new Size(targetPixels, targetPixels)) is { } reference)
                    {
                        logo = await reference.OpenReadAsync();
                    }
                }
                catch (Exception exception) when (IsPackageUnavailable(exception))
                {
                    // A hidden or recently removed app may have no registered display metadata.
                }

                providers.Add(aumid, new(aumid, name, logo, UsePlaceholder: logoPath is null));
            }
        }

        var defaultProvider = providers.Remove(Default.Aumid, out var installedDefault) ? installedDefault : Default;
        return [defaultProvider, .. providers.Values.OrderBy(provider => provider.Name, StringComparer.CurrentCulture)
            .ThenBy(provider => provider.Aumid, StringComparer.OrdinalIgnoreCase)];
    }

    internal static void EnsureAvailable(string aumid)
    {
        if (string.Equals(aumid, Default.Aumid, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        int separator = aumid.IndexOf('!');
        if (separator > 0)
        {
            var manager = new PackageManager();
            foreach (var package in manager.FindPackagesForUser(string.Empty, aumid[..separator]))
            {
                if (ReadPackageProviders(package).Any(app => string.Equals(app.Aumid, aumid, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }
            }
        }

        throw new InvalidOperationException($"所选应用已卸载或不再声明锁屏扩展。\n{aumid}");
    }

    private static AppRegistration[] ReadPackageProviders(Package package)
    {
        try
        {
            if (!package.Status.VerifyIsOK())
            {
                return [];
            }

            using var manifest = File.OpenRead(Path.Combine(package.InstalledPath, "AppxManifest.xml"));
            return ReadManifest(manifest, package.Id.FamilyName);
        }
        catch (Exception exception) when (IsPackageUnavailable(exception))
        {
            return [];
        }
    }

    internal static AppRegistration[] ReadManifest(Stream manifest, string familyName)
    {
        using var reader = XmlReader.Create(manifest, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var root = XDocument.Load(reader).Root;
        return root?.Element(Foundation + "Applications")?.Elements(Foundation + "Application")
            .Where(app => app.Element(Foundation + "Extensions")?.Elements(Uap + "Extension")
                .Any(extension => (string?)extension.Attribute("Category") == "windows.lockScreen") == true)
            .Where(app => !string.IsNullOrEmpty((string?)app.Attribute("Id")))
            .Select(app => new AppRegistration($"{familyName}!{(string?)app.Attribute("Id")}", ReadLogoPath(app)))
            .ToArray() ?? [];
    }

    private static string? ReadLogoPath(XElement app)
    {
        foreach (var visual in app.Elements().Where(element =>
            element.Name == Uap + "VisualElements" || element.Name == Uap3 + "VisualElements"))
        {
            var tile = visual.Element(Uap + "DefaultTile");
            string?[] candidates = [
                (string?)visual.Attribute("Square44x44Logo"),
                (string?)visual.Attribute("Square150x150Logo"),
                (string?)tile?.Attribute("Square71x71Logo"),
                (string?)tile?.Attribute("Square310x310Logo"),
                (string?)tile?.Attribute("Wide310x150Logo"),
            ];
            if (candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)) is { } path)
            {
                return path.Trim();
            }
        }

        return null;
    }

    private static async Task<IRandomAccessStream?> OpenDeclaredLogoAsync(Package package, string relativePath,
        int targetPixels, int scalePercent, bool lightTheme)
    {
        try
        {
            string directory = Path.TrimEndingDirectorySeparator(package.InstalledPath) + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(relativePath, directory);
            if (!path.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string pattern = Path.GetFileNameWithoutExtension(path) + "*" + Path.GetExtension(path);
            var files = Directory.EnumerateFiles(Path.GetDirectoryName(path)!, pattern);
            foreach (string candidate in AppIconVariants.Order(path, files, targetPixels, scalePercent, lightTheme))
            {
                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(candidate);
                    return await file.OpenReadAsync();
                }
                catch (Exception exception) when (IsPackageUnavailable(exception))
                {
                    // A package can be updated while its assets are being enumerated.
                }
            }
        }
        catch (Exception exception) when (IsPackageUnavailable(exception))
        {
            // Use Windows' registered resource resolver when the package directory is unavailable.
        }

        return null;
    }

    private static bool IsPackageUnavailable(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or COMException or ArgumentException or XmlException;
}
