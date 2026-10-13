using ShopEasy.OrderService.Controllers;

namespace ShopEasy.OrderService.Services;

// Limits enforced on every PlaceOrder request. Documented in the README ("Order API").
public static class OrderLimits
{
    public const int MaxItemsPerOrder = 50;
    public const int MaxQuantityPerProduct = 100;
    public const int MaxNotesLength = 500;
}

// One validated, de-duplicated line: which product and how many. Price and name are
// deliberately absent — they are resolved server-side from the Product service.
public record RequestedLine(Guid ProductId, int Quantity);

public record OrderRequestValidation(
    IReadOnlyList<string> Errors,
    IReadOnlyList<RequestedLine> Lines)
{
    public bool IsValid => Errors.Count == 0;
}

// Pure request validation (no I/O) so it can be unit tested without a database or HTTP.
public static class OrderRequestValidator
{
    public static OrderRequestValidation Validate(PlaceOrderRequest? request)
    {
        var errors = new List<string>();

        if (request?.Items is null || request.Items.Count == 0)
        {
            errors.Add("At least one item is required.");
            return new OrderRequestValidation(errors, []);
        }

        if (request.Items.Count > OrderLimits.MaxItemsPerOrder)
            errors.Add($"An order can contain at most {OrderLimits.MaxItemsPerOrder} items.");

        if (request.Notes is { Length: > OrderLimits.MaxNotesLength })
            errors.Add($"Notes must not exceed {OrderLimits.MaxNotesLength} characters.");

        var valid = new List<RequestedLine>();
        for (var i = 0; i < request.Items.Count; i++)
        {
            var position = i + 1;
            var item = request.Items[i];

            if (item is null)
            {
                errors.Add($"Item {position}: must not be null.");
                continue;
            }

            var itemOk = true;

            if (item.ProductId == Guid.Empty)
            {
                errors.Add($"Item {position}: productId is required.");
                itemOk = false;
            }

            if (item.Quantity < 1)
            {
                errors.Add($"Item {position}: quantity must be at least 1.");
                itemOk = false;
            }
            else if (item.Quantity > OrderLimits.MaxQuantityPerProduct)
            {
                errors.Add(
                    $"Item {position}: quantity must not exceed {OrderLimits.MaxQuantityPerProduct}.");
                itemOk = false;
            }

            if (itemOk)
                valid.Add(new RequestedLine(item.ProductId, item.Quantity));
        }

        // Merge repeated products into one line so stock is checked and reserved once per product.
        var merged = new List<RequestedLine>();
        foreach (var group in valid.GroupBy(l => l.ProductId))
        {
            var total = group.Sum(l => l.Quantity);
            if (total > OrderLimits.MaxQuantityPerProduct)
            {
                errors.Add(
                    $"Product {group.Key}: total quantity across line items must not exceed " +
                    $"{OrderLimits.MaxQuantityPerProduct}.");
                continue;
            }
            merged.Add(new RequestedLine(group.Key, total));
        }

        return new OrderRequestValidation(errors, merged);
    }
}

// A line with the price and name taken from the Product service.
public record ResolvedLine(Guid ProductId, string ProductName, int Quantity, decimal UnitPrice);

public record LineResolution(
    IReadOnlyList<ResolvedLine> Lines,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

// Combines the requested lines with the catalog answer. Pure function — unit tested.
public static class OrderLineResolver
{
    public static LineResolution Resolve(
        IReadOnlyList<RequestedLine> requested,
        IReadOnlyDictionary<Guid, CatalogProduct?> catalog)
    {
        var errors = new List<string>();
        var lines = new List<ResolvedLine>();

        foreach (var line in requested)
        {
            catalog.TryGetValue(line.ProductId, out var product);

            if (product is null)
                errors.Add($"Product {line.ProductId} does not exist.");
            else if (!product.IsActive)
                errors.Add($"Product '{product.Name}' is no longer available.");
            else if (product.Price <= 0)
                errors.Add($"Product '{product.Name}' has no valid price and cannot be ordered.");
            else
                lines.Add(new ResolvedLine(
                    line.ProductId, product.Name, line.Quantity, product.Price));
        }

        return new LineResolution(lines, errors);
    }
}
