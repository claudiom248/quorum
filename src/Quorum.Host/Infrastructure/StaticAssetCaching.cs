using System.Text.RegularExpressions;
using Microsoft.AspNetCore.StaticFiles;

namespace Quorum.Host.Infrastructure;

/// <summary>
/// The UI's stylesheet, scripts and entry page have no fingerprint in their name, so a browser that kept an old copy would
/// show a new build without its styles. They are revalidated on every load (a 304 when nothing changed). Files whose URL
/// carries a fingerprint (the WebAssembly assemblies) change name with their content and are left to the framework.
/// </summary>
internal static partial class StaticAssetCaching
{
    /// <summary>
    /// A new instance for each middleware: the static file middleware fills in the options it is given (file provider,
    /// content types), so two of them must not share one.
    /// </summary>
    public static StaticFileOptions CreateOptions() => new() { OnPrepareResponse = Apply };

    private static void Apply(StaticFileResponseContext context)
    {
        // The request path, not the physical file: static web assets map a fingerprinted URL onto an unfingerprinted file.
        if (!Fingerprinted().IsMatch(context.Context.Request.Path.Value ?? string.Empty))
        {
            context.Context.Response.Headers.CacheControl = "no-cache";
        }
    }

    /// <summary>Names such as <c>Quorum.Web.Client.4eflxx5dlb.wasm</c> or <c>dotnet.native.k2v8ce1xwb.js.br</c>.</summary>
    [GeneratedRegex(@"\.[a-z0-9]{10}\.[a-z0-9]+(\.(br|gz))?$", RegexOptions.CultureInvariant)]
    private static partial Regex Fingerprinted();
}
