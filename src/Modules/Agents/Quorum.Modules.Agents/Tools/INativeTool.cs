using System.Text.Json;

namespace Quorum.Modules.Agents.Tools;

/// <summary>A tool Quorum runs in-process. It goes through the same executor pipeline as any other tool.</summary>
internal interface INativeTool
{
    /// <summary>The tool's own name, as in <see cref="NativeToolCatalog"/> (without the <c>native__</c> prefix).</summary>
    string Name { get; }

    /// <returns>The result the model receives.</returns>
    /// <exception cref="ToolFailedException">The call cannot be done; the message goes to the model as the error.</exception>
    Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken);
}

/// <summary>A tool ran and could not do what was asked. Its message is written for the model, which may retry with other arguments.</summary>
internal sealed class ToolFailedException : Exception
{
    public ToolFailedException()
    {
    }

    public ToolFailedException(string message)
        : base(message)
    {
    }

    public ToolFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
