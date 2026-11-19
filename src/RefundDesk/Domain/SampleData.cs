namespace RefundDesk.Domain;

/// <summary>The fictional store's data. Deterministic: no model is involved here.</summary>
public static class SampleData
{
    public static readonly CustomerRequest DefaultRequest = new(
        CustomerId: "C-881",
        CustomerName: "Jordan Rivera",
        Text: """
              Hi - I bought the Summit 2 tent (order #1042) and one of the poles snapped the
              first night I used it. I'd like my money back. Also, I ordered extra titanium
              stakes separately (#1057). I won't need them now, so please make sure I'm not
              charged for those. Thanks, Jordan
              """);

    public static List<Order> Orders() =>
    [
        new("1042", "C-881", "Summit 2 Tent", 34900, "Delivered",
            new DateOnly(2026, 10, 24), new DateOnly(2026, 10, 28), "Visa ****4417"),
        new("1057", "C-881", "Titanium Stakes (8-pack)", 3900, "Backordered",
            new DateOnly(2026, 11, 2), null, "Visa ****4417"),
        new("0991", "C-112", "Trail Stove", 8900, "Delivered",
            new DateOnly(2026, 9, 30), new DateOnly(2026, 10, 3), "Mastercard ****0021"),
    ];

    /// <summary>Policy knowledge base the research workers read from.</summary>
    public static readonly IReadOnlyDictionary<string, KnowledgeSource> Knowledge =
        new Dictionary<string, KnowledgeSource>
        {
            ["returns"] = new("returns", "Returns policy: what can be returned and when",
                "Returns. Unused items in original packaging may be returned within 30 days of delivery for a full refund. " +
                "Items that have been used are not eligible for return under this policy; see the warranty policy for defects."),
            ["warranty"] = new("warranty", "Warranty policy: defects, replacements and warranty refunds",
                "Warranty. Manufacturing defects are covered for 12 months from delivery. " +
                "Structural failure during normal use, including a broken tent pole, is treated as a manufacturing defect. " +
                "For a covered defect we offer a replacement part; if the replacement part is out of stock, we refund the full purchase price to the original payment method. " +
                "Refunds over $500 require a supervisor."),
            ["backorders"] = new("backorders", "Backorder policy: charges and cancellation of unshipped items",
                "Backorders. A backordered item is charged when the order is placed. " +
                "The customer may cancel a backordered item at any time before it ships, and the full charge is refunded to the original payment method within 3-5 business days."),
            ["inventory"] = new("inventory", "Inventory status: stock levels and restock dates",
                "Inventory status (updated daily). Summit 2 replacement pole set: out of stock, expected restock January 15. " +
                "Titanium stake 8-pack: backordered, expected ship date December 2."),
            ["shipping"] = new("shipping", "Shipping policy: delivery times and costs",
                "Shipping. Standard shipping takes 3-7 business days. Expedited shipping takes 2 business days and costs $19. " +
                "Orders over $75 ship free."),
        };
}

public record KnowledgeSource(string Id, string Title, string Text);
