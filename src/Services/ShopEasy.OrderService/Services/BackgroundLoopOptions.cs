namespace ShopEasy.OrderService.Services;

// Settings under "BackgroundLoops" (appsettings.json, or BackgroundLoops__PassTimeoutSeconds).
// One pass of a polling loop (saga orchestrator, saga timeout service, outbox delivery, dead-letter
// monitor) takes well under a second. A pass still running after PassTimeoutSeconds is cancelled and
// counts as a failed pass, so a call that hangs cannot freeze the loop for good.
public class BackgroundLoopOptions
{
    public const string SectionName = "BackgroundLoops";

    public int PassTimeoutSeconds { get; set; } = 120;
}
