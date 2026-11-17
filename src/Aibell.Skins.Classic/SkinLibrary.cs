using System.Security.Cryptography;

namespace Aibell.Skins.Classic;

/// <summary>An installed skin: <see cref="Id"/> is its file name in the skins folder; the built-in skin has an empty id.</summary>
public sealed record InstalledSkin(string Id, string Name)
{
    public bool IsBuiltIn => Id.Length == 0;
}

/// <summary>
/// The skins folder: installs skin archives after checking that they are classic skins, lists and removes them, and loads
/// them over the default skin. Only plain file names inside the folder are ever touched.
/// </summary>
public sealed class SkinLibrary(string directory)
{
    public static InstalledSkin BuiltIn { get; } = new(string.Empty, DefaultSkin.Name);

    public string Directory { get; } = Path.GetFullPath(directory);

    /// <summary>The built-in skin first, then the installed ones by name.</summary>
    public IReadOnlyList<InstalledSkin> List()
    {
        List<InstalledSkin> skins = [];
        if (System.IO.Directory.Exists(Directory))
        {
            foreach (string file in System.IO.Directory.EnumerateFiles(Directory))
            {
                string extension = Path.GetExtension(file);
                if (!extension.Equals(".wsz", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                skins.Add(new InstalledSkin(Path.GetFileName(file), Path.GetFileNameWithoutExtension(file)));
            }
        }
        skins.Sort((left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));
        skins.Insert(0, BuiltIn);
        return skins;
    }

    /// <summary>Installs a skin file from disk.</summary>
    public InstalledSkin InstallFile(string path)
    {
        FileInfo file = new(path);
        if (file.Length > SkinArchive.MaximumArchiveBytes) throw new InvalidSkinException(InvalidSkinReason.TooLarge);
        return Install(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>
    /// Checks and stores <paramref name="archive"/> as <paramref name="name"/>. Installing the same skin again returns the
    /// copy already there; a different skin with a taken name gets a numbered name.
    /// </summary>
    public InstalledSkin Install(byte[] archive, string name)
    {
        ArgumentNullException.ThrowIfNull(archive);
        string cleanName = CleanName(name);
        // Throws InvalidSkinException for anything that is not a drawable classic skin.
        ClassicSkin.Load(SkinArchive.Read(archive), DefaultSkin.Skin, cleanName);
        System.IO.Directory.CreateDirectory(Directory);
        byte[] hash = SHA256.HashData(archive);
        foreach (InstalledSkin existing in List().Where(skin => !skin.IsBuiltIn))
        {
            string path = Path.Combine(Directory, existing.Id);
            if (new FileInfo(path).Length == archive.Length && SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(hash))
                return existing;
        }
        string fileName = cleanName + ".wsz";
        for (int number = 2; File.Exists(Path.Combine(Directory, fileName)); number++) fileName = $"{cleanName} ({number}).wsz";
        string target = Path.Combine(Directory, fileName);
        string temporary = target + ".partial";
        File.WriteAllBytes(temporary, archive);
        File.Move(temporary, target);
        return new InstalledSkin(fileName, Path.GetFileNameWithoutExtension(fileName));
    }

    /// <summary>Deletes an installed skin; the built-in one cannot be removed.</summary>
    public void Uninstall(InstalledSkin skin)
    {
        ArgumentNullException.ThrowIfNull(skin);
        if (skin.IsBuiltIn) throw new InvalidOperationException("The built-in skin cannot be removed.");
        File.Delete(PathOf(skin.Id));
    }

    /// <summary>The skin with <paramref name="id"/>, or the built-in skin when the id is empty, unknown or unreadable.</summary>
    public ClassicSkin Load(string? id)
    {
        if (string.IsNullOrEmpty(id)) return DefaultSkin.Skin;
        try
        {
            string path = PathOf(id);
            if (!File.Exists(path)) return DefaultSkin.Skin;
            return ClassicSkin.Load(SkinArchive.Read(File.ReadAllBytes(path)), DefaultSkin.Skin, Path.GetFileNameWithoutExtension(path));
        }
        catch (Exception exception) when (exception is InvalidSkinException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return DefaultSkin.Skin;
        }
    }

    private string PathOf(string id)
    {
        if (id.Length == 0 || Path.GetFileName(id) != id || id is "." or "..") throw new ArgumentException("Not a skin id.", nameof(id));
        return Path.Combine(Directory, id);
    }

    /// <summary>A file name made of <paramref name="name"/>: no folders, no characters any system refuses, never empty.</summary>
    internal static string CleanName(string? name)
    {
        string clean = new([.. (name ?? string.Empty).Select(character =>
            char.IsControl(character) || Path.GetInvalidFileNameChars().Contains(character) || character is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'
                ? '_' : character)]);
        clean = clean.Trim().Trim('.').Trim();
        if (clean.Length > 80) clean = clean[..80].Trim();
        return clean.Length == 0 ? "Skin" : clean;
    }
}
