namespace LockScreenSwitcher;

internal static class AppIconVariants
{
    private enum AssetKind { ThemedTargetSize, TargetSize, Scale }

    internal static IEnumerable<string> Order(string declaredPath, IEnumerable<string> files,
        int targetPixels, int scalePercent, bool lightTheme)
    {
        return files
            .Select(file => (File: file, Preference: GetPreference(declaredPath, file, lightTheme)))
            .Where(candidate => candidate.Preference.HasValue)
            .Select(candidate =>
            {
                var (kind, size) = candidate.Preference.GetValueOrDefault();
                int requested = kind == AssetKind.Scale ? scalePercent : targetPixels;
                return (candidate.File, Kind: kind, Smaller: size < requested, Distance: Math.Abs(size - requested));
            })
            .OrderBy(candidate => candidate.Kind)
            .ThenBy(candidate => candidate.Smaller)
            .ThenBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.File, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.File);
    }

    private static (AssetKind Kind, int Size)? GetPreference(string declaredPath, string file, bool lightTheme)
    {
        if (string.Equals(declaredPath, file, StringComparison.OrdinalIgnoreCase))
        {
            return (AssetKind.Scale, 100);
        }

        string stem = Path.GetFileNameWithoutExtension(declaredPath) + ".";
        string name = Path.GetFileNameWithoutExtension(file);
        if (!name.StartsWith(stem, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetExtension(file).Equals(Path.GetExtension(declaredPath), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string alternate = lightTheme ? "altform-lightunplated" : "altform-unplated";
        int targetSize = 0;
        int scale = 0;
        bool unplated = false;
        foreach (string qualifier in name[stem.Length..].Split('_'))
        {
            if (qualifier.Equals(alternate, StringComparison.OrdinalIgnoreCase))
            {
                unplated = true;
            }
            else if (qualifier.StartsWith("targetsize-", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(qualifier.AsSpan("targetsize-".Length), out int size) && size > 0)
            {
                targetSize = size;
            }
            else if (qualifier.StartsWith("scale-", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(qualifier.AsSpan("scale-".Length), out int percent) && percent > 0)
            {
                scale = percent;
            }
            else
            {
                return null;
            }
        }

        if (targetSize > 0 && scale == 0)
        {
            return (unplated ? AssetKind.ThemedTargetSize : AssetKind.TargetSize, targetSize);
        }

        return scale > 0 && targetSize == 0 && !unplated ? (AssetKind.Scale, scale) : null;
    }
}
