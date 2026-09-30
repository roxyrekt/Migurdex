using System.Xml.Linq;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
/// BULGU 3: <c>CopyPluginsToApi</c> hedefi <c>PluginProject</c> listesini kopyalıyordu ve
/// listede iki plugin eksikti; lokal build'de <c>.../Plugins</c> altında 12 dll çıkıyordu.
/// CI yayın yolu solution üzerinden 14'ünü kopyaladığı için fark yalnız kaynaktan derleyen
/// geliştiricide görünüyordu. Liste ile solution eşleşmeyi bırakırsa test kırılır.
/// </summary>
public sealed class PluginProjectListTests
{
    [Fact]
    public void CopyPluginsToApi_CoversEveryPluginProjectInTheSolution()
    {
        var repoRoot = FindRepositoryRoot();
        Assert.NotNull(repoRoot);

        var solutionProjects = ReadPluginProjects(Path.Combine(repoRoot!, "Migurdex.slnx"), "Project");
        var copiedProjects   = ReadPluginProjects(Path.Combine(repoRoot!, "Migurdex.Api", "Migurdex.Api.csproj"),
                                                   "PluginProject");

        Assert.NotEmpty(solutionProjects);
        Assert.Equal(solutionProjects.Count, copiedProjects.Count);
        Assert.Equal(solutionProjects, copiedProjects);
    }

    /// <summary>Verilen XML'deki elementin proje adlarını alfabetik sıralı döner.</summary>
    private static List<string> ReadPluginProjects(string xmlPath, string elementName)
    {
        var document = XDocument.Load(xmlPath);

        return document.Descendants(elementName)
                       .Select(element => element.Attribute("Path")?.Value
                                         ?? element.Attribute("Include")?.Value
                                         ?? string.Empty)
                       .Select(ToProjectName)
                       .Where(name => name.StartsWith("Migurdex.Plugins.", StringComparison.Ordinal))
                       .Distinct(StringComparer.Ordinal)
                       .OrderBy(name => name, StringComparer.Ordinal)
                       .ToList();
    }

    /// <summary>
    /// <c>Plugins/Migurdex.Plugins.AniHub/Migurdex.Plugins.AniHub.csproj</c> gibi yolları
    /// <c>Migurdex.Plugins.AniHub</c> adına indirger; uzantısız proje adlarını olduğu gibi bırakır.
    /// </summary>
    private static string ToProjectName(string pathOrProject)
    {
        var name = pathOrProject.Replace('\\', '/').Split('/')[^1];
        return name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            ? name[..^".csproj".Length]
            : name;
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Migurdex.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
