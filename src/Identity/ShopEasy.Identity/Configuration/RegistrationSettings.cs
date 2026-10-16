namespace ShopEasy.Identity.Configuration;

// Settings under "Registration": whether visitors may create their own account on the sign-in
// pages. Such an account is always a customer; administrators come from AdminBootstrap.
public class RegistrationSettings
{
    public const string SectionName = "Registration";

    // Turn off to close sign-up (the page answers 404 and the sign-in page stops linking to it)
    public bool Enabled { get; set; } = true;
}
