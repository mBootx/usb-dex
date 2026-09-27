using System.IO;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace DexStream.App.Services;

/// <summary>
/// Supplies the device agent jar that gets pushed to the phone.
/// </summary>
/// <remarks>
/// The jar is embedded in the executable when it was present at build time, which is what makes the
/// portable single-file build self-contained. A build without it still runs: the agent is then looked
/// for next to the executable, which is how a developer iterating on the agent uses a freshly built
/// one without rebuilding the host.
/// </remarks>
public static class AgentPackage
{
    private const string ResourceName = "DexStream.Agent.jar";
    private const string FileName = "dexstream-agent.jar";

    /// <summary>Where the agent came from, so the UI can explain a missing one.</summary>
    public enum Source
    {
        Embedded,
        NextToExecutable,
        RepositoryBuildOutput,
    }

    /// <summary>Reads the agent jar.</summary>
    /// <exception cref="FileNotFoundException">No agent jar could be found.</exception>
    public static (byte[] Content, Source Origin) Load(ILogger? logger = null)
    {
        Assembly assembly = typeof(AgentPackage).Assembly;

        using (Stream? embedded = assembly.GetManifestResourceStream(ResourceName))
        {
            if (embedded is not null)
            {
                using var buffer = new MemoryStream();
                embedded.CopyTo(buffer);
                logger?.LogDebug("Using the embedded device agent ({Bytes} bytes).", buffer.Length);
                return (buffer.ToArray(), Source.Embedded);
            }
        }

        string? directory = Path.GetDirectoryName(Environment.ProcessPath ?? assembly.Location);

        foreach ((string path, Source origin) in EnumerateCandidatePaths(directory))
        {
            if (File.Exists(path))
            {
                logger?.LogInformation("Using the device agent from {Path}.", path);
                return (File.ReadAllBytes(path), origin);
            }
        }

        throw new FileNotFoundException(
            $"The device agent ({FileName}) is neither embedded in this build nor present next to the " +
            "executable. Build it with agent/build.sh and rebuild, or copy the jar beside DexStream.exe.");
    }

    private static IEnumerable<(string Path, Source Origin)> EnumerateCandidatePaths(string? directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            yield break;
        }

        yield return (Path.Combine(directory, FileName), Source.NextToExecutable);

        // A development checkout: walk up to the repository root and look in the agent's build output.
        var current = new DirectoryInfo(directory);
        for (int depth = 0; depth < 6 && current is not null; depth++)
        {
            string candidate = Path.Combine(current.FullName, "agent", "build", FileName);
            yield return (candidate, Source.RepositoryBuildOutput);
            current = current.Parent;
        }
    }
}
