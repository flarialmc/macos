using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Flarial.Runtime.Linux;

public static class GameContent
{
    const string ManifestFile = "manifest.json", LevelFile = "level.dat";

    internal static string? DataDirectoryOverride;

    /// active installed game dir, else Paths.Games; created if missing
    public static string InstallationDirectory => Directory.CreateDirectory(LinuxGameService.ActiveDir() ?? Paths.Games).FullName;

    /// <Paths.Prefix>/drive_c/users/<WineUser>/AppData/Roaming/Minecraft Bedrock ; created if missing
    public static string DataDirectory => Directory.CreateDirectory(DataDirectoryOverride ?? Path.Combine(Paths.Prefix, "drive_c", "users", Paths.WineUser, "AppData", "Roaming", "Minecraft Bedrock")).FullName;

    /// imports a .mcpack/.zip/.mcaddon/.mcworld/.mctemplate file; returns a user-facing summary
    public static string Import(string file)
    {
        var name = Path.GetFileName(file);
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext is not (".mcpack" or ".zip" or ".mcaddon" or ".mcworld" or ".mctemplate")) throw Invalid(name);

        var units = new List<Unit>();
        var moved = new List<string>();
        try
        {
            ZipArchive zip;
            try { zip = ZipFile.OpenRead(file); }
            catch (InvalidDataException) { throw Invalid(name); }
            using (zip) Stage(zip, name, ext, units);
            if (units.Count == 0) throw Invalid(name);

            var items = new List<(string Kind, string Label)>();
            foreach (var unit in units)
            {
                var dest = Unique(unit.Parent, unit.Name);
                Directory.Move(unit.Stage, dest);
                moved.Add(dest);
                items.Add((unit.Kind, unit.Label));
            }
            return items.Count == 1
                ? $"Imported {Describe(items[0])}"
                : $"Imported {items.Count} items from \"{name}\": {string.Join(", ", items.Select(Describe))}";
        }
        catch
        {
            foreach (var dest in moved) Directory.Delete(dest, true);
            foreach (var unit in units) if (Directory.Exists(unit.Stage)) Directory.Delete(unit.Stage, true);
            throw;
        }
    }

    /// Bedrock's user folder: the most recently written numeric Users/<xuid>, else Users/Shared.
    static string UserRoot
    {
        get
        {
            var users = Path.Combine(DataDirectory, "Users");
            var newest = Directory.Exists(users)
                ? Directory.GetDirectories(users)
                    .Where(d => Path.GetFileName(d) is { Length: > 0 } n && n.All(char.IsAsciiDigit))
                    .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d))
                    .FirstOrDefault()
                : null;
            return newest ?? Path.Combine(users, "Shared");
        }
    }

    static string PacksDir(string sub) => Path.Combine(DataDirectory, "Users", "Shared", "games", "com.mojang", sub);
    static string WorldsDir => Path.Combine(UserRoot, "games", "com.mojang", "minecraftWorlds");
    static string TemplatesDir => Path.Combine(UserRoot, "games", "com.mojang", "world_templates");

    sealed record Unit(string Kind, string Label, string Parent, string Name, string Stage);

    static void Stage(ZipArchive zip, string file, string ext, List<Unit> units)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        var packs = RootsOf(zip, ManifestFile);
        var worlds = RootsOf(zip, LevelFile);
        switch (ext)
        {
            case ".mcworld":
                if (worlds.Count == 0) throw Invalid(file);
                AddWorld(zip, file, stem, worlds[0], units);
                break;
            case ".mctemplate":
                if (packs.Count + worlds.Count == 0) throw Invalid(file);
                AddTemplate(zip, file, stem, packs.Count > 0 ? packs[0] : worlds[0], units);
                break;
            case ".mcaddon":
                foreach (var entry in zip.Entries.Where(e => !Name(e).Contains('/') && Path.GetExtension(Name(e)).ToLowerInvariant() is ".mcpack" or ".mcworld" or ".mctemplate" or ".zip"))
                    StageNested(entry, units);
                foreach (var prefix in packs) AddPack(zip, file, stem, prefix, units);
                break;
            default:
                if (packs.Count == 0)
                {
                    if (worlds.Count == 0) throw Invalid(file);
                    AddWorld(zip, file, stem, worlds[0], units);
                }
                foreach (var prefix in packs) AddPack(zip, file, stem, prefix, units);
                break;
        }
    }

    static void StageNested(ZipArchiveEntry entry, List<Unit> units)
    {
        using var bytes = new MemoryStream();
        using (var s = entry.Open()) s.CopyTo(bytes);
        bytes.Position = 0;
        ZipArchive inner;
        try { inner = new ZipArchive(bytes, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { throw Invalid(Name(entry)); }
        using (inner) Stage(inner, Name(entry), Path.GetExtension(Name(entry)).ToLowerInvariant(), units);
    }

    static void AddPack(ZipArchive zip, string file, string stem, string prefix, List<Unit> units)
    {
        using var doc = ReadManifest(zip, prefix) ?? throw Invalid(file);
        var label = Header(doc, "name") ?? stem;
        var name = Sanitize(label) ?? Sanitize(Header(doc, "uuid")) ?? RandomId();
        var types = ModuleTypes(doc);
        if (types.Contains("world_template"))
        {
            AddUnit(zip, file, "world template", label, TemplatesDir, name, prefix, units);
            return;
        }
        var targets = new List<(string Kind, string Dir)>();
        if (types.Contains("resources")) targets.Add(("resource pack", PacksDir("resource_packs")));
        if (types.Contains("data") || types.Contains("script")) targets.Add(("behavior pack", PacksDir("behavior_packs")));
        if (types.Contains("skin_pack")) targets.Add(("skin pack", PacksDir("skin_packs")));
        if (targets.Count == 0) throw Invalid(file);
        foreach (var (kind, dir) in targets) AddUnit(zip, file, kind, label, dir, name, prefix, units);
    }

    static void AddTemplate(ZipArchive zip, string file, string stem, string prefix, List<Unit> units)
    {
        using var doc = ReadManifest(zip, prefix);
        var label = Header(doc, "name") ?? stem;
        AddUnit(zip, file, "world template", label, TemplatesDir, Sanitize(label) ?? RandomId(), prefix, units);
    }

    static void AddWorld(ZipArchive zip, string file, string stem, string prefix, List<Unit> units)
    {
        var label = ReadText(zip, prefix + "levelname.txt") ?? stem;
        AddUnit(zip, file, "world", label, WorldsDir, RandomId(), prefix, units);
    }

    /// extracts into a hidden staging folder beside the target; Import moves it into place only after every unit staged
    static void AddUnit(ZipArchive zip, string file, string kind, string label, string parent, string name, string prefix, List<Unit> units)
    {
        Directory.CreateDirectory(parent);
        var unit = new Unit(kind, label, parent, name, Path.Combine(parent, "." + Guid.NewGuid().ToString("N") + ".importing"));
        units.Add(unit);
        Extract(zip, file, prefix, unit.Stage);
    }

    static void Extract(ZipArchive zip, string file, string prefix, string dest)
    {
        var root = Directory.CreateDirectory(dest).FullName + Path.DirectorySeparatorChar;
        foreach (var entry in zip.Entries)
        {
            if (!Name(entry).StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rel = Name(entry)[prefix.Length..];
            if (rel.Length == 0) continue;
            var target = Path.GetFullPath(Path.Combine(root, rel));
            if (!target.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException($"{file} contains an unsafe path.");
            if (rel.EndsWith('/')) Directory.CreateDirectory(target);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, true);
            }
        }
    }

    /// folders (prefixes) holding a file: the root, or a single top folder
    static List<string> RootsOf(ZipArchive zip, string file) =>
        zip.Entries.Select(Name)
            .Where(n => n == file || (n.EndsWith("/" + file, StringComparison.Ordinal) && n.Count(c => c == '/') == 1))
            .Select(n => n[..^file.Length])
            .ToList();

    /// entry path with '/' separators; some Windows tools store '\\'
    static string Name(ZipArchiveEntry entry) => entry.FullName.Replace('\\', '/');

    static ZipArchiveEntry? Find(ZipArchive zip, string path) => zip.Entries.FirstOrDefault(e => Name(e) == path);

    static JsonDocument? ReadManifest(ZipArchive zip, string prefix)
    {
        var entry = Find(zip, prefix + ManifestFile);
        if (entry is null) return null;
        try
        {
            using var reader = new StreamReader(entry.Open());
            var doc = JsonDocument.Parse(reader.ReadToEnd(), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc;
            doc.Dispose();
            return null;
        }
        catch (JsonException) { return null; }
    }

    static string? ReadText(ZipArchive zip, string path)
    {
        var entry = Find(zip, path);
        if (entry is null) return null;
        using var reader = new StreamReader(entry.Open());
        var text = reader.ReadToEnd().Trim();
        return text.Length == 0 ? null : text;
    }

    static string? Header(JsonDocument? doc, string key)
    {
        if (doc is null || !doc.RootElement.TryGetProperty("header", out var header) || header.ValueKind != JsonValueKind.Object) return null;
        return header.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    static HashSet<string> ModuleTypes(JsonDocument doc)
    {
        var types = new HashSet<string>();
        if (!doc.RootElement.TryGetProperty("modules", out var modules) || modules.ValueKind != JsonValueKind.Array) return types;
        foreach (var module in modules.EnumerateArray())
            if (module.ValueKind == JsonValueKind.Object && module.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
                types.Add(type.GetString()!);
        return types;
    }

    static string? Sanitize(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((name ?? "").Where(c => !invalid.Contains(c)).ToArray()).Trim().Trim('.').Trim();
        return clean.Length == 0 ? null : clean.Length > 80 ? clean[..80].TrimEnd() : clean;
    }

    static string Unique(string parent, string name)
    {
        var dest = Path.Combine(parent, name);
        for (var i = 2; Directory.Exists(dest) || File.Exists(dest); i++) dest = Path.Combine(parent, $"{name} ({i})");
        return dest;
    }

    static string RandomId() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(9)).Replace('+', '-').Replace('/', '_');

    static string Describe((string Kind, string Label) item) => $"{item.Kind} \"{item.Label}\"";

    static InvalidDataException Invalid(string file) => new($"{file} is not a Minecraft pack, add-on, world or template.");

    internal static Task<int> SelfTest()
    {
        var failed = 0;
        void Check(bool ok, string what) { Console.WriteLine((ok ? "ok   " : "FAIL ") + what); if (!ok) failed++; }
        static string Outcome(Func<string> act) { try { return act(); } catch (Exception e) { return e.GetType().Name + ": " + e.Message; } }
        static (string, byte[]) T(string name, string text) => (name, Encoding.UTF8.GetBytes(text));
        static byte[] Zip(params (string Name, byte[] Data)[] entries)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (name, data) in entries)
                {
                    using var s = zip.CreateEntry(name).Open();
                    s.Write(data);
                }
            return ms.ToArray();
        }

        var root = Path.Combine(Path.GetTempPath(), "flarial-import-selftest-" + Guid.NewGuid().ToString("N"));
        var inbox = Directory.CreateDirectory(Path.Combine(root, "in")).FullName;
        string Write(string name, params (string Name, byte[] Data)[] entries) { var path = Path.Combine(inbox, name); File.WriteAllBytes(path, Zip(entries)); return path; }
        DataDirectoryOverride = Path.Combine(root, "data");
        try
        {
            var packs = Path.Combine(DataDirectory, "Users", "Shared", "games", "com.mojang");
            var rp = Write("rp.mcpack",
                T("NiceRP/manifest.json", "{ // comment\n \"format_version\": 2, \"header\": {\"name\": \"Nice RP\", \"uuid\": \"u1\"}, \"modules\": [{\"type\": \"resources\"},], }"),
                T("NiceRP/textures/a.txt", "x"));
            Check(Outcome(() => GameContent.Import(rp)) == "Imported resource pack \"Nice RP\"", "resource pack in folder with comment and trailing commas");
            Check(File.Exists(Path.Combine(packs, "resource_packs", "Nice RP", "textures", "a.txt")), "resource pack content extracted without the top folder");
            Check(Outcome(() => GameContent.Import(rp)) == "Imported resource pack \"Nice RP\"" && Directory.Exists(Path.Combine(packs, "resource_packs", "Nice RP (2)")), "duplicate name gets a numbered folder");

            Check(Outcome(() => GameContent.Import(Write("beh.mcpack", T("manifest.json", "{\"header\":{\"name\":\"Beh\"},\"modules\":[{\"type\":\"data\"}]}")))) == "Imported behavior pack \"Beh\"", "behavior pack at zip root");
            Check(Directory.Exists(Path.Combine(packs, "behavior_packs", "Beh")), "behavior pack folder exists");
            Check(Outcome(() => GameContent.Import(Write("win.mcpack", T("WinRP\\manifest.json", "{\"header\":{\"name\":\"Win\"},\"modules\":[{\"type\":\"resources\"}]}"), T("WinRP\\textures\\b.txt", "x")))) == "Imported resource pack \"Win\"" && File.Exists(Path.Combine(packs, "resource_packs", "Win", "textures", "b.txt")), "backslash-separated entries");

            var xuid = Directory.CreateDirectory(Path.Combine(DataDirectory, "Users", "2535400123")).FullName;
            Check(Outcome(() => GameContent.Import(Write("world.mcworld", T("Save/level.dat", "x"), T("Save/levelname.txt", "My World")))) == "Imported world \"My World\"", "mcworld with levelname.txt");
            var worlds = Directory.GetDirectories(Path.Combine(xuid, "games", "com.mojang", "minecraftWorlds"));
            Check(worlds.Length == 1 && File.Exists(Path.Combine(worlds[0], "level.dat")), "world goes to the newest numeric user dir with a random id");

            var addon = Write("addon.mcaddon",
                T("A/manifest.json", "{\"header\":{\"name\":\"AddA\"},\"modules\":[{\"type\":\"resources\"}]}"),
                T("B/manifest.json", "{\"header\":{\"name\":\"AddB\"},\"modules\":[{\"type\":\"script\"}]}"),
                T("B/scripts/x.js", "1"),
                ("Gamma.mcpack", Zip(T("manifest.json", "{\"header\":{\"name\":\"Gamma\"},\"modules\":[{\"type\":\"resources\"}]}"))));
            var addonResult = Outcome(() => GameContent.Import(addon));
            Check(addonResult.StartsWith("Imported 3 items from \"addon.mcaddon\"", StringComparison.Ordinal), "mcaddon with two folder packs and a nested mcpack");
            Check(Directory.Exists(Path.Combine(packs, "resource_packs", "AddA")) && Directory.Exists(Path.Combine(packs, "behavior_packs", "AddB")) && Directory.Exists(Path.Combine(packs, "resource_packs", "Gamma")), "mcaddon packs are in place");

            var slip = Write("slip.mcpack", T("manifest.json", "{\"header\":{\"name\":\"Slip\"},\"modules\":[{\"type\":\"resources\"}]}"), T("../evil.txt", "x"));
            Check(Outcome(() => GameContent.Import(slip)) == "InvalidDataException: slip.mcpack contains an unsafe path.", "zip-slip entry is rejected");
            Check(!File.Exists(Path.Combine(DataDirectory, "evil.txt")) && !Directory.Exists(Path.Combine(packs, "resource_packs", "Slip")) && !Directory.GetDirectories(Path.Combine(packs, "resource_packs")).Any(d => d.EndsWith(".importing", StringComparison.Ordinal)), "zip-slip leaves nothing behind");

            Check(Outcome(() => GameContent.Import(Write("notes.zip", T("readme.txt", "hi")))) == "InvalidDataException: notes.zip is not a Minecraft pack, add-on, world or template.", "random zip is rejected");
            var bad = Path.Combine(inbox, "bad.mcpack");
            File.WriteAllText(bad, "not a zip");
            Check(Outcome(() => GameContent.Import(bad)) == "InvalidDataException: bad.mcpack is not a Minecraft pack, add-on, world or template.", "non-zip file is rejected");
        }
        finally
        {
            DataDirectoryOverride = null;
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
        return Task.FromResult(failed == 0 ? 0 : 1);
    }
}
