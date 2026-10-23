namespace ShopEasy.Identity.Configuration;

// Settings under "IdentityServer": the issuer, the address that every token names in its `iss` claim.
//
// Identity is reached by two names: the services and the gateway call it by its internal name
// (http://identity:8080), browsers by its public one (http://localhost:5001). Left alone, Duende
// takes the issuer from the address of each request, so the gateway's tokens say "identity:8080"
// and a browser asking to end the session says "localhost:5001". The sign-out request carries the
// ID token as proof of who is signing out; checked against the other name it fails, Identity does not
// know where to send the customer afterwards, and they are left on Identity's page instead of the
// shop. Duende also counts the two names as two issuers (a licensing error in the log).
//
// With one issuer set here, tokens are the same whichever name asked for them. The APIs and the
// gateway learn the issuer from Identity's discovery document, so they need no setting of their own.
public class IssuerSettings
{
    public const string SectionName = "IdentityServer";

    // Identity's public address, as the gateway's IdentityServer__PublicUrl names it. Empty: the
    // issuer follows each request's address (the behaviour described above).
    public string IssuerUri { get; set; } = "";

    public static IssuerSettings From(IConfiguration configuration) =>
        configuration.GetSection(SectionName).Get<IssuerSettings>() ?? new IssuerSettings();

    public void Validate()
    {
        if (IssuerUri.Length == 0)
            return;

        // an issuer is an origin: a scheme, a host and perhaps a port, nothing more
        var valid = Uri.TryCreate(IssuerUri, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            && uri.UserInfo.Length == 0
            && uri.AbsolutePath == "/"
            && uri.Query.Length == 0
            && uri.Fragment.Length == 0
            && !IssuerUri.EndsWith('/');
        if (!valid)
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(IssuerUri)} must be an http:// or https:// address without a path, such as " +
                $"'http://localhost:5001', not '{IssuerUri}'.");
    }
}
