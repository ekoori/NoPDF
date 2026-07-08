using System.Reflection;

namespace NoPdf.App;

/// <summary>The application version, read from assembly metadata (set in Directory.Build.props).</summary>
public static class AppVersion
{
    /// <summary>Semantic version, e.g. "0.0.1-beta.01".</summary>
    public static string Informational
    {
        get
        {
            var v = typeof(AppVersion).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? "0.0.0";
            int plus = v.IndexOf('+'); // strip any +<git-sha>
            return plus >= 0 ? v[..plus] : v;
        }
    }

    /// <summary>Display string, e.g. "noPDF v0.0.1-beta.01".</summary>
    public static string Display => "noPDF v" + Informational;
}
