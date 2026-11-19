namespace RefundDesk.Domain;

/// <summary>
/// The prefix every tool result starts with ("OK: ...", "TIMEOUT: ..."). The model reads
/// these (the executor prompt refers to them), and the console and flow view color by them.
/// </summary>
public static class ToolOutcome
{
    public const string Ok = "OK";
    public const string Duplicate = "DUPLICATE";
    public const string Timeout = "TIMEOUT";
    public const string Denied = "DENIED";
    public const string Error = "ERROR";

    // get_payment_status answers
    public const string Completed = "completed";
    public const string NotFound = "not_found";

    /// <summary>The outcome prefix of a tool result.</summary>
    public static string Of(string result) => result.Split(':')[0];

    public static bool IsFailure(string result) => Of(result) is Timeout or Denied or Error;
}
