using System.Reflection;

namespace SaveTether.Core;

/// <summary>Identity of this build of SaveTether.</summary>
public static class ProductInfo
{
    public const string Name = "SaveTether";

    /// <summary>
    /// The version set in Directory.Build.props, without build metadata
    /// (the "+commit" suffix the SDK may append).
    /// </summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var informational = typeof(ProductInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrEmpty(informational))
        {
            return "0.0.0";
        }

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}
