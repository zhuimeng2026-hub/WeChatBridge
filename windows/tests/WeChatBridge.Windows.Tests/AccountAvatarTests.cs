using WeChatBridge.Windows.Core.Activation;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// The two pure decisions behind the account avatar, pinned.
/// <para>
/// This file exists because the rest of that feature cannot be checked from here:
/// the picture itself is a 32-pixel circle in a WPF pane, and the pane lives in an
/// assembly this project (<c>net10.0</c>) cannot reference. What can be checked is
/// the part that decides <i>which</i> picture and <i>which</i> letter — and those
/// two are exactly the branches a user would otherwise only discover on screen.
/// </para>
/// <para>
/// The rejections are the interesting half. The service returns an empty
/// <c>avatar_url</c> for every account that has no cached picture, so "no picture"
/// is the ordinary state and not an error path; and a fallback that rendered as a
/// blank square or a torn-image glyph would be worse than no avatar at all. Every
/// case below that yields null is a state the pane has to survive quietly.
/// </para>
/// </summary>
public sealed class AccountAvatarTests
{
    private const string Base = "https://ocbot.aixifs.com/";

    // ---------------------------------------------------------------- initials

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ANameWithNoLettersInItStillGetsAPlaceholderCharacter(string? name)
    {
        // AccountView.DisplayName already falls back to the account Id, so this is
        // only reachable for an account with no Id either — rare, but a blank
        // circle there reads as a broken layout rather than as a missing name.
        Assert.Equal(AccountAvatar.Unknown, AccountAvatar.Initials(name));
    }

    [Theory]
    [InlineData("alice", "A")]
    [InlineData("Alice", "A")]
    [InlineData("ALICE", "A")]
    [InlineData("演示买家", "演")]
    [InlineData("张伟", "张")]
    public void TheFirstCharacterOfTheNameIsWhatShows(string name, string expected)
    {
        Assert.Equal(expected, AccountAvatar.Initials(name));
    }

    [Fact]
    public void ALowercaseLatinNameIsUpperCasedRatherThanShownAsTyped()
    {
        Assert.Equal("A", AccountAvatar.Initials("alice"));

        // And a Chinese character survives upper-casing unchanged, which is the
        // other half of the same rule and not worth a separate file.
        Assert.Equal("演", AccountAvatar.Initials("演播"));
    }

    [Fact]
    public void LeadingWhitespaceIsNotTheFirstCharacter()
    {
        Assert.Equal("A", AccountAvatar.Initials("   alice"));
    }

    [Fact]
    public void ANicknameOpeningWithAnEmojiKeepsTheEmojiWhole()
    {
        // Taking [0] would hand WPF a lone high surrogate and render a replacement
        // box — the exact artefact the letter block is meant to prevent.
        var initial = AccountAvatar.Initials("😀同学");

        Assert.Equal("😀", initial);
        Assert.False(char.IsHighSurrogate(initial[^1]), "the grapheme was cut in half");
    }

    // -------------------------------------------------------------------- uri

    [Fact]
    public void AnAbsoluteAddressIsUsedExactlyAsTheServerSentIt()
    {
        var url = "https://ocbot.aixifs.com/avatars/acc_9f2?v=3c1d0a";

        var resolved = AccountAvatar.ResolveUri(url, Base);

        Assert.NotNull(resolved);
        Assert.Equal(url, resolved.ToString());
    }

    [Fact]
    public void AnAbsoluteAddressIsNotRewrittenEvenWhenTheBaseSaysOtherwise()
    {
        // A dev build points itself at localhost through the environment variable,
        // but the address in the payload is the one the server that answered chose.
        var resolved = AccountAvatar.ResolveUri("https://other.example/avatars/a1", "http://127.0.0.1:8080/");

        Assert.NotNull(resolved);
        Assert.Equal("https://other.example/avatars/a1", resolved.ToString());
    }

    [Fact]
    public void ARootedPathIsJoinedOntoTheConfiguredBase()
    {
        // What a deployment behind a different public origin would send.
        var resolved = AccountAvatar.ResolveUri("/avatars/acc_9f2", Base);

        Assert.NotNull(resolved);
        Assert.Equal("https://ocbot.aixifs.com/avatars/acc_9f2", resolved.ToString());
    }

    [Fact]
    public void ARootedPathKeepsItsVersionQuery()
    {
        var resolved = AccountAvatar.ResolveUri("/avatars/acc_9f2?v=3c1d0a", Base);

        Assert.NotNull(resolved);
        Assert.Equal("https://ocbot.aixifs.com/avatars/acc_9f2?v=3c1d0a", resolved.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoAddressAtAllResolvesToNothing(string? url)
    {
        // The shape accountDTO returns whenever there is no cached picture: an empty
        // string, in the ordinary case of a WeChat account that never set one.
        Assert.Null(AccountAvatar.ResolveUri(url, Base));
    }

    [Theory]
    [InlineData("http://")]              // scheme and introducer, no host
    [InlineData("avatars/acc_9f2")]      // relative without the root the server emits
    [InlineData("//evil.example/a.png")] // would resolve against a foreign host
    [InlineData("not a url")]
    public void AnUnusableAddressResolvesToNothingRatherThanToAGuess(string url)
    {
        Assert.Null(AccountAvatar.ResolveUri(url, Base));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a base")]
    public void ARootedPathWithoutAUsableBaseResolvesToNothing(string? baseAddress)
    {
        Assert.Null(AccountAvatar.ResolveUri("/avatars/acc_9f2", baseAddress));
    }

    [Fact]
    public void TheShippedBaseAddressIsTheOneTheDefaultRefersTo()
    {
        // Pins the constant the pane falls back to, so a change of host cannot move
        // relative avatar paths without this test noticing.
        var resolved = AccountAvatar.ResolveUri("/avatars/a1", ActivationEndpoint.DefaultBaseAddress);

        Assert.NotNull(resolved);
        Assert.StartsWith(ActivationEndpoint.DefaultBaseAddress, resolved.ToString(), StringComparison.Ordinal);
    }
}
