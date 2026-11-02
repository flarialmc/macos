using System.Collections.Generic;
using System.IO;
using Flarial.Runtime.Exceptions;

namespace Flarial.Runtime.Game;

public sealed class ModificationLibrary(string? path)
{
    // Upstream: LoadLibraryEx(DONT_RESOLVE_DLL_REFERENCES) succeeds and the image has IMAGE_FILE_DLL; checked on the PE headers instead.
    public bool IsLoadable => _path is { } && PortableImage.Open(_path) is { };

    internal PortableImage? Open() => _path is { } ? PortableImage.Open(_path) : null;

    internal bool HasExport(string name) => Open() is { } image && image.Exports.Contains(name);

    internal string AsPath()
    {
        if (_path is null)
            throw new LibraryLoadFailureException();

        if (!IsLoadable)
            throw new LibraryLoadFailureException();

        return _path;
    }

    /// <summary>The DLL's imported libraries (resolved against the game's system directory) followed by the DLL itself, in injection order.</summary>
    internal IReadOnlyList<string> AsImports()
    {
        var path = AsPath();

        if (Open() is not { } image || image.Imports.Count is 0)
            throw new LibraryLoadFailureException();

        var system = Platform.Platform.Injector.SystemDirectory.TrimEnd('\\');

        List<string> items = [];
        foreach (var name in image.Imports)
            items.Add($"{system}\\{name}");

        items.Add(path);
        return items;
    }

    readonly string? _path = Path.HasExtension(path) ? Path.GetFullPath(path) : null;
}
