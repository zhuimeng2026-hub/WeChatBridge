using System.Globalization;

namespace WeChatBridge.Windows.Core.Activation;

/// <summary>
/// The two decisions the buyer UI makes about an account picture, with no WPF in
/// either of them.
/// <para>
/// They live here rather than in the pane because they are the only part of the
/// avatar that can be pinned by a test on this machine: the rest is a 32-pixel
/// circle, which a Linux CI box cannot look at. Core is also the assembly
/// <c>EditionIsolationTests</c> certifies as naming no UI type, so both functions
/// stay free of <c>System.Windows</c>.
/// </para>
/// <para>
/// The pair exists together on purpose. A nickname always yields an initial, and an
/// address either yields a picture or yields nothing — there is no third case where
/// the pane has to decide what an unreadable string means. That is what keeps a
/// failed download from turning into a broken-image glyph on screen.
/// </para>
/// </summary>
public static class AccountAvatar
{
    /// <summary>
    /// The one character shown when there is no picture to show.
    /// <para>
    /// A question mark rather than nothing: an empty <c>TextBlock</c> over the brand
    /// fill is a blank coloured circle, which reads as a rendering bug rather than as
    /// a deliberate placeholder.
    /// </para>
    /// </summary>
    public const string Unknown = "?";

    /// <summary>
    /// The character to put in the letter block: the nickname's first character,
    /// upper-cased so an all-lowercase name does not look half-finished.
    /// <para>
    /// One grapheme, not one <see cref="char"/> — a nickname whose first character
    /// is an emoji or a combining mark would otherwise hand WPF half a surrogate
    /// pair and render a replacement box, which is the very artefact the letter
    /// block exists to avoid.
    /// </para>
    /// </summary>
    public static string Initials(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return Unknown;

        // TrimStart, not Trim: a nickname that is mostly leading spaces still has a
        // first character worth showing.
        var first = StringInfo.GetNextTextElement(displayName.TrimStart());
        return first.ToUpperInvariant();
    }

    /// <summary>
    /// Where an <see cref="AccountView.AvatarUrl"/> points, or null when it points
    /// nowhere usable.
    /// <para>
    /// The service hands out an absolute address built from its own public origin
    /// (<c>AvatarURL</c> in <c>server/internal/auth/service.go</c>), and returns an
    /// empty string instead whenever the picture is missing, not cached, or the
    /// avatar store is switched off. A relative <c>/avatars/…</c> is therefore a
    /// deployment talking to a client that was told a different base address, and is
    /// joined onto that base rather than treated as a file path.
    /// </para>
    /// <para>
    /// Anything that is neither absolute nor a rooted path is null. That is stricter
    /// than <see cref="Uri"/> alone, which would happily resolve "not a url" against
    /// the base and hand back a request for a path that does not exist. A leading
    /// <c>//</c> is refused even earlier, before anything tries to read a scheme out
    /// of it: it names a host, that host is not the licence service, and .NET will
    /// happily read such a string as a UNC file path and call it absolute.
    /// </para>
    /// </summary>
    public static Uri? ResolveUri(string? url, string? baseAddress)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var candidate = url.Trim();

        if (candidate.StartsWith("//", StringComparison.Ordinal))
            return null;

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var absolute))
            return absolute;

        if (candidate[0] != '/')
            return null;

        return Uri.TryCreate(baseAddress, UriKind.Absolute, out var root) &&
               Uri.TryCreate(root, candidate, out var joined)
            ? joined
            : null;
    }
}
