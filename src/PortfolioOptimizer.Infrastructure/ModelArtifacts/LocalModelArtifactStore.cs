using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;

namespace PortfolioOptimizer.Infrastructure.ModelArtifacts;

public sealed class ModelArtifactOptions
{
    public const string Section = "ModelArtifacts";

    /// <summary>
    /// Directory for model artifacts (absolute, or relative to the content root). In containers mount a persistent volume
    /// here (/app/artifacts). Back it up together with the database: rows reference files by path and SHA-256.
    /// </summary>
    public string RootPath { get; set; } = "data/model-artifacts";
}

/// <summary>
/// Stores artifacts as &lt;root&gt;/&lt;experimentId&gt;/&lt;versionId&gt;.model. Writes are atomic (temporary file + rename).
/// Only paths of exactly that generated form are accepted when reading, so stored paths cannot escape the root.
/// </summary>
public sealed partial class LocalModelArtifactStore : IModelArtifactStore
{
    private readonly string _root;

    public LocalModelArtifactStore(IOptions<ModelArtifactOptions> options, IHostEnvironment environment)
    {
        var configured = options.Value.RootPath;
        _root = Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured));
        Directory.CreateDirectory(_root);
    }

    public async Task<StoredArtifact> SaveAsync(Guid experimentId, Guid versionId, byte[] content, CancellationToken ct)
    {
        var relative = $"{experimentId:N}/{versionId:N}.model";
        var full = Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".tmp";
        await File.WriteAllBytesAsync(temp, content, ct);
        File.Move(temp, full, overwrite: false);
        return new StoredArtifact(relative, Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), content.LongLength);
    }

    public async Task<byte[]> ReadAsync(string relativePath, CancellationToken ct)
    {
        var full = Resolve(relativePath);
        if (!File.Exists(full)) throw new FileNotFoundException($"Model artifact {relativePath} is missing from the artifact store.");
        return await File.ReadAllBytesAsync(full, ct);
    }

    public Task DeleteExperimentAsync(Guid experimentId, CancellationToken ct)
    {
        var dir = Path.Combine(_root, experimentId.ToString("N"));
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        return Task.CompletedTask;
    }

    private string Resolve(string relative)
    {
        if (!PathPattern().IsMatch(relative)) throw new InvalidOperationException("Invalid model artifact path.");
        var full = Path.GetFullPath(Path.Combine(_root, relative));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidOperationException("Invalid model artifact path.");
        return full;
    }

    [GeneratedRegex("^[0-9a-f]{32}/[0-9a-f]{32}\\.model$")]
    private static partial Regex PathPattern();
}
