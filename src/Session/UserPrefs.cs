using Godot;

namespace Wipebound.Session;

/// <summary>
/// The player's own small settings file, and the one rule that keeps it whole.
///
/// It had two writers with different discipline. PlayerIdentity read the file
/// before saving; the class preference did not, and constructed a fresh
/// ConfigFile instead -- so choosing a class silently erased the identity stored
/// beside it. Every launch then minted a new id, which is precisely what "stable
/// for this install" existed to prevent, and nothing in a log said so: the run
/// records looked perfectly well formed, with a different player every time.
///
/// So there is one writer now, and it always merges.
/// </summary>
public static class UserPrefs
{
    public const string Path = "user://player.cfg";

    public static Variant Read(string section, string key, Variant fallback)
    {
        var file = new ConfigFile();
        if (file.Load(Path) != Error.Ok) return fallback;
        return file.GetValue(section, key, fallback);
    }

    /// <summary>Read, change, write. Never write without reading.</summary>
    public static void Write(string section, params (string Key, Variant Value)[] entries)
    {
        var file = new ConfigFile();

        // A missing file is not an error here: the first write creates it.
        file.Load(Path);

        foreach ((string key, Variant value) in entries) file.SetValue(section, key, value);
        Commit(file);
    }

    /// <summary>Drop a whole section, for a reset and for tests that must not
    /// leave their scratch behind in a real player's settings.</summary>
    public static void Forget(string section)
    {
        var file = new ConfigFile();
        if (file.Load(Path) != Error.Ok) return;
        if (!file.HasSection(section)) return;

        file.EraseSection(section);
        Commit(file);
    }

    /// <summary>
    /// Write beside the file, then move it into place.
    ///
    /// ConfigFile.Save truncates and then writes, so a second instance reading
    /// during that window sees an empty file -- and an empty file means no id,
    /// which means minting a new one and silently becoming a different player.
    /// That is the same failure the single-writer rule above was introduced to
    /// fix, reached by a different route, and it is reachable whenever two
    /// clients run on one machine, which is exactly how this project tests.
    ///
    /// A rename is atomic, so a reader sees the whole old file or the whole new
    /// one. The same discipline RunSubmitter uses for its spool.
    /// </summary>
    private static void Commit(ConfigFile file)
    {
        string staging = Path + ".tmp";
        if (file.Save(staging) != Error.Ok) return;

        using var dir = DirAccess.Open("user://");
        if (dir is null) return;

        // Rename over the target. Godot's rename replaces an existing file.
        dir.Rename(staging.Replace("user://", ""), Path.Replace("user://", ""));
    }
}
