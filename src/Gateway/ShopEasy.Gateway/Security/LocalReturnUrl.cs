namespace ShopEasy.Gateway.Security;

// /account/login?returnUrl=... sends the browser on after sign-in. Anything but a path on this site
// would let a link to the shop's own sign-in page end on a page of someone else (an open redirect),
// so a web address, a protocol-relative "//host" or a backslash form is replaced by the home page.
public static class LocalReturnUrl
{
    public const string Home = "/";

    public static string OrHome(string? candidate) => IsLocal(candidate) ? candidate! : Home;

    public static bool IsLocal(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate) || candidate[0] != '/')
            return false;

        // "//host" and "/\host" are read by browsers as another site
        if (candidate.Length > 1 && (candidate[1] == '/' || candidate[1] == '\\'))
            return false;

        // tabs, line breaks and other control characters are dropped by browsers inside an address,
        // which can turn "/\t/host" into "//host"
        return !candidate.Any(char.IsControl);
    }
}
