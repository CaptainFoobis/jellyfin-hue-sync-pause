using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Jellyfin.Plugin.HueSync.Tests;

public class VersionConsistencyTests
{
    private const string PluginRelativePath = "src/Jellyfin.Plugin.HueSync";

    [Fact]
    public void Release_version_matches_across_the_project()
    {
        var root = FindRepoRoot();
        var version = ReadProjectVersion(Path.Combine(root, PluginRelativePath, "Jellyfin.Plugin.HueSync.csproj"));
        Assert.Matches(@"^\d+\.\d+\.\d+\.0$", version);

        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, PluginRelativePath, "meta.json")));
        Assert.Equal(version, meta.RootElement.GetProperty("version").GetString());

        var build = File.ReadAllText(Path.Combine(root, "build.yaml"));
        Assert.Matches($@"(?m)^version:\s*""{Regex.Escape(version)}""\s*$", build);

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
        var releases = manifest.RootElement[0].GetProperty("versions");
        var current = releases[0];
        Assert.Equal(version, current.GetProperty("version").GetString());
        Assert.EndsWith($"/download/v{version}/HueSyncPause.zip", current.GetProperty("sourceUrl").GetString());

        var page = File.ReadAllText(Path.Combine(root, PluginRelativePath, "Configuration", "configPage.html"));
        var shortVersion = string.Join('.', version.Split('.')[..3]);
        Assert.Contains($"Settings page {shortVersion} auth", page, StringComparison.Ordinal);

        var parsed = Version.Parse(version);
        for (var i = 1; i < releases.GetArrayLength(); i++)
        {
            var olderText = releases[i].GetProperty("version").GetString();
            Assert.False(string.IsNullOrEmpty(olderText));
            var older = Version.Parse(olderText);
            Assert.True(parsed > older, $"List {version} before {olderText} in manifest.json.");
        }
    }

    [Fact]
    public void Plugin_source_newer_than_the_release_tag_has_a_higher_version()
    {
        var root = FindRepoRoot();
        if (!Directory.Exists(Path.Combine(root, ".git")))
        {
            return;
        }

        var version = Version.Parse(ReadProjectVersion(Path.Combine(root, PluginRelativePath, "Jellyfin.Plugin.HueSync.csproj")));
        var tag = LatestVersionTag(root);
        if (tag is null)
        {
            return;
        }

        var changed = ChangedPluginFiles(root, tag);
        if (changed.Count == 0)
        {
            return;
        }

        var taggedVersion = Version.Parse(tag[1..]);
        Assert.True(
            version > taggedVersion,
            $"Plugin files changed since {tag} ({string.Join(", ", changed)}). Bump the version above {taggedVersion}.");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "manifest.json"))
                && File.Exists(Path.Combine(dir.FullName, "build.yaml")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find the repository root from " + AppContext.BaseDirectory);
    }

    private static string ReadProjectVersion(string projectPath)
    {
        var doc = XDocument.Load(projectPath);
        string Read(string name)
        {
            var value = doc.Descendants(name).SingleOrDefault()?.Value.Trim();
            Assert.False(string.IsNullOrEmpty(value), projectPath + " is missing " + name + ".");
            return value!;
        }

        var version = Read("Version");
        Assert.Equal(version, Read("AssemblyVersion"));
        Assert.Equal(version, Read("FileVersion"));
        return version;
    }

    private static string? LatestVersionTag(string root)
    {
        var output = Git(root, "tag", "--list", "v*");
        Version? best = null;
        string? tag = null;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Length < 2 || line[0] != 'v' || !Version.TryParse(line[1..], out var parsed) || parsed.Revision < 0)
            {
                continue;
            }

            if (best is null || parsed > best)
            {
                best = parsed;
                tag = line;
            }
        }

        return tag;
    }

    private static List<string> ChangedPluginFiles(string root, string tag)
    {
        var diff = Git(root, "diff", "--name-only", tag, "--", PluginRelativePath);
        var untracked = Git(root, "ls-files", "--others", "--exclude-standard", "--", PluginRelativePath);
        return (diff + "\n" + untracked)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string Git(string root, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start);
        Assert.NotNull(process);
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, "git " + string.Join(' ', args) + " failed: " + stderr);
        return stdout;
    }
}
