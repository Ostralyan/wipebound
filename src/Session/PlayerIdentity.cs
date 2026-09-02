using System;
using System.Text;
using Godot;

namespace Wipebound.Session;

/// <summary>
/// Who a run belongs to.
///
/// Every number this project protects -- dedicated-server authority, content
/// fingerprints, digest idempotency, movement validated to 300ms -- was guarding
/// a row that identified its player by ENet peer id: a random integer minted
/// fresh on every connection. Two runs by the same person shared no key, so the
/// ladder could rank but could not attribute.
///
/// PROVENANCE TRAVELS WITH THE CLAIM, exactly as authority already does for the
/// run itself. A record carries an id, a name, and how much the server actually
/// knows about them. Today that is "anonymous": a value this install generated
/// for itself, which the server accepted without checking, because there is
/// nothing yet to check it against. When a platform ticket can be verified, the
/// same fields carry a verified provenance and the ladder's policy changes
/// without the schema changing.
///
/// An anonymous id therefore identifies AN INSTALL, not a person. It cannot
/// survive a reinstall and it cannot be trusted against impersonation. Saying so
/// is the point of the field.
/// </summary>
public static class PlayerIdentity
{
    /// The server verified nothing. It is still the server that decides this --
    /// see NetworkManager.DeclarePlayer -- because a client that could name its
    /// own provenance would make the field worthless.
    public const string Anonymous = "anonymous";

    public const int MaxNameLength = 24;
    public const int MinIdLength = 8;
    public const int MaxIdLength = 64;

    private const string Section = "player";

    private static string _id;
    private static string _name;

    /// <summary>Stable for this install. Generated once, then never again.</summary>
    public static string Id
    {
        get
        {
            Load();
            return _id;
        }
    }

    public static string DisplayName
    {
        get
        {
            Load();
            return _name;
        }
    }

    public static void SetDisplayName(string name)
    {
        Load();
        _name = CleanName(name);
        Save();
    }

    /// <summary>
    /// A name fit to print.
    ///
    /// Control characters are stripped rather than escaped, and NEWLINES ARE THE
    /// REASON. Names reach the server's log, which is read line by line by
    /// people and by the tests in tools/, so a client allowed to send one could
    /// write its own log lines and claim anything happened.
    ///
    /// Pure and static so it can be tested without a filesystem.
    /// </summary>
    public static string CleanName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "Anonymous";

        var clean = new StringBuilder(MaxNameLength);

        foreach (char c in raw.Trim())
        {
            if (char.IsControl(c)) continue;
            clean.Append(c);
            if (clean.Length >= MaxNameLength) break;
        }

        string result = clean.ToString().Trim();
        return result.Length == 0 ? "Anonymous" : result;
    }

    /// <summary>
    /// Whether an id could have come from any identity provider we support.
    ///
    /// Deliberately wider than the shape this install generates: a verified id
    /// from a platform is somebody else's format -- decimal rather than hex, and
    /// a different length -- and rejecting it here would mean rewriting this the
    /// day verification arrives.
    /// </summary>
    public static bool IsWellFormedId(string id)
    {
        if (id is null || id.Length < MinIdLength || id.Length > MaxIdLength) return false;

        foreach (char c in id)
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_') return false;

        return true;
    }

    private static void Load()
    {
        if (_id is not null) return;

        string stored = UserPrefs.Read(Section, "id", "").AsString();
        _id = IsWellFormedId(stored) ? stored : Guid.NewGuid().ToString("N");
        _name = CleanName(UserPrefs.Read(Section, "name", "").AsString());

        if (stored == _id) return;

        // Written back immediately when it was minted, so an install that closes
        // before its first run still keeps the same identity next time.
        Save();

        // Then adopt whatever actually landed. Two instances starting together
        // against a missing file each mint a candidate, and without this they
        // would keep their own and diverge permanently. Re-reading converges
        // them on whichever write won.
        //
        // A window remains: an instance that re-reads before the other writes
        // keeps its own id. Closing it properly needs an exclusive create, which
        // Godot's file API does not offer. The consequence is bounded and worth
        // stating plainly -- an install can split into two anonymous identities,
        // only on a simultaneous first launch, and an anonymous identity is
        // already explicitly not evidence of who anybody is.
        string landed = UserPrefs.Read(Section, "id", "").AsString();
        if (IsWellFormedId(landed)) _id = landed;
    }

    private static void Save() => UserPrefs.Write(Section, ("id", _id), ("name", _name));
}
